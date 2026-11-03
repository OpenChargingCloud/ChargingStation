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

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

using cloud.charging.open.ChargingStation.OCPP;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the CSMS is told of the time servers without asking: the security
    /// events of the Secure Time Synchronization white paper (section 3.2.6),
    /// and the events of the monitors it set (NTS concept B.12).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told through the queue of each OCPP node a connection of its version is
    /// configured for, so that an event outlives an outage and a restart; a
    /// station that dials no CSMS of a version queues nothing for it.
    /// </para>
    /// <para>
    /// A change made through OCPP is the CSMS's own and is not told back, as
    /// B.12 point 7 says; its monitors are asked all the same, because the
    /// node writes a value the way it keeps it, which need not be the way it
    /// was sent. The whole of what changed is in the log; the techInfo of a
    /// security event carries as much of it as fits.
    /// </para>
    /// <para>
    /// SettingSystemTime is not told: the node has no threshold for an offset
    /// to measure against yet (B.4 point 6).
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region (private) FollowNTSEvents()

        /// <summary>
        /// Tell the CSMS what the node says of its time servers.
        /// </summary>
        private void FollowNTSEvents()
        {

            OnNTSGroupChanged += (group, changes, origin) => {
                if (!ChangeOrigin.IsOCPP(origin))
                    TellCSMS("ReconfigurationOfSecurityParameters",
                             $"nts.groups.{group.Id}: {String.Join("; ", changes)} (by {origin ?? "nobody said"})");
                CheckMonitors();
            };

            OnNTSGroupAdded   += (group, origin) => {
                if (!ChangeOrigin.IsOCPP(origin))
                    TellCSMS("ReconfigurationOfSecurityParameters", $"nts.groups.{group.Id} added (by {origin ?? "nobody said"})");
                CheckMonitors();
            };

            OnNTSGroupRemoved += (group, origin) => {
                if (!ChangeOrigin.IsOCPP(origin))
                    TellCSMS("ReconfigurationOfSecurityParameters", $"nts.groups.{group.Id} removed (by {origin ?? "nobody said"})");
                CheckMonitors();
            };

            OnNTSGroupLimited += WhenNTSGroupLimited;

            OnServerNews += (judgement, news) => {

                if (judgement.Service != CertificateUsages.NTS)
                    return;

                switch (news)
                {

                    case ServerNews.Refused:
                        TellCSMS("InvalidTimeServerCertificate",
                                 $"{judgement.Server}: {judgement.Outcome}, certificate {judgement.CertificateFingerprint ?? "none"}, root {judgement.RootFingerprint ?? "none"}");
                        break;

                    case ServerNews.CertificateChanged:
                        TellCSMS("TimeServerCertificateChanged",
                                 $"{judgement.Server}: certificate {judgement.Previously?.Certificate} -> {judgement.CertificateFingerprint}, " +
                                 $"root {judgement.Previously?.Root ?? "unknown"} -> {judgement.RootFingerprint ?? "unknown"}");
                        break;

                }

                CheckMonitors();

            };

        }

        #endregion

        #region (internal) WhenNTSGroupLimited(Group, Limited)

        /// <summary>
        /// The legal group reached MaxErrors, or produced a time again after it.
        /// </summary>
        /// <remarks>
        /// Of its own, so that a test can say it without making a group fail
        /// ten rounds in a row.
        /// </remarks>
        internal void WhenNTSGroupLimited(NTSGroup  Group,
                                          Boolean   Limited)
        {

            if (Group.IsLegal)
                TellCSMS(Limited ? "LegalTimeLimited" : "LegalTimeRestored",
                         Limited ? $"nts.groups.{Group.Id}: {Group.ErrorsInARow} errors in a row"
                                 : $"nts.groups.{Group.Id}: a round produced a time again");

            CheckMonitors();

        }

        #endregion

        #region (private) TellCSMS(Type, TechInfo, Service = NTS)

        /// <summary>
        /// Queue a security event at each OCPP node a connection of its version
        /// is configured for, and write the whole of it into the log, tagged
        /// with the service it is of: the time servers' or the name servers'.
        /// </summary>
        private void TellCSMS(String  Type,
                              String  TechInfo,
                              String  Service   = CertificateUsages.NTS)
        {

            var versions = Connections.Connections.Select(connection => connection.OCPPVersion).Distinct().ToArray();

            if (versions.Length == 0)
                return;

            Log.Notice($"Security event {Type} for the CSMS: {TechInfo}", Service, "ocpp", "security");

            if (versions.Contains(OCPPVersion.OCPP2_1))
                Observe(cs02.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo), Type);

            if (versions.Contains(OCPPVersion.OCPP1_6))
                Observe(cs01.QueueSecurityEventAsync(OCPPv1_6.SecurityEvent.Parse(Type),     TechInfo), Type);

        }

        #endregion

        #region (private) CheckMonitors()

        /// <summary>
        /// Ask the monitors the CSMS set whether what they watch changed.
        /// </summary>
        private void CheckMonitors()
        {
            if (cs02.VariableMonitors.All.Count > 0)
                Observe(cs02.CheckMonitorsAsync(), "monitor events");
        }

        #endregion

        #region (private) Observe(Sending, What)

        /// <summary>
        /// Let a sending run on, and write into the log where it failed: it was
        /// queued before, and is tried again.
        /// </summary>
        private void Observe(Task     Sending,
                             String   What)
        {
            Sending.ContinueWith(sending => Log.Warning($"Telling the CSMS of {What} failed: {sending.Exception?.GetBaseException().Message}", "ocpp"),
                                 TaskContinuationOptions.OnlyOnFaulted);
        }

        #endregion

    }

}
