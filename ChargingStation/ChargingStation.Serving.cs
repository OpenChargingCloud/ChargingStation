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

using cloud.charging.open.protocols.WWCP.Node.Connections;
using org.GraphDefined.Vanaheimr.Illias;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The station port of this station: a WebSocket server a CSMS or a local
    /// controller dials, where the station cannot dial them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The station port every node that serves OCPP has - see
    /// OCPPServingNode.OCPPServer.cs -, with its section "webSocketServer",
    /// the certificate of the listener "ocpp" of the node's store, and its
    /// pages. OCPP as it always is: this station is the charging station, what
    /// dials in is its back end, and only who opens the WebSocket is the other
    /// way round. Who may come in is written down on the Connections page, as
    /// a connection the other end makes - see WWCPNode.Accepting.cs.
    /// </para>
    /// <para>
    /// As every node, it lets in what has a WebSocket login, over a WebSocket
    /// and over SSH alike, where the file switches them on: a node that dials
    /// it with a login of its own is let in as any node would let it in.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What the station port offers where the file names no subprotocols:
        /// OCPP 2.1 and 2.0.1, handed to the node of OCPP 2.1, and OCPP 1.6,
        /// handed to the node of OCPP 1.6.
        /// </summary>
        public static readonly String[]  DefaultStationPortSubprotocols  = [ "ocpp2.1", "ocpp2.0.1", "ocpp1.6" ];

        #endregion

        #region (protected override) OCPPServerStateJSON(State)

        /// <summary>
        /// Who dialled in and is connected now, by the name it came under, and
        /// the connections the other end makes, as the station port waits for
        /// them.
        /// </summary>
        protected override void OCPPServerStateJSON(JObject State)
        {

            State.Add(new JProperty("connected",  new JArray(StationServers.
                                                                 SelectMany(server => server.ConnectedNetworkingNodeIds).
                                                                 Select    (name   => name.ToString()).
                                                                 Order     ())));

            State.Add(new JProperty("inbound",    new JArray(Connections.Connections.
                                                                 Where (connection => connection.Inbound).
                                                                 Select(connection => new JObject(
                                                                                          new JProperty("id",           connection.Id),
                                                                                          new JProperty("description",  connection.Description.ToJSON()),
                                                                                          new JProperty("name",         connection.InboundName),
                                                                                          new JProperty("ocppVersion",  ConnectionEntry.AsText(connection.OCPPVersion)),
                                                                                          new JProperty("letIn",        connection.AutoConnect)
                                                                                      )))));

        }

        #endregion

    }

}
