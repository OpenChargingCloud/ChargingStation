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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Net.WebSockets;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether a charging station that was told to stop actually does.
    /// </summary>
    /// <remarks>
    /// This exists because it once did not. A browser on the Logs page holds a
    /// request open that is waiting for the next log entry rather than for its
    /// socket, so the server closing that socket underneath it does not wake
    /// it - and Hermod waits for every request it started before it reports
    /// itself stopped. A station with one browser watching would therefore
    /// never finish shutting down, which on a machine being restarted means
    /// waiting out a kill timeout instead of stopping.
    ///
    /// Stop() now ends the event streams before it stops the servers. These
    /// tests are what says it still does.
    ///
    /// A station has two listeners - the web interface and the display, on
    /// their own ports - and both have to be down before Stop() returns. Only
    /// the first one carries an event stream: the display polls.
    ///
    /// One thing cannot be undone once it happens: a stop that never returns
    /// cannot be cancelled, so a test that hits it leaves it running. A later
    /// test in the same run can then stop in milliseconds where on its own it
    /// hangs, which would report a pass nobody earned.
    ///
    /// So the first such test poisons the rest: every test after one that was
    /// left waiting is reported inconclusive rather than passed. The run still
    /// shows the real failure, and shows plainly that it cannot vouch for what
    /// came after it - run them one at a time to see the rest.
    /// </remarks>
    public class ShutdownTests
    {

        #region Data

        /// <summary>
        /// How long stopping may take before this counts as not stopping.
        /// </summary>
        /// <remarks>
        /// Stopping takes single-digit milliseconds when it works, and forever
        /// when it does not - so anything in between is a slow machine rather
        /// than a near miss, and this is set where a slow machine still passes.
        /// </remarks>
        private static readonly TimeSpan  MustStopWithin = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Whether a stop in this run was given up on and left running.
        /// </summary>
        /// <remarks>
        /// Static, and deliberately never reset: the abandoned stop is still
        /// there for the rest of the process, so every test after it is
        /// suspect for the rest of the process.
        /// </remarks>
        private static Boolean  aStopWasLeftRunning;

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {

            if (aStopWasLeftRunning)
                Assert.Inconclusive(
                    "An earlier test in this fixture was left waiting for a stop that never returned, and " +
                    "that stop is still running. Whatever this test would report cannot be trusted - run it " +
                    "on its own."
                );

            directory = TestStations.TemporaryDirectory("shutdown");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region StopsWithoutADisplay()

        /// <summary>
        /// The other branch of Stop(): a station started with --no-kiosk has
        /// one listener rather than two, and has to stop just the same.
        /// </summary>
        [Test]
        public async Task StopsWithoutADisplay()
        {

            var (station, http) = await StartOne(WithDisplay: false);

            using (http)
            {

                Assert.That(station.KioskURL, Is.Null,
                            "This station was asked for no display and made one anyway.");

                using var stream = await EventStream.OpenAndSettle(station, http);

                var elapsed = await TimeTheStop(station);

                Assert.That(elapsed, Is.LessThan(MustStopWithin),
                            $"A station without a display took {elapsed.TotalSeconds:F1} s to stop.");

            }

        }

        #endregion


        #region StopsWithAnAppOnTheWebSocket()

        /// <summary>
        /// A station with an app on the WebSocket of its local app server
        /// stops just the same - and the app is told so with a close frame,
        /// rather than finding out from a connection that broke.
        /// </summary>
        /// <remarks>
        /// The third listener, and the one kind of request here that stays
        /// open for as long as the app likes: the HTTP server hands the
        /// connection over and keeps waiting on it.
        /// </remarks>
        [Test]
        public async Task StopsWithAnAppOnTheWebSocket()
        {

            var station = await TestPorts.StartedOnFreshPorts(() => TestStations.New(directory, TestStations.Offline,
                                                                                     LocalAppPort: IPPort.Parse(TestPorts.Free())));

            using var app = new ClientWebSocket();

            await app.ConnectAsync(new Uri($"ws://{new Uri(station.LocalAppURL!.Value.ToString()).Authority}/localApp"),
                                   CancellationToken.None);

            var told     = app.ReceiveAsync(new Byte[1024], CancellationToken.None);

            var elapsed  = await TimeTheStop(station);

            Assert.That(elapsed, Is.LessThan(MustStopWithin),
                        $"A station with an app on its WebSocket took {elapsed.TotalSeconds:F1} s to stop.");

            Assert.That(await Task.WhenAny(told, Task.Delay(TimeSpan.FromSeconds(10))), Is.SameAs(told),
                        "The station stopped and the app on its WebSocket heard nothing.");

            try
            {

                var goodbye = await told;

                Assert.Multiple(() => {
                    Assert.That(goodbye.MessageType,             Is.EqualTo(WebSocketMessageType.Close),
                                "The app was sent something other than goodbye.");
                    Assert.That(goodbye.CloseStatus,             Is.EqualTo(WebSocketCloseStatus.EndpointUnavailable));
                    Assert.That(goodbye.CloseStatusDescription,  Is.EqualTo("The charging station is stopping."));
                });

            }
            catch (WebSocketException broken)
            {
                Assert.Fail($"The app found out from a broken connection rather than a close frame: {broken.Message}");
            }

        }

        #endregion


        #region AStepThatFailsDoesNotKeepTheOthersFromBeingTaken()

        /// <summary>
        /// A station whose stopping fails at its first step - its link below
        /// the cable will not end - takes the others all the same: the app on
        /// the WebSocket of its local app server is told that it is going, and
        /// neither the display nor the local app server listens any more,
        /// beside the web interface, which the node stops. What failed reaches
        /// whoever stopped the station, and the log says which step it was.
        /// </summary>
        /// <remarks>
        /// A stop that fails has stopped all the same and is not begun again:
        /// a second Stop() - the one that letting go of the station does - does
        /// nothing. So whatever a failed step kept the station from doing would
        /// stay undone for as long as the process runs.
        /// </remarks>
        [Test]
        public async Task AStepThatFailsDoesNotKeepTheOthersFromBeingTaken()
        {

            File.WriteAllText(Path.Combine(directory, "configuration.json"), TestStations.Offline.ToString());

            var station   = await TestPorts.StartedOnFreshPorts(() => new AStationWhoseV2GLinkWillNotEnd(directory, IPPort.Parse(TestPorts.Free())));

            var warnings  = new ConcurrentQueue<String>();

            station.Log.OnLogged += entry => {
                if (entry.Level == LogLevel.Warning)
                    warnings.Enqueue(entry.Message);
            };

            var ports     = new (String What, UInt16 Port)[] {
                                ("the web interface",     PortOf(station.WebInterfaceURL.ToString())),
                                ("the display",           PortOf(station.KioskURL!.Value.ToString())),
                                ("the local app server",  PortOf(station.LocalAppURL!.Value.ToString()))
                            };

            using var app = new ClientWebSocket();

            await app.ConnectAsync(new Uri($"ws://{new Uri(station.LocalAppURL!.Value.ToString()).Authority}/localApp"),
                                   CancellationToken.None);

            var told      = app.ReceiveAsync(new Byte[1024], CancellationToken.None);

            var stopping  = station.Stop();

            if (await Task.WhenAny(stopping, Task.Delay(MustStopWithin)) != stopping)
            {
                // Left running, for the reason TimeTheStop gives.
                aStopWasLeftRunning = true;
                Assert.Fail($"A station whose V2G link would not end took more than {MustStopWithin.TotalSeconds:F0} s to stop.");
            }

            Assert.That(async () => await stopping,
                        Throws.InstanceOf<IOException>().With.Message.EqualTo(AStationWhoseV2GLinkWillNotEnd.Why),
                        "What failed did not reach whoever stopped the station.");

            var goodbye   = await Task.WhenAny(told, Task.Delay(TimeSpan.FromSeconds(10))) == told && told.IsCompletedSuccessfully
                                ? told.Result.MessageType
                                : (WebSocketMessageType?) null;

            var listening = ports.Where (port => Listening(port.Port)).
                                  Select(port => $"{port.What} on port {port.Port}").
                                  ToArray();

            Assert.Multiple(() => {

                Assert.That(goodbye,    Is.EqualTo(WebSocketMessageType.Close),
                            "The app on the WebSocket was not told that the station is going.");

                Assert.That(listening,  Is.Empty,
                            "Still listening once the station had stopped: " + String.Join(", ", listening));

                Assert.That(warnings,   Has.Some.Matches<String>(warning => warning.Contains("ending the V2G link") &&
                                                                            warning.Contains(AStationWhoseV2GLinkWillNotEnd.Why)),
                            "The log does not say which step failed. Its warnings: " + String.Join(" | ", warnings));

            });

        }

        #endregion


        #region (private) StartOne(WithDisplay = true)

        /// <summary>
        /// A station, listening, and a browser already signed in to it.
        /// </summary>
        private async Task<(ChargingStation Station, HttpClient HTTP)> StartOne(Boolean WithDisplay = true)
        {

            // Offline: nothing here should wait on a network while it is
            // trying to measure how long stopping takes.
            var station = await TestPorts.StartedOnFreshPorts(() => TestStations.New(directory, TestStations.Offline, WithDisplay));

            return (station, await SignIn(station));

        }

        #endregion

        #region (private static) SignIn(Station)

        private static async Task<HttpClient> SignIn(ChargingStation Station)
        {

            var http = new HttpClient(new HttpClientHandler { UseCookies = true }) {
                           BaseAddress = new Uri(Station.WebInterfaceURL.ToString())
                       };

            var response = await http.PostAsync(
                                     $"{ChargingStation.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                     new FormUrlEncodedContent([
                                         new KeyValuePair<String, String>("login",     ChargingStation.DefaultAdminUser),
                                         new KeyValuePair<String, String>("password",  Station.GeneratedPassword ?? "")
                                     ])
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True, "Signing in failed.");

            return http;

        }

        #endregion

        #region (private static) TimeTheStop(Station)

        /// <summary>
        /// How long it took to stop - and, when it does not stop at all, how
        /// long this test was prepared to wait.
        /// </summary>
        /// <remarks>
        /// The stop is raced against a timer rather than simply awaited,
        /// because the failure this whole file is about is a stop that never
        /// returns: awaiting it would hang the test run instead of failing it,
        /// and a hung run says nothing about which test hung.
        /// </remarks>
        private static async Task<TimeSpan> TimeTheStop(ChargingStation Station)
        {

            var clock    = Stopwatch.StartNew();

            var stopping = Station.Stop();
            var giveUp   = Task.Delay(MustStopWithin);

            var first    = await Task.WhenAny(stopping, giveUp);

            clock.Stop();

            if (first == stopping)
            {
                // Observed, so that a stop which failed rather than hung is
                // reported as the exception it threw.
                await stopping;
                return clock.Elapsed;
            }

            // Left running, because there is no way not to: awaiting the stop
            // that did not finish is exactly the hang this is here to report
            // instead of. What can be done is to stop trusting what comes next.
            aStopWasLeftRunning = true;

            return MustStopWithin + TimeSpan.FromSeconds(1);

        }

        #endregion

        #region (private static) PortOf(URL)

        private static UInt16 PortOf(String URL)

            => (UInt16) new Uri(URL).Port;

        #endregion

        #region (private static) Listening(Port)

        /// <summary>
        /// Whether anything on this machine listens on the given TCP port.
        /// </summary>
        /// <remarks>
        /// Asked of the operating system's table of listeners rather than tried
        /// with a connection: on Windows, a connection to a closed port on the
        /// loopback is refused only after some two seconds of retries.
        /// </remarks>
        private static Boolean Listening(UInt16 Port)

            => IPGlobalProperties.GetIPGlobalProperties().
                   GetActiveTcpListeners().
                   Any(listener => listener.Port == Port);

        #endregion


        #region (private class) AStationWhoseV2GLinkWillNotEnd

        /// <summary>
        /// A station as TestStations.New makes one, with a display and a local
        /// app server, whose link below the cable will not end.
        /// </summary>
        /// <remarks>
        /// Whether a link ends all of its own parts when one of them fails is
        /// what V2GLinkEndingTests asks. Here the step that ends it fails
        /// instead: what this station has to get right is what comes after
        /// that step, not the link.
        /// </remarks>
        private sealed class AStationWhoseV2GLinkWillNotEnd(String  Directory,
                                                            IPPort  LocalAppPort)

            : ChargingStation(DNSClient:       TestStations.Resolver(),
                              HTTPPort:        IPPort.Parse(TestPorts.Free()),
                              KioskPort:       IPPort.Parse(TestPorts.Free()),
                              LocalAppPort:    LocalAppPort,
                              AccountsPath:    Path.Combine(Directory, ChargingStation.DefaultAccountsPath),
                              ConfigFile:      new WWCPConfigFile(Path.Combine(Directory, "configuration.json")),
                              LogToConsole:    false,
                              BridgeDebugLog:  false)

        {

            /// <summary>
            /// What ending it says.
            /// </summary>
            public const String Why = "The V2G link would not end.";

            protected override Task EndTheV2GLink()

                => Task.FromException(new IOException(Why));

        }

        #endregion

    }

}
