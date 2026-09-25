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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A connection this station was told to keep comes back when its back
    /// end goes away and comes back.
    /// </summary>
    /// <remarks>
    /// A back end that restarts - a CSMS being deployed, a local controller
    /// rebooted - ends every connection to it, and a station that stays
    /// disconnected afterwards is a station that is off the network until
    /// somebody restarts it too. The station gives every connection it was
    /// told to keep a reconnect policy for exactly that; what is asked here is
    /// whether the policy is ever used.
    ///
    /// Two ways for a back end to go: shut down, which tells every client with
    /// a close frame, and stopped, which closes the sockets and says nothing.
    /// A plain WebSocket server stands in for the back end, on the same port
    /// before and after.
    /// </remarks>
    [TestFixture]
    public class ReconnectTests
    {

        #region Data

        /// <summary>
        /// How long a station may take to come back. The policy's first attempt
        /// is a second after the loss, give or take a fifth.
        /// </summary>
        private static readonly TimeSpan  ComesBackWithin = TimeSpan.FromSeconds(15);

        private String            directory  = "";
        private ChargingStation?  station;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("reconnect");
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


        #region AConnectionComesBackWhenItsBackEndIsShutDownAndStartedAgain()

        [Test]
        public async Task AConnectionComesBackWhenItsBackEndIsShutDownAndStartedAgain()
        {

            var port   = IPPort.Parse(TestStations.FreePort());
            var first  = await Connected(port);

            await first.Shutdown("Restarting.");

            Assert.That(await ComesBack(port), Is.True,
                        $"The back end was shut down and started again, and the station did not come back within {ComesBackWithin.TotalSeconds:F0} s.");

        }

        #endregion

        #region AConnectionComesBackWhenItsBackEndIsStoppedAndStartedAgain()

        [Test]
        public async Task AConnectionComesBackWhenItsBackEndIsStoppedAndStartedAgain()
        {

            var port   = IPPort.Parse(TestStations.FreePort());
            var first  = await Connected(port);

            await first.Stop();

            Assert.That(await ComesBack(port), Is.True,
                        $"The back end was stopped and started again, and the station did not come back within {ComesBackWithin.TotalSeconds:F0} s.");

        }

        #endregion

        #region AConnectionComesBackThroughABackEndThatIsStillStarting()

        /// <summary>
        /// A back end behind a reverse proxy answers the first attempts with
        /// 503 while it is still starting; the station keeps trying through
        /// them.
        /// </summary>
        [Test]
        public async Task AConnectionComesBackThroughABackEndThatIsStillStarting()
        {

            var port     = IPPort.Parse(TestStations.FreePort());
            var first    = await Connected(port);
            var refused  = 0;

            await first.Stop();

            var lent     = new WebSocketServer(AutoStart: false);
            var proxy    = new HTTPServer(TCPPort: port);
            var upgrade  = WebSocketUpgrade.For(lent);

            proxy.AddHTTPAPI().AddHandler(
                HTTPMethod.GET,
                HTTPPath.Parse("/cs001"),
                HTTPDelegate: request => Interlocked.Increment(ref refused) <= 2
                                             ? Task.FromResult(new HTTPResponse.Builder(request) {
                                                                   HTTPStatusCode  = HTTPStatusCode.ServiceUnavailable,
                                                                   Connection      = ConnectionType.Close
                                                               }.AsImmutable)
                                             : upgrade(request)
            );

            await proxy.Start();

            try
            {

                var giveUp = DateTimeOffset.UtcNow + ComesBackWithin;

                while (DateTimeOffset.UtcNow < giveUp && !lent.WebSocketConnections.Any())
                    await Task.Delay(100);

                Assert.Multiple(() => {
                    Assert.That(lent.WebSocketConnections.Any(), Is.True,
                                $"The station was answered 503 on its way back and did not come back within {ComesBackWithin.TotalSeconds:F0} s.");
                    Assert.That(refused, Is.GreaterThanOrEqualTo(3),
                                "The station came back without being refused first, so this test tested nothing.");
                });

            }
            finally
            {
                await lent.Shutdown();
                await proxy.Stop();
            }

        }

        #endregion

        #region WhatIsSaidOfAConnectionFollowsIt()

        /// <summary>
        /// A connection that is lost is said to be lost - in the log, and in
        /// what DialledConnections tells somebody who arrives later - and one
        /// that comes back is said to be connected again.
        /// </summary>
        /// <remarks>
        /// The station used to write "Connected, and will come back by itself
        /// if it drops" once, when it dialled, and then nothing: a connection
        /// gone for an hour was still "Connected".
        /// </remarks>
        [Test]
        public async Task WhatIsSaidOfAConnectionFollowsIt()
        {

            var port   = IPPort.Parse(TestStations.FreePort());
            var first  = await Connected(port);
            var id     = station!.DialledConnections.Keys.Single();

            await first.Shutdown("Restarting.");

            var lost = await Said(id, what => what.Contains("was lost"));

            Assert.That(lost, Does.Contain("1001").And.Contain("Restarting."),
                        "What the station says of the connection did not change when it was lost.");

            // Started here rather than through ComesBack, which shuts its server
            // down again as soon as the station is back - and a station that is
            // asked afterwards has lost the connection a second time.
            var backAgain = new WebSocketServer(HTTPPort: port, AutoStart: true);

            try
            {

                var giveUp = DateTimeOffset.UtcNow + ComesBackWithin;

                while (DateTimeOffset.UtcNow < giveUp && !backAgain.WebSocketConnections.Any())
                    await Task.Delay(100);

                Assert.That(backAgain.WebSocketConnections.Any(), Is.True, "The station did not come back.");

                var again   = await Said(id, what => what.StartsWith("Connected again"));
                var logged  = station.Log.Recent(500).Select(entry => entry.Message).ToArray();

                Assert.Multiple(() => {
                    Assert.That(again,  Does.StartWith("Connected again"),
                                "What the station says of the connection did not change when it came back.");
                    Assert.That(logged, Has.Some.Contains("was lost (1001 GoingAway: Restarting.)"));
                    Assert.That(logged, Has.Some.Contains("Connected again"));
                });

            }
            finally
            {
                await backAgain.Shutdown();
            }

        }

        #endregion


        #region (private) Connected(Port)

        /// <summary>
        /// A back end on the given port, and the station connected to it.
        /// </summary>
        private async Task<WebSocketServer> Connected(IPPort Port)
        {

            var backEnd = new WebSocketServer(HTTPPort: Port, AutoStart: true);

            Assert.That(station!.Connections.TryAddConnection(
                            "Restarts",
                            $"ws://127.0.0.1:{Port}/cs001",
                            "CSMS",
                            true, null, null, out var connection, out var error),
                        Is.True, error);

            await station.Start();

            Assert.That(station.DialledConnections[connection!], Does.Contain("Connected"),
                        "The station did not get connected to begin with.");

            return backEnd;

        }

        #endregion

        #region (private) Said(Id, Enough)

        /// <summary>
        /// What DialledConnections says of the connection, once it says what
        /// is expected or five seconds have passed.
        /// </summary>
        private async Task<String> Said(String Id, Func<String, Boolean> Enough)
        {

            var giveUp = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            var what   = station!.DialledConnections[Id];

            while (DateTimeOffset.UtcNow < giveUp && !Enough(what))
            {
                await Task.Delay(50);
                what = station.DialledConnections[Id];
            }

            return what;

        }

        #endregion

        #region (private) ComesBack(Port)

        /// <summary>
        /// Whether the station connects to a back end started again on the
        /// port, within the time it has.
        /// </summary>
        private static async Task<Boolean> ComesBack(IPPort Port)
        {

            var again = new WebSocketServer(HTTPPort: Port, AutoStart: true);

            try
            {

                var giveUp = DateTimeOffset.UtcNow + ComesBackWithin;

                while (DateTimeOffset.UtcNow < giveUp)
                {

                    if (again.WebSocketConnections.Any())
                        return true;

                    await Task.Delay(100);

                }

                return false;

            }
            finally
            {
                await again.Shutdown();
            }

        }

        #endregion

    }

}
