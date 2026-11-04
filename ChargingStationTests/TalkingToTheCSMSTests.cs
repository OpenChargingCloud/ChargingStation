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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether a CSMS this station dialled can talk to it, and it to the CSMS.
    /// </summary>
    /// <remarks>
    /// The station dialled its connections without telling its OCPP nodes who
    /// was at the other end. A node sends what it has to say to "CSMS" and
    /// answers a request by the name of the connection it came over, so a
    /// station connected to its CSMS answered none of the CSMS's requests and
    /// sent none of its security events or the events of its monitors: they
    /// waited in the queue. Every test that dialled asked only whether the
    /// connection was made.
    ///
    /// A plain WebSocket server stands in for the CSMS, one that answers every
    /// request with an empty answer and keeps every frame it is sent.
    /// </remarks>
    [TestFixture]
    public class TalkingToTheCSMSTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind: it takes every request, answers it with
        /// nothing, and keeps what it was sent.
        /// </summary>
        internal sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray> received = new();

            public WebSocketServer  Server   { get; }
            public IPPort           Port     { get; }

            public CSMS()
            {

                Port    = IPPort.Parse(TestPorts.Free());
                Server  = new WebSocketServer(HTTPPort: Port, RequireAuthentication: false, AutoStart: true);

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    received.Enqueue(message);

                    if (message[0].Value<Int32>() == 2)
                        await Server.SendTextMessage(connection, new JArray(3, message[1], new JObject()).ToString(Formatting.None));

                };

            }

            /// <summary>
            /// How many connections it has.
            /// </summary>
            public Int32 Connections
                => Server.WebSocketConnections.Count();

            /// <summary>
            /// End the one connection there is, as a CSMS going away would.
            /// </summary>
            public Task Drop()
                => Server.WebSocketConnections.Single().Close(WebSocketFrame.ClosingStatusCode.GoingAway, "Going away for a moment.");

            /// <summary>
            /// Send the station a request, as the one connection there is.
            /// </summary>
            public Task Ask(String Request)
                => Server.SendTextMessage(Server.WebSocketConnections.Single(), Request);

            /// <summary>
            /// The first frame it was sent that is what is looked for, once
            /// there is one or ten seconds have passed.
            /// </summary>
            public async Task<JArray?> Sent(Func<JArray, Boolean> Wanted)
            {

                var watch = Stopwatch.StartNew();

                while (watch.Elapsed < TimeSpan.FromSeconds(10))
                {

                    var found = received.FirstOrDefault(Wanted);

                    if (found is not null)
                        return found;

                    await Task.Delay(20);

                }

                return null;

            }

            /// <summary>
            /// The request of the given action it was sent whose payload says
            /// the given type, if any.
            /// </summary>
            public Task<JArray?> SentRequest(String Action, String Type)
                => Sent(message => message[0].Value<Int32>() == 2 &&
                                   message[2].Value<String>() == Action &&
                                   message[3]?["type"]?.Value<String>() == Type);

            public async ValueTask DisposeAsync()
                => await Server.Shutdown();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("csms");
            station    = TestStations.New(directory, TestStations.Offline);
            csms       = new CSMS();
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            station  = null;
            csms     = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region (private) Connected(Version)

        /// <summary>
        /// The station started, connected to this test's CSMS with the given
        /// version of OCPP.
        /// </summary>
        private async Task Connected(String Version)
        {

            Assert.That(station!.Connections.TryAddConnection($"A CSMS of OCPP {Version}", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: Version),
                        Is.True, error);

            station = await TestStations.Started(station, directory);

            Assert.That(station.DialledConnections[id!], Does.StartWith("Connected"), "the station did not get connected to begin with");

        }

        #endregion

        #region (private static) Within(Condition)

        /// <summary>
        /// Whether the condition comes true within ten seconds.
        /// </summary>
        private static async Task<Boolean> Within(Func<Boolean> Condition)
        {

            var watch = Stopwatch.StartNew();

            while (!Condition())
            {

                if (watch.Elapsed > TimeSpan.FromSeconds(10))
                    return false;

                await Task.Delay(20);

            }

            return true;

        }

        #endregion

        #region (private) QueueSecurityEvent(Version, Type)

        /// <summary>
        /// A security event for the CSMS, as the station sends one, through
        /// the node of the given version.
        /// </summary>
        private Task QueueSecurityEvent(String Version, String Type)

            => Version == "1.6"
                   ? station!.OCPPv1_6Node.QueueSecurityEventAsync(OCPPv1_6.SecurityEvent.Parse(Type),       "from a test")
                   : station!.OCPPv2_1Node.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type),   "from a test");

        private Int32 Waiting(String Version)

            => Version == "1.6"
                   ? station!.OCPPv1_6Node.OfflineQueue.Pending.Count
                   : station!.OCPPv2_1Node.OfflineQueue.Pending.Count;

        #endregion


        #region TheCSMSIsAnswered(Version)

        /// <summary>
        /// A request of the CSMS is answered over the connection it came over.
        /// </summary>
        [TestCase("2.1", """[2,"asked","GetVariables",{"getVariableData":[{"component":{"name":"ClockCtrlr"},"variable":{"name":"TimeSource"}}]}]""")]
        [TestCase("1.6", """[2,"asked","GetConfiguration",{}]""")]
        public async Task TheCSMSIsAnswered(String Version, String Request)
        {

            await Connected(Version);

            await csms!.Ask(Request);

            var answer = await csms.Sent(message => message[0].Value<Int32>() == 3 && message[1].Value<String>() == "asked");

            Assert.That(answer, Is.Not.Null, "the request was not answered");
            Assert.That(answer![2], Is.InstanceOf<JObject>().And.Property("Count").GreaterThan(0), "the answer said nothing");

        }

        #endregion

        #region TheStationsEventsReachTheCSMS(Version)

        /// <summary>
        /// A security event the station sends reaches the CSMS, and does not
        /// wait in the queue.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task TheStationsEventsReachTheCSMS(String Version)
        {

            await Connected(Version);

            await QueueSecurityEvent(Version, "ConnectedTestEvent");

            Assert.That(await csms!.SentRequest("SecurityEventNotification", "ConnectedTestEvent"), Is.Not.Null, "the CSMS was not told");
            Assert.That(Waiting(Version), Is.Zero, "the event still waits in the queue");

        }

        #endregion

        #region WhatWaitedIsSentWhenTheCSMSIsReached(Version)

        /// <summary>
        /// What the station had to say before the CSMS could be reached is sent
        /// once it can be.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task WhatWaitedIsSentWhenTheCSMSIsReached(String Version)
        {

            await QueueSecurityEvent(Version, "WaitingTestEvent");

            Assert.That(Waiting(Version), Is.EqualTo(1), "with nothing connected, the event waits");

            await Connected(Version);

            Assert.That(await csms!.SentRequest("SecurityEventNotification", "WaitingTestEvent"), Is.Not.Null, "the CSMS was not told once it was reached");

        }

        #endregion

        #region WhatWaitedIsSentWhenTheConnectionComesBack(Version)

        /// <summary>
        /// What the station had to say while its connection was lost is sent
        /// once the connection is back.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task WhatWaitedIsSentWhenTheConnectionComesBack(String Version)
        {

            await Connected(Version);

            await csms!.Drop();

            Assert.That(await Within(() => station!.DialledConnections.Values.Single().Contains("was lost")), Is.True, "the station did not notice the loss");

            await QueueSecurityEvent(Version, "WhileItWasGone");

            Assert.That(await csms.SentRequest("SecurityEventNotification", "WhileItWasGone"), Is.Not.Null, "the CSMS was not told once the connection was back");

        }

        #endregion

    }

}
