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
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.ChargingStation.Logging;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The display on the front of the charging station: a page with no sign-in
    /// behind it, on a TCP port of its own.
    /// </summary>
    /// <remarks>
    /// **Why a second server and not a second page.** Everything here is public
    /// by design, and the machine it is shown on is a screen bolted to a
    /// charging station in a car park. Somebody with a keyboard, a USB port or
    /// a way out of a browser's full-screen mode is standing at that machine,
    /// and whatever that machine can reach, they can reach.
    ///
    /// A page on the main server would mean the display and the administration
    /// share an origin: the sign-in form, the session cookie and every
    /// configuration route are one URL away from the screen, and keeping them
    /// apart would rest on every route being correctly gated, for ever, by
    /// everybody who adds one. A second listener makes it a property of the
    /// deployment instead of a property of the code - and, the part that
    /// actually matters, it can be bound to a different address: the display on
    /// the loopback interface or the screen's own network, the administration
    /// on the maintenance side, neither reachable from the other's wire. That
    /// is a sentence somebody can test with a port scanner, which "we checked
    /// the handlers" is not.
    ///
    /// The cost is one more socket. That is the whole of it: the same process,
    /// the same station object, the same log.
    ///
    /// **What it can do.** Read everything on the display, and two writes -
    /// starting or stopping a charge, and letting a reservation go - both of
    /// which need a card held against a reader whose cards are typed in, the
    /// fake one. A station with only real readers configured has no write here
    /// at all. There is no sign-in, no session and no cookie, so there is
    /// nothing here to steal and nothing to be tricked into doing on somebody
    /// else's behalf: what you may do here is what you can hold up, not who you
    /// say you are.
    /// </remarks>
    public class KioskHTTPAPI : HTTPAPI
    {

        #region Data

        /// <summary>
        /// Where this API lives on its server.
        /// </summary>
        public static readonly HTTPPath  DefaultAPIPath  = HTTPPath.Parse("/api");

        /// <summary>
        /// The page of the bundle this server hands out. The same bundle the
        /// web interface is built from, a second entry point in it.
        /// </summary>
        public const String      IndexFile       = "kiosk.html";

        /// <summary>
        /// The most a card may be sent as, so that a screen in public cannot
        /// be used to hand this station a megabyte.
        /// </summary>
        public const UInt32      MaxRequestSize  = 4 * 1024;

        #endregion

        #region Properties

        /// <summary>
        /// The charging station this is the front of.
        /// </summary>
        public ChargingStation  Station  { get; }

        /// <summary>
        /// Everything that happens inside it.
        /// </summary>
        public EventLog         Log      { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Attach the display API to the given (separate) HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The server of the display, which is not the server of the web interface.</param>
        /// <param name="Station">The charging station.</param>
        /// <param name="Log">The event log.</param>
        /// <param name="RootPath">Where this API lives, "/api" by default.</param>
        public KioskHTTPAPI(HTTPServer       HTTPServer,
                            ChargingStation  Station,
                            EventLog         Log,
                            HTTPPath?        RootPath   = null)

            : base(HTTPServer,
                   RootPath:     RootPath ?? DefaultAPIPath,
                   Description:  I18NString.Create("The display of this charging station"))

        {

            this.Station  = Station;
            this.Log      = Log;

            AddHandler(HTTPPath.Root + "kiosk",                    GetKiosk,          HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "kiosk/rfid",               PostRFIDCard,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "kiosk/reservation/cancel", PostCancelHold,    HTTPMethod.POST);

        }

        #endregion


        #region (private) GetKiosk(Request)

        /// <summary>
        /// GET /api/kiosk: everything on the display, in one answer.
        /// </summary>
        private Task<HTTPResponse> GetKiosk(HTTPRequest Request)

            => Task.FromResult(
                   JSONResponse(Request, HTTPStatusCode.OK, Station.KioskJSON())
               );

        #endregion

        #region (private) PostRFIDCard(Request)

        /// <summary>
        /// POST /api/kiosk/rfid with {"uid": "...", "reader": "...", "evse": 1}:
        /// hold a card against a reader whose cards are typed in.
        /// </summary>
        /// <remarks>
        /// The only thing this server changes anything with, and it can only
        /// ever reach a fake reader - the station refuses the rest, and it
        /// refuses it by what the reader is rather than by who is asking, which
        /// is the only kind of rule that holds on a port with no sign-in.
        /// </remarks>
        private async Task<HTTPResponse> PostRFIDCard(HTTPRequest Request)
        {

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Station.TryPresentToken(json.Value<String>("reader"),
                                         json["evse"]?.Type == JTokenType.Integer ? (Byte?) json.Value<Byte>("evse") : null,
                                         json.Value<String>("uid"),
                                         out var result,
                                         out var error))
            {
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);
            }

            await Task.CompletedTask;

            return JSONResponse(Request, HTTPStatusCode.OK, result);

        }

        #endregion


        #region (private) PostCancelHold(Request)

        /// <summary>
        /// POST /api/kiosk/reservation/cancel with {"uid": "...", "evse": 1}:
        /// let a held outlet go again.
        /// </summary>
        /// <remarks>
        /// The card is the whole of the authorisation, because at a screen with
        /// no sign-in in front of it there is nothing else anybody can prove.
        /// Anybody may press the button; only the card the outlet is held for
        /// gets anywhere, and the display never showed which card that is.
        /// </remarks>
        private async Task<HTTPResponse> PostCancelHold(HTTPRequest Request)
        {

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Station.TryReleaseReservation(json.Value<String>("reader"),
                                               json["evse"]?.Type == JTokenType.Integer ? (Byte?) json.Value<Byte>("evse") : null,
                                               json.Value<String>("uid"),
                                               out var result,
                                               out var error))
            {
                return ErrorJSON(Request, HTTPStatusCode.BadRequest, error);
            }

            await Task.CompletedTask;

            return JSONResponse(Request, HTTPStatusCode.OK, result);

        }

        #endregion

        #region (private static) TryParseJSONObject(Request, out JSON, out ErrorResponse)

        /// <summary>
        /// The body as a JSON object, or the answer that says why not.
        /// </summary>
        private static Boolean TryParseJSONObject(HTTPRequest                             Request,
                                                  [NotNullWhen(true)]  out JObject?       JSON,
                                                  [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            var body = Request.HTTPBody;

            if (body is null || body.Length == 0)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "A JSON object is expected.");
                return false;
            }

            if (body.Length > MaxRequestSize)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"At most {MaxRequestSize} bytes, please.");
                return false;
            }

            try
            {
                JSON = JObject.Parse(Encoding.UTF8.GetString(body));
            }
            catch (Exception e)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, $"Invalid JSON: {e.Message}");
                return false;
            }

            return true;

        }

        #endregion

        #region (private static) JSONResponse(Request, Status, JSON) / ErrorJSON(...)

        /// <summary>
        /// A JSON answer that no cache anywhere may keep.
        /// </summary>
        /// <remarks>
        /// A display shows what is true now. A proxy between the screen and the
        /// station holding on to "EVSE 2 is free" for a minute is the one
        /// failure this page must not have.
        /// </remarks>
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
        private static HTTPResponse ErrorJSON(HTTPRequest     Request,
                                              HTTPStatusCode  Status,
                                              String?         Message)

            => JSONResponse(
                   Request,
                   Status,
                   new JObject(new JProperty("error", Message ?? "Something went wrong."))
               );

        #endregion

    }

}
