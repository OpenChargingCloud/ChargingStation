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

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// How each EVSE stands, as the EVSEs page of the web interface shows it:
    /// asked once when the page opens, and every change after that on the event
    /// stream - said once, when it happens, and not again while nothing changes.
    /// </summary>
    [TestFixture]
    public class EVSEStatusTests : AChargingStationTests
    {

        #region Data

        public const String  CardOfKnown  = "04A2112233";

        #endregion

        #region A station with two EVSEs, a card reader and a meter on one of them

        protected override JObject Configuration
            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   )),
                   new JProperty("evses", new JArray(
                       new JObject(
                           new JProperty("id",                  1),
                           new JProperty("connectors",          new JArray("sType2")),
                           new JProperty("maxPower_kW",         22.0),
                           new JProperty("operative",           true),
                           new JProperty("physicalReference",   "A"),
                           new JProperty("meterType",           "SunSpec 213"),
                           new JProperty("meterSerialNumber",   "EnergyMeter01")
                       ),
                       new JObject(
                           new JProperty("id",                  2),
                           new JProperty("connectors",          new JArray("cCCS2")),
                           new JProperty("maxPower_kW",         150.0),
                           new JProperty("operative",           true),
                           new JProperty("physicalReference",   "B")
                       )
                   )),
                   new JProperty("rfid", new JArray(
                       new JObject(
                           new JProperty("id",       "reader"),
                           new JProperty("kind",     "GraphDefined.FakeRFID"),
                           new JProperty("evse",     null),
                           new JProperty("enabled",  true)
                       )
                   )),
                   new JProperty("operator", new JObject(
                       new JProperty("name",      "Stadtwerke Musterstadt"),
                       new JProperty("language",  "de"),
                       new JProperty("emps",      new JArray(
                           new JObject(
                               new JProperty("id",             "emp-one"),
                               new JProperty("name",           "Elektro Mobil GmbH"),
                               new JProperty("tokenPrefixes",  new JArray("04A2"))
                           )
                       ))
                   ))
               );

        #endregion

        #region (private) Helpers

        private static JObject EVSE(JObject Status, Byte Id)

            => (Status["evses"] as JArray)!.
                   OfType<JObject>().
                   First(evse => evse.Value<Byte>("id") == Id);

        private JObject EVSENow(Byte Id)
            => EVSE(Station.EVSEsStatusJSON(), Id);

        private void StartWithTheCard(Byte EVSEId)
            => Assert.That(Station.TryPresentToken("reader", EVSEId, CardOfKnown, out _, out var error), Is.True, error);

        #endregion


        #region What each EVSE says

        [Test]
        public void EveryEVSEIsListedByItsPhysicalReferenceAndFree()
        {

            var status = Station.EVSEsStatusJSON();

            Assert.Multiple(() => {
                Assert.That(status.Value<String>("timestamp"),                Is.Not.Null.And.Not.Empty);
                Assert.That((status["evses"] as JArray)!.Count,               Is.EqualTo(2));
                Assert.That(EVSE(status, 1).Value<String>("label"),           Is.EqualTo("A"));
                Assert.That(EVSE(status, 2).Value<String>("label"),           Is.EqualTo("B"));
                Assert.That(EVSE(status, 1).Value<String>("status"),          Is.EqualTo("available"));
                Assert.That(EVSE(status, 2).Value<String>("status"),          Is.EqualTo("available"));
                Assert.That(EVSE(status, 1)["session"]!.Type,                 Is.EqualTo(JTokenType.Null));
                Assert.That(EVSE(status, 2)["connectors"]![0]!.Value<String>("type"), Is.EqualTo("cCCS2"));
            });

        }

        [Test]
        public void TheMeterIsSaidWhereThereIsOneAndNotWhereThereIsNone()
        {

            Assert.Multiple(() => {
                Assert.That(EVSENow(1)["meter"]!.Value<String>("type"),          Is.EqualTo("SunSpec 213"));
                Assert.That(EVSENow(1)["meter"]!.Value<String>("serialNumber"),  Is.EqualTo("EnergyMeter01"));
                Assert.That(EVSENow(2)["meter"]!.Type,                           Is.EqualTo(JTokenType.Null));
            });

        }

        [Test]
        public void ASessionIsSaidWithHowItWasStartedAndWhoseCustomerItIs()
        {

            StartWithTheCard(1);

            var evse = EVSENow(1);

            Assert.Multiple(() => {
                Assert.That(evse.Value<String>("status"),                        Is.EqualTo("occupied"));
                Assert.That(evse["session"]!.Value<String>("method"),            Is.Not.Null.And.Not.Empty);
                Assert.That(evse["session"]!.Value<String>("startedAt"),         Is.Not.Null.And.Not.Empty);
                Assert.That(evse["session"]!["provider"]!.Value<String>("name"), Is.EqualTo("Elektro Mobil GmbH"));
                Assert.That(EVSENow(2).Value<String>("status"),                  Is.EqualTo("available"), "the other EVSE is still free");
            });

        }

        [Test]
        public void TheCardsUIDIsNotSaid()
        {

            StartWithTheCard(1);

            Assert.That(Station.EVSEsStatusJSON().ToString().Contains(CardOfKnown, StringComparison.Ordinal), Is.False,
                        "The card's UID is in what goes out to every signed-in browser.");

        }

        [Test]
        public void AHeldEVSEIsSaidReserved()
        {

            Assert.That(
                Station.TryReserveNow(
                    new JObject(
                        new JProperty("evse",     2),
                        new JProperty("idToken",  CardOfKnown),
                        new JProperty("minutes",  15)
                    ),
                    out _,
                    out var holding
                ),
                Is.True,
                holding
            );

            Assert.Multiple(() => {
                Assert.That(EVSENow(2).Value<String>("status"),  Is.EqualTo("reserved"));
                Assert.That(EVSENow(2)["reservation"]!.Type,      Is.EqualTo(JTokenType.Object));
            });

        }

        #endregion

        #region Said when it changes, and only then

        [Test]
        public void AChangeIsSaidOnceAndNothingWhileNothingChanges()
        {

            var heard = new List<JObject>();
            Station.EVSEsChanged += heard.Add;

            // Whatever the timer found since the station started.
            Station.ReportEVSEChanges();
            heard.Clear();

            Assert.That(Station.ReportEVSEChanges(), Is.False, "nothing changed, yet a change was said");

            StartWithTheCard(1);

            Assert.Multiple(() => {
                Assert.That(Station.ReportEVSEChanges(),                Is.True,  "a session started, and nobody was told");
                Assert.That(Station.ReportEVSEChanges(),                Is.False, "the same session was said twice");
                Assert.That(heard,                                      Has.Count.EqualTo(1));
                Assert.That(EVSE(heard[0], 1).Value<String>("status"),  Is.EqualTo("occupied"));
                Assert.That(heard[0]["timestamp"],                      Is.Null, "a timestamp would make every look a change");
            });

        }

        [Test]
        public async Task TheStationLooksByItselfAndTellsTheEventStream()
        {

            using var http    = await SignedIn();
            using var events  = await EventStream.OpenAndSettle(Station, http);

            StartWithTheCard(1);

            Assert.That(await events.ReadUntil("occupied"), Is.True,
                        "The session that started never reached the event stream.");

            Assert.That(events.Count("event: evses"), Is.GreaterThanOrEqualTo(1),
                        "It came down the stream, but not as an \"evses\" event.");

        }

        #endregion

        #region Over the JSON API

        [Test]
        public async Task TheJSONAPISaysWhatTheStationSays()
        {

            StartWithTheCard(2);

            using var http  = await SignedIn();
            var answer      = await GetJSON(http, "api/v1/status/evses");

            Assert.Multiple(() => {
                Assert.That(EVSE(answer, 2).Value<String>("status"),  Is.EqualTo("occupied"));
                Assert.That(EVSE(answer, 1).Value<String>("status"),  Is.EqualTo("available"));
            });

        }

        #endregion

    }

}
