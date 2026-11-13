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

            AddHandler(HTTPPath.Root + "v1/status/connections", GetConnectionStates, HTTPMethod.GET);
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

            AddHandler(HTTPPath.Root + "v1/configuration/authentications",        GetConnectionsAndAuth,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications",        PostAuthentication,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications/update", PostUpdateAuthentication,  HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/authentications/remove", PostRemoveAuthentication,  HTTPMethod.POST);

            AddHandler(HTTPPath.Root + "v1/configuration/connections",            GetConnectionsAndAuth,     HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/connections",            PostConnection,            HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/update",     PostUpdateConnection,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/remove",     PostRemoveConnection,      HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/test",       PostTestConnection,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/pinSetSigners",         PostPinSetSigner,        HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/connections/pinSetSigners/remove",  PostRemovePinSetSigner,  HTTPMethod.POST);

            // Which keys sign as which role, and the signature policy (white paper
            // "Signed OCPP Messages and Signer Roles", section 6.6).
            AddHandler(HTTPPath.Root + "v1/configuration/signatures",                   GetSignatures,          HTTPMethod.GET);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/settings",          PutSignatureSettings,   HTTPMethod.PUT);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles",             PostSignerRole,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles/firstOwner",  PostFirstOwner,         HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/roles/remove",      PostRemoveSignerRole,   HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/policy",            PostSignaturePolicy,    HTTPMethod.POST);
            AddHandler(HTTPPath.Root + "v1/configuration/signatures/reset",             PostResetSignerRoles,   HTTPMethod.POST);

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

            // What this station made of the certificate the server of each
            // wss:// connection checked by its CA showed last: what its page
            // offers to hold it to, and says it came to.
            foreach (var connection in json["connections"]?.OfType<JObject>() ?? [])
            {

                // Which version of HTTP the WebSocket it has now was opened
                // over: where the server chose, the entry does not say.
                if (URL.TryParse(connection.Value<String>("url") ?? "", out var dialled) &&
                    Station.HTTPVersionInUseAt(dialled) is String httpVersionInUse)
                {
                    connection.Add(new JProperty("httpVersionInUse", httpVersionInUse));
                }

                // Which subprotocol it agreed on, and so how its messages are
                // written: where the back end chose, the entry does not say.
                // Over a WebSocket and over ssh:// alike.
                if (connection.Value<String>("id") is String connectionId &&
                    Station.Connections.Connections.FirstOrDefault(entry => entry.Id == connectionId) is OCPP.ConnectionEntry itsEntry &&
                    Station.SubprotocolInUseOf(itsEntry) is String subprotocolInUse)
                {
                    connection.Add(new JProperty("subprotocolInUse", subprotocolInUse));
                }

                if (connection.Value<Boolean?>("secure")    != true ||
                    connection.Value<Boolean?>("keyPinned") == true ||
                    !URL.TryParse(connection.Value<String>("url") ?? "", out var url))
                {
                    continue;
                }

                if (Station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, url.Host.ToString()) is { } judgement)
                    connection.Add(new JProperty("judgement", judgement.ToJSON()));

            }

            // Which certificates a connection may point at. Read from the
            // certificate store rather than remembered here, so that one
            // removed on the page next door is gone from this list too.
            // The key this station signs in with over SSH, for the line that
            // goes into a server's authorized_keys - null until the first
            // ssh:// connection made it.
            json.Add(new JProperty("sshClientKey", Station.SshKey.ToJSON()));

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
        private async Task<HTTPResponse> PostConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!TryPinsOf(json, out var pins, out var wrongPins))
                return NotChanged(Request, HTTPStatusCode.BadRequest, wrongPins, false);

            if (!Station.Connections.TryAddConnection(json.Value<String>("description"),
                                                       json.Value<String>("url"),
                                                       json.Value<String>("connectionType"),
                                                       json.Value<Boolean?>("autoConnect"),
                                                       json.Value<String>("authenticationId"),
                                                       json.Value<String>("certificateId"),
                                                       out var id,
                                                       out var error,
                                                       out var notSaved,
                                                       json.Value<String>("ocppVersion"),
                                                       json.Value<String>("sshHostKey"),
                                                       SshTrustOf(json),
                                                       KeyPinsOf(json, "serverKeyPins"),
                                                       KeyPinTrustOf(json),
                                                       pins,
                                                       json.Value<String>(OCPP.ConnectionEntry.HTTPVersionKey),
                                                       json.Value<String>(OCPP.ConnectionEntry.EncodingKey),
                                                       json.Value<String>(OCPP.ConnectionEntry.SignatureFormatsKey)))
            {
                return NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved);
            }

            await SshKeyIfNeeded(id, Request.CancellationToken);

            return JSONResponse(Request, HTTPStatusCode.Created,
                                new JObject(
                                    new JProperty("id",          id),
                                    new JProperty("connections", ConnectionsJSON())
                                ));

        }

        /// <summary>
        /// POST /api/v1/configuration/connections/update with {"id", ...}.
        /// </summary>
        /// <remarks>
        /// The host key as the page showed it comes back beside the one in the
        /// form: where somebody left it as it was, the one there is now stays -
        /// written down meanwhile by the first connection, perhaps. The same
        /// for the key pins of a wss:// connection, which a signed pin set may
        /// have replaced meanwhile.
        /// </remarks>
        private async Task<HTTPResponse> PostUpdateConnection(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return refused;

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return errorResponse;

            if (!TryPinsOf(json, out var pins, out var wrongPins))
                return NotChanged(Request, HTTPStatusCode.BadRequest, wrongPins, false);

            if (!Station.Connections.TryUpdateConnection(json.Value<String>("id"),
                                                          json.Value<String>("description"),
                                                          json.Value<String>("url"),
                                                          json.Value<String>("connectionType"),
                                                          json.Value<Boolean?>("autoConnect"),
                                                          json.Value<String>("authenticationId"),
                                                          json.Value<String>("certificateId"),
                                                          out var error,
                                                          out var notSaved,
                                                          json.Value<String>("ocppVersion"),
                                                          json.Value<String>("sshHostKey"),
                                                          json.Value<String>("sshHostKeyAsShown"),
                                                          SshTrustOf(json),
                                                          KeyPinsOf(json, "serverKeyPins"),
                                                          KeyPinsOf(json, "serverKeyPinsAsShown"),
                                                          KeyPinTrustOf(json),
                                                          pins,
                                                          json.Value<String>(OCPP.ConnectionEntry.HTTPVersionKey),
                                                          json.Value<String>(OCPP.ConnectionEntry.EncodingKey),
                                                          json.Value<String>(OCPP.ConnectionEntry.SignatureFormatsKey)))
            {
                return NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved);
            }

            await SshKeyIfNeeded(json.Value<String>("id"), Request.CancellationToken);

            return JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON());

        }

        /// <summary>
        /// The key this station signs in with over SSH, made where the
        /// connection just written down is an ssh:// one: its public half is
        /// what somebody has to hand the server before the first connection,
        /// and the page that just saved it is where it is shown.
        /// </summary>
        /// <remarks>
        /// A key that cannot be made does not undo what was written down: the
        /// connection stands, and says why when it is dialled.
        /// </remarks>
        private async Task SshKeyIfNeeded(String? ConnectionId, CancellationToken CancellationToken)
        {

            if (Station.SshKey.Key is not null ||
                !Station.Connections.Connections.Any(entry => entry.Id == ConnectionId && entry.IsSSH))
                return;

            try
            {
                await Station.SshKey.MakeIfNeeded(CancellationToken);
            }
            catch (Exception e)
            {
                Log.Warning($"The key this station signs in with over SSH could not be made: {e.Message}", "ocpp", "connections", "ssh");
            }

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

        /// <summary>
        /// POST /api/v1/configuration/connections/pinSetSigners with
        /// {"publicKey", "description"}: a key a pin set for a connection over
        /// SSH is believed of - the CSMS's signing anchor.
        /// </summary>
        private Task<HTTPResponse> PostPinSetSigner(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryAddPinSetSigner(json.Value<String>("publicKey"), json.Value<String>("description"), out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.Created, ConnectionsJSON()));

        }

        /// <summary>
        /// POST /api/v1/configuration/connections/pinSetSigners/remove with
        /// {"publicKey"}: no pin set signed by it is believed any more.
        /// </summary>
        private Task<HTTPResponse> PostRemovePinSetSigner(HTTPRequest Request)
        {

            if (!TryAuthorize(Request, Permission.Edit(StationAccess.Connections), true, out _, out var refused))
                return Task.FromResult(refused);

            if (!TryParseJSONObject(Request, out var json, out var errorResponse))
                return Task.FromResult(errorResponse);

            if (!Station.Connections.TryRemovePinSetSigner(json.Value<String>("publicKey"), out var error, out var notSaved))
                return Task.FromResult(NotChanged(Request, HTTPStatusCode.BadRequest, error, notSaved));

            return Task.FromResult(JSONResponse(Request, HTTPStatusCode.OK, ConnectionsJSON()));

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

            // With "ocppRequest" for what is asked in OCPP once it is
            // connected: "heartbeat", which it is where nothing is said, or "none".
            return JSONResponse(
                       Request,
                       HTTPStatusCode.OK,
                       await Station.TestConnection(json, Request.CancellationToken)
                   );

        }

        /// <summary>
        /// The key pins of a wss:// connection as they were sent: a list of them,
        /// or one text of them as typed - as one text, separated by commas, which
        /// is what the connection reads. Null where none of it was sent, to keep
        /// what there is; a list with something in it that is no text is sent
        /// on as it is, to be refused in words.
        /// </summary>
        internal static String? KeyPinsOf(JObject JSON, String Name)

            => JSON[Name] switch {
                   JArray list                                  => String.Join(",", list.Select(pin => pin.Type == JTokenType.String
                                                                                                           ? pin.Value<String>()
                                                                                                           : pin.ToString(Newtonsoft.Json.Formatting.None))),
                   JValue { Type: JTokenType.Null } or null     => null,
                   var other                                    => other.ToString()
               };

        /// <summary>
        /// What the server of a wss:// connection checked by its CA is held to,
        /// as it was sent under "pins" - with what the page showed it held to,
        /// under "pinsAsShown", where it says so - or null, where neither was
        /// sent, to keep what it is held to.
        /// </summary>
        /// <remarks>
        /// Whether it is a wss:// connection at all is asked where the URL is
        /// known. What is read here is whether the words are ones a server can
        /// be held to: the same ones, and refused with the same sentences, as
        /// those of a time server, a name server and a meter.
        /// </remarks>
        private static Boolean TryPinsOf(JObject                           JSON,
                                         out OCPP.ServerPinsChange?        Pins,
                                         [NotNullWhen(false)] out String?  Error)
        {

            Pins   = null;
            Error  = null;

            var sent   = JSON[OCPP.ConnectionEntry.PinsKey];
            var shown  = JSON[OCPP.ConnectionEntry.PinsAsShownKey];

            if (sent is null && shown is null)
                return true;

            if (!TryServerPins(sent,  OCPP.ConnectionEntry.PinsKey,        out var sentPins,  out Error) ||
                !TryServerPins(shown, OCPP.ConnectionEntry.PinsAsShownKey, out var shownPins, out Error))
            {
                return false;
            }

            Pins = new OCPP.ServerPinsChange(sentPins, shownPins, shown is not null);
            return true;

        }

        private static Boolean TryServerPins(JToken?                                              JSON,
                                             String                                               Path,
                                             out protocols.WWCP.Node.Configuration.ServerPins?    Pins,
                                             [NotNullWhen(false)] out String?                     Error)
        {

            Pins   = null;
            Error  = null;

            if (JSON is null || JSON.Type == JTokenType.Null)
                return true;

            if (JSON is not JObject entry)
            {
                Error = $"'{Path}' is no object: it holds what the server is held to under 'certificateFingerprints', 'rootFingerprints', 'rootGroup', 'onMismatch' and 'trustOnFirstUse'.";
                return false;
            }

            return protocols.WWCP.Node.Configuration.ServerPins.TryParse(entry, Path, out Pins, out Error);

        }

        /// <summary>
        /// Whether a wss:// connection offers raw public keys and learns a key
        /// pin on first use, and how long that waits, as it was sent - or null,
        /// where none of it was, to keep what it says.
        /// </summary>
        internal static ServerKeyPinSettings? KeyPinTrustOf(JObject JSON)
        {

            var probation = JSON["serverKeyPinProbationTime"];

            if (JSON["rawPublicKeys"] is null && JSON["trustOnFirstUse"] is null && probation is null)
                return null;

            Int64? seconds = probation?.Type switch {
                                 JTokenType.Integer                                                  => probation.Value<Int64>(),
                                 JTokenType.String when Int64.TryParse(probation.Value<String>(), out var parsed) => parsed,
                                 JTokenType.Null or null                                             => null,
                                 _                                                                   => -1
                             };

            return new ServerKeyPinSettings(JSON.Value<String>("rawPublicKeys"),
                                                 JSON.Value<String>("trustOnFirstUse"),
                                                 seconds);

        }

        /// <summary>
        /// How an ssh:// connection believes its server beyond its pins, as it
        /// was sent - or null, where none of it was, to keep what it says. A
        /// probation that is no whole number is sent on as one no connection
        /// takes, so that it is refused in words rather than with a 500.
        /// </summary>
        internal static OCPP.SshTrustSettings? SshTrustOf(JObject JSON)
        {

            var probation = JSON["sshProbationTime"];

            if (JSON["sshTrustOnFirstUse"] is null && JSON["sshOnPinMismatch"] is null && probation is null)
                return null;

            Int64? seconds = probation?.Type switch {
                                 JTokenType.Integer                                                  => probation.Value<Int64>(),
                                 JTokenType.String when Int64.TryParse(probation.Value<String>(), out var parsed) => parsed,
                                 JTokenType.Null or null                                             => null,
                                 _                                                                   => -1
                             };

            return new OCPP.SshTrustSettings(JSON.Value<String>("sshTrustOnFirstUse"),
                                        JSON.Value<String>("sshOnPinMismatch"),
                                        seconds);

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
