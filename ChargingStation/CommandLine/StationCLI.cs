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

using System.Reflection;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

#endregion

namespace cloud.charging.open.ChargingStation.CommandLine
{

    /// <summary>
    /// The command line of a running charging station.
    /// </summary>
    /// <remarks>
    /// The node's command line, with the commands every node has - syncNTS
    /// among them - and the console until 'quit', Ctrl+C or SIGTERM. What only
    /// a charging station can be told is a command built from a StationCLI in
    /// this assembly, found as the node's are: a new command is a new file and
    /// nothing else, and is tested in ChargingStationTests.
    /// The station makes it for every session over SSH itself (see its
    /// constructor), so every program that hosts a station offers the same
    /// commands there; ChargingStationCLI runs it on its console as well.
    /// </remarks>
    public class StationCLI : NodeCLI
    {

        #region Data

        /// <summary>
        /// What stands in front of the command being typed.
        /// </summary>
        public const String Prompt = "ChargingStation> ";

        #endregion

        #region Properties

        /// <summary>
        /// The charging station these commands are about.
        /// </summary>
        public ChargingStation Station { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Create the command line of the given charging station.
        /// </summary>
        /// <param name="Station">The running charging station.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one is searched either way.</param>
        public StationCLI(ChargingStation    Station,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Station, AssembliesWithCLICommands)

        {

            this.Station = Station;

            RegisterCLIType(typeof(StationCLI));

        }

        /// <summary>
        /// Create the command line of the given ChargingStation on the given terminal,
        /// for the given caller - a session over SSH.
        /// </summary>
        /// <param name="Station">The running ChargingStation.</param>
        /// <param name="Terminal">What the command line is typed at and written on.</param>
        /// <param name="Caller">Who is typing at it.</param>
        /// <param name="AssembliesWithCLICommands">Further assemblies to search for commands. This one and the node's are searched either way.</param>
        public StationCLI(ChargingStation    Station,
                          ICLITerminal       Terminal,
                          CLICaller          Caller,
                          params Assembly[]  AssembliesWithCLICommands)

            : base(Station, Terminal, Caller, AssembliesWithCLICommands)

        {

            this.Station = Station;

            RegisterCLIType(typeof(StationCLI));

        }

        #endregion


        #region (static) Asked(Caller)

        /// <summary>
        /// Who asked, as the log's sentences go on: "somebody at the command
        /// line", or "'root' at the command line over SSH".
        /// </summary>
        public static String Asked(CLICaller Caller)

            => Caller.AtTheConsole
                   ? "somebody at the command line"
                   : Caller.Asker;

        #endregion

        #region (protected override) GetPrompt()

        /// <summary>
        /// What this program is, because a charging station usually runs on a
        /// bench next to a vehicle, and the two consoles should not have to be
        /// told apart by what scrolls past on them.
        /// </summary>
        /// <remarks>
        /// Not a name of its own: unlike the vehicle, a station has none to
        /// give. Its two OCPP identities are fixed test values and would say
        /// less than this does.
        /// </remarks>
        protected override String GetPrompt()

            => Prompt;

        #endregion

    }

}
