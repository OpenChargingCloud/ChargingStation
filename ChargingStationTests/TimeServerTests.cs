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

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Asking one time server everything there is to ask.
    /// </summary>
    /// <remarks>
    /// Nothing here reaches a real time server: what is measured is which
    /// question the station decides to put, and what it says when it decides
    /// it cannot put one at all.
    /// </remarks>
    [TestFixture]
    public class TimeServerTests
    {

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("time-server");
            station    = TestStations.New(directory);
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


        #region AnAddressNamedByAKeyExchangeCannotBeAskedOnItsOwn(Named)

        /// <summary>
        /// A key exchange that names an address rather than a host name.
        /// </summary>
        /// <remarks>
        /// Which is the ordinary case rather than an odd one: measured against
        /// nts.netnod.se, the exchange named "2a01:3f7:2:44::9" and nothing
        /// else. The page offers a button per named server, so this is the
        /// button most people will press first, and what it must not do is
        /// fail with something that sounds like a bug in the station.
        ///
        /// An NTS key exchange is a TLS connection whose certificate has to be
        /// checked against a name, so an address cannot be given one of its
        /// own. The sentence says that, and says what does reach that server
        /// instead.
        /// </remarks>
        [Test]
        [TestCase("2a01:3f7:2:44::9")]
        [TestCase("192.53.103.108")]
        public async Task AnAddressNamedByAKeyExchangeCannotBeAskedOnItsOwn(String Named)
        {

            var result = await station!.TestTimeServerAsync(Named);

            var steps  = result["steps"]!.Values<JObject>().ToArray();

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"), Is.False);

                Assert.That(steps.Any(step => (step!.Value<String>("text") ?? "").Contains("issued for a name")),
                            Is.True,
                            "The refusal does not say why an address cannot have a key exchange of its own.");

                Assert.That(steps.Any(step => (step!.Value<String>("text") ?? "").Contains("Sync now")),
                            Is.True,
                            "The refusal does not say what does reach that server instead.");

            });

        }

        #endregion

        #region SomethingThatIsNeitherIsRefusedToo()

        /// <summary>
        /// Not a name and not an address.
        /// </summary>
        [Test]
        public async Task SomethingThatIsNeitherIsRefusedToo()
        {

            var result = await station!.TestTimeServerAsync("not a host at all");

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"), Is.False);
                Assert.That(result["steps"]?.First?.Value<String>("text"),
                            Does.Contain("neither a name nor an address"));
            });

        }

        #endregion

        #region NothingIsAskedWhileTimeSynchronisationIsOff()

        /// <summary>
        /// Switched off means switched off, for the test as much as for the
        /// station's own checks.
        /// </summary>
        /// <remarks>
        /// A test that quietly asked anyway would be a station sending traffic
        /// somebody switched off - and the page would be showing an answer
        /// from a server this station is not using.
        /// </remarks>
        [Test]
        public async Task NothingIsAskedWhileTimeSynchronisationIsOff()
        {

            Assert.That(station!.TryUpdateNTSConfiguration(new JObject(new JProperty("enabled", false)),
                                                           out var error),
                        Is.True, error);

            var result = await station.TestTimeServerAsync();

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"), Is.False);
                Assert.That(result["steps"]?.First?.Value<String>("text"), Does.Contain("switched off"));
            });

        }

        #endregion

    }

}
