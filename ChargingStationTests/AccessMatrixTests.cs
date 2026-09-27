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

using NUnit.Framework;


#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Every guarded route of the JSON API, asked by each of the four roles a
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

        private static readonly String[] Everybody       = [ "viewer", "cpo", "installer", "systemadmin" ];
        private static readonly String[] TheOperator     = [         "cpo", "installer", "systemadmin" ];
        private static readonly String[] TheInstaller    = [                "installer", "systemadmin" ];
        private static readonly String[] TheAdministrator = [                             "systemadmin" ];

        /// <summary>
        /// Every guarded route, and the roles that are let in.
        /// </summary>
        private static readonly (String Method, String Path, String[] LetIn)[] Routes = [

            ("GET",    "api/v1/configuration",                           Everybody),
            ("GET",    "api/v1/status/connections",                      Everybody),

            ("GET",    "api/v1/configuration/dns",                       Everybody),
            ("PUT",    "api/v1/configuration/dns",                       TheOperator),
            ("POST",   "api/v1/configuration/dns/query",                 TheOperator),

            ("GET",    "api/v1/configuration/nts",                       Everybody),
            ("PUT",    "api/v1/configuration/nts",                       TheOperator),
            ("POST",   "api/v1/configuration/nts/sync",                  TheOperator),
            ("POST",   "api/v1/configuration/nts/test",                  TheOperator),

            ("GET",    "api/v1/configuration/v2g",                       Everybody),
            ("PUT",    "api/v1/configuration/v2g",                       TheOperator),

            ("GET",    "api/v1/configuration/power",                     Everybody),
            ("PUT",    "api/v1/configuration/power",                     TheInstaller),

            ("GET",    "api/v1/configuration/display",                   Everybody),
            ("PUT",    "api/v1/configuration/display",                   TheOperator),

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

            // Signed in once, with a cookie from here on, as a browser is. With
            // the password sent along with every one of some two hundred
            // requests, the accounts started answering 401 partway through.
            using var http  = await SignedIn();

            var wrong       = new List<String>();
            var holding     = "systemadmin";

            foreach (var role in Everybody)
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

    }

}
