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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

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
    public class ChargingStation : IAsyncDisposable
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
        /// The file of the bundle that is the web interface; its presence is
        /// what says there is one to serve at all.
        /// </summary>
        public const String  IndexFile           = "index.html";

        /// <summary>
        /// The icon of the bundle, which /favicon.ico is pointed at.
        /// </summary>
        public const String  FaviconSVG          = "favicon.svg";

        private readonly  DNSClient                            dnsClient;
        private readonly  NTSClient                            ntsClient;

        private readonly  HTTPServer                           httpServer;
        private readonly  HTTPPath                             httpRootPath;

        private readonly  ConsoleLog?                          consoleLog;
        private readonly  TraceBridge?                         traceBridge;

        private readonly  OCPPv1_6.   TestChargePointNode      cs01;
        private readonly  OCPPv2_1.CS.TestChargingStationNode  cs02;

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

            #region The clients everything below shares

            this.dnsClient     = DNSClient    ?? new DNSClient();

            // The clock goes to the time client too: a station that reads one
            // clock itself and disciplines another would have two, which is
            // one more than a charging station may have.
            this.ntsClient     = NTSClient    ?? new NTSClient(
                                                     DomainName.Parse("ptbtime1.ptb.de"),
                                                     Timeout:         TimeSpan.FromSeconds(10),
                                                     DNSClient:       dnsClient,
                                                     TimeProvider:    this.TimeProvider
                                                 );

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

            #region The OCPP nodes

            cs01 = new OCPPv1_6.TestChargePointNode(
                       ChargeBoxId:               NetworkingNode_Id.Parse("test01"),
                       Connectors:                [
                                                      new OCPPv1_6.CP.ConnectorSpec(
                                                          Availability:        OCPPv1_6.Availabilities.Operative,
                                                          PhysicalReference:   "A",
                                                          MaxPower:            Watt.    FromKW  (22),
                                                          MaxEnergy:           WattHour.FromKWh(100),
                                                          EnergyMeter:         null
                                                      )
                                                  ],
                       Description:               null,
                       ChargePointVendor:         null,
                       ChargePointModel:          null,
                       ChargePointSerialNumber:   null,
                       ChargeBoxSerialNumber:     null,
                       FirmwareVersion:           null,
                       Iccid:                     null,
                       IMSI:                      null,
                       UplinkEnergyMeter:         null
                   );

            cs02 = new OCPPv2_1.CS.TestChargingStationNode(
                       Id:                             NetworkingNode_Id.Parse("test02"),
                       VendorName:                     "gef",
                       Model:                          "cs1",
                       Description:                    I18NString.Empty,
                       SerialNumber:                   null,
                       FirmwareVersion:                null,
                       Modem:                          null,

                       EVSEs:                          [
                                                           new OCPPv2_1.CS.EVSESpec(
                                                               AdminStatus:         OCPPv2_1.OperationalStatus.Operative,
                                                               ConnectorTypes:      [ OCPPv2_1.ConnectorType.sType2 ],
                                                               MeterType:           "",
                                                               MeterSerialNumber:   "",
                                                               MeterPublicKey:      ""
                                                           )
                                                       ],
                       UplinkEnergyMeter:              null,

                       DefaultRequestTimeout:          null,

                       SignaturePolicy:                null,
                       ForwardingSignaturePolicy:      null,

                       HTTPAPI_Disabled:               true,
                       HTTPAPI_Port:                   null,
                       HTTPAPI_ServerName:             null,
                       HTTPAPI_ServiceName:            null,
                       HTTPAPI_RobotEMailAddress:      null,
                       HTTPAPI_RobotGPGPassphrase:     null,
                       HTTPAPI_EventLoggingDisabled:   true,

                       WebAPI:                         null,
                       WebAPI_Disabled:                true,
                       WebAPI_Path:                    null,

                       ControlWebSocketServer:         null,

                       DisableSendHeartbeats:          true,
                       SendHeartbeatsEvery:            null,

                       DisableMaintenanceTasks:        true,
                       MaintenanceEvery:               null,

                       CustomData:                     null,
                       DNSClient:                      dnsClient
                   );

            // "this." and not for tidiness: the parameters of this constructor
            // shadow the properties of the same name, and the "Log" parameter
            // is null whenever the caller did not bring an event log of its own.
            this.Log.Info($"OCPP 1.6 charge point '{cs01.Id}' and OCPP 2.1 charging station '{cs02.Id}' are set up.", "ocpp");

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

            await httpServer.Start();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");
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

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
