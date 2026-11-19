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
    /// A charging station serves no charging stations: a WebSocket under a
    /// name no such connection is written down under is turned away, and a
    /// client certificate none of them signs in with as well. Nor does it
    /// serve OCPP over SSH to anything but the tools that commission it - see
    /// OCPPServingNode.Commissioning.cs.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How many sign-ins that were wrong one address is given within a
        /// minute before the station port tells it to wait.
        /// </summary>
        public const UInt32  MaxFailedOCPPSignInsPerMinute  = 10;

        /// <summary>
        /// What the station port offers where the file names no subprotocols:
        /// OCPP 2.1 and 2.0.1, handed to the node of OCPP 2.1, and OCPP 1.6,
        /// handed to the node of OCPP 1.6.
        /// </summary>
        public static readonly String[]  DefaultStationPortSubprotocols  = [ "ocpp2.1", "ocpp2.0.1", "ocpp1.6" ];

        #endregion

        #region (protected override) ServesStations / ServesOCPPOverSSH / MaxFailedSignInsPerMinute

        /// <summary>
        /// None: what dials this station is its back end, and nothing else.
        /// </summary>
        protected override Boolean ServesStations
            => false;

        /// <summary>
        /// Not over SSH, whose subsystem is the tools' that commission this
        /// station, and over which nothing dials it.
        /// </summary>
        protected override Boolean ServesOCPPOverSSH
            => false;

        /// <summary>
        /// A password is guessed at from wherever the station port can be
        /// reached: so many wrong a minute, and then the address waits.
        /// </summary>
        protected override UInt32? MaxFailedSignInsPerMinute
            => MaxFailedOCPPSignInsPerMinute;

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
                                                                                          new JProperty("description",  connection.Description),
                                                                                          new JProperty("name",         connection.InboundName),
                                                                                          new JProperty("ocppVersion",  ConnectionEntry.AsText(connection.OCPPVersion)),
                                                                                          new JProperty("letIn",        connection.AutoConnect)
                                                                                      )))));

        }

        #endregion

    }

}
