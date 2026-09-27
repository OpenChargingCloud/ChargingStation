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
    /// Roles the configuration file adds: enforced by the station as its own
    /// are, and named where a refusal names who may.
    /// </summary>
    /// <remarks>
    /// A role is a list of permissions, each an operation on a resource, as on
    /// every node - so the file can add one the station never heard of: a
    /// support desk that may look at the name and time servers and nothing
    /// else, and one that may change the name servers as well.
    /// </remarks>
    public class RolesFromTheFileTests : AChargingStationTests
    {

        #region Configuration

        protected override JObject Configuration
        {
            get
            {

                var file = (JObject) TestStations.Offline.DeepClone();

                file["roles"] = new JObject(
                                    new JProperty("support",  new JArray("dns:read", "nts:read")),
                                    new JProperty("dnsdesk",  new JArray("dns:read", "dns:edit"))
                                );

                return file;

            }
        }

        #endregion


        #region ARoleTheFileAddsMayDoWhatItSaysAndNothingElse()

        [Test]
        public async Task ARoleTheFileAddsMayDoWhatItSaysAndNothingElse()
        {

            using var http = await SignedIn();

            await Become("support", "systemadmin");

            var dns      = await http.GetAsync("api/v1/configuration/dns");
            var nts      = await http.GetAsync("api/v1/configuration/nts");
            var evses    = await http.GetAsync("api/v1/configuration/evses");
            var change   = await http.PutAsync("api/v1/configuration/dns", new StringContent("{}", Encoding.UTF8, "application/json"));
            var me       = JObject.Parse(await (await http.GetAsync("api/v1/auth/me")).Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(dns.StatusCode,     Is.EqualTo(HttpStatusCode.OK),        "reading the name servers is what the role is for");
                Assert.That(nts.StatusCode,     Is.EqualTo(HttpStatusCode.OK),        "and the time servers");
                Assert.That(evses.StatusCode,   Is.EqualTo(HttpStatusCode.Forbidden), "the EVSEs are none of its business");
                Assert.That(change.StatusCode,  Is.EqualTo(HttpStatusCode.Forbidden), "reading is not changing");
                Assert.That(me["roles"]!.Values<String>(),        Is.EqualTo(new[] { "support" }));
                Assert.That(me["permissions"]!.Values<String>(),  Is.EquivalentTo(new[] { "dns:read", "nts:read" }));
            });

        }

        #endregion

        #region ARefusalNamesTheRolesTheFileAdds()

        [Test]
        public async Task ARefusalNamesTheRolesTheFileAdds()
        {

            using var http = await SignedIn();

            await Become("viewer", "systemadmin");

            var refused = await http.PutAsync("api/v1/configuration/dns", new StringContent("{}", Encoding.UTF8, "application/json"));
            var said    = JObject.Parse(await refused.Content.ReadAsStringAsync()).Value<String>("error");

            Assert.Multiple(() => {
                Assert.That(refused.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(said,               Is.EqualTo("This needs the cpo or installer or dnsdesk or systemadmin role."));
            });

        }

        #endregion

        #region WhatTheFileAddsIsSaidInTheLog()

        [Test]
        public void WhatTheFileAddsIsSaidInTheLog()
        {

            var said = Station.Log.Recent(500, Tag: "security").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(said, Has.Some.Contains("adds the role 'support'"));
                Assert.That(said, Has.Some.Contains("adds the role 'dnsdesk'"));
            });

        }

        #endregion

    }

}
