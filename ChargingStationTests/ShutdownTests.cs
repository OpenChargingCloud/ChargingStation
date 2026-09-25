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

using System.Diagnostics;
using System.Net.WebSockets;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

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


        #region StopsWithNobodyWatching()

        [Test]
        public async Task StopsWithNobodyWatching()
        {

            var (station, _) = await StartOne();

            var elapsed = await TimeTheStop(station);

            Assert.That(elapsed, Is.LessThan(MustStopWithin),
                        $"An idle station took {elapsed.TotalSeconds:F1} s to stop.");

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

        #region StopsWithABrowserOnTheLogsPage()

        /// <summary>
        /// The regression test. One open event stream, which is what a browser
        /// showing the Logs page is, and then a stop.
        /// </summary>
        [Test]
        public async Task StopsWithABrowserOnTheLogsPage()
        {

            var (station, http) = await StartOne();

            using (http)
            {

                // Settled, not merely opened: a handler that is still writing
                // is ended by its socket closing, and this test would then pass
                // against a station that cannot shut down at all.
                using var stream = await EventStream.OpenAndSettle(station, http);

                // And now left alone, which is what a browser sitting on the
                // Logs page is when the station is told to stop.
                var elapsed = await TimeTheStop(station);

                Assert.That(elapsed, Is.LessThan(MustStopWithin),
                            $"A station with one open event stream took {elapsed.TotalSeconds:F1} s to stop. " +
                            "The streams are not being ended before the servers are.");

            }

        }

        #endregion

        #region StopsWithSeveralBrowsersOnTheLogsPage()

        /// <summary>
        /// Several of them, because ending the streams has to end all of them -
        /// one cancellation token that only the first stream observed would
        /// pass the test above and hang a real station.
        /// </summary>
        [Test]
        public async Task StopsWithSeveralBrowsersOnTheLogsPage()
        {

            var (station, first) = await StartOne();

            var browsers = new List<HttpClient> { first };
            var streams  = new List<EventStream>();

            try
            {

                streams.Add(await EventStream.OpenAndSettle(station, first));

                for (var i = 0; i < 3; i++)
                {
                    var another = await SignIn(station);
                    browsers.Add(another);
                    streams.Add(await EventStream.OpenAndSettle(station, another));
                }

                var elapsed = await TimeTheStop(station);

                Assert.That(elapsed, Is.LessThan(MustStopWithin),
                            $"A station with {streams.Count} open event streams took {elapsed.TotalSeconds:F1} s to stop.");

            }
            finally
            {
                foreach (var stream  in streams)   stream. Dispose();
                foreach (var browser in browsers)  browser.Dispose();
            }

        }

        #endregion

        #region StoppingTwiceIsHarmless()

        /// <summary>
        /// DisposeAsync stops as well, and a station inside a using block that
        /// was also stopped by hand is an ordinary thing to write.
        /// </summary>
        [Test]
        public async Task StoppingTwiceIsHarmless()
        {

            var (station, http) = await StartOne();

            http.Dispose();

            await station.Stop();

            Assert.DoesNotThrowAsync(async () => {
                await station.Stop();
                await station.DisposeAsync();
            });

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

            var station = TestStations.New(directory, TestStations.Offline,
                                           LocalAppPort: IPPort.Parse(TestStations.FreePort()));

            await station.Start();

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
                Assert.That((await told).MessageType, Is.EqualTo(WebSocketMessageType.Close),
                            "The app was sent something other than goodbye.");
            }
            catch (WebSocketException broken)
            {
                Assert.Fail($"The app found out from a broken connection rather than a close frame: {broken.Message}");
            }

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
            var station = TestStations.New(directory, TestStations.Offline, WithDisplay);

            await station.Start();

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

    }

}
