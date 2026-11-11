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

using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation.EVSEs
{

    /// <summary>
    /// How one EVSE stands right now: what the EVSEs page and the display say
    /// of it, for whatever reads it without JSON - the command line.
    /// </summary>
    /// <param name="EVSE">The EVSE, as configured.</param>
    /// <param name="Status">Whether it is free, charging, held or out of service.</param>
    /// <param name="Closing">Whether it goes out of service when the session on it ends.</param>
    /// <param name="Session">The session on it, if a car is charging.</param>
    /// <param name="Provider">Whose customer is charging: the provider of the card, the operator for an ad hoc payment, or null where nobody knows.</param>
    /// <param name="Power_kW">What it delivers right now while a car is charging, in kW; null while none is.</param>
    /// <param name="PowerIsSimulated">Whether that figure is the display's ramp rather than what a meter measures.</param>
    /// <param name="ReservedUntil">Until when it is held, where it is.</param>
    public sealed record EVSENow(EVSEConfig        EVSE,
                                 EVSEStatus        Status,
                                 Boolean           Closing,
                                 ChargingSession?  Session,
                                 String?           Provider,
                                 Decimal?          Power_kW,
                                 Boolean           PowerIsSimulated,
                                 DateTimeOffset?   ReservedUntil)
    {

        /// <summary>
        /// What is written on the EVSE, or its id where nothing is.
        /// </summary>
        public String Label
            => EVSE.PhysicalReference ?? EVSE.Id.ToString();

    }

}
