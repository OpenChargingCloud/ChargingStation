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

using System.Net;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a station says about the wire below the charging cable, and what
    /// happens when somebody changes it from the web interface.
    /// </summary>
    /// <remarks>
    /// Switched off throughout, and that is not a gap being left. Switching it
    /// on opens a raw socket, binds UDP 15118 and joins an IPv6 multicast
    /// group on whatever machine the suite runs on, which is a thing to do on
    /// a bench with a powerline modem and not in a test run. So what is
    /// measured here is the configuration reaching the station and coming back
    /// out; that the listener itself answers a vehicle is a question for a
    /// bench, and the station's log is where the answer shows up.
    ///
    /// Every station in this fixture therefore comes up with nothing below the
    /// cable, which is also the shape the page has to render without pretending
    /// something is wrong - a station that has not been asked for V2G is not a
    /// station whose V2G failed.
    /// </remarks>
    public class V2GConfigurationAPITests : AChargingStationTests
    {

        #region Data

        private const String Resource = "/api/v1/configuration/v2g";

        #endregion

        #region SetUp

        /// <summary>
        /// A file that says something about every field, so that a station
        /// reading none of them would be visible rather than merely equal to
        /// the defaults.
        /// </summary>
        protected override JObject Configuration

            => new (

                   new JProperty("nts", new JObject(
                       new JProperty("enabled",    false)
                   )),

                   new JProperty("v2g", new JObject(
                       new JProperty("enabled",    false),
                       new JProperty("sdp",        true),
                       new JProperty("loopback",   true),
                       new JProperty("interface",  "eth-from-the-file"),
                       new JProperty("port",       15118),
                       new JProperty("evseId",     "DE*GEF*E0007*3"),
                       new JProperty("slac",       "none")
                   ))

               );

        #endregion


        #region TheStationReadsTheV2GSectionOfItsFile()

        /// <summary>
        /// The section in the file is what the station says it was told, which
        /// before this existed it could not be told at all.
        /// </summary>
        [Test]
        public async Task TheStationReadsTheV2GSectionOfItsFile()
        {

            using var http = await SignedIn();

            var v2g = await GetJSON(http, Resource);

            Assert.Multiple(() => {
                Assert.That(v2g.Value<Boolean>("enabled"),    Is.False);
                Assert.That(v2g.Value<Boolean>("sdp"),        Is.True);
                Assert.That(v2g.Value<Boolean>("loopback"),   Is.True);
                Assert.That(v2g.Value<String> ("interface"),  Is.EqualTo("eth-from-the-file"));
                Assert.That(v2g.Value<Int32>  ("port"),       Is.EqualTo(15118));
                Assert.That(v2g.Value<String> ("evseId"),     Is.EqualTo("DE*GEF*E0007*3"));
                Assert.That(v2g.Value<String> ("slac"),       Is.EqualTo("none"));
            });

        }

        #endregion

        #region NothingIsUpBelowTheCableWhenNobodyAskedForIt()

        /// <summary>
        /// Switched off, the link is simply not there - and the page has to be
        /// able to tell that apart from a link that was asked for and failed.
        /// </summary>
        [Test]
        public async Task NothingIsUpBelowTheCableWhenNobodyAskedForIt()
        {

            using var http = await SignedIn();

            var v2g = await GetJSON(http, Resource);

            Assert.Multiple(() => {
                Assert.That(v2g.Value<Boolean>("running"),  Is.True,  "This station has been started.");
                Assert.That(v2g["link"]?.Type,              Is.EqualTo(JTokenType.Null).Or.Null,
                            "Something came up below the cable in a test run.");
            });

        }

        #endregion

        #region TheAnswerCarriesWhatThePageCannotKnowByItself()

        /// <summary>
        /// The transports to offer in the picker, whether there is a
        /// certificate, and which file all of this is written to.
        /// </summary>
        /// <remarks>
        /// The certificate is the interesting one. It is not settable from the
        /// page - it is a file and a password - and it is the only thing that
        /// decides whether the endpoint speaks TLS and therefore what SDP
        /// advertises to every vehicle that asks. A page that could not say so
        /// would leave somebody looking for a TLS switch that does not exist.
        /// </remarks>
        [Test]
        public async Task TheAnswerCarriesWhatThePageCannotKnowByItself()
        {

            using var http = await SignedIn();

            var v2g = await GetJSON(http, Resource);

            Assert.Multiple(() => {

                Assert.That(v2g["slacTransports"]?.Values<String>(),
                            Is.EquivalentTo(new[] { "none", "auto", "afpacket", "udp" }));

                Assert.That(v2g.Value<Boolean>("certificate"),  Is.False,
                            "No certificate was given to a test station.");

                Assert.That(v2g.Value<String>("file"),  Does.EndWith("configuration.json"));

            });

        }

        #endregion

        #region ChangingTheSettingsChangesWhatTheStationSays()

        [Test]
        public async Task ChangingTheSettingsChangesWhatTheStationSays()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                               Resource,
                               JSONBody(
                                   new JProperty("sdp",        false),
                                   new JProperty("loopback",   false),
                                   new JProperty("interface",  "eth-from-the-page"),
                                   new JProperty("port",       0),
                                   new JProperty("evseId",     "DE*GEF*E0009*1"),
                                   new JProperty("slac",       "udp")
                               )
                           );

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"The V2G settings were refused with {(Int32) response.StatusCode}.");

            var answered = JObject.Parse(await response.Content.ReadAsStringAsync());
            var asked    = await GetJSON(http, Resource);

            Assert.Multiple(() => {

                // What the PUT answered and what a fresh GET says are the same
                // thing, because the answer is the configuration and not a
                // receipt for it.
                Assert.That(answered.Value<Boolean>("sdp"),        Is.False);
                Assert.That(answered.Value<String> ("interface"),  Is.EqualTo("eth-from-the-page"));
                Assert.That(answered.Value<Int32>  ("port"),       Is.EqualTo(0));

                Assert.That(asked.Value<Boolean>("sdp"),        Is.False);
                Assert.That(asked.Value<Boolean>("loopback"),   Is.False);
                Assert.That(asked.Value<String> ("interface"),  Is.EqualTo("eth-from-the-page"));
                Assert.That(asked.Value<Int32>  ("port"),       Is.EqualTo(0));
                Assert.That(asked.Value<String> ("evseId"),     Is.EqualTo("DE*GEF*E0009*1"));
                Assert.That(asked.Value<String> ("slac"),       Is.EqualTo("udp"));

                // Nothing was said about it, so it stayed off - and nothing
                // opened a socket on the way.
                Assert.That(asked.Value<Boolean>("enabled"),  Is.False);
                Assert.That(asked["link"]?.Type,              Is.EqualTo(JTokenType.Null).Or.Null);

            });

        }

        #endregion

        #region AChangeSurvivesIntoTheFile()

        /// <summary>
        /// Saved means written down, not only held in memory until the next
        /// restart.
        /// </summary>
        [Test]
        public async Task AChangeSurvivesIntoTheFile()
        {

            using var http = await SignedIn();

            await http.PutAsync(Resource, JSONBody(new JProperty("evseId", "DE*GEF*E0042*9")));

            var onDisk = JObject.Parse(await File.ReadAllTextAsync(
                                           Path.Combine(Directory, "configuration.json")));

            Assert.That(onDisk["v2g"]?.Value<String>("evseId"),  Is.EqualTo("DE*GEF*E0042*9"),
                        "The change was not written to the configuration file.");

        }

        #endregion

        #region SomethingThatIsNotASettingIsRefusedByName()

        /// <summary>
        /// A refusal names the field, because a browser showing "invalid" over
        /// a form with six things in it has said nothing.
        /// </summary>
        [Test]
        [TestCase("port",    "70000",           "v2g.port")]
        [TestCase("slac",    "\"pigeon\"",      "v2g.slac")]
        [TestCase("evseId",  "\"AAAAAAAAAAAAAAAAAA\"", "v2g.evseId")]
        [TestCase("enabled", "\"yes\"",         "v2g.enabled")]
        public async Task SomethingThatIsNotASettingIsRefusedByName(String Field, String Written, String Named)
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                               Resource,
                               new StringContent($$"""{ "{{Field}}": {{Written}} }""",
                                                 System.Text.Encoding.UTF8,
                                                 "application/json")
                           );

            Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest),
                        $"'{Field}': {Written} was accepted.");

            var error = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.That(error.ToString(),  Does.Contain(Named),
                        $"The refusal of '{Field}' did not name the field.");

        }

        #endregion

        #region ARoleThatMayOnlyLookCannotChangeIt()

        /// <summary>
        /// Reading it is one permission and changing it is another, because
        /// changing it takes the link down.
        /// </summary>
        [Test]
        public async Task ARoleThatMayOnlyLookCannotChangeIt()
        {

            using var http = Anonymous();

            var reading  = await http.GetAsync(Resource);
            var changing = await http.PutAsync(Resource, JSONBody(new JProperty("sdp", false)));

            Assert.Multiple(() => {
                Assert.That(reading. StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(changing.StatusCode,  Is.EqualTo(HttpStatusCode.Unauthorized));
            });

        }

        #endregion

    }

}
