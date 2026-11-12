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

using System.Globalization;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// evses: how each EVSE of the station stands right now - free, charging,
    /// held or out of service - and, while a car is charging, at what power.
    /// </summary>
    /// <remarks>
    /// What the EVSEs page and the display say, at the permission the EVSEs
    /// page asks for. The power is what the meter of the EVSE measures where
    /// one is read, and the display's ramp otherwise - said to be simulated
    /// every time, because a console is where somebody would take it for a
    /// measurement.
    /// </remarks>
    public class EVSEsCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                 ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "evses".
        /// </summary>
        public static readonly String CommandName = nameof(EVSEsCommand)[..^7].ToLowerInvariant();

        #endregion


        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

            => CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                   ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                   : [];

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override Task<String[]> Execute(String[]           Arguments,
                                               CancellationToken  CancellationToken)
        {

            if (Arguments.Length > 1)
                return Task.FromResult<String[]>([ $"Usage: {CommandName}" ]);

            // What is going on, as GET /api/v1/status/evses says it: the
            // support may ask it, and see nothing of how the station is set up.
            if (!cli.MayDo(Permission.Read(NodeResources.State), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            var evses = cli.Station.EVSEsNow();

            if (evses.Count == 0)
                return Task.FromResult<String[]>([ "This charging station has no EVSEs." ]);

            var width = evses.Max(evse => evse.Label.Length);

            return Task.FromResult<String[]>([
                       .. evses.Select(evse => $"{evse.Label.PadRight(width)}  {Said(evse)}")
                   ]);

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} - how each EVSE stands right now, and at what power a car is charging";

        #endregion


        #region (private) Said(EVSE)

        /// <summary>
        /// One EVSE in words: "charging at 7.4 kW of 22 kW, measured, since
        /// 12:03 (14 min), RFID, Elektro Mobil GmbH".
        /// </summary>
        private String Said(EVSENow EVSE)
        {

            var max = $"{kW(EVSE.EVSE.MaxPower_kW)} kW";

            switch (EVSE.Status)
            {

                case EVSEStatus.Occupied when EVSE.Session is { } session:

                    var running = cli.Station.TimeProvider.GetUtcNow() - session.StartedAt;

                    return $"charging at {kW(EVSE.Power_kW ?? 0)} kW of {max}, " +
                           (EVSE.PowerIsSimulated ? "simulated - no meter is read" : "measured") +
                           $", since {session.StartedAt.ToLocalTime():HH:mm} ({Minutes(running)})" +
                           $", {Method(session.Method)}" +
                           (EVSE.Provider is not null ? $", {EVSE.Provider}" : "") +
                           (EVSE.Closing ? "; out of service once this session ends" : "");

                case EVSEStatus.Reserved:
                    return EVSE.ReservedUntil is { } until
                               ? $"reserved until {until.ToLocalTime():HH:mm}, up to {max}"
                               : $"reserved, up to {max}";

                case EVSEStatus.Inoperative:
                    return "out of service";

                default:
                    return $"available, up to {max}";

            }

        }

        #endregion

        #region (private static) kW(Value)

        /// <summary>
        /// A power figure with at most one decimal and a point, whatever the
        /// culture of the machine: "7.4", "22".
        /// </summary>
        private static String kW(Decimal Value)
            => Math.Round(Value, 1).ToString("0.#", CultureInfo.InvariantCulture);

        #endregion

        #region (private static) Minutes(Running)

        /// <summary>
        /// How long a session has been running: "0 min", "14 min", "2 h 05 min".
        /// </summary>
        private static String Minutes(TimeSpan Running)
        {

            var minutes = Math.Max(0, (Int64) Running.TotalMinutes);

            return minutes < 60
                       ? $"{minutes} min"
                       : $"{minutes / 60} h {minutes % 60:00} min";

        }

        #endregion

        #region (private static) Method(Method)

        /// <summary>
        /// How a session was started, in words.
        /// </summary>
        private static String Method(AuthorizationMethod Method)

            => Method switch {
                   AuthorizationMethod.PnC       => "Plug & Charge",
                   AuthorizationMethod.RFID      => "RFID",
                   AuthorizationMethod.AdHoc     => "ad hoc payment",
                   AuthorizationMethod.Remote    => "started by the CSMS",
                   AuthorizationMethod.LocalApp  => "local app",
                   _                             => Method.ToString()
               };

        #endregion

    }

}
