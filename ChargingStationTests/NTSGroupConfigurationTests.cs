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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.ChargingStation.Configuration;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a group of time servers comes to in a charging station: the four
    /// it starts with, and a group from the file all the way to its display.
    /// </summary>
    /// <remarks>
    /// What the "nts" section may say about a group is the node's to read,
    /// and is tested in WWCP_Node, section by section and in effect on a
    /// node.
    /// </remarks>
    [TestFixture]
    public class NTSGroupConfigurationTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("nts-group");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region TheDefaultFourAreWhatAStationStartsWith()

        /// <summary>
        /// A station nobody has configured asks the PTB's four.
        /// </summary>
        /// <remarks>
        /// This is the case every existing installation is in - built the way
        /// it has always been built, with no "nts" section at all - and it
        /// used to be a group of one. Four is the better default for a clock
        /// that a signed reading hangs off: one host being rebooted no longer
        /// leaves the station without a time, and two that agree catch what
        /// one cannot, a server that is wrong rather than absent.
        ///
        /// The first of the four is still what the single-server client points
        /// at, so the group and the client cannot name different hosts - which
        /// is what the third assertion is for, and why it reads the same as it
        /// did when there was only one.
        /// </remarks>
        [Test]
        public async Task TheDefaultFourAreWhatAStationStartsWith()
        {

            await using var station = TestStations.New(directory);

            Assert.Multiple(() => {
                Assert.That(station.TimeSources.Bands(),                 Has.Count.EqualTo(1),  "peers, asked together");
                Assert.That(station.TimeSources.Bands()[0],              Has.Count.EqualTo(4));
                Assert.That(station.TimeSources.Bands()[0][0].Hostname,  Is.EqualTo(station.NTSClient.Hostname));
                Assert.That(station.TimeSources.MinServers,              Is.EqualTo(2));
            });

        }

        #endregion

        #region AConfiguredGroupReachesTheStationAndItsDisplay()

        /// <summary>
        /// The whole way through: four servers in the file, four in the group,
        /// four on the display - and no server named on a screen, because
        /// naming one of four would be the nicer-looking lie.
        /// </summary>
        [Test]
        public async Task AConfiguredGroupReachesTheStationAndItsDisplay()
        {

            await using var station = TestStations.New(
                                          directory,
                                          new JObject(
                                              new JProperty("nts", new JObject(
                                                  new JProperty("servers", new JArray("ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                                                                      "ptbtime3.ptb.de", "ptbtime4.ptb.de")),
                                                  new JProperty("minServers", 2)
                                              ))
                                          )
                                      );

            var clock = station.ClockJSON();

            Assert.Multiple(() => {

                Assert.That(station.TimeSources.Bands(),                 Has.Count.EqualTo(1));
                Assert.That(station.TimeSources.Bands()[0],              Has.Count.EqualTo(4));
                Assert.That(station.TimeSources.MinServers,              Is.EqualTo(2));

                Assert.That(clock["nts"]?["servers"]?.Values<String>(),  Has.Exactly(4).Items);
                Assert.That(clock["nts"]?["server"]?.Type,               Is.EqualTo(JTokenType.Null),
                            "a screen would have printed one of four as though it were the one");

                // Nothing has been checked yet, and the display is told that
                // in numbers rather than being left to read it out of a name.
                Assert.That(clock["nts"]?["asked"]?.Type,                Is.EqualTo(JTokenType.Null));
                Assert.That(clock["nts"]?["answered"]?.Type,             Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

    }

}
