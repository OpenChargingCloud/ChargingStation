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

using System.Net;
using System.Security.Cryptography.X509Certificates;

#endregion

namespace cloud.charging.open.ChargingStation.ISO15118
{

    #region SlacTransportKind

    /// <summary>
    /// Which medium the SLAC listener listens on.
    /// </summary>
    public enum SlacTransportKind
    {

        /// <summary>
        /// No SLAC at all.
        /// </summary>
        None,

        /// <summary>
        /// The real powerline interface where there is one, i.e. AF_PACKET on
        /// Linux, and nothing anywhere else. Never the simulated medium: a
        /// station that quietly matches vehicles over UDP because the machine
        /// it runs on has no powerline modem would be lying about what it is.
        /// </summary>
        Auto,

        /// <summary>
        /// The real powerline interface, EtherType 0x88E1 over AF_PACKET.
        /// Linux only, and needs CAP_NET_RAW.
        /// </summary>
        AfPacket,

        /// <summary>
        /// A simulated shared medium over UDP, for a bench without a powerline
        /// modem. Asked for explicitly, never chosen by itself.
        /// </summary>
        UDP

    }

    #endregion


    /// <summary>
    /// What this charging station offers a vehicle on the wire below the
    /// charging cable: the SLAC matching, the SECC discovery, and the V2G
    /// endpoint the two of them lead to.
    /// </summary>
    /// <remarks>
    /// Off unless somebody says otherwise. Binding UDP 15118, joining an IPv6
    /// multicast group and putting a listener on a link-local address is not
    /// something a charging station should do on a developer's laptop because
    /// the binary happened to start.
    /// </remarks>
    public sealed record V2GOptions
    {

        #region Data

        /// <summary>
        /// The EVSE identification SLAC hands to a vehicle, unless another is given.
        /// </summary>
        public const String DefaultEVSEId = "DE*GEF*E0001*1";

        #endregion

        #region Properties

        /// <summary>
        /// Whether any of this runs at all.
        /// </summary>
        public Boolean            Enabled            { get; init; }

        /// <summary>
        /// The network interface the vehicle is on: the powerline modem. Null
        /// takes the first one the system offers that looks like a candidate,
        /// which on a machine with one cable is the right guess and on a
        /// machine with six is not - so the console says which one it took.
        /// </summary>
        public String?            InterfaceName      { get; init; }

        /// <summary>
        /// The TCP port the V2G endpoint listens on; 0 lets the operating
        /// system pick a free one, which SDP then advertises. There is no
        /// well-known port for it - SDP exists precisely so that there need
        /// not be one.
        /// </summary>
        public UInt16             V2GPort            { get; init; }

        /// <summary>
        /// The certificate the V2G endpoint authenticates itself with. Without
        /// one the endpoint speaks plain TCP, which ISO 15118-20 does not
        /// allow and which the station says out loud when it starts.
        /// </summary>
        public X509Certificate2?  ServerCertificate  { get; init; }

        /// <summary>
        /// Whether the SECC Discovery Protocol answers vehicles looking for
        /// that endpoint.
        /// </summary>
        public Boolean            SDP                { get; init; } = true;

        /// <summary>
        /// Which medium the SLAC listener listens on.
        /// </summary>
        public SlacTransportKind  SlacTransport      { get; init; } = SlacTransportKind.Auto;

        /// <summary>
        /// Where the simulated SLAC medium lives, for
        /// <see cref="SlacTransportKind.UDP"/>. Null listens on a free port of
        /// the loopback address.
        /// </summary>
        public IPEndPoint?        SlacUDPEndpoint    { get; init; }

        /// <summary>
        /// The other stations on that simulated medium, which UDP has no
        /// broadcast of its own to reach.
        /// </summary>
        public IEnumerable<IPEndPoint>? SlacUDPPeers { get; init; }

        /// <summary>
        /// The EVSE identification SLAC hands to a vehicle. Cut or padded with
        /// NUL to the 17 bytes HomePlug wants.
        /// </summary>
        public String             EVSEId             { get; init; } = DefaultEVSEId;

        #endregion


        /// <summary>
        /// Nothing on the wire below the cable - the default.
        /// </summary>
        public static V2GOptions Off { get; } = new();

    }

}
