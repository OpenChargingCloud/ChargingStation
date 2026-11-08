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

using System.Net;
using System.Net.Sockets;
using System.Reflection;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.TCP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether a charging station lets go of the servers of its display and of
    /// its local app, and of every server it built for a move of its display.
    /// </summary>
    /// <remarks>
    /// A Hermod server ticks from when it is built until it is let go of: its
    /// maintenance timer, and its Warden every 30 s. Stopping it does not end
    /// that. A station used to only stop these servers, and the station's
    /// tests left hundreds of them ticking by the end of a run - the node
    /// below lets go of the web interface's server since WWCP_Node b23be7cd.
    ///
    /// Nothing a server says in public tells whether it was let go of, so
    /// these tests ask its maintenance timer, as the kit does
    /// (<see cref="NodeConformanceTests.TicksStill(ATCPServer)"/>), and find
    /// the servers where the station keeps them.
    /// </remarks>
    [TestFixture]
    public class ServerTimersTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("servers");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region (private static) Field(Station, Name)

        /// <summary>
        /// A private field of the station, which a test has to learn anew where
        /// it is gone.
        /// </summary>
        private static T Field<T>(ChargingStation  Station,
                                  String           Name)

            where T : class
        {

            var value = typeof(ChargingStation).GetField(Name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Station) as T;

            Assert.That(value, Is.Not.Null, $"A station has no '{Name}' any more: this test has to learn where its servers are now.");

            return value!;

        }

        #endregion

        #region (private static) TheDisplay(Station) / TheLocalApp(Station) / StillToBeLetGoOf(Station)

        /// <summary>
        /// The server of the display.
        /// </summary>
        private static HTTPServer TheDisplay(ChargingStation Station)
            => Field<HTTPServer>(Station, "kioskServer");

        /// <summary>
        /// The server of the local app.
        /// </summary>
        private static HTTPServer TheLocalApp(ChargingStation Station)
            => Field<HTTPServer>(Station, "localAppServer");

        /// <summary>
        /// The servers of the display the station has not let go of yet.
        /// </summary>
        private static HTTPServer[] StillToBeLetGoOf(ChargingStation Station)
        {

            var list = Field<List<HTTPServer>>(Station, "displayServers");

            lock (list)
                return [.. list];

        }

        #endregion

        #region (private static) MoveTheDisplay(Station, Port)

        /// <summary>
        /// Move the display to the given port, as the Configuration page does.
        /// </summary>
        private static Task<(Boolean Success, String? Error, Boolean NotSaved)> MoveTheDisplay(ChargingStation  Station,
                                                                                               UInt16           Port)

            => Station.UpdateDisplayConfiguration(new JObject(new JProperty("port", Port)));

        #endregion

        #region (private static) LetGoOfWithinPatience(Server)

        /// <summary>
        /// Whether the given server is let go of before Patience has passed,
        /// looked at every 20 ms. Asking holds a timer that still ticks where
        /// it is, which for a server about to be let go of is no loss.
        /// </summary>
        private static async Task<Boolean> LetGoOfWithinPatience(HTTPServer Server)
        {

            var watch = System.Diagnostics.Stopwatch.StartNew();

            while (watch.Elapsed < TalkingToTheCSMSTests.Patience)
            {

                if (!NodeConformanceTests.TicksStill(Server))
                    return true;

                await Task.Delay(20);

            }

            return !NodeConformanceTests.TicksStill(Server);

        }

        #endregion

        #region (private) WithALocalApp()

        /// <summary>
        /// A station with a display and a local app, started.
        /// </summary>
        private Task<ChargingStation> WithALocalApp()

            => TestPorts.StartedOnFreshPorts(() => TestStations.New(directory, LocalAppPort: IPPort.Parse(TestPorts.Free())));

        #endregion


        #region AStationLetGoOfLetsGoOfTheServersOfItsDisplayAndItsLocalApp()

        /// <summary>
        /// A station that is let go of lets go of the servers of its display and
        /// of its local app.
        /// </summary>
        [Test]
        public async Task AStationLetGoOfLetsGoOfTheServersOfItsDisplayAndItsLocalApp()
        {

            var station   = await WithALocalApp();
            var display   = TheDisplay (station);
            var localApp  = TheLocalApp(station);

            await station.DisposeAsync();

            Assert.Multiple(() => {
                Assert.That(NodeConformanceTests.TicksStill(display),   Is.False, "The station was let go of, and its display's server still ticks.");
                Assert.That(NodeConformanceTests.TicksStill(localApp),  Is.False, "The station was let go of, and its local app's server still ticks.");
            });

        }

        #endregion

        #region TheLocalAppsWebSocketHasNoServerOfItsOwn()

        /// <summary>
        /// The WebSocket of the local app speaks on the connections the local
        /// app's server hands it, and has no TCP server of its own: one would
        /// tick from the station's start on - a listener, its warden and the
        /// cache of its DNS client - and nothing lets go of a WebSocket.
        /// </summary>
        [Test]
        public async Task TheLocalAppsWebSocketHasNoServerOfItsOwn()
        {

            var station = TestStations.New(directory, LocalAppPort: IPPort.Parse(TestPorts.Free()));

            try
            {

                var api        = station.LocalAppAPI;

                Assert.That(api, Is.Not.Null, "A station with a local app port has no local app.");

                var webSockets = typeof(LocalAppHTTPAPI).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).
                                                         Select (field => field.GetValue(api)).
                                                         OfType<AWebSocketEndpoint>().
                                                         ToArray();

                Assert.That(webSockets, Has.Length.EqualTo(1), "The local app has no WebSocket where this test looks for one: it has to learn where it is now.");

                Assert.That(webSockets[0], Is.Not.InstanceOf<ITCPServer>(),
                            "The local app's WebSocket has a TCP server of its own, which ticks until it is let go of.");

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region AStationNeverStartedLetsGoOfThemAsWell()

        /// <summary>
        /// A station that was never started has built its servers all the
        /// same, and lets go of them all the same.
        /// </summary>
        [Test]
        public async Task AStationNeverStartedLetsGoOfThemAsWell()
        {

            var station   = TestStations.New(directory, LocalAppPort: IPPort.Parse(TestPorts.Free()));
            var display   = TheDisplay (station);
            var localApp  = TheLocalApp(station);

            await station.DisposeAsync();

            Assert.Multiple(() => {
                Assert.That(NodeConformanceTests.TicksStill(display),   Is.False, "The station was let go of, and its display's server still ticks.");
                Assert.That(NodeConformanceTests.TicksStill(localApp),  Is.False, "The station was let go of, and its local app's server still ticks.");
            });

        }

        #endregion

        #region AStationStoppedKeepsItsServersForTheNextStart()

        /// <summary>
        /// A station that only stops keeps the servers of its display and of
        /// its local app, since a start may follow.
        /// </summary>
        [Test]
        public async Task AStationStoppedKeepsItsServersForTheNextStart()
        {

            var station = await WithALocalApp();

            try
            {

                await station.Stop();

                Assert.Multiple(() => {
                    Assert.That(NodeConformanceTests.TicksStill(TheDisplay (station)),  Is.True, "The station only stopped, and its display's server was let go of.");
                    Assert.That(NodeConformanceTests.TicksStill(TheLocalApp(station)),  Is.True, "The station only stopped, and its local app's server was let go of.");
                });

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region APortTheDisplayMovedAwayFromIsLetGoOfAfterTheHandover()

        /// <summary>
        /// The port the display moved away from is let go of once its handover
        /// is over, and not only stopped.
        /// </summary>
        [Test]
        public async Task APortTheDisplayMovedAwayFromIsLetGoOfAfterTheHandover()
        {

            var station = await TestStations.Started(TestStations.New(directory), directory);

            try
            {

                station.DisplayHandover = TimeSpan.FromMilliseconds(200);

                var old    = TheDisplay(station);
                var moved  = await MoveTheDisplay(station, TestPorts.Free());

                Assert.That(moved.Success, Is.True, moved.Error);

                Assert.That(await LetGoOfWithinPatience(old),
                            Is.True,
                            $"The old port's server still ticks {TalkingToTheCSMSTests.Patience.TotalSeconds} s after a handover of 200 ms.");

                Assert.That(StillToBeLetGoOf(station), Is.EqualTo(new[] { TheDisplay(station) }), "what the station has still to let go of");

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region AStationThatStopsDuringAHandoverLetsGoOfTheOldPort()

        /// <summary>
        /// A station that stops while the port its display moved away from
        /// still tells screens where it went lets go of that port, and keeps
        /// the display's.
        /// </summary>
        [Test]
        public async Task AStationThatStopsDuringAHandoverLetsGoOfTheOldPort()
        {

            var station = await TestStations.Started(TestStations.New(directory), directory);

            try
            {

                station.DisplayHandover = TimeSpan.FromHours(1);

                var old    = TheDisplay(station);
                var moved  = await MoveTheDisplay(station, TestPorts.Free());

                Assert.That(moved.Success, Is.True, moved.Error);

                await station.Stop();

                Assert.Multiple(() => {
                    Assert.That(NodeConformanceTests.TicksStill(old),                  Is.False, "The station stopped, and the port its display moved away from still ticks.");
                    Assert.That(NodeConformanceTests.TicksStill(TheDisplay(station)),  Is.True,  "The station only stopped, and its display's server was let go of.");
                });

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region AMoveBeforeTheStartLetsGoOfTheServerItReplaced()

        /// <summary>
        /// A display moved before the station starts was listening nowhere,
        /// and the server it had is let go of at once.
        /// </summary>
        [Test]
        public async Task AMoveBeforeTheStartLetsGoOfTheServerItReplaced()
        {

            var station = TestStations.New(directory);

            try
            {

                var old    = TheDisplay(station);
                var moved  = await MoveTheDisplay(station, TestPorts.Free());

                Assert.That(moved.Success, Is.True, moved.Error);

                Assert.Multiple(() => {
                    Assert.That(TheDisplay(station),                     Is.Not.SameAs(old), "the display has a server of its own");
                    Assert.That(NodeConformanceTests.TicksStill(old),    Is.False, "The display moved before the start, and its old server still ticks.");
                    Assert.That(StillToBeLetGoOf(station),               Is.EqualTo(new[] { TheDisplay(station) }), "what the station has still to let go of");
                });

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region AMoveToAPortSomethingElseHasLeavesNoServerBehind()

        /// <summary>
        /// A move to a port something else has is refused, and the server
        /// built for that port is let go of.
        /// </summary>
        [Test]
        public async Task AMoveToAPortSomethingElseHasLeavesNoServerBehind()
        {

            var station = await TestStations.Started(TestStations.New(directory), directory);

            try
            {

                using var squatter = new TcpListener(System.Net.IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
                squatter.Start();

                var display  = TheDisplay(station);
                var moved    = await MoveTheDisplay(station, (UInt16) ((IPEndPoint) squatter.LocalEndpoint).Port);

                Assert.Multiple(() => {
                    Assert.That(moved.Success,              Is.False, "a port something else has");
                    Assert.That(TheDisplay(station),        Is.SameAs(display), "the display stays where it is");
                    Assert.That(StillToBeLetGoOf(station),  Is.EqualTo(new[] { display }), "The server built for the port that could not be had was not let go of.");
                });

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

        #region AMoveTheFileCannotTakeLeavesNoServerBehind(Started)

        /// <summary>
        /// A move the configuration file cannot take is refused, and the server
        /// opened for it is let go of - listening already on a station that
        /// runs, and only built on one that does not yet.
        /// </summary>
        [TestCase(true)]
        [TestCase(false)]
        public async Task AMoveTheFileCannotTakeLeavesNoServerBehind(Boolean Started)
        {

            var station = Started
                              ? await TestStations.Started(TestStations.New(directory), directory)
                              : TestStations.New(directory);

            try
            {

                // Where the file is written first, so that it cannot be.
                Directory.CreateDirectory(station.ConfigFile.Path + ".tmp");

                var display  = TheDisplay(station);
                var moved    = await MoveTheDisplay(station, TestPorts.Free());

                Assert.Multiple(() => {
                    Assert.That(moved.Success,              Is.False, "a file that cannot be written");
                    Assert.That(moved.NotSaved,             Is.True,  moved.Error);
                    Assert.That(TheDisplay(station),        Is.SameAs(display), "the display stays where it is");
                    Assert.That(StillToBeLetGoOf(station),  Is.EqualTo(new[] { display }), "The server opened for the move was not let go of.");
                });

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

    }

}
