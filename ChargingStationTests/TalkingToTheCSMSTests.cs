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
    /// A plain WebSocket server stands in for the CSMS, one that answers a
    /// BootNotification as it is told to, a Heartbeat with its time and every
    /// other request with an empty answer, and keeps every frame it is sent.
    ///
    /// And registering: the station sends its BootNotification once it is
    /// connected, tells the CSMS nothing else until it is accepted, then
    /// sends a heartbeat at the interval it was given; it registers again
    /// after its OCPP nodes were built anew, and not when a connection that
    /// was accepted comes back.
    /// </remarks>
    [TestFixture]
    public class TalkingToTheCSMSTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind: it takes every request, answers it with
        /// what is least, and keeps what it was sent.
        /// </summary>
        internal sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>                         received    = new();
            private readonly ConcurrentQueue<(String Action, TimeSpan At)>  arrivals    = new();
            private readonly Stopwatch                                       clock       = Stopwatch.StartNew();
            private readonly ConcurrentDictionary<String, Byte>              refuseOnce  = new();
            private          Int32                                           accepted;

            public WebSocketServer  Server         { get; }
            public IPPort           Port           { get; }

            /// <summary>
            /// How it answers a BootNotification: "Accepted", "Pending" or
            /// "Rejected" - or, null, with an error, and "Silent", not at all.
            /// </summary>
            public String?          BootStatus     { get; set; } = "Accepted";

            /// <summary>
            /// The interval it answers a BootNotification with, in seconds.
            /// </summary>
            public Int32            BootInterval   { get; set; } = 300;

            public CSMS()
            {

                Port    = IPPort.Parse(TestPorts.Free());
                Server  = new WebSocketServer(HTTPPort: Port, RequireAuthentication: false, AutoStart: true);

                Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {
                    Interlocked.Increment(ref accepted);
                    return Task.CompletedTask;
                };

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    // Answered as it is told to when the request arrives -
                    // before a test can see that it arrived and tell it
                    // otherwise.
                    var answer  = message[0].Value<Int32>() == 2 ? AnswerTo(message) : null;

                    // When it came, before the answer goes and before a test
                    // can count it.
                    if (message[0].Value<Int32>() == 2)
                        arrivals.Enqueue((message[2].Value<String>() ?? "", clock.Elapsed));

                    received.Enqueue(message);

                    if (answer is not null)
                        await Server.SendTextMessage(connection, answer.ToString(Formatting.None));

                };

            }

            /// <summary>
            /// The answer to a request: an error where it is to be refused, and
            /// none where it is not to be answered.
            /// </summary>
            private JArray? AnswerTo(JArray Request)
            {

                var action = Request[2].Value<String>() ?? "";

                if (action == "BootNotification" && BootStatus == "Silent")
                    return null;

                if (refuseOnce.TryRemove(action, out _) || (action == "BootNotification" && BootStatus is null))
                    return new JArray(4, Request[1], "InternalError", "Refused by the test CSMS.", new JObject());

                return new JArray(3, Request[1], action switch {

                           "BootNotification"  => new JObject(
                                                      new JProperty("status",       BootStatus),
                                                      new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                      new JProperty("interval",     BootInterval)
                                                  ),

                           "Heartbeat"         => new JObject(
                                                      new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                                  ),

                           _                   => new JObject()

                       });

            }

            /// <summary>
            /// Refuse the next request of the given action, and take the one after.
            /// </summary>
            public void RefuseOnce(String Action)
                => refuseOnce[Action] = 0;

            /// <summary>
            /// The requests of the given action it was sent so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 &&
                                                 message[2].Value<String>() == Action)];

            /// <summary>
            /// When the requests of the given action came, in the order they
            /// came, counted from when this CSMS was made.
            /// </summary>
            public TimeSpan[] Arrivals(String Action)
                => [.. arrivals.Where(arrival => arrival.Action == Action).Select(arrival => arrival.At)];

            /// <summary>
            /// How many connections it took, ever.
            /// </summary>
            public Int32 Accepted
                => Volatile.Read(ref accepted);

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
            /// there is one or Patience has passed.
            /// </summary>
            public async Task<JArray?> Sent(Func<JArray, Boolean> Wanted)
            {

                var watch = Stopwatch.StartNew();

                while (watch.Elapsed < Patience)
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

        /// <summary>
        /// How long a test waits for what it waits for: a frame at the CSMS, a
        /// state of the station.
        /// </summary>
        /// <remarks>
        /// Ten seconds were enough wherever the machine kept up, where none of
        /// these waits takes more than five. GitHub's runners stand still for
        /// seconds now and then: a heartbeat due after five seconds had not
        /// come after fifteen, a station answered a CSMS dropped seven seconds
        /// later in one run and one in the next, and a first dial on the
        /// loopback ran into its own timeout - a different test of these each
        /// time, never one locally. Every wait here looks every 20 ms and ends
        /// as soon as what it waits for is there, so a longer one costs a run
        /// that passes nothing, and a test that fails still fails.
        /// </remarks>
        internal static readonly TimeSpan  Patience   = TimeSpan.FromSeconds(30);

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
        /// version of OCPP - and registered with it, where the CSMS accepts
        /// stations: until then, the station tells it nothing.
        /// </summary>
        private async Task Connected(String Version)
        {

            Assert.That(station!.Connections.TryAddConnection($"A CSMS of OCPP {Version}", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: Version),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            // Waited for rather than read at once: a first dial that ran into its
            // own timeout on a runner that stood still is tried again by itself.
            Assert.That(await Within(() => started.DialledConnections[id!].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected to begin with: {started.DialledConnections[id!]}");

            if (csms.BootStatus == "Accepted")
                Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station did not register: it is {Registration(Version)}");

        }

        #endregion

        #region (private static) RequestOf(Version) / (private static) Within(Condition)

        /// <summary>
        /// A request a CSMS of the given version may send, by the id "asked".
        /// </summary>
        private static String RequestOf(String Version)

            => Version == "1.6"
                   ? """[2,"asked","GetConfiguration",{}]"""
                   : """[2,"asked","GetVariables",{"getVariableData":[{"component":{"name":"ClockCtrlr"},"variable":{"name":"TimeSource"}}]}]""";

        /// <summary>
        /// Whether the condition comes true within Patience.
        /// </summary>
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

        #region (private) SwitchTheEVSEs()

        /// <summary>
        /// Take every EVSE out of service, or put it back: a change that builds
        /// the OCPP nodes anew.
        /// </summary>
        private void SwitchTheEVSEs()
        {

            var switched = new JObject(
                               new JProperty("evses", new JArray(
                                   station!.EVSEs.Select(evse => {
                                       var json = evse.ToJSON();
                                       json["operative"] = !evse.Operative;
                                       return json;
                                   })
                               ))
                           );

            Assert.That(station.TryUpdateEVSEConfiguration(switched, _ => true, out _, out var error, out _), Is.True, error);

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
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task TheCSMSIsAnswered(String Version)
        {

            await Connected(Version);

            await csms!.Ask(RequestOf(Version));

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

        #region AfterAChangeOfTheEVSEsTheStationConnectsAgainThroughItsNewNodes(Version)

        /// <summary>
        /// A change of the EVSEs, which builds the OCPP nodes anew, hangs up
        /// and dials again: the CSMS is answered, and told what the station
        /// says, by the new nodes.
        /// </summary>
        /// <remarks>
        /// The connection used to stay on the node that made it. That node
        /// answered the CSMS with the EVSEs as they had been, and what the
        /// station said afterwards went through the new nodes, which had no
        /// connection.
        /// </remarks>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AfterAChangeOfTheEVSEsTheStationConnectsAgainThroughItsNewNodes(String Version)
        {

            await Connected(Version);

            SwitchTheEVSEs();

            Assert.That(await Within(() => csms!.Accepted == 2 && csms.Connections == 1), Is.True,
                        $"the station did not hang up and connect again: {csms!.Accepted} connection(s) taken, {csms.Connections} open");

            Assert.That(station!.OCPPWebSocketClientCount, Is.EqualTo(1), "the new nodes do not hold the connection");

            await QueueSecurityEvent(Version, "AfterTheChange");

            Assert.That(await csms.SentRequest("SecurityEventNotification", "AfterTheChange"), Is.Not.Null, "the CSMS was not told");

            await csms.Ask(RequestOf(Version));

            Assert.That(await csms.Sent(message => message[0].Value<Int32>() == 3 && message[1].Value<String>() == "asked"), Is.Not.Null,
                        "the request was not answered");

        }

        #endregion

        #region AStationStoppedAfterAChangeOfTheEVSEsHangsUp(Version)

        /// <summary>
        /// A station stopped after a change of its EVSEs hangs up: what it is
        /// connected to is on the nodes it has.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AStationStoppedAfterAChangeOfTheEVSEsHangsUp(String Version)
        {

            await Connected(Version);

            SwitchTheEVSEs();

            Assert.That(await Within(() => csms!.Accepted == 2 && csms.Connections == 1), Is.True, "the station did not connect again");

            await station!.Stop();

            Assert.That(await Within(() => csms!.Connections == 0), Is.True, "the CSMS is still connected to a station that stopped");

        }

        #endregion

        #region AStationNotStartedDialsNothingWhenItsEVSEsChange()

        /// <summary>
        /// A station that was never started dials nothing when its EVSEs change,
        /// although it has a connection set to connect by itself.
        /// </summary>
        [Test]
        public async Task AStationNotStartedDialsNothingWhenItsEVSEsChange()
        {

            Assert.That(station!.Connections.TryAddConnection("A CSMS", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out _, out var error, OCPPVersion: "2.1"),
                        Is.True, error);

            SwitchTheEVSEs();

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(csms.Accepted, Is.Zero);

        }

        #endregion


        #region (private) Registration(Version) / (private static) IsRequest(Message, Action) / (private static) Said(BootNotification, Version)

        /// <summary>
        /// Where the station's node of the given version stands with its CSMS.
        /// </summary>
        private String? Registration(String Version)
            => station!.RegistrationJSON()[Version]?["state"]?.Value<String>();

        private static Boolean IsRequest(JArray Message, String Action)
            => Message[0].Value<Int32>() == 2 && Message[2].Value<String>() == Action;

        /// <summary>
        /// What a BootNotification says the station is, under the names its
        /// version gives them.
        /// </summary>
        private static (String? Vendor, String? Model, String? SerialNumber, String? Firmware) Said(JArray BootNotification, String Version)
        {

            var payload = (BootNotification[3] as JObject)!;

            if (Version == "1.6")
                return (payload.Value<String>("chargePointVendor"),
                        payload.Value<String>("chargePointModel"),
                        payload.Value<String>("chargePointSerialNumber"),
                        payload.Value<String>("firmwareVersion"));

            var station = (payload["chargingStation"] as JObject)!;

            return (station.Value<String>("vendorName"),
                    station.Value<String>("model"),
                    station.Value<String>("serialNumber"),
                    station.Value<String>("firmwareVersion"));

        }

        #endregion


        #region TheStationRegistersWithWhatItSaysItIs(Version)

        /// <summary>
        /// Once connected, the station sends its BootNotification: who made it,
        /// which model it is and the firmware it was built from - and, in 2.1,
        /// that it powered up. The CSMS accepts it.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task TheStationRegistersWithWhatItSaysItIs(String Version)
        {

            await Connected(Version);

            var boot = await csms!.Sent(message => IsRequest(message, "BootNotification"));

            Assert.That(boot, Is.Not.Null, "no BootNotification was sent");

            var said = Said(boot!, Version);

            Assert.Multiple(() => {

                Assert.That(said.Vendor,        Is.EqualTo("GraphDefined"));
                Assert.That(said.Model,         Is.EqualTo("ChargingStationCLI"));
                Assert.That(said.SerialNumber,  Is.Null, "a station whose file says no serial number said one");
                Assert.That(said.Firmware,      Is.Not.Null.And.Not.Empty);

                if (Version == "2.1")
                    Assert.That(boot![3]!.Value<String>("reason"), Is.EqualTo("PowerUp"));

            });

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");

        }

        #endregion

        #region NothingElseIsSentUntilTheCSMSAccepts(Version)

        /// <summary>
        /// A CSMS that answers Pending is told nothing else - no event, no
        /// heartbeat - and asked again after the interval it gave. Once it
        /// accepts the station, what waited is sent.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task NothingElseIsSentUntilTheCSMSAccepts(String Version)
        {

            csms!.BootStatus    = "Pending";
            csms. BootInterval  = 1;

            await QueueSecurityEvent(Version, "BeforeConnected");

            await Connected(Version);

            // And one while connected, which the node would send at once.
            await QueueSecurityEvent(Version, "WhilePending");

            Assert.That(await Within(() => csms.Requests("BootNotification").Length >= 2), Is.True, "a pending station did not ask again after the interval");

            Assert.Multiple(() => {
                Assert.That(csms.Requests("SecurityEventNotification"),  Is.Empty,          "a pending station sent its events");
                Assert.That(csms.Requests("Heartbeat"),                  Is.Empty,          "a pending station sent a heartbeat");
                Assert.That(Waiting(Version),                            Is.EqualTo(2),     "the events do not wait");
            });

            csms.BootStatus = "Accepted";

            Assert.That(await csms.SentRequest("SecurityEventNotification", "BeforeConnected"), Is.Not.Null, "what waited was not sent once the station was accepted");
            Assert.That(await csms.SentRequest("SecurityEventNotification", "WhilePending"),    Is.Not.Null, "what waited was not sent once the station was accepted");
            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");

        }

        #endregion

        #region ARejectedStationWaitsTheIntervalBeforeItAsksAgain(Version)

        /// <summary>
        /// A CSMS that rejects the station is told nothing else, and asked
        /// again only once the interval it gave is up.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ARejectedStationWaitsTheIntervalBeforeItAsksAgain(String Version)
        {

            csms!.BootStatus    = "Rejected";
            csms. BootInterval  = 3;

            await Connected(Version);

            Assert.That(await Within(() => Registration(Version) == "Rejected"), Is.True, $"the station is {Registration(Version)}");

            await QueueSecurityEvent(Version, "WhileRejected");

            Assert.That(await Within(() => csms.Requests("BootNotification").Length >= 2), Is.True, "a rejected station did not ask again once its time was up");

            // Measured where the CSMS took the two, not by when this test
            // looks: a runner slow enough to take seconds between the
            // rejection and a look at what came would see the second one,
            // on time, and call it early. A timer may fire a tick of the
            // clock before its time, hence the little that is let off.
            var asked = csms.Arrivals("BootNotification");

            Assert.Multiple(() => {
                Assert.That(asked[1] - asked[0],                         Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(csms.BootInterval) - TimeSpan.FromMilliseconds(100)),
                                                                                                 "a rejected station asked again before its time");
                Assert.That(csms.Requests("SecurityEventNotification"),  Is.Empty,               "a rejected station sent its event");
            });

        }

        #endregion

        #region AnUnansweredBootNotificationLeavesTheStationUnregistered(Version)

        /// <summary>
        /// A CSMS that answers the BootNotification with an error leaves the
        /// station unregistered: it says why and when it asks again, sends no
        /// heartbeat, and keeps what it has to say.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AnUnansweredBootNotificationLeavesTheStationUnregistered(String Version)
        {

            csms!.BootStatus = null;

            await QueueSecurityEvent(Version, "Unregistered");

            await Connected(Version);

            Assert.That(await Within(() => Registration(Version) == "Unanswered"), Is.True, $"the station is {Registration(Version)}");

            // And one while connected, which the node would send at once.
            await QueueSecurityEvent(Version, "StillUnregistered");

            var registration = station!.RegistrationJSON()[Version]!;

            Assert.Multiple(() => {
                Assert.That(registration.Value<String>("problem"),       Is.Not.Null.And.Not.Empty);
                Assert.That(registration.Value<String>("nextTry"),       Is.Not.Null);
                Assert.That(csms.Requests("SecurityEventNotification"),  Is.Empty,  "an unregistered station sent its events");
                Assert.That(Version == "1.6"
                                ? station.OCPPv1_6Node.DisableSendHeartbeats
                                : station.OCPPv2_1Node.DisableSendHeartbeats,  Is.True,  "an unregistered station sends heartbeats");
                Assert.That(Waiting(Version),                            Is.EqualTo(2));
            });

        }

        #endregion

        #region AnAcceptedStationSendsHeartbeatsAtItsInterval(Version)

        /// <summary>
        /// Accepted, the station sends a heartbeat at the interval it was given.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AnAcceptedStationSendsHeartbeatsAtItsInterval(String Version)
        {

            // The shortest a node takes.
            csms!.BootInterval = 5;

            await Connected(Version);

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");
            Assert.That(await csms.Sent(message => IsRequest(message, "Heartbeat")), Is.Not.Null, "no heartbeat was sent");

        }

        #endregion

        #region AStationAcceptedDoesNotRegisterAgainWhenTheConnectionComesBack(Version)

        /// <summary>
        /// A connection that comes back to a CSMS that accepted the station is
        /// not registered again - OCPP asks for a BootNotification at a start -
        /// and what the station has to say goes at once.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AStationAcceptedDoesNotRegisterAgainWhenTheConnectionComesBack(String Version)
        {

            await Connected(Version);

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");

            await csms!.Drop();

            Assert.That(await Within(() => csms.Accepted == 2 && csms.Connections == 1), Is.True, "the connection did not come back");

            await QueueSecurityEvent(Version, "AfterComingBack");

            Assert.That(await csms.SentRequest("SecurityEventNotification", "AfterComingBack"), Is.Not.Null, "the CSMS was not told");
            Assert.That(csms.Requests("BootNotification"), Has.Length.EqualTo(1), "the station registered again");

        }

        #endregion

        #region ABootNotificationLostWithItsConnectionIsAskedAgainWhenItComesBack(Version)

        /// <summary>
        /// A BootNotification asked over a connection that is lost before the
        /// answer comes is asked again over the connection that comes back,
        /// rather than waited for until it times out.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ABootNotificationLostWithItsConnectionIsAskedAgainWhenItComesBack(String Version)
        {

            csms!.BootStatus = "Silent";

            await Connected(Version);

            Assert.That(await Within(() => csms.Requests("BootNotification").Length == 1), Is.True, "no BootNotification was sent");

            // Nothing has answered, so nothing has switched the heartbeats
            // off: a node built with them would send them now.
            Assert.That(Version == "1.6"
                            ? station!.OCPPv1_6Node.DisableSendHeartbeats
                            : station!.OCPPv2_1Node.DisableSendHeartbeats,  Is.True,  "a station not registered sends heartbeats");

            csms.BootStatus = "Accepted";

            await csms.Drop();

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");
            Assert.That(csms.Requests("BootNotification"), Has.Length.EqualTo(2));

        }

        #endregion

        #region AfterAChangeOfTheEVSEsTheStationRegistersAgain(Version)

        /// <summary>
        /// After a change of the EVSEs the new nodes register again - in 2.1 as
        /// a local reset - and are accepted.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AfterAChangeOfTheEVSEsTheStationRegistersAgain(String Version)
        {

            await Connected(Version);

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");

            SwitchTheEVSEs();

            Assert.That(await Within(() => csms!.Requests("BootNotification").Length == 2), Is.True, "the new nodes did not register");

            if (Version == "2.1")
                Assert.That(csms!.Requests("BootNotification")[1][3]!.Value<String>("reason"), Is.EqualTo("LocalReset"));

            Assert.That(await Within(() => Registration(Version) == "Accepted"), Is.True, $"the station is {Registration(Version)}");

        }

        #endregion

        #region AChangeOfWhatTheStationSaysItIsRegistersAgainWithIt(Version)

        /// <summary>
        /// A change of what the station says it is hangs up, dials again and
        /// registers with what it says now.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task AChangeOfWhatTheStationSaysItIsRegistersAgainWithIt(String Version)
        {

            await Connected(Version);

            Assert.That(await Within(() => csms!.Requests("BootNotification").Length == 1), Is.True, "the station did not register to begin with");

            Assert.That(station!.TryUpdateNameplateConfiguration(new JObject(
                                                                     new JProperty("vendor",           "Test Vendor"),
                                                                     new JProperty("model",            "Test Model"),
                                                                     new JProperty("serialNumber",     "SN-4711"),
                                                                     new JProperty("firmwareVersion",  "fw-1.2.3")
                                                                 ),
                                                                 out var error,
                                                                 out _),
                        Is.True, error);

            Assert.That(await Within(() => csms!.Requests("BootNotification").Length == 2), Is.True, "the station did not register again");
            Assert.That(csms!.Accepted, Is.EqualTo(2), "the station did not hang up and dial again");

            var said = Said(csms.Requests("BootNotification")[1], Version);

            Assert.Multiple(() => {
                Assert.That(said.Vendor,        Is.EqualTo("Test Vendor"));
                Assert.That(said.Model,         Is.EqualTo("Test Model"));
                Assert.That(said.SerialNumber,  Is.EqualTo("SN-4711"));
                Assert.That(said.Firmware,      Is.EqualTo("fw-1.2.3"));
            });

        }

        #endregion

        #region TheOCPP21NodeTriesItsQueueAgainByTheClock()

        /// <summary>
        /// The OCPP 2.1 node keeps its maintenance: an event the CSMS refused is
        /// sent again by the clock, without anything else happening.
        /// </summary>
        /// <remarks>
        /// Its maintenance used to be switched off, so a message the CSMS did
        /// not take waited for the next one, or for the connection to come back.
        /// </remarks>
        [Test]
        public async Task TheOCPP21NodeTriesItsQueueAgainByTheClock()
        {

            await Connected("2.1");

            Assert.That(await Within(() => Registration("2.1") == "Accepted"), Is.True, $"the station is {Registration("2.1")}");

            station!.OCPPv2_1Node.OfflineQueueRetry = TimeSpan.FromSeconds(1);

            csms!.RefuseOnce("SecurityEventNotification");

            await QueueSecurityEvent("2.1", "RefusedOnce");

            Assert.That(await Within(() => csms.Requests("SecurityEventNotification").Length == 2), Is.True, "the event was not tried again");
            Assert.That(await Within(() => Waiting("2.1") == 0), Is.True, "the event still waits");

        }

        #endregion

    }

}
