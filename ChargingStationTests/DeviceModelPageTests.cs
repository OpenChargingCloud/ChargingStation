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
    /// The OCPP Device Model page of a station, at its JSON API: read by
    /// whoever may read the OCPP log and by nobody else, the components of
    /// OCPP 2.1 with what their variables say now, and a value OCPP keeps
    /// WriteOnly shown to a systemadmin alone (Achim 2026-11-26).
    /// </summary>
    [TestFixture]
    public class DeviceModelPageTests : AChargingStationTests
    {

        private static readonly String[] TheRoles = [ "viewer", "support", "cpo", "installer", "systemadmin" ];

        private const String DeviceModel  = "api/v1/ocpp/deviceModel";
        private const String OCPPLog      = "api/v1/ocpp/messages";


        #region IsReadAsTheOCPPLogIs()

        [Test]
        public async Task IsReadAsTheOCPPLogIs()
        {

            using var http  = await SignedIn();

            var wrong       = new List<String>();
            var letIn       = new List<String>();
            var holding     = "systemadmin";

            foreach (var role in TheRoles)
            {

                holding = await Become(role, holding);

                using var deviceModel  = await http.GetAsync(DeviceModel);
                using var ocppLog      = await http.GetAsync(OCPPLog);

                if ((deviceModel.StatusCode is HttpStatusCode.Forbidden) != (ocppLog.StatusCode is HttpStatusCode.Forbidden))
                    wrong.Add($"{role}: the Device Model answered {(Int32) deviceModel.StatusCode}, the OCPP log {(Int32) ocppLog.StatusCode}");

                if (deviceModel.StatusCode is HttpStatusCode.OK)
                    letIn.Add(role);

            }

            Assert.Multiple(() => {
                Assert.That(wrong,  Is.Empty, String.Join(Environment.NewLine, wrong));
                Assert.That(letIn,  Does.Contain("systemadmin"));
                Assert.That(letIn,  Has.Some.Not.EqualTo("systemadmin"), "Nobody but a systemadmin reads it, so nothing is hidden from anybody.");
                Assert.That(letIn,  Does.Not.Contain("viewer"));
            });

        }

        #endregion

        #region AWriteOnlyValueIsShownToASystemadminAlone()

        [Test]
        public async Task AWriteOnlyValueIsShownToASystemadminAlone()
        {

            using var http  = await SignedIn();

            var asAdmin     = await GetJSON(http, DeviceModel);

            // Whoever else may read the OCPP log.
            String? other   = null;
            var holding     = "systemadmin";

            foreach (var role in TheRoles.Where(role => role != "systemadmin"))
            {
                holding = await Become(role, holding);
                using var response = await http.GetAsync(DeviceModel);
                if (response.StatusCode is HttpStatusCode.OK)
                {
                    other = role;
                    break;
                }
            }

            Assert.That(other, Is.Not.Null, "Nobody but a systemadmin reads the Device Model.");

            var asOther     = await GetJSON(http, DeviceModel);

            var adminSees   = asAdmin["deviceModel"]?["ocpp2.1"]?["SecurityCtrlr"]?["BasicAuthPassword"] as JObject;
            var otherSees   = asOther["deviceModel"]?["ocpp2.1"]?["SecurityCtrlr"]?["BasicAuthPassword"] as JObject;

            Assert.Multiple(() => {

                Assert.That(asAdmin["writeOnlyShown"]?.Value<Boolean>(),                 Is.True);
                Assert.That(adminSees,                                                    Is.Not.Null, asAdmin.ToString());
                Assert.That(adminSees!.ContainsKey("value"),                              Is.True,  "A systemadmin was hidden a WriteOnly value.");
                Assert.That(adminSees.ContainsKey("hidden"),                              Is.False);
                Assert.That(adminSees["mutability"]?.Value<String>(),                     Is.EqualTo("WriteOnly"));

                Assert.That(asOther["writeOnlyShown"]?.Value<Boolean>(),                  Is.False);
                Assert.That(otherSees,                                                    Is.Not.Null);
                Assert.That(otherSees!.ContainsKey("value"),                              Is.False, $"'{other}' was shown a WriteOnly value.");
                Assert.That(otherSees["hidden"]?.Value<Boolean>(),                        Is.True);

                // What every variable says now, read as GetVariables reads it.
                Assert.That(asOther["deviceModel"]?["ocpp2.1"]?["SecurityCtrlr"]?["OrganizationName"]?["value"]?.Type, Is.EqualTo(JTokenType.String));

            });

        }

        #endregion

    }

}
