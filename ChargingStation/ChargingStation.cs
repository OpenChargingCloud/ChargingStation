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
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.RFID;
using cloud.charging.open.ChargingStation.ISO15118;
using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.ChargingStation.Web;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// One charging station: the OCPP nodes it speaks through, the HTTP server
    /// in front of them, the JSON API at "/api" and the web interface at "/".
    /// </summary>
    /// <remarks>
    /// The web interface is a bundle of HTML, CSS and JavaScript built by
    /// webpack from Frontend/ and embedded into this assembly, so that the
    /// station is one file to deploy and needs nothing installed beside it. The
    /// browser and the station talk over the JSON API and one Server-Sent
    /// Events stream; nothing is rendered on the server.
    ///
    /// What every one of these programs is before it is anything in
    /// particular - the log, the configuration file, name resolution and the
    /// time, the certificate store, the accounts, and the HTTP server with the
    /// web interface behind it - is the <see cref="WWCPNode"/> below. A
    /// charging station is one of those with sections of its own in the same
    /// file, its own JSON API below "/api", a display on a port of its own,
    /// and the OCPP nodes it dials its back ends with.
    /// </remarks>
    public partial class ChargingStation : WWCPNode
    {

        #region Data

        /// <summary>
        /// The manifest resource prefix of the embedded frontend bundle
        /// (see the EmbedFrontend target of ChargingStation.csproj).
        /// </summary>
        public const String  HTTPRoot            = "cloud.charging.open.ChargingStation.HTTPRoot.";

        /// <summary>
        /// The TCP port the web interface listens on, unless another is given.
        /// </summary>
        /// <remarks>
        /// The node below has a port of its own for a node of no particular
        /// kind, and this one is handed to it rather than left to it.
        /// </remarks>
        public static new readonly IPPort DefaultHTTPPort = IPPort.Parse(2348);

        /// <summary>
        /// The TCP port the display listens on, when nobody says otherwise.
        /// </summary>
        /// <remarks>
        /// Next to the web interface so that the pair is easy to remember, and
        /// a different socket so that the two can be bound to different
        /// addresses and firewalled apart - which is the whole reason the
        /// display is a second server. See
        /// <see cref="KioskHTTPAPI"/>.
        /// </remarks>
        public static readonly IPPort DefaultKioskPort = IPPort.Parse(2349);

        /// <summary>
        /// What the display's port is for, as a port that cannot be had names
        /// it: the other of the two things this station listens for, beside
        /// <see cref="NodePort.WebInterface"/>.
        /// </summary>
        public static readonly NodePort DisplayPort = new ("The display");

        /// <summary>
        /// The organization the accounts of this station are in.
        /// </summary>
        /// <remarks>
        /// A charging station has no organizations to speak of, and this one
        /// exists because the HTTPExt API's sign-in refuses an account that is
        /// in none - "You do not have access to any organization!" - however
        /// right its password is. So there is exactly one, named after the
        /// thing it stands for.
        /// </remarks>
        public const String  DefaultOrganization          = "ChargingStation";

        /// <summary>
        /// Who is charging where, as far as the display is concerned. See
        /// ChargingStation.Kiosk.cs for what drives this and what does not.
        /// </summary>
        private readonly  ConcurrentDictionary<Byte, ChargingSession>  sessions = [];

        private readonly  HTTPServer?                          kioskServer;

        private readonly  WebPaymentsConfiguration?            webPayments;

        private           OCPPv1_6.   TestChargePointNode      cs01;
        private           OCPPv2_1.CS.TestChargingStationNode  cs02;

        #endregion

        #region Properties

        /// <summary>
        /// The keys and certificates this station holds up when it dials a
        /// back end.
        /// </summary>
        /// <remarks>
        /// Beside the configuration file rather than beside the process,
        /// because that is where everything else this station was given lives -
        /// and because a station that is moved by copying its directory should
        /// take its identity with it or not at all, never half of it.
        /// </remarks>
        public ClientCertificateStore ClientCertificates     { get; }

        /// <summary>
        /// The places this station dials, and the credentials it proves itself
        /// with when it gets there.
        /// </summary>
        /// <remarks>
        /// Beside the certificates and for the same reason, and with the same
        /// care over what is secret: the passwords and shared secrets are
        /// written where only their owner may read them, and nothing hands
        /// them back out.
        /// </remarks>
        public ConnectionStore        Connections           { get; }

        /// <summary>
        /// The EVSEs this station has, right now.
        /// </summary>
        /// <remarks>
        /// Changing this list rebuilds the OCPP nodes, so what it says and what
        /// a back end is told about this station are never two different things.
        /// </remarks>
        public IReadOnlyList<EVSEConfig>  EVSEs              { get; private set; }

        /// <summary>
        /// The most this station may draw from the grid in total, or null when
        /// nobody has said.
        /// </summary>
        /// <remarks>
        /// A property of the building rather than of the station: it is what
        /// the connection behind the meter allows, and it stays put while EVSEs
        /// are added and taken away in front of it. Nothing here enforces it
        /// yet - there is no load management in this station to enforce it
        /// with - so it is a number this station knows and reports, and the day
        /// smart charging arrives it is the number it starts from.
        /// </remarks>
        public Decimal?               UplinkPowerLimit_kW    { get; private set; }

        /// <summary>
        /// The quiet hours the screen on the front of this station keeps.
        /// </summary>
        /// <remarks>
        /// Never null, so that nothing has to ask twice whether this station has
        /// a display section before asking what it says: a station nobody has
        /// told simply has no quiet hours, which is the honest default - a
        /// screen that went dark on its own would be read as a fault.
        /// </remarks>
        public DisplayConfiguration   Display                { get; private set; }

        /// <summary>
        /// The calibration certificates this station runs under.
        /// </summary>
        public IReadOnlyList<CalibrationCertificate>  CalibrationCertificates  { get; private set; }

        /// <summary>
        /// The RFID readers this station has, and where they sit.
        /// </summary>
        public IReadOnlyList<RFIDReaderConfig>  RFIDReaders  { get; private set; }

        /// <summary>
        /// Whose charging station this is, and whose cards it recognises.
        /// </summary>
        public OperatorConfiguration          Operator               { get; private set; }

        /// <summary>
        /// Whether a QR code to pay by is shown on the display.
        /// </summary>
        /// <remarks>
        /// Read from the file at the start and not changeable while running -
        /// see <see cref="WebPaymentsConfiguration"/>, which explains why the
        /// one setting in this station that carries a secret is the one setting
        /// that never travels over HTTP.
        /// </remarks>
        public Boolean                        WebPaymentsEnabled     { get; }

        /// <summary>
        /// Where the display of this station is, or null when it has none.
        /// </summary>
        public URL?                           KioskURL               { get; }

        /// <summary>
        /// The port the display listens on, or null where there is no display.
        /// </summary>
        public IPPort?                        KioskPort              { get; }

        /// <summary>
        /// The display API, on its own server and its own port.
        /// </summary>
        public KioskHTTPAPI?                  KioskAPI               { get; }

        /// <summary>
        /// The JSON API at "/api/".
        /// </summary>
        public CSHTTPAPI              API                    { get; }

        /// <summary>
        /// What is on the wire below the charging cable - SLAC, SDP and the
        /// V2G endpoint - once <see cref="WWCPNode.Start"/> has brought it up;
        /// null when this station was not asked for any of it.
        /// </summary>
        public V2GLink?               V2G                    { get; private set; }

        /// <summary>
        /// What was asked for on that wire.
        /// </summary>
        /// <remarks>
        /// Settable because the V2G configuration page changes it while the
        /// station runs; every change goes through
        /// <see cref="UpdateV2GConfiguration"/>, which holds the same lock as
        /// the other sections and restarts the link.
        /// </remarks>
        public V2GOptions             V2GOptions             { get; private set; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a charging station with a web interface in front of it.
        /// Nothing listens yet: <see cref="WWCPNode.Start"/> does.
        /// </summary>
        /// <param name="DNSClient">The DNS client used by everything below.</param>
        /// <param name="NTSClient">The time client.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="BasePath">What everything of this station sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">The root path of the JSON API, "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this station's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="HTTPHostname">The address to listen on; the loopback address by default.</param>
        /// <param name="HTTPPort">The TCP port to listen on; <see cref="DefaultHTTPPort"/> by default.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
        /// <param name="ConfigFile">Where everything this station can be told in writing lives: one file, whose sections the node below and the station each read for themselves; "configuration.json" beside the process by default.</param>
        /// <param name="EVSEs">What this station is made of, unless the configuration file says otherwise; one 22 kW type 2 socket by default.</param>
        /// <param name="UplinkPowerLimit_kW">The most this station may draw from the grid, unless the configuration file says otherwise; unknown by default.</param>
        /// <param name="CalibrationCertificates">The calibration certificates it runs under, unless the configuration file says otherwise; none by default.</param>
        /// <param name="KioskPort">The TCP port the display listens on; DefaultKioskPort by default. Its own server on its own port - see KioskHTTPAPI.</param>
        /// <param name="KioskHostname">The address the display listens on; the same as the web interface by default.</param>
        /// <param name="NoKiosk">Whether to leave the display out entirely, so that the station listens on one port.</param>
        /// <param name="Frontend">Where the web interface comes from; the bundle embedded in this assembly by default.</param>
        /// <param name="V2G">What to offer a vehicle on the wire below the charging cable; nothing by default.</param>
        /// <param name="Log">The event log; a new one by default.</param>
        /// <param name="LogToConsole">Whether the event log is also written to the console.</param>
        /// <param name="ConsoleLogLevel">What the console shows of it.</param>
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in the log.</param>
        /// <param name="TimeProvider">Where this station reads the time; the system clock by default.</param>
        public ChargingStation(DNSClient?                            DNSClient                 = null,
                               NTSClient?                            NTSClient                 = null,
                               HTTPServer?                           HTTPServer                = null,
                               HTTPPath?                             BasePath                  = null,
                               HTTPPath?                             HTTPRootPath              = null,
                               HTTPExtAPI?                           ExtAPI                    = null,
                               IIPAddress?                           HTTPHostname              = null,
                               IPPort?                               HTTPPort                  = null,
                               String?                               AccountsPath              = null,
                               WWCPConfigFile?                       ConfigFile                = null,
                               IEnumerable<EVSEConfig>?              EVSEs                     = null,
                               Decimal?                              UplinkPowerLimit_kW       = null,
                               IEnumerable<CalibrationCertificate>?  CalibrationCertificates   = null,
                               IPPort?                               KioskPort                 = null,
                               IIPAddress?                           KioskHostname             = null,
                               Boolean                               NoKiosk                   = false,
                               IStaticContentSource?                 Frontend                  = null,
                               V2GOptions?                           V2G                       = null,
                               EventLog?                             Log                       = null,
                               Boolean                               LogToConsole              = true,
                               LogLevel                              ConsoleLogLevel           = LogLevel.Info,
                               String?                               LogPath                   = null,
                               Boolean                               BridgeDebugLog            = true,
                               TimeProvider?                         TimeProvider              = null)

            // Every name as it was before there was a node below: the entries
            // about the station itself are tagged "station", the Server header
            // says "OpenChargingCloud ChargingStation", and a day's log file is
            // "station-2026-09-25.log" - so that a log directory kept since then
            // goes on under the same names, and nothing reading one has to
            // learn a second. The organization is written into the accounts at
            // the first start and read back at every start after it, and must
            // never change at all.
            : base(Kind:              new NodeKind(
                                          Name:           "charging station",
                                          Tag:            "station",
                                          Product:        "ChargingStation",
                                          Organization:   DefaultOrganization,
                                          LogFilePrefix:  "station"
                                      ),
                   Version:           typeof(ChargingStation).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                   HTTPPort:          HTTPPort ?? DefaultHTTPPort,
                   HTTPHostname:      HTTPHostname,
                   HTTPServer:        HTTPServer,
                   BasePath:          BasePath,
                   HTTPRootPath:      HTTPRootPath,
                   ExtAPI:            ExtAPI,
                   AccountsPath:      AccountsPath,
                   Roles:             UserRole.All.Select(role => role.Name),
                   ConfigFile:        ConfigFile,
                   DNSClient:         DNSClient,
                   NTSClient:         NTSClient,
                   Frontend:          Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(ChargingStation).Assembly),

                   // None in the node's store. The keys this station dials its
                   // back ends with are in a store of its own, and what it offers
                   // a vehicle comes with its V2G options - so a store of a
                   // vehicle's seven kinds beside its configuration file would
                   // be seven empty directories promising something nothing
                   // here reads.
                   CertificateKinds:  [],
                   Log:               Log,
                   LogToConsole:      LogToConsole,
                   ConsoleLogLevel:   ConsoleLogLevel,
                   LogPath:           LogPath,
                   BridgeDebugLog:    BridgeDebugLog,
                   TimeProvider:      TimeProvider)

        {

            // "this." throughout, and not for tidiness: the parameters of this
            // constructor shadow the properties of the same name, and a
            // parameter such as "Log" or "ConfigFile" is null whenever the
            // caller did not bring one of its own - the same trap that once
            // stopped this station from starting at all.

            this.V2GOptions  = V2G ?? V2GOptions.Off;

            #region The stores beside the configuration file

            this.ClientCertificates = new ClientCertificateStore(
                                          System.IO.Path.Combine(
                                              System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(this.ConfigFile.Path)) ?? ".",
                                              ClientCertificateStore.DefaultDirectoryName
                                          ),
                                          this.TimeProvider
                                      );

            this.ClientCertificates.OnNotice += (level, message) => this.Log.Log(level, message, "ocpp", "certificates");

            this.Connections = new ConnectionStore(
                                   System.IO.Path.Combine(
                                       System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(this.ConfigFile.Path)) ?? ".",
                                       ConnectionStore.DefaultDirectoryName
                                   ),
                                   this.TimeProvider
                               );

            // Which certificates exist is the certificate store's business and
            // changes while this one is alive, so it is asked rather than told
            // once.
            this.Connections.KnownCertificateIds  = () => this.ClientCertificates.Entries.Select(entry => entry.Id);
            this.Connections.OnNotice            += (level, message) => this.Log.Log(level, message, "ocpp", "connections");

            #endregion

            #region What the configuration file says about a charging station

            // Its own sections of the document the node below has already
            // read: the ones that reading passed over are the ones this is
            // for. A file that is there but cannot be read has stopped the
            // node before this line; a section of it that is wrong stops the
            // station here, for the same reason - somebody wrote down what
            // their station is and got it wrong, and quietly running as
            // something else instead would be worse than stopping.
            if (!StationConfiguration.TryParse(ConfigurationDocument, out var configuration, out var problem))
                throw new InvalidOperationException($"'{this.ConfigFile.Path}': {problem} Repair or remove '{this.ConfigFile.Path}' and start again.");

            if (!configuration.IsEmpty)
                this.Log.Info($"Station configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            // The EVSEs used to live in a file of their own. Somebody who has
            // one of those and starts this station would otherwise find it
            // running on one imaginary socket and no explanation anywhere.
            if (configuration.EVSEs is null)
            {

                var formerEVSEFile = Path.Combine(Path.GetDirectoryName(this.ConfigFile.Path) ?? ".", "evses.json");

                if (File.Exists(formerEVSEFile))
                    this.Log.Warning(
                        $"'{formerEVSEFile}' is no longer read: the EVSEs now live in the \"evses\" section of " +
                        $"'{this.ConfigFile.Path}'. Move them over, or configure them again in the web interface.",
                        "evse", "config"
                    );

            }

            // Same rule for the wire below the cable as for everything else in
            // the file: the command line asked for something, and the file has
            // the last word on it. Nothing is started here - Start() does that,
            // much later - so this only decides what will be started.
            if (configuration.V2G is not null)
                this.V2GOptions = configuration.V2G.Apply(this.V2GOptions);

            #endregion

            #region The EVSEs this station has

            this.EVSEs = configuration.EVSEs
                             ?? EVSEs?.OrderBy(evse => evse.Id).ToArray()
                             ?? [ EVSEConfig.Default(1) ];

            this.Log.Info(
                $"{this.EVSEs.Count} EVSE(s): {String.Join("; ", this.EVSEs)}.",
                "evse", "config"
            );

            LogCustomConnectorTypes(this.EVSEs);

            #endregion

            #region What this station may draw, and what it is certified for

            this.UplinkPowerLimit_kW = configuration.Power?.UplinkPowerLimit_kW
                                           ?? UplinkPowerLimit_kW;

            LogPowerLimits();

            this.CalibrationCertificates = configuration.Calibration
                                               ?? CalibrationCertificates?.ToArray()
                                               ?? [];

            LogCalibrationCertificates(this.CalibrationCertificates);

            this.RFIDReaders = configuration.RFID ?? [];

            LogRFIDReaders(this.RFIDReaders);

            this.Operator = configuration.Operator ?? new OperatorConfiguration();

            this.WebPaymentsEnabled = configuration.WebPayments?.Enabled == true &&
                                      configuration.WebPayments.URLTemplate.HasValue;

            if (configuration.WebPayments?.Enabled == true && !configuration.WebPayments.URLTemplate.HasValue)
                this.Log.Warning(
                    $"Web payments are switched on in '{this.ConfigFile.Path}' but no 'urlTemplate' is configured; " +
                    "the display will show no QR code.",
                    "kiosk", "config"
                );

            this.webPayments  = configuration.WebPayments;
            this.Display      = configuration.Display ?? new DisplayConfiguration();

            if (Display.DimsAtNight)
                this.Log.Info($"The display is {Display}.", "kiosk", "config");

            #endregion

            #region The JSON API

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this station are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This station signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // The JSON API at "/api", beside the web interface the node below
            // has already put at "/". The more specific of the two, so that an
            // unknown /api path never reaches the single-page-application
            // stub.
            this.API           = new CSHTTPAPI(
                                     HTTPServer:  this.HTTPServer,
                                     Station:     this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     this.HTTPRootPath,
                                     Version:     Version
                                 );

            #endregion

            #region The display, on a server and a port of its own

            // Its own listener rather than another page, so that the screen in
            // the car park and the administration of this station are two
            // sockets that can be bound to two addresses - see KioskHTTPAPI
            // for the whole argument. Building it here and starting it in
            // OnListening, once the node below has its own port.
            if (!NoKiosk)
            {

                var address       = HTTPHostname  ?? IPv4Address.Localhost;
                var kioskAddress  = KioskHostname ?? address;
                var kioskPort     = KioskPort     ?? DefaultKioskPort;

                if (kioskAddress.Equals(address) && kioskPort == this.HTTPPort)
                    throw new ArgumentException(
                              $"The display and the web interface would both listen on {address}:{this.HTTPPort}. " +
                              "The point of the display being its own server is that it is somewhere else.",
                              nameof(KioskPort)
                          );

                this.kioskServer  = new HTTPServer(
                                        IPAddress:       kioskAddress,
                                        TCPPort:         kioskPort,
                                        HTTPServerName:  $"OpenChargingCloud ChargingStation Display v{Version}",
                                        DNSClient:       this.DNSClient
                                    );

                this.KioskPort    = kioskPort;
                this.KioskURL     = URL.Parse($"http://{kioskAddress}:{kioskPort}/");

                this.KioskAPI     = new KioskHTTPAPI(
                                        HTTPServer:  kioskServer,
                                        Station:     this,
                                        Log:         this.Log
                                    );

                if (this.Frontend.TryGet(KioskHTTPAPI.IndexFile, out _))
                    kioskServer.AddHTTPAPI().
                                MapSinglePageApplication(
                                    this.Frontend,
                                    new SinglePageAppOptions {
                                        // The same bundle as the web interface,
                                        // entered at its other door. The assets
                                        // are shared; the page is not.
                                        IndexFile       = KioskHTTPAPI.IndexFile,
                                        IndexTransform  = html => html.Replace("{{ServerVersion}}", $"v{Version}", StringComparison.Ordinal)
                                    }
                                );

                else
                    this.Log.Error(
                        $"No display page to serve ({this.Frontend.Description} has no '{KioskHTTPAPI.IndexFile}'): " +
                        "the display API answers, the screen gets nothing.",
                        "kiosk"
                    );

                kioskServer.OnHTTPRequest += (server, request, cancellationToken) => {
                    this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "kiosk", "http");
                    return Task.CompletedTask;
                };

            }

            #endregion

            #region The OCPP nodes

            // Built from the EVSEs above, and rebuilt whenever those change -
            // see BuildOCPPNodes, which is the one place that knows how an EVSE
            // of this station is spelled in each of the two protocols.
            (cs01, cs02) = BuildOCPPNodes(this.EVSEs);

            this.Log.Info($"OCPP 1.6 charge point '{cs01.Id}' and OCPP 2.1 charging station '{cs02.Id}' are set up with {this.EVSEs.Count} EVSE(s).", "ocpp");

            #endregion

        }

        #endregion


        #region (protected override) OnListening()

        /// <summary>
        /// The display's port, once the web interface has its own.
        /// </summary>
        /// <remarks>
        /// Before this station calls itself started, so that a display that
        /// cannot have its port ends the start rather than leaving a station
        /// that says it is listening and has no screen. The socket layer throws
        /// the same exception for both servers, and its own words for it name
        /// neither the port nor what the port was for; both are known here. The
        /// node below lets go of the web interface's port again on the way out.
        /// </remarks>
        protected override async Task OnListening()
        {

            if (kioskServer is null || !KioskPort.HasValue)
                return;

            try
            {
                await kioskServer.Start();
            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(KioskPort.Value, problem, DisplayPort);
            }

        }

        #endregion

        #region (protected override) OnStarted()

        /// <summary>
        /// What a station says and does once it is up: where its display and
        /// its API are, then the wire below the cable, then the back ends.
        /// </summary>
        protected override async Task OnStarted()
        {

            if (KioskURL.HasValue)
                Log.Notice($"The display is listening on {KioskURL.Value} - no sign-in, and nothing of the administration on it.", "kiosk", "http");

            Log.Info($"The JSON API is at {APIURL}v1/status", "web", "http");

            // After the web interface, so that whoever is watching the Logs
            // page sees SLAC, SDP and the V2G endpoint come up rather than
            // having to reload to find out how it went.
            V2G = await V2GLink.TryStart(V2GOptions, Log, TimeProvider);

            // After the web interface for the same reason as the V2G link
            // above: somebody watching the Logs page sees each back end come up
            // or fail while it happens, rather than reloading afterwards to
            // find out how it went. Nothing in here throws - see
            // DialConfiguredConnections.
            await DialConfiguredConnections();

        }

        #endregion

        #region (protected override) OnStopping()

        /// <summary>
        /// End what this station holds open beyond the web interface, before
        /// the server stops.
        /// </summary>
        protected override async Task OnStopping()
        {

            if (V2G is not null)
            {
                await V2G.DisposeAsync();
                V2G = null;
            }

            // Before the servers, so that a close this station asked for is
            // recognised as one and does not start a reconnect on the way out.
            await HangUp();

            // Before the servers, and that order is the whole point: every
            // browser with the Logs page open holds a request that is waiting
            // for the next log entry rather than for its socket, and the HTTP
            // server waits for every request it started. Closing the sockets
            // does not wake those, so they are ended here first - whoever owns
            // the server, because the streams are this station's.
            API.CloseEventStreams();

            if (kioskServer is not null)
                await kioskServer.Stop();

        }

        #endregion


        #region ConfigurationJSON()

        /// <summary>
        /// What this charging station is made of, as the Configuration page of
        /// the web interface reads it: what the node below says of itself, and
        /// on top the station, its link below the cable, its OCPP nodes and the
        /// assemblies it was built from.
        /// </summary>
        public override JObject ConfigurationJSON()
        {

            var json = base.ConfigurationJSON();

            // First, because it is the card the page leads with.
            json.AddFirst(new JProperty("station",    new JObject(
                              new JProperty("version",        Version),
                              new JProperty("createdAt",      CreatedAt.ToString("o")),
                              new JProperty("machine",        Environment.MachineName),
                              new JProperty("runtime",        Environment.Version.ToString()),
                              new JProperty("os",             Environment.OSVersion.ToString())
                          )));

            json.Property("log")!.AddAfterSelf(new JProperty("v2g",        V2G?.ToJSON() ?? new JObject(
                                                   new JProperty("enabled",        false)
                                               )));

            json.Add(new JProperty("ocpp",       new JArray(

                         new JObject(
                             new JProperty("version",      "1.6"),
                             new JProperty("role",         "Charge Point"),
                             new JProperty("id",           cs01.Id.ToString()),
                             new JProperty("vendor",       cs01.ChargePointVendor),
                             new JProperty("model",        cs01.ChargePointModel),
                             new JProperty("connectors",   new JArray(
                                 cs01.Connectors.Select(connector => new JObject(
                                     new JProperty("id",            connector.Id.ToString()),
                                     new JProperty("availability",  connector.Availability.ToString()),
                                     new JProperty("maxPower",      $"{connector.MaxPower.kW} kW")
                                 ))
                             ))
                         ),

                         new JObject(
                             new JProperty("version",      "2.1"),
                             new JProperty("role",         "Charging Station"),
                             new JProperty("id",           cs02.Id.ToString()),
                             new JProperty("vendor",       cs02.VendorName),
                             new JProperty("model",        cs02.Model),
                             new JProperty("evses",        new JArray(
                                 cs02.EVSEs.Select(evse => new JObject(
                                     new JProperty("id",            evse.Id.ToString()),
                                     new JProperty("adminStatus",   evse.AdminStatus.ToString()),
                                     new JProperty("status",        evse.Status.    ToString())
                                 ))
                             ))
                         )

                     )));

            json.Add(new JProperty("assemblies", new JArray(
                         AssemblyJSON<HTTPServer>                            ("Hermod"),
                         AssemblyJSON<NTSClient>                             ("Norn"),
                         AssemblyJSON<WWCPNode>                              ("WWCP Node"),
                         AssemblyJSON<OCPPv1_6.   TestChargePointNode>       ("OCPP 1.6"),
                         AssemblyJSON<OCPPv2_1.CS.TestChargingStationNode>   ("OCPP 2.1")
                     )));

            return json;

        }

        #endregion


        #region (private static) AssemblyJSON<T>(Name)

        private static JObject AssemblyJSON<T>(String Name)
        {

            var assembly = typeof(T).Assembly.GetName();

            return new JObject(
                       new JProperty("name",      Name),
                       new JProperty("assembly",  assembly.Name),
                       new JProperty("version",   assembly.Version?.ToString(3))
                   );

        }

        #endregion

    }

}
