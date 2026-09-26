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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What the station says about where each of its connections stands - to
    /// the Connections page, and in DialledConnections - follows the
    /// connection: connected, lost, not reached yet, turned away.
    /// </summary>
    /// <remarks>
    /// The page is where somebody looks to find out whether this station is on
    /// its back end, and a page that promises a connection nothing is trying to
    /// make any more is worse than no page: it is believed. So what is asked
    /// here is whether the station says where a connection stands - and what
    /// happens next.
    ///
    /// A plain WebSocket server stands in for the back end, as in
    /// <see cref="ReconnectTests"/> - one that lets anybody in - and the page
    /// is a signed-in HTTP client asking the route the page asks.
    /// </remarks>
    [TestFixture]
    public class ConnectionStateTests
    {

        #region Data

        /// <summary>
        /// How long a connection may take to come to where it is expected: the
        /// policy's first attempt is a second after a loss, give or take a
        /// fifth, and the second one two seconds after that.
        /// </summary>
        private static readonly TimeSpan  Within  = TimeSpan.FromSeconds(15);

        private const           String    States  = "/api/v1/status/connections";

        private String            directory  = "";
        private ChargingStation?  station;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("states");
            station    = TestStations.New(directory, TestStations.Offline);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            station = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region AConnectionThatGotThroughIsSaidToBeConnected()

        /// <summary>
        /// Connected, since when, and what was dialled - on the route the page
        /// asks while it is open, and in what the page loads first.
        /// </summary>
        [Test]
        public async Task AConnectionThatGotThroughIsSaidToBeConnected()
        {

            var port     = IPPort.Parse(TestStations.FreePort());
            var backEnd  = await Connected(port);

            try
            {

                using var http   = await SignedIn();

                var answer       = await GetJSON(http, States);
                var id           = station!.DialledConnections.Keys.Single();
                var state        = answer["states"]?[id] as JObject;

                Assert.That(state, Is.Not.Null, $"The station said nothing about the connection it dialled: {answer}");

                var loaded       = await GetJSON(http, "/api/v1/configuration/connections");

                Assert.Multiple(() => {

                    Assert.That(state!["status"]?.Value<String>(),       Is.EqualTo("connected"));
                    Assert.That(state ["url"]?.Value<String>(),          Is.EqualTo($"ws://127.0.0.1:{port}/cs001"));
                    Assert.That(state ["description"]?.Value<String>(),  Is.EqualTo("Restarts"));
                    Assert.That(state ["said"]?.Value<String>(),         Does.StartWith("Connected"));

                    Assert.That(DateTimeOffset.TryParse(state["since"]?.Value<String>(), out _),        Is.True,
                                "It did not say since when.");
                    Assert.That(DateTimeOffset.TryParse(answer["timestamp"]?.Value<String>(), out _),  Is.True,
                                "It did not say what time it is at the station, which the page counts from.");

                    Assert.That(loaded["states"]?[id]?["status"]?.Value<String>(), Is.EqualTo("connected"),
                                "What the page loads first does not say where the connection stands.");

                });

            }
            finally
            {
                await backEnd.Shutdown();
            }

        }

        #endregion

        #region ALostConnectionIsSaidToBeLostAndWhenItIsTriedAgain()

        /// <summary>
        /// Lost, with when the next attempt is - and connected again once it
        /// is back.
        /// </summary>
        [Test]
        public async Task ALostConnectionIsSaidToBeLostAndWhenItIsTriedAgain()
        {

            var port     = IPPort.Parse(TestStations.FreePort());
            var first    = await Connected(port);
            var id       = station!.DialledConnections.Keys.Single();

            using var http = await SignedIn();

            await first.Shutdown("Restarting.");

            var lost     = await StateOf(http, id, state => state["status"]?.Value<String>() == "lost" &&
                                                            state["nextAttemptAt"] is not null);

            Assert.Multiple(() => {
                Assert.That(lost["status"]?.Value<String>(),  Is.EqualTo("lost"));
                Assert.That(lost["said"]?.Value<String>(),    Does.Contain("Restarting.").And.Contain("comes back by itself"));
                Assert.That(lost["attempt"]?.Value<UInt32>(), Is.GreaterThanOrEqualTo(1),
                            "It did not say which attempt comes next.");
                Assert.That(lost["nextAttemptAt"]?.Value<DateTime>(), Is.GreaterThanOrEqualTo(lost["since"]!.Value<DateTime>()),
                            "It said the next attempt was before the connection was lost.");
            });

            var again    = new WebSocketServer(HTTPPort: port, RequireAuthentication: false, AutoStart: true);

            try
            {

                var back = await StateOf(http, id, state => state["status"]?.Value<String>() == "connected");

                Assert.Multiple(() => {
                    Assert.That(back["status"]?.Value<String>(),  Is.EqualTo("connected"),
                                "The connection came back, and the station did not say so.");
                    Assert.That(back["said"]?.Value<String>(),    Does.StartWith("Connected again"));
                    Assert.That(back["attempt"],                  Is.Null,
                                "A connection that is back still said when it would be tried next.");
                });

            }
            finally
            {
                await again.Shutdown();
            }

        }

        #endregion

        #region AConnectionRefusedOnItsWayBackIsNotPromisedAnyMore()

        /// <summary>
        /// A back end that comes back as one that will not have this station -
        /// its passwords changed, the station removed from it - ends the
        /// trying, and the station says so.
        /// </summary>
        /// <remarks>
        /// The station went on saying "it comes back by itself" of a connection
        /// nothing was trying to make any more.
        /// </remarks>
        [Test]
        public async Task AConnectionRefusedOnItsWayBackIsNotPromisedAnyMore()
        {

            var port     = IPPort.Parse(TestStations.FreePort());
            var first    = await Connected(port);
            var id       = station!.DialledConnections.Keys.Single();

            await first.Shutdown("Restarting.");

            var otherOne = new HTTPServer(TCPPort: port);

            otherOne.AddHTTPAPI().AddHandler(
                HTTPMethod.GET,
                HTTPPath.Parse("/cs001"),
                HTTPDelegate: request => Task.FromResult(new HTTPResponse.Builder(request) {
                                                             HTTPStatusCode  = HTTPStatusCode.NotFound,
                                                             Connection      = ConnectionType.Close
                                                         }.AsImmutable)
            );

            await otherOne.Start();

            try
            {

                var said = await Said(id, what => what.Contains("not tried again"), Within);

                Assert.That(said, Does.Contain("404").And.Contain("not tried again"),
                            "A connection whose back end came back and turned it away was still said to come back by itself.");

                using var http = await SignedIn();

                Assert.That((await GetJSON(http, States))["states"]?[id]?["status"]?.Value<String>(), Is.EqualTo("refused"));

            }
            finally
            {
                await otherOne.Stop();
            }

        }

        #endregion

        #region AConnectionToNothingCouldNotBeReachedAndIsTriedAgain()

        /// <summary>
        /// Nothing there when the station starts: not reached, tried again,
        /// and when.
        /// </summary>
        /// <remarks>
        /// What the station can tell apart now: an attempt that never got to
        /// ask anything is not an answer, and "did not become a WebSocket
        /// connection: BadRequest" sent people looking for a server that had
        /// said 400 and did not exist.
        /// </remarks>
        [Test]
        public async Task AConnectionToNothingCouldNotBeReachedAndIsTriedAgain()
        {

            Assert.That(station!.Connections.TryAddConnection(
                            "Not up yet",
                            $"ws://127.0.0.1:{TestStations.FreePort()}/cs001",
                            "CSMS",
                            true, null, null, out var id, out var error),
                        Is.True, error);

            await station.Start();

            using var http = await SignedIn();

            var trying = await StateOf(http, id!, state => state["nextAttemptAt"] is not null);

            Assert.Multiple(() => {
                Assert.That(trying["status"]?.Value<String>(),  Is.EqualTo("trying"));
                Assert.That(trying["said"]?.Value<String>(),    Does.Contain("could not be reached").And.Contain("tried again by itself"));
                Assert.That(trying["attempt"]?.Value<UInt32>(), Is.GreaterThanOrEqualTo(1));
            });

        }

        #endregion

        #region AStartDoesNotWaitForABackEndThatNeverAnswers()

        /// <summary>
        /// A back end that takes the connection and never answers - a hung
        /// process, a load balancer with nothing behind it - neither holds up
        /// the start nor keeps the station from trying again.
        /// </summary>
        /// <remarks>
        /// The deadline for the answer to the upgrade was looked at only after
        /// a byte had arrived, so an attempt nobody answered never ended: the
        /// start waited for the client's request timeout, ten minutes, and the
        /// connection was never tried again.
        /// </remarks>
        [Test]
        public async Task AStartDoesNotWaitForABackEndThatNeverAnswers()
        {

            var listener  = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            var taken     = new List<System.Net.Sockets.Socket>();

            listener.Start();

            _ = Task.Run(async () => {
                while (true)
                {
                    try
                    {
                        var socket = await listener.AcceptSocketAsync();
                        lock (taken)
                            taken.Add(socket);
                    }
                    catch
                    {
                        return;
                    }
                }
            });

            try
            {

                Assert.That(station!.Connections.TryAddConnection(
                                "Says nothing",
                                $"ws://127.0.0.1:{((IPEndPoint) listener.LocalEndpoint).Port}/cs001",
                                "CSMS",
                                true, null, null, out var id, out var error),
                            Is.True, error);

                var took     = System.Diagnostics.Stopwatch.StartNew();
                var starting = station.Start();

                Assert.That(await Task.WhenAny(starting, Task.Delay(TimeSpan.FromSeconds(30))), Is.SameAs(starting),
                            "The station had not started 30 s after it was told to, waiting for a back end that never answers.");

                took.Stop();

                Assert.Multiple(() => {
                    Assert.That(took.Elapsed, Is.LessThan(TimeSpan.FromSeconds(15)));
                    Assert.That(station.DialledConnections[id!], Does.Contain("tried again by itself"));
                });

                var giveUp = DateTimeOffset.UtcNow + Within;

                while (DateTimeOffset.UtcNow < giveUp)
                {
                    lock (taken)
                        if (taken.Count >= 2)
                            break;
                    await Task.Delay(100);
                }

                lock (taken)
                    Assert.That(taken.Count, Is.GreaterThanOrEqualTo(2),
                                "The station never tried again after an attempt nobody answered.");

            }
            finally
            {
                listener.Stop();
                lock (taken)
                    foreach (var socket in taken)
                        socket.Close();
            }

        }

        #endregion

        #region OnlySomebodyWhoMayReadTheConfigurationIsTold()

        /// <summary>
        /// Where the connections go and what their back ends answered is the
        /// configuration's to tell, and nobody signed out is told.
        /// </summary>
        [Test]
        public async Task OnlySomebodyWhoMayReadTheConfigurationIsTold()
        {

            await station!.Start();

            using var http = Anonymous();

            Assert.That((await http.GetAsync(States)).StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion


        #region (private) Connected(Port)

        /// <summary>
        /// A back end on the given port, and the station connected to it.
        /// </summary>
        private async Task<WebSocketServer> Connected(IPPort Port)
        {

            var backEnd = new WebSocketServer(HTTPPort: Port, RequireAuthentication: false, AutoStart: true);

            Assert.That(station!.Connections.TryAddConnection(
                            "Restarts",
                            $"ws://127.0.0.1:{Port}/cs001",
                            "CSMS",
                            true, null, null, out var connection, out var error),
                        Is.True, error);

            await station.Start();

            Assert.That(station.DialledConnections[connection!], Does.StartWith("Connected"),
                        "The station did not get connected to begin with.");

            return backEnd;

        }

        #endregion

        #region (private) Anonymous() / SignedIn()

        private HttpClient Anonymous()

            => new (new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                   BaseAddress = new Uri(station!.WebInterfaceURL.ToString())
               };

        /// <summary>
        /// A browser signed in as the station's first account, with the
        /// password it made up for itself.
        /// </summary>
        private async Task<HttpClient> SignedIn()
        {

            var http      = Anonymous();

            var response  = await http.PostAsync(
                                $"{ChargingStation.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                new FormUrlEncodedContent([
                                    new KeyValuePair<String, String>("login",     ChargingStation.DefaultAdminUser),
                                    new KeyValuePair<String, String>("password",  station!.GeneratedPassword!)
                                ]));

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"Signing in failed with {(Int32) response.StatusCode}, and every assertion below it would say so instead.");

            return http;

        }

        #endregion

        #region (private static) GetJSON(HTTP, Path)

        private static async Task<JObject> GetJSON(HttpClient  HTTP,
                                                   String      Path)
        {

            var response = await HTTP.GetAsync(Path);

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"GET {Path} answered {(Int32) response.StatusCode}.");

            // The timestamps as the page gets them, as text: read as dates,
            // they would come back out in whatever shape this machine's
            // culture writes a date in.
            using var reader = new JsonTextReader(new StringReader(await response.Content.ReadAsStringAsync())) {
                                   DateParseHandling = DateParseHandling.None
                               };

            return JObject.Load(reader);

        }

        #endregion

        #region (private) StateOf(HTTP, Id, Enough)

        /// <summary>
        /// Where the station says the connection stands, once it says what is
        /// expected or the time is up - asked the way the page asks.
        /// </summary>
        private static async Task<JObject> StateOf(HttpClient             HTTP,
                                                   String                 Id,
                                                   Func<JObject, Boolean> Enough)
        {

            var giveUp  = DateTimeOffset.UtcNow + Within;
            var state   = (await GetJSON(HTTP, States))["states"]?[Id] as JObject ?? [];

            while (DateTimeOffset.UtcNow < giveUp && !Enough(state))
            {
                await Task.Delay(100);
                state = (await GetJSON(HTTP, States))["states"]?[Id] as JObject ?? [];
            }

            return state;

        }

        #endregion

        #region (private) Said(Id, Enough, Within = 5 s)

        /// <summary>
        /// What DialledConnections says of the connection, once it says what
        /// is expected or the time is up.
        /// </summary>
        private async Task<String> Said(String                  Id,
                                        Func<String, Boolean>   Enough,
                                        TimeSpan?               Within  = null)
        {

            var giveUp = DateTimeOffset.UtcNow + (Within ?? TimeSpan.FromSeconds(5));
            var what   = station!.DialledConnections[Id];

            while (DateTimeOffset.UtcNow < giveUp && !Enough(what))
            {
                await Task.Delay(50);
                what = station.DialledConnections[Id];
            }

            return what;

        }

        #endregion

    }

}
