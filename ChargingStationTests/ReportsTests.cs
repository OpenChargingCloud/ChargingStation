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

using cloud.charging.open.ChargingStation.CommandLine;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a station tells its CSMS of itself: how each EVSE stands, when it
    /// was accepted and whenever one changes - and again when it is asked to,
    /// with a BootNotification and a Heartbeat, by a TriggerMessage, on the
    /// web page and at the command line. And a cable let go of, where nobody
    /// is charging.
    /// </summary>
    /// <remarks>
    /// Asked over a connection to a CSMS, as a CSMS asks. The sessions are the
    /// station's own simulated ones, started as an app on the network starts
    /// one.
    /// </remarks>
    [TestFixture]
    public class ReportsTests
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

            directory  = TestStations.TemporaryDirectory("reports");
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
        /// version of OCPP, registered with it - and told it how both EVSEs
        /// stand, which is what it does first.
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

            Assert.That(await Within(() => Told(1).LastOrDefault() == "Available" && Told(2).LastOrDefault() == "Available"), Is.True,
                        $"a station that was accepted did not say how its EVSEs stand: EVSE 1 [{String.Join(", ", Told(1))}], EVSE 2 [{String.Join(", ", Told(2))}]");

        }

        #endregion

        #region (private) Ask(Action, Payload) / Trigger(Version, Message, EVSE) / UnlockOverOCPP(Version, EVSE)

        /// <summary>
        /// Send the station a request, as the CSMS does, and answer what it
        /// answered with.
        /// </summary>
        private async Task<JObject> Ask(String Action, JObject Payload)
        {

            var id = Guid.NewGuid().ToString();

            await csms!.Ask(new JArray(2, id, Action, Payload).ToString(Formatting.None));

            var answer = await csms.Sent(message => message[0].Value<Int32>() is 3 or 4 &&
                                                    message[1].Value<String>() == id);

            Assert.That(answer, Is.Not.Null, $"the station did not answer a {Action}");
            Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"the station refused a {Action}: {answer.ToString(Formatting.None)}");

            return (JObject) answer[2];

        }

        /// <summary>
        /// A TriggerMessage, for the station as a whole or for one EVSE, in
        /// the words of the given version.
        /// </summary>
        private async Task<String?> Trigger(String Version, String Message, Byte? EVSE = null)
        {

            var payload = new JObject(new JProperty("requestedMessage", Message));

            if (EVSE is Byte evse)
            {
                if (Version == "1.6")
                    payload["connectorId"] = evse;
                else
                    payload["evse"]        = new JObject(new JProperty("id", evse));
            }

            return (await Ask("TriggerMessage", payload))["status"]?.Value<String>();

        }

        /// <summary>
        /// An UnlockConnector for an EVSE, in the words of the given version.
        /// </summary>
        private async Task<String?> UnlockOverOCPP(String Version, Byte EVSE)

            => (await Ask("UnlockConnector",
                          Version == "1.6"
                              ? new JObject(new JProperty("connectorId", EVSE))
                              : new JObject(new JProperty("evseId", EVSE), new JProperty("connectorId", 1))))["status"]?.Value<String>();

        #endregion

        #region (private) Told(EVSE) / Sent(Action) / Start(EVSE) / Stop(EVSE) / StatusOf(EVSE) / Within(Condition)

        /// <summary>
        /// What the CSMS was told of an EVSE so far, in the order it was
        /// told: the status of every StatusNotification for it, of either
        /// version.
        /// </summary>
        private String[] Told(Byte EVSE)

            => [.. csms!.Requests("StatusNotification").
                         Select(request => request[3]).
                         Where (payload => (payload["evseId"] ?? payload["connectorId"])?.Value<Int32>() == EVSE).
                         Select(payload => (payload["connectorStatus"] ?? payload["status"])?.Value<String>() ?? "")];

        private Int32 Sent(String Action)
            => csms!.Requests(Action).Length;

        private Boolean Start(Byte EVSE, out String? Error)

            => station!.TryStartLocally(EVSE, card, out _, out _, out Error);

        private void Stop(Byte EVSE)

            => Assert.That(station!.TryStopSession(EVSE, out _, out var error), Is.True, error);

        private String? StatusOf(Byte EVSE)

            => (station!.EVSEsStatusJSON()["evses"] as JArray)?.
                   FirstOrDefault(evse => evse["id"]?.Value<Byte>() == EVSE)?["status"]?.Value<String>();

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


        #region AnEVSEThatChangesIsToldTheCSMS(Version, Charging)

        /// <summary>
        /// A station that was accepted says how its EVSEs stand - that is in
        /// Connected - and every change afterwards: somebody charges, in the
        /// word of the version, and the EVSE is free again.
        /// </summary>
        [TestCase("1.6", "Charging")]
        [TestCase("2.1", "Occupied")]
        public async Task AnEVSEThatChangesIsToldTheCSMS(String Version, String Charging)
        {

            await Connected(Version);

            var of2 = Told(2).Length;

            Assert.That(Start(1, out var error), Is.True, error);

            Assert.That(await Within(() => Told(1).LastOrDefault() == Charging), Is.True,
                        $"the CSMS was not told that somebody charges at EVSE 1: [{String.Join(", ", Told(1))}]");

            Stop(1);

            Assert.That(await Within(() => Told(1).LastOrDefault() == "Available"), Is.True,
                        $"the CSMS was not told that EVSE 1 is free again: [{String.Join(", ", Told(1))}]");

            Assert.That(Told(2), Has.Length.EqualTo(of2), "the CSMS was told of an EVSE that did not change");

        }

        #endregion

        #region AnEVSEWaitingForARestartIsToldUnavailable(Version)

        /// <summary>
        /// While a restart waits for a session to end, the free EVSE starts
        /// nothing - and the CSMS is told so.
        /// </summary>
        [TestCase("1.6")]
        [TestCase("2.1")]
        public async Task AnEVSEWaitingForARestartIsToldUnavailable(String Version)
        {

            await Connected(Version);

            Assert.That(Start(1, out var error), Is.True, error);

            station!.Restart(false, "'alice' on the web page");

            Assert.That(await Within(() => Told(2).LastOrDefault() == "Unavailable"), Is.True,
                        $"the CSMS was not told that EVSE 2 starts nothing: [{String.Join(", ", Told(2))}]");

        }

        #endregion

        #region ATriggerMessageHasItToldAgain(Version)

        /// <summary>
        /// Asked by a TriggerMessage, the station says yes and sends it: how
        /// every EVSE stands, or one of them; a Heartbeat; a BootNotification.
        /// An EVSE it does not have is refused.
        /// </summary>
        [TestCase("1.6")]
        [TestCase("2.1")]
        public async Task ATriggerMessageHasItToldAgain(String Version)
        {

            await Connected(Version);

            var of1 = Told(1).Length;
            var of2 = Told(2).Length;

            Assert.That(await Trigger(Version, "StatusNotification"), Is.EqualTo("Accepted"));

            Assert.That(await Within(() => Told(1).Length == of1 + 1 && Told(2).Length == of2 + 1), Is.True,
                        $"the CSMS was not told again how both EVSEs stand: {Told(1).Length - of1} and {Told(2).Length - of2} more");

            Assert.That(await Trigger(Version, "StatusNotification", 2), Is.EqualTo("Accepted"));

            Assert.That(await Within(() => Told(2).Length == of2 + 2), Is.True, "the CSMS was not told again how EVSE 2 stands");

            Assert.That(await Trigger(Version, "StatusNotification", 9), Is.EqualTo("Rejected"), "an EVSE the station does not have");

            await Task.Delay(TimeSpan.FromMilliseconds(300));

            Assert.That(Told(1), Has.Length.EqualTo(of1 + 1), "the CSMS was told of EVSE 1, which nobody asked for again");

            var heartbeats = Sent("Heartbeat");

            Assert.That(await Trigger(Version, "Heartbeat"), Is.EqualTo("Accepted"));
            Assert.That(await Within(() => Sent("Heartbeat") > heartbeats), Is.True, "no Heartbeat was sent");

            var boots = Sent("BootNotification");

            Assert.That(await Trigger(Version, "BootNotification"), Is.EqualTo("Accepted"));
            Assert.That(await Within(() => Sent("BootNotification") > boots), Is.True, "no BootNotification was sent");

            if (Version == "2.1")
                Assert.That(csms!.Requests("BootNotification").Last()[3]["reason"]?.Value<String>(), Is.EqualTo("Triggered"));

            // Registered again, it says how its EVSEs stand once more.
            Assert.That(await Within(() => Told(1).Length > of1 + 1), Is.True, "a station accepted again did not say how its EVSEs stand");

        }

        #endregion

        #region ThePageAndTheCommandLineHaveItToldAgain(Version)

        /// <summary>
        /// What the web page and the command line ask for: the same three,
        /// named however they are typed, and to how many back ends each goes.
        /// </summary>
        [TestCase("1.6")]
        [TestCase("2.1")]
        public async Task ThePageAndTheCommandLineHaveItToldAgain(String Version)
        {

            await Connected(Version);

            var of1 = Told(1).Length;

            Assert.That(station!.TrySendAgain("statusnotification", "'alice' on the web page", out var result, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(result!.Value<String>("message"),   Is.EqualTo("StatusNotification"));
                Assert.That(result!.Value<Int32> ("backEnds"),  Is.EqualTo(1));
            });

            Assert.That(await Within(() => Told(1).Length == of1 + 1), Is.True, "the CSMS was not told again how EVSE 1 stands");

            var heartbeats = Sent("Heartbeat");

            var said = await new SendCommand(new StationCLI(station)).Execute([ SendCommand.CommandName, "heart" ], CancellationToken.None);

            Assert.That(said, Is.EqualTo(new[] { "A Heartbeat goes to 1 back end(s)." }));
            Assert.That(await Within(() => Sent("Heartbeat") > heartbeats), Is.True, "no Heartbeat was sent");

            var boots = Sent("BootNotification");

            said = await new SendCommand(new StationCLI(station)).Execute([ SendCommand.CommandName, "BOOT" ], CancellationToken.None);

            Assert.That(said, Is.EqualTo(new[] { "A BootNotification goes to 1 back end(s)." }));
            Assert.That(await Within(() => Sent("BootNotification") > boots), Is.True, "no BootNotification was sent");

            Assert.That(station.TrySendAgain("MeterValues", "'alice' on the web page", out _, out error), Is.False);
            Assert.That(error, Does.Contain("StatusNotification"));

            said = await new SendCommand(new StationCLI(station)).Execute([ SendCommand.CommandName ], CancellationToken.None);

            Assert.That(said[0], Is.EqualTo(SendCommand.Usage));

        }

        #endregion

        #region WithoutABackEndNobodyIsTold()

        /// <summary>
        /// A station that dials nobody says so, and sends nothing.
        /// </summary>
        [Test]
        public async Task WithoutABackEndNobodyIsTold()
        {

            station = await TestStations.Started(station!, directory);

            Assert.Multiple(() => {

                Assert.That(station.TrySendAgain("StatusNotification", "'alice' on the web page", out _, out var status), Is.False);
                Assert.That(status, Does.Contain("No back end"));

                Assert.That(station.TrySendAgain("BootNotification", "'alice' on the web page", out _, out var boot), Is.False);
                Assert.That(boot, Does.Contain("No back end"));

            });

        }

        #endregion

        #region ACableIsLetGoOfOnlyWhereNobodyCharges(Version, Charging, Unknown)

        /// <summary>
        /// An UnlockConnector lets go of the cable of a free EVSE. Where
        /// somebody charges it is refused, in the word the version has for
        /// that, and the session goes on; an EVSE the station does not have
        /// is refused too. The page and the command line are told the same.
        /// </summary>
        [TestCase("1.6", "UnlockFailed",                  "NotSupported")]
        [TestCase("2.1", "OngoingAuthorizedTransaction",  "UnknownConnector")]
        public async Task ACableIsLetGoOfOnlyWhereNobodyCharges(String Version, String Charging, String Unknown)
        {

            await Connected(Version);

            Assert.That(await UnlockOverOCPP(Version, 1), Is.EqualTo("Unlocked"));
            Assert.That(await UnlockOverOCPP(Version, 9), Is.EqualTo(Unknown));

            Assert.That(Start(1, out var error), Is.True, error);

            Assert.That(await UnlockOverOCPP(Version, 1), Is.EqualTo(Charging));
            Assert.That(StatusOf(1), Is.EqualTo("occupied"), "letting go of a cable ended the session");

            Assert.Multiple(() => {
                Assert.That(station!.Unlock(1, null, "'alice' on the web page"),  Is.EqualTo(UnlockResult.Charging));
                Assert.That(station!.Unlock(2, 1,    "'alice' on the web page"),  Is.EqualTo(UnlockResult.Unlocked));
                Assert.That(station!.Unlock(2, 2,    "'alice' on the web page"),  Is.EqualTo(UnlockResult.UnknownConnector));
                Assert.That(station!.Unlock(9, null, "'alice' on the web page"),  Is.EqualTo(UnlockResult.UnknownEVSE));
            });

            var unlock = new UnlockCommand(new StationCLI(station!));

            var held   = await unlock.Execute([ UnlockCommand.CommandName, "1" ], CancellationToken.None);
            var let    = await unlock.Execute([ UnlockCommand.CommandName, "2" ], CancellationToken.None);
            var usage  = await unlock.Execute([ UnlockCommand.CommandName ],      CancellationToken.None);

            Assert.Multiple(() => {
                Assert.That(held,      Is.EqualTo(new[] { "Somebody is charging at EVSE 1: stop the session first." }));
                Assert.That(let,       Is.EqualTo(new[] { "The cable at EVSE 2 was let go of." }));
                Assert.That(usage[0],  Is.EqualTo(UnlockCommand.Usage));
            });

            Assert.That(StatusOf(1), Is.EqualTo("occupied"), "letting go of a cable ended the session");

        }

        #endregion

    }

}
