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

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the CSMS is told of the name servers without asking: the security
    /// events of the Secure DNS white paper (section 3.1.4), and the events of
    /// the monitors it set (SecureDNS concept B.11).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Told as the time servers are - see ChargingStation.NTSEvents.cs: through
    /// the queue of each OCPP node a connection of its version is configured
    /// for, which matters most here, since a CSMS dialled by its name is out of
    /// reach precisely while the names do not resolve.
    /// </para>
    /// <para>
    /// A change made through OCPP is the CSMS's own and is not told back; its
    /// monitors are asked all the same, because the node writes a value the way
    /// it keeps it - an IPv6 address short, a port written out. A pin learned
    /// on first use is a change of the configuration by "tofu", and told as
    /// such, once: not again as the news of the handshake it was learned at.
    /// A server believed again is in the log alone.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region (private) FollowDNSEvents()

        /// <summary>
        /// Tell the CSMS what the node says of its name servers.
        /// </summary>
        private void FollowDNSEvents()
        {

            OnDNSConfigurationChanged += (changes, origin) => {
                if (!ChangeOrigin.IsOCPP(origin))
                    TellCSMS("ReconfigurationOfSecurityParameters",
                             $"dns: {String.Join("; ", changes)} (by {origin ?? "nobody said"})",
                             CertificateUsages.DNS);
                CheckMonitors();
            };

            OnServerNews += (judgement, news) => {

                if (judgement.Service != CertificateUsages.DNS)
                    return;

                switch (news)
                {

                    case ServerNews.Refused:
                        TellCSMS("InvalidNameServerCertificate",
                                 $"{judgement.Server}: {judgement.Outcome}, certificate {judgement.CertificateFingerprint ?? "none"}, root {judgement.RootFingerprint ?? "none"}",
                                 CertificateUsages.DNS);
                        break;

                    case ServerNews.Tolerated:
                    case ServerNews.Recorded:
                        TellCSMS("NameServerPinMismatchTolerated",
                                 $"{judgement.Server}: {judgement.Outcome}, certificate {judgement.CertificateFingerprint ?? "none"}, root {judgement.RootFingerprint ?? "none"}, " +
                                 $"held to {judgement.HeldTo?.Text ?? "nothing"}",
                                 CertificateUsages.DNS);
                        break;

                    case ServerNews.CertificateChanged:
                        TellCSMS("NameServerCertificateChanged",
                                 $"{judgement.Server}: certificate {judgement.Previously?.Certificate} -> {judgement.CertificateFingerprint}, " +
                                 $"root {judgement.Previously?.Root ?? "unknown"} -> {judgement.RootFingerprint ?? "unknown"}",
                                 CertificateUsages.DNS);
                        break;

                }

                CheckMonitors();

            };

        }

        #endregion

    }

}
