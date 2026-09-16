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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.RFID;
using cloud.charging.open.ChargingStation.ISO15118;
using cloud.charging.open.ChargingStation.Logging;
using cloud.charging.open.ChargingStation.Web;

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
    /// </remarks>
    public partial class ChargingStation : IAsyncDisposable
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
        public static readonly IPPort DefaultHTTPPort = IPPort.Parse(2348);

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
        /// The file of the bundle that is the web interface; its presence is
        /// what says there is one to serve at all.
        /// </summary>
        public const String  IndexFile           = "index.html";

        /// <summary>
        /// The icon of the bundle, which /favicon.ico is pointed at.
        /// </summary>
        public const String  FaviconSVG          = "favicon.svg";

        private readonly  DNSClient                            dnsClient;
        private           NTSClient                            ntsClient;

        /// <summary>
        /// The name servers this station would ask, whether or not name
        /// resolution is switched on at the moment.
        /// </summary>
        /// <remarks>
        /// Kept beside the DNS client because switching name resolution off is
        /// done by taking its servers away - which is what being switched off
        /// actually means, for everything holding that client and not only for
        /// the parts of this station that remember to ask first. Switching it
        /// back on needs the list back, and this is where it waited.
        /// </remarks>
        private           IReadOnlyList<DNSServerConfig>       configuredDNSServers;

        /// <summary>
        /// Serialises changes to what this station is made of, so that two
        /// browsers saving at the same moment do not build half a station each.
        /// </summary>
        private readonly  SemaphoreSlim                        reconfigureLock = new (1, 1);

        private readonly  HTTPServer                           httpServer;


        private readonly  HTTPPath                             httpRootPath;

        /// <summary>
        /// Who is charging where, as far as the display is concerned. See
        /// ChargingStation.Kiosk.cs for what drives this and what does not.
        /// </summary>
        private readonly  ConcurrentDictionary<Byte, ChargingSession>  sessions = [];

        private readonly  HTTPServer?                          kioskServer;



        private readonly  WebPaymentsConfiguration?            webPayments;

        /// <summary>
        /// When this station last managed to check its clock, what it found,
        /// and against whom.
        /// </summary>
        /// <remarks>
        /// Three fields rather than one object because they are written from
        /// one place and read from another, and the alternative - digging them
        /// back out of the JSON of the last check - would make the display
        /// depend on the shape of a diagnostic.
        /// </remarks>
        private           DateTimeOffset?                      lastTimeCheck;
        private           TimeSpan?                            lastTimeCheckOffset;
        private           String?                              lastTimeCheckServer;

        /// <summary>
        /// The clock that makes this station check its own, when NTS is on.
        /// </summary>
        private           ITimer?                              timeCheckTimer;

        /// <summary>
        /// What the file said about the time client, kept because the parts of
        /// it that are not the client itself - how often to check, and what the
        /// operator claims about the server - are read long afterwards.
        /// </summary>
        private           NTSConfiguration?                    ntsSettings;

        private readonly  ConsoleLog?                          consoleLog;
        private readonly  TraceBridge?                         traceBridge;

        private           OCPPv1_6.   TestChargePointNode      cs01;
        private           OCPPv2_1.CS.TestChargingStationNode  cs02;

        private           Boolean                              started;

        #endregion

        #region Properties

        /// <summary>
        /// Everything that happens inside this charging station.
        /// </summary>
        public EventLog               Log                    { get; }

        /// <summary>
        /// Who may open the web interface, and which browsers currently may.
        /// </summary>
        public WebSessions            Sessions               { get; }

        /// <summary>
        /// Where the web login lives between starts.
        /// </summary>
        public WebLoginFile           LoginFile              { get; }

        /// <summary>
        /// Where everything this station can be told in writing lives between
        /// starts: its name resolution, its time source, its EVSEs.
        /// </summary>
        public StationConfigFile      ConfigFile             { get; }

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
        /// How this station resolves names.
        /// </summary>
        public DNSClient              DNSClient
            => dnsClient;

        /// <summary>
        /// Where this station reads the time.
        /// </summary>
        /// <remarks>
        /// Replaced rather than reconfigured when it is pointed at another
        /// server: an NTS client is bound to its host at construction, and the
        /// cookies and keys it holds belong to that host and to no other.
        /// </remarks>
        public NTSClient              NTSClient
            => ntsClient;

        /// <summary>
        /// Whether this station resolves names at all.
        /// </summary>
        /// <remarks>
        /// Switched off by taking the name servers away from the DNS client, so
        /// that it is off for everything that was handed that client - not only
        /// for the parts of this station that would have remembered to check a
        /// flag first. A query then fails at once and says why.
        /// </remarks>
        public Boolean                DNSEnabled             { get; private set; } = true;

        /// <summary>
        /// Whether this station may ask its time server.
        /// </summary>
        public Boolean                NTSEnabled             { get; private set; } = true;

        /// <summary>
        /// The password this station made up because there was no login file,
        /// or null when the login came from the file. It is shown once, on the
        /// console, and kept nowhere but in its hash.
        /// </summary>
        public String?                GeneratedPassword      { get; }

        /// <summary>
        /// Where the web interface comes from: this assembly, or a directory
        /// on disk.
        /// </summary>
        public IStaticContentSource   Frontend               { get; }

        /// <summary>
        /// The JSON API at "/api/".
        /// </summary>
        public CSHTTPAPI              API                    { get; }

        /// <summary>
        /// The web interface at "/", or null when no bundle was found to serve.
        /// </summary>
        public HTTPAPI?               WebInterface           { get; }

        /// <summary>
        /// What is on the wire below the charging cable - SLAC, SDP and the
        /// V2G endpoint - once <see cref="Start"/> has brought it up; null when
        /// this station was not asked for any of it.
        /// </summary>
        public V2GLink?               V2G                    { get; private set; }

        /// <summary>
        /// What was asked for on that wire.
        /// </summary>
        public V2GOptions             V2GOptions             { get; }

        /// <summary>
        /// The URL to open in a browser.
        /// </summary>
        public URL                    WebInterfaceURL        { get; }

        /// <summary>
        /// The port the web interface listens on.
        /// </summary>
        public IPPort                 HTTPPort               { get; }

        /// <summary>
        /// The version of this charging station.
        /// </summary>
        public String                 Version                { get; }

        /// <summary>
        /// Where this charging station reads the time.
        /// </summary>
        /// <remarks>
        /// A charging station is measured by its clock - what a meter reading
        /// is worth, whether a certificate is still valid, what a log line
        /// means - so the clock is something to be handed in rather than
        /// reached for. The system clock by default; an NTS-disciplined or a
        /// fake one where a test or a calibration says so.
        /// </remarks>
        public TimeProvider           TimeProvider           { get; }

        /// <summary>
        /// When this charging station was created, by its own clock.
        /// </summary>
        public DateTimeOffset         CreatedAt              { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create a charging station with a web interface in front of it.
        /// Nothing listens yet: <see cref="Start"/> does.
        /// </summary>
        /// <param name="DNSClient">The DNS client used by everything below.</param>
        /// <param name="NTSClient">The time client.</param>
        /// <param name="HTTPServer">An HTTP server to register within, or null to make one.</param>
        /// <param name="HTTPRootPath">The root path of the JSON API, "/api" by default.</param>
        /// <param name="HTTPHostname">The address to listen on; the loopback address by default.</param>
        /// <param name="HTTPPort">The TCP port to listen on.</param>
        /// <param name="LoginFile">Where the web login lives; "web-login.json" beside the process by default.</param>
        /// <param name="ConfigFile">Where everything this station can be told in writing lives; "configuration.json" beside the process by default.</param>
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
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in the log.</param>
        /// <param name="TimeProvider">Where this station reads the time; the system clock by default.</param>
        public ChargingStation(DNSClient?             DNSClient         = null,
                               NTSClient?             NTSClient         = null,
                               HTTPServer?            HTTPServer        = null,
                               HTTPPath?              HTTPRootPath      = null,
                               IIPAddress?            HTTPHostname      = null,
                               IPPort?                HTTPPort          = null,
                               WebLoginFile?          LoginFile         = null,
                               StationConfigFile?     ConfigFile        = null,
                               IEnumerable<EVSEConfig>?  EVSEs          = null,
                               Decimal?               UplinkPowerLimit_kW  = null,
                               IEnumerable<CalibrationCertificate>?  CalibrationCertificates = null,
                               IPPort?                KioskPort         = null,
                               IIPAddress?            KioskHostname     = null,
                               Boolean                NoKiosk           = false,
                               IStaticContentSource?  Frontend          = null,
                               V2GOptions?            V2G               = null,
                               EventLog?              Log               = null,
                               Boolean                LogToConsole      = true,
                               LogLevel               ConsoleLogLevel   = LogLevel.Info,
                               Boolean                BridgeDebugLog    = true,
                               TimeProvider?          TimeProvider      = null)
        {

            #region The clock, before anything that wants to know the time

            // First of all, and not for tidiness: the event log below stamps
            // every entry with this, so a clock set afterwards would leave the
            // log reading the system one - and a log on a different clock than
            // the station it belongs to cannot be held against anything.
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;
            this.CreatedAt     = this.TimeProvider.GetUtcNow();

            #endregion

            #region The log, next - everything below it may want to say something

            this.Version      = typeof(ChargingStation).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            this.Log          = Log ?? new EventLog(TimeProvider: this.TimeProvider);

            this.consoleLog   = LogToConsole
                                    ? new ConsoleLog(this.Log, ConsoleLogLevel)
                                    : null;

            // Attached before anything else is built, so that what the DNS
            // client, the HTTP server and the OCPP nodes say while they are
            // being made is already in the log a browser will see later.
            this.traceBridge  = BridgeDebugLog
                                    ? TraceBridge.Attach(this.Log)
                                    : null;

            this.V2GOptions   = V2G ?? V2GOptions.Off;

            this.Log.Notice($"Charging station v{this.Version} starting up.", "station");

            #endregion

            #region Who may open the web interface

            this.LoginFile = LoginFile ?? new WebLoginFile(WebLoginFile.DefaultFileName);

            if (this.LoginFile.TryLoad(out var loadedLogin, out var loginError) && loadedLogin is not null)
                this.Sessions = new WebSessions(loadedLogin,   TimeProvider: this.TimeProvider);

            else
            {

                // A login file that is there but unreadable is not something to
                // paper over with a new password: that would lock out whoever
                // owns the old one without saying why.
                if (loginError is not null)
                    throw new InvalidOperationException($"{loginError} Repair or remove '{this.LoginFile.Path}' and start again.");

                // A first start: nobody can sign in to a web interface whose
                // login is not set yet, and an unauthenticated setup page would
                // be a door of its own. So the password is made up here and
                // shown once, on the console, to whoever started the process.
                var (generated, password) = WebLoginSettings.Generate();

                this.LoginFile.Save(generated);

                this.Sessions           = new WebSessions(generated, TimeProvider: this.TimeProvider);
                this.GeneratedPassword  = password;

                this.Log.Notice($"No web login found, so one was made up and written to '{this.LoginFile.Path}'.", "web", "auth");

            }

            #endregion

            #region What the configuration file says

            this.ConfigFile = ConfigFile ?? new StationConfigFile(StationConfigFile.DefaultFileName);

            StationConfiguration? configuration = null;

            if (this.ConfigFile.Exists)
            {

                // A file that is there but cannot be read is not something to
                // paper over with defaults: somebody wrote down what their
                // station is and got it wrong, and quietly running as something
                // else instead would be worse than stopping.
                if (!this.ConfigFile.TryLoad(out configuration, out var configError))
                    throw new InvalidOperationException($"{configError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                this.Log.Info($"Configuration from '{this.ConfigFile.Path}': {configuration}.", "config");

            }

            // The EVSEs used to live in a file of their own. Somebody who has
            // one of those and starts this station would otherwise find it
            // running on one imaginary socket and no explanation anywhere.
            if (configuration?.EVSEs is null)
            {

                var formerEVSEFile = Path.Combine(Path.GetDirectoryName(this.ConfigFile.Path) ?? ".", "evses.json");

                if (File.Exists(formerEVSEFile))
                    this.Log.Warning(
                        $"'{formerEVSEFile}' is no longer read: the EVSEs now live in the \"evses\" section of " +
                        $"'{this.ConfigFile.Path}'. Move them over, or configure them again in the web interface.",
                        "evse", "config"
                    );

            }

            #endregion

            #region The clients everything below shares

            this.dnsClient             = DNSClient ?? new DNSClient();
            this.configuredDNSServers  = [.. dnsClient.DNSServers];

            // The clock goes to the time client too: a station that reads one
            // clock itself and disciplines another would have two, which is
            // one more than a charging station may have.
            this.ntsClient     = NTSClient    ?? new NTSClient(
                                                     DomainName.Parse(NTSConfiguration.DefaultHostname),
                                                     Timeout:         TimeSpan.FromSeconds(10),
                                                     DNSClient:       dnsClient,
                                                     TimeProvider:    this.TimeProvider
                                                 );

            // Last, and that is the whole precedence rule: what this
            // constructor was handed holds until the file says otherwise, and
            // what the file does not mention is left exactly as it was.
            if (configuration?.DNS is not null)
                ApplyDNSConfiguration(configuration.DNS);

            if (configuration?.NTS is not null)
                ApplyNTSConfiguration(configuration.NTS);

            this.ntsSettings = configuration?.NTS;

            #endregion

            #region The EVSEs this station has

            this.EVSEs = configuration?.EVSEs
                             ?? EVSEs?.OrderBy(evse => evse.Id).ToArray()
                             ?? [ EVSEConfig.Default(1) ];

            this.Log.Info(
                $"{this.EVSEs.Count} EVSE(s): {String.Join("; ", this.EVSEs)}.",
                "evse", "config"
            );

            LogCustomConnectorTypes(this.EVSEs);

            #endregion

            #region What this station may draw, and what it is certified for

            this.UplinkPowerLimit_kW = configuration?.Power?.UplinkPowerLimit_kW
                                           ?? UplinkPowerLimit_kW;

            LogPowerLimits();

            this.CalibrationCertificates = configuration?.Calibration
                                               ?? CalibrationCertificates?.ToArray()
                                               ?? [];

            LogCalibrationCertificates(this.CalibrationCertificates);

            this.RFIDReaders = configuration?.RFID ?? [];

            LogRFIDReaders(this.RFIDReaders);

            this.Operator = configuration?.Operator ?? new OperatorConfiguration();

            this.WebPaymentsEnabled = configuration?.WebPayments?.Enabled == true &&
                                      configuration.WebPayments.URLTemplate.HasValue;

            if (configuration?.WebPayments?.Enabled == true && !configuration.WebPayments.URLTemplate.HasValue)
                this.Log.Warning(
                    $"Web payments are switched on in '{this.ConfigFile.Path}' but no 'urlTemplate' is configured; " +
                    "the display will show no QR code.",
                    "kiosk", "config"
                );

            this.webPayments  = configuration?.WebPayments;
            this.Display      = configuration?.Display ?? new DisplayConfiguration();

            // this.Log, not Log: inside this constructor the bare name is the
            // parameter of the same name, which is null unless somebody handed
            // one in - which is why every other line here says this.Log too.
            if (Display.DimsAtNight)
                this.Log.Info($"The display is {Display}.", "kiosk", "config");

            #endregion

            #region The HTTP server, the JSON API and the web interface

            var address        = HTTPHostname ?? IPv4Address.Localhost;
            var port           = HTTPPort     ?? DefaultHTTPPort;

            this.httpServer    = HTTPServer   ?? new HTTPServer(
                                                     IPAddress:       address,
                                                     TCPPort:         port,
                                                     HTTPServerName:  $"OpenChargingCloud ChargingStation v{Version}",
                                                     DNSClient:       dnsClient
                                                 );

            this.httpRootPath  = HTTPRootPath ?? CSHTTPAPI.DefaultAPIPath;

            this.HTTPPort        = port;
            this.WebInterfaceURL = URL.Parse($"http://{address}:{port}/");

            // 1) The JSON API at "/api". Registered first, so that it is the
            //    most specific API and an unknown /api path never reaches the
            //    single-page-application stub below.
            this.API           = new CSHTTPAPI(
                                     HTTPServer:  httpServer,
                                     Station:     this,
                                     Sessions:    Sessions,
                                     Log:         this.Log,
                                     APIPath:     httpRootPath,
                                     Version:     Version
                                 );

            // 2) The web interface at "/": the files of the bundle, and the
            //    single-page-application stub for every other page URL, so
            //    that a reload on /logs and a bookmark to it both work.
            this.Frontend      = Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(ChargingStation).Assembly);

            if (this.Frontend.TryGet(IndexFile, out _))
            {

                this.WebInterface = httpServer.AddHTTPAPI();

                this.WebInterface.MapSinglePageApplication(
                    this.Frontend,
                    new SinglePageAppOptions {
                        IndexTransform = html => html.Replace("{{ServerVersion}}", $"v{Version}", StringComparison.Ordinal)
                    }
                );

                // Browsers ask for /favicon.ico whatever the page says, and a
                // bundle built by webpack carries an SVG. A literal route wins
                // over the catch-all, so this answers before the stub would -
                // and beats a 404 on every visit, which is a line in the log
                // and a broken icon in the tab.
                if (this.Frontend.TryGet(FaviconSVG, out _))
                    this.WebInterface.AddHandler(
                        HTTPPath.Parse("/favicon.ico"),
                        request => Task.FromResult(
                                       new HTTPResponse.Builder(request) {
                                           HTTPStatusCode  = HTTPStatusCode.TemporaryRedirect,
                                           Location        = Location.From(HTTPPath.Parse("/" + FaviconSVG)),
                                           CacheControl    = "public, max-age=3600"
                                       }.AsImmutable
                                   ),
                        HTTPMethod.GET
                    );

            }

            else
                this.Log.Error(
                    $"No web interface to serve ({this.Frontend.Description}): the JSON API answers, the browser gets nothing. " +
                    "Build the frontend (npm run build in Frontend/) or point the station at a directory with --frontend.",
                    "web"
                );

            #region Every request, into the log

            httpServer.OnHTTPRequest  += (server, request, cancellationToken) => {

                // The event stream is one request that stays open for as long
                // as a browser has the page open; logging it would say nothing
                // and logging its response would say it at the wrong moment.
                if (!IsEventStream(request))
                    this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "http");

                return Task.CompletedTask;

            };

            // Only OnHTTPResponse, and not OnHTTPError beside it: Hermod raises
            // both for the same response, and one line per request is what a
            // log is for.
            httpServer.OnHTTPResponse += (server, request, response, cancellationToken) => {

                if (IsEventStream(request))
                    return Task.CompletedTask;

                var code = response.HTTPStatusCode.Code;

                this.Log.Log(
                    code >= 500 ? LogLevel.Error
                        // A 401 is how the web interface asks whether anybody
                        // is signed in, and the answer "nobody" is not a fault.
                        : code == 401 ? LogLevel.Debug
                        : code >= 400 ? LogLevel.Warning
                        : LogLevel.Debug,
                    $"{code} {response.HTTPStatusCode.Name} for {request.HTTPMethod} {request.Path}",
                    "http"
                );

                return Task.CompletedTask;

            };

            #endregion

            #endregion

            #region The display, on a server and a port of its own

            // Its own listener rather than another page, so that the screen in
            // the car park and the administration of this station are two
            // sockets that can be bound to two addresses - see KioskHTTPAPI
            // for the whole argument. Building it here and starting it in
            // Start(), like the other one.
            if (!NoKiosk)
            {

                var kioskAddress  = KioskHostname ?? address;
                var kioskPort     = KioskPort     ?? DefaultKioskPort;

                if (kioskAddress.Equals(address) && kioskPort == port)
                    throw new ArgumentException(
                              $"The display and the web interface would both listen on {address}:{port}. " +
                              "The point of the display being its own server is that it is somewhere else.",
                              nameof(KioskPort)
                          );

                this.kioskServer  = new HTTPServer(
                                        IPAddress:       kioskAddress,
                                        TCPPort:         kioskPort,
                                        HTTPServerName:  $"OpenChargingCloud ChargingStation Display v{Version}",
                                        DNSClient:       dnsClient
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

            // "this." and not for tidiness: the parameters of this constructor
            // shadow the properties of the same name, and the "Log" parameter
            // is null whenever the caller did not bring an event log of its own.
            this.Log.Info($"OCPP 1.6 charge point '{cs01.Id}' and OCPP 2.1 charging station '{cs02.Id}' are set up with {this.EVSEs.Count} EVSE(s).", "ocpp");

            #endregion

        }

        #endregion


        #region Start()

        /// <summary>
        /// Start listening.
        /// </summary>
        public async Task Start()
        {

            if (started)
                return;

            await Listen(httpServer, HTTPPort, StationPort.WebInterface);

            if (kioskServer is not null && KioskPort.HasValue)
            {
                try
                {
                    await Listen(kioskServer, KioskPort.Value, StationPort.Display);
                }
                catch (PortUnavailableException)
                {
                    // The web interface already has its port by now. Nothing
                    // is left behind by a process that is about to end anyway,
                    // but a caller that catches this and carries on - a test,
                    // or a station that tries another port - should not be
                    // holding a socket it never got to use.
                    await httpServer.Stop();
                    throw;
                }
            }

            StartCheckingTheClock();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");

            if (KioskURL.HasValue)
                Log.Notice($"The display is listening on {KioskURL.Value} - no sign-in, and nothing of the administration on it.", "kiosk", "http");
            Log.Info   ($"The JSON API is at {WebInterfaceURL}{httpRootPath.ToString().Trim('/')}/v1/status", "web", "http");

            // After the web interface, so that whoever is watching the Logs
            // page sees SLAC, SDP and the V2G endpoint come up rather than
            // having to reload to find out how it went.
            V2G = await V2GLink.TryStart(V2GOptions, Log);

            //var ws01          = await cs01.ConnectOCPPWebSocketClient(
            //                              RemoteURL:                   URL.Parse("wss://c.electriqua.com/abesp7/test01"),
            //                              RemoteCertificateValidator:  (sender, certificate, chain, client, policyErrors) => {
            //                                                               return TLSValidationResult.Success();
            //                                                           },
            //                              DNSClient:                   dnsClient
            //                          );
            //var ws01response  = ws01.HTTPStatusCode;

            //await cs02.Start();

        }

        #endregion

        #region (private) Listen(Server, Port, Whose)

        /// <summary>
        /// Take a port, and say which one it was where it cannot be had.
        /// </summary>
        /// <remarks>
        /// The socket layer throws the same exception for both of this
        /// station's servers, and its own words for it name neither the port
        /// nor what the port was for. Both are known here.
        /// </remarks>
        private static async Task Listen(HTTPServer   Server,
                                         IPPort       Port,
                                         StationPort  Whose)
        {

            try
            {
                await Server.Start();
            }
            catch (SocketException problem)
            {
                throw new PortUnavailableException(Port, Whose, problem);
            }

        }

        #endregion

        #region Stop()

        /// <summary>
        /// Stop listening.
        /// </summary>
        public async Task Stop()
        {

            if (!started)
                return;

            Log.Notice("The charging station is shutting down.", "station");

            if (V2G is not null)
            {
                await V2G.DisposeAsync();
                V2G = null;
            }

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            // Before the servers, and that order is the whole point: every
            // browser with the Logs page open holds a request that is waiting
            // for the next log entry rather than for its socket, and the HTTP
            // server waits for every request it started. Closing the sockets
            // does not wake those, so they are ended here first.
            API.CloseEventStreams();

            if (kioskServer is not null)
                await kioskServer.Stop();

            await httpServer.Stop();

            started = false;

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this charging station is made of, as the Configuration page of
        /// the web interface reads it.
        /// </summary>
        /// <remarks>
        /// Read-only for now: it answers "what am I running", not "change it".
        /// Nothing here is a secret - the web login appears with its username
        /// and the path of its file, and never with anything about its password.
        /// </remarks>
        public JObject ConfigurationJSON()

            => new (

                   new JProperty("station",    new JObject(
                       new JProperty("version",        Version),
                       new JProperty("createdAt",      CreatedAt.ToString("o")),
                       new JProperty("machine",        Environment.MachineName),
                       new JProperty("runtime",        Environment.Version.ToString()),
                       new JProperty("os",             Environment.OSVersion.ToString())
                   )),

                   new JProperty("http",       new JObject(
                       new JProperty("serverName",     httpServer.HTTPServerName),
                       new JProperty("url",            WebInterfaceURL.ToString()),
                       new JProperty("apiPath",        httpRootPath.ToString()),
                       new JProperty("running",        started),
                       new JProperty("frontend",       Frontend.Description),
                       new JProperty("webInterface",   WebInterface is not null)
                   )),

                   new JProperty("web",        new JObject(
                       new JProperty("username",       Sessions.Username),
                       new JProperty("loginFile",      LoginFile.Path),
                       new JProperty("cookie",         Sessions.CookieName.ToString()),
                       new JProperty("secureCookies",  Sessions.SecureCookies),
                       new JProperty("idleTimeout",    Sessions.IdleTimeout.    ToString()),
                       new JProperty("maxLifetime",    Sessions.MaximumLifetime.ToString()),
                       new JProperty("sessions",       Sessions.Count)
                   )),

                   new JProperty("log",        new JObject(
                       new JProperty("capacity",       Log.Capacity),
                       new JProperty("entries",        Log.Count),
                       new JProperty("lastId",         Log.LastId),
                       new JProperty("debugBridge",    traceBridge is not null),
                       new JProperty("console",        consoleLog is not null),
                       new JProperty("tags",           new JArray(Log.KnownTags))
                   )),

                   new JProperty("v2g",        V2G?.ToJSON() ?? new JObject(
                       new JProperty("enabled",        false)
                   )),

                   new JProperty("time",       new JObject(
                       new JProperty("nts",            ntsClient.Hostname.ToString()),
                       new JProperty("now",            TimeProvider.GetUtcNow().ToString("o"))
                   )),

                   new JProperty("ocpp",       new JArray(

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

                   )),

                   new JProperty("assemblies", new JArray(
                       AssemblyJSON<HTTPServer>                            ("Hermod"),
                       AssemblyJSON<NTSClient>                             ("Norn"),
                       AssemblyJSON<OCPPv1_6.   TestChargePointNode>       ("OCPP 1.6"),
                       AssemblyJSON<OCPPv2_1.CS.TestChargingStationNode>   ("OCPP 2.1")
                   ))

               );

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

        #region (private static) IsEventStream(Request)

        /// <summary>
        /// Whether this request is a browser hanging on the event stream.
        /// </summary>
        private static Boolean IsEventStream(HTTPRequest Request)
            => Request.Path.ToString().EndsWith("/events", StringComparison.Ordinal);

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening and let go of the console and the debug bridge.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await Stop();

            traceBridge?.Dispose();
            consoleLog? .Dispose();

            reconfigureLock.Dispose();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
