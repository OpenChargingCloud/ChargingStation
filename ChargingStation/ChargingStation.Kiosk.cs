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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.RFID;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the display on the front of this charging station shows, and the
    /// readers it shows symbols for.
    /// </summary>
    /// <remarks>
    /// Everything here is readable by anybody standing in front of the station,
    /// and that is the whole design: which outlets are free, what they can
    /// deliver, whose customer is charging at one, and how to pay if you are
    /// nobody's customer. Nothing in here says anything about the network this
    /// station hangs on, what it is configured as, or who may sign in to it -
    /// see <see cref="KioskHTTPAPI"/> for why that separation is a
    /// second TCP port and not a careful choice of fields.
    ///
    /// **What drives it.** The status of an EVSE comes from its own
    /// configuration - an inoperative EVSE says so - from OCPP's reservations
    /// (see ChargingStation.Reservations.cs) and from the sessions below. The QR code is a real time-based one-time password over the
    /// shared secret this station is configured with, worked out afresh every
    /// time somebody asks. The sessions are started and stopped by the RFID
    /// readers, and today the only reader with anything behind it is the fake
    /// one. So: a station nobody has touched shows every outlet as free, and it
    /// is the typed-in cards that make it show anything else. Nothing here is
    /// connected to a CSMS, because nothing in this station is yet.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How much of a one-time password has to be left for it to be worth
        /// putting on a screen.
        /// </summary>
        /// <remarks>
        /// A QR code with two seconds left is a code somebody points a phone
        /// at and then cannot use, which is worse than no code at all: the
        /// second attempt looks like the station is broken. The display polls
        /// about this often anyway, so nothing is lost by waiting for the next
        /// one.
        /// </remarks>
        public static readonly TimeSpan MinimumQRCodeTime = TimeSpan.FromSeconds(5);

        #endregion

        #region Kiosk state

        #region KioskJSON()

        /// <summary>
        /// Everything the display shows, in one answer.
        /// </summary>
        /// <remarks>
        /// One answer rather than a resource per EVSE, because a display draws
        /// all of it at once and a screen showing four outlets from four
        /// requests can show them as they were at four different moments.
        ///
        /// Polled rather than pushed - the page asks again every couple of
        /// seconds. An event stream would be fewer bytes, and a display on a
        /// wall is the one client where a dropped connection must not be
        /// noticed by anybody: polling recovers by itself, and by the time
        /// somebody walks up to the screen it is right again.
        /// </remarks>
        public JObject KioskJSON()
        {

            var now       = TimeProvider.GetUtcNow();
            var readers   = RFIDReaders;
            var stationReader = readers.FirstOrDefault(reader => reader.IsStationWide && reader.Enabled);

            return new JObject(

                       new JProperty("station",      new JObject(
                                                         new JProperty("name",  Operator.Name),
                                                         new JProperty("logo",  Operator.Logo)
                                                     )),

                       new JProperty("timestamp",    now.ToString("o")),

                       new JProperty("evses",        new JArray(EVSEs.Select(evse => EVSEPresentationJSON(evse, now, readers)))),

                       // The station-wide reader, when there is one. A reader
                       // per EVSE shows up beside its EVSE instead; both at
                       // once cannot happen, because a station has at most one
                       // reader per place and the whole housing is a place.
                       new JProperty("rfid",         stationReader is null
                                                         ? null
                                                         : ReaderJSON(stationReader)),

                       new JProperty("webPayments",  WebPaymentsEnabled)

                   );

        }

        #endregion

        #region (private) EVSEPresentationJSON(EVSE, Now, Readers)

        /// <summary>
        /// One outlet as the display shows it.
        /// </summary>
        private JObject EVSEPresentationJSON(EVSEConfig                        EVSE,
                                             DateTimeOffset                    Now,
                                             IReadOnlyList<RFIDReaderConfig>   Readers)
        {

            sessions.TryGetValue(EVSE.Id, out var session);

            var reservation = ReservationJSON(EVSE.Id, Now);

            var status  = !EVSE.Operative
                              ? EVSEStatus.Inoperative
                              : session is not null
                                    ? EVSEStatus.Occupied
                                    : reservation is not null
                                          ? EVSEStatus.Reserved
                                          : EVSEStatus.Available;

            var reader  = Readers.FirstOrDefault(candidate => candidate.EVSEId == EVSE.Id && candidate.Enabled);

            var qrCode  = status == EVSEStatus.Available
                              ? WebPaymentURL(EVSE.Id, Now)
                              : null;

            return new JObject(

                       new JProperty("id",                 EVSE.Id),
                       new JProperty("label",              EVSE.PhysicalReference ?? EVSE.Id.ToString()),
                       new JProperty("status",             status.ToString().ToLowerInvariant()),

                       new JProperty("maxPower_kW",        EVSE.MaxPower_kW),
                       new JProperty("currentPower_kW",    session?.SimulatedPower_kW(Now, EVSE.MaxPower_kW)),

                       // Said out loud, every time, because there is no meter
                       // behind that number and a display is exactly where
                       // somebody would assume there is one.
                       new JProperty("powerIsSimulated",   session is not null),

                       new JProperty("connectors",         new JArray(EVSE.Connectors.Select(connector => new JObject(
                                                               new JProperty("id",           connector.Id),
                                                               new JProperty("type",         connector.Type),
                                                               new JProperty("maxPower_kW",  connector.MaxPower_kW)
                                                           )))),

                       new JProperty("session",            session is null
                                                               ? null
                                                               : new JObject(
                                                                     new JProperty("method",     session.Method.ToString()),
                                                                     new JProperty("startedAt",  session.StartedAt.ToString("o")),
                                                                     new JProperty("provider",   ProviderJSON(session))
                                                                 )),

                       new JProperty("qrCode",             qrCode is null
                                                               ? null
                                                               : new JObject(
                                                                     new JProperty("url",        qrCode.Value.URL),
                                                                     new JProperty("expiresAt",  qrCode.Value.EndTime.ToString("o"))
                                                                 )),

                       new JProperty("reservation",        reservation),

                       new JProperty("rfid",               reader is null ? null : ReaderJSON(reader))

                   );

        }

        #endregion

        #region (private) WebPaymentURL(EVSEId, Now)

        /// <summary>
        /// The URL to pay at this outlet with, right now, or null when this
        /// station takes no web payments.
        /// </summary>
        /// <remarks>
        /// Worked out when it is asked for rather than kept and refreshed on a
        /// timer. It is a function of the clock and the shared secret, so the
        /// answer to "what is it now" is always exactly right, there is nothing
        /// to go stale while nobody is looking, and no timer has to be stopped
        /// when this station is reconfigured.
        ///
        /// The same template, secret, validity and length that OCPP 2.1's own
        /// <c>WebPaymentsCtrlr</c> is configured with - see ConfigureWebPayments -
        /// so the URL on the screen and the URL that component would report are
        /// the same URL.
        ///
        /// A password that is about to run out is not shown: a QR code with two
        /// seconds left is a code somebody photographs and then cannot use, and
        /// the next poll is a second away.
        /// </remarks>
        private (String URL, DateTimeOffset EndTime)? WebPaymentURL(Byte            EVSEId,
                                                                    DateTimeOffset  Now)
        {

            if (!WebPaymentsEnabled || webPayments?.URLTemplate is null)
                return null;

            try
            {

                // {evseId} is a placeholder the generator knows but does not
                // fill in from here, so it is filled in first: one outlet's
                // code must not start a charge at the one beside it.
                var template = URL.Parse(
                                   webPayments.URLTemplate.Value.ToString().
                                       Replace("{evseId}", EVSEId.ToString(), StringComparison.OrdinalIgnoreCase)
                               );

                var (url, remaining, endTime) = TOTPGenerator.GenerateURL(
                                                    template,
                                                    webPayments.SharedSecret ?? "",
                                                    webPayments.ValidityTime,
                                                    webPayments.TOTPLength,
                                                    Timestamp: Now
                                                );

                return remaining < MinimumQRCodeTime
                           ? null
                           : (url, endTime);

            }
            catch (Exception e)
            {
                // Once per poll would be a log nobody can read; this is the
                // one thing here that can throw, and it throws for every EVSE
                // at once when it does.
                Log.Debug($"The web payment URL could not be generated: {e.Message}", "kiosk");
                return null;
            }

        }

        #endregion

        #region (private) ProviderJSON(Session) / ReaderJSON(Reader)

        /// <summary>
        /// Whose customer is charging, as far as the display can tell.
        /// </summary>
        /// <remarks>
        /// Ad hoc has no provider and falls back to the operator, because that
        /// is who somebody paying at the screen is buying from. A card this
        /// station does not recognise has no name to show, and showing the
        /// operator's there would be a claim about somebody else's customer.
        /// </remarks>
        private JObject? ProviderJSON(ChargingSession Session)
        {

            if (Session.Provider is not null)
                return new JObject(
                           new JProperty("name",  Session.Provider.Name),
                           new JProperty("logo",  Session.Provider.Logo)
                       );

            if (Session.Method == AuthorizationMethod.AdHoc && Operator.Name is not null)
                return new JObject(
                           new JProperty("name",  Operator.Name),
                           new JProperty("logo",  Operator.Logo)
                       );

            return null;

        }

        /// <summary>
        /// A reader, as the display needs it: what it is called, and whether
        /// its cards are typed in.
        /// </summary>
        private static JObject ReaderJSON(RFIDReaderConfig Reader)

            => new (
                   new JProperty("id",     Reader.Id),
                   new JProperty("kind",   Reader.Kind),
                   // The one thing the page does something with: a fake reader
                   // is a reader you can hold a card against by typing it.
                   new JProperty("fake",   Reader.IsFake),
                   new JProperty("ready",  Reader.HasDriver)
               );

        #endregion

        #endregion

        #region Cards

        #region TryPresentToken(ReaderId, EVSEId, UID, out Result, out Error)

        /// <summary>
        /// Hold a card against one of this station's readers.
        /// </summary>
        /// <remarks>
        /// The one thing the display can do rather than only show, and it is
        /// deliberately the narrowest possible door: the reader has to exist,
        /// be switched on, and be one whose cards are typed in. A station with
        /// only real readers configured answers this with a refusal no matter
        /// what is sent to it.
        ///
        /// The same card twice is start and then stop, which is how every
        /// charging station in the world behaves and what somebody standing in
        /// front of one expects. A different card at an occupied outlet is
        /// refused rather than taking it over.
        /// </remarks>
        /// <param name="ReaderId">Which reader, or null when the station has only one.</param>
        /// <param name="EVSEId">Which EVSE, needed when the reader serves the whole station.</param>
        /// <param name="UID">The card.</param>
        /// <param name="Result">What happened, for the display to show.</param>
        /// <param name="Error">Why nothing happened.</param>
        public Boolean TryPresentToken(String?                           ReaderId,
                                       Byte?                             EVSEId,
                                       String?                           UID,
                                       [NotNullWhen(true)]  out JObject? Result,
                                       [NotNullWhen(false)] out String?  Error)
        {

            Result = null;

            if (!RFIDToken.TryParse(UID, out var token, out Error))
                return false;

            var readers = RFIDReaders.Where(reader => reader.Enabled).ToArray();

            var reader  = ReaderId is not null
                              ? readers.FirstOrDefault(candidate => String.Equals(candidate.Id, ReaderId, StringComparison.OrdinalIgnoreCase))
                              : readers.Length == 1
                                    ? readers[0]
                                    : null;

            if (reader is null)
            {
                Error = ReaderId is null
                            ? "This station has more than one RFID reader; say which one."
                            : $"This station has no RFID reader called '{ReaderId}' switched on.";
                return false;
            }

            if (!reader.IsFake)
            {
                // Not a refusal about permission but about physics: a real
                // reader reads cards, and there is no way to hand one a card
                // over HTTP.
                Error = $"'{reader.Id}' is a {reader.Kind} reader; a card is held against it, not typed into it.";
                return false;
            }

            var evseId = reader.EVSEId ?? EVSEId;

            if (evseId is null)
            {
                Error = "This reader serves the whole station, so it needs to be told which EVSE the card is for.";
                return false;
            }

            var evse = EVSEs.FirstOrDefault(candidate => candidate.Id == evseId);

            if (evse is null)
            {
                Error = $"This station has no EVSE {evseId}.";
                return false;
            }

            if (!evse.Operative)
            {
                Error = $"EVSE {evse.Id} is out of service.";
                return false;
            }

            var now      = TimeProvider.GetUtcNow();
            var started  = false;

            // An outlet held for somebody lets that somebody in and nobody
            // else. The card is how they prove it is theirs, which is why the
            // display does not print the token it is waiting for.
            var reservation = cs02.ReservationOf(OCPPv2_1.EVSE_Id.Parse(evse.Id));

            if (reservation is not null &&
                !sessions.ContainsKey(evse.Id) &&
                !reservation.Admits(new OCPPv2_1.IdToken(token.UID, OCPPv2_1.IdTokenType.ISO14443)))
            {
                Log.Notice(
                    $"Card {token} was turned away from EVSE {evse.Id}: it is held under reservation {reservation.Id} " +
                    $"until {reservation.ExpiryDate:HH:mm:ss}.",
                    "rfid", "reservation", "kiosk"
                );
                Error = $"EVSE {evse.Id} is reserved until {reservation.ExpiryDate.ToLocalTime():HH:mm}.";
                return false;
            }

            if (sessions.TryGetValue(evse.Id, out var running))
            {

                if (!String.Equals(running.TokenUID, token.UID, StringComparison.Ordinal))
                {
                    Error = $"Another card is charging at EVSE {evse.Id}; the same card stops it.";
                    return false;
                }

                sessions.TryRemove(evse.Id, out _);

                SetCharging(evse.Id, false);

                Log.Notice(
                    $"Card {token} stopped the session at EVSE {evse.Id} after {(now - running.StartedAt).TotalSeconds:F0} s (reader '{reader.Id}').",
                    "rfid", "kiosk"
                );

            }

            else
            {

                var provider = Operator.ProviderOf(token);

                sessions[evse.Id] = new ChargingSession(
                                        evse.Id,
                                        AuthorizationMethod.RFID,
                                        token.UID,
                                        provider,
                                        now
                                    );

                started = true;

                SetCharging(evse.Id, true);

                // The reservation has done its job: the person it was held for
                // is here and plugged in. Leaving it standing would hold the
                // outlet against its own driver when they stop and start again.
                if (reservation is not null)
                {
                    cs02.CancelReservation(reservation.Id);
                    Log.Notice($"The reservation {reservation.Id} was taken up at EVSE {evse.Id}.", "rfid", "reservation", "kiosk");
                }

                Log.Notice(
                    $"Card {token} started a session at EVSE {evse.Id} " +
                    (provider is null ? "for a provider this station does not recognise" : $"for {provider.Name}") +
                    $" (reader '{reader.Id}').",
                    "rfid", "kiosk"
                );

            }

            Result = new JObject(
                         new JProperty("evse",     evse.Id),
                         new JProperty("uid",      token.UID),
                         new JProperty("started",  started),
                         new JProperty("stopped",  !started)
                     );

            return true;

        }

        #endregion

        #region (private) SetCharging(EVSEId, Charging)

        /// <summary>
        /// Tell the OCPP node that an outlet is in use, or is not.
        /// </summary>
        /// <remarks>
        /// So that the node can answer a ReserveNow truthfully. Two places that
        /// may disagree about whether an outlet is busy is one place too many,
        /// and the one a CSMS asks is the node.
        /// </remarks>
        private void SetCharging(Byte     EVSEId,
                                 Boolean  Charging)
        {

            if (cs02.TryGetEVSE(OCPPv2_1.EVSE_Id.Parse(EVSEId), out var evse))
                evse.IsCharging = Charging;

        }

        #endregion

        #region StopAllSessions(Why)

        /// <summary>
        /// End every session, because the station is no longer the station they
        /// were started at.
        /// </summary>
        /// <remarks>
        /// Called when the EVSEs are rebuilt. A session belongs to an outlet,
        /// and after a change to what the outlets are there is no honest way to
        /// say which of the new ones it belongs to - EVSE 2 may have become a
        /// different socket, or stopped existing.
        /// </remarks>
        private void StopAllSessions(String Why)
        {

            if (sessions.IsEmpty)
                return;

            var ended = sessions.Count;

            foreach (var evseId in sessions.Keys)
                SetCharging(evseId, false);

            sessions.Clear();

            Log.Notice($"{ended} session(s) on the display were ended: {Why}.", "kiosk");

        }

        #endregion

        #endregion

    }

}
