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

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// send: have this station send again what it sends by itself - how its
    /// EVSEs stand, a BootNotification, a Heartbeat.
    /// </summary>
    /// <remarks>
    /// What a TriggerMessage of OCPP asks for, and the buttons of the EVSEs
    /// page, at the permissions those ask for: how the EVSEs stand is
    /// "state:run", which the operator's telephone support holds; the other
    /// two are "node:run". A message is named however it is typed, and by its
    /// beginning: "send status".
    /// </remarks>
    public class SendCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "send".
        /// </summary>
        public static readonly String CommandName = nameof(SendCommand)[..^7].ToLowerInvariant();

        /// <summary>
        /// How it is used.
        /// </summary>
        public static readonly String Usage = $"Usage: {CommandName} {String.Join("|", ChargingStation.SentAgain)}";

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

            // By its beginning, where that names one of them alone.
            var named    = Arguments.Length == 2
                               ? ChargingStation.SentAgain.Where(sent => sent.StartsWith(Arguments[1], StringComparison.OrdinalIgnoreCase)).ToArray()
                               : [];

            if (named.Length != 1 ||
                ChargingStation.PermissionToSend(named[0]) is not { } permission)
            {
                return Task.FromResult<String[]>([
                           Usage,
                           $"  {ChargingStation.SendsStatusNotification}  how each EVSE stands, to every back end this station is connected to",
                           $"  {ChargingStation.SendsBootNotification}    this station registers again with every back end it is connected to",
                           $"  {ChargingStation.SendsHeartbeat}           a heartbeat to every back end this station is connected to"
                       ]);
            }

            if (!cli.MayDo(permission, $"{CommandName} {named[0]}", out var refused))
                return Task.FromResult<String[]>([ refused ]);

            return Task.FromResult<String[]>([
                       cli.Station.TrySendAgain(named[0], StationCLI.Asked(cli.Caller), out var result, out var error)
                           ? $"A {named[0]} goes to {result.Value<Int32>("backEnds")} back end(s)."
                           : error
                   ]);

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} <message> - have this station send again how its EVSEs stand, a BootNotification or a Heartbeat";

        #endregion

    }

}
