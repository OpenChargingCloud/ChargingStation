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
    /// Who may ask this charging station anything, and what happens to
    /// everybody else.
    /// </summary>
    public class AuthenticationTests : AChargingStationTests
    {

        #region TheAPIRefusesWithoutASession()

        [Test]
        public async Task TheAPIRefusesWithoutASession()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/api/v1/status");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region AnUnknownAPIPathAnswersJSONAndNotTheStub()

        /// <summary>
        /// The JSON API lives in its own HTTPAPI so that a mistyped API path
        /// never falls through to the single-page-application stub - a browser
        /// that asked for JSON and got HTML with status 200 has no way to tell
        /// what went wrong.
        /// </summary>
        [Test]
        public async Task AnUnknownAPIPathAnswersJSONAndNotTheStub()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/api/v1/nonsense");

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,                              Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(response.Content.Headers.ContentType?.MediaType,  Is.EqualTo("application/json"));
            });

        }

        #endregion

        #region AWrongPasswordIsRefused()

        [Test]
        public async Task AWrongPasswordIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", Station.Sessions.Username),
                                         new JProperty("password", "not the password")
                                     )
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region AWrongUsernameIsRefused()

        [Test]
        public async Task AWrongUsernameIsRefused()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", "somebody-else"),
                                         new JProperty("password", Password)
                                     )
                                 );

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));

        }

        #endregion

        #region TheGeneratedPasswordSignsIn()

        /// <summary>
        /// The password this station made up at its first start, shown once
        /// on the console and kept nowhere but in its hash, opens the web
        /// interface.
        /// </summary>
        [Test]
        public async Task TheGeneratedPasswordSignsIn()
        {

            using var http = Anonymous();

            var response = await http.PostAsync(
                                     "/api/v1/auth/login",
                                     JSONBody(
                                         new JProperty("username", Station.Sessions.Username),
                                         new JProperty("password", Password)
                                     )
                                 );

            var me = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,   Is.True);
                Assert.That(me.Value<String>("username"),   Is.EqualTo(Station.Sessions.Username));
            });

        }

        #endregion

        #region TheSessionSaysWhatItMayDo()

        /// <summary>
        /// A first start signs in as the system administrator, because there is
        /// nobody else yet to hand the rest to. What the browser is told is a
        /// copy of what the station enforces and not the enforcement itself;
        /// this is the copy.
        /// </summary>
        [Test]
        public async Task TheSessionSaysWhatItMayDo()
        {

            using var http = await SignedIn();

            var me = await GetJSON(http, "/api/v1/auth/me");

            var roles        = me["roles"]?.      Values<String>().ToArray() ?? [];
            var permissions  = me["permissions"]?.Values<String>().ToArray() ?? [];

            Assert.Multiple(() => {
                Assert.That(roles,       Is.EquivalentTo(new[] { "systemadmin" }));
                Assert.That(permissions, Is.EquivalentTo(new[] { "readConfiguration",
                                                                 "changeNetworkSettings",
                                                                 "runDiagnostics",
                                                                 "changeAvailability",
                                                                 "changePowerLimits",
                                                                 "manageCalibration",
                                                                 "changeHardware" }));
            });

        }

        #endregion

        #region SigningOutEndsTheSession()

        [Test]
        public async Task SigningOutEndsTheSession()
        {

            using var http = await SignedIn();

            Assert.That((await http.GetAsync("/api/v1/auth/me")).IsSuccessStatusCode, Is.True,
                        "The session was not live before it was ended.");

            var logout = await http.PostAsync("/api/v1/auth/logout", null);

            Assert.Multiple(() => {
                Assert.That(logout.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
                Assert.That(Station.Sessions.Count, Is.EqualTo(0));
            });

            Assert.That((await http.GetAsync("/api/v1/auth/me")).StatusCode,
                        Is.EqualTo(HttpStatusCode.Unauthorized),
                        "The cookie still opened the station after signing out.");

        }

        #endregion

        #region ACrossSiteChangeIsRefused()

        /// <summary>
        /// The session cookie is SameSite=strict, so a cross-site request would
        /// arrive without a session anyway. This is the second lock on the same
        /// door: browsers say where a request came from, and a state-changing
        /// request from anywhere but this origin is refused before it is read.
        /// </summary>
        [Test]
        public async Task ACrossSiteChangeIsRefused()
        {

            using var http = await SignedIn();

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/configuration/dns") {
                              Content = JSONBody(new JProperty("useCache", false))
                          };

            request.Headers.Add("Sec-Fetch-Site", "cross-site");

            var response = await http.SendAsync(request);

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,          Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(Station.DNSClient.UseCache, Is.True,
                            "The refused request changed something anyway.");
            });

        }

        #endregion

        #region ASameOriginChangeIsNotRefused()

        /// <summary>
        /// The other half of the test above: the header that the station
        /// looks at is the one a browser sets for its own page, and that one
        /// has to go through - or the lock is on the wrong door.
        /// </summary>
        [Test]
        public async Task ASameOriginChangeIsNotRefused()
        {

            using var http = await SignedIn();

            var request = new HttpRequestMessage(HttpMethod.Put, "/api/v1/configuration/dns") {
                              Content = JSONBody(new JProperty("useCache", false))
                          };

            request.Headers.Add("Sec-Fetch-Site", "same-origin");

            var response = await http.SendAsync(request);

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,   Is.True);
                Assert.That(Station.DNSClient.UseCache,  Is.False);
            });

        }

        #endregion

        #region TheDisplayNeedsNoSignIn()

        /// <summary>
        /// The display is a screen in a car park with nobody signed in at it,
        /// so its own server asks for nothing - which is exactly why it is a
        /// different server on a different port.
        /// </summary>
        [Test]
        public async Task TheDisplayNeedsNoSignIn()
        {

            using var display = AtTheDisplay();

            var kiosk = await GetJSON(display, "/api/kiosk");

            Assert.That(kiosk, Is.Not.Null);

        }

        #endregion

        #region TheDisplayServesNoneOfTheAdministration()

        /// <summary>
        /// And the other half of that, which is the half worth testing: the
        /// port with no sign-in in front of it must not answer the resources
        /// the sign-in is there to guard. A station whose two servers ever
        /// became one would pass every other test in this file.
        /// </summary>
        [Test]
        public async Task TheDisplayServesNoneOfTheAdministration()
        {

            using var display = AtTheDisplay();

            foreach (var guarded in new[] {
                         "/api/v1/status",
                         "/api/v1/configuration",
                         "/api/v1/configuration/dns",
                         "/api/v1/configuration/nts",
                         "/api/v1/logs",
                         "/api/v1/auth/me"
                     })
            {

                var response = await display.GetAsync(guarded);

                Assert.That(response.IsSuccessStatusCode, Is.False,
                            $"The display answered {guarded}, which nobody has to sign in to reach.");

            }

        }

        #endregion

        #region TheDisplayDoesNotTakeTheWebInterfacesSession()

        /// <summary>
        /// Signing in to the web interface must not open the display's server
        /// either.
        /// </summary>
        /// <remarks>
        /// And the session cookie really does arrive there: cookies belong to a
        /// host and not to a port, so a browser signed in on 2348 sends the
        /// same cookie to 2349. Which is exactly why this is worth asserting -
        /// the two ports are kept apart by being two servers with two sets of
        /// resources, and not by the cookie failing to make the trip.
        /// </remarks>
        [Test]
        public async Task TheDisplayDoesNotTakeTheWebInterfacesSession()
        {

            using var http = await SignedIn();

            // The same client, cookie and all, pointed at the other port.
            var response = await http.GetAsync($"{KioskURL.TrimEnd('/')}/api/v1/configuration");

            Assert.That(response.IsSuccessStatusCode, Is.False,
                        "The display served the configuration to a browser that signed in next door.");

        }

        #endregion

    }

}
