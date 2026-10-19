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
    /// <param name="Id">The id the store keeps it under.</param>
    public sealed record MeterIdentity(X509Certificate2                       Certificate,
                                       IReadOnlyCollection<X509Certificate2>  Intermediates,
                                       String                                 Role,
                                       String                                 Label,
                                       String                                 Id) : IDisposable
    {

        /// <summary>
        /// Who it is, as a page says it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",       Id),
                   new JProperty("label",    Label),
                   new JProperty("role",     Role),
                   new JProperty("subject",  Certificate.Subject)
               );

        public void Dispose()
        {

            Certificate.Dispose();

            foreach (var intermediate in Intermediates)
                intermediate.Dispose();

        }

    }


    /// <summary>
    /// Whom the station is to a meter for one kind of request: an identity,
    /// or why there is none to show.
    /// </summary>
    /// <param name="Identity">The identity, with its private key; whoever asks disposes of it.</param>
    /// <param name="Why">Why there is none.</param>
    public sealed record IdentityChoice(MeterIdentity?  Identity,
                                        String?         Why);


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

        private readonly Func<ModbusOperation, IdentityChoice>                               identityFor;
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
        /// <param name="IdentityFor">Who this station is to it for a read or a write, asked again at every connection: an identity imported or configured while the station runs is used from the next one on.</param>
        /// <param name="Judge">Whether to go ahead with the certificate it showed.</param>
        /// <param name="Log">Where what happens is written.</param>
        /// <param name="TimeProvider">The clock.</param>
        /// <param name="ReadInterval">How often it is read while connected.</param>
        /// <param name="RetryInterval">How long after a failure it is tried again.</param>
        public EVSEMeter(Byte                                                                 EVSE,
                         MeterEndpoint                                                        Endpoint,
                         String                                                               About,
                         Func<ModbusOperation, IdentityChoice>                                IdentityFor,
                         Func<X509Certificate2?, X509Chain?, SslPolicyErrors, MeterJudgement> Judge,
                         EventLog                                                             Log,
                         TimeProvider                                                         TimeProvider,
                         TimeSpan?                                                            ReadInterval   = null,
                         TimeSpan?                                                            RetryInterval  = null)
        {

            this.EVSE           = EVSE;
            this.Endpoint       = Endpoint;
            this.About          = About;
            this.identityFor    = IdentityFor;
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


        #region Ask(Request, CancellationToken)

        /// <summary>
        /// Ask the meter one request, on a connection of its own, showing it the
        /// identity this station is to it for what the request does: the one
        /// for reading for a read, the one for writing for a write - and say
        /// what came back, down to the bytes.
        /// </summary>
        /// <remarks>
        /// A connection of its own, rather than the reading loop's, because a
        /// TLS connection shows one identity for as long as it lasts: a write
        /// on the connection the meter is read on would be a write by the
        /// identity that reads. The meter is judged as it is for reading.
        ///
        /// What the meter does not like is the answer and not a failure: an
        /// exception response, a refused handshake and a meter that cannot be
        /// reached are each said as what happened.
        /// </remarks>
        public async Task<JObject> Ask(ModbusRequest      Request,
                                       CancellationToken  CancellationToken)
        {

            var at       = timeProvider.GetUtcNow();
            var choice   = identityFor(Request.Operation);
            var request  = Request.ToPDU();

            var json     = new JObject(
                               new JProperty("at",         at.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                               new JProperty("endpoint",   Endpoint.ToJSON()),
                               new JProperty("function",   Request.Function),
                               new JProperty("name",       Request.Name),
                               new JProperty("operation",  Request.Operation.ToString().ToLowerInvariant()),
                               new JProperty("address",    Request.Address),
                               new JProperty("count",      Request.Count),
                               new JProperty("request",    Convert.ToHexString(request))
                           );

            using var identity = choice.Identity;

            if (identity is null)
            {
                json["outcome"] = "noIdentity";
                json["error"]   = choice.Why ?? "There is no TLS identity to show the meter.";
                return json;
            }

            json["identity"] = identity.ToJSON();

            MeterJudgement? judgement = null;
            var             started   = System.Diagnostics.Stopwatch.StartNew();

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

                json["tls"]       = new JObject(
                                        new JProperty("protocol",     connection.Protocol.ToString()),
                                        new JProperty("cipherSuite",  connection.CipherSuite),
                                        new JProperty("meter",        connection.MeterCertificate is X509Certificate2 shown
                                                                          ? V2GLink.CertificateJSON(shown)
                                                                          : null),
                                        new JProperty("judged",       judgement?.Outcome)
                                    );

                var response      = await connection.Exchange(Endpoint.Unit, request, DefaultTimeout, CancellationToken);

                json["response"]  = Convert.ToHexString(response);

                if (ModbusPDU.TryGetException(response) is ModbusExceptionCode code)
                {
                    json["outcome"]    = "exception";
                    json["exception"]  = new JObject(
                                             new JProperty("code",  (Byte) code),
                                             new JProperty("name",  code.ToString())
                                         );
                }

                else if (Request.Operation == ModbusOperation.Read)
                {

                    var registers  = ModbusPDU.DecodeReadResponse(response);

                    json["outcome"]    = "answered";
                    json["registers"]  = new JArray(registers.Select((value, index) => new JObject(
                                             new JProperty("address",  Request.Address + index),
                                             new JProperty("value",    value)
                                         )));

                }

                else
                    json["outcome"] = "answered";

            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (judgement is { Accepted: false })
            {
                json["outcome"] = "untrusted";
                json["error"]   = $"The meter at {Endpoint} is not believed: {judgement.Why ?? judgement.Outcome}";
            }
            catch (AuthenticationException e)
            {
                json["outcome"] = "refused";
                json["error"]   = $"The TLS handshake with the meter at {Endpoint} failed: {e.GetBaseException().Message}";
            }
            catch (Exception e)
            {
                json["outcome"] = "failed";
                json["error"]   = $"The meter at {Endpoint} could not be asked: {e.GetBaseException().Message}";
            }

            json["ms"] = Math.Round(started.Elapsed.TotalMilliseconds, 1);

            var said = $"{About}: {Request} asked the meter at {Endpoint} as '{identity.Label}' ({identity.Role}): {json.Value<String>("outcome")}" +
                       (json["exception"] is JObject exception ? $", {exception.Value<String>("name")}" : "") + ".";

            // A write is a change to the meter, and said as one.
            if (Request.Operation == ModbusOperation.Write)
                log.Notice(said, "meter", "modbus");
            else
                log.Info  (said, "meter", "modbus");

            return json;

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

                var choice = identityFor(ModbusOperation.Read);

                using var identity = choice.Identity;

                if (identity is null)
                {
                    Became("noIdentity", choice.Why ?? "There is no TLS identity to show the meter.");
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
                                      new JProperty("identity",     identity.ToJSON()),
                                      new JProperty("judged",       judgement?.Outcome)
                                  );

                    ReadRegisters read = (first, count) => connection.ReadHoldingRegisters(Endpoint.Unit, first, count, DefaultTimeout, CancellationToken);

                    // Where its registers begin and where its meter model is,
                    // once for the connection: its list of models, walked.
                    SunSpecDevice device;

                    try
                    {
                        device = await SunSpecDevice.Discover(read);
                    }
                    catch (NotASunSpecMeter e)
                    {
                        Became("notSunSpec", $"The meter at {Endpoint} answered, but not as a SunSpec meter this station can read: {e.Message}", details);
                        await Wait(RetryInterval, CancellationToken);
                        continue;
                    }

                    details["sunSpec"] = device.ToJSON();

                    while (!CancellationToken.IsCancellationRequested)
                    {

                        var block = await device.ReadMeter(read);

                        if (!MeterReading.TryDecode(device, block, timeProvider.GetUtcNow(), out var reading, out var notAMeter))
                        {
                            Became("notSunSpec", $"The meter at {Endpoint} answered, but not as a SunSpec meter this station can read: {notAMeter}", details);
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
                catch (NotASunSpecMeter e)
                {
                    Became("notSunSpec", $"The meter at {Endpoint} answered, but not as a SunSpec meter this station can read: {e.Message}");
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
