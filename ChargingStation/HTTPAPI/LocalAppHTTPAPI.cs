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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The local app server: where an app on a phone in the station's own
    /// network starts and stops a charge, on a TCP port of its own.
    /// </summary>
    /// <remarks>
    /// **Why a third server.** For the reason the display has a second one -
    /// see <see cref="KioskHTTPAPI"/> - and more so. The app reaches this over
    /// the station's own network, which may be an open WLAN that anybody in
    /// range can join. The administration must never be one URL away from that
    /// network, and a listener of its own, bound to the WLAN's address while
    /// the administration stays on the maintenance side, is a sentence a port
    /// scanner can check. So it follows neither the web interface's address nor
    /// the display's, and a station has none of it until it is given a port.
    ///
    /// **What it can do.** Two things, through either of two doors:
    /// <list type="bullet">
    ///   <item>POST /localStart with {"evse": 1, "uid": "04A2B3C4"} starts a
    ///   session with a card's UID, as a card held against a reader would, and
    ///   answers with the handle to stop it with.</item>
    ///   <item>POST /localStop/{SessionId} stops the session that handle was
    ///   given for.</item>
    ///   <item>The WebSocket /localApp takes the same two as
    ///   {"action": "start", ...} and {"action": "stop", "sessionId": "..."},
    ///   and answers each with what the POST would have answered, its status
    ///   included - one piece of code behind both doors.</item>
    /// </list>
    /// Nothing else: nothing of what the station is or how it is configured,
    /// no sign-in, no cookie.
    ///
    /// **What it cannot do yet.** Tell who is holding the UID up. This station
    /// does not check a card's UID either, but a card has to be at the reader;
    /// on an open WLAN, anybody in range can send any UID. A one-time password,
    /// a certificate, a challenge and a response are what will make it more
    /// than that. Until they are here, a request that carries one is refused
    /// rather than served as if it had been checked.
    /// </remarks>
    public class LocalAppHTTPAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// Where the WebSocket is, on the same port as the two POSTs.
        /// </summary>
        public static readonly HTTPPath  WebSocketPath   = HTTPPath.Parse("/localApp");

        /// <summary>
        /// The most a request may be, over either door, so that a network
        /// anybody can join cannot be used to hand this station a megabyte.
        /// </summary>
        public const UInt32              MaxRequestSize  = 4 * 1024;

        /// <summary>
        /// The WebSocket's protocol, which the HTTP server borrows for one
        /// path. Never started: it accepts nothing on a port of its own.
        /// </summary>
        private readonly LocalAppWebSocketServer webSocketServer;

        #endregion

        #region Properties

        /// <summary>
        /// The charging station an app starts and stops charges at.
        /// </summary>
        public ChargingStation  Station  { get; }

        /// <summary>
        /// Everything that happens inside it.
        /// </summary>
        public EventLog         Log      { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Attach the local app API to the given (separate) HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The server of the local app, which is neither the web interface's nor the display's.</param>
        /// <param name="Station">The charging station.</param>
        /// <param name="Log">The event log.</param>
        public LocalAppHTTPAPI(HTTPServer       HTTPServer,
                               ChargingStation  Station,
                               EventLog         Log)

            : base(HTTPServer,
                   RootPath:     HTTPPath.Root,
                   Description:  I18NString.Create("The local app server of this charging station"))

        {

            this.Station          = Station;
            this.Log              = Log;
            this.webSocketServer  = new LocalAppWebSocketServer(this);

            AddHandler(HTTPPath.Parse("/localStart"),             PostLocalStart,                         HTTPMethod.POST);
            AddHandler(HTTPPath.Parse("/localStop/{SessionId}"),  PostLocalStop,                          HTTPMethod.POST);
            AddHandler(WebSocketPath,                             WebSocketUpgrade.For(webSocketServer),  HTTPMethod.GET);

            // Everything else is somewhere this server is not. Said in JSON,
            // because what asks here is an app, and an app reads JSON.
            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD, HTTPMethod.POST, HTTPMethod.PUT, HTTPMethod.DELETE })
                AddHandler(HTTPPath.Parse("/{path..}"), UnknownPath, method);

        }

        #endregion


        #region CloseWebSockets()

        /// <summary>
        /// Say goodbye to every app on the WebSocket, before the server stops.
        /// </summary>
        /// <remarks>
        /// Stopping the HTTP server closes their sockets anyway; this is so that
        /// each app is told with a close frame - 1001, going away, and this
        /// sentence as its reason - that the station went, rather than finding
        /// out from a connection that broke.
        /// </remarks>
        public Task CloseWebSockets()
            => webSocketServer.Shutdown("The charging station is stopping.");

        #endregion

        #region (static) Loggable(Path)

        /// <summary>
        /// A path as it may be written down: with the handle of a stop left
        /// out, because whoever reads it could end that session with it.
        /// </summary>
        /// <param name="Path">The path of a request to this server.</param>
        public static String Loggable(HTTPPath Path)
        {

            var path = Path.ToString();

            return path.StartsWith("/localStop/", StringComparison.OrdinalIgnoreCase)
                       ? "/localStop/{SessionId}"
                       : path;

        }

        #endregion


        #region (private) PostLocalStart(Request)

        /// <summary>
        /// POST /localStart with {"evse": 1, "uid": "04A2B3C4"}: start a session
        /// with a card's UID.
        /// </summary>
        private Task<HTTPResponse> PostLocalStart(HTTPRequest Request)
        {

            var body = Request.HTTPBody;

            if (body is null || body.Length == 0)
                return Task.FromResult(JSONResponse(Request, HTTPStatusCode.BadRequest, Refused("A JSON object is expected.")));

            if (body.Length > MaxRequestSize)
                return Task.FromResult(JSONResponse(Request, HTTPStatusCode.BadRequest, Refused($"At most {MaxRequestSize} bytes, please.")));

            if (!TryParseJSONObject(Encoding.UTF8.GetString(body), out var json, out var problem))
                return Task.FromResult(JSONResponse(Request, HTTPStatusCode.BadRequest, Refused(problem)));

            var (status, answer) = Start(json);

            return Task.FromResult(JSONResponse(Request, status, answer));

        }

        #endregion

        #region (private) PostLocalStop(Request)

        /// <summary>
        /// POST /localStop/{SessionId}: stop the session that handle was given
        /// for.
        /// </summary>
        private Task<HTTPResponse> PostLocalStop(HTTPRequest Request)
        {

            Request.TryGetURLParameter("SessionId", out var sessionId);

            var (status, answer) = Stop(sessionId);

            return Task.FromResult(JSONResponse(Request, status, answer));

        }

        #endregion

        #region (private) UnknownPath(Request)

        private Task<HTTPResponse> UnknownPath(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(
                       Request,
                       HTTPStatusCode.NotFound,
                       new JObject(
                           new JProperty("error",  "Unknown path. This server knows POST /localStart, POST /localStop/{SessionId} and the WebSocket /localApp."),
                           new JProperty("path",   Request.Path.ToString())
                       )
                   )
               );

        #endregion


        #region (private) Start(Request) / Stop(SessionId)

        /// <summary>
        /// A start, as the app sends it through either door: what to answer,
        /// and with which status.
        /// </summary>
        private (HTTPStatusCode Status, JObject Answer) Start(JObject Request)
        {

            // Refused rather than ignored. An app that sends a one-time password
            // believes it is being checked, and a start that went ahead on the
            // UID alone would let it go on believing that.
            foreach (var (field, what) in new[] { ("totp", "A one-time password"), ("certificate", "A certificate") })
                if (Request.ContainsKey(field))
                    return (HTTPStatusCode.NotImplemented,
                            Refused($"{what} is not checked by this station yet, and a start that took it along unchecked would be pretending otherwise. Send the UID alone."));

            Byte? evse = null;

            if (Request["evse"] is JToken given && given.Type != JTokenType.Null)
            {

                if (given.Type != JTokenType.Integer || given.Value<Int64>() is < Byte.MinValue or > Byte.MaxValue)
                    return (HTTPStatusCode.BadRequest, Refused("\"evse\" is the number of an EVSE of this station."));

                evse = (Byte) given.Value<Int64>();

            }

            if (!Station.TryStartLocally(evse,
                                         Request["uid"]?.Type == JTokenType.String ? Request.Value<String>("uid") : null,
                                         out var result,
                                         out var refusal,
                                         out var error))
            {
                return (StatusOf(refusal), Refused(error));
            }

            return (HTTPStatusCode.OK, result);

        }

        /// <summary>
        /// A stop, as the app sends it through either door: what to answer,
        /// and with which status.
        /// </summary>
        private (HTTPStatusCode Status, JObject Answer) Stop(String? SessionId)
        {

            if (!Station.TryStopLocally(SessionId,
                                        out var result,
                                        out var refusal,
                                        out var error))
            {
                return (StatusOf(refusal), Refused(error));
            }

            return (HTTPStatusCode.OK, result);

        }

        /// <summary>
        /// What each refusal is called in HTTP.
        /// </summary>
        private static HTTPStatusCode StatusOf(LocalAppRefusal Refusal)

            => Refusal switch {
                   LocalAppRefusal.Unknown  => HTTPStatusCode.NotFound,
                   LocalAppRefusal.Busy     => HTTPStatusCode.Conflict,
                   LocalAppRefusal.NotYet   => HTTPStatusCode.NotImplemented,
                   _                        => HTTPStatusCode.BadRequest
               };

        #endregion

        #region (private) Answer(Message)

        /// <summary>
        /// A message on the WebSocket, answered with what the POST it stands
        /// for would have answered.
        /// </summary>
        /// <remarks>
        /// {"action": "start", "evse": 1, "uid": "04A2B3C4"} is POST /localStart
        /// with the rest of the object as its body, {"action": "stop",
        /// "sessionId": "..."} is POST /localStop/{SessionId}. The answer is the
        /// POST's answer with the action and the HTTP status in front, and with
        /// the "id" of the message, when it had one, so that an app with more
        /// than one question on the way can tell the answers apart.
        /// </remarks>
        private JObject Answer(String Message)
        {

            if (!TryParseJSONObject(Message, out var json, out var problem))
                return WebSocketAnswer(null, null, HTTPStatusCode.BadRequest, Refused(problem));

            var id      = json["id"];
            var action  = json["action"]?.Type == JTokenType.String ? json.Value<String>("action") : null;

            var (status, answer) = action switch {
                                       "start"  => Start(json),
                                       "stop"   => Stop(json["sessionId"]?.Type == JTokenType.String ? json.Value<String>("sessionId") : null),
                                       _        => (HTTPStatusCode.BadRequest, Refused("Say what to do: \"action\" is \"start\" or \"stop\"."))
                                   };

            return WebSocketAnswer(id, action, status, answer);

        }

        private static JObject WebSocketAnswer(JToken?         Id,
                                               String?         Action,
                                               HTTPStatusCode  Status,
                                               JObject         Answer)
        {

            var message = new JObject();

            if (Id is not null)
                message.Add("id",      Id.DeepClone());

            if (Action is not null)
                message.Add("action",  Action);

            message.Add("status", Status.Code);

            foreach (var property in Answer.Properties())
                message.Add(property.Name, property.Value.DeepClone());

            return message;

        }

        #endregion

        #region (private static) TryParseJSONObject(Text, out JSON, out Problem)

        /// <summary>
        /// The text as a JSON object, or the sentence saying why not.
        /// </summary>
        private static Boolean TryParseJSONObject(String                            Text,
                                                  [NotNullWhen(true)]  out JObject? JSON,
                                                  [NotNullWhen(false)] out String?  Problem)
        {

            JSON     = null;
            Problem  = null;

            try
            {
                JSON = JObject.Parse(Text);
                return true;
            }
            catch (Exception e)
            {
                Problem = $"Invalid JSON: {e.Message}";
                return false;
            }

        }

        #endregion

        #region (private static) JSONResponse(Request, Status, JSON) / Refused(Message)

        /// <summary>
        /// A JSON answer that no cache anywhere may keep.
        /// </summary>
        private static HTTPResponse JSONResponse(HTTPRequest     Request,
                                                 HTTPStatusCode  Status,
                                                 JObject         JSON)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = Status,
                   ContentType     = HTTPContentType.Application.JSON_UTF8,
                   Content         = Encoding.UTF8.GetBytes(JSON.ToString(Newtonsoft.Json.Formatting.None)),
                   CacheControl    = "no-store",
                   Connection      = ConnectionType.KeepAlive
               }.AsImmutable;

        /// <summary>
        /// One sentence saying what is wrong.
        /// </summary>
        private static JObject Refused(String? Message)
            => new (new JProperty("error", Message ?? "Something went wrong."));

        #endregion


        #region (private class) LocalAppWebSocketServer

        /// <summary>
        /// The WebSocket at /localApp: every text message is a start or a stop,
        /// answered by the same code that answers the POSTs.
        /// </summary>
        /// <remarks>
        /// Never started, and that is the whole idea: it never accepts anything
        /// on a port of its own. The HTTP server hands it the connections that
        /// ask to be upgraded at /localApp and lets it speak the protocol.
        ///
        /// No authentication, stated rather than assumed: the base class would
        /// otherwise say it requires some and check none. What a message may do
        /// is what it holds up, as on the POSTs.
        /// </remarks>
        private sealed class LocalAppWebSocketServer : AWebSocketServer
        {

            private readonly LocalAppHTTPAPI api;

            public LocalAppWebSocketServer(LocalAppHTTPAPI API)

                : base(TCPPort:                IPPort.Parse(0),
                       HTTPServerName:         "OpenChargingCloud ChargingStation Local App",
                       RequireAuthentication:  false,
                       AutoStart:              false)

            {

                this.api                  = API;

                // More than this and the connection is closed with "message too
                // big", before anything reads it.
                this.MaxTextMessageSizeIn = MaxRequestSize;

            }

            public override async Task ProcessTextMessage(DateTimeOffset             Timestamp,
                                                          AWebSocketServer           Server,
                                                          WebSocketServerConnection  Connection,
                                                          EventTracking_Id           EventTrackingId,
                                                          WebSocketFrame             TextFrame,
                                                          String                     TextMessage,
                                                          CancellationToken          CancellationToken)
            {

                // Quick enough to answer in the connection's own loop: a start
                // and a stop are both a look at a dictionary and a log line.
                var answer = api.Answer(TextMessage);

                await SendTextMessage(Connection,
                                      answer.ToString(Newtonsoft.Json.Formatting.None),
                                      EventTrackingId,
                                      CancellationToken);

            }

        }

        #endregion

    }

}
