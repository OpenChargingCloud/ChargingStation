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
        /// The meter of each EVSE: read, how each is read and what it measures,
        /// and a request asked that only reads; edited, where it is read, whom
        /// it is believed to be and whom this station is to it; run, a request
        /// asked that writes to it.
        /// </summary>
        /// <remarks>
        /// Its own resource rather than a part of the EVSEs, because reading a
        /// meter is a matter of the network and the certificates as much as of
        /// what is bolted to the wall - and writing to one is a matter of its
        /// own.
        /// </remarks>
        public const String  Meters        = "meters";

        /// <summary>
        /// The e-mobility providers this station knows: read, which they are
        /// and their logos; edited, a logo put up, replaced or removed - over
        /// SFTP, in /logos.
        /// </summary>
        /// <remarks>
        /// Its own resource rather than a part of the display, because dimming
        /// the screen and showing other companies' logos on it are different
        /// decisions - and it is where a page that edits the providers
        /// themselves belongs, once something other than the file edits them.
        /// </remarks>
        public const String  Providers     = "providers";

        /// <summary>
        /// Its nameplate, what this station tells a CSMS it is: who made it,
        /// which model, its serial number and its firmware. Read, by everybody; edited, by the
        /// system administrator alone.
        /// </summary>
        /// <remarks>
        /// Not the operator's nor the installer's, for the reason the shape of
        /// a socket is not: it says what the equipment is, a claim nothing
        /// further down can check, and a CSMS believes it.
        /// </remarks>
        public const String  Nameplate     = "nameplate";

        /// <summary>
        /// Commissioning over SSH, a tool speaking OCPP to this station on its
        /// own SSH server: read, whether it is served and where; edited,
        /// switched on or off; run, the tool let in - an account that may run
        /// it gets the subsystem "ocpp@open.charging.cloud".
        /// </summary>
        /// <remarks>
        /// Its own resource rather than a part of the connections, because the
        /// connections are where this station dials, and this is who may dial
        /// it: whoever speaks OCPP to a station may tell it nearly anything a
        /// CSMS may, from where it dials to when it restarts.
        /// </remarks>
        public const String  Commissioning = "commissioning";

        /// <summary>
        /// The lock of a connector: run, a cable that is stuck let go of - what
        /// a CSMS asks with UnlockConnector.
        /// </summary>
        /// <remarks>
        /// Its own resource rather than a part of the sessions, because it is
        /// what a caller at the station asks the operator's telephone support
        /// for, and the support may start and stop no session (white paper
        /// "Signed OCPP Messages and Signer Roles", section 5.3).
        /// </remarks>
        public const String  Unlock        = "unlock";

        /// <summary>
        /// The monitoring of the station as a whole: read, the report of its
        /// monitors - GetMonitoringReport; edited, its base and its level set
        /// and monitors cleared - SetMonitoringBase, SetMonitoringLevel and
        /// ClearVariableMonitoring.
        /// </summary>
        /// <remarks>
        /// Its own resource, because these four are about no single component:
        /// a monitor on a variable is set with the right to edit that
        /// variable's component, and what is reported of all of them, and how
        /// much, is the operator's to say (white paper "Signed OCPP Messages
        /// and Signer Roles", section 5.3).
        /// </remarks>
        public const String  Monitoring    = "monitoring";

        /// <summary>
        /// Whom the station lets charge without asking: read, the version of
        /// its local authorization list - GetLocalListVersion; edited, the
        /// list sent and the authorization cache cleared - SendLocalList and
        /// ClearCache.
        /// </summary>
        /// <remarks>
        /// Not "rfid", which is the card readers as equipment: this is the
        /// list of cards and what the station remembers of the answers of its
        /// back end (white paper "Signed OCPP Messages and Signer Roles",
        /// section 5.3).
        /// </remarks>
        public const String  Authorization = "authorization";

        /// <summary>
        /// All of them, in the order a role in the file is checked against.
        /// </summary>
        public static readonly IReadOnlyList<String>  Resources = [ EVSEs, RFID, Availability, Power, Calibration,
                                                                    Display, Session, Connections, V2G, Meters, Providers,
                                                                    Nameplate, Commissioning, Unlock, Monitoring, Authorization ];

        #endregion

        #region Roles

        /// <summary>
        /// The operator of this charging station: may point it at other name
        /// and time servers and test them, may say where it dials and with
        /// what, may take an outlet or a reader out of service, runs the
        /// display and the sessions, and puts up the providers' logos - but may
        /// not redescribe the hardware it is bolted to, nor change what it may
        /// deliver. It restarts the station, once idle and at once, has what is
        /// going on reported again, asks for any other message, lets go of a
        /// stuck cable and updates the firmware - where the station is
        /// metrologically relevant, a firmware update needs the installer's
        /// "calibration:edit" too. It says how much of the station is
        /// monitored, and reads what is; and it sends the local authorization
        /// list and clears the authorization cache.
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
                                                         Permission.Run (Session),
                                                         Permission.Edit(Providers),
                                                         Permission.Run (NodeResources.Node),
                                                         Permission.Run (NodeResources.Reset),
                                                         Permission.Run (NodeResources.ResetImmediate),
                                                         Permission.Run (NodeResources.State),
                                                         Permission.Edit(NodeResources.Firmware),
                                                         Permission.Run (Unlock),
                                                         Permission.Edit(Monitoring),
                                                         Permission.Edit(Authorization) ],
                                                       "runs the station: its network, its connections, what is in service, the display, the providers' logos, the sessions, restarts, the firmware, the monitoring and the local authorization list");

        /// <summary>
        /// Whoever commissions this charging station: everything the operator
        /// may do, and on top of it the numbers and the papers that belong to
        /// the installation - what the grid connection and each cable may
        /// deliver, how each meter is read and what is written to it, and
        /// which calibration certificates this station runs
        /// under - and a commissioning tool speaking OCPP to it over SSH.
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
                                                         Permission.Edit(Calibration),
                                                         Permission.Edit(Meters),
                                                         Permission.Run (Meters),
                                                         Permission.Edit(Commissioning),
                                                         Permission.Run (Commissioning) ],
                                                       "commissions the station: everything the operator may do, its power limits and calibration certificates, its meters - how each is read, and what is written to it - and OCPP over SSH from a commissioning tool");

        /// <summary>
        /// The operator's telephone support: sees what is going on, has it
        /// reported again, restarts the station once nothing is charging, and
        /// lets go of a cable that is stuck - and nothing else: no
        /// configuration, no log, no restart at once, no session started or
        /// stopped.
        /// </summary>
        /// <remarks>
        /// The node's support and a station's one thing more. Many people hold
        /// it, because a restart is what a caller at a station most often needs,
        /// and that is why it may do so little (white paper "Signed OCPP
        /// Messages and Signer Roles", section 5.3).
        /// </remarks>
        public static readonly Role  Support    = new ("support",
                                                       [ .. Role.Support.Permissions,
                                                         Permission.Run (Unlock) ],
                                                       "the operator's telephone support: sees what is going on, has it reported again, restarts the station once it is idle and lets go of a stuck cable");

        /// <summary>
        /// All three, in the order a sentence naming them reads best.
        /// </summary>
        public static readonly IReadOnlyList<Role>  Roles = [ CPO, Installer, Support ];

        #endregion

    }

}
