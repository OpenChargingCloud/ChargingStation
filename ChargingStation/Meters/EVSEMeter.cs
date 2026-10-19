/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of ChargingStation <https://github.com/OpenChargingCloud/ChargingStation>
 *
 * Licensed under the Affero GPL license, Version 3.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.gnu.org/licenses/agpl.html
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.ISO15118;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Who this station is to a meter: a TLS identity of its store whose
    /// certificate carries a SunSpec role, with its private key and the CAs
    /// between it and its root.
    /// </summary>
    /// <param name="Certificate">The certificate, with its private key.</param>
    /// <param name="Intermediates">The CAs it is sent along with.</param>
    /// <param name="Role">The SunSpec role in it.</param>
    /// <param name="Label">What the store calls it.</param>
    public sealed record MeterIdentity(X509Certificate2                       Certificate,
                                       IReadOnlyCollection<X509Certificate2>  Intermediates,
                                       String                                 Role,
                                       String                                 Label) : IDisposable
    {

        public void Dispose()
        {

            Certificate.Dispose();

            foreach (var intermediate in Intermediates)
                intermediate.Dispose();

        }

    }


    /// <summary>
    /// What the station made of the certificate a meter showed: whether to go
    /// ahead, in one word, and why not in a sentence.
    /// </summary>
    public sealed record MeterJudgement(Boolean  Accepted,
                                        String   Outcome,
                                        String?  Why);


    /// <summary>
    /// The energy meter of one EVSE, read over Modbus/TLS once a second for as
    /// long as the station runs - and what it said last, or why it said
    /// nothing.
    /// </summary>
    /// <remarks>
    /// A meter that cannot be reached, refuses this station or answers with
    /// something that is no SunSpec meter is tried again a few seconds later,
    /// for as long as the EVSE is configured to have it read: a meter is
    /// switched on after the station often enough, and a certificate for it is
    /// imported while the station runs. What went wrong is written into the log
    /// once, when it starts going wrong and when it changes, and not every few
    /// seconds.
    /// </remarks>
    public sealed class EVSEMeter : IAsyncDisposable
    {

        #region (private record) MeterState

        /// <summary>
        /// How a meter stands, as one value replaced as a whole: written by the
        /// reading loop and read by the station's look at its EVSEs.
        /// </summary>
        private sealed record MeterState(String           State,
                                         DateTimeOffset   Since,
                                         String?          Error,
                                         JObject?         Connection,
                                         MeterReading?    Reading);

        #endregion

        #region Data

        /// <summary>
        /// How long connecting, the handshake and one request may take.
        /// </summary>
        public static readonly TimeSpan  DefaultTimeout        = TimeSpan.FromSeconds(5);

        /// <summary>
        /// How often a meter that is connected is read.
        /// </summary>
        public static readonly TimeSpan  DefaultReadInterval   = TimeSpan.FromSeconds(1);

        /// <summary>
        /// How long after a failure a meter is tried again.
        /// </summary>
        public static readonly TimeSpan  DefaultRetryInterval  = TimeSpan.FromSeconds(5);

        private readonly Func<MeterIdentity?>                                                identityOf;
        private readonly Func<X509Certificate2?, X509Chain?, SslPolicyErrors, MeterJudgement> judge;
        private readonly EventLog                                                            log;
        private readonly TimeProvider                                                        timeProvider;
        private readonly CancellationTokenSource                                             stopping     = new ();
        private          TaskCompletionSource                                                wake         = new (TaskCreationOptions.RunContinuationsAsynchronously);
        private          Task?                                                               loop;
        private volatile MeterState                                                          state;
        private          String?                                                             lastSaid;

        #endregion

        #region Properties

        /// <summary>
        /// The EVSE whose meter this is.
        /// </summary>
        public Byte           EVSE              { get; }

        /// <summary>
        /// Where it is read.
        /// </summary>
        public MeterEndpoint  Endpoint          { get; }

        /// <summary>
        /// The EVSE as the log names it: "EVSE A".
        /// </summary>
        public String         About             { get; }

        /// <summary>
        /// How often it is read while it is connected.
        /// </summary>
        public TimeSpan       ReadInterval      { get; }

        /// <summary>
        /// How long after a failure it is tried again.
        /// </summary>
        public TimeSpan       RetryInterval     { get; }

        /// <summary>
        /// What it said when it was last read, as long as that was recently
        /// enough to still be true - three readings' worth; null otherwise.
        /// </summary>
        public MeterReading?  Reading
        {
            get
            {

                var current = state;

                return current.Reading is MeterReading reading &&
                       current.State == "reading"              &&
                       timeProvider.GetUtcNow() - reading.At <= 3 * ReadInterval + DefaultTimeout
                           ? reading
                           : null;

            }
        }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The meter of one EVSE; read once <see cref="Start"/> is called.
        /// </summary>
        /// <param name="EVSE">The EVSE whose meter it is.</param>
        /// <param name="Endpoint">Where it is read.</param>
        /// <param name="About">The EVSE as the log names it.</param>
        /// <param name="IdentityOf">Who this station is to it, asked again at every connection: an identity imported while the station runs is used from the next one on. Null while the store has none.</param>
        /// <param name="Judge">Whether to go ahead with the certificate it showed.</param>
        /// <param name="Log">Where what happens is written.</param>
        /// <param name="TimeProvider">The clock.</param>
        /// <param name="ReadInterval">How often it is read while connected.</param>
        /// <param name="RetryInterval">How long after a failure it is tried again.</param>
        public EVSEMeter(Byte                                                                 EVSE,
                         MeterEndpoint                                                        Endpoint,
                         String                                                               About,
                         Func<MeterIdentity?>                                                 IdentityOf,
                         Func<X509Certificate2?, X509Chain?, SslPolicyErrors, MeterJudgement> Judge,
                         EventLog                                                             Log,
                         TimeProvider                                                         TimeProvider,
                         TimeSpan?                                                            ReadInterval   = null,
                         TimeSpan?                                                            RetryInterval  = null)
        {

            this.EVSE           = EVSE;
            this.Endpoint       = Endpoint;
            this.About          = About;
            this.identityOf     = IdentityOf;
            this.judge          = Judge;
            this.log            = Log;
            this.timeProvider   = TimeProvider;
            this.ReadInterval   = ReadInterval  ?? DefaultReadInterval;
            this.RetryInterval  = RetryInterval ?? DefaultRetryInterval;
            this.state          = new MeterState("connecting", TimeProvider.GetUtcNow(), null, null, null);

        }

        #endregion


        #region Start()

        /// <summary>
        /// Begin reading it.
        /// </summary>
        public void Start()
        {

            log.Info($"{About}: reading the meter at {Endpoint} over Modbus/TLS.", "meter");

            loop = Task.Run(() => Read(stopping.Token));

        }

        #endregion

        #region TryAgainNow()

        /// <summary>
        /// Try a meter that failed again at once, rather than when its wait is
        /// over: something it failed for may just have changed - the store, say.
        /// </summary>
        public void TryAgainNow()

            => wake.TrySetResult();

        #endregion

        #region StatusJSON()

        /// <summary>
        /// How the meter stands, as the EVSEs page shows it: where it is read,
        /// whether it is, over what, and what it said.
        /// </summary>
        public JObject StatusJSON()
        {

            var current = state;

            return new JObject(
                       new JProperty("endpoint",    Endpoint.ToJSON()),
                       new JProperty("state",       current.State),
                       new JProperty("since",       current.Since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                       new JProperty("error",       current.Error),
                       new JProperty("connection",  current.Connection),
                       new JProperty("reading",     current.Reading?.ToJSON())
                   );

        }

        #endregion


        #region (private) Read(CancellationToken)

        /// <summary>
        /// Connect, read, and connect again whenever that fails - until stopped.
        /// </summary>
        private async Task Read(CancellationToken CancellationToken)
        {

            while (!CancellationToken.IsCancellationRequested)
            {

                using var identity = identityOf();

                if (identity is null)
                {
                    Became("noIdentity",
                           "There is no TLS identity in this station's certificate store whose certificate carries a SunSpec role, so there is nothing to show the meter. " +
                           "Import one, with the role the meter lets read - ReadOnlySunSpec is enough.");
                    await Wait(RetryInterval, CancellationToken);
                    continue;
                }

                MeterJudgement? judgement = null;

                try
                {

                    await using var connection = await ModbusTLSConnection.Open(
                                                           Endpoint.Host,
                                                           Endpoint.Port,
                                                           identity.Certificate,
                                                           identity.Intermediates,
                                                           (sender, certificate, chain, errors) => {
                                                               judgement = judge(certificate as X509Certificate2, chain, errors);
                                                               return judgement.Accepted;
                                                           },
                                                           DefaultTimeout,
                                                           CancellationToken
                                                       );

                    var details = new JObject(
                                      new JProperty("protocol",     connection.Protocol.ToString()),
                                      new JProperty("cipherSuite",  connection.CipherSuite),
                                      new JProperty("meter",        connection.MeterCertificate is X509Certificate2 shown
                                                                        ? V2GLink.CertificateJSON(shown)
                                                                        : null),
                                      new JProperty("identity",     new JObject(
                                                                        new JProperty("label",    identity.Label),
                                                                        new JProperty("role",     identity.Role),
                                                                        new JProperty("subject",  identity.Certificate.Subject)
                                                                    )),
                                      new JProperty("judged",       judgement?.Outcome)
                                  );

                    while (!CancellationToken.IsCancellationRequested)
                    {

                        var registers = await connection.ReadHoldingRegisters(Endpoint.Unit,
                                                                              MeterReading.FirstRegister,
                                                                              MeterReading.RegisterCount,
                                                                              DefaultTimeout,
                                                                              CancellationToken);

                        if (!MeterReading.TryDecode(registers, timeProvider.GetUtcNow(), out var reading, out var notAMeter))
                        {
                            Became("notSunSpec", $"The meter at {Endpoint} answered, but not as a SunSpec meter: {notAMeter}", details);
                            break;
                        }

                        Became("reading", null, details, reading);

                        await Task.Delay(ReadInterval, timeProvider, CancellationToken);

                    }

                }
                catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception) when (judgement is { Accepted: false })
                {
                    Became("untrusted", $"The meter at {Endpoint} is not believed: {judgement.Why ?? judgement.Outcome}");
                }
                catch (AuthenticationException e)
                {
                    Became("refused", $"The TLS handshake with the meter at {Endpoint} failed: {e.GetBaseException().Message} " +
                                      "A meter that does not accept this station's certificate - its CA is not one the meter trusts - ends the handshake like this.");
                }
                catch (ModbusException e)
                {
                    Became("refused", $"The meter at {Endpoint} refused to be read by '{identity.Role}': {e.Code}.");
                }
                catch (Exception e) when (e is SocketException or IOException or OperationCanceledException or TimeoutException)
                {
                    Became("unreachable", $"The meter at {Endpoint} cannot be reached: {e.GetBaseException().Message}");
                }
                catch (Exception e)
                {
                    Became("failed", $"Reading the meter at {Endpoint} failed: {e.GetBaseException().Message}");
                }

                await Wait(RetryInterval, CancellationToken);

            }

        }

        #endregion

        #region (private) Became(State, Error, Connection = null, Reading = null)

        /// <summary>
        /// The meter stands differently now: kept for the page, and said in the
        /// log where it is news.
        /// </summary>
        private void Became(String         State,
                            String?        Error,
                            JObject?       Connection  = null,
                            MeterReading?  Reading     = null)
        {

            var previous = state;
            var since    = previous.State == State && previous.Error == Error
                               ? previous.Since
                               : timeProvider.GetUtcNow();

            state = new MeterState(State, since, Error, Connection, Reading);

            var said = Error ?? State;

            if (said == lastSaid)
                return;

            lastSaid = said;

            if (Error is null)
                log.Notice($"{About}: the meter at {Endpoint} is read" +
                           (Reading is null ? "." : $" - {Reading.Manufacturer} {Reading.Model}, serial number {Reading.SerialNumber}."),
                           "meter");
            else
                log.Warning($"{About}: {Error}", "meter");

        }

        #endregion

        #region (private) Wait(Delay, CancellationToken)

        /// <summary>
        /// Wait before trying again - or less, where somebody says to try now.
        /// </summary>
        private async Task Wait(TimeSpan           Delay,
                                CancellationToken  CancellationToken)
        {

            var woken = wake.Task;

            try
            {
                await Task.WhenAny(Task.Delay(Delay, timeProvider, CancellationToken), woken);
            }
            catch (OperationCanceledException)
            { }

            if (woken.IsCompleted)
                wake = new (TaskCreationOptions.RunContinuationsAsynchronously);

        }

        #endregion


        #region DisposeAsync()

        /// <summary>
        /// Stop reading it, and let go of the connection.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            stopping.Cancel();

            if (loop is not null)
            {
                try
                {
                    await loop;
                }
                catch (OperationCanceledException)
                { }
            }

            stopping.Dispose();

        }

        #endregion

    }

}
