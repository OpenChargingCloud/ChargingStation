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

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What somebody types into the box that asks for a name, and which server
    /// gets asked about it.
    /// </summary>
    /// <remarks>
    /// Nothing here asks a real name server. What is measured is the part that
    /// happens before anything leaves the station: turning what was typed into
    /// something that can be asked, and picking which of the configured
    /// servers is going to be asked it.
    /// </remarks>
    [TestFixture]
    public class DNSQuestionTests
    {

        #region AnAddressIsTurnedAround(Typed, Expected)

        /// <summary>
        /// An address typed into a box that asks for a name means "who is
        /// this", and that is a question about a different name.
        /// </summary>
        /// <remarks>
        /// Asking for "192.168.178.1" as written sends it out as a domain name
        /// and comes back with nothing, which reads as a broken resolver
        /// rather than as a misunderstanding. The cases below are the ones that
        /// go wrong when this is written by hand: the octets have to be
        /// reversed, and for IPv6 it is every nibble reversed rather than every
        /// byte.
        /// </remarks>
        [Test]
        [TestCase("192.168.178.1",  "1.178.168.192.in-addr.arpa")]
        [TestCase("8.8.8.8",        "8.8.8.8.in-addr.arpa")]
        [TestCase("127.0.0.1",      "1.0.0.127.in-addr.arpa")]
        [TestCase("  1.2.3.4  ",    "4.3.2.1.in-addr.arpa")]
        [TestCase("2001:db8::1",
                  "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.8.b.d.0.1.0.0.2.ip6.arpa")]
        [TestCase("::1",
                  "1.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.0.ip6.arpa")]
        public void AnAddressIsTurnedAround(String Typed, String Expected)
        {

            Assert.That(ChargingStation.AsAQuestion(Typed, out var name, out var says), Is.True,
                        $"'{Typed}' was not recognised as an address.");

            Assert.Multiple(() => {

                Assert.That(name, Is.EqualTo(Expected));

                Assert.That(says, Is.Not.Null.And.Contains(Expected),
                            "The page is not told that something other than what was typed was asked for.");

            });

        }

        #endregion

        #region ANameIsLeftAlone(Typed)

        /// <summary>
        /// Anything that is not an address is asked for as written.
        /// </summary>
        [Test]
        [TestCase("example.org")]
        [TestCase("ptbtime1.ptb.de")]
        [TestCase("1.2.3.4.example.org")]
        [TestCase("999.999.999.999")]
        public void ANameIsLeftAlone(String Typed)
        {

            Assert.That(ChargingStation.AsAQuestion(Typed, out var name, out var says), Is.False,
                        $"'{Typed}' was mistaken for an address.");

            Assert.Multiple(() => {
                Assert.That(name, Is.EqualTo(Typed.Trim()));
                Assert.That(says, Is.Null, "Something was said about a name that was asked for as written.");
            });

        }

        #endregion

        #region AServerThisStationDoesNotHaveIsRefused()

        /// <summary>
        /// Asking the fourth name server of a station that has two.
        /// </summary>
        /// <remarks>
        /// Which happens by itself: the page offers one button per server, and
        /// somebody else can take a server away between the page being drawn
        /// and the button being pressed. It is counted from one in the sentence
        /// because that is how the page numbers them.
        /// </remarks>
        [Test]
        public async Task AServerThisStationDoesNotHaveIsRefused()
        {

            var directory = TestStations.TemporaryDirectory("dns-question");
            var station   = TestStations.New(directory);

            try
            {

                var result = await station.ResolveAsync("example.org", null, 99);

                Assert.Multiple(() => {
                    Assert.That(result.Value<Boolean>("ok"),   Is.False);
                    Assert.That(result.Value<String>("error"), Does.Contain("no name server number 100"));
                });

            }
            finally
            {
                await station.DisposeAsync();
                TestStations.Remove(directory);
            }

        }

        #endregion

    }

}
