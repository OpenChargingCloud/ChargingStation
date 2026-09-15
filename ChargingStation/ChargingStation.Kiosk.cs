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
        /// A QR code with two seconds left is a code somebody points a phone at
        /// and then cannot use, which is worse than no code at all: the second
        /// attempt looks like the station is broken.
        ///
        /// What happens in that last stretch is that the *next* password is
        /// shown a few seconds early, not that nothing is shown. This used to
        /// say the display could simply wait, which measured badly: with a
        /// thirty-second validity the outlet had no code at all for four
        /// seconds out of every thirty - twice a minute, for as long as the
        /// station stands there - and the card jumped its whole layout each
        /// time as the code came and went.
        /// </remarks>
        public static readonly TimeSpan MinimumQRCodeTime = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The longest a display message may be.
        /// </summary>
        /// <remarks>
        /// A line somebody reads from three metres away while walking past. Not
        /// a rule of OCPP, which says nothing about length: a rule of the
        /// screen this ends up on.
        /// </remarks>
        public const Int32 MaxMessageLength = 200;

        /// <summary>
        /// The longest a display message may be asked to stay.
        /// </summary>
        public static readonly TimeSpan MaxMessageTime = TimeSpan.FromDays(7);

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
                                                         new JProperty("name",      Operator.Name),
                                                         new JProperty("logo",      Operator.Logo),
                                                         // What the screen speaks. The page has words of its
                                                         // own to put on it - "free", "scan to charge" - and
                                                         // they are no use in a language nobody there reads.
                                                         new JProperty("language",  Operator.Language)
                                                     )),

                       new JProperty("timestamp",    now.ToString("o")),

                       // The clock, and what it is worth. A charging station
                       // that shows a time somebody may later be billed against
                       // should say in the same breath whether that time has
                       // been checked, against whom, and how long ago.
                       new JProperty("clock",        ClockJSON()),

                       new JProperty("evses",        new JArray(EVSEs.Select(evse => EVSEPresentationJSON(evse, now, readers)))),

                       // The station-wide reader, when there is one. A reader
                       // per EVSE shows up beside its EVSE instead; both at
                       // once cannot happen, because a station has at most one
                       // reader per place and the whole housing is a place.
                       new JProperty("rfid",         stationReader is null
                                                         ? null
                                                         : ReaderJSON(stationReader)),

                       // A reservation that names no EVSE belongs over the
                       // whole station rather than beside an outlet: it is a
                       // promise that one will be free, not a claim on any one
                       // of them.
                       new JProperty("holds",        StationHoldJSON(now)),

                       // What a back end asked this station to say, for the
                       // housing as a whole. The ones tied to an outlet are on
                       // that outlet below.
                       new JProperty("messages",     DisplayMessagesJSON(StationMessageState(), null, now)),

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

            // A car that is charging is charging, whatever the
            // configuration now says about the outlet it is on: an outlet taken
            // out of service under a running session goes out of service when
            // that session ends, not while somebody is standing at it. Out of
            // service beats held, because a hold on an outlet nobody can use is
            // let go of at the same moment.
            var status  = session is not null
                              ? EVSEStatus.Occupied
                              : !EVSE.Operative
                                    ? EVSEStatus.Inoperative
                                    : reservation is not null
                                          ? EVSEStatus.Reserved
                                          : EVSEStatus.Available;

            // Charging now, and not to be used again afterwards. The one thing
            // the person on this cable needs to know that nobody else does.
            var closing = session is not null && !EVSE.Operative;

            var reader  = Readers.FirstOrDefault(candidate => candidate.EVSEId == EVSE.Id && candidate.Enabled);

            var qrCode  = status == EVSEStatus.Available
                              ? WebPaymentURL(EVSE.Id, Now)
                              : null;

            return new JObject(

                       new JProperty("id",                 EVSE.Id),
                       new JProperty("label",              EVSE.PhysicalReference ?? EVSE.Id.ToString()),
                       new JProperty("status",             status.ToString().ToLowerInvariant()),
                       new JProperty("closing",            closing),

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

                       new JProperty("messages",           DisplayMessagesJSON(MessageStateOf(EVSE), EVSE.Id, Now)),

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
        /// A password that is about to run out is not shown; the one that is
        /// about to begin is shown instead. TOTP is verified against three
        /// passwords - the one before, the one now and the one next - which is
        /// what makes that safe, and it is the same triple this generator hands
        /// back. So the code on the screen is always one somebody can use, and
        /// there is never a moment with no way to pay at an outlet that is
        /// standing free.
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

                var (_, current, next, remaining, endTime) = TOTPGenerator.GenerateURLs(
                                                                     template,
                                                                     webPayments.SharedSecret ?? "",
                                                                     webPayments.ValidityTime,
                                                                     webPayments.TOTPLength,
                                                                     Timestamp: Now
                                                                 );

                // Near the end of a slot, hand out the one that is about to
                // begin, along with when *it* runs out - so a phone that reads
                // the screen in the last second of a slot still has a whole
                // slot to pay in.
                return remaining < MinimumQRCodeTime
                           ? (next,    endTime + (webPayments.ValidityTime ?? TimeSpan.FromSeconds(30)))
                           : (current, endTime);

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

            if (!TryReadCard(ReaderId, UID, out var token, out var reader, out Error) ||
                !TryResolveEVSE(reader, EVSEId, out var evse, out Error))
            {
                return false;
            }

            // Out of service turns cards away - except the one that is
            // already charging here. Whoever started a session has to be able to
            // end it, and an outlet does not stop being their way of doing that
            // because an operator ticked a box while they were away.
            if (!evse.Operative && !sessions.ContainsKey(evse.Id))
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

        #region (private) TryReadCard(ReaderId, EVSEId, UID, out Token, out Reader, out EVSE, out Error)

        /// <summary>
        /// The card, the reader it was held against and the outlet it is for -
        /// or the sentence saying why none of that came together.
        /// </summary>
        /// <remarks>
        /// The narrow door every card on the display comes through, whether it
        /// is starting a charge or letting a reservation go. The reader has to
        /// exist, be switched on, and be one whose cards are typed in: a
        /// station with only real readers configured answers every card sent to
        /// it with a refusal, no matter what the request says.
        /// </remarks>
        private Boolean TryReadCard(String?                                 ReaderId,
                                    String?                                 UID,
                                    [NotNullWhen(true)]  out RFIDToken?        Token,
                                    [NotNullWhen(true)]  out RFIDReaderConfig? Reader,
                                    [NotNullWhen(false)] out String?           Error)
        {

            Token   = null;
            Reader  = null;

            if (!RFIDToken.TryParse(UID, out Token, out Error))
                return false;

            var readers = RFIDReaders.Where(reader => reader.Enabled).ToArray();

            Reader = ReaderId is not null
                         ? readers.FirstOrDefault(candidate => String.Equals(candidate.Id, ReaderId, StringComparison.OrdinalIgnoreCase))
                         : readers.Length == 1
                               ? readers[0]
                               : null;

            if (Reader is null)
            {
                Error = ReaderId is null
                            ? "This station has more than one RFID reader; say which one."
                            : $"This station has no RFID reader called '{ReaderId}' switched on.";
                return false;
            }

            if (!Reader.IsFake)
            {
                // Not a refusal about permission but about physics: a real
                // reader reads cards, and there is no way to hand one a card
                // over HTTP.
                Error = $"'{Reader.Id}' is a {Reader.Kind} reader; a card is held against it, not typed into it.";
                Reader = null;
                return false;
            }

            return true;

        }

        #endregion

        #region (private) TryResolveEVSE(Reader, EVSEId, out EVSE, out Error)

        /// <summary>
        /// Which outlet a card held against the given reader is for.
        /// </summary>
        /// <remarks>
        /// A reader beside an outlet answers this by standing where it stands,
        /// and what the request says about it is beside the point: somebody
        /// holding a card against the reader on EVSE 1 is at EVSE 1. A reader
        /// for the whole housing has to be told.
        /// </remarks>
        private Boolean TryResolveEVSE(RFIDReaderConfig                 Reader,
                                       Byte?                            EVSEId,
                                       [NotNullWhen(true)]  out EVSEConfig? EVSE,
                                       [NotNullWhen(false)] out String?     Error)
        {

            EVSE   = null;
            Error  = null;

            var evseId = Reader.EVSEId ?? EVSEId;

            if (evseId is null)
            {
                Error = "This reader serves the whole station, so it needs to be told which EVSE the card is for.";
                return false;
            }

            EVSE = EVSEs.FirstOrDefault(candidate => candidate.Id == evseId);

            if (EVSE is null)
            {
                Error = $"This station has no EVSE {evseId}.";
                return false;
            }

            return true;

        }

        #endregion

        #region TryReleaseReservation(ReaderId, EVSEId, UID, out Result, out Error)

        /// <summary>
        /// Let a held outlet go again, from the display.
        /// </summary>
        /// <remarks>
        /// The second and last thing the display can change, and it is the same
        /// rule as the first: the only way to say that a reservation is yours,
        /// at a screen with no sign-in in front of it, is to hold the card it
        /// is held for against the reader. Anybody walking past can press the
        /// button; only the card gets anywhere.
        ///
        /// A card that is not the one is turned away without being told how
        /// close it was, and the display never showed the token to begin with -
        /// see <see cref="ReservationJSON"/>.
        ///
        /// Which reservation is meant comes from the request rather than from
        /// where the reader stands, and <paramref name="EVSEId"/> being null
        /// means the one over the whole station - the hold that names no outlet
        /// and therefore has no outlet to be let go at. The reader is the thing
        /// that reads a card here, not the thing that says which promise is
        /// being given back.
        /// </remarks>
        /// <param name="EVSEId">The outlet whose hold is meant, or null for the one over the whole station.</param>
        public Boolean TryReleaseReservation(String?                           ReaderId,
                                             Byte?                             EVSEId,
                                             String?                           UID,
                                             [NotNullWhen(true)]  out JObject? Result,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Result = null;

            if (!TryReadCard(ReaderId, UID, out var token, out var reader, out Error))
                return false;

            var card  = new OCPPv2_1.IdToken(token.UID, OCPPv2_1.IdTokenType.ISO14443);
            var where = EVSEId.HasValue ? $"EVSE {EVSEId.Value}" : "this station";

            var reservation = EVSEId.HasValue
                                  ? cs02.ReservationOf(OCPPv2_1.EVSE_Id.Parse(EVSEId.Value))
                                  // Among the holds that name no outlet, the
                                  // one this card can speak for. Somebody
                                  // else's is none of this card's business, and
                                  // saying so would say that it exists.
                                  : cs02.Reservations.FirstOrDefault(hold => !hold.EVSEId.HasValue && hold.Admits(card));

            if (reservation is null)
            {
                Error = EVSEId.HasValue
                            ? $"EVSE {EVSEId.Value} is not being held for anybody."
                            : "This station is not holding anything for that card.";
                return false;
            }

            if (!reservation.Admits(card))
            {
                Log.Notice(
                    $"Card {token} tried to let go of the reservation {reservation.Id} at {where} and is not the card it is held for.",
                    "rfid", "reservation", "kiosk"
                );
                Error = $"That is not the card {(EVSEId.HasValue ? "this EVSE" : "this station")} is being held for.";
                return false;
            }

            cs02.CancelReservation(reservation.Id);

            Log.Notice(
                $"Card {token} let go of the reservation {reservation.Id} at {where} (reader '{reader.Id}').",
                "rfid", "reservation", "kiosk"
            );

            Result = new JObject(
                         new JProperty("evse",       EVSEId.HasValue ? EVSEId.Value : null),
                         new JProperty("uid",        token.UID),
                         new JProperty("cancelled",  true)
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
