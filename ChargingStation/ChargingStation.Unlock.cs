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

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What came of letting go of a cable.
    /// </summary>
    public enum UnlockResult
    {

        /// <summary>
        /// The cable was let go of.
        /// </summary>
        Unlocked,

        /// <summary>
        /// Somebody is charging there, and a cable somebody charges through
        /// is not let go of: the session is stopped first, by whoever may.
        /// </summary>
        Charging,

        /// <summary>
        /// This station has no such EVSE.
        /// </summary>
        UnknownEVSE,

        /// <summary>
        /// The EVSE has no such connector.
        /// </summary>
        UnknownConnector

    }


    /// <summary>
    /// A cable that is stuck, let go of: an UnlockConnector of OCPP 2.1 and of
    /// OCPP 1.6, and the same on the web page and at the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only where nobody is charging. OCPP 2.1 has a word for the other case,
    /// "OngoingAuthorizedTransaction", and this station says it; OCPP 1.6 has
    /// none and says "UnlockFailed" - and where its text ends the transaction
    /// to let go of the cable, this station does not: who may let go of a
    /// cable, the operator's telephone support, may stop no session (white
    /// paper "Signed OCPP Messages and Signer Roles", section 5.3), and so
    /// letting go of one is not a way of stopping one.
    /// </para>
    /// <para>
    /// The connectors of this station have no lock it could drive, so what it
    /// lets go of is written into the log and nothing moves; a station with a
    /// lock opens it where this one writes.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region (private) AttachUnlock(ChargePoint, ChargingStation)

        /// <summary>
        /// Answer the UnlockConnectors both new OCPP nodes are sent.
        /// </summary>
        private void AttachUnlock(OCPPv1_6.TestChargePointNode         ChargePoint,
                                  OCPPv2_1.CS.TestChargingStationNode  ChargingStation)
        {

            // The node of OCPP 1.6 answers no UnlockConnector by itself.
            ChargePoint.OCPP.IN.OnUnlockConnector += (timestamp, sender, connection, request, cancellationToken) => {

                // Connector 0 is the charge point as a whole, which has no cable.
                var result = request.ConnectorId.Value is > 0 and <= Byte.MaxValue
                                 ? Unlock((Byte) request.ConnectorId.Value, null, "an UnlockConnector over OCPP 1.6")
                                 : UnlockResult.UnknownEVSE;

                return Task.FromResult(
                           new OCPPv1_6.CP.UnlockConnectorResponse(
                               request,
                               result switch {
                                   UnlockResult.Unlocked  => OCPPv1_6.UnlockStatus.Unlocked,
                                   UnlockResult.Charging  => OCPPv1_6.UnlockStatus.UnlockFailed,
                                   _                      => OCPPv1_6.UnlockStatus.NotSupported
                               }
                           )
                       );

            };

            ChargingStation.OnUnlockConnectorByOwner = (request, cancellationToken) => {

                var result = request.EVSEId.Value is > 0 and <= Byte.MaxValue
                                 ? Unlock((Byte) request.EVSEId.Value, request.ConnectorId.Value, "an UnlockConnector over OCPP 2.1")
                                 : UnlockResult.UnknownEVSE;

                return Task.FromResult<OCPPv2_1.CS.UnlockConnectorResponse?>(
                           new OCPPv2_1.CS.UnlockConnectorResponse(
                               request,
                               result switch {
                                   UnlockResult.Unlocked  => OCPPv2_1.UnlockStatus.Unlocked,
                                   UnlockResult.Charging  => OCPPv2_1.UnlockStatus.OngoingAuthorizedTransaction,
                                   _                      => OCPPv2_1.UnlockStatus.UnknownConnector
                               }
                           )
                       );

            };

        }

        #endregion

        #region Unlock(EVSEId, Connector, By)

        /// <summary>
        /// Let go of the cable at an EVSE, where nobody is charging there.
        /// </summary>
        /// <param name="EVSEId">The EVSE.</param>
        /// <param name="Connector">Which of its connectors, counted from 1; null for all of them.</param>
        /// <param name="By">Who asked for it, for the log: "an UnlockConnector over OCPP 2.1".</param>
        public UnlockResult Unlock(Byte     EVSEId,
                                   Byte?    Connector,
                                   String   By)
        {

            var evse = EVSEs.FirstOrDefault(evse => evse.Id == EVSEId);

            if (evse is null)
                return UnlockResult.UnknownEVSE;

            if (Connector is Byte connector && (connector == 0 || connector > evse.Connectors.Count))
                return UnlockResult.UnknownConnector;

            if (sessions.ContainsKey(EVSEId))
            {
                Log.Notice($"{Capitalized(By)} asked to let go of the cable at EVSE {EVSEId}, where somebody is charging: it is held.", "evses", "unlock");
                return UnlockResult.Charging;
            }

            Log.Notice($"The cable at EVSE {EVSEId}{(Connector is Byte one ? $", connector {one}," : "")} was let go of, as {By} asked for.", "evses", "unlock");

            return UnlockResult.Unlocked;

        }

        #endregion

        #region (static) Said(UnlockResult, EVSEId)

        /// <summary>
        /// What came of letting go of a cable, as a sentence for the page and
        /// the command line.
        /// </summary>
        public static String Said(UnlockResult  Result,
                                  Byte          EVSEId)

            => Result switch {
                   UnlockResult.Unlocked          => $"The cable at EVSE {EVSEId} was let go of.",
                   UnlockResult.Charging          => $"Somebody is charging at EVSE {EVSEId}: stop the session first.",
                   UnlockResult.UnknownConnector  => $"EVSE {EVSEId} has no such connector.",
                   _                              => $"This station has no EVSE {EVSEId}."
               };

        #endregion

    }

}
