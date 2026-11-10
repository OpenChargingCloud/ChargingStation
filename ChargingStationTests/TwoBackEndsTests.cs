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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using CSMS     = cloud.charging.open.ChargingStation.Tests.TalkingToTheCSMSTests.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A station with two connections of one OCPP version to two back ends - a
    /// CSMS and its successor, during a migration.
    /// </summary>
    /// <remarks>
    /// Both connections were dialled, and both were registered in the OCPP
    /// node's routing table under one name, "CSMS" or "CentralSystem". The route
    /// by that name went to whichever connection came up last, so:
    /// - everything the station said by itself - its security events, the
    ///   events of its monitors - went to the back end that had connected or
    ///   reconnected last;
    /// - the node registered once, so the second back end never received a
    ///   BootNotification and had nothing to go on;
    /// - and a second back end's answer to its BootNotification would have set
    ///   the heartbeat of the node, whose heartbeat goes to the first.
    ///
    /// Each connection has a name of its own now, and the name "CSMS" is the
    /// oldest connection that dials by itself: the primary, until a switchover
    /// says otherwise. Each connection is registered with on its own, and a
    /// back end that accepted the station gets its heartbeats at the interval
    /// it gave.
    /// </remarks>
    [TestFixture]
    public class TwoBackEndsTests
    {

        #region Data

        private static TimeSpan   Patience
            => TalkingToTheCSMSTests.Patience;

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             first;
        private CSMS?             second;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("two-back-ends");
            station    = TestStations.New(directory, TestStations.Offline);
            first      = new CSMS();
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

            station  = null;
            first    = null;
            second   = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region (private) ConnectedToBoth(Version)

        /// <summary>
        /// The station started with a connection to the first back end and then
        /// one to the second, both dialling by themselves - and connected to both.
        /// </summary>
        private async Task ConnectedToBoth(String Version)
        {

            second = new CSMS();

            var (firstId, secondId) = TwoConnections(Version, second.Port);

            var started = station = await TestStations.Started(station!, directory);

            Assert.That(await Within(() => started.DialledConnections[firstId]. StartsWith("Connected", StringComparison.Ordinal) &&
                                           started.DialledConnections[secondId].StartsWith("Connected", StringComparison.Ordinal)),
                        Is.True,
                        $"the station did not get connected to both: '{started.DialledConnections[firstId]}', '{started.DialledConnections[secondId]}'");

        }

        #endregion

        #region (private) ConnectedToTheFirstThenToTheSecond(Version, Second)

        /// <summary>
        /// The station started with the same two connections, while only the first
        /// back end is there: it registers with that one, and then the second one
        /// comes, as made by the given function on the given port.
        /// </summary>
        /// <remarks>
        /// So that the second one comes up after the first one accepted the
        /// station: with both there from the start, which one a node registered
        /// with and sent its heartbeat to depended on which answered first.
        /// </remarks>
        private async Task ConnectedToTheFirstThenToTheSecond(String Version, Func<IPPort, CSMS> Second)
        {

            var port                 = IPPort.Parse(TestPorts.Free());
            var (firstId, secondId)  = TwoConnections(Version, port);

            var started = station = await TestStations.Started(station!, directory);

            Assert.That(await Within(() => started.RegistrationJSON()[Version]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        $"the station did not register with the first back end: '{started.DialledConnections[firstId]}'");

            second = Second(port);

            Assert.That(await Within(() => started.DialledConnections[secondId].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected to the second back end: '{started.DialledConnections[secondId]}'");

        }

        #endregion

        #region (private) TwoConnections(Version, SecondPort)

        /// <summary>
        /// A connection to the first back end and then one to the given port, both
        /// dialling by themselves.
        /// </summary>
        private (String First, String Second) TwoConnections(String Version, IPPort SecondPort)
        {

            Assert.That(station!.Connections.TryAddConnection($"The CSMS, OCPP {Version}", $"ws://127.0.0.1:{first!.Port}/cs001", "CSMS", true, null, null,
                                                              out var firstId, out var error, OCPPVersion: Version),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection($"Its successor, OCPP {Version}", $"ws://127.0.0.1:{SecondPort}/cs001", "CSMS", true, null, null,
                                                             out var secondId, out error, OCPPVersion: Version),
                        Is.True, error);

            return (firstId!, secondId!);

        }

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

        #region (private) QueueSecurityEvent(Version, Type)

        private Task QueueSecurityEvent(String Version, String Type)

            => Version == "1.6"
                   ? station!.OCPPv1_6Node.QueueSecurityEventAsync(OCPPv1_6.SecurityEvent.Parse(Type),       "from a test")
                   : station!.OCPPv2_1Node.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type),   "from a test");

        #endregion

        #region (private static) RequestOf(Version, Id)

        private static String RequestOf(String Version, String Id)

            => Version == "1.6"
                   ? $$$"""[2,"{{{Id}}}","GetConfiguration",{}]"""
                   : $$$"""[2,"{{{Id}}}","GetVariables",{"getVariableData":[{"component":{"name":"ClockCtrlr"},"variable":{"name":"TimeSource"}}]}]""";

        #endregion

        #region (private static) AnswerTo(CSMS, Id)

        private static Task<JArray?> AnswerTo(CSMS CSMS, String Id)
            => CSMS.Sent(message => message[0].Value<Int32>() == 3 && message[1].Value<String>() == Id);

        #endregion


        #region BothBackEndsAreToldWhoTheStationIs(Version)

        /// <summary>
        /// Each back end receives a BootNotification: the second one too, when it
        /// comes after the first one accepted the station.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task BothBackEndsAreToldWhoTheStationIs(String Version)
        {

            await ConnectedToTheFirstThenToTheSecond(Version, port => new CSMS(port));

            Assert.That(first!.Requests("BootNotification"),                                 Has.Length.EqualTo(1),
                        "the back end the station dialled first was not told who it is, once");

            Assert.That(await Within(() => second!.Requests("BootNotification").Length > 0), Is.True,
                        "the back end that came second received no BootNotification");

        }

        #endregion

        #region WhatTheStationSaysByItselfGoesToTheFirst(Version)

        /// <summary>
        /// A security event goes to the oldest connection, not to the one that
        /// connected last.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task WhatTheStationSaysByItselfGoesToTheFirst(String Version)
        {

            await ConnectedToBoth(Version);

            await QueueSecurityEvent(Version, "ConnectedTestEvent");

            Assert.That(await first!.SentRequest("SecurityEventNotification", "ConnectedTestEvent"), Is.Not.Null,
                        "the back end the station dialled first was not told");

            Assert.That(second!.Requests("SecurityEventNotification"), Is.Empty,
                        "the back end the station dialled second was told what is the first one's");

        }

        #endregion

        #region TheFirstStaysTheOneWhenTheSecondComesBack(Version)

        /// <summary>
        /// The second back end drops the connection and the station connects
        /// again: what the station says by itself still goes to the first.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task TheFirstStaysTheOneWhenTheSecondComesBack(String Version)
        {

            await ConnectedToBoth(Version);

            var accepted = second!.Accepted;

            await second.Drop();

            Assert.That(await Within(() => second.Accepted > accepted && second.Connections == 1), Is.True,
                        "the station did not connect to the second back end again");

            await QueueSecurityEvent(Version, "ConnectedTestEvent");

            Assert.That(await first!.SentRequest("SecurityEventNotification", "ConnectedTestEvent"), Is.Not.Null,
                        "the back end the station dialled first was not told");

            Assert.That(second.Requests("SecurityEventNotification"), Is.Empty,
                        "the back end that connected again took over what is the first one's");

        }

        #endregion

        #region EachBackEndIsAnswered(Version)

        /// <summary>
        /// Each back end's request is answered to that back end.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task EachBackEndIsAnswered(String Version)
        {

            await ConnectedToBoth(Version);

            await first! .Ask(RequestOf(Version, "asked-by-the-first"));
            await second!.Ask(RequestOf(Version, "asked-by-the-second"));

            Assert.That(await AnswerTo(first,  "asked-by-the-first"),  Is.Not.Null, "the first back end was not answered");
            Assert.That(await AnswerTo(second, "asked-by-the-second"), Is.Not.Null, "the second back end was not answered");

        }

        #endregion

        #region ASecondOutOfReachHoldsUpNothing(Version)

        /// <summary>
        /// The second back end goes away and stays away: what the station says by
        /// itself goes to the first all the same, and waits for nothing.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ASecondOutOfReachHoldsUpNothing(String Version)
        {

            await ConnectedToBoth(Version);

            await second!.DisposeAsync();
            second = null;

            await QueueSecurityEvent(Version, "ConnectedTestEvent");

            Assert.That(await first!.SentRequest("SecurityEventNotification", "ConnectedTestEvent"), Is.Not.Null,
                        "the first back end was not told while the second one was away");

            // Waited for: the first back end has the event the moment it reads
            // it, and the station lets go of it once the answer is back.
            Assert.That(await Within(() => (Version == "1.6"
                                                ? station!.OCPPv1_6Node.OfflineQueue.Pending.Count
                                                : station!.OCPPv2_1Node.OfflineQueue.Pending.Count) == 0),
                        Is.True,
                        "the event still waits in the queue");

        }

        #endregion

        #region ASecondThatRefusesLeavesTheFirstsHeartbeat(Version)

        /// <summary>
        /// The second back end will not have the station: the heartbeat the first
        /// one asked for goes on.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ASecondThatRefusesLeavesTheFirstsHeartbeat(String Version)
        {

            first!.BootInterval = 5;

            await ConnectedToTheFirstThenToTheSecond(Version, port => new CSMS(port) { BootStatus = "Rejected" });

            Assert.That(await Within(() => second!.Requests("BootNotification").Length > 0), Is.True,
                        "the second back end was never asked, so its refusal was never tested");

            var before = first.Requests("Heartbeat").Length;

            Assert.That(await Within(() => first.Requests("Heartbeat").Length >= before + 2), Is.True,
                        "the heartbeat to the first back end stopped when the second one refused the station");

            Assert.That(second!.Requests("Heartbeat"), Is.Empty,
                        "the back end that refused the station got heartbeats");

        }

        #endregion

        #region ASecondThatAcceptsGetsItsHeartbeat(Version)

        /// <summary>
        /// The second back end accepted the station: it gets a heartbeat at the
        /// interval it gave, and the first one goes on getting its own.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ASecondThatAcceptsGetsItsHeartbeat(String Version)
        {

            first!.BootInterval = 5;

            await ConnectedToTheFirstThenToTheSecond(Version, port => new CSMS(port) { BootInterval = 5 });

            var before = first.Requests("Heartbeat").Length;

            Assert.That(await Within(() => second!.Requests("Heartbeat").Length >= 2), Is.True,
                        "the second back end, which accepted the station, gets no heartbeat");

            Assert.That(first.Requests("Heartbeat").Length, Is.GreaterThanOrEqualTo(before + 1),
                        "the first back end got no heartbeat any more once the second one was there");

        }

        #endregion

    }

}
