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
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.RFID;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Why a local app was turned away.
    /// </summary>
    public enum LocalAppRefusal
    {

        /// <summary>
        /// What was sent cannot be read as a request: no UID, a UID that is no
        /// UID, an EVSE this station does not have.
        /// </summary>
        Invalid,

        /// <summary>
        /// No session with that handle is running here: it has ended, or it
        /// never was.
        /// </summary>
        Unknown,

        /// <summary>
        /// The EVSE is not free for it: charging, held for somebody else, or
        /// out of service.
        /// </summary>
        Busy,

        /// <summary>
        /// Something this station is going to check one day and does not yet -
        /// a one-time password, a certificate.
        /// </summary>
        NotYet

    }


    /// <summary>
    /// What an app on a phone in the station's own network can do: start a
    /// session with a card's UID, and stop the session it started.
    /// </summary>
    /// <remarks>
    /// Reached through <see cref="LocalAppHTTPAPI"/>, on a server and a port of
    /// its own. Everything here is also what the display does with a card,
    /// minus the reader, which is why it lives beside ChargingStation.Kiosk.cs
    /// and uses the same sessions.
    /// </remarks>
    public partial class ChargingStation
    {

        #region TryStartLocally(EVSEId, UID, out Result, out Refusal, out Error)

        /// <summary>
        /// Start a session for a local app, with the UID it holds up.
        /// </summary>
        /// <remarks>
        /// A card held against a reader, without the reader: the same rules for
        /// what a UID is, the same rules for the outlet - out of service turns
        /// it away, a reservation admits the card it is held for and nobody
        /// else - and, as for a card, no authorization of the UID itself, which
        /// this station has for nothing.
        ///
        /// What differs is how it ends. A card stops what it started by being
        /// held up again; an app is given a handle instead, and the handle is
        /// the one way it has of ending the session - see
        /// <see cref="TryStopLocally"/>. The same card at a reader still stops
        /// it, because it is the same card.
        ///
        /// An occupied outlet is refused rather than toggled. An app that asks
        /// to start and is answered with a stop has been misunderstood.
        /// </remarks>
        /// <param name="EVSEId">Which EVSE, or null when the station has only one.</param>
        /// <param name="UID">The card's UID, as a card reader would read it.</param>
        /// <param name="Result">What happened, including the handle to stop it with.</param>
        /// <param name="Refusal">Why nothing happened, when nothing did.</param>
        /// <param name="Error">The same, in a sentence.</param>
        public Boolean TryStartLocally(Byte?                             EVSEId,
                                       String?                           UID,
                                       [NotNullWhen(true)]  out JObject? Result,
                                       out LocalAppRefusal               Refusal,
                                       [NotNullWhen(false)] out String?  Error)
        {

            Result   = null;
            Refusal  = LocalAppRefusal.Invalid;

            if (!RFIDToken.TryParse(UID, out var token, out Error))
                return false;

            // An app has no reader standing beside an outlet to say which one
            // it is at, and a station with a single outlet leaves nothing to
            // say.
            var evseId = EVSEId ?? (EVSEs.Count == 1 ? (Byte?) EVSEs.First().Id : null);

            if (evseId is null)
            {
                Error = "This station has more than one EVSE; say which one with \"evse\".";
                return false;
            }

            var evse = EVSEs.FirstOrDefault(candidate => candidate.Id == evseId);

            if (evse is null)
            {
                Error = $"This station has no EVSE {evseId}.";
                return false;
            }

            Refusal = LocalAppRefusal.Busy;

            if (!evse.Operative)
            {
                Error = $"EVSE {evse.Id} is out of service.";
                return false;
            }

            if (sessions.ContainsKey(evse.Id))
            {
                Error = $"EVSE {evse.Id} is already charging.";
                return false;
            }

            var now          = TimeProvider.GetUtcNow();
            var reservation  = cs02.ReservationOf(OCPPv2_1.EVSE_Id.Parse(evse.Id));

            // As a type of token, the card the app holds up is a card: a hold
            // made for somebody's card lets in their app, and nobody else's.
            if (reservation is not null &&
                !reservation.Admits(new OCPPv2_1.IdToken(token.UID, OCPPv2_1.IdTokenType.ISO14443)))
            {
                Log.Notice(
                    $"The local app was turned away from EVSE {evse.Id} with card {token}: it is held under reservation {reservation.Id} " +
                    $"until {reservation.ExpiryDate:HH:mm:ss}.",
                    "localapp", "reservation", "kiosk"
                );
                Error = $"EVSE {evse.Id} is reserved until {reservation.ExpiryDate.ToLocalTime():HH:mm}.";
                return false;
            }

            // A hundred and twenty-eight random bits: the handle is the whole of
            // what it takes to stop this session, so it must not be guessable
            // from the ones before it, and nobody sends it but the app it was
            // given to.
            var sessionId  = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var provider   = Operator.ProviderOf(token);

            // Added only where nothing is. The display and an app on the
            // network can ask at the same moment, and whichever comes second is
            // told so rather than overwriting the first.
            if (!sessions.TryAdd(evse.Id, ChargingSession.ForLocalApp(evse.Id, token.UID, provider, now, sessionId)))
            {
                Error = $"EVSE {evse.Id} is already charging.";
                return false;
            }

            SetCharging(evse.Id, true);

            // As for a card: the person the outlet was held for is here.
            if (reservation is not null)
            {
                cs02.CancelReservation(reservation.Id);
                Log.Notice($"The reservation {reservation.Id} was taken up at EVSE {evse.Id}.", "localapp", "reservation", "kiosk");
            }

            Log.Notice(
                $"The local app started a session at EVSE {evse.Id} with card {token} " +
                (provider is null ? "for a provider this station does not recognise." : $"for {provider.Name}."),
                "localapp", "kiosk"
            );

            Result = new JObject(
                         new JProperty("sessionId",  sessionId),
                         new JProperty("evse",       evse.Id),
                         new JProperty("uid",        token.UID),
                         new JProperty("started",    true)
                     );

            Error = null;
            return true;

        }

        #endregion

        #region TryStopLocally(SessionId, out Result, out Refusal, out Error)

        /// <summary>
        /// Stop the session a local app was given this handle for.
        /// </summary>
        /// <remarks>
        /// The handle is the whole of the authorization, as the card is at the
        /// display: whoever holds it started the session or was given the
        /// handle by whoever did. A handle that stops nothing is answered the
        /// same way whether it never was one or its session has ended, so that
        /// sending guesses tells nobody which of them came close.
        /// </remarks>
        /// <param name="SessionId">The handle the app was given when it started.</param>
        /// <param name="Result">What happened.</param>
        /// <param name="Refusal">Why nothing happened, when nothing did.</param>
        /// <param name="Error">The same, in a sentence.</param>
        public Boolean TryStopLocally(String?                           SessionId,
                                      [NotNullWhen(true)]  out JObject? Result,
                                      out LocalAppRefusal               Refusal,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Result   = null;
            Refusal  = LocalAppRefusal.Unknown;

            // Every session compared, each the same careful way, rather than
            // stopping at the first that matches.
            var found = sessions.ToArray().
                                 Where  (pair => pair.Value.WasStartedWith(SessionId)).
                                 ToArray();

            // Removed only if it is still that session: one that ended a moment
            // ago and was followed by another at the same outlet is not this
            // handle's to stop.
            if (found.Length != 1 || !sessions.TryRemove(found[0]))
            {
                Error = "No session with that handle is running here.";
                return false;
            }

            var (evseId, ended) = (found[0].Key, found[0].Value);

            SetCharging(evseId, false);

            var seconds = (TimeProvider.GetUtcNow() - ended.StartedAt).TotalSeconds;

            Log.Notice($"The local app stopped the session at EVSE {evseId} after {seconds:F0} s.", "localapp", "kiosk");

            Result = new JObject(
                         new JProperty("sessionId",  SessionId),
                         new JProperty("evse",       evseId),
                         new JProperty("seconds",    Math.Round(seconds)),
                         new JProperty("stopped",    true)
                     );

            Error = null;
            return true;

        }

        #endregion

    }

}
