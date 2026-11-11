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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.CLI;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

using cloud.charging.open.ChargingStation.CommandLine;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The command line of a charging station: the one a session over SSH
    /// gets, and the one ChargingStationCLI runs on its console.
    /// </summary>
    /// <remarks>
    /// Both are the station's own, made by the station library, so every
    /// program that hosts a station offers the same commands. What a station
    /// alone can be told is a command beside StationCLI in the library, and
    /// its tests belong here.
    /// </remarks>
    [TestFixture]
    public class StationCLITests : AChargingStationTests
    {

        #region Data

        /// <summary>
        /// The commands every node has, which a station has as well.
        /// </summary>
        private static readonly String[] NodeCommands = [
            WhoCommand.           CommandName,
            ListNTSGroupsCommand. CommandName,
            QueryDNSCommand.      CommandName,
            SyncNTSCommand.       CommandName,
            LogCommand.           CommandName,
            SSHKeysCommand.       CommandName,
            APIKeysCommand.       CommandName,
            SSHAccessRulesCommand.CommandName
        ];

        #endregion

        #region (private) Commands(CLI)

        /// <summary>
        /// The names of the commands the given command line lists under 'help'.
        /// </summary>
        private static async Task<String[]> Commands(NodeCLI CLI)

            => [.. (await CLI.Execute("help")).
                       Skip(1).
                       Select(line => line.Split(' ', 2)[0]).
                       Order(StringComparer.Ordinal)];

        #endregion


        #region OverSSHTheStationHasItsOwnCommandLine()

        /// <summary>
        /// Whoever signs in over SSH gets the station's command line, about
        /// this station, without the program that runs it having to say so.
        /// </summary>
        [Test]
        public async Task OverSSHTheStationHasItsOwnCommandLine()
        {

            Assert.That(Station.CommandLines, Is.Not.Null, "a session over SSH would get the node's command line only");

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = Station.CommandLines!(terminal, CLICaller.Console);

            Assert.That(cli,                          Is.InstanceOf<StationCLI>());
            Assert.That(((StationCLI) cli).Station,   Is.SameAs(Station));

        }

        #endregion

        #region ItHasTheCommandsOfEveryNodeAndItsOwn()

        /// <summary>
        /// The station's command line has every node's commands and its own,
        /// evses among them, and says of
        /// one it does not know that it does not know it.
        /// </summary>
        [Test]
        public async Task ItHasTheCommandsOfEveryNodeAndItsOwn()
        {

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = Station.CommandLines!(terminal, CLICaller.Console);

            Assert.That(await Commands(cli),                        Is.SupersetOf(NodeCommands));
            Assert.That(await Commands(cli),                        Does.Contain(EVSEsCommand.CommandName), "the station's own command");
            Assert.That((await cli.Execute("noSuchCommand")).Single(), Is.EqualTo("Unknown command: noSuchCommand"));

        }

        #endregion

        #region TheConsoleHasTheSameCommands()

        /// <summary>
        /// The command line ChargingStationCLI runs on its console has the
        /// same commands as the one over SSH.
        /// </summary>
        [Test]
        public async Task TheConsoleHasTheSameCommands()
        {

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var overSSH   = Station.CommandLines!(terminal, CLICaller.Console);
            using       var console   = new StationCLI(Station);

            Assert.That(await Commands(console), Is.EqualTo(await Commands(overSSH)));

        }

        #endregion

    }

}
