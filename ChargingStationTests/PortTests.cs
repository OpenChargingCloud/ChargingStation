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

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

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

                Assert.That(problem.Whose,    Is.EqualTo(NodePort.WebInterface),
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
                Assert.That(problem.Whose,   Is.EqualTo(ChargingStation.DisplayPort));
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

        #region The local app server's port

        /// <summary>
        /// The local app server has a port of its own, and its own way out.
        /// </summary>
        [Test]
        public async Task TheLocalAppServersPortIsSaidToBeItsOwn()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline,
                                                     LocalAppPort: IPPort.Parse(TestPorts.Free()));

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      LocalAppPort: first.LocalAppPort);

            var problem = Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start())!;

            Assert.Multiple(() => {
                Assert.That(problem.Whose,   Is.EqualTo(ChargingStation.AppPort));
                Assert.That(problem.Port,    Is.EqualTo(first.LocalAppPort));
                Assert.That(problem.Message, Does.Contain("local app"));
            });

        }

        /// <summary>
        /// And the web interface and the display let their ports go again on
        /// the way out.
        /// </summary>
        /// <remarks>
        /// By the time the local app server fails, both have been listening for
        /// a moment. The node below lets go of the web interface's port; the
        /// display is the station's own, and nobody else would stop it - a
        /// station that did not start is not stopped by its Stop().
        /// </remarks>
        [Test]
        public async Task TheWebInterfaceAndTheDisplayLetGoWhenTheLocalAppServerCannotStart()
        {

            await using var first = TestStations.New(Path.Combine(directory, "first"), TestStations.Offline,
                                                     LocalAppPort: IPPort.Parse(TestPorts.Free()));

            await first.Start();

            await using var second = TestStations.New(Path.Combine(directory, "second"), TestStations.Offline,
                                                      LocalAppPort: first.LocalAppPort);

            Assert.ThrowsAsync<PortUnavailableException>(async () => await second.Start());

            foreach (var (port, what) in new[] { (second.HTTPPort, "web interface"), (second.KioskPort!.Value, "display") })
            {

                var listener = new TcpListener(System.Net.IPAddress.Loopback, port.ToUInt16());

                try
                {
                    Assert.DoesNotThrow(() => listener.Start(),
                                        $"The {what} was still holding its port after the local app server could not have one.");
                }
                finally
                {
                    listener.Stop();
                }

            }

        }

        /// <summary>
        /// The local app server is refused the web interface's port and the
        /// display's, before anything listens: the point of it is to be
        /// somewhere else.
        /// </summary>
        [Test]
        public void TheLocalAppServerIsNeitherTheWebInterfaceNorTheDisplay()
        {

            var port = IPPort.Parse(TestPorts.Free());

            Assert.Multiple(() => {

                Assert.Throws<ArgumentException>(() => TestStations.New(Path.Combine(directory, "web"),     TestStations.Offline,
                                                                        HTTPPort:      port,
                                                                        LocalAppPort:  port));

                Assert.Throws<ArgumentException>(() => TestStations.New(Path.Combine(directory, "display"), TestStations.Offline,
                                                                        KioskPort:     port,
                                                                        LocalAppPort:  port));

            });

        }

        #endregion

        #region The ports a test gives a station

        /// <summary>
        /// A test station's web interface and display are given ports that this
        /// test run hands out to nobody else.
        /// </summary>
        /// <remarks>
        /// The operating system hands the same free port out twice. On Debian it
        /// stopped a station's setup, which was told that its display and its web
        /// interface would both listen on 127.0.0.1:36729. The ports come from
        /// the kit's TestPorts, which remembers every one it hands out, and a
        /// port it has handed out cannot be claimed again.
        /// </remarks>
        [Test]
        public async Task AStationIsGivenPortsNobodyElseInTheRunIsGiven()
        {

            await using var station = TestStations.New(Path.Combine(directory, "given"), TestStations.Offline);

            Assert.Multiple(() => {
                Assert.That(TestPorts.TryClaim(station.HTTPPort.ToUInt16()),          Is.False, "The web interface's port could still be claimed by somebody else.");
                Assert.That(TestPorts.TryClaim(station.KioskPort!.Value.ToUInt16()),  Is.False, "The display's port could still be claimed by somebody else.");
            });

        }

        #endregion

    }

}
