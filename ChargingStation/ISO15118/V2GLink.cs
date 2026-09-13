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
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.Ethernet;

using cloud.charging.open.protocols.ISO15118.NetworkInterfaces;
using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.ISO15118.SDP.Server;
using cloud.charging.open.protocols.ISO15118.SLAC.StateMachine;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport.Linux;
using cloud.charging.open.protocols.ISO15118.Transport;
using cloud.charging.open.protocols.ISO15118.V2GTP;
using cloud.charging.open.protocols.ISO15118.Framing;

using cloud.charging.open.ChargingStation.Logging;

#endregion

namespace cloud.charging.open.ChargingStation.ISO15118
{

    /// <summary>
    /// What happens on the wire below the charging cable, and what it says
    /// about itself in the log.
    /// </summary>
    /// <remarks>
    /// Three things, in the order a vehicle meets them:
    ///
    /// <list type="number">
    ///   <item><b>SLAC</b> matches the powerline modem in the car to the one
    ///   in this station, so that the two share a network and nobody talks to
    ///   the car parked next to it (ISO 15118-3, HomePlug Green PHY).</item>
    ///   <item><b>SDP</b> answers the car asking, over IPv6 multicast, where
    ///   the charging station's V2G endpoint is.</item>
    ///   <item>The <b>V2G endpoint</b> is what that answer points at: a TCP
    ///   listener, with TLS where a certificate was given.</item>
    /// </list>
    ///
    /// They are wired to each other and not just started next to each other:
    /// the listener is bound first, and the port the operating system gave it
    /// is what SDP advertises. A station that discovers its own endpoint wrong
    /// is a station no car can reach.
    ///
    /// Every event of all three goes into the station's event log tagged
    /// "15118" plus "slac", "sdp" or "v2g", which is what makes the Logs page
    /// of the web interface show a whole plug-in as one sequence.
    /// </remarks>
    public sealed class V2GLink : IAsyncDisposable
    {

        #region Data

        /// <summary>
        /// The number of bytes HomePlug wants an EVSE identification to have.
        /// </summary>
        public const Int32 EVSEIdSize = 17;

        /// <summary>
        /// The number of bytes of a HomePlug AV network identifier.
        /// </summary>
        public const Int32 NIDSize    = 7;

        /// <summary>
        /// The number of bytes of a HomePlug AV network membership key.
        /// </summary>
        public const Int32 NMKSize    = 16;

        private readonly EventLog                 log;
        private readonly CancellationTokenSource  shutdown = new();

        private          ISlacTransport?          slacTransport;
        private          EvseSlacListener?        slacListener;
        private          SECC_SDPServer?          sdpServer;
        private          TcpV2GListener?          v2gListener;
        private          Task?                    acceptLoop;

        #endregion

        #region Properties

        /// <summary>
        /// What was asked for.
        /// </summary>
        public V2GOptions            Options       { get; }

        /// <summary>
        /// The interface the vehicle is on, when one was found.
        /// </summary>
        public V2GNetworkInterface?  Interface     { get; private set; }

        /// <summary>
        /// Where the V2G endpoint listens, once it does.
        /// </summary>
        public IPEndPoint?           V2GEndpoint   { get; private set; }

        /// <summary>
        /// Whether the V2G endpoint speaks TLS.
        /// </summary>
        public Boolean               UsesTLS
            => Options.ServerCertificate is not null;

        /// <summary>
        /// Whether SLAC is listening.
        /// </summary>
        public Boolean               SLACRunning
            => slacListener is not null;

        /// <summary>
        /// Whether SDP is answering.
        /// </summary>
        public Boolean               SDPRunning
            => sdpServer is not null;

        /// <summary>
        /// The SLAC matchings going on right now.
        /// </summary>
        public Int32                 ActiveSLACSessions
            => slacListener?.ActiveSessions.Count ?? 0;

        #endregion

        #region Constructor(s)

        private V2GLink(V2GOptions  Options,
                        EventLog    Log)
        {
            this.Options  = Options;
            this.log      = Log;
        }

        #endregion


        #region (static) TryStart(Options, Log, CancellationToken = default)

        /// <summary>
        /// Bring up as much of the three as the options ask for and the machine
        /// allows; null when the options ask for none of it.
        /// </summary>
        /// <remarks>
        /// One piece failing does not take the others down - a station without
        /// a powerline modem should still answer SDP on the bench - but every
        /// piece that does not come up says why, at a level somebody will see.
        /// </remarks>
        public static async Task<V2GLink?> TryStart(V2GOptions         Options,
                                                    EventLog           Log,
                                                    CancellationToken  CancellationToken   = default)
        {

            if (!Options.Enabled)
                return null;

            var link = new V2GLink(Options, Log);

            link.FindInterface();

            await link.StartV2GEndpoint  (CancellationToken);
            await link.StartSDPServer    (CancellationToken);
            await link.StartSLACListener (CancellationToken);

            return link;

        }

        #endregion


        #region (private) FindInterface()

        /// <summary>
        /// The interface the vehicle is on: the one that was named, or the
        /// first candidate the system offers.
        /// </summary>
        private void FindInterface()
        {

            var provider = new SystemV2GNetworkInterfaceProvider();

            if (Options.InterfaceName is not null)
            {

                Interface = provider.FindByName(Options.InterfaceName);

                if (Interface is null)
                    log.Error(
                        $"There is no network interface '{Options.InterfaceName}', or it has no IPv6 link-local address. " +
                        $"Candidates: {Describe(provider.Discover())}",
                        "15118"
                    );

            }

            else
            {

                var candidates = provider.Discover();

                Interface = candidates.FirstOrDefault();

                if (Interface is null)
                    log.Warning(
                        "No network interface with an IPv6 link-local address was found: SDP and AF_PACKET SLAC need one.",
                        "15118"
                    );

                else if (candidates.Count > 1)
                    log.Warning(
                        $"Taking '{Interface.Name}' as the interface to the vehicle, out of {candidates.Count} candidates " +
                        $"({Describe(candidates)}). Name one to be sure.",
                        "15118"
                    );

            }

            if (Interface is not null)
                log.Notice(
                    $"The vehicle is expected on '{Interface.Name}' ({Interface.LinkLocalIPAddress}, " +
                    $"{MACAddress.From(Interface.MACAddress)}).",
                    "15118"
                );

        }

        #endregion

        #region (private) StartV2GEndpoint  (CancellationToken)

        /// <summary>
        /// The TCP listener a vehicle connects to once it knows where it is.
        /// </summary>
        private Task StartV2GEndpoint(CancellationToken CancellationToken)
        {

            try
            {

                var tls = Options.ServerCertificate is null
                              ? null
                              : new TlsOptions {
                                    ServerCertificate    = Options.ServerCertificate,
                                    // TLS 1.3 alone: ISO 15118-20 asks for it,
                                    // and a station standing on a public street
                                    // has no legacy client to be kind to.
                                    EnabledSslProtocols  = SslProtocols.Tls13
                                };

                v2gListener  = new TcpV2GListener(
                                   new IPEndPoint(IPAddress.IPv6Any, Options.V2GPort),
                                   tls
                               );

                V2GEndpoint  = v2gListener.LocalEndpoint;

                log.Notice(
                    $"The V2G endpoint is listening on {V2GEndpoint} " +
                    (UsesTLS
                         ? $"with TLS 1.3, certificate '{Options.ServerCertificate!.Subject}'."
                         : "without TLS."),
                    "15118", "v2g", "tls"
                );

                if (!UsesTLS)
                    log.Warning(
                        "The V2G endpoint speaks plain TCP: no certificate was given. ISO 15118-20 requires TLS, " +
                        "and a -2 vehicle that asks for it will be turned away by SDP.",
                        "15118", "v2g", "tls"
                    );

                acceptLoop = Task.Run(() => AcceptLoop(shutdown.Token), CancellationToken);

            }
            catch (Exception e)
            {
                log.Exception(e, "The V2G endpoint could not be opened", "15118", "v2g");
            }

            return Task.CompletedTask;

        }

        #endregion

        #region (private) AcceptLoop        (CancellationToken)

        /// <summary>
        /// Every vehicle that connects, and the first thing it says.
        /// </summary>
        /// <remarks>
        /// The session above this - SupportedAppProtocol, the EXI messages of
        /// -2 or -20, the charging loop - is not wired up yet. So the frame is
        /// read, said out loud, and the connection is closed again. That is
        /// more use than it sounds: it is the difference between "the listener
        /// is bound" and "a vehicle got through SLAC, found us over SDP,
        /// connected, and its first frame was a SupportedAppProtocol request" -
        /// which is the whole handshake, and all of it in the log.
        /// </remarks>
        private async Task AcceptLoop(CancellationToken CancellationToken)
        {

            while (!CancellationToken.IsCancellationRequested && v2gListener is not null)
            {

                Stream? stream = null;

                try
                {

                    stream = await v2gListener.AcceptAsync(CancellationToken);

                    log.Notice($"A vehicle connected to the V2G endpoint.", "15118", "v2g");

                    var (frame, payloadType) = await V2GTPStream.ReadRawFrameAsync(stream, CancellationToken);

                    log.Log(
                        LogLevel.Info,
                        $"Its first V2GTP frame is {Name(payloadType)}, {frame.Length} bytes.",
                        new JObject(
                            new JProperty("payloadType",  $"0x{payloadType:X4}"),
                            new JProperty("payloadName",  Name(payloadType)),
                            new JProperty("bytes",        frame.Length)
                        ),
                        "15118", "v2g"
                    );

                    log.Warning(
                        "Closing it again: the V2G session layer above the listener is not wired up yet.",
                        "15118", "v2g"
                    );

                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception e)
                {
                    log.Exception(e, "A V2G connection failed", "15118", "v2g");
                }
                finally
                {
                    if (stream is not null)
                        await stream.DisposeAsync();
                }

            }

        }

        #endregion

        #region (private) StartSDPServer    (CancellationToken)

        /// <summary>
        /// The answer to a vehicle asking where the V2G endpoint is.
        /// </summary>
        private async Task StartSDPServer(CancellationToken CancellationToken)
        {

            if (!Options.SDP)
                return;

            if (Interface is null)
            {
                log.Error("SDP cannot start: it needs a network interface with an IPv6 link-local address.", "15118", "sdp");
                return;
            }

            if (V2GEndpoint is null)
            {
                log.Error("SDP will not start: there is no V2G endpoint for it to point a vehicle at.", "15118", "sdp");
                return;
            }

            try
            {

                sdpServer = new SECC_SDPServer(
                                new SECC_SDPServerOptions {
                                    Interface        = Interface,
                                    SeccPort         = (UInt16) V2GEndpoint.Port,
                                    // What the endpoint actually is, not what
                                    // the standard would like it to be: a
                                    // station without a certificate that
                                    // advertised TLS would send every vehicle
                                    // into a handshake that cannot finish.
                                    OfferedSecurity  = UsesTLS
                                                           ? SDP_Security.TLS
                                                           : SDP_Security.NoTLS
                                }
                            );

                sdpServer.RequestReceived          += request => log.Log(
                                                          request.Accepted ? LogLevel.Info : LogLevel.Warning,
                                                          request.Accepted
                                                              ? $"SDP request from {request.RemoteEndpoint}: {request.Request.Version}, {request.Request.Security}, {request.Request.TransportProtocol}."
                                                              : $"SDP request from {request.RemoteEndpoint} refused: {request.RejectReason}",
                                                          "15118", "sdp"
                                                      );

                sdpServer.ResponseSent             += response => log.Info(
                                                          $"SDP answered {response.RemoteEndpoint}: the V2G endpoint is " +
                                                          $"[{response.Response.SeccIPAddress}]:{response.Response.SeccPort}, {response.Response.Security}.",
                                                          "15118", "sdp"
                                                      );

                sdpServer.MalformedRequestReceived += malformed => log.Warning(
                                                          $"A malformed SDP frame from {malformed.RemoteEndpoint} ({malformed.RawBytes.Length} bytes): {malformed.Reason}",
                                                          "15118", "sdp"
                                                      );

                await sdpServer.Start(CancellationToken);

                log.Notice(
                    $"SDP is answering on '{Interface.Name}', pointing vehicles at port {V2GEndpoint.Port}.",
                    "15118", "sdp"
                );

            }
            catch (Exception e)
            {
                sdpServer = null;
                log.Exception(e, "SDP could not start", "15118", "sdp");
            }

        }

        #endregion

        #region (private) StartSLACListener (CancellationToken)

        /// <summary>
        /// The matching of the powerline modem in the car to the one here.
        /// </summary>
        private async Task StartSLACListener(CancellationToken CancellationToken)
        {

            slacTransport = OpenSlacTransport();

            if (slacTransport is null)
                return;

            try
            {

                var evseId = EVSEIdBytes(Options.EVSEId);

                slacListener = new EvseSlacListener(
                                   slacTransport,
                                   // A fresh network key per vehicle, which is
                                   // the point of the exercise: the key is what
                                   // keeps this car's link apart from the one at
                                   // the next outlet.
                                   () => new EvseSlacOptions {
                                             EvseId  = evseId,
                                             Nid     = RandomNumberGenerator.GetBytes(NIDSize),
                                             Nmk     = RandomNumberGenerator.GetBytes(NMKSize)
                                         }
                               );

                slacListener.SessionStarted    += (sender, session) => log.Notice(
                                                      $"SLAC matching started with {session.PevMac} (run {session.RunId}).",
                                                      "15118", "slac"
                                                  );

                slacListener.SessionCompleted  += (sender, completed) => log.Notice(
                                                      $"SLAC matched {completed.Session.PevMac} (run {completed.Session.RunId}): the vehicle is on our network now.",
                                                      "15118", "slac"
                                                  );

                slacListener.SessionFailed     += (sender, failed) => log.Warning(
                                                      $"SLAC matching with {failed.Session.PevMac} failed in state {failed.Session.State}: {failed.Error.Message}",
                                                      "15118", "slac"
                                                  );

                // The listener's own running commentary, which has no level of
                // its own - it is the detail somebody turns on when a vehicle
                // will not match, so it goes in quietly.
                slacListener.Log               += (sender, message) => log.Debug(message, "15118", "slac");

                await slacListener.StartAsync(CancellationToken);
                await slacTransport.StartAsync(CancellationToken);

                log.Notice($"SLAC is listening on {Describe(slacTransport)}.", "15118", "slac");

            }
            catch (Exception e)
            {
                slacListener = null;
                log.Exception(e, "SLAC could not start", "15118", "slac");
            }

        }

        #endregion

        #region (private) OpenSlacTransport ()

        /// <summary>
        /// The medium SLAC listens on, or null when there is none to listen on.
        /// </summary>
        private ISlacTransport? OpenSlacTransport()
        {

            var kind = Options.SlacTransport;

            if (kind == SlacTransportKind.None)
                return null;

            if (kind == SlacTransportKind.Auto)
            {

                // Only the real medium is ever chosen by itself. The simulated
                // one has to be asked for, because a station that matches
                // vehicles over UDP without being told to would look like it
                // works and be talking to nothing.
                if (!OperatingSystem.IsLinux())
                {
                    log.Warning(
                        "SLAC is not listening: the powerline interface needs AF_PACKET, which is Linux only. " +
                        "Ask for the simulated medium explicitly to run without a modem.",
                        "15118", "slac"
                    );
                    return null;
                }

                kind = SlacTransportKind.AfPacket;

            }

            try
            {

                if (kind == SlacTransportKind.AfPacket)
                {

                    if (Interface is null)
                    {
                        log.Error("SLAC cannot use AF_PACKET: no network interface was found.", "15118", "slac");
                        return null;
                    }

                    return new AfPacketSlacTransport(Interface.Name);

                }

                return new UdpSlacTransport(
                           Interface is not null
                               ? MACAddress.From(Interface.MACAddress)
                               : MACAddress.Parse("02:00:00:00:00:01"),
                           Options.SlacUDPEndpoint ?? new IPEndPoint(IPAddress.Loopback, 0),
                           Options.SlacUDPPeers
                       );

            }
            catch (Exception e)
            {
                log.Exception(e, $"The SLAC transport ({kind}) could not be opened", "15118", "slac");
                return null;
            }

        }

        #endregion


        #region ToJSON()

        /// <summary>
        /// What is on the wire below the cable, for the Configuration page.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("interface",      Interface?.Name),
                   new JProperty("linkLocal",      Interface?.LinkLocalIPAddress.ToString()),
                   new JProperty("v2gEndpoint",    V2GEndpoint?.ToString()),
                   new JProperty("v2gTLS",         UsesTLS),
                   new JProperty("sdp",            SDPRunning),
                   new JProperty("slac",           SLACRunning),
                   new JProperty("slacTransport",  slacTransport is null ? null : Describe(slacTransport)),
                   new JProperty("slacSessions",   ActiveSLACSessions),
                   new JProperty("evseId",         Options.EVSEId)
               );

        #endregion

        #region (private static) Name(PayloadType) / Describe(...) / EVSEIdBytes(...)

        /// <summary>
        /// What a V2GTP payload type is called, or its number when it is none
        /// this station knows.
        /// </summary>
        private static String Name(UInt16 PayloadType)

            => Enum.IsDefined(typeof(V2GTP_PayloadType), PayloadType)
                   ? ((V2GTP_PayloadType) PayloadType).ToString()
                   : $"an unknown payload type 0x{PayloadType:X4}";


        private static String Describe(IEnumerable<V2GNetworkInterface> Interfaces)
        {

            var names = Interfaces.Select(networkInterface => $"'{networkInterface.Name}'").ToArray();

            return names.Length > 0
                       ? String.Join(", ", names)
                       : "none";

        }


        private static String Describe(ISlacTransport Transport)

            => Transport switch {
                   UdpSlacTransport udp  => $"a simulated medium at {udp.LocalEndpoint}",
                   AfPacketSlacTransport => "the powerline interface (AF_PACKET, EtherType 0x88E1)",
                   _                     => Transport.GetType().Name
               };


        /// <summary>
        /// An EVSE identification as HomePlug wants it: 17 bytes of ASCII,
        /// padded with NUL, and cut where somebody was too generous.
        /// </summary>
        private static Byte[] EVSEIdBytes(String EVSEId)
        {

            var bytes = new Byte[EVSEIdSize];
            var ascii = Encoding.ASCII.GetBytes(EVSEId);

            Array.Copy(ascii, bytes, Math.Min(ascii.Length, EVSEIdSize));

            return bytes;

        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening on all three, in the reverse order of starting them.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await shutdown.CancelAsync();

            if (slacListener is not null)
                await slacListener.DisposeAsync();

            if (slacTransport is not null)
                await slacTransport.DisposeAsync();

            if (sdpServer is not null)
                await sdpServer.DisposeAsync();

            v2gListener?.Dispose();

            if (acceptLoop is not null)
            {
                try
                {
                    await acceptLoop;
                }
                catch (OperationCanceledException)
                { }
            }

            shutdown.Dispose();

        }

        #endregion

    }

}
