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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.Web;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// restart: restart this station once nothing is charging any more, or
    /// at once, which ends what is charging.
    /// </summary>
    /// <remarks>
    /// What a Reset of OCPP asks for - OnIdle and Soft, Immediate and Hard -
    /// and the "Restart" buttons of the EVSEs page, at the permissions those
    /// ask for: "reset:run" for the one that waits, which the operator's
    /// telephone support holds, and "reset-immediate:run" for the one that
    /// can do damage. Without a word it says how it is used and restarts
    /// nothing.
    /// </remarks>
    public class RestartCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                   ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "restart".
        /// </summary>
        public static readonly String CommandName = nameof(RestartCommand)[..^7].ToLowerInvariant();

        /// <summary>
        /// Once nothing is charging any more.
        /// </summary>
        public const String Idle = "idle";

        /// <summary>
        /// At once.
        /// </summary>
        public const String Now  = "now";

        /// <summary>
        /// How it is used.
        /// </summary>
        public static readonly String Usage = $"Usage: {CommandName} {Idle}|{Now}";

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

            var when = Arguments.Length == 2
                           ? Arguments[1].ToLowerInvariant()
                           : null;

            if (when is not (Idle or Now))
                return Task.FromResult<String[]>([
                           Usage,
                           $"  {Idle}  once nothing is charging any more; no new session starts until then",
                           $"  {Now}   at once: what is charging is ended"
                       ]);

            if (!cli.MayDo(Permission.Run(when == Now ? NodeResources.ResetImmediate : NodeResources.Reset), $"{CommandName} {when}", out var refused))
                return Task.FromResult<String[]>([ refused ]);

            return Task.FromResult<String[]>([ Said(cli.Station.Restart(when == Now, StationCLI.Asked(cli.Caller))) ]);

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} {Idle}|{Now} - restart this station once nothing is charging any more, or at once";

        #endregion


        #region (static) Said(Result)

        /// <summary>
        /// What Restart answered, as a sentence.
        /// </summary>
        public static String Said(JObject Result)
        {

            var sessions = Result.Value<Int32?>("sessions") ?? 0;

            return Result.Value<String>("restart") == "now"
                       ? (Result.Value<Int32?>("ended") ?? 0) > 0
                             ? $"{sessions} session(s) were ended, and this station restarts its OCPP side now."
                             : "Nothing is charging: this station restarts its OCPP side now."
                       : $"This station restarts its OCPP side once the {sessions} session(s) charging now have ended, and starts no new session until then.";

        }

        #endregion

    }

}
