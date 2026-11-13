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

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// testconnection: make one of the connections that are written down,
    /// once, ask the back end something in OCPP, and say everything that
    /// happened and when.
    /// </summary>
    /// <remarks>
    /// What the "Test" button beside a connection on the Connections page
    /// does, at the permission that button asks for, and with its answer line
    /// by line: the milliseconds since the test began, how much it matters,
    /// and what happened. A connection is named by its identification or by
    /// its description; without a name the command says which there are.
    /// Nothing is written down and nothing stays connected.
    /// </remarks>
    public class TestConnectionCommand(StationCLI CLI) : ACLICommand<StationCLI>(CLI),
                                                          ICLICommand
    {

        #region Data

        /// <summary>
        /// What is typed: "testconnection".
        /// </summary>
        public static readonly String CommandName = nameof(TestConnectionCommand)[..^7].ToLowerInvariant();

        /// <summary>
        /// How it is used.
        /// </summary>
        public static readonly String Usage
            = $"Usage: {CommandName} <id or description of a connection> [{ChargingStation.TestAsksHeartbeat}|{ChargingStation.TestAsksNothing}]";

        #endregion


        #region Suggest(Arguments)

        public override IEnumerable<SuggestionResponse> Suggest(String[] Arguments)

            => CommandName.StartsWith(Arguments[0], StringComparison.OrdinalIgnoreCase)
                   ? [ SuggestionResponse.CommandCompleted(CommandName) ]
                   : [];

        #endregion

        #region Execute(Arguments, CancellationToken)

        public override async Task<String[]> Execute(String[]           Arguments,
                                                     CancellationToken  CancellationToken)
        {

            // What the button on the page needs: a test makes the station open
            // a connection and show a credential.
            if (!cli.MayDo(Permission.Run(StationAccess.Connections), CommandName, out var refused))
                return [ refused ];

            var connections = cli.Station.Connections.Connections;

            if (Arguments.Length < 2)
                return [
                           Usage,
                           .. connections.Count == 0
                                  ? [ "No connection is written down." ]
                                  : connections.Select(Said)
                       ];

            // The last word is what to ask, where it is one of the two; a
            // description may be of several words, and is everything before it.
            var words  = Arguments.Skip(1).ToArray();
            var asks   = words.Length > 1 && IsARequest(words[^1])
                             ? words[^1].ToLowerInvariant()
                             : null;
            var name   = String.Join(" ", asks is null ? words : words[..^1]).Trim().Trim('"');

            var found  = connections.Where(connection => connection.Id.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();

            if (found.Length == 0)
                found  = [.. connections.Where(connection => connection.Description.Equals(name, StringComparison.OrdinalIgnoreCase))];

            if (found.Length == 0)
                return [
                           $"No connection is written down as '{name}'.",
                           .. connections.Select(Said)
                       ];

            if (found.Length > 1)
                return [
                           $"{found.Length} connections are written down as '{name}'; name one by its identification.",
                           .. found.Select(Said)
                       ];

            // Handed over the way the page hands over its form: what the
            // connection says of itself.
            var fields = found[0].ToJSON();

            if (asks is not null)
                fields[ChargingStation.TestRequestKey] = asks;

            var result = await cli.Station.TestConnection(fields, CancellationToken);

            return Lines(result);

        }

        #endregion

        #region Help()

        public override String Help()
            => $"{CommandName} <connection> [{ChargingStation.TestAsksHeartbeat}|{ChargingStation.TestAsksNothing}] - make a connection once, ask its back end in OCPP, and say what happened and when";

        #endregion


        #region (static) Lines(Result)

        /// <summary>
        /// The answer of a connection test, line by line: when, how much it
        /// matters, what happened - and what came of it at the end.
        /// </summary>
        /// <param name="Result">What TestConnection answered.</param>
        public static String[] Lines(JObject Result)
        {

            var steps  = Result["steps"] as JArray ?? [];
            var width  = Math.Max(1, steps.Select(step => (step.Value<Int64?>("at_ms") ?? 0).ToString().Length).DefaultIfEmpty(1).Max());

            var ocpp   = Result["ocpp"] as JObject;

            return [
                       .. steps.Select(step => $"{(step.Value<Int64?>("at_ms") ?? 0).ToString().PadLeft(width)} ms  " +
                                               $"{(step.Value<String>("level") ?? "").PadRight(7)}  " +
                                               step.Value<String>("text")),

                       (Result.Value<Boolean?>("ok") == true
                            ? "OK"
                            : "FAILED") +
                       $" after {Result.Value<Int64?>("runtime_ms") ?? 0} ms" +
                       (ocpp?.Value<Int64?>("roundTrip_ms") is Int64 roundTrip
                            ? $"; the {ocpp.Value<String>("request")} was answered within {roundTrip} ms"
                            : "") +
                       "."
                   ];

        }

        #endregion

        #region (private static) IsARequest(Word)

        private static Boolean IsARequest(String Word)

            => Word.Equals(ChargingStation.TestAsksHeartbeat, StringComparison.OrdinalIgnoreCase) ||
               Word.Equals(ChargingStation.TestAsksNothing,   StringComparison.OrdinalIgnoreCase);

        #endregion

        #region (private static) Said(Connection)

        /// <summary>
        /// One connection as it is named here: its identification, its
        /// description, and where it goes.
        /// </summary>
        private static String Said(ConnectionEntry Connection)
            => $"  {Connection.Id}  {Connection.Description}  {Connection.URL}";

        #endregion

    }

}
