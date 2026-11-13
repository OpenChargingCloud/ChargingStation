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

using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using SignedDataObject   = cloud.charging.open.protocols.WWCP.SignedDataObject;
using SignerRoleReasons  = cloud.charging.open.protocols.WWCP.SignerRoleReasons;

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
    public class CSHTTPAPI : OCPPDiallingHTTPAPI
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
                   StationAccess.Connections,
                   APIPath,
                   Version ?? typeof(CSHTTPAPI).Assembly.GetName().Version?.ToString(3) ?? "0.0.0")

        {

            this.Station = Station;

            RegisterURLTemplates();

            // What an EVSE does, to the EVSEs page as it happens - on the
            // stream every page of the web interface already follows.
            Station.EVSEsChanged += json => Publish("evses", json);

            // What the meters measure, on an event of its own: news every
            // second, which the EVSEs' event should not have to carry.
            Station.MetersChanged += json => Publish("meters", json);

        }

        #endregion


        #region (private) RegisterURLTemplates()

        /// <summary>
        /// What only a charging station has.
        /// </summary>
        private void RegisterURLTemplates()
        {

            AddHandler(HTTPPath.Root + "v1/status/evses",       GetEVSEStates,       HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/status/meters",      GetMeterStates,      HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/meters",     GetMeterConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/meters",     PutMeterConfiguration, HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/meters/modbus",            PostModbusRequest,     HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/meters/sessions",          GetSignedSessions,     HTTPMethod.GET);

            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        GetV2GConfiguration,   HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/v2g",        PutV2GConfiguration,   HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/power",      GetPowerConfiguration,        HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/power",      PutPowerConfiguration,        HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/display",    GetDisplayConfiguration,      HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/display",    PutDisplayConfiguration,      HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/nameplate",   GetNameplateConfiguration,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/nameplate",   PutNameplateConfiguration,     HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/commissioning", GetCommissioningConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/commissioning", PutCommissioningConfiguration, HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/evses",      GetEVSEConfiguration,         HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/evses",      PutEVSEConfiguration,         HTTPMethod.PUT);

            AddHandler(HTTPPath.Root + "v1/configuration/calibration", GetCalibrationConfiguration, HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/calibration", PutCalibrationConfiguration, HTTPMethod.PUT);



            // Which keys sign as which role, and the signature policy (white paper
            // "Signed OCPP Messages and Signer Roles", section 6.6).
            AddHandler(HTTPPath.Root + "v1/configuration/signatures",                   GetSignatures,          HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/settings",          PutSignatureSettings,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles",             PostSignerRole,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles/firstOwner",  PostFirstOwner,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles/remove",      PostRemoveSignerRole,   HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/policy",            PostSignaturePolicy,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/reset",             PostResetSignerRoles,   HTTPMethod.POST);


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

            AddHandler(HTTPPath.Root + "v1/restart",         PostRestartOnceIdle,  HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/restart/now",     PostRestartAtOnce,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/evses/unlock",    PostUnlock,           HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/status/report",   PostReportStatus,     HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/ocpp/send",       PostSendAgain,        HTTPMethod.POST);

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
        /// PUT /api/v1/configuration/display with {"dimFrom", "dimUntil", "dimTo",
        /// "keepMoving", "port"}: when the screen on the front is dim, and how
        /// dim; whether its picture walks against burn-in; and the port it is
        /// served on, which it is moved to at once.
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
        /// and an empty object is how dimming is turned off. A port something
        /// else has is refused with 400 and the display left where it was.
        /// </remarks>
        private async Task<HTTPResponse> PutDisplayConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Display), true, out _, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            var (success, error, notSaved) = await Station.UpdateDisplayConfiguration(json, Request.CancellationToken);

            if (!success)
                return NotChanged(Request, HTTPStatusCode.BadRequest, error ?? "The display configuration could not be changed.", notSaved);

            return JSONResponse(Request, HTTPStatusCode.OK, Station.DisplayConfigurationJSON());

        }

        /// <summary>
        /// GET /api/v1/configuration/nameplate: what this station tells a CSMS it
        /// is, and where each OCPP node stands with its CSMS.
        /// </summary>
        private Task<HTTPResponse> GetNameplateConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Nameplate), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NameplateConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/nameplate with {"vendor", "model",
        /// "serialNumber", "firmwareVersion"}: what this station tells a CSMS it
        /// is. Its OCPP nodes hang up, dial again and say so in their
        /// BootNotification.
        /// </summary>
        /// <remarks>
        /// The system administrator's alone - see StationAccess.Nameplate. The
        /// whole section at once, and an empty object is a station that says
        /// the defaults.
        /// </remarks>
        private Task<HTTPResponse> PutNameplateConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Nameplate), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateNameplateConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.NameplateConfigurationJSON())
                   );

        }

        /// <summary>
        /// GET /api/v1/configuration/commissioning: whether a tool may speak
        /// OCPP to this station over its SSH server, where, and who does.
        /// </summary>
        private Task<HTTPResponse> GetCommissioningConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Commissioning), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.CommissioningJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/commissioning with {"enabled"}: switch
        /// commissioning over SSH on or off, at once. Switched off, every tool
        /// connected is let go of.
        /// </summary>
        /// <remarks>
        /// The installer's, as running it is - see StationAccess.Commissioning.
        /// </remarks>
        private Task<HTTPResponse> PutCommissioningConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Commissioning), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateCommissioningConfiguration(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.CommissioningJSON())
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

        #region (private) PostRestartOnceIdle(Request) / PostRestartAtOnce(Request)

        /// <summary>
        /// POST /api/v1/restart with {}: restart this station once nothing is
        /// charging any more. No new session starts until then.
        /// </summary>
        /// <remarks>
        /// "reset:run", which the operator's telephone support holds. A route
        /// of its own beside the restart at once, as the two are rights of
        /// their own: the door is the route, before anything sent is read.
        /// </remarks>
        private Task<HTTPResponse> PostRestartOnceIdle(HTTPRequest Request)

            => PostRestart(Request, false);

        /// <summary>
        /// POST /api/v1/restart/now with {}: restart this station at once,
        /// which ends what is charging.
        /// </summary>
        /// <remarks>
        /// "reset-immediate:run": the one of the two that can do damage.
        /// </remarks>
        private Task<HTTPResponse> PostRestartAtOnce(HTTPRequest Request)

            => PostRestart(Request, true);

        private Task<HTTPResponse> PostRestart(HTTPRequest  Request,
                                               Boolean      AtOnce)
        {

            if (!TryAuthorize(Request,
                              Permission.Run(AtOnce ? NodeResources.ResetImmediate : NodeResources.Reset),
                              true,
                              out var user,
                              out var refused))
                return Task.FromResult(refused);

            // Nothing in it is read. An object all the same: a restart is
            // asked for by somebody who sent one, not by anything posted here.
            if (!TryParseJSONObject(Request, out _, out var errorResponse))
                return Task.FromResult(errorResponse);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.Restart(AtOnce, $"'{user.Id}' on the web page"))
                   );

        }

        #endregion

        #region (private) PostUnlock(Request)

        /// <summary>
        /// POST /api/v1/evses/unlock with {"evse": 1} and, where it has
        /// several, {"connector": 2}: let go of a cable that is stuck, where
        /// nobody is charging.
        /// </summary>
        private Task<HTTPResponse> PostUnlock(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Run(StationAccess.Unlock), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var evse = json["evse"]?.Value<Byte?>();

            if (evse is null)
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "Which EVSE? Send \"evse\"."));

            var result = Station.Unlock(evse.Value, json["connector"]?.Value<Byte?>(), $"'{user.Id}' on the web page");

            return Task.FromResult(
                       result == UnlockResult.Unlocked
                           ? JSONResponse(Request,
                                          HTTPStatusCode.OK,
                                          new JObject(
                                              new JProperty("evse",      evse.Value),
                                              new JProperty("unlocked",  true)
                                          ))
                           : ErrorJSON   (Request,
                                          result == UnlockResult.Charging
                                              ? HTTPStatusCode.Conflict
                                              : HTTPStatusCode.BadRequest,
                                          ChargingStation.Said(result, evse.Value))
                   );

        }

        #endregion

        #region (private) PostReportStatus(Request) / PostSendAgain(Request)

        /// <summary>
        /// POST /api/v1/status/report with {}: have this station tell every
        /// back end that accepted it how each EVSE stands, again.
        /// </summary>
        /// <remarks>
        /// "state:run", which the operator's telephone support holds - and so
        /// a route of its own beside the other messages sent again.
        /// </remarks>
        private Task<HTTPResponse> PostReportStatus(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Run(NodeResources.State), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out _, out var errorResponse))
                return Task.FromResult(errorResponse);

            return Task.FromResult(
                       Station.TrySendAgain(ChargingStation.SendsStatusNotification, $"'{user.Id}' on the web page", out var result, out var error)
                           ? JSONResponse(Request, HTTPStatusCode.OK,       result)
                           : ErrorJSON   (Request, HTTPStatusCode.Conflict, error)
                   );

        }

        /// <summary>
        /// POST /api/v1/ocpp/send with {"message": "BootNotification"} or
        /// "Heartbeat": have this station send again what it sends by itself.
        /// </summary>
        /// <remarks>
        /// "node:run". How the EVSEs stand is told again at
        /// /api/v1/status/report, which asks for another right.
        /// </remarks>
        private Task<HTTPResponse> PostSendAgain(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Run(NodeResources.Node), true, out var user, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            var message = ChargingStation.NameOfSent(json["message"]?.Value<String>());

            if (message is not (ChargingStation.SendsBootNotification or ChargingStation.SendsHeartbeat))
                return Task.FromResult(ErrorJSON(Request,
                                                 HTTPStatusCode.BadRequest,
                                                 $"Which message? Send \"message\": \"{ChargingStation.SendsBootNotification}\" or \"{ChargingStation.SendsHeartbeat}\"."));

            return Task.FromResult(
                       Station.TrySendAgain(message, $"'{user.Id}' on the web page", out var result, out var error)
                           ? JSONResponse(Request, HTTPStatusCode.OK,       result)
                           : ErrorJSON   (Request, HTTPStatusCode.Conflict, error)
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

        #region (private) GetMeterConfiguration(Request) / PutMeterConfiguration(Request) / PostModbusRequest(Request)

        /// <summary>
        /// GET /api/v1/configuration/meters: how the meter of each EVSE is read,
        /// and the identities of the store it can be shown.
        /// </summary>
        private Task<HTTPResponse> GetMeterConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Meters), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.MetersConfigurationJSON())
                   );

        }

        /// <summary>
        /// PUT /api/v1/configuration/meters with {"evse", "host", "port", "unit",
        /// "identities", and what the meter's certificate is held to}: change
        /// how one EVSE's meter is read, and read it so at once.
        /// </summary>
        private async Task<HTTPResponse> PutMeterConfiguration(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Meters), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            Log.Info($"'{user.Id}' is changing how a meter is read.", "meter", "config", "web");

            var (success, error, notSaved) = await Station.UpdateMeterConfiguration(json, Request.CancellationToken);

            if (!success)
                return NotChanged(Request, HTTPStatusCode.BadRequest, error ?? "How the meter is read could not be changed.", notSaved);

            return JSONResponse(Request, HTTPStatusCode.OK, Station.MetersConfigurationJSON());

        }

        /// <summary>
        /// POST /api/v1/meters/modbus with {"evse", "function", "address",
        /// "count" | "value" | "values"}: ask the meter of an EVSE one Modbus
        /// request, as the identity this station is to it for what the request
        /// does, and say what came back.
        /// </summary>
        /// <remarks>
        /// A read at the permission that reads the meters, a write at the one
        /// that runs them: a register written is a meter changed. Which it is
        /// is known only from the body, so the body is read before the
        /// permission is asked.
        /// </remarks>
        private async Task<HTTPResponse> PostModbusRequest(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Meters), true, out var user, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!Configuration.MeterConfiguration.TryReadEVSE(json, "modbus", out var evse, out var error) ||
                !Meters.ModbusRequest.TryParse(json, out var modbus, out error))
                return JSONResponse(Request, HTTPStatusCode.BadRequest, new JObject(new JProperty("error", error)));

            if (modbus.Operation == Meters.ModbusOperation.Write &&
                !TryAuthorize(Request, Permission.Run(StationAccess.Meters), true, out _, out refused))
                return refused;

            if (Station.AskMeter(evse, modbus, Request.CancellationToken) is not Task<JObject> asking)
                return JSONResponse(Request, HTTPStatusCode.NotFound,
                                    new JObject(new JProperty("error", $"No meter is read at EVSE {evse}, so there is nothing to ask.")));

            if (modbus.Operation == Meters.ModbusOperation.Write)
                Log.Notice($"'{user.Id}' asks the meter of EVSE {evse}: {modbus}.", "meter", "modbus", "web");

            return JSONResponse(Request, HTTPStatusCode.OK, await asking);

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
        /// GET /api/v1/status/evses: how each EVSE stands right now.
        /// </summary>
        /// <remarks>
        /// What the EVSEs page draws when it is opened; every change after that
        /// reaches it as an "evses" event on the stream. At the permission that
        /// reads what is going on, apart from how the station is set up: the
        /// support sees how each EVSE stands, and not what is fitted, nor where
        /// the station dials (white paper "Signed OCPP Messages and Signer
        /// Roles", section 5.3).
        /// </remarks>
        private Task<HTTPResponse> GetEVSEStates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NodeResources.State), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.EVSEsStatusJSON())
                   );

        }

        /// <summary>
        /// GET /api/v1/status/meters: how each meter that is to be read stands
        /// right now, and what it measures; every change after that reaches a
        /// page as a "meters" event on the stream.
        /// </summary>
        private Task<HTTPResponse> GetMeterStates(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Meters), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.MetersStatusJSON())
                   );

        }

        /// <summary>
        /// GET /api/v1/meters/sessions?evse=1: the charging sessions the meters
        /// signed, or were to sign, the newest first - each with its document,
        /// the key it is signed with, and what ChargyCore said of the two.
        /// </summary>
        private Task<HTTPResponse> GetSignedSessions(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(StationAccess.Meters), false, out _, out var refused))
                return Task.FromResult(refused);

            Byte? evse = null;

            if (Request.QueryString.GetString("evse") is String text)
            {

                if (!Byte.TryParse(text, out var parsed) || parsed == 0)
                    return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, $"'evse' must be the number of an EVSE, not '{text}'."));

                evse = parsed;

            }

            return Task.FromResult(
                       JSONResponse(Request, HTTPStatusCode.OK, Station.SignedSessionsJSON(evse))
                   );

        }

        #region Signer roles and the signature policy

        /// <summary>
        /// A signed data object as it was sent: read without making dates of
        /// its texts, which would break its signatures.
        /// </summary>
        private static Boolean TryParseSignedObject(HTTPRequest                             Request,
                                                    [NotNullWhen(true)]  out JObject?       JSON,
                                                    [NotNullWhen(false)] out HTTPResponse?  ErrorResponse)
        {

            JSON           = null;
            ErrorResponse  = null;

            try
            {
                JSON = SignedDataObject.ParseAsReceived(Request.HTTPBodyAsUTF8String ?? "");
                return true;
            }
            catch (Exception)
            {
                ErrorResponse = ErrorJSON(Request, HTTPStatusCode.BadRequest, "This is no JSON object.");
                return false;
            }

        }

        /// <summary>
        /// Why a role assignment or a policy was not taken: 409 where it came
        /// too late or does not fit what is there, 403 where it is not the
        /// owners', 400 where it is none.
        /// </summary>
        private static HTTPResponse NotTaken(HTTPRequest Request, SignerRoleReasons Reason, String Error)

            => ErrorJSON(Request,
                         Reason switch {
                             SignerRoleReasons.NotAuthorized    => HTTPStatusCode.Forbidden,
                             SignerRoleReasons.VersionNotNewer  => HTTPStatusCode.Conflict,
                             SignerRoleReasons.RoleExists       => HTTPStatusCode.Conflict,
                             SignerRoleReasons.WouldLockOut     => HTTPStatusCode.Conflict,
                             SignerRoleReasons.UnknownRole      => HTTPStatusCode.NotFound,
                             _                                  => HTTPStatusCode.BadRequest
                         },
                         $"{Reason}: {Error}");

        /// <summary>
        /// GET /api/v1/configuration/signatures: the settings of the signature
        /// policy, the policy table the station was given, and its roles with
        /// the keys that sign as them.
        /// </summary>
        private Task<HTTPResponse> GetSignatures(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Read(NodeResources.Signatures), false, out _, out var refused))
                return Task.FromResult(refused);

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, Station.SignaturesJSON()));

        }

        /// <summary>
        /// PUT /api/v1/configuration/signatures/settings with what changes of
        /// {"mode", "acceptFormat0", "maxClockSkew", "maxSignatureAge",
        /// "allowFirstOwnerByCSMS", "allowPermissionsByCSMS", "quorum"}.
        /// </summary>
        private Task<HTTPResponse> PutSignatureSettings(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryUpdateSignatureSettings(json, out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, Station.SignaturesJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/signatures/roles with a signed role
        /// assignment: checked as one received over OCPP is.
        /// </summary>
        private Task<HTTPResponse> PostSignerRole(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseSignedObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryImportSignerRole(json, out var reason, out var error))
                return Task.FromResult(NotTaken(Request, reason, error));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.Created, Station.SignaturesJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/signatures/roles/firstOwner with a role
        /// assignment for "systemadmin", or with {"jwk", "label"}: the first
        /// owner of the station, while it has none.
        /// </summary>
        private Task<HTTPResponse> PostFirstOwner(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseSignedObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            SignerRoleReasons  reason;
            String?            error;

            var installed = json["jwk"] is JObject jwk && json["role"] is null
                                ? Station.TryInstallFirstOwnerKey(jwk, json["label"]?.Type == JTokenType.String ? json["label"]!.Value<String>() : null, out reason, out error)
                                : Station.TryInstallFirstOwner   (json, out reason, out error);

            if (!installed)
                return Task.FromResult(NotTaken(Request, reason, error!));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.Created, Station.SignaturesJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/signatures/roles/remove with {"role",
        /// "version"}: the keys of a role are taken away.
        /// </summary>
        private Task<HTTPResponse> PostRemoveSignerRole(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (json["role"]?.   Type != JTokenType.String  ||
                json["version"]?.Type != JTokenType.Integer ||
                !UInt64.TryParse(json["version"]!.ToString(), out var version))
            {
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "A role and the version of its assignment are needed."));
            }

            if (!Station.TryDeleteSignerRole(json["role"]!.Value<String>()!, version, out var reason, out var error))
                return Task.FromResult(NotTaken(Request, reason, error));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, Station.SignaturesJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/signatures/policy with a signed policy
        /// table: checked as one received over OCPP is.
        /// </summary>
        private Task<HTTPResponse> PostSignaturePolicy(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseSignedObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.TryImportSignaturePolicy(json, out var reason, out var error))
                return Task.FromResult(NotTaken(Request, reason, error));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.Created, Station.SignaturesJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/signatures/reset with {"confirm":
        /// "reset"}: every key of every role and the policy table are
        /// forgotten. The CSMS is told.
        /// </summary>
        private Task<HTTPResponse> PostResetSignerRoles(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(NodeResources.Signatures), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (json["confirm"]?.Type != JTokenType.String || json["confirm"]!.Value<String>() != "reset")
                return Task.FromResult(ErrorJSON(Request, HTTPStatusCode.BadRequest, "Forgetting every key of every role is confirmed with {\"confirm\": \"reset\"}."));

            Station.ResetSignerRoles();

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, Station.SignaturesJSON()));

        }

        #endregion

        #endregion

        #region (private) PostTestConnection(Request)

        /// <summary>
        /// The key pins of a wss:// connection as they were sent: a list of them,
        /// or one text of them as typed - as one text, separated by commas, which
        /// is what the connection reads. Null where none of it was sent, to keep
        /// what there is; a list with something in it that is no text is sent
        /// on as it is, to be refused in words.
        /// </summary>
        internal static String? KeyPinsOf(JObject JSON, String Name)

            => OCPP.ConnectionFields.KeyPinsOf(JSON, Name);

        /// <summary>
        /// Whether a wss:// connection offers raw public keys and learns a key
        /// pin on first use, and how long that waits, as it was sent - or null,
        /// where none of it was, to keep what it says.
        /// </summary>
        internal static ServerKeyPinSettings? KeyPinTrustOf(JObject JSON)

            => OCPP.ConnectionFields.KeyPinTrustOf(JSON);

        /// <summary>
        /// How an ssh:// connection believes its server beyond its pins, as it
        /// was sent - or null, where none of it was, to keep what it says. A
        /// probation that is no whole number is sent on as one no connection
        /// takes, so that it is refused in words rather than with a 500.
        /// </summary>
        internal static OCPP.SshTrustSettings? SshTrustOf(JObject JSON)

            => OCPP.ConnectionFields.SshTrustOf(JSON);

        #endregion

        #region (private) GetClientCertificates(Request) / PostClientKey(Request) / PostClientCertificate(Request) / PostRemoveClientKey(Request)

        #endregion

    }

}
