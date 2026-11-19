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

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Registering with the CSMS: what a charging station says of itself in
    /// its BootNotification, and what it does before and after - the rest is
    /// what every node that serves OCPP does, see
    /// OCPPServingNode.Registration.cs.
    /// </summary>
    /// <remarks>
    /// Until a CSMS accepts the BootNotification, a station tells it nothing
    /// of its own: what it has to say waits in the queue of its OCPP node.
    /// Accepted, the node sends its heartbeats to the primary by itself, and
    /// the back end is told how each EVSE stands.
    ///
    /// A node built anew because the EVSEs or what this station says it is
    /// changed is a new start: it hangs up, dials again and registers again,
    /// with the BootReason LocalReset in OCPP 2.1 rather than PowerUp.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        private Boolean nodesBuiltBefore;

        #endregion


        #region (private) NewRegistrations(ChargePoint, ChargingStation)

        /// <summary>
        /// Two nodes just built, neither of them registered: PowerUp for the
        /// first two this station builds, LocalReset for every two after.
        /// </summary>
        private void NewRegistrations(OCPPv1_6.TestChargePointNode         ChargePoint,
                                      OCPPv2_1.CS.TestChargingStationNode  ChargingStation)
        {

            var reason = nodesBuiltBefore
                             ? OCPPv2_1.BootReason.LocalReset
                             : OCPPv2_1.BootReason.PowerUp;

            nodesBuiltBefore = true;

            NewRegistrations(reason, ChargePoint, ChargingStation);

        }

        #endregion

        #region (protected override) ConnectionIsUp(Connection)

        /// <summary>
        /// A connection is up: a key older than its maximum age is rotated, and
        /// a station certificate due is renewed, once a back end can be told -
        /// and registered as every node registers.
        /// </summary>
        protected override void ConnectionIsUp(ConnectionEntry Connection)
        {

            _ = OCPPDialling.RotateIfOld();
            _ = RenewStationCertificateIfDue();

            base.ConnectionIsUp(Connection);

        }

        #endregion

        #region (protected override) NodeBeatsByItself(Node) / HoldWhatWaits(Node, Hold) / WhenAccepted(Node, Registration)

        /// <summary>
        /// The OCPP nodes of a station send their heartbeats to the primary
        /// themselves.
        /// </summary>
        protected override Boolean NodeBeatsByItself(AWWCPNetworkingNode Node)
            => true;

        /// <summary>
        /// What the node queued for its CSMS waits until the CSMS accepted it.
        /// </summary>
        protected override void HoldWhatWaits(AWWCPNetworkingNode  Node,
                                              Boolean              Hold)
        {
            switch (Node)
            {
                case OCPPv1_6.TestChargePointNode         chargePoint:      chargePoint.    HoldOfflineQueue = Hold; break;
                case OCPPv2_1.CS.TestChargingStationNode  chargingStation:  chargingStation.HoldOfflineQueue = Hold; break;
            }
        }

        /// <summary>
        /// What a back end is told first of a station it accepted, and again
        /// when it comes back: how each of its EVSEs stands.
        /// </summary>
        protected override void WhenAccepted(AWWCPNetworkingNode  Node,
                                             Registration         Registration)

            => TellHowTheEVSEsStand(Node, Registration);

        #endregion

        #region (protected override) SendBootNotification(Node, Destination, Reason, CancellationToken) / SendHeartbeat(...)

        /// <summary>
        /// The BootNotification of either node, built here rather than with the
        /// node's own SendBootNotification, which says a serial number the node
        /// made up where this station has none.
        /// </summary>
        protected override async Task<(Boolean Answered, String Status, TimeSpan Interval, String? Problem)>

            SendBootNotification(AWWCPNetworkingNode  Node,
                                 NetworkingNode_Id    Destination,
                                 OCPPv2_1.BootReason  Reason,
                                 CancellationToken    CancellationToken)

            => Node switch {

                   OCPPv1_6.TestChargePointNode chargePoint
                       => Of(await chargePoint.OCPP.OUT.BootNotification(
                                       new OCPPv1_6.CP.BootNotificationRequest(
                                           Destination:              SourceRouting.To(Destination),
                                           ChargePointVendor:        chargePoint.ChargePointVendor,
                                           ChargePointModel:         chargePoint.ChargePointModel,
                                           ChargePointSerialNumber:  Nameplate.SerialNumber,
                                           FirmwareVersion:          chargePoint.FirmwareVersion,
                                           RequestId:                chargePoint.NextRequestId,
                                           RequestTimestamp:         chargePoint.Now,
                                           RequestTimeout:           chargePoint.OCPP.DefaultRequestTimeout,
                                           NetworkPath:              NetworkPath.From(chargePoint.Id),
                                           CancellationToken:        CancellationToken
                                       )
                                   )),

                   OCPPv2_1.CS.TestChargingStationNode chargingStation
                       => Of(await OCPPv2_1.CS.IChargingStationNodeExtensions.SendBootNotification(
                                       chargingStation,
                                       Reason,
                                       Destination:        SourceRouting.To(Destination),
                                       CancellationToken:  CancellationToken
                                   )),

                   _   => (false, "", TimeSpan.Zero, $"'{Node.Id}' is no OCPP node this station knows how to register.")

               };

        /// <summary>
        /// A heartbeat of either node, to a back end other than the primary:
        /// the node's own goes to the primary alone.
        /// </summary>
        protected override async Task SendHeartbeat(AWWCPNetworkingNode  Node,
                                                    NetworkingNode_Id    Destination,
                                                    CancellationToken    CancellationToken)
        {

            var destination = SourceRouting.To(Destination);

            switch (Node)
            {

                case OCPPv1_6.TestChargePointNode chargePoint:
                    await OCPPv1_6.CP.IChargePointNodeExtensions.SendHeartbeat(
                              chargePoint,
                              Destination:        destination,
                              CancellationToken:  CancellationToken
                          );
                    break;

                case OCPPv2_1.CS.TestChargingStationNode chargingStation:
                    await OCPPv2_1.CS.IChargingStationNodeExtensions.SendHeartbeat(
                              chargingStation,
                              Destination:        destination,
                              CancellationToken:  CancellationToken
                          );
                    break;

            }

        }

        #endregion

        #region (private static) Of(Response)

        // What both versions' answers have to say, the same way.
        private static (Boolean Answered, String Status, TimeSpan Interval, String? Problem) Of(OCPPv1_6.CS.BootNotificationResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.Status.ToString(), Response.HeartbeatInterval, null)
                   : (false, "", TimeSpan.Zero, Response.Result.Description ?? Response.Result.ResultCode.ToString());

        private static (Boolean Answered, String Status, TimeSpan Interval, String? Problem) Of(OCPPv2_1.CSMS.BootNotificationResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.Status.ToString(), Response.Interval, null)
                   : (false, "", TimeSpan.Zero, Response.Result.Description ?? Response.Result.ResultCode.ToString());

        #endregion

    }

}
