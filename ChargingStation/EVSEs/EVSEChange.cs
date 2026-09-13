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
    /// anybody is asked to reason about: did somebody correct a number, or did
    /// somebody redescribe the hardware.
    ///
    /// Which permission each of these needs is not decided here. That is the
    /// API's business - see <see cref="Web.Permissions"/>.
    /// </remarks>
    public enum EVSEChange
    {

        /// <summary>
        /// The two lists describe the same station. Nothing to do and nothing
        /// to ask about.
        /// </summary>
        None,

        /// <summary>
        /// The same equipment, with different numbers on it: what the EVSEs
        /// and their cables may deliver.
        /// </summary>
        PowerLimits,

        /// <summary>
        /// A different station: other EVSEs, other connectors, other meters or
        /// other labels on the housing.
        /// </summary>
        Hardware

    }

}
