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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A restart once nothing charges any more: a Reset of OCPP 2.1 OnIdle,
    /// and a Soft one of OCPP 1.6. The station builds its OCPP side anew and
    /// dials again - its CSMS sees the connection come back, and a new
    /// BootNotification - and while something charges it waits, and starts
    /// nothing new.
    /// </summary>
    /// <remarks>
    /// Asked over a connection to a CSMS, as a CSMS asks. The sessions are the
    /// station's own simulated ones, started as an app on the network starts
    /// one.
    /// </remarks>
    [TestFixture]
    public class ResetTests
    {

        #region Data

        private const String                  card       = "04A2B3C4D5E6F7";

        private String                        directory  = "";
        private ChargingStation?              station;
        private TalkingToTheCSMSTests.CSMS?   csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {

            var configuration = TestStations.Offline;

            configuration["evses"] = new JArray(
                                         new JObject(
                                             new JProperty("id",                 1),
                                             new JProperty("connectors",         new JArray("sType2")),
                                             new JProperty("maxPower_kW",        22.0),
                                             new JProperty("operative",          true),
                                             new JProperty("physicalReference",  "A")
                                         ),
                                         new JObject(
                                             new JProperty("id",                 2),
                                             new JProperty("connectors",         new JArray("sType2")),
                                             new JProperty("maxPower_kW",        22.0),
                                             new JProperty("operative",          true),
                                             new JProperty("physicalReference",  "B")
                                         )
                                     );

            directory  = TestStations.TemporaryDirectory("reset");
            station    = TestStations.New(directory, configuration);
            csms       = new TalkingToTheCSMSTests.CSMS();

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
        /// The station started, connected to this test's CSMS over the given
        /// version of OCPP, and registered with it.
        /// </summary>
        private async Task Connected(String Version)
        {

            Assert.That(station!.Connections.TryAddConnection($"A CSMS of OCPP {Version}", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: Version),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.DialledConnections[id!].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected: {started.DialledConnections[id!]}");

            Assert.That(await Within(() => started.RegistrationJSON()[Version]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        "the station did not register");

        }

        #endregion

        #region (private) Reset(Type)

        /// <summary>
        /// Send the station a Reset of the given type, as the CSMS does, and
        /// answer the status it answered with.
        /// </summary>
        private async Task<String?> Reset(String Type)
        {

            var id = Guid.NewGuid().ToString();

            await csms!.Ask(new JArray(2, id, "Reset", new JObject(new JProperty("type", Type))).ToString(Formatting.None));

            var answer = await csms.Sent(message => message[0].Value<Int32>() is 3 or 4 &&
                                                    message[1].Value<String>() == id);

            Assert.That(answer, Is.Not.Null, $"the station did not answer a Reset {Type}");
            Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"the station refused a Reset {Type}: {answer.ToString(Formatting.None)}");

            return answer[2]?["status"]?.Value<String>();

        }

        #endregion

        #region (private) Boots / Start(EVSE) / Stop(EVSE) / Within(Condition)

        /// <summary>
        /// How many BootNotifications the CSMS was sent so far.
        /// </summary>
        private Int32 Boots
            => csms!.Requests("BootNotification").Length;

        private Boolean Start(Byte EVSE, out String? Error)

            => station!.TryStartLocally(EVSE, card, out _, out _, out Error);

        /// <summary>
        /// What the EVSEs page says of an EVSE: "available", "occupied" ...
        /// </summary>
        private String? StatusOf(Byte EVSE)

            => (station!.EVSEsStatusJSON()["evses"] as JArray)?.
                   FirstOrDefault(evse => evse["id"]?.Value<Byte>() == EVSE)?["status"]?.Value<String>();

        private void Stop(Byte EVSE)

            => Assert.That(station!.TryStopSession(EVSE, out _, out var error), Is.True, error);

        private static async Task<Boolean> Within(Func<Boolean> Condition)
        {

            var watch = Stopwatch.StartNew();

            while (!Condition())
            {

                if (watch.Elapsed > TalkingToTheCSMSTests.Patience)
                    return false;

                await Task.Delay(20);

            }

            return true;

        }

        #endregion


        #region AnIdleStationRestartsAtOnce(Version, Type)

        /// <summary>
        /// Nothing charges: the station says yes, and restarts its OCPP side -
        /// the CSMS sees the connection close and come back, and is sent a new
        /// BootNotification.
        /// </summary>
        [TestCase("1.6", "Soft")]
        [TestCase("2.1", "OnIdle")]
        public async Task AnIdleStationRestartsAtOnce(String Version, String Type)
        {

            await Connected(Version);

            var boots        = Boots;
            var connections  = csms!.Accepted;

            Assert.That(await Reset(Type), Is.EqualTo("Accepted"));

            Assert.That(await Within(() => csms.Accepted > connections && Boots > boots), Is.True,
                        $"the station did not restart: {csms.Accepted - connections} new connection(s), {Boots - boots} new BootNotification(s)");

        }

        #endregion

        #region ABusyStationWaitsAndStartsNothingNew(Version, Type, Said)

        /// <summary>
        /// Something charges: the station says so - "Scheduled" over OCPP 2.1,
        /// "Accepted" over OCPP 1.6, which has no other word - starts no new
        /// session at the free EVSE, and restarts when the running session
        /// ends. Then sessions start again.
        /// </summary>
        [TestCase("1.6", "Soft",   "Accepted")]
        [TestCase("2.1", "OnIdle", "Scheduled")]
        public async Task ABusyStationWaitsAndStartsNothingNew(String Version, String Type, String Said)
        {

            await Connected(Version);

            Assert.That(Start(1, out var error), Is.True, error);

            var boots = Boots;

            Assert.That(await Reset(Type), Is.EqualTo(Said));

            Assert.That(Start(2, out var refused), Is.False, "a session started while a restart waits");
            Assert.That(refused, Does.Contain("restarts"));
            Assert.That(StatusOf(2), Is.EqualTo("inoperative"), "the free EVSE is not shown out of service while the restart waits");

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.That(Boots, Is.EqualTo(boots), "the station restarted while something charged");

            Stop(1);

            Assert.That(await Within(() => Boots > boots), Is.True, "the station did not restart when the last session ended");

            String? afterwards = null;

            Assert.That(await Within(() => StatusOf(2) == "available"), Is.True, $"the free EVSE is not shown available after the restart: {StatusOf(2)}");
            Assert.That(await Within(() => Start(2, out afterwards)), Is.True, $"no session starts after the restart: {afterwards}");

        }

        #endregion

        #region ARestartAtOnceIsLeftAsItWas(Version, Type)

        /// <summary>
        /// A Hard reset of OCPP 1.6 and one of OCPP 2.1 Immediate are answered
        /// as before, and nothing more: they are not a restart once idle.
        /// </summary>
        [TestCase("1.6", "Hard")]
        [TestCase("2.1", "Immediate")]
        public async Task ARestartAtOnceIsLeftAsItWas(String Version, String Type)
        {

            await Connected(Version);

            Assert.That(Start(1, out var error), Is.True, error);

            var boots = Boots;

            Assert.That(await Reset(Type), Is.EqualTo("Accepted"));

            Assert.That(Start(2, out var second), Is.True, second);

            await Task.Delay(TimeSpan.FromMilliseconds(500));

            Assert.That(Boots, Is.EqualTo(boots));

        }

        #endregion

    }

}
