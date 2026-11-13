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

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
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
    /// A key belongs to a role of the station - who it is said to be is the
    /// station's table of signer roles to know, and AddSignerRole's to change
    /// (ChargingStation.Signatures.cs). What that role may do is
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
        /// <remarks>
        /// Its SSH peers among them: who a host is believed by, what goes over
        /// the connection to it, and where the station's files may go. They are
        /// no pin set left to the signers of a connection, though: a peer is
        /// written as every other variable is - by anybody while the policy is
        /// off, as a CSMS that signs nothing has to, and by a signer whose role
        /// may edit the connections once it is enforced.
        /// </remarks>
        private static String ResourceOfComponent(String Component)

            => Component switch {
                   "SSHClientController"  => StationAccess.Connections,
                   "TLSClientController"  => StationAccess.Connections,
                   "SSHPeerController"    => StationAccess.Connections,
                   _                      => protocols.WWCP.SignaturePolicy.DefaultResourceOfComponent(Component)
               };

        #endregion

        #region (private) KeepWhoMayDoWhat(Old, New, OldRoles, NewRoles)

        /// <summary>
        /// Hold a new OCPP node to what the node it replaces was held to: the
        /// mode of its policy, the roles its keys have, and the entries of its
        /// policy table that the station does not add by itself.
        /// </summary>
        /// <remarks>
        /// A new node starts with the policy off and knows no key. Left at
        /// that, a change of the EVSEs or a restart - which the support may ask
        /// for - turned a station that asked who signed into one that takes
        /// everything from everybody. What a role assignment or the mode of
        /// the policy said is written down, and is the new node's anyway
        /// (ChargingStation.Signatures.cs); this keeps what a program set at
        /// the node itself, which lasts as long as the process does.
        /// </remarks>
        private void KeepWhoMayDoWhat(SignaturePolicy  Old,
                                      SignaturePolicy  New,
                                      List<protocols.WWCP.UserRole>   OldRoles,
                                      List<protocols.WWCP.UserRole>   NewRoles)
        {

            New.Mode = Old.Mode;

            foreach (var role in OldRoles)
                if (!NewRoles.Any(known => known.Id == role.Id))
                    NewRoles.Add(role);

            var held = New.PolicyEntries.Select(entry => entry.Id).ToHashSet();

            foreach (var entry in Old.PolicyEntries.Where(entry => !held.Contains(entry.Id)))
            {
                try
                {
                    New.AddPolicyEntry(entry);
                }
                catch (ArgumentException e)
                {
                    Log.Warning($"The policy entry '{entry.Id}' was not kept across the rebuild of the OCPP nodes: {e.Message}", "ocpp", "signatures");
                }
            }

        }

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
            policy.AddActionPermissions(OCPPv2_1.CS.ChargingStationActionPermissions.Default(() => IsMetrologicallyRelevant,
                                                                                             () => TakesItsFirstOwner));

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

        /// <summary>
        /// Hold the signers of what a Central System sends to the OCPP 1.6 node
        /// to this station's roles, as those of the OCPP 2.1 node are.
        /// </summary>
        /// <remarks>
        /// Instead of what the node makes of it by itself, where only an "admin"
        /// may do anything. The table is the one of OCPP 1.6: a soft reset is
        /// the restart once idle, a hard one the restart at once, and
        /// ChangeConfiguration and GetConfiguration are decided key by key - a
        /// key of the DNS client or of the network time by "dns" or "nts", any
        /// other by "configuration". No key of OCPP 1.6 is a pin set of a
        /// connection, so there is nothing to leave to pin set signers.
        /// </remarks>
        private void HoldSignersToTheRoles(OCPPv1_6.TestChargePointNode Node)
        {

            var policy = Node.OCPP.SignaturePolicy;

            policy.IncomingDirection    = "CSMS->CS";
            policy.PermissionsOfRole    = PermissionsOfRole;
            policy.ResourceOfComponent  = ResourceOfComponent;

            policy.ClearActionPermissions();
            policy.AddActionPermissions(OCPPv1_6.CP.ChargePointActionPermissions.Default(() => IsMetrologicallyRelevant,
                                                                                         () => TakesItsFirstOwner));

            foreach (var action in OCPPv1_6.CP.ChargePointActionPermissions.ActionsDecidedPerKey)
                policy.ActionsDecidedPerEntry.Add(action);

        }

        #endregion

    }

}
