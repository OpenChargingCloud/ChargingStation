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

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Kiosk
{

    /// <summary>
    /// How somebody said who they are.
    /// </summary>
    public enum AuthorizationMethod
    {

        /// <summary>
        /// Plug and Charge: the vehicle itself, over the charging cable.
        /// </summary>
        PnC,

        /// <summary>
        /// A card held against a reader.
        /// </summary>
        RFID,

        /// <summary>
        /// Nobody: paid for on the spot, through the QR code on the display.
        /// </summary>
        AdHoc,

        /// <summary>
        /// A back end, over OCPP - an app, a call to a hotline, a roaming
        /// platform.
        /// </summary>
        Remote

    }


    /// <summary>
    /// What an EVSE is doing, as far as a display has to care.
    /// </summary>
    public enum EVSEStatus
    {

        /// <summary>
        /// Free: anybody may plug in.
        /// </summary>
        Available,

        /// <summary>
        /// Held for somebody who is on their way.
        /// </summary>
        Reserved,

        /// <summary>
        /// Somebody is charging.
        /// </summary>
        Occupied,

        /// <summary>
        /// Out of service; nobody is served by it.
        /// </summary>
        Inoperative

    }


    /// <summary>
    /// Somebody charging at one EVSE of this station.
    /// </summary>
    /// <remarks>
    /// Everything a display needs and nothing else. There is deliberately no
    /// meter reading, no tariff and no identifier of a transaction in here:
    /// this station has no metering and no billing, and a record that carried
    /// fields for them would invite somebody to believe it did.
    /// </remarks>
    /// <param name="EVSEId">Which EVSE.</param>
    /// <param name="Method">How they said who they are.</param>
    /// <param name="TokenUID">The card, when it was a card.</param>
    /// <param name="Provider">Whose customer they are, when this station can tell.</param>
    /// <param name="StartedAt">When it began.</param>
    public sealed record ChargingSession(Byte                 EVSEId,
                                         AuthorizationMethod  Method,
                                         String?              TokenUID,
                                         EMobilityProvider?   Provider,
                                         DateTimeOffset       StartedAt)
    {

        #region Data

        /// <summary>
        /// How long the simulated charge takes to reach the limit of its cable.
        /// </summary>
        /// <remarks>
        /// See <see cref="SimulatedPower_kW"/>. A real charge ramps because the
        /// vehicle and the station negotiate their way up; this one ramps
        /// because a display that jumps straight to its maximum and sits there
        /// tells you nothing about whether anything is still running.
        /// </remarks>
        public static readonly TimeSpan  RampUpTime = TimeSpan.FromSeconds(30);

        #endregion

        #region SimulatedPower_kW(Now, Limit_kW)

        /// <summary>
        /// What this session would be drawing, if anything were measuring.
        /// </summary>
        /// <remarks>
        /// **Simulated, and said to be simulated wherever it is handed out.**
        /// This station has no energy meter and is not connected to anything
        /// that does - it is a test station. A display with a dash where the
        /// power should be would be honest and useless for looking at; a number
        /// that pretends to be a meter reading would be useful and a lie. So it
        /// is a number, and everything that carries it says where it came from.
        ///
        /// Worked out from the clock rather than counted up by a timer, so that
        /// two browsers looking at the same station see the same figure and a
        /// station that was asleep does not come back with a stale one.
        /// </remarks>
        public Decimal SimulatedPower_kW(DateTimeOffset  Now,
                                         Decimal         Limit_kW)
        {

            var running = Now - StartedAt;

            if (running <= TimeSpan.Zero)
                return 0;

            var share = running >= RampUpTime
                            ? 1.0
                            : running.TotalSeconds / RampUpTime.TotalSeconds;

            return Math.Round(Limit_kW * (Decimal) share, 1);

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"EVSE {EVSEId}: {Method}" +
               (TokenUID is null ? "" : $" {TokenUID}") +
               (Provider is null ? "" : $" of {Provider.Name}") +
               $" since {StartedAt:HH:mm:ss}";

        #endregion

    }

}
