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
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The JSON API the browser talks to, registered at "/api": what every
    /// node has - see <see cref="NodeHTTPAPI"/> - and what only a charging
    /// station has: its EVSEs and their display, its connections and how it
    /// authenticates on them, power, calibration, V2G and RFID, reservations,
    /// sessions and the messages on its screen.
    /// </summary>
    /// <remarks>
    /// The sign-in, the configuration, name resolution and the time servers,
    /// the certificate store, the log and the event stream are the node's,
    /// the same on every kind of node; this class used to have its own copy
    /// of all of them. The kiosk's API and the local app's are not this one,
    /// and stay the station's own.
    /// </remarks>
    public class CSHTTPAPI : NodeHTTPAPI
    {

        #region Properties

        /// <summary>
        /// The charging station this API speaks for.
        /// </summary>
        public ChargingStation  Station  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create and register the JSON API within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Station">The charging station this API speaks for.</param>
        /// <param name="ExtAPI">The accounts and the groups they are in.</param>
        /// <param name="Log">Everything that happens inside this charging station.</param>
        /// <param name="APIPath">The root path of the API, "/api" by default.</param>
        /// <param name="Version">The version reported by the status resource.</param>
        public CSHTTPAPI(HTTPServer       HTTPServer,
                         ChargingStation  Station,
                         HTTPExtAPI       ExtAPI,
                         EventLog         Log,
                         HTTPPath?        APIPath   = null,
                         String?          Version   = null)

            : base(HTTPServer,
                   Station,
                   ExtAPI,
                   Log,
                   APIPath,
                   Version ?? typeof(CSHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.Station = Station;

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        /// <summary>
        /// What only a charging station has.
        /// </summary>
        private void RegisterURLTemplates()
        {

            AddHandler(HTTPPath.Root + "v1/status/connections", GetConnectionStates, HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        GetV2GConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        PutV2GConfiguration,   HTTPMethod.PUT);

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

        }

        #endregion


        #region (private) GetEVSEConfiguration(Request) / PutEVSEConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/display: the quiet hours the screen keeps.
        /// </summary>
        private Task<HTTPResponse> GetDisplayConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Display), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Display), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateDisplayConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.DisplayConfigurationJSON())
                   );

        }

        /// <summary>
        /// GET /api/v1/configuration/evses: the EVSEs of this charging station.
        /// </summary>
        private Task<HTTPResponse> GetEVSEConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.EVSEs), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.EVSEs), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateEVSEConfiguration(json,
                                                    change => Station.IsAllowed(user, PermissionsFor(change)),
                                                    out var change,
                                                    out var error,
                                                    out var forbidden,
                                                    out var notSaved))
            {
                return Task.FromResult(
                           forbidden
                               ? RefusePermission(Request, user, PermissionsFor(change), error)
                               : NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved)
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
        private static IReadOnlyCollection<Permission> PermissionsFor(EVSEChange Change)
        {

            var permissions = new List<Permission>();

            if (Change.HasFlag(EVSEChange.Availability))  permissions.Add(Permission.Edit(StationAccess.Availability));
            if (Change.HasFlag(EVSEChange.PowerLimits))   permissions.Add(Permission.Edit(StationAccess.Power));
            if (Change.HasFlag(EVSEChange.Hardware))      permissions.Add(Permission.Edit(StationAccess.EVSEs));

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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Display), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Display), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Display), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Session), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Session), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Session), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Session), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Session), true, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.RFID), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.RFID), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateRFIDConfiguration(json,
                                                    change => Station.IsAllowed(user, PermissionsFor(change)),
                                                    out var change,
                                                    out var error,
                                                    out var forbidden,
                                                    out var notSaved))
            {
                return Task.FromResult(
                           forbidden
                               ? RefusePermission(Request, user, PermissionsFor(change), error)
                               : NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved)
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
        private static IReadOnlyCollection<Permission> PermissionsFor(RFIDChange Change)
        {

            var permissions = new List<Permission>();

            if (Change.HasFlag(RFIDChange.Availability))  permissions.Add(Permission.Edit(StationAccess.Availability));
            if (Change.HasFlag(RFIDChange.Placement))     permissions.Add(Permission.Edit(StationAccess.RFID));

            return permissions;

        }

        #endregion

        #region (private) GetV2GConfiguration(Request) / PutV2GConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/v2g: what this station offers a vehicle
        /// below the charging cable, and what of it actually came up.
        /// </summary>
        private Task<HTTPResponse> GetV2GConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.V2G), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.V2GConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/v2g: change it, and put it into effect.
        /// </summary>
        /// <remarks>
        /// The only configuration handler here that awaits, because this one
        /// takes a raw socket, a multicast membership and a TCP listener down
        /// and opens them again. Answering before that has happened would tell
        /// the page the change was made when it had only been written down.
        ///
        /// It is also logged before it is done rather than after: a change of
        /// interface can take the link down and fail to bring it up, and the
        /// log should say who asked for that rather than only that it happened.
        /// </remarks>
        private async Task<HTTPResponse> PutV2GConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.V2G), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            Log.Info($"'{user.Id}' is changing what this station offers below the charging cable.", "15118", "config", "web");

            var (success, error, notSaved) = await Station.UpdateV2GConfiguration(json, Request.CancellationToken);

            if (!success)
                return NotChanged(Request, HTTPStatusCode.BadRequest, error ?? "The V2G configuration could not be changed.", notSaved);

            return JSONResponse(Request, HTTPStatusCode.OK, Station.V2GConfigurationJSON());

        }

        #endregion

        #region (private) GetPowerConfiguration(Request) / PutPowerConfiguration(Request)

        /// <summary>
        /// GET /api/v1/configuration/power: what this station may draw from the
        /// grid, and what its EVSEs could deliver together.
        /// </summary>
        private Task<HTTPResponse> GetPowerConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Power), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Power), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdatePowerConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Calibration), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Calibration), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateCalibrationConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Connections), false, out _, out var refused))
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

            // Where each connection stands, so that the page does not first
            // draw every connection without it and then again with it.
            json.Merge(ConnectionStatesJSON());

            return json;

        }

        /// <summary>
        /// Where each connection this station dialled stands, by
        /// identification, and what time it is here.
        /// </summary>
        /// <remarks>
        /// The time, because the page says how long ago and how soon, and the
        /// clock it would otherwise ask is the browser's - which is not this
        /// station's, and on a laptop in a car park not always right either.
        /// </remarks>
        private JObject ConnectionStatesJSON()

            => new (
                   new JProperty("timestamp",  Station.TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                   new JProperty("states",     new JObject(
                       Station.ConnectionStates.Select(entry => new JProperty(entry.Key, entry.Value.ToJSON()))
                   ))
               );

        /// <summary>
        /// GET /api/v1/status/connections: where each connection this station
        /// dialled stands.
        /// </summary>
        /// <remarks>
        /// Asked again and again by the Connections page while it is open, so
        /// that a connection lost or back again is seen without a reload -
        /// which is why it is its own route and not the whole configuration
        /// every few seconds. At the permission that reads the configuration,
        /// because what it says - the addresses, what the back end answered -
        /// is the configuration's.
        /// </remarks>
        private Task<HTTPResponse> GetConnectionStates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Connections), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, ConnectionStatesJSON())
                   );

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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryAddAuthentication(json.Value<String>("description"),
                                                           json.Value<String>("kind"),
                                                           json.Value<String>("login"),
                                                           json.Value<String>("secret"),
                                                           out var id,
                                                           out var error,
                                                           out var notSaved,
                                                           json.Value<Double?> ("validitySeconds"),
                                                           json.Value<UInt32?>("length"),
                                                           json.Value<String> ("alphabet"),
                                                           json.Value<String> ("hashAlgorithm"),
                                                           json.Value<Boolean?>("tlsChannelBinding")))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryUpdateAuthentication(json.Value<String>("id"),
                                                              json.Value<String>("description"),
                                                              json.Value<String>("kind"),
                                                              json.Value<String>("login"),
                                                              json.Value<String>("secret"),
                                                              out var error,
                                                              out var notSaved,
                                                              json.Value<Double?> ("validitySeconds"),
                                                              json.Value<UInt32?>("length"),
                                                              json.Value<String> ("alphabet"),
                                                              json.Value<String> ("hashAlgorithm"),
                                                              json.Value<Boolean?>("tlsChannelBinding")))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryRemoveAuthentication(json.Value<String>("id"), out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
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
                                                       out var notSaved,
                                                       json.Value<String>("ocppVersion")))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
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
                                                          out var notSaved,
                                                          json.Value<String>("ocppVersion")))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryRemoveConnection(json.Value<String>("id"), out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

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

            if (!TryAuthorize(Request, Permission.Run (StationAccess.Connections), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            Log.Info($"'{user.Id}' asked this station to test a connection.", "ocpp", "connections", "test", "web");

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

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Connections), false, out _, out var refused))
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryCreateKey(json.Value<String>("subject")   ?? "",
                                                         json.Value<String>("algorithm"),
                                                         out var id,
                                                         out var csr,
                                                         out var error,
                                                         out var notSaved))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryAddCertificate(json.Value<String>("pem") ?? "",
                                                              out var id,
                                                              out var warnings,
                                                              out var error,
                                                              out var notSaved))
            {
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));
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

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.ClientCertificates.TryRemove(json.Value<String>("id") ?? "", out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.ClientCertificates.ToJSON())
                   );

        }

        #endregion

    }

}
