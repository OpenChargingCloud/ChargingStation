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

using System.Diagnostics.CodeAnalysis;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    /// <summary>
    /// What somebody signed in to this charging station is allowed to do.
    /// </summary>
    /// <remarks>
    /// Flags rather than a list, because a permission is asked about one at a
    /// time and answered by a single test - and because the set a role grants
    /// is then a constant instead of a collection to be built and searched.
    /// </remarks>
    [Flags]
    public enum Permissions : UInt32
    {

        /// <summary>
        /// Nothing at all. What an unknown role would grant.
        /// </summary>
        None                   = 0,

        /// <summary>
        /// See how this station is configured.
        /// </summary>
        ReadConfiguration      = 1,

        /// <summary>
        /// Change how this station reaches the network: its name resolution
        /// and where it reads the time.
        /// </summary>
        /// <remarks>
        /// Separate from the hardware below because it is reversible and it
        /// complains: a wrong name server makes the station say so, and the
        /// next change puts it right. A wrong connector type does not complain,
        /// and the cable on the wall is still the other one.
        /// </remarks>
        ChangeNetworkSettings  = 2,

        /// <summary>
        /// Make this station ask a name server or a time server something, to
        /// find out whether it can.
        /// </summary>
        /// <remarks>
        /// Its own permission and not part of reading: a diagnostic sends
        /// traffic from this station to a host somebody names, which is more
        /// than it sounds like to hand to everybody who may look at a page.
        /// </remarks>
        RunDiagnostics         = 4,

        /// <summary>
        /// Change how much power may be drawn and delivered: the limit of the
        /// grid connection this station hangs on, and the limit of each cable.
        /// </summary>
        /// <remarks>
        /// Separate from the hardware below because it is a different kind of
        /// statement about the same equipment. What cable is fitted is a fact
        /// of the installation; what it may deliver is a setting, arrived at
        /// from the fuse it is behind and the cross-section that was pulled -
        /// and it is the one number that is routinely wrong on the day of
        /// commissioning and right a week later, when the grid operator has
        /// said what the connection may actually draw.
        ///
        /// Lowering a limit is always safe. Raising one is a statement that
        /// the hardware behind it can take it, which is why this is not
        /// something the operator of the station gets by default.
        /// </remarks>
        ChangePowerLimits      = 8,

        /// <summary>
        /// Put calibration certificates on this station, and take them off.
        /// </summary>
        /// <remarks>
        /// Whoever commissions a station under a calibration law regime is the
        /// one holding the certificates, and they arrive with the meters
        /// rather than with the network. They are public documents - a
        /// certificate is a signature over a public key, and there is nothing
        /// secret in one - so this permission is not about keeping them from
        /// being read. It is about who may say which ones this station is
        /// running under.
        /// </remarks>
        ManageCalibration      = 16,

        /// <summary>
        /// Change what this station is made of: how many EVSEs it has and what
        /// can be plugged into them.
        /// </summary>
        /// <remarks>
        /// This describes hardware somebody installed. Saying there is a CCS
        /// socket where a type 2 socket is bolted to the wall does not change
        /// the wall - it changes what every vehicle and every back end is told
        /// about it, and nothing further down is in a position to notice that
        /// it is wrong.
        ///
        /// The most a socket may deliver is deliberately not here but above:
        /// a number that gets corrected is a different thing from a socket
        /// that gets invented.
        /// </remarks>
        ChangeHardware         = 32

    }


    /// <summary>
    /// A role somebody signs in as: a name, and the permissions it carries.
    /// </summary>
    /// <remarks>
    /// A closed set, unlike the connector types elsewhere in this project, and
    /// deliberately so: a connector type this station has never heard of is
    /// still a socket somebody can plug a car into, but a role it has never
    /// heard of is a role it cannot enforce. So an unrecognised name is refused
    /// when the login file is read, rather than quietly granting nothing - or,
    /// far worse, being taken for a known one because it looks similar.
    /// </remarks>
    /// <param name="Name">How the role is written in the login file.</param>
    /// <param name="Permissions">What it grants.</param>
    public sealed record UserRole(String       Name,
                                  Permissions  Permissions)
    {

        #region Data

        /// <summary>
        /// May look at this station, and do nothing to it.
        /// </summary>
        public static readonly UserRole  Viewer       = new ("viewer",
                                                             Permissions.ReadConfiguration);

        /// <summary>
        /// The operator of this charging station: may point it at other name
        /// and time servers and may test them, but may not redescribe the
        /// hardware it is bolted to.
        /// </summary>
        public static readonly UserRole  CPO          = new ("cpo",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics);

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
        /// what the equipment *is* - how many sockets there are and what
        /// shape they have - and that is a claim nothing further down can
        /// check, because a vehicle is told what plug it is looking at.
        /// </remarks>
        public static readonly UserRole  Installer    = new ("installer",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics        |
                                                             Permissions.ChangePowerLimits     |
                                                             Permissions.ManageCalibration);

        /// <summary>
        /// Everything this station can be told, by whoever is trusted with all
        /// of it at once.
        /// </summary>
        public static readonly UserRole  SystemAdmin  = new ("systemadmin",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics        |
                                                             Permissions.ChangePowerLimits     |
                                                             Permissions.ManageCalibration     |
                                                             Permissions.ChangeHardware);

        /// <summary>
        /// Every role this station knows.
        /// </summary>
        public static readonly IReadOnlyList<UserRole>  All = [ Viewer, CPO, Installer, SystemAdmin ];

        #endregion


        #region (static) TryParse(Text, out Role, out Error)

        /// <summary>
        /// A role by the name the login file writes it under, in any case.
        /// </summary>
        public static Boolean TryParse(String?                            Text,
                                       [NotNullWhen(true)]  out UserRole?  Role,
                                       [NotNullWhen(false)] out String?    Error)
        {

            Role   = All.FirstOrDefault(role => String.Equals(role.Name, Text?.Trim(), StringComparison.OrdinalIgnoreCase));

            Error  = Role is null
                         ? $"\"{Text}\" is not a role this charging station knows. Known roles: {String.Join(", ", All.Select(role => role.Name))}."
                         : null;

            return Role is not null;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => Name;

        #endregion

    }


    /// <summary>
    /// What a set of roles adds up to.
    /// </summary>
    public static class UserRoleExtensions
    {

        #region PermissionsOf(this Roles)

        /// <summary>
        /// Everything the given roles grant together.
        /// </summary>
        public static Permissions PermissionsOf(this IEnumerable<UserRole> Roles)
        {

            var permissions = Permissions.None;

            foreach (var role in Roles)
                permissions |= role.Permissions;

            return permissions;

        }

        #endregion

        #region Names(this Permissions)

        /// <summary>
        /// The permissions as the web interface reads them, so that a page can
        /// grey out what this browser may not do instead of finding out by
        /// being refused.
        /// </summary>
        /// <remarks>
        /// What the browser is told is a copy of what the station enforces, and
        /// not the enforcement: every request is checked again on arrival. A
        /// greyed-out button is a courtesy, not a lock.
        /// </remarks>
        public static IEnumerable<String> Names(this Permissions Permissions)

            => Enum.GetValues<Permissions>().
                    Where (permission => permission != Web.Permissions.None && Permissions.HasFlag(permission)).
                    Select(permission => Char.ToLowerInvariant(permission.ToString()[0]) + permission.ToString()[1..]);

        #endregion

    }

}
