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

using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso2;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.T1S.Monitoring;

#endregion

namespace cloud.charging.open.ChargingStation.ISO15118
{

    /// <summary>
    /// The charge loop of a DC session, as what the coupler's pins say reaches
    /// it: a current limit below the most the station can deliver, and the end
    /// of the charging - Terminate over ISO 15118-20, StopCharging over -2.
    /// </summary>
    public interface IThermalChargeLoop
    {

        /// <summary>
        /// The most current the station can deliver at all, in amperes - what a
        /// limit is below.
        /// </summary>
        Double   MaximumCurrent_A    { get; }

        /// <summary>
        /// The current the station may deliver now, in amperes, or null for its
        /// maximum.
        /// </summary>
        Double?  CurrentLimit_A      { get; set; }

        /// <summary>
        /// Whether the vehicle is being told to end the charging.
        /// </summary>
        Boolean  EndRequested        { get; }

        /// <summary>
        /// Tell the vehicle, in the next charge-loop response, to end the
        /// charging.
        /// </summary>
        void     EndCharging();

    }


    /// <summary>
    /// What applying the state of the coupler's pins changed in a charge loop.
    /// </summary>
    public enum ThermalChange
    {

        /// <summary>
        /// Nothing: the charge loop already was as the pins say.
        /// </summary>
        None,

        /// <summary>
        /// The current is limited now, or limited otherwise than before.
        /// </summary>
        Limited,

        /// <summary>
        /// The full current again.
        /// </summary>
        Restored,

        /// <summary>
        /// The vehicle is told to end the charging.
        /// </summary>
        Ended

    }


    /// <summary>
    /// The charge loops what the coupler's pins say reaches, and what it does
    /// to them.
    /// </summary>
    public static class ThermalChargeLoop
    {

        #region Of(Session)

        /// <summary>
        /// The charge loop of an ISO 15118-20 session, or null for AC, which no
        /// coupler with pin sensors carries.
        /// </summary>
        /// <param name="Session">The state machine of the session.</param>
        public static IThermalChargeLoop? Of(Secc20Base Session)

            => Session is Secc20Dc dc
                   ? new Iso20Loop(dc)
                   : null;


        /// <summary>
        /// The charge loop of an ISO 15118-2 session in the given power mode,
        /// or null for AC, which no coupler with pin sensors carries.
        /// </summary>
        /// <param name="Session">The state machine of the session.</param>
        /// <param name="Mode">The power mode the vehicle and the station agreed on.</param>
        public static IThermalChargeLoop? Of(Secc2      Session,
                                             PowerMode  Mode)

            => Mode == PowerMode.Dc
                   ? new Iso2Loop(Session)
                   : null;

        #endregion

        #region Apply(Loop, Overall)

        /// <summary>
        /// Give the charge loop what the state of the coupler's pins means -
        /// see <see cref="V2GLink.ThermalAction"/> - and say what that changed.
        /// </summary>
        /// <param name="Loop">The charge loop of the session that runs.</param>
        /// <param name="Overall">The worst state of any pin.</param>
        public static ThermalChange Apply(IThermalChargeLoop  Loop,
                                          ThermalState        Overall)
        {

            var (limit, end) = V2GLink.ThermalAction(Overall, Loop.MaximumCurrent_A);

            if (end)
            {

                var already = Loop.EndRequested;

                Loop.CurrentLimit_A = 0;
                Loop.EndCharging();

                return already
                           ? ThermalChange.None
                           : ThermalChange.Ended;

            }

            if (Loop.CurrentLimit_A == limit)
                return ThermalChange.None;

            Loop.CurrentLimit_A = limit;

            return limit is null
                       ? ThermalChange.Restored
                       : ThermalChange.Limited;

        }

        #endregion


        #region (private) Iso20Loop / Iso2Loop

        /// <summary>
        /// ISO 15118-20: the limit and Terminate of a DC session.
        /// </summary>
        private sealed class Iso20Loop(Secc20Dc Session) : IThermalChargeLoop
        {

            public Double   MaximumCurrent_A
                => Session.MaximumCurrent_A;

            public Double?  CurrentLimit_A
            {
                get => Session.CurrentLimit_A;
                set => Session.CurrentLimit_A = value;
            }

            public Boolean  EndRequested
                => Session.TerminateRequested;

            public void     EndCharging()
                => Session.Terminate();

        }

        /// <summary>
        /// ISO 15118-2: the running limit and StopCharging of a DC session.
        /// </summary>
        private sealed class Iso2Loop(Secc2 Session) : IThermalChargeLoop
        {

            public Double   MaximumCurrent_A
                => Session.MaximumCurrent_A;

            public Double?  CurrentLimit_A
            {
                get => Session.DcRunningMaxAmps;
                set => Session.DcRunningMaxAmps = value;
            }

            public Boolean  EndRequested
                => Session.StopChargingRequested;

            public void     EndCharging()
                => Session.StopCharging();

        }

        #endregion

    }

}
