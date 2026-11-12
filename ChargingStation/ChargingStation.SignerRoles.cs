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

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What a signer of a request a CSMS sends may have done to this station:
    /// what the role of the key that signed it may do on the page and over
    /// SSH (white paper "Signed OCPP Messages and Signer Roles", section 5.3,
    /// phase 3 of chapter 7).
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key belongs to a role of the OCPP node - who it is said to be is the
    /// node's to know, and AddUserRole's to change. What that role may do is
    /// this station's role model, the one its accounts are held to: a support
    /// key restarts an idle station, as a support account does, and nothing
    /// more. The table of what each action needs is the one WWCP_OCPP ships
    /// for every charging station; this station only says whether it is
    /// metrologically relevant.
    /// </para>
    /// <para>
    /// Nothing of it is looked at while the mode of the policy is "Off", which
    /// it is unless somebody sets another.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// The device model variables whose writing is a pin set, and which
        /// this station holds to the pin set signers of their connection
        /// itself: a connection over SSH, and one over wss://.
        /// </summary>
        private static readonly (String Component, String Variable)[]  pinSets = [
            ("SSHClientController", "HostKeyPins"),
            ("SSHClientController", "HostKeyPinsVersion"),
            ("TLSClientController", "ServerKeyPins"),
            ("TLSClientController", "ServerKeyPinsVersion")
        ];

        #endregion

        #region IsMetrologicallyRelevant

        /// <summary>
        /// Whether this station is metrologically relevant: it runs under a
        /// calibration certificate, or a meter it reads signs its sessions.
        /// </summary>
        /// <remarks>
        /// The station's to say and not a request's, or a signer would leave it
        /// out. Both are the installer's to change - and the installer is who
        /// may update the firmware of such a station - so an operator cannot
        /// make it something it is not to update it alone.
        /// </remarks>
        public Boolean IsMetrologicallyRelevant

            => CalibrationCertificates.Count > 0 ||
               EVSEs.Any(evse => metersFile.MeterOf(evse.Id) is not null);

        #endregion

        #region (private static) ResourceOfComponent(Component)

        /// <summary>
        /// The resource of this station's role model a device model component
        /// belongs to: where this station dials is its connections.
        /// </summary>
        private static String ResourceOfComponent(String Component)

            => Component switch {
                   "SSHClientController"  => StationAccess.Connections,
                   "TLSClientController"  => StationAccess.Connections,
                   _                      => protocols.WWCP.SignaturePolicy.DefaultResourceOfComponent(Component)
               };

        #endregion

        #region (private) PermissionsOfRole(Name)

        /// <summary>
        /// What a role of this station's role model may do, as the policy
        /// asks it: "resource:operation". Nothing for a role it does not know.
        /// </summary>
        private IEnumerable<String> PermissionsOfRole(String Name)

            => Access.RoleNamed(Name) is Role known
                   ? known.Permissions.Select(permission => permission.ToString())
                   : [];

        #endregion

        #region (private) HoldSignersToTheRoles(Node)

        /// <summary>
        /// Hold the signers of what a CSMS sends to the OCPP 2.1 node to this
        /// station's roles.
        /// </summary>
        /// <remarks>
        /// Instead of what the node makes of it by itself, where only its
        /// administrators may do anything. A pin set is signed by a key of the
        /// CSMS that holds no role, and is held to the pin set signers of its
        /// connection by this station whatever the mode: the policy leaves it
        /// to that, rather than refusing what no role signed.
        /// </remarks>
        private void HoldSignersToTheRoles(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            var policy = Node.OCPP.SignaturePolicy;

            policy.IncomingDirection    = "CSMS->CS";
            policy.PermissionsOfRole    = PermissionsOfRole;
            policy.ResourceOfComponent  = ResourceOfComponent;

            policy.ClearActionPermissions();
            policy.AddActionPermissions(OCPPv2_1.CS.ChargingStationActionPermissions.Default(() => IsMetrologicallyRelevant));

            policy.ActionsDecidedPerEntry.Add("SetVariables");
            policy.ActionsDecidedPerEntry.Add("GetVariables");

            policy.AddPolicyEntries(pinSets.Select(pinSet => new MessagePolicyEntry(
                                                                 Id:           $"pin set {pinSet.Component}.{pinSet.Variable}",
                                                                 Action:       "SetVariables",
                                                                 Require:      SignerRequirement.Unsigned,
                                                                 Direction:    "CSMS->CS",
                                                                 Component:    pinSet.Component,
                                                                 Variable:     pinSet.Variable,
                                                                 Description:  "A pin set: held to the pin set signers of its connection by the station itself"
                                                             )));

        }

        #endregion

    }

}
