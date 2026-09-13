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
        /// Change what this station is made of: how many EVSEs it has, what
        /// can be plugged into them, how much they may deliver.
        /// </summary>
        /// <remarks>
        /// This describes hardware somebody installed. Saying there is a CCS
        /// socket where a type 2 socket is bolted to the wall does not change
        /// the wall - it changes what every vehicle and every back end is told
        /// about it, and nothing further down is in a position to notice that
        /// it is wrong.
        /// </remarks>
        ChangeHardware         = 8

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
        /// Whoever bolted this station to the wall: may say what it is made of
        /// and test that it can reach anything, but may not repoint it at other
        /// name or time servers.
        /// </summary>
        /// <remarks>
        /// The complement of the CPO above rather than a step above it. The
        /// installer knows which socket is in the housing because they put it
        /// there, and is gone by the time the network behind the station is
        /// renumbered; the operator knows the network and was not there when
        /// the cable went in. Neither one needs what the other has, and the
        /// role that has both is the one below.
        /// </remarks>
        public static readonly UserRole  Installer    = new ("installer",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.RunDiagnostics        |
                                                             Permissions.ChangeHardware);

        /// <summary>
        /// Everything this station can be told, by whoever is trusted with all
        /// of it at once.
        /// </summary>
        public static readonly UserRole  SystemAdmin  = new ("systemadmin",
                                                             Permissions.ReadConfiguration     |
                                                             Permissions.ChangeNetworkSettings |
                                                             Permissions.RunDiagnostics        |
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
