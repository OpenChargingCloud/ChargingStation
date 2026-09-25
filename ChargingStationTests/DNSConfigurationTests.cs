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
    /// What the "dns" section may say about the name servers - and what it may
    /// not, said as a sentence about the file rather than as an exception.
    /// </summary>
    /// <remarks>
    /// The log and the banner name a name server as "udp://10.0.0.1:53", which
    /// is not a form this section has ever taken - here, or in the vehicle and
    /// the energy meter whose sections it shares. A station given it stopped
    /// at its start with an ArgumentException out of IPAddress.TryParse, whose
    /// message named neither the file nor the key, and the DNS page got an
    /// internal server error for it: the parser found an address somewhere in
    /// the text and handed all of the text to one that threw on the rest.
    ///
    /// What the section may say is the node's to read, and is tested in
    /// WWCP_Node. What is tested here is that a station stops over it the way
    /// it stops over any file it cannot read.
    /// </remarks>
    [TestFixture]
    public class DNSConfigurationTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("dns");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region AStationWhoseFileSaysSoStopsWithASentence()

        /// <summary>
        /// And at a start: the station stops over the file the way it stops
        /// over any file it cannot read, saying what is wrong and where -
        /// rather than with an ArgumentException naming neither, which is how
        /// it stopped before.
        /// </summary>
        /// <remarks>
        /// Built and never started: the constructor is what reads the file.
        /// </remarks>
        [Test]
        public void AStationWhoseFileSaysSoStopsWithASentence()
        {

            var file     = Path.Combine(directory, "configuration.json");

            var problem  = Assert.Throws<InvalidOperationException>(() => TestStations.New(
                                                                              directory,
                                                                              JObject.Parse("""{ "dns": { "servers": [ "udp://213.133.98.98:53" ] } }""")
                                                                          ));

            Assert.That(problem?.Message,  Does.Contain("'dns.servers'").And.Contain("udp://213.133.98.98:53").And.Contain(file));

        }

        #endregion

    }

}
