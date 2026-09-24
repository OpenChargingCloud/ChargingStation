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
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using NullMailer = org.GraphDefined.Vanaheimr.Hermod.SMTP.NullMailer;
using org.GraphDefined.Vanaheimr.Norn.Monitoring;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.RFID;
using cloud.charging.open.ChargingStation.ISO15118;
using cloud.charging.open.ChargingStation.Logging;
using cloud.charging.open.ChargingStation.OCPP;
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
        /// Where the accounts live, unless another directory is given.
        /// </summary>
        public const String  DefaultAccountsPath          = "accounts";

        /// <summary>
        /// The accounts themselves, inside that directory.
        /// </summary>
        public const String  DefaultAccountsDatabaseFile  = "users.db";

        /// <summary>
        /// Where the log files go, unless another directory is given.
        /// </summary>
        public const String  DefaultLogPath               = "logs";

        /// <summary>
        /// Where the HTTPExt API answers: accounts, groups and API keys.
        /// </summary>
        /// <remarks>
        /// Beside "/api" rather than under it, because it is not this station's
        /// API: it is Hermod's, with its own routes and its own vocabulary, and
        /// putting it under /api/v1 would promise that this station versions
        /// it.
        /// </remarks>
        public static readonly HTTPPath  ExtAPIPath       = HTTPPath.Parse("/ext");

        /// <summary>
        /// The account made at a first start.
        /// </summary>
        public const String  DefaultAdminUser             = "root";

        /// <summary>
        /// The organization that account belongs to.
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
        /// Every time server of this station, and the rules for believing them.
        /// </summary>
        /// <remarks>
        /// Beside the single client rather than instead of it, because the two
        /// answer different questions. The group answers "what is the time",
        /// which several servers should agree on before a station believes it.
        /// The client answers "what is that one server doing", which is what
        /// the detailed test on the page asks and which a group would only
        /// blur, having four of everything.
        /// </remarks>
        private           TimeSourceGroup                      timeSources;

        /// <summary>
        /// How many of those servers this station was told must answer: by the
        /// last section that named "minServers", or the default of two.
        /// </summary>
        /// <remarks>
        /// Kept apart from the group's own quorum, which cannot be more than the
        /// servers it has switched on. A lone hostname holds a group to one, and
        /// if that one were all that was remembered, a list of four arriving
        /// afterwards would be held to one as well - where the same file, read
        /// at the next start, holds it to two.
        /// </remarks>
        private           Byte                                 ntsQuorum           = NTSConfiguration.DefaultMinServers;

        /// <summary>
        /// What does the asking.
        /// </summary>
        /// <remarks>
        /// One engine for the life of this station, and that is not tidiness:
        /// it holds the key exchange of each server between rounds, and a new
        /// engine per check would pay a TLS handshake to every server every
        /// time and throw the cookies away unspent. It refreshes an exchange
        /// when it is older than half an hour or down to its last cookie, which
        /// is the same discipline the single client follows.
        /// </remarks>
        private readonly  MeasurementEngine                    timeEngine;

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
        /// Separate fields rather than one object because they are written from
        /// one place and read from another, and the alternative - digging them
        /// back out of the JSON of the last check - would make the display
        /// depend on the shape of a diagnostic.
        ///
        /// The server is a host name only where there is one of them. A group
        /// of four is counted instead, in numbers, because the display puts
        /// this behind "checked against" in whichever language it is showing
        /// and a phrase assembled here would arrive in the wrong one.
        /// </remarks>
        private           DateTimeOffset?                      lastTimeCheck;
        private           TimeSpan?                            lastTimeCheckOffset;
        private           String?                              lastTimeCheckServer;
        private           Int32?                               lastTimeCheckAsked;
        private           Int32?                               lastTimeCheckAnswered;

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
        private readonly  FileLog?                             fileLog;
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
        /// Who may open the web interface: the accounts, the groups they are
        /// in, and the sessions and API keys they hold.
        /// </summary>
        public HTTPExtAPI             ExtAPI                 { get; }

        /// <summary>
        /// Whether those accounts are this station's own, or somebody else's
        /// that it was handed.
        /// </summary>
        /// <remarks>
        /// Handing one in is what makes one sign-in open several of these
        /// programs at once: the groups each of them makes as it starts land
        /// in one set of accounts, and the names overlap on purpose - an
        /// account in "systemadmin" is an administrator of every one of them.
        /// </remarks>
        public Boolean                OwnsExtAPI             { get; }

        /// <summary>
        /// Whether the HTTP server is this station's own, or one it was
        /// handed and shares with somebody else.
        /// </summary>
        /// <remarks>
        /// A shared server is started and stopped by whoever made it. One that
        /// started a server it did not make would take the same socket twice
        /// where several of these programs are on it, and one that stopped it
        /// would close the web interface of every other program registered
        /// within it.
        /// </remarks>
        public Boolean                OwnsHTTPServer         { get; }

        /// <summary>
        /// Everything of this station - its web interface, its JSON API and,
        /// where the accounts are its own, those too - sits below this.
        /// </summary>
        /// <remarks>
        /// The root, which is what a station on a port of its own wants and
        /// what it always used to be. It is something else only where several
        /// of these programs share one HTTP server and are told apart by the
        /// first path segment rather than by the port.
        /// </remarks>
        public HTTPPath               BasePath               { get; }

        /// <summary>
        /// The base path as it is written into a URL: the empty string at the
        /// root, and "/ChargingStation" or the like below one.
        /// </summary>
        /// <remarks>
        /// Its own property because the two forms are not interchangeable and
        /// the difference is exactly one character: <c>HTTPPath.Root</c> writes
        /// itself as "/", and "/" + "/index.html" is a URL nothing serves.
        /// </remarks>
        public String                 BasePathText
            => BasePath == HTTPPath.Root
                   ? ""
                   : BasePath.ToString().TrimEnd('/');

        /// <summary>
        /// The directory the accounts live in between starts.
        /// </summary>
        public String                 AccountsPath           { get; }

        /// <summary>
        /// The directory the log files are written to, one per day, or null
        /// when this station writes none.
        /// </summary>
        public String?                LogPath
            => fileLog?.Directory;

        /// <summary>
        /// Where everything this station can be told in writing lives between
        /// starts: its name resolution, its time source, its EVSEs.
        /// </summary>
        public StationConfigFile      ConfigFile             { get; }

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
        /// The time servers of this station, as a group.
        /// </summary>
        public TimeSourceGroup        TimeSources
            => timeSources;

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
        /// The password made up at a first start and shown once, or null when
        /// accounts were already there. It is kept nowhere but in its hash.
        /// </summary>
        /// <remarks>
        /// Set by <see cref="Start"/> rather than by the constructor, because
        /// creating the account is asynchronous and a constructor that waited
        /// on it would be a constructor that can deadlock.
        /// </remarks>
        public String?                GeneratedPassword      { get; private set; }

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
        /// <remarks>
        /// Settable because the V2G configuration page changes it while the
        /// station runs; every change goes through
        /// <see cref="UpdateV2GConfiguration"/>, which holds the same lock as
        /// the other sections and restarts the link.
        /// </remarks>
        public V2GOptions             V2GOptions             { get; private set; }

        /// <summary>
        /// The URL to open in a browser.
        /// </summary>
        public URL                    WebInterfaceURL        { get; }

        /// <summary>
        /// The JSON API as a browser would type it: the server and the API's
        /// root path, which already carries the base path, with a slash at the end.
        /// </summary>
        public URL                    APIURL                 { get; }

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
        /// <param name="BasePath">What everything of this station sits below; the root by default. Something else only where several of these programs share one HTTP server.</param>
        /// <param name="HTTPRootPath">The root path of the JSON API, "/api" below <paramref name="BasePath"/> by default.</param>
        /// <param name="ExtAPI">An HTTPExt API to sign in against, or null for one of this station's own. Handing one in is what makes one sign-in open several of these programs at once.</param>
        /// <param name="HTTPHostname">The address to listen on; the loopback address by default.</param>
        /// <param name="HTTPPort">The TCP port to listen on.</param>
        /// <param name="AccountsPath">The directory the accounts live in between starts.</param>
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
        /// <param name="LogPath">The directory the log files are written to, or null to write none.</param>
        /// <param name="BridgeDebugLog">Whether what the libraries below write with DebugX ends up in the log.</param>
        /// <param name="TimeProvider">Where this station reads the time; the system clock by default.</param>
        public ChargingStation(DNSClient?             DNSClient         = null,
                               NTSClient?             NTSClient         = null,
                               HTTPServer?            HTTPServer        = null,
                               HTTPPath?              BasePath          = null,
                               HTTPPath?              HTTPRootPath      = null,
                               HTTPExtAPI?            ExtAPI            = null,
                               IIPAddress?            HTTPHostname      = null,
                               IPPort?                HTTPPort          = null,
                               String?                AccountsPath      = null,
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
                               String?                LogPath           = null,
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

            // Everything, and not what the console was told to show: a level
            // is chosen to keep a console readable, and a file nobody is
            // reading has no such problem. What is left out here cannot be
            // asked for afterwards.
            this.fileLog      = LogPath is not null
                                    ? new FileLog(this.Log, LogPath)
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

            #region Where the accounts live

            // Ending in a separator, because the HTTPExt API builds the paths
            // of its files by putting strings together rather than with
            // Path.Combine: a directory that does not end in one would give it
            // "...accountsUsersAPI" and not "...accounts/UsersAPI".
            this.AccountsPath = AccountsPath ?? DefaultAccountsPath;

            if (!this.AccountsPath.EndsWith(Path.DirectorySeparatorChar))
                this.AccountsPath += Path.DirectorySeparatorChar;

            #endregion

            #region What the configuration file says

            this.ConfigFile = ConfigFile ?? new StationConfigFile(StationConfigFile.DefaultFileName);

            this.ClientCertificates = new ClientCertificateStore(
                                          System.IO.Path.Combine(
                                              System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(this.ConfigFile.Path)) ?? ".",
                                              ClientCertificateStore.DefaultDirectoryName
                                          ),
                                          TimeProvider
                                      );

            // "this.Log", because the bare name here is the constructor's own
            // parameter, which is null at this point - the same trap that once
            // stopped this station from starting at all.
            this.ClientCertificates.OnNotice += (level, message) => this.Log.Log(level, message, "ocpp", "certificates");

            this.Connections = new ConnectionStore(
                                   System.IO.Path.Combine(
                                       System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(this.ConfigFile.Path)) ?? ".",
                                       ConnectionStore.DefaultDirectoryName
                                   ),
                                   TimeProvider
                               );

            // Which certificates exist is the certificate store's business and
            // changes while this one is alive, so it is asked rather than told
            // once.
            this.Connections.KnownCertificateIds  = () => this.ClientCertificates.Entries.Select(entry => entry.Id);
            this.Connections.OnNotice            += (level, message) => this.Log.Log(level, message, "ocpp", "connections");

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

            this.timeEngine    = new MeasurementEngine(
                                     new MonitoringConfig {
                                         DroneId       = "chargingStation",
                                         NTPTimeout    = TimeSpan.FromSeconds(5),
                                         NTSKETimeout  = TimeSpan.FromSeconds(10)
                                     },
                                     this.TimeProvider
                                 );

            // The four this station asks when nobody says otherwise - but only
            // when nobody handed it a client either. A caller that named its
            // own server means that server, and a group naming four others
            // beside it would be a report about somebody else's clock.
            this.timeSources   = NTSClient is null
                                     ? NTSConfiguration.DefaultGroup()
                                     : new TimeSourceGroup(
                                           "legal",
                                           [ new NTSServerEndpoint(
                                                 ntsClient.Hostname,
                                                 ntsClient.NTSKE_Port,
                                                 ntsClient.NTP_Port
                                             ) ]
                                       );

            // Last, and that is the whole precedence rule: what this
            // constructor was handed holds until the file says otherwise, and
            // what the file does not mention is left exactly as it was.
            if (configuration?.DNS is not null)
                ApplyDNSConfiguration(configuration.DNS);

            if (configuration?.NTS is not null)
            {

                // Checked here rather than when the file was read: a quorum
                // on its own is about the servers in effect, and which those
                // are is only known now.
                if (!TryCheckNTSQuorum(configuration.NTS, out var quorumError))
                    throw new InvalidOperationException($"{quorumError} Repair or remove '{this.ConfigFile.Path}' and start again.");

                ApplyNTSConfiguration(configuration.NTS);

            }

            this.ntsSettings = configuration?.NTS;

            // Same rule for the wire below the cable: the command line asked
            // for something, and the file has the last word on it. Nothing is
            // started here - Start() does that, much later - so this only
            // decides what will be started.
            if (configuration?.V2G is not null)
                this.V2GOptions = configuration.V2G.Apply(this.V2GOptions);

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

            this.OwnsHTTPServer = HTTPServer is null;

            this.httpServer    = HTTPServer   ?? new HTTPServer(
                                                     IPAddress:       address,
                                                     TCPPort:         port,
                                                     HTTPServerName:  $"OpenChargingCloud ChargingStation v{Version}",
                                                     DNSClient:       dnsClient
                                                 );

            // The root unless somebody is putting several of these programs on
            // one server, where the first path segment is what tells them
            // apart. Everything below is relative to it, which is the whole
            // reason it is settled here and read rather than repeated.
            this.BasePath      = BasePath     ?? HTTPPath.Root;

            this.httpRootPath  = HTTPRootPath ?? this.BasePath + CSHTTPAPI.DefaultAPIPath;

            this.HTTPPort        = port;
            this.WebInterfaceURL = URL.Parse($"http://{address}:{port}{this.BasePath.ToString().TrimEnd('/')}/");

            // From the server rather than from the web interface's URL: the API's
            // root path already carries the base path, and behind a URL that ends
            // in the base path it would be named twice.
            this.APIURL          = URL.Parse($"http://{address}:{port}/{this.httpRootPath.ToString().Trim('/')}/");

            // 1) The HTTPExt API at "/ext". First of the three, because it is
            //    the one with a database behind it: whatever it finds wrong
            //    with its files, it should say so before a port is opened and
            //    before anybody is let in against accounts that were not read.
            this.OwnsExtAPI    = ExtAPI is null;

            this.ExtAPI        = ExtAPI ?? new HTTPExtAPI(
                                     HTTPServer:             httpServer,
                                     RootPath:               this.BasePath + (ExtAPIPath),
                                     HTTPServerName:         $"OpenChargingCloud ChargingStation v{Version}",
                                     HTTPServiceName:        $"OpenChargingCloud ChargingStation v{Version}",
                                     APIRobotEMailAddress:   EMailAddress.Parse("OpenChargingCloud ChargingStation Robot <robot@charging.cloud>"),
                                     APIRobotGPGPassphrase:  "",

                                     // Nothing here sends mail. A station that
                                     // notifies by e-mail is told so by whoever
                                     // runs it, with a submission client of
                                     // their own; until then a mailer that
                                     // swallows what it is given is better than
                                     // one that quietly retries against a host
                                     // nobody configured.
                                     SMTPSubmissionClient:   new NullMailer(),
                                     DisableNotifications:   true,

                                     // The cookie has to reach "/api", and its
                                     // path would otherwise be the root path of
                                     // this API - "/ext" - so a browser signed
                                     // in at /ext/login would send nothing to
                                     // the API and look signed out everywhere
                                     // else.
                                     HTTPCookiePath:         "/",

                                     // A secure cookie is dropped by a browser
                                     // over plain HTTP, and a station on a
                                     // bench is reached over plain HTTP. Tied
                                     // to the TLS the server is actually using
                                     // rather than switched off: on a station
                                     // with a certificate this stays on.
                                     UseSecureCookies:       false,

                                     // The shortest name a role of this station has,
                                     // because that is what a group identification has
                                     // to be allowed to be. Hermod's own floor is four
                                     // characters and "cpo" is three, so the group
                                     // would be refused - by a returned result rather
                                     // than an exception, which is a refusal nobody is
                                     // obliged to notice - and the role it carries
                                     // could never be held by anybody.
                                     MinUserGroupIdLength:   (Byte) UserRole.All.Min(role => role.Name.Length),

                                     LoggingPath:            AccountsPath,
                                     DatabaseFileName:       DefaultAccountsDatabaseFile,

                                     // Left on, and that is what makes the
                                     // directory above: switching it off skips
                                     // the CreateDirectory that the accounts
                                     // file is written into, and the first
                                     // account created would fail on a path
                                     // that was never made.
                                     DisableLogging:         false
                                 );

            this.Log.Info(
                OwnsExtAPI
                    ? $"The accounts of this station are in '{this.ExtAPI.DatabaseFileName}', its HTTPExt API at '{this.ExtAPI.RootPath}'."
                    : $"This station signs in against accounts it shares, at '{this.ExtAPI.RootPath}'.",
                "web", "http"
            );

            // 2) The JSON API at "/api". Before the web interface, so that it
            //    is the more specific API and an unknown /api path never
            //    reaches the single-page-application stub below.
            this.API           = new CSHTTPAPI(
                                     HTTPServer:  httpServer,
                                     Station:     this,
                                     ExtAPI:      this.ExtAPI,
                                     Log:         this.Log,
                                     APIPath:     httpRootPath,
                                     Version:     Version
                                 );

            // 3) The web interface at "/": the files of the bundle, and the
            //    single-page-application stub for every other page URL, so
            //    that a reload on /logs and a bookmark to it both work.
            this.Frontend      = Frontend ?? new EmbeddedContentSource(HTTPRoot, typeof(ChargingStation).Assembly);

            if (this.Frontend.TryGet(IndexFile, out _))
            {

                this.WebInterface = httpServer.AddHTTPAPI(this.BasePath);

                this.WebInterface.MapSinglePageApplication(
                    this.Frontend,
                    new SinglePageAppOptions {

                        // Three placeholders and not one. The bundle reads
                        // where it is and where its API is out of <meta> tags
                        // rather than assuming "/" and "/api/v1", because
                        // under a base path both of those are wrong - and a
                        // single-page application that guesses its own base
                        // path is one that works until somebody mounts it
                        // somewhere.
                        IndexTransform = html => html.
                                                     Replace("{{ServerVersion}}", $"v{Version}",         StringComparison.Ordinal).
                                                     Replace("{{BasePath}}",      BasePathText,          StringComparison.Ordinal).
                                                     Replace("{{APIBase}}",       $"{httpRootPath.ToString().TrimEnd('/')}/v1", StringComparison.Ordinal).
                                                     Replace("{{ExtBase}}",       this.ExtAPI.RootPath.ToString().TrimEnd('/'), StringComparison.Ordinal)

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
                                           Location        = Location.From(HTTPPath.Parse($"{BasePathText}/{FaviconSVG}")),
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

            // Before the port opens, and that order is the point: a web
            // interface reachable before its accounts exist is a door with
            // nobody behind it.
            await EnsureAccounts();

            // Only where it is ours: a shared server is started by whoever
            // made it, and starting it again would take the same socket twice.
            if (OwnsHTTPServer)
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
                    //
                    // Only where it is this station's, though: a shared server
                    // carries other programs' web interfaces too, and closing
                    // it because this station could not have a display would
                    // take all of them down with it.
                    if (OwnsHTTPServer)
                        await httpServer.Stop();

                    throw;
                }
            }

            StartCheckingTheClock();

            started = true;

            Log.Notice($"The web interface is listening on {WebInterfaceURL}", "web", "http");

            if (KioskURL.HasValue)
                Log.Notice($"The display is listening on {KioskURL.Value} - no sign-in, and nothing of the administration on it.", "kiosk", "http");
            Log.Info   ($"The JSON API is at {APIURL}v1/status", "web", "http");

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

            // Before the servers, so that a close this station asked for is
            // recognised as one and does not start a reconnect on the way out.
            await HangUp();

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

            // The event streams above are ended whoever owns the server,
            // because they are this station's; the socket is closed only where
            // it is this station's too.
            if (OwnsHTTPServer)
                await httpServer.Stop();

            started = false;

        }

        #endregion

        #region (private) EnsureAccounts()

        /// <summary>
        /// Make the four groups and, at a first start, the one account that is
        /// in the last of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The groups are made every start rather than only the first, because
        /// they are this station's vocabulary and not somebody's data: a group
        /// deleted by hand would otherwise leave a role that can never be held
        /// again, and the routes asking for it would refuse everybody with no
        /// way to put it right.
        /// </para>
        /// <para>
        /// The account is made only when there is none at all. Nobody can sign
        /// in to a web interface whose accounts are empty, and an
        /// unauthenticated setup page would be a door of its own - so the
        /// password is made up here and shown once, on the console, to whoever
        /// started the process. It is never written down: what the accounts
        /// hold is the hash the HTTPExt API makes of it.
        /// </para>
        /// </remarks>
        private async Task EnsureAccounts()
        {

            // Read what is on disk first. The HTTPExt API writes its accounts
            // as it goes but does not read them back when it is built, so a
            // station that skipped this would find no accounts at every start,
            // make a second root beside the first, and refuse the password its
            // owner already has.
            await ExtAPI.LoadDatabase();

            var firstStart  = !ExtAPI.Users.Any();

            IUser?  admin   = null;

            #region The one account, when there is none

            if (firstStart)
            {

                var password  = RandomExtensions.RandomString(24);
                var userId    = User_Id.Parse(DefaultAdminUser);

                // CreateUser rather than AddUser: the password is set from
                // inside the OnAdded callback, where the user already has its
                // API back-reference, and that is the only place the password
                // store can be reached. AddUser followed by ChangePassword
                // looks equivalent and writes the account without one - which
                // is an account nobody can sign in to, and nothing says so.
                var organization  = await ExtAPI.CreateOrganizationIfNotExists(
                                              Organization_Id.Parse(DefaultOrganization),
                                              I18NString.Create(Languages.en, DefaultOrganization)
                                          );

                if (organization is not Organization stationOrganization)
                    throw new InvalidOperationException("The organization of this station could not be created, and an account outside one cannot sign in.");

                admin         = await ExtAPI.CreateUser(
                                          userId,
                                          I18NString.Create(Languages.en, DefaultAdminUser),
                                          SimpleEMailAddress.Parse($"{DefaultAdminUser}@localhost"),
                                          User2OrganizationEdgeLabel.IsAdmin,
                                          stationOrganization,
                                          Password:                  password,

                                          // Nothing is sent and nobody is told:
                                          // a station has no mail server, no
                                          // second user to notify, and the one
                                          // account it makes is announced on the
                                          // console it was started from.
                                          SkipDefaultNotifications:  true,
                                          SkipNewUserEMail:          true,
                                          SkipNewUserNotifications:  true,

                                          // Without this nobody can sign in, and
                                          // nothing says why: the sign-in paths
                                          // require an accepted EULA and refuse a
                                          // correct password without one. There is
                                          // no agreement to show here - whoever
                                          // started the process owns the station -
                                          // so it is accepted at the moment the
                                          // account is made.
                                          AcceptedEULA:              TimeProvider.GetUtcNow().AddSeconds(-1),

                                          IsAuthenticated:           true
                                      );

                if (admin is null)
                    throw new InvalidOperationException("The account of this station could not be created, so nobody could sign in to it.");

                GeneratedPassword = password;

                Log.Notice($"No accounts were found, so '{DefaultAdminUser}' was made up and put in the {UserRole.SystemAdmin.Name} group.",
                           "web", "auth");

            }

            #endregion

            #region The four groups

            foreach (var role in UserRole.All)
            {

                if (ExtAPI.TryGetUserGroup(role.GroupId, out _))
                    continue;

                var added = await ExtAPI.AddUserGroup(
                                      new UserGroup(
                                          role.GroupId,
                                          I18NString.Create(Languages.en, role.Name)
                                      )
                                  );

                // Looked at, and that is the point: this answers with a result
                // rather than throwing, so a group it declined to make would
                // otherwise leave a role nobody can ever hold - and every route
                // asking for it refusing everybody, with nothing anywhere to
                // say why. Better to stop before the port opens.
                if (added.Result != CommandResult.Success)
                    throw new InvalidOperationException(
                              $"The user group '{role.GroupId}' of this station could not be made: " +
                              $"{added.Description.FirstText()} A role without its group is a role nobody can hold."
                          );

            }

            #endregion

            #region The one account joins the one group that can fix the rest

            // Through AddUserToUserGroup, which writes a command of its own.
            // Putting the edge on the group object before storing it looks
            // equivalent and is not: what AddUserGroup writes is the group,
            // and a group's stored form does not carry its members - so the
            // membership was there until the next start and gone after it,
            // which is the worst shape a permission can have.
            if (admin is not null)
            {

                if (!ExtAPI.TryGetUser     (admin.Id,                     out var storedAdmin) ||
                    !ExtAPI.TryGetUserGroup(UserRole.SystemAdmin.GroupId, out var adminGroup)  ||
                    storedAdmin is not User      user ||
                    adminGroup  is not UserGroup group)
                {
                    // The password has been made up by now and is about to be
                    // printed. An account that is in no group can do nothing at
                    // all, so saying so here is better than handing somebody a
                    // password that opens nothing.
                    throw new InvalidOperationException(
                              $"The account '{DefaultAdminUser}' could not be put in the {UserRole.SystemAdmin.Name} group, " +
                               "so the one account this station has would be able to do nothing at all."
                          );
                }

                var joined = await ExtAPI.AddUserToUserGroup(
                                       user,
                                       User2UserGroupEdgeLabel.IsAdmin,
                                       group
                                   );

                // Looked at for the same reason as the group above: this
                // answers with a result too, and a membership it declined to
                // write leaves the one account able to do nothing at all -
                // with a password about to be printed that opens nothing.
                // A different result type from AddUserGroup's, and so a
                // different question: IsSuccess rather than Result.
                if (!joined.IsSuccess)
                    throw new InvalidOperationException(
                              $"The account '{DefaultAdminUser}' could not be put in the {UserRole.SystemAdmin.Name} group: " +
                              $"{joined.ErrorDescription?.FirstText()} It would be able to do nothing at all."
                          );

            }

            #endregion

        }

        #endregion

        #region ConfigurationJSON()

        /// <summary>
        /// What this charging station is made of, as the Configuration page of
        /// the web interface reads it.
        /// </summary>
        /// <remarks>
        /// Read-only for now: it answers "what am I running", not "change it".
        /// Nothing here is a secret - the accounts appear as a directory, a
        /// route to sign in at and two counts, and never with anything about a
        /// password.
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
                       new JProperty("accountsPath",   AccountsPath),
                       new JProperty("signInAt",       $"{ExtAPIPath.ToString().TrimEnd('/')}/login"),
                       new JProperty("users",          ExtAPI.Users.     Count()),
                       new JProperty("groups",         ExtAPI.UserGroups.Count()),
                       new JProperty("cookie",         ExtAPI.SessionCookieName.ToString()),
                       new JProperty("maxLifetime",    ExtAPI.MaxSignInSessionLifetime.ToString())
                   )),

                   new JProperty("log",        new JObject(
                       new JProperty("capacity",       Log.Capacity),
                       new JProperty("entries",        Log.Count),
                       new JProperty("lastId",         Log.LastId),
                       new JProperty("debugBridge",    traceBridge is not null),
                       new JProperty("console",        consoleLog is not null),
                       new JProperty("files",          LogPath),
                       new JProperty("tags",           new JArray(Log.KnownTags))
                   )),

                   new JProperty("v2g",        V2G?.ToJSON() ?? new JObject(
                       new JProperty("enabled",        false)
                   )),

                   // The group, which is what sets the clock. This card used to
                   // lead with "NTS" and the host of the single client the
                   // detailed test starts from - one server, above the four that
                   // are actually asked, and with its root dot - and it left out
                   // every server that was switched off. The servers are now
                   // named the way the log names them when they change.
                   //
                   // And the last synchronisation - the button's, the prompt's
                   // or the clock check's - when it happened and how it went,
                   // or nothing while there has been none.
                   new JProperty("time",       new JObject(
                       new JProperty("ntsEnabled",     NTSEnabled),
                       new JProperty("timeServers",    Described(timeSources)),
                       new JProperty("minServers",     timeSources.MinServers),
                       new JProperty("checkedEvery",   TimeCheckEvery.ToString()),
                       new JProperty("lastSync",       lastTimeSync?.Value<String>("at")),
                       new JProperty("lastSyncResult", LastSyncSaid(lastTimeSync)),
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

        #region ShareConsoleWith(WriteBlock)

        /// <summary>
        /// Let somebody else decide when this station's log may write on the
        /// console, because they are writing on it too.
        /// </summary>
        /// <remarks>
        /// A station at a console assumes the console is its own and writes an
        /// entry whenever one happens, from whichever thread it happened on.
        /// That assumption stops holding the moment somebody is typing a command
        /// on the same screen: an entry arriving mid-word puts half a log line
        /// into the middle of a half-typed command and ruins both.
        ///
        /// So the writing is handed over rather than suppressed. Whoever owns
        /// the line takes the entry, clears what is being typed, writes the
        /// entry as one piece and puts the line back. Nothing is lost and
        /// nothing is delayed, which is what makes this better than the obvious
        /// alternative of going quiet while a command line is open.
        ///
        /// Has no effect on a station whose log does not reach the console.
        /// </remarks>
        /// <param name="WriteBlock">Runs what it is given with the console to itself.</param>
        public void ShareConsoleWith(Action<Action> WriteBlock)
        {

            if (consoleLog is not null)
                consoleLog.WriteBlock = WriteBlock;

        }

        #endregion

        #region DisposeAsync()

        /// <summary>
        /// Stop listening and let go of the console, the log file and the debug
        /// bridge.
        /// </summary>
        public async ValueTask DisposeAsync()
        {

            await Stop();

            traceBridge?.Dispose();
            consoleLog? .Dispose();
            fileLog?    .Dispose();

            reconfigureLock.Dispose();

            GC.SuppressFinalize(this);

        }

        #endregion

    }

}
