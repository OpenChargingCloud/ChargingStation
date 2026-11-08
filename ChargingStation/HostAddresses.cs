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
using org.GraphDefined.Vanaheimr.Hermod.SSH;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The addresses of a host this station connects to by a socket of its own:
    /// a meter, the TCP probe of a connection test.
    /// </summary>
    /// <remarks>
    /// Resolved with this station's DNS client and never with the resolver of
    /// the operating system: the station's name servers, DNS over TLS or HTTPS
    /// and the names it pins are what a CSMS and the web interface configure,
    /// and a socket handed a host name would go around all of it. Hermod's
    /// resolution of SSH hosts does exactly this, so it is used rather than
    /// repeated: an IP address is itself, "localhost" the loopback addresses,
    /// and a host name what the DNS client says, IPv6 first.
    /// </remarks>
    public static class HostAddresses
    {

        /// <summary>
        /// The addresses to connect to the given host, in the order to try them.
        /// </summary>
        /// <param name="Host">A host name, an IP address, or "localhost".</param>
        /// <param name="DNSClient">This station's DNS client.</param>
        /// <param name="CancellationToken">A cancellation token.</param>
        /// <exception cref="SshNameResolutionException">A host name without a DNS client, or one it found no address for.</exception>
        public static async Task<System.Net.IPAddress[]> Of(String             Host,
                                                            IDNSClient?        DNSClient,
                                                            CancellationToken  CancellationToken = default)

            => [.. await SshTcp.ResolveAsync(Host, DNSClient, CancellationToken)];

    }

}
