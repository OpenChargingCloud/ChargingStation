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
using System.Text;
using System.Diagnostics;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.WWCP.Node.Connections;

using CSMS = cloud.charging.open.ChargingStation.Tests.TalkingToTheCSMSTests.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A back end with more than one endpoint is connected to at one of them at a
    /// time, and at the next one when that cannot be reached - work package P8
    /// of EVQI Transport.
    /// </summary>
    /// <remarks>
    /// The endpoints of a peer are alternatives, tried in order (section 11.4),
    /// and the next one is connected to after NetworkProfileConnectionAttempts
    /// attempts at one failed (OCPP 2.1, B09), or at once where the answer was
    /// final. A peer of one endpoint goes on trying it, as before.
    /// </remarks>
    [TestFixture]
    public class FailoverTests
    {

        #region Data

        private static TimeSpan   Patience
            => TalkingToTheCSMSTests.Patience;

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             first;
        private CSMS?             second;
        private TcpListener?      refusing;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory = TestStations.TemporaryDirectory("failover");
            station   = TestStations.New(directory, TestStations.Offline);
            station.FailoverPause = TimeSpan.FromSeconds(1);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (first is not null)
                await first.DisposeAsync();

            if (second is not null)
                await second.DisposeAsync();

            refusing?.Dispose();

            station   = null;
            first     = null;
            second    = null;
            refusing  = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region TheSecondEndpointIsConnectedAfterTheAttemptsAtTheFirstFailed()

        /// <summary>
        /// A first endpoint nothing listens at is tried as often as the station
        /// is told to, and then the second one is connected to - not before, and not
        /// beside it: the first one waits in reserve.
        /// </summary>
        [Test]
        public async Task TheSecondEndpointIsConnectedAfterTheAttemptsAtTheFirstFailed()
        {

            second = new CSMS();

            var (mainId, spareId) = OnePeerOfTwo(TestPorts.Free(), second.Port);

            Assert.That(station!.NetworkProfileConnectionAttempts, Is.EqualTo(3), "OCPP's default");

            var started = station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => Status(mainId) == ConnectionStatus.Trying), Is.True, $"the first endpoint was not tried: '{Said(mainId)}'");
            Assert.That(Status(spareId), Is.EqualTo(ConnectionStatus.Standby), $"the second endpoint does not wait in reserve: '{Said(spareId)}'");
            Assert.That(second.Accepted, Is.EqualTo(0), "the second endpoint was connected to beside the first one");

            Assert.That(await Within(() => second.Connections == 1 && Said(spareId).StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the second endpoint was not connected to: '{Said(spareId)}', '{Said(mainId)}'");

            Assert.That(Status(mainId), Is.EqualTo(ConnectionStatus.Standby));
            Assert.That(Said(mainId),   Does.Contain("3 attempt(s) failed"));

            // It is the back end in charge: registered with, as the CSMS.
            Assert.That(await Within(() => started.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        "the station did not register with the second endpoint of its CSMS");

        }

        #endregion

        #region AFinalAnswerGoesOnToTheNextEndpointAtOnce()

        /// <summary>
        /// A first endpoint whose answer means no is not tried again: the second
        /// one is connected to at once, however many attempts were allowed.
        /// </summary>
        [Test]
        public async Task AFinalAnswerGoesOnToTheNextEndpointAtOnce()
        {

            second   = new CSMS();
            refusing = ARefusingServer();

            var (mainId, spareId) = OnePeerOfTwo(IPPort.Parse(((IPEndPoint) refusing.LocalEndpoint).Port.ToString()), second.Port);

            station!.NetworkProfileConnectionAttempts = 100;

            station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => second.Connections == 1 && Said(spareId).StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the second endpoint was not connected to: '{Said(spareId)}', '{Said(mainId)}'");

            Assert.That(Status(mainId), Is.EqualTo(ConnectionStatus.Standby));
            Assert.That(Said(mainId),   Does.Contain("it cannot be tried again"));

        }

        #endregion

        #region ARoundThatReachedNoneStartsAgainAfterAPause()

        /// <summary>
        /// Where neither endpoint can be reached, the station goes round them,
        /// waits, and starts at the first again - which is where it gets in once
        /// that one is there.
        /// </summary>
        [Test]
        public async Task ARoundThatReachedNoneStartsAgainAfterAPause()
        {

            var mainPort          = TestPorts.Free();
            var (mainId, spareId) = OnePeerOfTwo(mainPort, TestPorts.Free());

            station!.NetworkProfileConnectionAttempts = 1;

            station = await TestStations.Booted(station, directory);

            // Round to the second endpoint, tried once, and back in reserve.
            Assert.That(await Within(() => Status(spareId) is ConnectionStatus.Standby &&
                                           Said(spareId).Contains("1 attempt(s) failed", StringComparison.Ordinal)), Is.True,
                        $"the second endpoint was not tried and left: '{Said(spareId)}', '{Said(mainId)}'");

            first = new CSMS(mainPort);

            Assert.That(await Within(() => first.Connections == 1 && Said(mainId).StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the first endpoint was not connected to again: '{Said(mainId)}', '{Said(spareId)}'");

            Assert.That(Status(spareId), Is.EqualTo(ConnectionStatus.Standby));

        }

        #endregion

        #region RoundAfterRoundTheNodeKeepsTheClientConnectedOnly()

        /// <summary>
        /// Where neither endpoint can be reached, round after round, the OCPP
        /// node keeps the client of the endpoint connected to and no other: every
        /// one the station went on from it let go of.
        /// </summary>
        /// <remarks>
        /// The node kept every client it made, and a station that went round
        /// its endpoints for as long as its back end was away gathered one for
        /// every endpoint it connected to - two a round here, one more per second.
        /// </remarks>
        [Test]
        public async Task RoundAfterRoundTheNodeKeepsTheClientConnectedOnly()
        {

            var (mainId, spareId) = OnePeerOfTwo(TestPorts.Free(), TestPorts.Free());

            station!.NetworkProfileConnectionAttempts = 1;

            station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => Status(spareId) is ConnectionStatus.Standby &&
                                           Said(spareId).Contains("1 attempt(s) failed", StringComparison.Ordinal)), Is.True,
                        $"the second endpoint was not tried and left: '{Said(spareId)}', '{Said(mainId)}'");

            // Two more rounds, at the least: a pause of a second each.
            var most   = 0;
            var watch  = Stopwatch.StartNew();

            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                most = Math.Max(most, station.OCPPv2_1Node.WWCPWebSocketClients.Count());
                await Task.Delay(20);
            }

            Assert.Multiple(() => {
                Assert.That(station.OCPPv2_1Node.WWCPWebSocketClients.Count(), Is.LessThanOrEqualTo(1),
                            "The node kept clients of endpoints the station had gone on from.");
                Assert.That(most,                                               Is.LessThanOrEqualTo(1),
                            "The node held more than the client of the endpoint connected to at one time.");
            });

        }

        #endregion

        #region APeerOfOneEndpointGoesOnTryingIt()

        /// <summary>
        /// A back end of one endpoint has nothing to go on to: it is tried again
        /// by itself, beyond the attempts that would have ended an endpoint.
        /// </summary>
        [Test]
        public async Task APeerOfOneEndpointGoesOnTryingIt()
        {

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{TestPorts.Free()}/cs001"), "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            station.NetworkProfileConnectionAttempts = 1;

            station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => station.ConnectionStates.TryGetValue(id!, out var state) &&
                                           state.Status == ConnectionStatus.Trying &&
                                           state.Attempt >= 3),
                        Is.True, $"a peer of one endpoint was not tried again: '{Said(id!)}'");

        }

        #endregion

        #region AnEndpointAddedToAPeerThatIsUpWaitsInReserve()

        /// <summary>
        /// An endpoint added to a back end the station is on waits in reserve, and
        /// the connection that is up stays up.
        /// </summary>
        [Test]
        public async Task AnEndpointAddedToAPeerThatIsUpWaitsInReserve()
        {

            first   = new CSMS();
            second  = new CSMS();

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{first.Port}/cs001"), "CSMS", true, null, null,
                                                              out var mainId, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => Said(mainId!).StartsWith("Connected", StringComparison.Ordinal)), Is.True, Said(mainId!));

            Assert.That(station.Connections.TryAddConnection(I18NString.Create("Its spare"), URL.Parse($"ws://127.0.0.1:{second.Port}/cs001"), "CSMS", true, null, null,
                                                             out var spareId, out error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(station.Connections.TryMoveEndpoint(spareId, station.Connections.PeerOf(mainId!)!.Id, null, out error), Is.True, error);

            Assert.That(await Within(() => Status(spareId!) == ConnectionStatus.Standby && second.Connections == 0), Is.True,
                        $"the endpoint added does not wait in reserve: '{Said(spareId!)}', {second.Connections} connection(s)");

            Assert.That(first.Accepted, Is.EqualTo(1), "the connection that was up was connected to again");

        }

        #endregion

        #region AnAnswerTakenAfterAnEndpointWentIntoReserveLeavesItThere()

        /// <summary>
        /// An endpoint that goes into reserve while the answer to its
        /// BootNotification is being taken stays in reserve: the answer does
        /// not say afterwards that it is connected.
        /// </summary>
        /// <remarks>
        /// What happened by chance in a run of all tests: the answer was read
        /// before the endpoint was hung up and taken after, and the endpoint
        /// said "Connected" with nothing connected. Here the station holds the
        /// answer until the endpoint is in reserve.
        /// </remarks>
        [Test]
        public async Task AnAnswerTakenAfterAnEndpointWentIntoReserveLeavesItThere()
        {

            first   = new CSMS();
            second  = new CSMS();

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{first.Port}/cs001"), "CSMS", true, null, null,
                                                              out var mainId, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            station = await TestStations.Booted(station, directory);

            Assert.That(await Within(() => Said(mainId!).StartsWith("Connected", StringComparison.Ordinal)), Is.True, Said(mainId!));

            var held     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            station.WhenABootNotificationIsAnswered = async connection => {
                if (connection.Description.FirstText() == "Its spare")
                {
                    held.TrySetResult();
                    await release.Task;
                }
            };

            try
            {

                Assert.That(station.Connections.TryAddConnection(I18NString.Create("Its spare"), URL.Parse($"ws://127.0.0.1:{second.Port}/cs001"), "CSMS", true, null, null,
                                                                 out var spareId, out error, OCPPVersion: "OCPP2.1"),
                            Is.True, error);

                Assert.That(await Within(() => held.Task.IsCompleted), Is.True,
                            $"the spare's BootNotification was not answered: '{Said(spareId!)}'");

                Assert.That(station.Connections.TryMoveEndpoint(spareId, station.Connections.PeerOf(mainId!)!.Id, null, out error), Is.True, error);

                Assert.That(await Within(() => Status(spareId!) == ConnectionStatus.Standby && second.Connections == 0), Is.True,
                            $"the endpoint added does not wait in reserve: '{Said(spareId!)}', {second.Connections} connection(s)");

                // The answer is taken now - after the endpoint went into reserve.
                release.TrySetResult();

                var watch = Stopwatch.StartNew();

                while (watch.Elapsed < TimeSpan.FromSeconds(1))
                {

                    Assert.That(Status(spareId!), Is.EqualTo(ConnectionStatus.Standby),
                                $"an answer taken after the endpoint went into reserve says how it stands: '{Said(spareId!)}'");

                    await Task.Delay(20);

                }

                Assert.That(first.Accepted, Is.EqualTo(1), "the connection that was up was connected to again");

            }
            finally
            {
                release.TrySetResult();
            }

        }

        #endregion


        #region (private) OnePeerOfTwo(MainPort, SparePort)

        /// <summary>
        /// One CSMS of two endpoints, over OCPP 2.1: the first at the one port,
        /// the second at the other.
        /// </summary>
        private (String Main, String Spare) OnePeerOfTwo(IPPort MainPort, IPPort SparePort)
        {

            var store = station!.Connections;

            Assert.That(store.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{MainPort}/cs001"), "CSMS", true, null, null,
                                               out var mainId, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(store.TryAddConnection(I18NString.Create("Its spare"), URL.Parse($"ws://127.0.0.1:{SparePort}/cs001"), "CSMS", true, null, null,
                                               out var spareId, out error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(store.TryMoveEndpoint(spareId, store.PeerOf(mainId!)!.Id, null, out error), Is.True, error);

            Assert.That(store.PeerOf(mainId!)!.EndpointIds, Is.EqualTo(new[] { mainId, spareId }));

            return (mainId!, spareId!);

        }

        #endregion

        #region (private) ARefusingServer()

        /// <summary>
        /// An HTTP server that answers every request with 404: a back end that
        /// does not know this station, which is an answer and not tried again.
        /// </summary>
        private static TcpListener ARefusingServer()
        {

            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();

            _ = Task.Run(async () => {
                while (true)
                {

                    TcpClient client;

                    try
                    {
                        client = await listener.AcceptTcpClientAsync();
                    }
                    catch
                    {
                        return;
                    }

                    _ = Task.Run(async () => {
                        using (client)
                        {
                            try
                            {

                                var stream  = client.GetStream();
                                var buffer  = new Byte[8192];
                                var read    = new StringBuilder();

                                while (!read.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                                {
                                    var n = await stream.ReadAsync(buffer);
                                    if (n == 0)
                                        return;
                                    read.Append(Encoding.ASCII.GetString(buffer, 0, n));
                                }

                                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                                await stream.FlushAsync();

                            }
                            catch
                            { }
                        }
                    });

                }
            });

            return listener;

        }

        #endregion

        #region (private) Status(Id) / Said(Id)

        private ConnectionStatus? Status(String Id)

            => station!.ConnectionStates.TryGetValue(Id, out var state)
                   ? state.Status
                   : null;

        private String Said(String Id)

            => station!.OutboundConnections.TryGetValue(Id, out var said)
                   ? said
                   : "(nothing)";

        #endregion

        #region (private static) Within(Condition)

        private static async Task<Boolean> Within(Func<Boolean> Condition)
        {

            var watch = Stopwatch.StartNew();

            while (!Condition())
            {

                if (watch.Elapsed > Patience)
                    return false;

                await Task.Delay(20);

            }

            return true;

        }

        #endregion

    }

}
