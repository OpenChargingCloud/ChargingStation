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

namespace cloud.charging.open.ChargingStation.EVSEs
{

    /// <summary>
    /// What kind of change a new list of EVSEs would be to the one a station
    /// is running with.
    /// </summary>
    /// <remarks>
    /// The EVSEs are sent as a whole - they are only valid together - so the
    /// question "may this person do this" cannot be answered from the request
    /// alone. It is answered from the difference between what was sent and what
    /// the station has, and this is that difference, in the only granularity
    /// anybody is asked to reason about: a switch, a number, or a claim about
    /// what is bolted to the wall.
    ///
    /// Flags, because one save can be more than one of them at once - somebody
    /// who takes an EVSE out of service and corrects a cable in the same breath
    /// has done both, and needs to be allowed both rather than whichever one is
    /// looked at first.
    ///
    /// Which permission each of these needs is not decided here. That is the
    /// API's business - see <see cref="Web.Permissions"/>.
    /// </remarks>
    [Flags]
    public enum EVSEChange
    {

        /// <summary>
        /// The two lists describe the same station. Nothing to do and nothing
        /// to ask about.
        /// </summary>
        None         = 0,

        /// <summary>
        /// The same equipment, taken out of service or put back.
        /// </summary>
        Availability = 1,

        /// <summary>
        /// The same equipment, with different numbers on it: what the EVSEs
        /// and their cables may deliver.
        /// </summary>
        PowerLimits  = 2,

        /// <summary>
        /// A different station: other EVSEs, other connectors, other meters or
        /// other labels on the housing.
        /// </summary>
        Hardware     = 4

    }

}
