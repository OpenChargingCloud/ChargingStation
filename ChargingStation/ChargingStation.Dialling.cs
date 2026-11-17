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

using cloud.charging.open.protocols.OCPP.Node;
using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What a charging station says of itself to the dialling it shares with
    /// every node that dials a CSMS: its two OCPP nodes, what they keep for
    /// a CSMS out of reach, and the SSH client of its device model.
    /// </summary>
    public partial class ChargingStation
    {

        #region (static) Constructor

        /// <summary>
        /// What the connections of this process say of the node they are
        /// the connections of: it is a station.
        /// </summary>
        static ChargingStation()
        {
            NodeWording.Is("station", "charging station");
        }

        #endregion

        #region OCPPDialling / Nodes

        /// <summary>
        /// What OCPP says of the connections of this node.
        /// </summary>
        public OCPPDialling OCPPDialling
            => (OCPPDialling) Dialling;

        /// <summary>
        /// The OCPP nodes this one has: the one for OCPP 2.1, and before it
        /// the one for OCPP 1.6 where it speaks that.
        /// </summary>
        protected IEnumerable<cloud.charging.open.protocols.OCPP.NetworkingNode.AOCPPNetworkingNode> Nodes
            => OCPPDialling.Nodes;

        #endregion

        #region Node16 / Node21

        /// <summary>
        /// The node that speaks OCPP 1.6 for this station.
        /// </summary>
        protected OCPPv1_6.NetworkingNode.AOCPPNetworkingNode? Node16
            => cs01;

        /// <summary>
        /// The node that speaks OCPP 2.1 for this station.
        /// </summary>
        protected OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node21
            => cs02;

        #endregion

        #region (protected override) EndRegistrationOf(Connection)

        /// <summary>
        /// Nothing more is asked of the back end over the given connection, nor
        /// waited for: no BootNotification, no heartbeat.
        /// </summary>
        protected override void EndRegistrationOf(ConnectionEntry Connection)
        {

            AWWCPNetworkingNode node = Connection.OCPPVersion == OCPPVersion.OCPP1_6 ? cs01 : cs02;

            if (registrations.TryGetValue(node, out var nodeRegistrations) &&
                nodeRegistrations.ByConnection.TryRemove(Connection.Id, out var registration))
            {
                StopTheHeartbeat(registration);
                registration.Asking?.Cancel();
            }

        }

        #endregion

        #region (protected override) FlushOfflineQueueOf(Version) / QueueSecurityEvent(Version, Type, TechInfo)

        /// <summary>
        /// Send what the node of the given version of OCPP kept for its CSMS
        /// while it was out of reach.
        /// </summary>
        protected override Task FlushOfflineQueueOf(OCPPVersion Version)

            => Version == OCPPVersion.OCPP1_6
                   ? cs01.FlushOfflineQueueAsync()
                   : cs02.FlushOfflineQueueAsync();

        /// <summary>
        /// Tell the CSMS of a security event, by the node of the given
        /// version of OCPP, now or once it is in reach.
        /// </summary>
        protected override void QueueSecurityEvent(OCPPVersion  Version,
                                                   String       Type,
                                                   String       TechInfo)
        {

            if (Version == OCPPVersion.OCPP1_6)
                _ = cs01.QueueSecurityEventAsync(OCPPv1_6.SecurityEvent.    Parse(Type), TechInfo);

            else
                _ = cs02.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo);

        }

        #endregion

        #region (private) StationDialling

        /// <summary>
        /// OCPP as every node that dials over it speaks it, with the Device
        /// Model of this station's OCPP 2.1 node.
        /// </summary>
        private sealed class StationDialling(ChargingStation Station)

            : OCPPDialling(Station, () => Station.Node16, () => Station.Node21)

        {


            public override IEnumerable<OCPPv2_1.SSHClientController> SSHClientControllersOf(OCPPv2_1.NetworkingNode.AOCPPNetworkingNode Node)

                => Node is OCPPv2_1.CS.TestChargingStationNode station
                       ? station.GetComponentConfigs(nameof(OCPPv2_1.SSHClientController)).OfType<OCPPv2_1.SSHClientController>()
                       : [];

            public override void AddComponentTo(OCPPv2_1.NetworkingNode.AOCPPNetworkingNode  Node,
                                                   OCPPv2_1.ComponentConfig                     Component)
            {
                if (Node is OCPPv2_1.CS.TestChargingStationNode station)
                    station.AddComponent(Component);
            }

            public override void RemoveComponentFrom(OCPPv2_1.NetworkingNode.AOCPPNetworkingNode  Node,
                                                        String                                       Name,
                                                        String?                                      Instance)
            {
                if (Node is OCPPv2_1.CS.TestChargingStationNode station)
                    station.RemoveComponent(Name, Instance);
            }

            public override void AnswerCustomTriggersOf(OCPPv2_1.NetworkingNode.AOCPPNetworkingNode                                          Node,
                                                           Func<OCPPv2_1.CSMS.TriggerMessageRequest, Task<OCPPv2_1.CS.TriggerMessageResponse?>>  Answer)
            {
                if (Node is OCPPv2_1.CS.TestChargingStationNode station)
                    station.OnCustomTrigger = Answer;
            }

        }

        #endregion

    }

}
