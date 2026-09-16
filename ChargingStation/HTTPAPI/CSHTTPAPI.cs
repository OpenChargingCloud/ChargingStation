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

using System.Diagnostics.CodeAnalysis;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.RFID;
using cloud.charging.open.ChargingStation.Logging;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": the sign-in,
    /// the configuration of this charging station, its log, and one
    /// Server-Sent Events stream that carries everything that happens.
    /// </summary>
    /// <remarks>
    /// It lives in its own HTTPAPI so that unknown API paths never reach the
    /// single-page-application fallback of the web interface at "/": Hermod
    /// dispatches a request to the most specific HTTPAPI first.
    ///
    /// Everything below /api/v1 except the sign-in itself needs the session
    /// cookie - the event stream included, which is why the stream is opened
    /// here by hand rather than through Hermod's MapEventSource.
    /// </remarks>
    public class CSHTTPAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// The default root path of this API.
        /// </summary>
        public static readonly HTTPPath  DefaultAPIPath      = HTTPPath.Parse("/api");

        /// <summary>
        /// The identification of the Server-Sent Events source.
        /// </summary>
        public const           String    EventSourceName     = "events";

        /// <summary>
        /// The sub-event every log entry travels as.
        /// </summary>
        public const           String    LogEventName        = "log";

        /// <summary>
        /// How long a failed sign-in waits before it answers. Not a lock-out,
        /// just enough to make guessing a slow business.
        /// </summary>
        public static readonly TimeSpan  FailedLoginDelay    = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// The most log entries one request may ask for.
        /// </summary>
        public const           Int32     MaxLogPageSize      = 2_000;

        /// <summary>
        /// How many log entries a request brings back when it does not say.
        /// </summary>
        public const           Int32     DefaultLogPageSize  = 500;

        private readonly DateTimeOffset  startedAt;

        /// <summary>
        /// Cancelled when this station is shutting down, so that the event
        /// streams end.
        /// </summary>
        /// <remarks>
        /// A browser on the Logs page holds a request open that is not waiting
        /// on its socket but on the next log entry, so closing the socket under
        /// it does not end it - and an HTTP server that waits for every request
        /// it started would then never finish stopping. This is what ends them
        /// instead; see <see cref="CloseEventStreams"/>.
        /// </remarks>
        private readonly CancellationTokenSource  shutdown = new ();

        #endregion

        #region Properties

        /// <summary>
        /// The charging station this API speaks for.
        /// </summary>
        public ChargingStation           Station   { get; }

        /// <summary>
        /// Everything that happens inside this charging station.
        /// </summary>
        public EventLog                  Log       { get; }

        /// <summary>
        /// The signed-in browsers.
        /// </summary>
        public WebSessions               Sessions  { get; }

        /// <summary>
        /// The version reported by the status resource.
        /// </summary>
        public String                    Version   { get; }

        /// <summary>
        /// The Server-Sent Events source every browser hangs on (/api/v1/events).
        /// </summary>
        public HTTPEventSource<JObject>  Events    { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Station">The charging station this API speaks for.</param>
        /// <param name="Sessions">The web sessions.</param>
        /// <param name="Log">Everything that happens inside this charging station.</param>
        /// <param name="APIPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public CSHTTPAPI(HTTPServer       HTTPServer,
                         ChargingStation  Station,
                         WebSessions      Sessions,
                         EventLog         Log,
                         HTTPPath?        APIPath   = null,
                         String?          Version   = null)

            : base(HTTPServer,
                   RootPath:     APIPath ?? DefaultAPIPath,
                   Description:  I18NString.Create("The JSON API of this charging station"))

        {

            this.Station   = Station;
            this.Sessions  = Sessions;
            this.Log       = Log;
            this.startedAt = Station.TimeProvider.GetUtcNow();

            this.Version   = Version
                                 ?? typeof(CSHTTPAPI).Assembly.GetName().Version?.ToString(3)
                                 ?? "0.0.0";

            // Hermod caches the last events and replays them to a new client.
            // The browser ignores everything older than the snapshot it loaded,
            // so a replay costs nothing but bytes; what it buys is that a
            // browser which reconnects after a hiccup gets what it missed.
            this.Events    = this.AddJSONEventSource(
                                 HTTPEventSource_Id.Parse(EventSourceName),
                                 MaxNumberOfCachedEvents:  500,
                                 RetryInterval:            TimeSpan.FromSeconds(2),
                                 EnableLogging:            false
                             );

            this.Log.OnLogged += entry => Publish(LogEventName, entry.ToJSON());

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        private void RegisterURLTemplates()
        {

            AddHandler(HTTPPath.Root + "v1/auth/login",    Login,             HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/logout",   Logout,            HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/auth/me",       Me,                HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/status",        GetStatus,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration", GetConfiguration,  HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/dns",        GetDNSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/dns",        PutDNSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/dns/query",  PostDNSQuery,          HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/nts",        GetNTSConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/nts",        PutNTSConfiguration,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/sync",   PostNTSSync,           HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/nts/test",   PostNTSTest,           HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/power",      GetPowerConfiguration,        HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/power",      PutPowerConfiguration,        HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/display",    GetDisplayConfiguration,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/display",    PutDisplayConfiguration,      HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/evses",      GetEVSEConfiguration,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/evses",      PutEVSEConfiguration,         HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/calibration", GetCalibrationConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/calibration", PutCalibrationConfiguration, HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/authentications",        GetConnectionsAndAuth,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications",        PostAuthentication,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications/update", PostUpdateAuthentication,  HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications/remove", PostRemoveAuthentication,  HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/connections",            GetConnectionsAndAuth,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/connections",            PostConnection,            HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/update",     PostUpdateConnection,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/remove",     PostRemoveConnection,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/test",       PostTestConnection,        HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/certificates",        GetClientCertificates,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/certificates",        PostClientKey,           HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/certificates/import", PostClientCertificate,   HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/certificates/remove", PostRemoveClientKey,     HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/rfid",       GetRFIDConfiguration,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/rfid",       PutRFIDConfiguration,         HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/reservations",        GetReservations,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/reservations",        PostReserveNow,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/reservations/cancel", PostCancelReservation, HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/sessions/webpayment", PostWebPaymentSession, HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/sessions/stop",       PostStopSession,       HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/messages",        GetMessages,       HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/messages",        PostMessage,       HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/messages/clear",  PostClearMessage,  HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/logs",          GetLogs,           HTTPMethod.GET);

            AddHandler(HTTPMethod.GET,
                       HTTPPath.Root + "v1/events",
                       HTTPContentType.Text.EVENTSTREAM,
                       StreamEvents);

            // Everything else below /api answers with a JSON 404 instead of
            // the single-page-application stub of the web interface.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PUT, HTTPMethod.DELETE })
                AddHandler(HTTPPath.Root + "{path..}", UnknownPath, method);

        }

        #endregion


        #region (private) Login           (Request)

        /// <summary>
        /// POST /api/v1/auth/login with {"username", "password"}: the session
        /// cookie, or 401 after a short pause.
        /// </summary>
        private async Task<HTTPResponse> Login(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Sessions.TryLogin(json.Value<String>("username"),
                                   json.Value<String>("password"),
                                   out var session))
            {

                Log.Warning($"Sign-in refused for {Request.RemoteSocket}.", "web", "auth");

                await Task.Delay(FailedLoginDelay, Request.CancellationToken);

                return ErrorJSON(Request, HTTPStatusCode.Unauthorized, "Wrong username or password.");

            }

            Log.Notice($"'{session.UserId}' signed in from {Request.RemoteSocket} as {String.Join(", ", Sessions.Roles.Select(role => role.Name))}.", "web", "auth");

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = HTTPContentType.Application.JSON_UTF8,
                       Content         = Encoding.UTF8.GetBytes(MeJSON(session).ToString(Formatting.None)),
                       CacheControl    = "no-store",
                       SetCookie       = Sessions.SessionCookie(session)
                   }.WithCommonSecurityHeaders().AsImmutable;

        }

        #endregion

        #region (private) Logout          (Request)

        /// <summary>
        /// POST /api/v1/auth/logout: ends the session and expires the cookie.
        /// </summary>
        private Task<HTTPResponse> Logout(HTTPRequest Request)
        {

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (Sessions.SignOut(Request))
                Log.Notice($"'{Sessions.Username}' signed out from {Request.RemoteSocket}.", "web", "auth");

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {
                           HTTPStatusCode  = HTTPStatusCode.NoContent,
                           CacheControl    = "no-store",
                           SetCookie       = Sessions.ExpiredCookie()
                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region (private) Me              (Request)

        /// <summary>
        /// GET /api/v1/auth/me: who is signed in, or 401.
        /// </summary>
        private Task<HTTPResponse> Me(HTTPRequest Request)

            => Task.FromResult(
                   TryGetSession(Request, out var session, out var unauthorized)
                       ? JSONResponse(Request, HTTPStatusCode.OK, MeJSON(session))
                       : unauthorized
               );

        #endregion


        #region (private) GetStatus       (Request)

        /// <summary>
        /// GET /api/v1/status: how this charging station is doing right now.
        /// </summary>
        private Task<HTTPResponse> GetStatus(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var now = Station.TimeProvider.GetUtcNow();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               new JProperty("service",    "ChargingStation"),
                               new JProperty("version",    Version),
                               new JProperty("hermod",     typeof(HTTPServer).Assembly.GetName().Version?.ToString(3)),
                               new JProperty("timestamp",  now.ToString("o")),
                               new JProperty("startedAt",  startedAt.ToString("o")),
                               new JProperty("uptime",     (now - startedAt).ToString(@"d\.hh\:mm\:ss")),
                               new JProperty("sessions",   Sessions.Count),
                               new JProperty("log",        new JObject(
                                                               new JProperty("entries",   Log.Count),
                                                               new JProperty("capacity",  Log.Capacity),
                                                               new JProperty("lastId",    Log.LastId),
                                                               new JProperty("tags",      new JArray(Log.KnownTags))
                                                           ))
                           )
                       )
                   );

        }

        #endregion

        #region (private) GetConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration: what this charging station is made of.
        /// </summary>
        private Task<HTTPResponse> GetConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.ConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetDNSConfiguration(Request) / PutDNSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/dns: how this station resolves names.
        /// </summary>
        private Task<HTTPResponse> GetDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/dns: change what may be changed about it.
        /// Answers with the whole configuration as it now stands, so that the
        /// page does not have to ask again to find out what it got.
        /// </summary>
        private Task<HTTPResponse> PutDNSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateDNSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DNSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/dns/query with {"name", "recordTypes"}:
        /// make this station look a name up and say what came back.
        /// </summary>
        /// <remarks>
        /// A POST although it changes nothing here, because it makes this
        /// station send traffic to a host somebody named - which is not
        /// something to leave sitting in a URL that a browser may repeat,
        /// prefetch or put in a history.
        /// </remarks>
        private async Task<HTTPResponse> PostDNSQuery(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var name = json.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, "A 'name' to look up is required.");

            if (!ChargingStation.TryParseRecordTypes(json["recordTypes"], out var recordTypes, out var problem))
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, problem);

            Log.Info($"'{session.UserId}' asked this station to resolve '{name}'.", "dns", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Station.ResolveAsync(name,
                                                  recordTypes,
                                                  json.Value<Int32?>("server"),
                                                  Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetNTSConfiguration(Request) / PutNTSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/nts: where this station gets the time from.
        /// </summary>
        private Task<HTTPResponse> GetNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/nts: change what may be changed about it.
        /// </summary>
        private Task<HTTPResponse> PutNTSConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateNTSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/nts/sync: one key exchange and one
        /// authenticated NTP request, with every step in the log.
        /// </summary>
        /// <remarks>
        /// Answers with the whole NTS configuration and not only with the
        /// result, because an exchange moves the cookie pool, the key material
        /// and the record of the last exchange - all of which the page is
        /// showing while it waits.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSSync(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            Log.Info($"'{session.UserId}' asked this station to synchronise its time.", "nts", "test", "web");

            var result = await Station.SyncTimeAsync(Request.CancellationToken);

            var json   = Station.NTSConfigurationJSON();

            json["result"] = result;

            return JSONResponse(Request, HTTPStatusCode.OK, json);

        }

        #endregion

        #region (private) PostNTSTest(Request)

        /// <summary>
        /// POST /api/v1/configuration/nts/test with an optional {"host"}: ask
        /// one time server everything there is to ask, and say where it got
        /// to.
        /// </summary>
        /// <remarks>
        /// The host is optional and names the server to ask; left out, it is
        /// the configured one. The key exchange may name NTP servers other
        /// than itself, and the page offers one of these per name - which is
        /// the whole reason this takes a host at all.
        ///
        /// At the diagnostics permission, with the other tests. Like "Sync
        /// now", it does not step the clock.
        /// </remarks>
        private async Task<HTTPResponse> PostNTSTest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var host = json.Value<String>("host")?.Trim();

            Log.Info($"'{session.UserId}' asked this station to test {(host is null ? "its time server" : $"the time server '{host}'")}.",
                     "nts", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Station.TestTimeServerAsync(host, Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetEVSEConfiguration(Request) / PutEVSEConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/display: the quiet hours the screen keeps.
        /// </summary>
        private Task<HTTPResponse> GetDisplayConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DisplayConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/display with {"dimFrom", "dimUntil", "dimTo"}:
        /// when the screen on the front is dim, and how dim.
        /// </summary>
        /// <remarks>
        /// The operator's, at the same permission as taking an outlet out of
        /// general use and as putting a line on the display: all three are
        /// statements about how this station presents itself to the people at
        /// it, and none of them touches what the equipment is or what it may
        /// deliver. Which hours are quiet is a fact about the site, and whoever
        /// runs the site is who knows it.
        ///
        /// The whole section at once - one end of a window is not a window -
        /// and an empty object is how dimming is turned off.
        /// </remarks>
        private Task<HTTPResponse> PutDisplayConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateDisplayConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DisplayConfigurationJSON())
                   );

        }

        /// <summary>
        /// GET /api/v1/configuration/evses: the EVSEs of this charging station.
        /// </summary>
        private Task<HTTPResponse> GetEVSEConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.EVSEConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/evses with {"evses": [...]}: replace them all.
        /// </summary>
        /// <remarks>
        /// Three permissions can guard this one route, because the request
        /// cannot say which of the three things it is doing: the whole list is
        /// sent either way, and taking an EVSE out of service, correcting what
        /// a cable may deliver and inventing a socket are the same document. So
        /// nothing more than reading gets in here, and the station is asked
        /// what the difference actually amounts to - under the lock that then
        /// applies it, so that nothing changes between the question and the
        /// answer. Everything below reading was already turned away above.
        ///
        /// One consequence is deliberate: somebody who may only read can send
        /// the list back unchanged and get a 200. Nothing was written and
        /// nothing was logged as a change, so that is a GET spelled the long
        /// way round - and the alternative, a bar at the door, would have to be
        /// the lowest of the three and would let them just as far in.
        /// </remarks>
        private Task<HTTPResponse> PutEVSEConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var permissions = Sessions.PermissionsOf(session);

            if (!Station.TryUpdateEVSEConfiguration(json,
                                                    change => permissions.HasFlag(PermissionsFor(change)),
                                                    out var change,
                                                    out var error,
                                                    out var forbidden))
            {
                return Task.FromResult(
                           forbidden
                               ? RefusePermission(Request, session, PermissionsFor(change), error)
                               : ErrorJSON(Request, HTTPStatusCode.BadRequest, error)
                       );
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.EVSEConfigurationJSON())
                   );

        }

        #endregion

        #region (private static) PermissionsFor(Change)

        /// <summary>
        /// Everything a change to the EVSEs of this station needs permission
        /// for - all of it, when one save was more than one kind of change.
        /// </summary>
        /// <remarks>
        /// The one place where the difference between a switch, a number and a
        /// claim about the hardware becomes a difference in who may do it.
        /// </remarks>
        private static Permissions PermissionsFor(EVSEChange Change)
        {

            var permissions = Permissions.None;

            if (Change.HasFlag(EVSEChange.Availability))  permissions |= Permissions.ChangeAvailability;
            if (Change.HasFlag(EVSEChange.PowerLimits))   permissions |= Permissions.ChangePowerLimits;
            if (Change.HasFlag(EVSEChange.Hardware))      permissions |= Permissions.ChangeHardware;

            return permissions;

        }

        #endregion

        #region (private) GetMessages(Request) / PostMessage(Request) / PostClearMessage(Request)

        /// <summary>
        /// GET /api/v1/messages: what this station has been asked to say, and
        /// which of it is on the screen at this moment.
        /// </summary>
        private Task<HTTPResponse> GetMessages(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.MessagesJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/messages with {"text": "...", "priority": "...",
        /// "state": "...", "evse": 1, "minutes": 60}: put a message in front of
        /// whoever is standing at this station.
        /// </summary>
        /// <remarks>
        /// A real OCPP SetDisplayMessage, answered by the same code a CSMS
        /// would reach - see ChargingStation.Messages.cs. It exists because
        /// nothing is connected to a CSMS yet.
        ///
        /// Saying something on the front of the station is the operator's to
        /// do: it is how a station tells somebody that the car park closes at
        /// ten, and it changes nothing about what the station is or what it may
        /// deliver.
        /// </remarks>
        private Task<HTTPResponse> PostMessage(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryShowMessage(json, out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        /// <summary>
        /// POST /api/v1/messages/clear with {"id": "..."}: take one off again.
        /// </summary>
        private Task<HTTPResponse> PostClearMessage(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryClearMessage(json, out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        #endregion

        #region (private) GetReservations(Request) / PostReserveNow(Request) / PostCancelReservation(Request)

        /// <summary>
        /// GET /api/v1/reservations: what this station is holding, and for how
        /// much longer.
        /// </summary>
        private Task<HTTPResponse> GetReservations(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.ReservationsJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/reservations with {"idToken": "...", "evse": 1, "minutes": 15}:
        /// hold an outlet.
        /// </summary>
        /// <remarks>
        /// A real OCPP <c>ReserveNow</c> is built and answered by the same code
        /// a CSMS would reach, so this is a way in rather than a second
        /// implementation - see ChargingStation.Reservations.cs. It exists
        /// because nothing is connected to a CSMS yet, and a station on a desk
        /// should still be able to show what a reservation looks like.
        ///
        /// Holding an outlet takes it out of general use until it runs out,
        /// which is the same kind of statement as taking one out of service -
        /// so it takes the same permission, and it is the operator's.
        /// </remarks>
        private Task<HTTPResponse> PostReserveNow(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryReserveNow(json, out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        /// <summary>
        /// POST /api/v1/sessions/webpayment with {"evse": 1, "totp": "..."}:
        /// somebody paid at the screen, so start charging there.
        /// </summary>
        /// <remarks>
        /// The other end of the QR code on the display, and the only way an
        /// AdHoc session can begin - see TryStartWebPayment for what the
        /// password proves and what it does not.
        ///
        /// Behind the sign-in, and at the same permission as holding an outlet
        /// for somebody: both are statements about who may use an outlet next,
        /// and both are the operator's to make. A payment back end that calls
        /// this signs in like anything else.
        /// </remarks>
        private Task<HTTPResponse> PostWebPaymentSession(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var evse = json["evse"]?.Value<Byte?>();

            if (evse is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "Which EVSE was paid for? Send \"evse\"."));

            if (!Station.TryStartWebPayment(evse.Value, json["totp"]?.Value<String>(), out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        /// <summary>
        /// POST /api/v1/sessions/stop with {"evse": 1}: stop what is charging
        /// there.
        /// </summary>
        /// <remarks>
        /// For the sessions that have no other way to end: a card session is
        /// stopped by the card that started it, and one paid for at the screen
        /// has no card to hold up again. Deliberately not on the display's own
        /// server - stopping somebody else's charge is not something for
        /// whoever is walking past.
        /// </remarks>
        private Task<HTTPResponse> PostStopSession(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var evse = json["evse"]?.Value<Byte?>();

            if (evse is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "Which EVSE? Send \"evse\"."));

            if (!Station.TryStopSession(evse.Value, out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        /// <summary>
        /// POST /api/v1/reservations/cancel with {"reservationId": "..."}:
        /// let an outlet go again.
        /// </summary>
        private Task<HTTPResponse> PostCancelReservation(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeAvailability, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryCancelReservation(json, out var result, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, result)
                   );

        }

        #endregion

        #region (private) GetRFIDConfiguration(Request) / PutRFIDConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/rfid: the card readers this station has.
        /// </summary>
        private Task<HTTPResponse> GetRFIDConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.RFIDConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/rfid with {"readers": [...]}: replace them all.
        /// </summary>
        /// <remarks>
        /// The same two-permission shape as the EVSEs, for the same reason:
        /// where a reader sits is a statement about the installation, switching
        /// one off is not, and the whole list is sent either way.
        /// </remarks>
        private Task<HTTPResponse> PutRFIDConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, true, out var session, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var permissions = Sessions.PermissionsOf(session);

            if (!Station.TryUpdateRFIDConfiguration(json,
                                                    change => permissions.HasFlag(PermissionsFor(change)),
                                                    out var change,
                                                    out var error,
                                                    out var forbidden))
            {
                return Task.FromResult(
                           forbidden
                               ? RefusePermission(Request, session, PermissionsFor(change), error)
                               : ErrorJSON(Request, HTTPStatusCode.BadRequest, error)
                       );
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.RFIDConfigurationJSON())
                   );

        }

        #endregion

        #region (private static) PermissionsFor(Change)

        /// <summary>
        /// Everything a change to the card readers needs permission for.
        /// </summary>
        private static Permissions PermissionsFor(RFIDChange Change)
        {

            var permissions = Permissions.None;

            if (Change.HasFlag(RFIDChange.Availability))  permissions |= Permissions.ChangeAvailability;
            if (Change.HasFlag(RFIDChange.Placement))     permissions |= Permissions.ChangeHardware;

            return permissions;

        }

        #endregion

        #region (private) GetPowerConfiguration(Request) / PutPowerConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/power: what this station may draw from the
        /// grid, and what its EVSEs could deliver together.
        /// </summary>
        private Task<HTTPResponse> GetPowerConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.PowerConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/power with {"uplinkPowerLimit_kW": 55}, or
        /// null to take the limit away.
        /// </summary>
        private Task<HTTPResponse> PutPowerConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangePowerLimits, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdatePowerConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.PowerConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetCalibrationConfiguration(Request) / PutCalibrationConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/calibration: the calibration certificates
        /// this station runs under.
        /// </summary>
        /// <remarks>
        /// Reading them needs no more than reading anything else here. A
        /// certificate is a signature over a public key and holds nothing that
        /// has to be kept; putting one on this station is what takes a
        /// permission of its own.
        /// </remarks>
        private Task<HTTPResponse> GetCalibrationConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.CalibrationConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/calibration with {"certificates": [...]}:
        /// replace them all.
        /// </summary>
        private Task<HTTPResponse> PutCalibrationConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ManageCalibration, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateCalibrationConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.CalibrationConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetConnectionsAndAuth(Request) / Post...Authentication(Request) / Post...Connection(Request)

        /// <summary>
        /// The whole picture both pages work from: the credentials, the
        /// connections, and the client certificates a connection may name.
        /// </summary>
        /// <remarks>
        /// One answer for two pages rather than two answers, because the
        /// Connections page needs the credentials in order to offer them and
        /// the Authentication page needs the connections in order to say which
        /// ones would break. Two routes returning it is a convenience for
        /// whoever reads the API; they are deliberately the same handler, so
        /// the two pages can never come to disagree about what exists.
        ///
        /// The passwords and shared secrets are not in here. What is in here
        /// is whether each set of credentials has one - see
        /// <see cref="AuthenticationEntry.ToJSON"/>, which leaves them out
        /// unless it is writing the file itself.
        /// </remarks>
        private Task<HTTPResponse> GetConnectionsAndAuth(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON())
                   );

        }

        /// <summary>
        /// What both pages are handed, in one place.
        /// </summary>
        private JObject ConnectionsJSON()
        {

            var json = Station.Connections.ToJSON();

            // Which certificates a connection may point at. Read from the
            // certificate store rather than remembered here, so that one
            // removed on the page next door is gone from this list too.
            json.Add(
                new JProperty("certificates",
                    new JArray(
                        Station.ClientCertificates.Entries.Select(entry =>
                            new JObject(
                                new JProperty("id",              entry.Id),
                                new JProperty("subject",         entry.Subject),
                                new JProperty("algorithm",       entry.Algorithm),
                                new JProperty("hasCertificate",  entry.Certificate is not null),
                                new JProperty("canBeHeldUp",     entry.CanBeHeldUp)
                            )))));

            return json;

        }

        /// <summary>
        /// POST /api/v1/configuration/authentications with
        /// {"description", "kind", "login", "secret", ...}: one set of
        /// credentials this station can prove itself with.
        /// </summary>
        /// <remarks>
        /// At the permission that changes how this station reaches the outside
        /// world, the same as the certificates and the name servers: a login
        /// for a back end is a statement about how this station gets to one,
        /// and nothing about what the equipment is or what it measures.
        /// </remarks>
        private Task<HTTPResponse> PostAuthentication(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryAddAuthentication(json.Value<String>("description"),
                                                           json.Value<String>("kind"),
                                                           json.Value<String>("login"),
                                                           json.Value<String>("secret"),
                                                           out var id,
                                                           out var error,
                                                           json.Value<Double?> ("validitySeconds"),
                                                           json.Value<UInt32?>("length"),
                                                           json.Value<String> ("alphabet"),
                                                           json.Value<String> ("hashAlgorithm"),
                                                           json.Value<Boolean?>("tlsChannelBinding")))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.Created,
                                    new JObject(
                                        new JProperty("id",          id),
                                        new JProperty("connections", ConnectionsJSON())
                                    ))
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/authentications/update with {"id", ...}.
        /// </summary>
        /// <remarks>
        /// A secret left out means the one already there stays. That is what
        /// makes it possible to correct a description without being shown the
        /// password - and being shown it is exactly what this station never
        /// does.
        /// </remarks>
        private Task<HTTPResponse> PostUpdateAuthentication(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryUpdateAuthentication(json.Value<String>("id"),
                                                              json.Value<String>("description"),
                                                              json.Value<String>("kind"),
                                                              json.Value<String>("login"),
                                                              json.Value<String>("secret"),
                                                              out var error,
                                                              json.Value<Double?> ("validitySeconds"),
                                                              json.Value<UInt32?>("length"),
                                                              json.Value<String> ("alphabet"),
                                                              json.Value<String> ("hashAlgorithm"),
                                                              json.Value<Boolean?>("tlsChannelBinding")))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/authentications/remove with {"id"}.
        /// </summary>
        /// <remarks>
        /// Refused while a connection is using it, and the refusal names the
        /// connection: the alternative leaves a station that cannot dial home
        /// and tells nobody why.
        /// </remarks>
        private Task<HTTPResponse> PostRemoveAuthentication(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryRemoveAuthentication(json.Value<String>("id"), out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/connections with
        /// {"description", "url", "connectionType", ...}: one place this
        /// station dials.
        /// </summary>
        private Task<HTTPResponse> PostConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryAddConnection(json.Value<String>("description"),
                                                       json.Value<String>("url"),
                                                       json.Value<String>("connectionType"),
                                                       json.Value<Boolean?>("autoConnect"),
                                                       json.Value<String>("authenticationId"),
                                                       json.Value<String>("certificateId"),
                                                       out var id,
                                                       out var error,
                                                       json.Value<String>("ocppVersion")))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.Created,
                                    new JObject(
                                        new JProperty("id",          id),
                                        new JProperty("connections", ConnectionsJSON())
                                    ))
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/connections/update with {"id", ...}.
        /// </summary>
        private Task<HTTPResponse> PostUpdateConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryUpdateConnection(json.Value<String>("id"),
                                                          json.Value<String>("description"),
                                                          json.Value<String>("url"),
                                                          json.Value<String>("connectionType"),
                                                          json.Value<Boolean?>("autoConnect"),
                                                          json.Value<String>("authenticationId"),
                                                          json.Value<String>("certificateId"),
                                                          out var error,
                                                          json.Value<String>("ocppVersion")))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/connections/remove with {"id"}.
        /// </summary>
        /// <remarks>
        /// The credentials it was using stay behind: they are frequently the
        /// ones whatever replaces it will use.
        /// </remarks>
        private Task<HTTPResponse> PostRemoveConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryRemoveConnection(json.Value<String>("id"), out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON())
                   );

        }

        #endregion

        #region (private) PostTestConnection(Request)

        /// <summary>
        /// POST /api/v1/configuration/connections/test with the fields of a
        /// connection: make it once, and say everything that happened.
        ///
        /// The fields rather than an identification, because the page offers
        /// this beside a connection being written down for the first time as
        /// well as beside one that already exists - and in both cases what
        /// somebody means by "test it" is what is on the screen. Nothing is
        /// stored either way.
        /// </summary>
        /// <remarks>
        /// At the diagnostics permission and not at the one that reads the
        /// configuration, for the same reason the name server and time server
        /// tests are: this makes the station open a connection to a host and
        /// show it a credential. That is more than it sounds like to hand to
        /// everybody who may look at a page, and it is less than changing what
        /// the station is configured to do - which is why it is not the
        /// network-settings permission either.
        ///
        /// It holds the request open for as long as the test takes, which is
        /// the connection plus about two seconds. The page's own deadline for
        /// a write is comfortably longer.
        /// </remarks>
        private async Task<HTTPResponse> PostTestConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.RunDiagnostics, true, out var session, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            Log.Info($"'{session.UserId}' asked this station to test a connection.", "ocpp", "connections", "test", "web");

            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Station.TestConnection(json.Value<String>("description"),
                                                    json.Value<String>("url"),
                                                    json.Value<String>("connectionType"),
                                                    json.Value<String>("ocppVersion"),
                                                    json.Value<Boolean?>("autoConnect") ?? false,
                                                    json.Value<String>("authenticationId"),
                                                    json.Value<String>("certificateId"),
                                                    Request.CancellationToken)
                   );

        }

        #endregion

        #region (private) GetClientCertificates(Request) / PostClientKey(Request) / PostClientCertificate(Request) / PostRemoveClientKey(Request)

        /// <summary>
        /// GET /api/v1/configuration/certificates: the keys and certificates
        /// this station holds up when it dials a back end.
        /// </summary>
        /// <remarks>
        /// Reading them needs no more than reading anything else here: a
        /// certificate is a signature over a public key, a signing request is
        /// meant to be handed to somebody, and the private keys are the one
        /// thing that never appears in this answer at all.
        /// </remarks>
        private Task<HTTPResponse> GetClientCertificates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ReadConfiguration, false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.ClientCertificates.ToJSON())
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/certificates with {"subject", "algorithm"}:
        /// a new key, and the signing request to be handed to a certificate
        /// authority.
        /// </summary>
        /// <remarks>
        /// At the same permission as the name servers and the time server, and
        /// for the same reason: this is a statement about how the station
        /// reaches the outside world. It is not about what the equipment is or
        /// may deliver, and it is not about calibration - a certificate here
        /// says who this station is to a back end, and nothing about what it
        /// measures.
        /// </remarks>
        private Task<HTTPResponse> PostClientKey(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryCreateKey(json.Value<String>("subject")   ?? "",
                                                         json.Value<String>("algorithm"),
                                                         out var id,
                                                         out var csr,
                                                         out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.Created,
                                    new JObject(
                                        new JProperty("id",           id),
                                        new JProperty("csr",          csr),
                                        new JProperty("certificates", Station.ClientCertificates.ToJSON())
                                    ))
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/certificates/import with {"pem"}: the
        /// certificate a certificate authority sent back, and whatever
        /// intermediates came with it.
        /// </summary>
        /// <remarks>
        /// What could not be used is refused here, while somebody is looking at
        /// the screen, rather than at the next handshake. What may legitimately
        /// look wrong from inside a station - a chain it cannot verify - comes
        /// back as a warning beside a certificate that was taken in.
        /// </remarks>
        private Task<HTTPResponse> PostClientCertificate(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryAddCertificate(json.Value<String>("pem") ?? "",
                                                              out var id,
                                                              out var warnings,
                                                              out var error))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));
            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK,
                                    new JObject(
                                        new JProperty("id",           id),
                                        new JProperty("warnings",     new JArray(warnings)),
                                        new JProperty("certificates", Station.ClientCertificates.ToJSON())
                                    ))
                   );

        }

        /// <summary>
        /// POST /api/v1/configuration/certificates/remove with {"id"}: take a
        /// key and everything belonging to it away, for good.
        /// </summary>
        /// <remarks>
        /// A POST rather than a DELETE because every other identification in
        /// this API travels in the body rather than in the path, and one route
        /// shaped differently from the rest is a route somebody gets wrong.
        /// </remarks>
        private Task<HTTPResponse> PostRemoveClientKey(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permissions.ChangeNetworkSettings, true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryRemove(json.Value<String>("id") ?? "", out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.ClientCertificates.ToJSON())
                   );

        }

        #endregion

        #region (private) GetLogs         (Request)

        /// <summary>
        /// GET /api/v1/logs?limit=&amp;after=&amp;tag=: what happened, oldest
        /// of the returned entries first.
        /// </summary>
        /// <remarks>
        /// This is the snapshot a browser loads before it starts following the
        /// event stream; "lastId" says how far it reaches, and everything the
        /// stream delivers with a greater id is new.
        /// </remarks>
        private Task<HTTPResponse> GetLogs(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var limit    = Request.QueryString.GetInt32 ("limit") ?? DefaultLogPageSize;
            var after    = Request.QueryString.GetUInt64("after");
            var tag      = Request.QueryString.GetString("tag");

            if (limit < 1 || limit > MaxLogPageSize)
                return Task.FromResult(
                           ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'limit' must be between 1 and {MaxLogPageSize}.")
                       );

            var entries  = Log.Recent(limit, after, tag).ToArray();

            return Task.FromResult(
                       JSONResponse(
                           Request,
                           HTTPStatusCode.OK,
                           new JObject(
                               // The whole log's last id and not the last of
                               // this page: a page filtered by a tag would
                               // otherwise make the browser ask again for
                               // everything between the two.
                               new JProperty("lastId",   Log.LastId),
                               new JProperty("capacity", Log.Capacity),
                               new JProperty("tags",     new JArray(Log.KnownTags)),
                               new JProperty("entries",  new JArray(entries.Select(entry => entry.ToJSON())))
                           )
                       )
                   );

        }

        #endregion


        #region (private) StreamEvents    (Request)

        /// <summary>
        /// GET /api/v1/events: the Server-Sent Events stream every browser
        /// hangs on. Modelled on Hermod's MapEventSource, with the session
        /// checked first and without opening the stream to other origins.
        /// </summary>
        private Task<HTTPResponse> StreamEvents(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            var clientId = Request.RemoteSocket.ToString();

            return Task.FromResult(
                       new HTTPResponse.Builder(Request) {

                           HTTPStatusCode  = HTTPStatusCode.OK,
                           Server          = HTTPServer.HTTPServerName,
                           ContentType     = HTTPContentType.Text.EVENTSTREAM,
                           CacheControl    = "no-cache",
                           Connection      = ConnectionType.KeepAlive,

                           HTTPSSEWorker   = async (response, stream) => {

                               // Either the browser going away or this station
                               // shutting down ends the stream. The second one
                               // is not something the request's own token knows
                               // about - see CloseEventStreams().
                               using var ending = CancellationTokenSource.CreateLinkedTokenSource(
                                                      Request.CancellationToken,
                                                      shutdown.Token
                                                  );

                               try
                               {

                                   await stream.WriteAsync("retry: ");
                                   await stream.WriteAsync(((UInt32) Events.RetryInterval.TotalMilliseconds).ToString());
                                   await stream.WriteAsync("\n\n");

                                   // The preamble has to leave the buffer now,
                                   // not with the first event: on a quiet
                                   // station the browser would otherwise wait
                                   // for its first byte until its own read
                                   // timeout expired.
                                   await stream.FlushAsync(ending.Token);

                                   await foreach (var httpEvent in Events.GetAllEventsGreater(
                                                                       clientId,
                                                                       Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                       ending.Token
                                                                   ))
                                   {
                                       await stream.WriteAsync(httpEvent.SerializedHeader);
                                       await stream.WriteAsync(httpEvent.SerializedData);
                                       await stream.WriteAsync("\n\n");
                                       await stream.FlushAsync(ending.Token);
                                   }

                               }
                               catch (OperationCanceledException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (ObjectDisposedException)
                               {
                                   await Events.Unsubscribe(clientId);
                               }
                               catch (Exception e)
                               {
                                   await Events.Unsubscribe(clientId);

                                   // Not through the event log: an event stream
                                   // that ends because the browser went away is
                                   // the normal end of one, and logging it here
                                   // would publish an event to the very streams
                                   // that are closing.
                                   System.Diagnostics.Debug.WriteLine($"The event stream of {clientId} ended: {e.Message}");
                               }

                           }

                       }.WithCommonSecurityHeaders().AsImmutable
                   );

        }

        #endregion

        #region CloseEventStreams()

        /// <summary>
        /// End every open event stream, so that the HTTP server can stop.
        /// </summary>
        /// <remarks>
        /// Called by <see cref="ChargingStation.Stop"/> before the servers are
        /// stopped, and not by the server itself: an event stream is a request
        /// that has been answered and is still being written to, and Hermod
        /// waits for every request it started before it reports itself stopped.
        /// Closing the socket underneath one does not wake it, because it is
        /// waiting for the next log entry and not for the network - so without
        /// this, a station with one browser on its Logs page never finishes
        /// shutting down.
        ///
        /// The browsers see the connection end and reconnect by themselves;
        /// that is what the retry interval of the stream is for.
        /// </remarks>
        public void CloseEventStreams()
        {

            if (!shutdown.IsCancellationRequested)
                shutdown.Cancel();

        }

        #endregion

        #region (private) UnknownPath     (Request)

        private Task<HTTPResponse> UnknownPath(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(
                       Request,
                       HTTPStatusCode.NotFound,
                       new JObject(
                           new JProperty("error",  "Unknown API path"),
                           new JProperty("path",   Request.Path.ToString())
                       )
                   )
               );

        #endregion


        #region (private) Publish(SubEvent, JSON)

        /// <summary>
        /// Hands an event to every browser. Fire-and-forget on purpose: this is
        /// called from inside whatever wrote the log entry, and none of those
        /// should wait for a slow browser.
        /// </summary>
        private void Publish(String   SubEvent,
                             JObject  JSON)
        {

            Events.SubmitEvent(SubEvent, JSON).
                   ContinueWith(task => System.Diagnostics.Debug.WriteLine($"Publishing a '{SubEvent}' event failed: {task.Exception?.GetBaseException().Message}"),
                                TaskContinuationOptions.OnlyOnFaulted);

        }

        #endregion

        #region (private) TryGetSession(Request, out Session, out Unauthorized)

        /// <summary>
        /// The live session behind the request, or the 401 response - which
        /// also expires a stale cookie, so that the browser stops sending it.
        /// </summary>
        private Boolean TryGetSession(HTTPRequest                             Request,
                                      [NotNullWhen(true)]  out Session?        Session,
                                      [NotNullWhen(false)] out HTTPResponse?  Unauthorized)
        {

            if (Sessions.TryGetSession(Request, out Session))
            {
                Unauthorized = null;
                return true;
            }

            var builder = new HTTPResponse.Builder(Request) {
                              HTTPStatusCode  = HTTPStatusCode.Unauthorized,
                              ContentType     = HTTPContentType.Application.JSON_UTF8,
                              Content         = Encoding.UTF8.GetBytes(new JObject(new JProperty("error", "Sign in required.")).ToString(Formatting.None)),
                              CacheControl    = "no-store"
                          };

            if (Sessions.HasCookie(Request))
                builder.SetCookie = Sessions.ExpiredCookie();

            Unauthorized = builder.WithCommonSecurityHeaders().AsImmutable;
            return false;

        }

        #endregion

        #region (private) TryAuthorize(Request, Required, StateChanging, out Session, out Refused)

        /// <summary>
        /// The live session behind the request, when it is allowed to do this -
        /// or the response that says why not.
        /// </summary>
        /// <remarks>
        /// Three refusals, in the order they have to happen: a request from
        /// another site is turned away before it is read at all, a request
        /// without a session is a 401 that also expires a stale cookie, and a
        /// request from somebody signed in who may not do this is a 403 naming
        /// the permission they are short of and the roles that carry it. The
        /// difference between the last two matters to a browser: 401 means sign
        /// in again, 403 means signing in again will not help.
        /// </remarks>
        /// <param name="Request">The request.</param>
        /// <param name="Required">What this request needs permission to do.</param>
        /// <param name="StateChanging">Whether it changes something, and is therefore also checked for being cross-site.</param>
        /// <param name="Session">The session behind it.</param>
        /// <param name="Refused">The response to send instead.</param>
        private Boolean TryAuthorize(HTTPRequest                             Request,
                                     Permissions                             Required,
                                     Boolean                                 StateChanging,
                                     [NotNullWhen(true)]  out Session?       Session,
                                     [NotNullWhen(false)] out HTTPResponse?  Refused)
        {

            Session = null;

            if (StateChanging && RefuseCrossSite(Request) is HTTPResponse crossSite)
            {
                Refused = crossSite;
                return false;
            }

            if (!TryGetSession(Request, out Session, out Refused))
                return false;

            var permissions = Sessions.PermissionsOf(Session);

            if (!permissions.HasFlag(Required))
            {
                Refused  = RefusePermission(Request, Session, Required, null);
                Session  = null;
                return false;
            }

            Refused = null;
            return true;

        }

        #endregion

        #region (private) RefusePermission(Request, Session, Required, Because)

        /// <summary>
        /// The 403 for somebody signed in who may not do this, naming the roles
        /// that carry the permission they are short of.
        /// </summary>
        /// <remarks>
        /// Its own method because it is needed twice: once before a request is
        /// read, and once after - a change to the EVSEs cannot be judged until
        /// it has been compared with what the station has, so that refusal
        /// happens with the body already parsed. Both say the same sentence,
        /// and both leave the same line in the log.
        /// </remarks>
        /// <param name="Because">What it was about this particular request, when the route alone does not say.</param>
        private HTTPResponse RefusePermission(HTTPRequest  Request,
                                              Session      Session,
                                              Permissions  Required,
                                              String?      Because)
        {

            // HasFlag with more than one flag asks for all of them, which is
            // what a role has to carry to do a change that was several kinds at
            // once. Nobody is named who could only do half of it.
            var allowed = UserRole.All.Where(role => role.Permissions.HasFlag(Required)).
                                       Select(role => role.Name);

            Log.Warning(
                $"'{Session.UserId}' was refused {Required} on {Request.HTTPMethod} {Request.Path}; " +
                $"signed in as {String.Join(", ", Sessions.Roles.Select(role => role.Name))}." +
                (Because is null ? "" : $" {Because}"),
                "web", "auth"
            );

            return ErrorJSON(
                       Request,
                       HTTPStatusCode.Forbidden,
                       (Because is null ? "" : Because + " ") +
                       $"This needs the {String.Join(" or ", allowed)} role."
                   );

        }

        #endregion

        #region (private static) RefuseCrossSite(Request)

        /// <summary>
        /// The 403 for a request that another site made the browser send, or
        /// null when the request is our own page's.
        /// </summary>
        /// <remarks>
        /// The cookie is SameSite=strict, so a cross-site request would arrive
        /// without a session anyway. This is the second lock on the same door:
        /// browsers say where a request came from (Sec-Fetch-Site, Origin), and
        /// a state-changing request from anywhere but this origin is refused
        /// before it is even read.
        /// </remarks>
        private static HTTPResponse? RefuseCrossSite(HTTPRequest Request)
        {

            var site = Request.GetHeaderField("Sec-Fetch-Site");

            if (site is not null && site is not ("same-origin" or "none"))
                return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");

            var origin = Request.GetHeaderField("Origin");

            if (origin is not null && origin != "null")
            {

                var host = Request.GetHeaderField("Host") ?? "";

                if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                    !uri.Authority.Equals(host, StringComparison.OrdinalIgnoreCase))
                {
                    return ErrorJSON(Request, HTTPStatusCode.Forbidden, "Cross-site requests are refused.");
                }

            }

            return null;

        }

        #endregion

        #region (private static) TryParseJSONObject(Request, out JSON, out ErrorResponse)

        /// <summary>
        /// The request body as a JSON object, or the 400 response describing
        /// what is wrong with it.
        /// </summary>
        private static Boolean TryParseJSONObject(HTTPRequest                             Request,
                                                  [NotNullWhen(true)]  out JObject?       JSON,
                                                  [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            var text = Request.HTTPBodyAsUTF8String;

            if (String.IsNullOrWhiteSpace(text))
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "The request body must be a JSON object!");
                return false;
            }

            try
            {
                JSON = JObject.Parse(text);
                return true;
            }
            catch (JsonException e)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"Invalid JSON: {e.Message}");
                return false;
            }

        }

        #endregion

        #region (private) MeJSON(Session)

        /// <summary>
        /// Who is signed in, and what they may do.
        /// </summary>
        /// <remarks>
        /// The permissions travel to the browser so that a page can grey out
        /// what this person may not do, rather than offering it and letting
        /// them find out by being refused. They are a copy of what the station
        /// enforces and not the enforcement: every request is checked again on
        /// arrival, so a browser that edits this list gains nothing but a
        /// button that answers 403.
        /// </remarks>
        private JObject MeJSON(Session Session)

            => new (
                   new JProperty("username",     Session.UserId.ToString()),
                   new JProperty("roles",        new JArray(Sessions.Roles.Select(role => role.Name))),
                   new JProperty("permissions",  new JArray(Sessions.PermissionsOf(Session).Names())),
                   new JProperty("session",      new JObject(
                                                     new JProperty("createdAt",  Session.CreatedAt.ToString("o")),
                                                     new JProperty("expiresAt",  Session.ExpiresAt.ToString("o"))
                                                 ))
               );

        #endregion

        #region (private static) ErrorJSON(...) / JSONResponse(...)

        private static HTTPResponse ErrorJSON(HTTPRequest     Request,
                                              HTTPStatusCode  StatusCode,
                                              String          Message)

            => JSONResponse(
                   Request,
                   StatusCode,
                   new JObject(new JProperty("error", Message))
               );


        private static HTTPResponse JSONResponse(HTTPRequest     Request,
                                                 HTTPStatusCode  StatusCode,
                                                 JToken          JSON)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(JSON.ToString(Formatting.None)),
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders().AsImmutable;

        #endregion

    }

}
