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

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.ChargingStation.RFID;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    #region (enum) TokenAuthorization

    /// <summary>
    /// What this station's back end said of a card.
    /// </summary>
    public enum TokenAuthorization
    {

        /// <summary>
        /// Nobody was asked: this station has no back end that accepted it and
        /// that it is connected to right now, and is on its own.
        /// </summary>
        NotAsked,

        /// <summary>
        /// The back end accepted the card.
        /// </summary>
        Accepted,

        /// <summary>
        /// The back end answered, and not with "Accepted".
        /// </summary>
        Refused,

        /// <summary>
        /// The back end was asked and did not answer, or what came back was no
        /// answer to the question.
        /// </summary>
        Unanswered

    }

    #endregion


    /// <summary>
    /// A card is asked about before it starts anything: an OCPP Authorize to
    /// the back end in charge.
    /// </summary>
    /// <remarks>
    /// A UID is no authorization, and this station has none of its own to
    /// give: which cards may charge is the operator's to say, and the operator
    /// is at the other end of the connection. So a card held against a reader,
    /// and the UID a local app holds up, are sent to the back end this station
    /// is registered with - the primary of OCPP 2.1 where that one accepted
    /// it, else the primary of OCPP 1.6 - and start a session only where the
    /// answer is "Accepted".
    ///
    /// Only a start is asked about. The card that started a session stops it
    /// by being the same card, and whoever started a session has to be able to
    /// end it whatever the back end says by then.
    ///
    /// A station with no back end that accepted it, or one it is not
    /// connected to at the moment, is on its own and lets a card start as it
    /// did before there was anybody to ask: nothing here can tell a station
    /// that is offline from one that never had a back end, and one that
    /// stopped charging whenever its uplink was down would be a station
    /// nobody can rely on. What a station offline is to do with a card it
    /// cannot ask about is the operator's to decide one day - OCPP calls it
    /// OfflineTxForUnknownIdEnabled - and until then it is this.
    ///
    /// A back end that was asked and did not answer in time is not that case:
    /// the card is turned away, and told why.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How long a back end has to answer an Authorize: somebody is
        /// standing in front of the station with a card in their hand.
        /// </summary>
        public static readonly TimeSpan  AuthorizationTimeout  = TimeSpan.FromSeconds(10);

        #endregion


        #region (private) AuthorizeAtTheBackEnd(Token, CancellationToken)

        /// <summary>
        /// Ask the back end in charge whether the given card may charge.
        /// </summary>
        /// <param name="Token">The card.</param>
        /// <param name="CancellationToken">Ends the asking.</param>
        /// <returns>What it said, and for everything but "Accepted" and "NotAsked" the sentence that says so.</returns>
        private async Task<(TokenAuthorization Authorization, String? Why)> AuthorizeAtTheBackEnd(RFIDToken          Token,
                                                                                                  CancellationToken  CancellationToken)
        {

            // The node of OCPP 2.1 first, as everywhere else in this station.
            var registration = ReachableRegistrationOf(cs02) ??
                               ReachableRegistrationOf(cs01);

            if (registration is null)
                return (TokenAuthorization.NotAsked, null);

            var backEnd = BackEndOf(registration);

            try
            {

                var (answered, status, problem) = registration.Version == "1.6"

                    ? Of(await OCPPv1_6.CP.IChargePointNodeExtensions.Authorize(
                                   cs01,
                                   OCPPv1_6.IdToken.Parse(Token.UID),
                                   Destination:        SourceRouting.To(registration.Destination),
                                   RequestTimeout:     AuthorizationTimeout,
                                   CancellationToken:  CancellationToken
                               ))

                    : Of(await OCPPv2_1.CS.IChargingStationNodeExtensions.Authorize(
                                   cs02,
                                   new OCPPv2_1.IdToken(Token.UID, OCPPv2_1.IdTokenType.ISO14443),
                                   Destination:        SourceRouting.To(registration.Destination),
                                   RequestTimeout:     AuthorizationTimeout,
                                   CancellationToken:  CancellationToken
                               ));

                if (!answered)
                {
                    Log.Warning($"Card {Token} was not authorized: {backEnd} did not answer the Authorize ({problem}).", "rfid", "ocpp", "authorization");
                    return (TokenAuthorization.Unanswered, $"{backEnd} did not answer whether this card may charge.");
                }

                if (status == "Accepted")
                {
                    Log.Info($"Card {Token} was accepted by {backEnd}.", "rfid", "ocpp", "authorization");
                    return (TokenAuthorization.Accepted, null);
                }

                Log.Notice($"Card {Token} was turned away by {backEnd}: {status}.", "rfid", "ocpp", "authorization");

                return (TokenAuthorization.Refused, $"This card may not charge here ({status}).");

            }
            catch (Exception e)
            {
                Log.Warning($"Card {Token} was not authorized: asking {backEnd} failed ({e.Message}).", "rfid", "ocpp", "authorization");
                return (TokenAuthorization.Unanswered, $"{backEnd} could not be asked whether this card may charge.");
            }

        }

        #endregion

        #region (private) ReachableRegistrationOf(Node)

        /// <summary>
        /// The registration of the given node with the back end in charge of
        /// it, where that one accepted the node and its connection is up.
        /// </summary>
        private Registration? ReachableRegistrationOf(AWWCPNetworkingNode Node)
        {

            var registration = PrimaryRegistrationOf(Node);

            return registration is { State: RegistrationState.Accepted } &&
                   ConnectionStates.TryGetValue(registration.Connection.Id, out var state) &&
                   state.Status == ConnectionStatus.Connected

                       ? registration
                       : null;

        }

        #endregion

        #region (private static) Of(Response)

        // What both versions' answers have to say, the same way.
        private static (Boolean Answered, String Status, String? Problem) Of(OCPPv1_6.CS.AuthorizeResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.IdTagInfo.Status.ToString(), null)
                   : (false, "", Response.Result.Description ?? Response.Result.ResultCode.ToString());

        private static (Boolean Answered, String Status, String? Problem) Of(OCPPv2_1.CSMS.AuthorizeResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.IdTokenInfo.Status.ToString(), null)
                   : (false, "", Response.Result.Description ?? Response.Result.ResultCode.ToString());

        #endregion


        #region PresentToken(ReaderId, EVSEId, UID, CancellationToken)

        /// <summary>
        /// Hold a card against one of this station's readers - and, where it
        /// would start a session, ask the back end first whether it may.
        /// </summary>
        /// <remarks>
        /// What TryPresentToken does, with the question in front of it. A card
        /// that stops its own session is not asked about, and neither is one
        /// that would be turned away here anyway: at a reader this station
        /// does not have, or an outlet that is out of service.
        /// </remarks>
        /// <param name="ReaderId">Which reader, or null when the station has only one.</param>
        /// <param name="EVSEId">Which EVSE, needed when the reader serves the whole station.</param>
        /// <param name="UID">The card.</param>
        /// <param name="CancellationToken">Ends the asking.</param>
        /// <returns>What happened, for the display to show - or why nothing happened.</returns>
        public async Task<(JObject? Result, String? Error)> PresentToken(String?            ReaderId,
                                                                         Byte?              EVSEId,
                                                                         String?            UID,
                                                                         CancellationToken  CancellationToken   = default)
        {

            if (TryReadCard   (ReaderId, UID,    out var token, out var reader, out _) &&
                TryResolveEVSE(reader,   EVSEId, out var evse,                  out _) &&
                evse.Operative &&
                !sessions.ContainsKey(evse.Id))
            {

                var (authorization, why) = await AuthorizeAtTheBackEnd(token, CancellationToken);

                if (authorization is TokenAuthorization.Refused or TokenAuthorization.Unanswered)
                    return (null, why);

            }

            return TryPresentToken(ReaderId, EVSEId, UID, out var result, out var error)
                       ? (result, null)
                       : (null,   error);

        }

        #endregion

        #region StartLocally(EVSEId, UID, CancellationToken)

        /// <summary>
        /// Start a session for a local app, with the UID it holds up - once
        /// the back end said that card may charge.
        /// </summary>
        /// <remarks>
        /// What TryStartLocally does, with the question in front of it, and
        /// for the same reason as at a reader: the UID is asked about only
        /// where it could start something.
        /// </remarks>
        /// <param name="EVSEId">Which EVSE, or null when the station has only one.</param>
        /// <param name="UID">The card's UID, as a card reader would read it.</param>
        /// <param name="CancellationToken">Ends the asking.</param>
        /// <returns>What happened, including the handle to stop it with - or why nothing happened.</returns>
        public async Task<(JObject? Result, LocalAppRefusal Refusal, String? Error)> StartLocally(Byte?              EVSEId,
                                                                                                  String?            UID,
                                                                                                  CancellationToken  CancellationToken   = default)
        {

            if (RFIDToken.TryParse(UID, out var token, out _))
            {

                var evseId = EVSEId ?? (EVSEs.Count == 1 ? (Byte?) EVSEs.First().Id : null);
                var evse   = EVSEs.FirstOrDefault(candidate => candidate.Id == evseId);

                if (evse is { Operative: true } && !sessions.ContainsKey(evse.Id))
                {

                    var (authorization, why) = await AuthorizeAtTheBackEnd(token, CancellationToken);

                    if (authorization == TokenAuthorization.Refused)
                        return (null, LocalAppRefusal.NotAuthorized, why);

                    if (authorization == TokenAuthorization.Unanswered)
                        return (null, LocalAppRefusal.BackEndSilent, why);

                }

            }

            return TryStartLocally(EVSEId, UID, out var result, out var refusal, out var error)
                       ? (result, refusal, null)
                       : (null,   refusal, error);

        }

        #endregion

    }

}
