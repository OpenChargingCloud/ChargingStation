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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Meters;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The energy meter of each EVSE that is configured to have one read: a
    /// SunSpec meter over Modbus/TLS, read once a second while the station runs.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Who the station is to a meter is a TLS identity of its certificate store
    /// whose certificate carries a SunSpec role - the least one that may read,
    /// ReadOnlySunSpec, where there is more than one - so that a meter which
    /// decides what a peer may do by the role in its certificate lets this
    /// station read and nothing else.
    /// </para>
    /// <para>
    /// Whom the station believes a meter to be is judged as every server the
    /// node connects to is judged: issued for the name or the address it is
    /// reached at, and chained to a root this machine trusts or a TLS root of
    /// the store for "meter" - see <see cref="MeterCertificateUsage"/>.
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

        private readonly ConcurrentDictionary<Byte, EVSEMeter>  meters        = [];
        private readonly SemaphoreSlim                          meterChanges  = new (1, 1);

        #endregion

        #region Properties

        /// <summary>
        /// The meter of each EVSE that has one read, by the EVSE's id.
        /// </summary>
        public IReadOnlyDictionary<Byte, EVSEMeter> Meters
            => meters;

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


        #region MeterOf(EVSE)

        /// <summary>
        /// The meter of the given EVSE, where it has one read.
        /// </summary>
        public EVSEMeter? MeterOf(Byte EVSE)

            => meters.TryGetValue(EVSE, out var meter)
                   ? meter
                   : null;

        #endregion

        #region MeterIdentity()

        /// <summary>
        /// Who this station is to its meters right now: the TLS identity of its
        /// store that carries the least SunSpec role, or null where none does.
        /// </summary>
        /// <remarks>
        /// Read anew for every connection, with its private key and the CAs its
        /// file holds beside it; whoever asks disposes of it.
        /// </remarks>
        public MeterIdentity? MeterIdentity()
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

            // The least that may read first - SunSpec lists them that way: a
            // station that reads a meter has no business holding the role that
            // may reset its counters.
            static Int32 RankOf(String Role)

                => Array.IndexOf([.. SunSpecRoles.AllMandatory], Role) is Int32 rank and >= 0
                       ? rank
                       : Int32.MaxValue;

        }

        #endregion

        #region (private) TryLoadIdentity(Entry, out Identity)

        /// <summary>
        /// One TLS identity of the store, where its certificate carries a
        /// SunSpec role a meter knows and its file has the private key.
        /// </summary>
        private Boolean TryLoadIdentity(CertificateEntry                                                   Entry,
                                        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out MeterIdentity? Identity)
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
                           Entry.Label
                       );

            return true;

        }

        #endregion

        #region (private) JudgeMeter(Endpoint, Certificate, Chain, PolicyErrors)

        /// <summary>
        /// Whether to go ahead with the certificate a meter showed, as the node
        /// judges every server it connects to.
        /// </summary>
        private MeterJudgement JudgeMeter(MeterEndpoint      Endpoint,
                                          X509Certificate2?  Certificate,
                                          X509Chain?         Chain,
                                          SslPolicyErrors    PolicyErrors)
        {

            var judgement = JudgeServer(MeterCertificateUsage,
                                        Endpoint.Host,
                                        null,
                                        Certificate,
                                        Chain,
                                        PolicyErrors,
                                        Evidence: false);

            return new MeterJudgement(
                       judgement.Accepted,
                       judgement.Outcome,
                       judgement.Steps.LastOrDefault(step => step.Level == "error").Text
                   );

        }

        #endregion


        #region (private) StartMeters()

        /// <summary>
        /// Begin reading the meter of every EVSE that is to have one read.
        /// </summary>
        private void StartMeters()
        {

            Certificates.OnChanged += TryTheMetersAgain;

            foreach (var evse in EVSEs)
                StartMeter(evse);

        }

        #endregion

        #region (private) StartMeter(EVSE)

        private void StartMeter(EVSEConfig EVSE)
        {

            if (EVSE.MeterEndpoint is not MeterEndpoint endpoint)
                return;

            var meter = new EVSEMeter(
                            EVSE.Id,
                            endpoint,
                            V2GLabelOf(EVSE.Id),
                            MeterIdentity,
                            (certificate, chain, errors) => JudgeMeter(endpoint, certificate, chain, errors),
                            Log,
                            TimeProvider,
                            MeterReadInterval,
                            MeterRetryInterval
                        );

            meters[EVSE.Id] = meter;

            meter.Start();

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
        /// After the EVSEs were changed: stop reading the meters of EVSEs that
        /// are gone or are read somewhere else now, and begin reading the ones
        /// that are new or moved. A meter whose EVSE kept it is left alone.
        /// </summary>
        private async Task FollowTheMeters()
        {

            if (!started)
                return;

            await meterChanges.WaitAsync();

            try
            {

                foreach (var (id, meter) in meters.ToArray())
                {

                    var now = EVSEs.FirstOrDefault(evse => evse.Id == id)?.MeterEndpoint;

                    if (now == meter.Endpoint)
                        continue;

                    Log.Notice(now is null
                                   ? $"{meter.About}: its meter at {meter.Endpoint} is no longer read."
                                   : $"{meter.About}: its meter is read at {now} now, not at {meter.Endpoint}.",
                               "meter", "config");

                    await EndMeter(id);

                }

                foreach (var evse in EVSEs)
                    if (evse.MeterEndpoint is not null && !meters.ContainsKey(evse.Id))
                        StartMeter(evse);

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
            if (meters.TryRemove(EVSE, out var meter))
                await meter.DisposeAsync();
        }

        /// <summary>
        /// Stop reading every meter - what a station does when it stops.
        /// </summary>
        protected virtual async Task EndTheMeters()
        {

            Certificates.OnChanged -= TryTheMetersAgain;

            foreach (var evse in meters.Keys.ToArray())
                await EndMeter(evse);

        }

        #endregion

        #region (private) MeterStatusJSON(EVSE)

        /// <summary>
        /// The meter of one EVSE, as the EVSEs page shows it: what is written
        /// down about it, and - where it is read - how that goes and what it
        /// said.
        /// </summary>
        private JObject? MeterStatusJSON(EVSEConfig EVSE)
        {

            if (EVSE.MeterType is null && EVSE.MeterSerialNumber is null && EVSE.MeterEndpoint is null)
                return null;

            var json = new JObject(
                           new JProperty("type",          EVSE.MeterType),
                           new JProperty("serialNumber",  EVSE.MeterSerialNumber)
                       );

            if (EVSE.MeterEndpoint is not null)
                json.Add("read", MeterOf(EVSE.Id)?.StatusJSON());

            return json;

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
