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

using System.Net.Sockets;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a charging station says when it cannot have a port it needs.
    /// </summary>
    /// <remarks>
    /// This exists because of what it used to say. Starting a second copy of
    /// the station produced thirteen frames of stack trace under the operating
    /// system's own words for a port in use - on a German Windows, "Normaler-
    /// weise darf jede Socketadresse (Protokoll, Netzwerkadresse oder
    /// Anschluss) nur jeweils einmal verwendet werden" - printed under eleven
    /// lines of English log, and naming neither the port nor which of the
    /// station's two servers had wanted it.
    ///
    /// It is the most ordinary way for a station not to start, and it happened
    /// twice in one afternoon of working on this.
    /// </remarks>
    [TestFixture]
    public class PortTests
    {

        #region Data

        private String directory = "";

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeSomewhereToPutThem()
        {
            directory = TestStations.TemporaryDirectory("ports");
        }

        [TearDown]
        public void TakeItAwayAgain()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region A port something else is already on

        /// <summary>
        /// The ordinary case: this station is already running.
        /// </summary>
        [Test]
        public async Task APortSomethingElseIsOnIsSaidInASentence()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline);

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      HTTPPort: first.HTTPPort);

            var problem = Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start())!;

            Assert.Multiple(() => {

                Assert.That(problem.Port,     Is.EqualTo(first.HTTPPort),
                            "The port that could not be had was not the one it was about.");

                Assert.That(problem.Whose,    Is.EqualTo(StationPort.WebInterface),
                            "It did not say which of the two servers wanted it.");

                Assert.That(problem.Because,  Is.EqualTo(SocketError.AddressAlreadyInUse));

                // The port named, in the sentence, where somebody reading it
                // will see it.
                Assert.That(problem.Message,  Does.Contain(first.HTTPPort.ToString()));
                Assert.That(problem.Message,  Does.Contain("web interface"));

            });

        }

        /// <summary>
        /// And in the language the rest of its output is in.
        /// </summary>
        /// <remarks>
        /// The operating system's message comes back in the machine's own
        /// language, which on this one is German. A station standing in Germany
        /// answering in German under eleven lines of English is how the first
        /// version of this read.
        /// </remarks>
        [Test]
        public async Task AndNotInWhateverLanguageTheMachineIsSetTo()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline);

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      HTTPPort: first.HTTPPort);

            var problem = Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start())!;

            Assert.That(problem.Message, Does.Not.Contain("Socketadresse"),
                        "The operating system's own words made it into the sentence.");

            // What the socket layer is called in its own vocabulary is the same
            // everywhere, and is what the message is built from.
            Assert.That(problem.Message, Does.Not.Contain("10048"));

        }

        #endregion

        #region The display's port

        /// <summary>
        /// The display has a port of its own, and its own way out.
        /// </summary>
        [Test]
        public async Task TheDisplaysPortIsSaidToBeTheDisplays()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline);

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      KioskPort: first.KioskPort);

            var problem = Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start())!;

            Assert.Multiple(() => {
                Assert.That(problem.Whose,   Is.EqualTo(StationPort.Display));
                Assert.That(problem.Port,    Is.EqualTo(first.KioskPort));
                Assert.That(problem.Message, Does.Contain("display"));
            });

        }

        /// <summary>
        /// And the web interface lets its own port go again on the way out.
        /// </summary>
        /// <remarks>
        /// By the time the display fails, the web interface has been listening
        /// for a moment. A process that is about to end would have it taken
        /// away anyway; a caller that catches this and carries on - a test, or
        /// a station made to try another port - would not.
        /// </remarks>
        [Test]
        public async Task TheWebInterfaceLetsGoWhenTheDisplayCannotStart()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline);

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      KioskPort: first.KioskPort);

            Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start());

            var listener = new TcpListener(System.Net.IPAddress.Loopback, second.HTTPPort.ToUInt16());

            try
            {
                Assert.DoesNotThrow(() => listener.Start(),
                                    "The web interface was still holding its port after the display could not have one.");
            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

    }

}
