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

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    /// <summary>
    /// What a charging station adds to the resources every node has, and the
    /// roles of the people around it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A role is a list of permissions, each an operation on a resource, as on
    /// every node - and asked of the node, so that a role means here what it
    /// means on a vehicle. The node brings the viewer, who may look at
    /// everything, and the administrators, who may do everything - the
    /// certificate store included, which is the one resource nobody else may
    /// edit: somebody who can add a root can make this station believe a
    /// server nobody else would.
    /// </para>
    /// <para>
    /// The resources are what this station is made of and what it does, and
    /// three of them are kept apart although they are about the same EVSE,
    /// because they are different kinds of statement for different people: a
    /// switch, a number, and a claim about what is bolted to the wall.
    /// <see cref="Availability"/> takes an EVSE or a card reader out of
    /// service and back, <see cref="Power"/> says what may be drawn and
    /// delivered, and <see cref="EVSEs"/> and <see cref="RFID"/>, edited, say
    /// what is installed.
    /// </para>
    /// <para>
    /// The configuration file may add roles to these and say differently what
    /// one of them may do - see the node's "roles" section. What is written
    /// here is what a station is when its file says nothing.
    /// </para>
    /// </remarks>
    public static class StationAccess
    {

        #region Resources

        /// <summary>
        /// The EVSEs: read, the page that shows them; edited, what is installed
        /// - other EVSEs, other connectors, other meters or other labels on the
        /// housing.
        /// </summary>
        public const String  EVSEs         = "evses";

        /// <summary>
        /// The card readers: read, which ones there are; edited, which readers
        /// sit where.
        /// </summary>
        public const String  RFID          = "rfid";

        /// <summary>
        /// Whether an EVSE or a card reader is in service: edited, one taken out
        /// of service or put back. Read with the EVSEs and the readers.
        /// </summary>
        /// <remarks>
        /// Its own resource rather than a kind of editing the EVSEs, because it
        /// is day-to-day operation: something is wrong with an outlet, or
        /// somebody is working on it, and the person who finds out is the one
        /// running the station rather than the one who installed it a year ago.
        /// It is also the one statement here that can safely be wrong in the
        /// careful direction.
        /// </remarks>
        public const String  Availability  = "availability";

        /// <summary>
        /// What may be drawn and delivered: the limit of the grid connection
        /// this station hangs on, and the limit of each EVSE and cable.
        /// </summary>
        public const String  Power         = "power";

        /// <summary>
        /// The calibration certificates this station runs under.
        /// </summary>
        public const String  Calibration   = "calibration";

        /// <summary>
        /// The screen on the front: edited, when it is dim; run, a message put
        /// on it or taken off.
        /// </summary>
        public const String  Display       = "display";

        /// <summary>
        /// Who charges where: read, the reservations; run, an outlet held for
        /// somebody or let go, a session paid for at the screen started, and
        /// one stopped.
        /// </summary>
        /// <remarks>
        /// The vehicle's name for what it drives, and the same question from
        /// the other end of the cable.
        /// </remarks>
        public const String  Session       = "session";

        /// <summary>
        /// Where this station dials, what it proves itself with there - the
        /// credentials, and the keys made here - and where each connection
        /// stands: edited, any of that changed; run, a connection tested.
        /// </summary>
        public const String  Connections   = "connections";

        /// <summary>
        /// The wire below the charging cable: which interface, and what is
        /// offered on it. The vehicle's name for the same wire.
        /// </summary>
        public const String  V2G           = "v2g";

        /// <summary>
        /// All of them, in the order a role in the file is checked against.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ EVSEs, RFID, Availability, Power, Calibration,
                                                                    Display, Session, Connections, V2G ];

        #endregion

        #region Roles

        /// <summary>
        /// The operator of this charging station: may point it at other name
        /// and time servers and test them, may say where it dials and with
        /// what, may take an outlet or a reader out of service, and runs the
        /// display and the sessions - but may not redescribe the hardware it is
        /// bolted to, nor change what it may deliver.
        /// </summary>
        public static readonly Role  CPO        = new ("cpo",
                                                       [ Permission.Read(Permission.AnyResource),
                                                         Permission.Edit(NodeResources.DNS),
                                                         Permission.Run (NodeResources.DNS),
                                                         Permission.Edit(NodeResources.NTS),
                                                         Permission.Run (NodeResources.NTS),
                                                         Permission.Edit(Connections),
                                                         Permission.Run (Connections),
                                                         Permission.Edit(V2G),
                                                         Permission.Edit(Availability),
                                                         Permission.Edit(Display),
                                                         Permission.Run (Display),
                                                         Permission.Run (Session) ],
                                                       "runs the station: its network, its connections, what is in service, the display and the sessions");

        /// <summary>
        /// Whoever commissions this charging station: everything the operator
        /// may do, and on top of it the numbers and the papers that belong to
        /// the installation - what the grid connection and each cable may
        /// deliver, and which calibration certificates this station runs
        /// under.
        /// </summary>
        /// <remarks>
        /// A step above the CPO and a step below the system administrator, and
        /// the two steps are different in kind. The installer corrects numbers
        /// about equipment that is already there: the grid operator says the
        /// connection may draw 55 kW rather than the 80 kW on the order, the
        /// cable that went in is a 32 A one. The system administrator says
        /// what the equipment <i>is</i> - how many sockets there are and what
        /// shape they have - and that is a claim nothing further down can
        /// check, because a vehicle is told what plug it is looking at.
        /// </remarks>
        public static readonly Role  Installer  = new ("installer",
                                                       [ .. CPO.Permissions,
                                                         Permission.Edit(Power),
                                                         Permission.Edit(Calibration) ],
                                                       "commissions the station: everything the operator may do, and its power limits and calibration certificates");

        /// <summary>
        /// Both, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ CPO, Installer ];

        #endregion

    }

}
