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
using System.Net.Http.Headers;
using System.Text;

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

            // Every operation on every resource, spelled out: the node's five and
            // the station's nine - what a page asks "dns:edit" of, rather than
            // having to know that "*" means that too.
            var resources = new[] { "configuration", "dns", "nts", "certificates", "ssh",
                                    "evses", "rfid", "availability", "power", "calibration",
                                    "display", "session", "connections", "v2g" };

            Assert.Multiple(() => {
                Assert.That(roles,       Is.EquivalentTo(new[] { "systemadmin" }));
                Assert.That(permissions, Is.EquivalentTo(resources.SelectMany(resource => new[] { $"{resource}:read",
                                                                                                  $"{resource}:edit",
                                                                                                  $"{resource}:run" })));
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

        #region TheDisplayHasNoSignIn()

        /// <summary>
        /// And the accounts are not on that port either. The HTTPExt API is
        /// registered within the web interface's server alone; a display that
        /// handed out session cookies would be a sign-in on a screen in a car
        /// park, whatever it then refused to serve.
        /// </summary>
        [Test]
        public async Task TheDisplayHasNoSignIn()
        {

            using var display = AtTheDisplay();

            var response = await display.PostAsync(SignInPath, SignInBody(ChargingStation.DefaultAdminUser, Password));

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode, Is.False,
                            "The display signed somebody in.");

                Assert.That(response.Headers.Contains("Set-Cookie"), Is.False,
                            "The display handed out a session cookie.");

            });

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
