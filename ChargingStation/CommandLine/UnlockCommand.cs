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

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// unlock: let go of the cable at an EVSE, where nobody is charging.
    /// </summary>
    /// <remarks>
    /// What an UnlockConnector of OCPP asks for, and the "Unlock" button
    /// beside an EVSE on the EVSEs page, at the permission those ask for:
    /// "unlock:run", which the operator's telephone support holds. Somebody
    /// charging there is not unplugged by it: the session is stopped first,
    /// by whoever may.
    /// </remarks>
    public class UnlockCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                  ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "unlock".
        /// </summary>
        public static readonly String CommandName = nameof(UnlockCommand)[..^7].ToLowerInvariant();

        /// <summary>
        /// How it is used.
        /// </summary>
        public static readonly String Usage = $"Usage: {CommandName} <EVSE> [<connector>]";

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

            if (!cli.MayDo(Permission.Run(StationAccess.Unlock), CommandName, out var refused))
                return Task.FromResult<String[]>([ refused ]);

            Byte connector = 0;

            if (Arguments.Length is < 2 or > 3                ||
                !Byte.TryParse(Arguments[1], out var evse)    ||
                (Arguments.Length == 3 && !Byte.TryParse(Arguments[2], out connector)))
            {
                return Task.FromResult<String[]>([
                           Usage,
                           .. cli.Station.EVSEs.Select(known => $"  {known.Id}  {known.Connectors.Count} connector(s)")
                       ]);
            }

            return Task.FromResult<String[]>([
                       ChargingStation.Said(
                           cli.Station.Unlock(evse,
                                              Arguments.Length == 3 ? connector : null,
                                              StationCLI.Asked(cli.Caller)),
                           evse
                       )
                   ]);

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} <EVSE> [<connector>] - let go of the cable at an EVSE, where nobody is charging";

        #endregion

    }

}
