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

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The energy meter of each EVSE that is configured to have one read: a
    /// SunSpec meter over Modbus/TLS, read once a second while the station runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How each is read is the "meters" section's - see
    /// <see cref="MeterConfiguration"/>: where, whom it is believed to be, and
    /// whom this station is to it to read and to write.
    /// </para>
    /// <para>
    /// Whom the station is to a meter is a TLS identity of its certificate store
    /// whose certificate carries a SunSpec role: a meter decides what a peer may
    /// do by that role, so a station reads with one and writes with another.
    /// The one to read with, where none is configured, is the identity with the
    /// least role that may read - ReadOnlySunSpec, where there is one; the one
    /// to write with is never guessed.
    /// </para>
    /// <para>
    /// Whom the station believes a meter to be is judged as every server the
    /// node connects to is judged: issued for the host it is reached at, and
    /// chained to a root this machine trusts or a TLS root of the store for
    /// "meter" - see <see cref="MeterCertificateUsage"/>; and, where its entry
    /// says so, held to a certificate or a root, or learning one the first time
    /// it is believed. What is news about it is written into the metrological
    /// log as well: the meter of an EVSE is what a charge is billed by.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What a TLS root of this station's store may be told it is for, where
        /// it vouches for its meters and nothing else.
        /// </summary>
        public const String  MeterCertificateUsage  = "meter";

        /// <summary>
        /// How long a lesson of trust on first use waits for a reconfiguration
        /// somebody else is in the middle of, before it is left to the next
        /// connection.
        /// </summary>
        private static readonly TimeSpan  MeterLearningWait = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<Byte, EVSEMeter>           meters        = [];
        private readonly ConcurrentDictionary<Byte, MeterConfiguration>  readAs        = [];
        private readonly SemaphoreSlim                          meterChanges  = new (1, 1);
        private          MetersConfiguration                    metersFile    = MetersConfiguration.Empty;
        private          String?                                lastMeters;

        #endregion

        #region Properties

        /// <summary>
        /// The meter of each EVSE that has one read, by the EVSE's id.
        /// </summary>
        public IReadOnlyDictionary<Byte, EVSEMeter> Meters
            => meters;

        /// <summary>
        /// How each EVSE's meter is configured to be read.
        /// </summary>
        public MetersConfiguration MetersFile
            => metersFile;

        /// <summary>
        /// How often a meter that is connected is read; for a test that cannot
        /// wait a second.
        /// </summary>
        internal TimeSpan  MeterReadInterval   { get; set; } = EVSEMeter.DefaultReadInterval;

        /// <summary>
        /// How long after a failure a meter is tried again; for a test that
        /// cannot wait five seconds.
        /// </summary>
        internal TimeSpan  MeterRetryInterval  { get; set; } = EVSEMeter.DefaultRetryInterval;

        #endregion

        #region Events

        /// <summary>
        /// The meters of this station, as <see cref="MetersStatusJSON"/> says
        /// them without its timestamp, whenever what it says changed - which,
        /// for a meter that is read, is every second.
        /// </summary>
        public event Action<JObject>?  MetersChanged;

        #endregion


        #region MeterOf(EVSE)

        /// <summary>
        /// The meter of the given EVSE, where it is read.
        /// </summary>
        public EVSEMeter? MeterOf(Byte EVSE)

            => meters.TryGetValue(EVSE, out var meter)
                   ? meter
                   : null;

        #endregion

        #region IdentityFor(EVSE, Operation)

        /// <summary>
        /// Whom this station is to the meter of an EVSE for a read or a write,
        /// right now - or why it is nobody.
        /// </summary>
        /// <remarks>
        /// Read anew for every connection, with its private key and the CAs its
        /// file holds beside it; whoever asks disposes of it.
        /// </remarks>
        public IdentityChoice IdentityFor(Byte             EVSE,
                                          ModbusOperation  Operation)
        {

            var meter      = metersFile.MeterOf(EVSE);
            var configured = Operation == ModbusOperation.Read
                                 ? meter?.ReadIdentity
                                 : meter?.WriteIdentity;
            var what       = Operation == ModbusOperation.Read ? "read" : "write";

            if (configured is null)
            {

                if (Operation == ModbusOperation.Write)
                    return new IdentityChoice(null, $"No TLS identity is configured to write to the meter of {V2GLabelOf(EVSE)}, so nothing is written to it. The Meters page says which one may.");

                return LeastIdentityThatMayRead() is MeterIdentity least
                           ? new IdentityChoice(least, null)
                           : new IdentityChoice(null, "There is no TLS identity in this station's certificate store whose certificate carries a SunSpec role, so there is nothing to show the meter. " +
                                                      "Import one, with the role the meter lets read - ReadOnlySunSpec is enough.");

            }

            if (Certificates.Get(configured) is not CertificateEntry entry || entry.Kind != CertificateKind.TLSIdentity)
                return new IdentityChoice(null, $"The TLS identity '{configured}' configured to {what} the meter of {V2GLabelOf(EVSE)} is not in the certificate store.");

            if (!entry.IsUsable)
                return new IdentityChoice(null, $"The TLS identity '{entry.Label}' configured to {what} the meter of {V2GLabelOf(EVSE)} is switched off or not valid now.");

            return TryLoadIdentity(entry, out var identity)
                       ? new IdentityChoice(identity, null)
                       : new IdentityChoice(null, $"The TLS identity '{entry.Label}' configured to {what} the meter of {V2GLabelOf(EVSE)} carries no SunSpec role a meter knows, or has no private key.");

        }

        #endregion

        #region LeastIdentityThatMayRead()

        /// <summary>
        /// The TLS identity of the store that carries the least SunSpec role,
        /// or null where none does.
        /// </summary>
        public MeterIdentity? LeastIdentityThatMayRead()
        {

            MeterIdentity? best = null;

            foreach (var entry in Certificates.UsableByKind(CertificateKind.TLSIdentity))
            {

                if (!TryLoadIdentity(entry, out var identity))
                    continue;

                if (best is null || RankOf(identity.Role) < RankOf(best.Role))
                {
                    best?.Dispose();
                    best = identity;
                }
                else
                    identity.Dispose();

            }

            return best;

        }

        /// <summary>
        /// The least that may read first - SunSpec lists them that way: a
        /// station that reads a meter has no business holding the role that may
        /// reset its counters.
        /// </summary>
        private static Int32 RankOf(String Role)

            => Array.IndexOf([.. SunSpecRoles.AllMandatory], Role) is Int32 rank and >= 0
                   ? rank
                   : Int32.MaxValue;

        #endregion

        #region (private) TryLoadIdentity(Entry, out Identity)

        /// <summary>
        /// One TLS identity of the store, where its certificate carries a
        /// SunSpec role a meter knows and its file has the private key.
        /// </summary>
        private Boolean TryLoadIdentity(CertificateEntry                         Entry,
                                        [NotNullWhen(true)] out MeterIdentity?   Identity)
        {

            Identity = null;

            X509Certificate2Collection collection;

            try
            {
                // On Windows into a key store of the user's for as long as it is
                // held: SChannel will not show a certificate whose key is in no
                // store, and a key read from a file is in none.
                collection = X509CertificateLoader.LoadPkcs12CollectionFromFile(
                                 Certificates.FullPath(Entry),
                                 null,
                                 OperatingSystem.IsWindows()
                                     ? X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable
                                     : X509KeyStorageFlags.EphemeralKeySet
                             );
            }
            catch
            {
                return false;
            }

            var leaf = collection.FirstOrDefault(certificate => certificate.HasPrivateKey);
            var role = leaf is null ? null : RoleExtractor.TryExtractRole(leaf);

            if (leaf is null || !SunSpecRoles.IsMandatoryRole(role))
            {
                foreach (var certificate in collection)
                    certificate.Dispose();
                return false;
            }

            Identity = new MeterIdentity(
                           leaf,
                           [.. collection.Where(certificate => !ReferenceEquals(certificate, leaf))],
                           role!,
                           Entry.Label,
                           Entry.Id
                       );

            return true;

        }

        #endregion

        #region MeterIdentitiesJSON()

        /// <summary>
        /// The TLS identities of the store a meter can be shown, with the role
        /// each carries - what the Meters page offers to read and to write with.
        /// </summary>
        public JArray MeterIdentitiesJSON()
        {

            var identities = new JArray();

            foreach (var entry in Certificates.ByKind(CertificateKind.TLSIdentity))
            {

                String? role = null;

                if (Certificates.TryLoad(entry, out var certificate, out _))
                    using (certificate)
                        role = RoleExtractor.TryExtractRole(certificate);

                if (!SunSpecRoles.IsMandatoryRole(role))
                    continue;

                identities.Add(new JObject(
                                   new JProperty("id",        entry.Id),
                                   new JProperty("label",     entry.Label),
                                   new JProperty("role",      role),
                                   new JProperty("subject",   entry.Subject),
                                   new JProperty("notAfter",  entry.NotAfter.ToString("o")),
                                   new JProperty("usable",    entry.IsUsable)
                               ));

            }

            return identities;

        }

        #endregion


        #region (private) JudgeMeter(EVSE, Endpoint, Certificate, Chain, PolicyErrors)

        /// <summary>
        /// Whether to go ahead with the certificate a meter showed, as the node
        /// judges every server it connects to - and with what the meter's entry
        /// holds it to, and learns.
        /// </summary>
        private MeterJudgement JudgeMeter(Byte               EVSE,
                                          MeterEndpoint      Endpoint,
                                          X509Certificate2?  Certificate,
                                          X509Chain?         Chain,
                                          SslPolicyErrors    PolicyErrors)
        {

            var judgement = JudgeServer(MeterCertificateUsage,
                                        Endpoint.Server,
                                        metersFile.MeterOf(EVSE)?.Pins,
                                        Certificate,
                                        Chain,
                                        PolicyErrors,
                                        Evidence:  true,
                                        Learn:     (what, fingerprint) => LearnMeterPin(EVSE, Endpoint, what, fingerprint));

            // Every step that refused it, and not only the last: "refused, as
            // its configuration says" says nothing without what it was held to
            // and what it showed.
            var refusals = judgement.Steps.Where (step => step.Level == "error").
                                           Select(step => step.Text).
                                           ToArray();

            return new MeterJudgement(
                       judgement.Accepted,
                       judgement.Outcome,
                       refusals.Length > 0 ? String.Join(" ", refusals) : null
                   );

        }

        #endregion

        #region (private) LearnMeterPin(EVSE, Endpoint, What, Fingerprint)

        /// <summary>
        /// Hold the meter of an EVSE to the fingerprint it was first believed
        /// with, from now on: written into its entry of the "meters" section, as
        /// a pin somebody typed would be.
        /// </summary>
        /// <remarks>
        /// Asked from inside the handshake that believed it. Waits a moment for a
        /// reconfiguration somebody else is in the middle of, and leaves the
        /// lesson to the next connection where that takes longer.
        /// </remarks>
        /// <returns>Whether the meter is held to it now.</returns>
        private Boolean LearnMeterPin(Byte             EVSE,
                                      MeterEndpoint    Endpoint,
                                      TrustOnFirstUse  What,
                                      String           Fingerprint)
        {

            if (!reconfigureLock.Wait(MeterLearningWait))
                return false;

            try
            {

                // Not where the meter is read any more - moved while it was
                // being judged: what it showed says nothing about where it is.
                if (metersFile.MeterOf(EVSE) is not MeterConfiguration meter || meter.Endpoint != Endpoint)
                    return false;

                var pins = meter.Pins ?? ServerPins.None;

                // Learned already - by another connection, or by somebody's hand
                // in between: the pin that is there is the one that counts.
                if ((What == TrustOnFirstUse.Root ? pins.Roots : pins.Certificates).Count > 0)
                    return true;

                var changed = metersFile.With(EVSE, meter with { Pins = pins.Learning(What, Fingerprint) });

                if (!ConfigFile.TryReplaceSection(MetersConfiguration.SectionName, changed.ToJSON(), out var error))
                {
                    Log.Error($"{V2GLabelOf(EVSE)}: what its meter is held to from now on could not be written into '{ConfigFile.Path}': {error}",
                              "meter", "certificates", "security");
                    return false;
                }

                metersFile = changed;

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion


        #region (private) StartMeters()

        /// <summary>
        /// Begin reading the meter of every EVSE that is to have one read.
        /// </summary>
        private void StartMeters()
        {

            Certificates.OnChanged += TryTheMetersAgain;

            lock (evseWatchLock)
                lastMeters = MetersJSON().ToString(Formatting.None);

            foreach (var evse in EVSEs)
                if (metersFile.MeterOf(evse.Id) is MeterConfiguration meter)
                    StartMeter(meter);

        }

        #endregion

        #region (private) StartMeter(Meter)

        private void StartMeter(MeterConfiguration Meter)
        {

            var evse     = Meter.EVSE;
            var endpoint = Meter.Endpoint;

            var reader   = new EVSEMeter(
                               evse,
                               endpoint,
                               V2GLabelOf(evse),
                               operation => IdentityFor(evse, operation),
                               (certificate, chain, errors) => JudgeMeter(evse, endpoint, certificate, chain, errors),
                               Log,
                               TimeProvider,
                               MeterReadInterval,
                               MeterRetryInterval
                           );

            meters[evse] = reader;
            readAs[evse] = Meter;

            reader.Start();

        }

        #endregion

        #region (private) TryTheMetersAgain()

        /// <summary>
        /// The store changed: a meter that had no identity to be shown, or whose
        /// certificate chained to no root, may now have one.
        /// </summary>
        private void TryTheMetersAgain()
        {
            foreach (var meter in meters.Values)
                meter.TryAgainNow();
        }

        #endregion

        #region (private) FollowTheMeters()

        /// <summary>
        /// After the EVSEs or the meters were changed: stop reading the meters
        /// of EVSEs that are gone, or that are read another way now, and begin
        /// reading the ones that are new or changed. A meter read as it was is
        /// left alone - and so is every other EVSE's.
        /// </summary>
        private async Task FollowTheMeters()
        {

            if (!started)
                return;

            await meterChanges.WaitAsync();

            try
            {

                var reading = metersFile;

                foreach (var (id, meter) in meters.ToArray())
                {

                    var evse = EVSEs.FirstOrDefault(candidate => candidate.Id == id);
                    var now  = evse is null ? null : reading.MeterOf(id);

                    if (now is not null && now.SameReadingAs(readAs.GetValueOrDefault(id)))
                        continue;

                    Log.Notice(evse is null
                                   ? $"EVSE {id} is gone, and its meter at {meter.Endpoint} is no longer read."
                                   : now is null
                                         ? $"{meter.About}: its meter at {meter.Endpoint} is no longer read."
                                         : $"{meter.About}: its meter is read as {now} now.",
                               "meter", "config");

                    await EndMeter(id);

                }

                foreach (var evse in EVSEs)
                    if (!meters.ContainsKey(evse.Id) && reading.MeterOf(evse.Id) is MeterConfiguration meter)
                        StartMeter(meter);

            }
            finally
            {
                meterChanges.Release();
            }

        }

        #endregion

        #region (private) EndMeter(EVSE) / EndTheMeters()

        private async Task EndMeter(Byte EVSE)
        {
            readAs.TryRemove(EVSE, out _);

            if (meters.TryRemove(EVSE, out var meter))
                await meter.DisposeAsync();
        }

        /// <summary>
        /// Stop reading every meter - what a station does when it stops.
        /// </summary>
        protected virtual async Task EndTheMeters()
        {

            await StopSigning();

            Certificates.OnChanged -= TryTheMetersAgain;

            foreach (var evse in meters.Keys.ToArray())
                await EndMeter(evse);

        }

        #endregion


        #region AskMeter(EVSE, Request, CancellationToken)

        /// <summary>
        /// Ask the meter of an EVSE one Modbus request, as the identity this
        /// station is to it for what the request does - see <see cref="EVSEMeter.Ask"/>.
        /// </summary>
        /// <returns>What came back; null where no meter is read at that EVSE.</returns>
        public Task<JObject>? AskMeter(Byte               EVSE,
                                       ModbusRequest      Request,
                                       CancellationToken  CancellationToken = default)

            => MeterOf(EVSE)?.Ask(Request, CancellationToken);

        #endregion


        #region MetersStatusJSON()

        /// <summary>
        /// How each meter that is to be read stands right now - whether it is
        /// read, over what, and what it measures - and what time it is here.
        /// </summary>
        public JObject MetersStatusJSON()
        {

            var now  = TimeProvider.GetUtcNow();
            var json = MetersJSON();

            json.AddFirst(new JProperty("timestamp", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));

            return json;

        }

        #endregion

        #region (private) MetersJSON()

        private JObject MetersJSON()

            => new (
                   new JProperty("meters", new JArray(
                       EVSEs.Where (evse => metersFile.MeterOf(evse.Id) is not null).
                             Select(evse => {

                                 var json = MeterOf(evse.Id)?.StatusJSON() ?? new JObject(
                                                new JProperty("endpoint",  metersFile.MeterOf(evse.Id)!.Endpoint.ToJSON()),
                                                new JProperty("state",     "notStarted")
                                            );

                                 json.AddFirst(new JProperty("label", evse.PhysicalReference ?? evse.Id.ToString()));
                                 json.AddFirst(new JProperty("evse",  evse.Id));

                                 json["signed"] = SignedSessionSummary(evse.Id);

                                 return json;

                             })
                   ))
               );

        #endregion

        #region ReportMeterChanges()

        /// <summary>
        /// Look at the meters, and tell <see cref="MetersChanged"/> when they are
        /// not as they were the last time - what the station does once a second
        /// while it runs, beside looking at its EVSEs.
        /// </summary>
        /// <returns>Whether they changed.</returns>
        public Boolean ReportMeterChanges()
        {

            JObject json;

            lock (evseWatchLock)
            {

                json = MetersJSON();

                var said = json.ToString(Formatting.None);

                if (said == lastMeters)
                    return false;

                lastMeters = said;

            }

            try
            {
                MetersChanged?.Invoke(json);
            }
            catch (Exception e)
            {
                Log.Warning($"Telling who follows the meters that they changed failed: {e.Message}", "meter");
            }

            return true;

        }

        #endregion

        #region (private) MeasuredPower_kW(EVSE)

        /// <summary>
        /// What the meter of an EVSE measures it delivering right now, in kW;
        /// null where it has no meter read, or none read recently.
        /// </summary>
        private Decimal? MeasuredPower_kW(Byte EVSE)

            => MeterOf(EVSE)?.Reading?.Power_W.All is Decimal watts
                   ? Math.Round(watts / 1000m, 1)
                   : null;

        #endregion

    }

}
