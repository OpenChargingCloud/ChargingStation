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

using CSMS = cloud.charging.open.ChargingStation.Tests.TalkingToTheCSMSTests.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A card is asked about at the back end before it starts a session: the
    /// OCPP Authorize, in both versions, and what this station does with the
    /// answer.
    /// </summary>
    /// <remarks>
    /// A station started a session for every UID it was shown, whoever it
    /// was registered with: a UID is no authorization, and this station sent
    /// no Authorize at all. The stand-in CSMS of TalkingToTheCSMSTests is at
    /// the other end, saying of a card what a test tells it to.
    /// </remarks>
    [TestFixture]
    public class AuthorizationTests
    {

        #region Data

        private const String  ACard  = "04A2B3C4D5E6F7";

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {

            directory  = TestStations.TemporaryDirectory("authorize");
            csms       = new CSMS();

            // One outlet, and a reader whose cards are typed in beside it.
            var configuration = TestStations.Offline;

            configuration.Add(new JProperty("evses", new JArray(
                new JObject(
                    new JProperty("id",                 1),
                    new JProperty("connectors",         new JArray("sType2")),
                    new JProperty("maxPower_kW",        22.0),
                    new JProperty("operative",          true),
                    new JProperty("physicalReference",  "A")
                )
            )));

            configuration.Add(new JProperty("rfid", new JArray(
                new JObject(
                    new JProperty("id",       "reader"),
                    new JProperty("kind",     "GraphDefined.FakeRFID"),
                    new JProperty("evse",     1),
                    new JProperty("enabled",  true)
                )
            )));

            station    = TestStations.New(directory, configuration);

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

        #region (private) Registered(Version) / Within(Condition) / Charging

        /// <summary>
        /// The station connected to the CSMS over the given version of OCPP,
        /// and accepted by it.
        /// </summary>
        private async Task Registered(String Version)
        {

            Assert.That(station!.Connections.TryAddConnection($"A CSMS of OCPP {Version}", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: Version),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.DialledConnections[id!].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected to begin with: {started.DialledConnections[id!]}");

            Assert.That(await Within(() => started.RegistrationJSON()[Version]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        "the station did not register");

        }

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

        private Boolean Charging
            => station!.KioskJSON().ToString().Contains("\"occupied\"");

        #endregion


        #region ACardTheBackEndAcceptsStartsASession_AndItsStopIsNotAskedAbout(Version)

        /// <summary>
        /// The card is sent to the back end as the version has it, starts a
        /// session where that one says "Accepted" - and stops it again without
        /// anybody being asked: whoever started a session may end it.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ACardTheBackEndAcceptsStartsASession_AndItsStopIsNotAskedAbout(String Version)
        {

            await Registered(Version);

            var (started, startError) = await station!.PresentToken("reader", null, ACard);

            Assert.That(started, Is.Not.Null, startError);

            var asked = csms!.Requests("Authorize");

            Assert.Multiple(() => {

                Assert.That(started!.Value<Boolean>("started"),  Is.True);
                Assert.That(Charging,                            Is.True);
                Assert.That(asked.Length,                        Is.EqualTo(1), "the back end was not asked about the card exactly once");

                if (Version == "1.6")
                    Assert.That(asked[0][3]?.Value<String>("idTag"),                   Is.EqualTo(ACard));

                else
                {
                    Assert.That(asked[0][3]?["idToken"]?.Value<String>("idToken"),     Is.EqualTo(ACard));
                    Assert.That(asked[0][3]?["idToken"]?.Value<String>("type"),        Is.EqualTo("ISO14443"));
                }

            });

            // The back end changes its mind, and the card still stops what it started.
            csms.CardStatus = "Blocked";

            var (stopped, stopError) = await station.PresentToken("reader", null, ACard);

            Assert.Multiple(() => {
                Assert.That(stopped,                              Is.Not.Null, stopError);
                Assert.That(stopped?.Value<Boolean>("stopped"),   Is.True);
                Assert.That(Charging,                             Is.False);
                Assert.That(csms.Requests("Authorize").Length,    Is.EqualTo(1), "the back end was asked about a stop");
            });

        }

        #endregion

        #region ACardTheBackEndTurnsAwayStartsNothing(Version, Status)

        /// <summary>
        /// Whatever the back end says that is not "Accepted": nothing starts,
        /// at a reader and for a local app alike, and whoever held the card up
        /// is told what the back end said.
        /// </summary>
        [TestCase("2.1", "Blocked")]
        [TestCase("2.1", "Unknown")]
        [TestCase("1.6", "Invalid")]
        [TestCase("1.6", "Expired")]
        public async Task ACardTheBackEndTurnsAwayStartsNothing(String Version, String Status)
        {

            await Registered(Version);

            csms!.CardStatus = Status;

            var (atTheReader, readerError)          = await station!.PresentToken("reader", null, ACard);
            var (inTheApp,    refusal, appError)    = await station. StartLocally(null, ACard);

            Assert.Multiple(() => {
                Assert.That(atTheReader,                         Is.Null, "a card the back end turned away started a session at the reader");
                Assert.That(readerError,                         Does.Contain(Status));
                Assert.That(inTheApp,                            Is.Null, "a card the back end turned away started a session for the app");
                Assert.That(refusal,                             Is.EqualTo(LocalAppRefusal.NotAuthorized));
                Assert.That(appError,                            Does.Contain(Status));
                Assert.That(Charging,                            Is.False);
                Assert.That(csms.Requests("Authorize").Length,   Is.EqualTo(2));
            });

        }

        #endregion

        #region ABackEndThatAnswersWithAnErrorStartsNothing(Version)

        /// <summary>
        /// A back end that was asked and gave no answer to the question is not
        /// a back end that said yes: the card is turned away, and the next one
        /// is asked about again.
        /// </summary>
        [TestCase("2.1")]
        [TestCase("1.6")]
        public async Task ABackEndThatAnswersWithAnErrorStartsNothing(String Version)
        {

            await Registered(Version);

            csms!.RefuseOnce("Authorize");

            var (refused, refusal, why) = await station!.StartLocally(null, ACard);

            Assert.Multiple(() => {
                Assert.That(refused,   Is.Null, "a card started a session on an error");
                Assert.That(refusal,   Is.EqualTo(LocalAppRefusal.BackEndSilent));
                Assert.That(why,       Does.Contain("did not answer").Or.Contain("could not be asked"));
                Assert.That(Charging,  Is.False);
            });

            var (started, _, error) = await station.StartLocally(null, ACard);

            Assert.That(started, Is.Not.Null, error);

        }

        #endregion

        #region AStationWithoutABackEndStartsACardByItself()

        /// <summary>
        /// A station that dials nobody has nobody to ask, and lets a card start
        /// as it did before there was an Authorize.
        /// </summary>
        [Test]
        public async Task AStationWithoutABackEndStartsACardByItself()
        {

            station = await TestStations.Started(station!, directory);

            var (started, error) = await station.PresentToken("reader", null, ACard);

            Assert.Multiple(() => {
                Assert.That(started,                              Is.Not.Null, error);
                Assert.That(Charging,                             Is.True);
                Assert.That(csms!.Requests("Authorize"),          Is.Empty);
            });

        }

        #endregion

        #region AStationItsBackEndDidNotAcceptAsksNothingOfIt()

        /// <summary>
        /// A back end that has not accepted this station is told nothing but
        /// the BootNotification - a card neither - and the station is on its
        /// own with it.
        /// </summary>
        [Test]
        public async Task AStationItsBackEndDidNotAcceptAsksNothingOfIt()
        {

            csms!.BootStatus = "Pending";

            Assert.That(station!.Connections.TryAddConnection("A CSMS that waits", $"ws://127.0.0.1:{csms.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: "2.1"),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Pending"), Is.True,
                        "the station was not told to wait");

            var (session, startError) = await station.PresentToken("reader", null, ACard);

            Assert.Multiple(() => {
                Assert.That(session,                      Is.Not.Null, startError);
                Assert.That(csms.Requests("Authorize"),   Is.Empty, "a back end that did not accept the station was asked about a card");
            });

        }

        #endregion

        #region ACardThatWouldBeTurnedAwayHereAnywayIsNotAskedAbout()

        /// <summary>
        /// An outlet out of service turns every card away, and the back end
        /// is not asked about a card that could start nothing.
        /// </summary>
        [Test]
        public async Task ACardThatWouldBeTurnedAwayHereAnywayIsNotAskedAbout()
        {

            await Registered("2.1");

            var outOfService = new JObject(
                                   new JProperty("evses", new JArray(
                                       station!.EVSEs.Select(evse => {
                                           var json = evse.ToJSON();
                                           json["operative"] = false;
                                           return json;
                                       })
                                   ))
                               );

            var before = csms!.Requests("Authorize").Length;

            Assert.That(station.TryUpdateEVSEConfiguration(outOfService, _ => true, out _, out var error, out _), Is.True, error);

            var (session, why) = await station.PresentToken("reader", null, ACard);

            Assert.Multiple(() => {
                Assert.That(session,                              Is.Null);
                Assert.That(why,                                  Does.Contain("out of service"));
                Assert.That(csms.Requests("Authorize").Length,    Is.EqualTo(before));
            });

        }

        #endregion

    }

}
