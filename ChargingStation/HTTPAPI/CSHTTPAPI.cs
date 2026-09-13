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

            AddHandler(HTTPPath.Root + "v1/configuration/dns", GetDNSConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/dns", PutDNSConfiguration, HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/nts", GetNTSConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/nts", PutNTSConfiguration, HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/evses", GetEVSEConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/evses", PutEVSEConfiguration, HTTPMethod.PUT);
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

            Log.Notice($"'{session.UserId}' signed in from {Request.RemoteSocket}.", "web", "auth");

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

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

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

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

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

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateDNSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DNSConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetNTSConfiguration(Request) / PutNTSConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/nts: where this station gets the time from.
        /// </summary>
        private Task<HTTPResponse> GetNTSConfiguration(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NTSConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/nts: change what may be changed about it.
        /// </summary>
        private Task<HTTPResponse> PutNTSConfiguration(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateNTSConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NTSConfigurationJSON())
                   );

        }

        #endregion

        #region (private) GetEVSEConfiguration(Request) / PutEVSEConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/evses: the EVSEs of this charging station.
        /// </summary>
        private Task<HTTPResponse> GetEVSEConfiguration(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.EVSEConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/evses with {"evses": [...]}: replace them all.
        /// </summary>
        private Task<HTTPResponse> PutEVSEConfiguration(HTTPRequest Request)
        {

            if (!TryGetSession(Request, out _, out var unauthorized))
                return Task.FromResult(unauthorized);

            if (RefuseCrossSite(Request) is HTTPResponse refused)
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateEVSEConfiguration(json, out var error))
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, error));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.EVSEConfigurationJSON())
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
                                   await stream.FlushAsync(Request.CancellationToken);

                                   await foreach (var httpEvent in Events.GetAllEventsGreater(
                                                                       clientId,
                                                                       Request.GetHeaderField(HTTPRequestHeaderField.LastEventId),
                                                                       Request.CancellationToken
                                                                   ))
                                   {
                                       await stream.WriteAsync(httpEvent.SerializedHeader);
                                       await stream.WriteAsync(httpEvent.SerializedData);
                                       await stream.WriteAsync("\n\n");
                                       await stream.FlushAsync(Request.CancellationToken);
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

        #region (private static) MeJSON(Session)

        private static JObject MeJSON(Session Session)

            => new (
                   new JProperty("username",  Session.UserId.ToString()),
                   new JProperty("session",   new JObject(
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
