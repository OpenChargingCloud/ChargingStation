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

using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.T1S.Monitoring;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

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

    #region T1SOptions

    /// <summary>
    /// The 10BASE-T1S bus of a Megawatt Charging System coupler, as this
    /// station coordinates it: where the emulated medium is, what the
    /// station calls itself on it, and where the thermal limits are.
    /// </summary>
    /// <remarks>
    /// <para>
    /// MCS has no powerline and no SLAC. Its link is IEEE 802.3cg 10BASE-T1S -
    /// a multidrop twisted pair with the station as PLCA coordinator - and the
    /// nodes on it are not only the vehicle: a temperature sensor in each pin
    /// of the coupler is a node too, asked every cycle whether the pin is
    /// getting hot. This station coordinates that bus and watches those
    /// sensors; a pin past its limit is an overload, and the station says so.
    /// </para>
    /// <para>
    /// The medium is a real adapter through AF_PACKET where there is one,
    /// or the emulation over UDP multicast for a bench without one - which
    /// is every bench today, and which has to be asked for, as the simulated
    /// SLAC medium has to be. Nothing above the medium knows which it got.
    /// </para>
    /// </remarks>
    /// <param name="Transport">Which medium: the adapter where there is one (Auto), the adapter by name (AfPacket), or the emulated one (UDP), which is never chosen by itself.</param>
    /// <param name="InterfaceName">The adapter, for AF_PACKET - the V2G interface when null; the interface to join the group on, for UDP - the operating system's pick when null.</param>
    /// <param name="Group">The multicast group and port that are the emulated bus; the library's default when null.</param>
    /// <param name="Name">What the station calls itself as coordinator.</param>
    /// <param name="Thermal">Where the thermal lines are drawn; the bench defaults when null.</param>
    /// <param name="CycleGap">The pause between cycles, which is how often every sensor is asked; the library's default when null.</param>
    public sealed record T1SOptions(T1STransportKind             Transport      = T1STransportKind.Auto,
                                    String?                      InterfaceName  = null,
                                    IPEndPoint?                  Group          = null,
                                    String                       Name           = "EVSE",
                                    CableThermalMonitorOptions?  Thermal        = null,
                                    TimeSpan?                    CycleGap       = null);

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

        /// <summary>
        /// The TCP port the V2G endpoint listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// 15118, which IANA registers for v2g-secc and which every other
        /// implementation uses. ISO 15118 does not require it - the port
        /// travels in the SDP response precisely so that it need not be fixed,
        /// and a vehicle that reads the answer finds the endpoint wherever it
        /// is.
        ///
        /// It is the default anyway, because the alternative was worse in
        /// practice than it was wrong in theory. This station used to leave it
        /// at zero and let the operating system choose, so it advertised 53417
        /// on one start and 57443 on the next: nothing a firewall rule can
        /// name, and a trap for the several tools out there that assume the
        /// registered port instead of reading the response.
        ///
        /// Zero still means "any free one" and is still the right answer for a
        /// machine running two stations at once.
        /// </remarks>
        public const UInt16 DefaultV2GPort = 15118;

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
        /// system pick a free one. Whichever it ends up being is what SDP
        /// advertises, because SDP carries the port it was told rather than
        /// one it assumes.
        /// </summary>
        public UInt16             V2GPort            { get; init; } = DefaultV2GPort;

        /// <summary>
        /// The certificate the V2G endpoint authenticates itself with. Without
        /// one the endpoint speaks plain TCP, which ISO 15118-20 does not
        /// allow and which the station says out loud when it starts.
        /// </summary>
        public X509Certificate2?  ServerCertificate  { get; init; }

        /// <summary>
        /// The Sub-CAs between that certificate and the V2G root, which the
        /// endpoint sends along with it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Without these a vehicle gets the leaf and nothing else, and cannot
        /// build a chain to any root it was given - so a station with a
        /// perfectly good certificate from a perfectly good authority is
        /// refused by every vehicle that checks, and accepted only by the ones
        /// that do not. The refusal reads as "a certificate chain to a trusted
        /// root could not be built", which sounds like the vehicle's trust
        /// store is wrong and is not.
        /// </para>
        /// <para>
        /// The root itself does not belong here. A chain that carries its own
        /// root invites the other side to trust it because it is there, and
        /// the whole point of a trust store is that the root arrived by
        /// another route.
        /// </para>
        /// </remarks>
        public X509Certificate2Collection?  ServerCertificateChain  { get; init; }

        /// <summary>
        /// Whether the SECC Discovery Protocol answers vehicles looking for
        /// that endpoint.
        /// </summary>
        public Boolean            SDP                { get; init; } = true;

        /// <summary>
        /// Whether SDP also answers vehicles running on this same machine.
        /// </summary>
        /// <remarks>
        /// Off, and off is what a station in the field wants: a charging
        /// station has no business answering a simulator somebody left running
        /// on its own controller. On for a bench where the vehicle is another
        /// process here.
        ///
        /// Whose socket this switch belongs to is a thing the platforms
        /// disagree about - POSIX says the sender's, Windows the receiver's -
        /// so a bench sets it on the vehicle as well. The measurement is in
        /// the remark on SECC_SDPServerOptions.MulticastLoopback.
        /// </remarks>
        public Boolean            MulticastLoopback  { get; init; }

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

        /// <summary>
        /// Whether the outlet behind this endpoint is AC or DC, which is what
        /// the station offers a vehicle during the protocol handshake.
        /// </summary>
        /// <remarks>
        /// AC, because a type 2 socket is what this station has. It is not a
        /// preference: the namespace a vehicle asks for names the mode, so a
        /// station offering the wrong one negotiates nothing at all.
        /// </remarks>
        public PowerMode          Mode               { get; init; } = PowerMode.Ac;

        /// <summary>
        /// How long one request-response step of a session may take before the
        /// station gives up on it.
        /// </summary>
        /// <remarks>
        /// Per step rather than per session, and it is the state machines that
        /// enforce it. Sixty seconds is what the reference SECC uses; a car
        /// that has gone quiet for a minute in the middle of a handshake is not
        /// coming back, and this endpoint is serving one cable.
        /// </remarks>
        public TimeSpan           SessionTimeout     { get; init; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The 10BASE-T1S bus of an MCS coupler, when this station is one.
        /// Null - no bus - for a CCS station, which has SLAC instead.
        /// </summary>
        public T1SOptions?        T1S                { get; init; }

        #endregion


        /// <summary>
        /// Nothing on the wire below the cable - the default.
        /// </summary>
        public static V2GOptions Off { get; } = new();

    }

}
