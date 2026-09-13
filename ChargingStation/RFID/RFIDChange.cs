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

namespace cloud.charging.open.ChargingStation.RFID
{

    /// <summary>
    /// What kind of change a new list of RFID readers would be to the one a
    /// station is running with.
    /// </summary>
    /// <remarks>
    /// The same shape and the same reason as
    /// <see cref="EVSEs.EVSEChange"/>: the whole list is sent, so what is being
    /// asked for can only be seen by comparing it with what the station has.
    /// Switching a reader off is not the same statement as saying a reader is
    /// bolted to the housing, and they are not for the same people.
    /// </remarks>
    [Flags]
    public enum RFIDChange
    {

        /// <summary>
        /// The two lists describe the same readers in the same places, switched
        /// the same way.
        /// </summary>
        None          = 0,

        /// <summary>
        /// The same readers, switched on or off.
        /// </summary>
        Availability  = 1,

        /// <summary>
        /// Other readers, or the same ones somewhere else.
        /// </summary>
        Placement     = 2

    }

}
