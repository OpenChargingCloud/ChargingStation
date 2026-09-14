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

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The outlets of this station that are held for somebody who is on their
    /// way.
    /// </summary>
    /// <remarks>
    /// Reservations are OCPP's, and they are kept where OCPP keeps them: in the
    /// 2.1 node, made by a <c>ReserveNow</c> request and let go of by a
    /// <c>CancelReservation</c>. Nothing is stored here beside them, because a
    /// second list would be a second opinion about whether an outlet is taken.
    ///
    /// Nothing is connected to a CSMS yet, so nothing sends those requests from
    /// outside. <see cref="TryReserveNow"/> below builds one and hands it to
    /// the same method the incoming handler calls, which is what makes the
    /// feature usable on a station standing on a desk - and means that the day
    /// a CSMS does connect, it is the same code answering it.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// The longest a reservation may be asked for here.
        /// </summary>
        /// <remarks>
        /// A plausibility limit rather than a rule of OCPP: an outlet held for
        /// a week is an outlet somebody forgot about, and the request that did
        /// it was almost certainly a unit mistaken for another.
        /// </remarks>
        public static readonly TimeSpan MaxReservationTime = TimeSpan.FromHours(24);

        #endregion

        #region Properties

        /// <summary>
        /// Every outlet of this station that is held, as OCPP holds it.
        /// </summary>
        public IEnumerable<OCPPv2_1.CS.Reservation> Reservations
            => cs02.Reservations;

        #endregion


        #region ReservationJSON(EVSEId, Now)

        /// <summary>
        /// The reservation on one outlet, for the display, or null when it is
        /// not held.
        /// </summary>
        /// <remarks>
        /// The token is deliberately not on it. A display in a car park saying
        /// which card an outlet is being held for would be printing somebody's
        /// identifier on a wall for anybody to copy down - the person it is
        /// held for finds out by holding their card against the reader, which
        /// is the one way of asking that proves something.
        /// </remarks>
        public JObject? ReservationJSON(Byte            EVSEId,
                                        DateTimeOffset  Now)
        {

            var reservation = cs02.ReservationOf(OCPPv2_1.EVSE_Id.Parse(EVSEId));

            return reservation is null
                       ? null
                       : new JObject(
                             new JProperty("until",          reservation.ExpiryDate.ToString("o")),
                             new JProperty("minutesLeft",    (Int32) Math.Ceiling((reservation.ExpiryDate - Now).TotalMinutes))
                         );

        }

        #endregion

        #region StationHoldJSON(Now)

        /// <summary>
        /// The outlets this station is holding without saying which, for the
        /// heading of the display - or null when it is holding none that way.
        /// </summary>
        /// <remarks>
        /// A reservation that names no EVSE is a promise that one will be free
        /// rather than a claim on any particular one, so it cannot be shown
        /// beside an outlet without saying something untrue about that outlet.
        /// It belongs where it is true: over the whole station.
        ///
        /// The soonest expiry, because that is the one a passer-by can act on -
        /// it is when a machine stops being kept from them.
        /// </remarks>
        public JObject? StationHoldJSON(DateTimeOffset Now)
        {

            var holds = Reservations.Where(reservation => !reservation.EVSEId.HasValue).ToArray();

            return holds.Length == 0
                       ? null
                       : new JObject(
                             new JProperty("count",        holds.Length),
                             new JProperty("minutesLeft",  (Int32) Math.Ceiling((holds.Min(hold => hold.ExpiryDate) - Now).TotalMinutes))
                         );

        }

        #endregion

        #region TryReserveNow(JSON, out Result, out Error)

        /// <summary>
        /// Hold an outlet, as a CSMS would.
        /// </summary>
        /// <remarks>
        /// A real <c>ReserveNowRequest</c> is built and handed to the same
        /// method the incoming OCPP handler calls, so a reservation made here
        /// and one made by a CSMS are the same reservation, answered by the
        /// same rules and refused for the same reasons. This is a way in, not a
        /// second implementation.
        /// </remarks>
        public Boolean TryReserveNow(JObject                           JSON,
                                     [NotNullWhen(true)]  out JObject? Result,
                                     [NotNullWhen(false)] out String?  Error)
        {

            Result  = null;
            Error   = null;

            #region The card it is held for

            var idToken = JSON.Value<String>("idToken")?.Trim();

            if (String.IsNullOrEmpty(idToken) || idToken.Length > 36)
            {
                Error = "A reservation needs the 'idToken' it is held for.";
                return false;
            }

            #endregion

            #region Which outlet, if any in particular

            OCPPv2_1.EVSE_Id? evseId = null;

            if (JSON["evse"] is JToken evseToken && evseToken.Type != JTokenType.Null)
            {

                var value = evseToken.Type == JTokenType.Integer ? evseToken.Value<Int64>() : -1;

                if (value < 1 || !EVSEs.Any(evse => evse.Id == value))
                {
                    Error = $"'evse' is the number of an EVSE of this station, or null for whichever one is free.";
                    return false;
                }

                evseId = OCPPv2_1.EVSE_Id.Parse((UInt16) value);

            }

            #endregion

            #region How long

            var minutes = JSON.Value<Double?>("minutes") ?? 15;

            if (minutes <= 0 || minutes > MaxReservationTime.TotalMinutes)
            {
                Error = $"'minutes' must be more than 0 and at most {MaxReservationTime.TotalMinutes}.";
                return false;
            }

            #endregion

            var now = TimeProvider.GetUtcNow();

            var reservationId = JSON.Value<String>("reservationId") is String text && text.Trim().Length > 0
                                    ? OCPPv2_1.Reservation_Id.TryParse(text.Trim())
                                    : OCPPv2_1.Reservation_Id.NewRandom;

            if (reservationId is null)
            {
                Error = "'reservationId' is a number.";
                return false;
            }

            var request = new OCPPv2_1.CSMS.ReserveNowRequest(
                              Destination:   SourceRouting.To(cs02.Id),
                              Id:            reservationId.Value,
                              ExpiryDate:    now + TimeSpan.FromMinutes(minutes),
                              IdToken:       new OCPPv2_1.IdToken(
                                                 idToken,
                                                 OCPPv2_1.IdTokenType.ISO14443
                                             ),
                              EVSEId:        evseId
                          );

            var status = cs02.Reserve(request, now);

            if (status != OCPPv2_1.ReservationStatus.Accepted)
            {

                Log.Notice(
                    $"A reservation for '{idToken}' " +
                    (evseId.HasValue ? $"at EVSE {evseId.Value.Value}" : "at any EVSE") +
                    $" was refused: {status}.",
                    "ocpp", "reservation"
                );

                Error = status switch {
                            OCPPv2_1.ReservationStatus.Occupied     => evseId.HasValue
                                                                          ? $"EVSE {evseId.Value.Value} is charging or already held for somebody else."
                                                                          : "Every EVSE of this station is charging or already held.",
                            OCPPv2_1.ReservationStatus.Unavailable  => $"EVSE {evseId?.Value} is out of service.",
                            OCPPv2_1.ReservationStatus.Faulted      => "This station cannot hold an outlet right now.",
                            _                                       => "This station will not hold that outlet."
                        };

                return false;

            }

            Log.Notice(
                $"EVSE {(evseId.HasValue ? evseId.Value.Value.ToString() : "(any)")} is held for '{idToken}' " +
                $"until {(now + TimeSpan.FromMinutes(minutes)):HH:mm:ss} (reservation {reservationId.Value}).",
                "ocpp", "reservation"
            );

            Result = new JObject(
                         new JProperty("status",         status.ToString()),
                         new JProperty("reservationId",  reservationId.Value.ToString()),
                         new JProperty("evse",           evseId.HasValue ? evseId.Value.Value : null),
                         new JProperty("expiryDate",     (now + TimeSpan.FromMinutes(minutes)).ToString("o"))
                     );

            return true;

        }

        #endregion

        #region TryCancelReservation(JSON, out Result, out Error)

        /// <summary>
        /// Let an outlet go again, as a CSMS would.
        /// </summary>
        public Boolean TryCancelReservation(JObject                           JSON,
                                            [NotNullWhen(true)]  out JObject? Result,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Result  = null;
            Error   = null;

            var text = JSON.Value<String>("reservationId")?.Trim();

            if (String.IsNullOrEmpty(text) ||
                OCPPv2_1.Reservation_Id.TryParse(text) is not OCPPv2_1.Reservation_Id reservationId)
            {
                Error = "A 'reservationId' is needed.";
                return false;
            }

            if (!cs02.CancelReservation(reservationId))
            {
                Error = $"This station is not holding anything under the reservation '{reservationId}'.";
                return false;
            }

            Log.Notice($"The reservation '{reservationId}' was cancelled.", "ocpp", "reservation");

            Result = new JObject(
                         new JProperty("status",         "Accepted"),
                         new JProperty("reservationId",  reservationId.ToString())
                     );

            return true;

        }

        #endregion

        #region ReservationsJSON()

        /// <summary>
        /// Every outlet this station is holding, for the web interface.
        /// </summary>
        public JObject ReservationsJSON()

            => new (

                   new JProperty("reservations",  new JArray(Reservations.Select(reservation => reservation.ToJSON()))),

                   new JProperty("evses",         new JArray(EVSEs.Select(evse => new JObject(
                                                      new JProperty("id",          evse.Id),
                                                      new JProperty("label",       evse.PhysicalReference),
                                                      new JProperty("operative",   evse.Operative),
                                                      new JProperty("charging",    sessions.ContainsKey(evse.Id))
                                                  )))),

                   new JProperty("maxMinutes",    MaxReservationTime.TotalMinutes)

               );

        #endregion

    }

}
