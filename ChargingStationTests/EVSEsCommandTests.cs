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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.CLI;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;

using cloud.charging.open.ChargingStation.CommandLine;
using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// evses at the station's command line: how each EVSE stands, and at what
    /// power a car is charging - simulated here, where no meter is read; what
    /// a meter measures is in MeterTests.
    /// </summary>
    [TestFixture]
    public class EVSEsCommandTests : AChargingStationTests
    {

        #region Data

        public const String  CardOfKnown  = "04A2112233";

        private readonly KioskTests.MovableClock clock = new (DateTimeOffset.UtcNow);

        #endregion

        #region A station with two EVSEs, a card reader and a clock a test can move

        protected override TimeProvider? Clock
            => clock;

        protected override JObject Configuration
            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("groups", new JObject(
                           new JProperty("legal", new JObject(
                               new JProperty("enabled", false)
                           ))
                       ))
                   )),
                   new JProperty("evses", new JArray(
                       new JObject(
                           new JProperty("id",                  1),
                           new JProperty("connectors",          new JArray("sType2")),
                           new JProperty("maxPower_kW",         22.0),
                           new JProperty("operative",           true),
                           new JProperty("physicalReference",   "A")
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

        private Task<String[]> EVSEs(params String[] Arguments)
            => new EVSEsCommand(new StationCLI(Station)).Execute([ EVSEsCommand.CommandName, .. Arguments ], CancellationToken.None);

        private void StartWithTheCard(Byte EVSEId)
            => Assert.That(Station.TryPresentToken("reader", EVSEId, CardOfKnown, out _, out var error), Is.True, error);

        private void TakeOutOfService(Byte EVSEId)
        {

            var evses = new JArray(
                            Station.EVSEs.Select(evse => {
                                var json = evse.ToJSON();
                                if (evse.Id == EVSEId)
                                    json["operative"] = false;
                                return json;
                            })
                        );

            Assert.That(Station.TryUpdateEVSEConfiguration(new JObject(new JProperty("evses", evses)), _ => true, out _, out var error, out _),
                        Is.True, $"Taking EVSE {EVSEId} out of service failed: {error}");

        }

        #endregion


        #region EveryEVSEIsSaidByItsPhysicalReference()

        /// <summary>
        /// Each EVSE on a line of its own, by what is written on it, free and
        /// with the most it can deliver.
        /// </summary>
        [Test]
        public async Task EveryEVSEIsSaidByItsPhysicalReference()
        {

            Assert.That(await EVSEs(), Is.EqualTo(new[] {
                "A  available, up to 22 kW",
                "B  available, up to 150 kW"
            }));

        }

        #endregion

        #region ACarChargingIsSaidWithItsPower()

        /// <summary>
        /// While a car charges, its EVSE says at what power, since when, how it
        /// was started and whose customer it is - and that the power is
        /// simulated, where no meter is read. The power follows the display's
        /// ramp: half of the EVSE's 22 kW after half of the ramp.
        /// </summary>
        [Test]
        public async Task ACarChargingIsSaidWithItsPower()
        {

            StartWithTheCard(1);

            var started = clock.Now.ToLocalTime().ToString("HH:mm");

            clock.Now += ChargingSession.RampUpTime / 2;

            var lines = await EVSEs();

            Assert.Multiple(() => {
                Assert.That(lines[0], Is.EqualTo($"A  charging at 11 kW of 22 kW, simulated - no meter is read, since {started} (0 min), RFID, Elektro Mobil GmbH"));
                Assert.That(lines[1], Is.EqualTo("B  available, up to 150 kW"), "the other EVSE is still free");
            });

            clock.Now += TimeSpan.FromMinutes(14);

            Assert.That((await EVSEs())[0], Does.StartWith("A  charging at 22 kW of 22 kW, simulated - no meter is read, since ").
                                            And.Contains("(14 min)"));

        }

        #endregion

        #region AnEVSEOutOfServiceIsSaidSo()

        /// <summary>
        /// An EVSE out of service says so; one taken out of service under a
        /// running session goes on charging, and says when it goes.
        /// </summary>
        [Test]
        public async Task AnEVSEOutOfServiceIsSaidSo()
        {

            TakeOutOfService(2);

            Assert.That((await EVSEs())[1], Is.EqualTo("B  out of service"));

            StartWithTheCard(1);
            TakeOutOfService(1);

            Assert.That((await EVSEs())[0], Does.StartWith("A  charging at ").
                                            And.EndWith("; out of service once this session ends"));

        }

        #endregion

        #region TheSupportSeesHowEachEVSEStands()

        /// <summary>
        /// Over SSH, the support may ask how each EVSE stands: that is what is
        /// going on, which the support sees, and nothing of how the station is
        /// set up, which it does not.
        /// </summary>
        [Test]
        public async Task TheSupportSeesHowEachEVSEStands()
        {

            await Become("support", "systemadmin");

            Assert.That(Station.ExtAPI.TryGetUser(User_Id.Parse(ChargingStation.DefaultAdminUser), out var stored) && stored is User, Is.True);

            var account = (User) stored!;
            var session = new SshSessionInfo("42", account.Id.ToString(), "publickey", null, null, null, null, SshSessionRestrictions.None);

            await using var terminal  = new VT100Terminal((bytes, cancellationToken) => ValueTask.CompletedTask);
            using       var cli       = new StationCLI(Station, terminal, new CLICaller(account, session));

            var said = await new EVSEsCommand(cli).Execute([ EVSEsCommand.CommandName ], CancellationToken.None);

            Assert.That(said, Is.Not.Empty);
            Assert.That(said[0], Does.StartWith("A  available"), String.Join(Environment.NewLine, said));

        }

        #endregion

        #region MoreThanTheCommandIsRefused()

        [Test]
        public async Task MoreThanTheCommandIsRefused()
        {

            Assert.That((await EVSEs("A")).Single(), Is.EqualTo("Usage: evses"));

        }

        #endregion

    }

}
