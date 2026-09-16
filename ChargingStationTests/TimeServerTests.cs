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


        #region AnAddressIsTakenAsOneOfTheNamedServers()

        /// <summary>
        /// An address is a server to be asked, not something to refuse.
        /// </summary>
        /// <remarks>
        /// A key exchange very commonly names addresses rather than host names
        /// - nts.netnod.se names "2a01:3f7:2:44::9" and nothing else - so this
        /// is the button most people press first. It cannot have a key exchange
        /// of its own, because the TLS certificate is issued for a name; the
        /// exchange therefore stays with the configured host and the time
        /// request is directed at the address with the cookies that exchange
        /// issued.
        ///
        /// What is asserted here is only that the address is accepted as a
        /// target and said to be one, because everything past that point needs
        /// a real key exchange over the network. Where the request actually
        /// goes is Norn's decision and is measured there, against a response
        /// built by hand.
        /// </remarks>
        [Test]
        public async Task AnAddressIsTakenAsOneOfTheNamedServers()
        {

            Assert.That(station!.TryUpdateNTSConfiguration(new JObject(new JProperty("enabled", false)),
                                                           out var error),
                        Is.True, error);

            // Switched off, so nothing leaves the station - but the sentence
            // about what would have been asked is not reached either, which is
            // the point of the second half below.
            var offResult = await station.TestTimeServerAsync("2a01:3f7:2:44::9");

            Assert.That(offResult.Value<Boolean>("ok"), Is.False);

            Assert.That(offResult["steps"]!.Values<JObject>().
                            Any(step => (step!.Value<String>("text") ?? "").Contains("neither a name nor an address")),
                        Is.False,
                        "An address was turned away as if it were not one.");

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
