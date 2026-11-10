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

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The time from the back end over the connection OCPP goes over: where a
    /// time server's entry says "useOCPPConnection" - SSHUseOCPPConnection of
    /// the OCPP Device Model - its key exchange is one more channel of the SSH
    /// connection this station dialled to that server's host for OCPP (section
    /// 8.3 of the white paper "NTS Key Establishment over SSH").
    /// </summary>
    /// <remarks>
    /// The station signs in for it as it does for OCPP, with its own SSH key,
    /// and the server is the one OCPP believed by its host key: no second key
    /// pair, no pin of its own, no connection of its own. Where OCPP goes over
    /// no SSH connection to that host, or the connection is down, the server
    /// cannot be reached for its key exchange; the cookies it handed out before
    /// keep NTP going.
    /// </remarks>
    public partial class ChargingStation
    {

        #region (private) ShareTheConnectionsOfOCPP()

        /// <summary>
        /// Tell the node which SSH connections its OCPP goes over, and the key it
        /// signs in with there: asked anew at every key exchange, as the
        /// connections come and go, and the OCPP nodes with them.
        /// </summary>
        private void ShareTheConnectionsOfOCPP()

            => OCPPOverSSH = new OCPPConnectionsOverSSH(OCPPConnectionOverSSHTo,
                                                        () => SshKey.PublicKeyLine);

        #endregion

        #region (private) OCPPConnectionOverSSHTo(Hostname)

        /// <summary>
        /// The SSH connection one of this station's OCPP connections goes over to
        /// the given host, while it lasts; null where there is none.
        /// </summary>
        /// <remarks>
        /// By the host the connection was dialled to, as its URL names it: a time
        /// server is the host its entry names. The first connection that lasts,
        /// where two back ends live on one host.
        /// </remarks>
        private SshClient? OCPPConnectionOverSSHTo(DomainName Hostname)
        {

            var host = Hostname.Trimmed;

            foreach (var client in cs01.WWCPSshClients.Concat(cs02.WWCPSshClients))
            {

                if (String.Equals(client.MessageClient.Options.Host.TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase) &&
                    client.MessageClient.Ssh is SshClient ssh)
                {
                    return ssh;
                }

            }

            return null;

        }

        #endregion

    }

}
