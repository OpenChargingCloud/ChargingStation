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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;


#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Every guarded route of the JSON API, asked by each of the five roles a
    /// station brings: which of them are let in, and which are turned away.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What each role may do, written down route by route - so that however
    /// the station comes to its answer, the answer can be held to what it was.
    /// A request that is let in is asked in a way that fails right after the
    /// door, with a body that is no JSON at all, so that nothing is changed
    /// and nothing is asked of anybody: what is compared is the door, a 403
    /// or anything but.
    /// </para>
    /// <para>
    /// The certificate store has its own tests, and the EVSEs and the card
    /// readers are only let in at the door here: what a change to them needs
    /// is decided by what it changes, which a body that is no JSON never gets
    /// to say.
    /// </para>
    /// </remarks>
    public class AccessMatrixTests : AChargingStationTests
    {

        #region Data

        /// <summary>
        /// The roles a station brings, each of which asks every route.
        /// </summary>
        private static readonly String[] TheRoles        = [ "viewer", "support", "cpo", "installer", "systemadmin" ];

        private static readonly String[] Everybody       = [ "viewer", "cpo", "installer", "systemadmin" ];

        /// <summary>
        /// Who may see what is going on: everybody, and the support, who sees
        /// nothing else.
        /// </summary>
        private static readonly String[] WhatIsGoingOn   = [ "viewer", "support", "cpo", "installer", "systemadmin" ];
        private static readonly String[] TheOperator     = [         "cpo", "installer", "systemadmin" ];

        /// <summary>
        /// Who may do what the operator's telephone support may: restart the
        /// station once it is idle, let go of a stuck cable, and have what is
        /// going on reported again.
        /// </summary>
        private static readonly String[] TheSupport      = [ "support", "cpo", "installer", "systemadmin" ];
        private static readonly String[] TheInstaller    = [                "installer", "systemadmin" ];
        private static readonly String[] TheAdministrator = [                             "systemadmin" ];

        /// <summary>
        /// Every guarded route, and the roles that are let in.
        /// </summary>
        private static readonly (String Method, String Path, String[] LetIn)[] Routes = [

            ("GET",    "api/v1/configuration",                           Everybody),
            ("GET",    "api/v1/status/connections",                      Everybody),
            ("GET",    "api/v1/status/evses",                            WhatIsGoingOn),
            ("GET",    "api/v1/status/meters",                           Everybody),

            ("GET",    "api/v1/configuration/dns",                       Everybody),
            ("PUT",    "api/v1/configuration/dns",                       TheOperator),
            ("POST",   "api/v1/configuration/dns/query",                 TheOperator),

            ("GET",    "api/v1/configuration/nts",                       Everybody),
            ("PUT",    "api/v1/configuration/nts",                       TheOperator),
            ("POST",   "api/v1/configuration/nts/sync",                  TheOperator),
            ("POST",   "api/v1/configuration/nts/test",                  TheOperator),

            ("GET",    "api/v1/configuration/v2g",                       Everybody),
            ("PUT",    "api/v1/configuration/v2g",                       TheOperator),

            ("GET",    "api/v1/configuration/meters",                    Everybody),
            ("PUT",    "api/v1/configuration/meters",                    TheInstaller),

            // Let in at the door to read; a write is let in further on, by
            // what the body asks - see MeterTests.
            ("POST",   "api/v1/meters/modbus",                           Everybody),
            ("GET",    "api/v1/meters/sessions",                         Everybody),

            ("GET",    "api/v1/configuration/power",                     Everybody),
            ("PUT",    "api/v1/configuration/power",                     TheInstaller),

            ("GET",    "api/v1/configuration/display",                   Everybody),
            ("PUT",    "api/v1/configuration/display",                   TheOperator),
            ("GET",    "api/v1/configuration/nameplate",                 Everybody),
            ("PUT",    "api/v1/configuration/nameplate",                 TheAdministrator),

            // Which keys sign as what is read by whoever reads the configuration, and
            // written by the owner alone.
            ("GET",    "api/v1/configuration/signatures",                 Everybody),
            ("PUT",    "api/v1/configuration/signatures/settings",        TheAdministrator),
            ("POST",   "api/v1/configuration/signatures/roles",           TheAdministrator),
            ("POST",   "api/v1/configuration/signatures/roles/firstOwner", TheAdministrator),
            ("POST",   "api/v1/configuration/signatures/roles/remove",    TheAdministrator),
            ("POST",   "api/v1/configuration/signatures/policy",          TheAdministrator),
            ("POST",   "api/v1/configuration/signatures/reset",           TheAdministrator),

            ("GET",    "api/v1/configuration/commissioning",             Everybody),
            ("PUT",    "api/v1/configuration/commissioning",             TheInstaller),

            ("GET",    "api/v1/configuration/evses",                     Everybody),
            ("PUT",    "api/v1/configuration/evses",                     Everybody),

            ("GET",    "api/v1/configuration/calibration",               Everybody),
            ("PUT",    "api/v1/configuration/calibration",               TheInstaller),

            ("GET",    "api/v1/configuration/authentications",           Everybody),
            ("POST",   "api/v1/configuration/authentications",           TheOperator),
            ("POST",   "api/v1/configuration/authentications/update",    TheOperator),
            ("POST",   "api/v1/configuration/authentications/remove",    TheOperator),

            ("GET",    "api/v1/configuration/connections",               Everybody),
            ("POST",   "api/v1/configuration/connections",               TheOperator),
            ("POST",   "api/v1/configuration/connections/update",        TheOperator),
            ("POST",   "api/v1/configuration/connections/remove",        TheOperator),
            ("POST",   "api/v1/configuration/connections/test",          TheOperator),

            ("GET",    "api/v1/configuration/certificates",              Everybody),
            ("POST",   "api/v1/configuration/certificates",              TheOperator),
            ("POST",   "api/v1/configuration/certificates/import",       TheOperator),
            ("POST",   "api/v1/configuration/certificates/remove",       TheOperator),

            ("GET",    "api/v1/configuration/rfid",                      Everybody),
            ("PUT",    "api/v1/configuration/rfid",                      Everybody),

            ("GET",    "api/v1/reservations",                            Everybody),
            ("POST",   "api/v1/reservations",                            TheOperator),
            ("POST",   "api/v1/reservations/cancel",                     TheOperator),

            ("POST",   "api/v1/sessions/webpayment",                     TheOperator),
            ("POST",   "api/v1/sessions/stop",                           TheOperator),

            ("POST",   "api/v1/restart",                                 TheSupport),
            ("POST",   "api/v1/restart/now",                             TheOperator),
            ("POST",   "api/v1/evses/unlock",                            TheSupport),
            ("POST",   "api/v1/status/report",                           TheSupport),
            ("POST",   "api/v1/ocpp/send",                               TheOperator),

            ("GET",    "api/v1/messages",                                Everybody),
            ("POST",   "api/v1/messages",                                TheOperator),
            ("POST",   "api/v1/messages/clear",                          TheOperator),

            ("GET",    "api/v1/certificates",                            Everybody),
            ("POST",   "api/v1/certificates",                            TheAdministrator),
            ("POST",   "api/v1/certificates/reload",                     TheAdministrator),
            ("GET",    "api/v1/certificates/0000000000000000",           Everybody),
            ("PATCH",  "api/v1/certificates/0000000000000000",           TheAdministrator),
            ("DELETE", "api/v1/certificates/0000000000000000",           TheAdministrator)

        ];

        #endregion


        #region EveryRoleIsLetInWhereItWasAndNowhereElse()

        [Test]
        public async Task EveryRoleIsLetInWhereItWasAndNowhereElse()
        {

            // Signed in once, with a cookie from here on, as a browser is.
            using var http  = await SignedIn();

            var wrong       = new List<String>();
            var holding     = "systemadmin";

            foreach (var role in TheRoles)
            {

                holding = await Become(role, holding);

                foreach (var (method, path, letIn) in Routes)
                {

                    using var request   = new HttpRequestMessage(new HttpMethod(method), path);

                    // Something that is no JSON at all, so that whatever gets
                    // past the door is turned away at the next step.
                    if (method is "POST" or "PUT" or "PATCH")
                        request.Content = new StringContent("this is no JSON", Encoding.UTF8, "application/json");

                    using var response  = await http.SendAsync(request);

                    var letInNow        = response.StatusCode is not HttpStatusCode.Forbidden;

                    if (response.StatusCode is HttpStatusCode.Unauthorized)
                        wrong.Add($"{role}: {method} {path} answered 401, as if nobody were signed in");

                    else if (letInNow != letIn.Contains(role))
                        wrong.Add($"{role}: {method} {path} was {(letInNow ? "let in" : "turned away")} ({(Int32) response.StatusCode})");

                }

            }

            Assert.That(wrong, Is.Empty, String.Join(Environment.NewLine, wrong));

        }

        #endregion

        #region WhatTheStationIsToldToDoIsDoneAndSaid()

        /// <summary>
        /// Past the door: a restart of a station nothing charges at is one
        /// now, a cable is let go of at an EVSE it has and not at one it has
        /// not, and a station that dials nobody says that nobody is told.
        /// </summary>
        [Test]
        public async Task WhatTheStationIsToldToDoIsDoneAndSaid()
        {

            using var http = await SignedIn();

            async Task<(HttpStatusCode Status, JObject JSON)> Post(String Path, JObject Body)
            {

                using var response = await http.PostAsync(Path, new StringContent(Body.ToString(), Encoding.UTF8, "application/json"));

                return (response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));

            }

            var restart    = await Post("api/v1/restart",       new JObject());
            var atOnce     = await Post("api/v1/restart/now",   new JObject());
            var noEVSE     = await Post("api/v1/evses/unlock",  new JObject());
            var unknown    = await Post("api/v1/evses/unlock",  new JObject(new JProperty("evse", 250)));
            var report     = await Post("api/v1/status/report", new JObject());
            var heartbeat  = await Post("api/v1/ocpp/send",     new JObject(new JProperty("message", "Heartbeat")));
            var other      = await Post("api/v1/ocpp/send",     new JObject(new JProperty("message", "StatusNotification")));

            Assert.Multiple(() => {

                Assert.That(restart.  Status,                          Is.EqualTo(HttpStatusCode.OK));
                Assert.That(restart.  JSON.Value<String>("restart"),   Is.EqualTo("now"));
                Assert.That(atOnce.   Status,                          Is.EqualTo(HttpStatusCode.OK));
                Assert.That(atOnce.   JSON.Value<Int32>("ended"),      Is.EqualTo(0));

                Assert.That(noEVSE.   Status,                          Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(unknown.  Status,                          Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(unknown.  JSON.ToString(),                 Does.Contain("no EVSE 250"));

                Assert.That(report.   Status,                          Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(report.   JSON.ToString(),                 Does.Contain("No back end"));
                Assert.That(heartbeat.Status,                          Is.EqualTo(HttpStatusCode.Conflict));

                // How the EVSEs stand is told again at its own route, which
                // asks for another right.
                Assert.That(other.    Status,                          Is.EqualTo(HttpStatusCode.BadRequest));

            });

            foreach (var evse in Station.EVSEs.Take(1))
            {

                var unlocked = await Post("api/v1/evses/unlock", new JObject(new JProperty("evse", evse.Id)));

                Assert.That(unlocked.Status,                           Is.EqualTo(HttpStatusCode.OK));
                Assert.That(unlocked.JSON.Value<Boolean>("unlocked"),  Is.True);

            }

        }

        #endregion

    }

}
