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

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The nameplate of a station, what it tells a CSMS it is - who made it,
    /// which model, its serial number and its firmware - read and changed
    /// from the web interface.
    /// </summary>
    /// <remarks>
    /// Each field is held to what both OCPP 1.6 and OCPP 2.1 take: a vendor of
    /// at most 20 characters, which is 1.6's limit, and printable ASCII, which
    /// is all 1.6 takes. A change builds the OCPP nodes anew, so that what they
    /// say in their BootNotification is what the file says; whether a CSMS is
    /// told so is in TalkingToTheCSMSTests.
    /// </remarks>
    [TestFixture]
    public class NameplateTests : AChargingStationTests
    {

        #region Data

        private const String Resource = "api/v1/configuration/nameplate";

        #endregion

        #region (private) FileSection()

        private JObject? FileSection()

            => JObject.Parse(File.ReadAllText(Station.ConfigFile.Path))[NameplateConfiguration.SectionName] as JObject;

        #endregion


        #region AStationNobodyToldSaysTheDefaults()

        /// <summary>
        /// A station whose file says nothing about what it is says it is a
        /// ChargingStationCLI by GraphDefined, with the firmware it was built
        /// from and no serial number - and has not registered with any CSMS.
        /// </summary>
        [Test]
        public async Task AStationNobodyToldSaysTheDefaults()
        {

            using var http  = await SignedIn();

            var nameplate   = await GetJSON(http, Resource);
            var said        = nameplate["said"]!;

            Assert.Multiple(() => {

                Assert.That(nameplate["vendor"]!.Type,                          Is.EqualTo(JTokenType.Null));
                Assert.That(said.Value<String>("vendor"),                      Is.EqualTo("GraphDefined"));
                Assert.That(said.Value<String>("model"),                       Is.EqualTo("ChargingStationCLI"));
                Assert.That(said["serialNumber"]!.Type,                        Is.EqualTo(JTokenType.Null));
                Assert.That(said.Value<String>("firmwareVersion"),             Is.Not.Null.And.Not.Empty);

                Assert.That(nameplate["limits"]!.Value<Int32>("vendor"),        Is.EqualTo(20));
                Assert.That(nameplate["registration"]!["1.6"]!.Value<String>("state"),  Is.EqualTo("NotAsked"));
                Assert.That(nameplate["registration"]!["2.1"]!.Value<String>("state"),  Is.EqualTo("NotAsked"));

                Assert.That(Station.OCPPv1_6Node.ChargePointVendor,            Is.EqualTo("GraphDefined"));
                Assert.That(Station.OCPPv2_1Node.Model,                        Is.EqualTo("ChargingStationCLI"));

            });

        }

        #endregion

        #region AChangeIsWrittenAndTheOCPPNodesSayIt()

        /// <summary>
        /// A change is written to the file, and the OCPP nodes, built anew,
        /// say it - as does the Configuration page's card of the station.
        /// </summary>
        [Test]
        public async Task AChangeIsWrittenAndTheOCPPNodesSayIt()
        {

            using var http  = await SignedIn();

            var old21       = Station.OCPPv2_1Node;

            var response    = await http.PutAsync(Resource, JSONBody(new JProperty("vendor",           "Test Vendor"),
                                                                     new JProperty("model",            "Test Model"),
                                                                     new JProperty("serialNumber",     "SN-4711"),
                                                                     new JProperty("firmwareVersion",  "fw-1.2.3")));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

            var answered    = JObject.Parse(await response.Content.ReadAsStringAsync());
            var card        = (await GetJSON(http, "api/v1/configuration"))["station"]!;

            Assert.Multiple(() => {

                Assert.That(answered["said"]!.Value<String>("vendor"),         Is.EqualTo("Test Vendor"));
                Assert.That(FileSection()?.Value<String>("serialNumber"),      Is.EqualTo("SN-4711"));

                Assert.That(Station.OCPPv2_1Node,                              Is.Not.SameAs(old21), "the OCPP nodes were not built anew");
                Assert.That(Station.OCPPv1_6Node.ChargePointVendor,            Is.EqualTo("Test Vendor"));
                Assert.That(Station.OCPPv1_6Node.ChargePointSerialNumber,      Is.EqualTo("SN-4711"));
                Assert.That(Station.OCPPv2_1Node.Model,                        Is.EqualTo("Test Model"));
                Assert.That(Station.OCPPv2_1Node.FirmwareVersion,              Is.EqualTo("fw-1.2.3"));

                Assert.That(card.Value<String>("vendor"),                      Is.EqualTo("Test Vendor"));
                Assert.That(card.Value<String>("firmware"),                    Is.EqualTo("fw-1.2.3"));

            });

        }

        #endregion

        #region AnEmptySectionSaysTheDefaultsAgain()

        /// <summary>
        /// An empty object takes back what was said, and blank fields count as
        /// not given: the station says the defaults again.
        /// </summary>
        [Test]
        public async Task AnEmptySectionSaysTheDefaultsAgain()
        {

            using var http = await SignedIn();

            await http.PutAsync(Resource, JSONBody(new JProperty("vendor", "Test Vendor"), new JProperty("serialNumber", "SN-4711")));

            var response   = await http.PutAsync(Resource, JSONBody(new JProperty("vendor", "  "), new JProperty("serialNumber", "")));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK), await response.Content.ReadAsStringAsync());

            var said       = JObject.Parse(await response.Content.ReadAsStringAsync())["said"]!;

            Assert.Multiple(() => {
                Assert.That(said.Value<String>("vendor"),         Is.EqualTo("GraphDefined"));
                Assert.That(said["serialNumber"]!.Type,           Is.EqualTo(JTokenType.Null));
                Assert.That(FileSection(),                        Is.Not.Null.And.Empty, "the file still says something");
                Assert.That(Station.OCPPv1_6Node.ChargePointVendor, Is.EqualTo("GraphDefined"));
            });

        }

        #endregion

        #region WhatOCPP16CannotTakeIsRefused(Field, Value, Why)

        /// <summary>
        /// A field longer than OCPP 1.6 takes, or with more than printable ASCII
        /// in it, is refused with why, and nothing is changed.
        /// </summary>
        [TestCase("vendor",           "123456789012345678901",                                "at most 20")]
        [TestCase("model",            "123456789012345678901",                                "at most 20")]
        [TestCase("serialNumber",     "12345678901234567890123456",                           "at most 25")]
        [TestCase("firmwareVersion",  "123456789012345678901234567890123456789012345678901",  "at most 50")]
        [TestCase("vendor",           "Müller",                                               "printable ASCII")]
        [TestCase("model",            "Line\tFeed",                                           "printable ASCII")]
        public async Task WhatOCPP16CannotTakeIsRefused(String Field, String Value, String Why)
        {

            using var http  = await SignedIn();

            var old16       = Station.OCPPv1_6Node;

            var response    = await http.PutAsync(Resource, JSONBody(new JProperty(Field, Value)));
            var said        = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,     Is.EqualTo(HttpStatusCode.BadRequest), said);
                Assert.That(said,                    Does.Contain($"nameplate.{Field}").And.Contain(Why));
                Assert.That(FileSection(),           Is.Null, "the refused change was written");
                Assert.That(Station.OCPPv1_6Node,    Is.SameAs(old16), "the OCPP nodes were built anew for nothing");
            });

        }

        #endregion

        #region AFieldThatIsNoTextIsRefused()

        [Test]
        public async Task AFieldThatIsNoTextIsRefused()
        {

            using var http = await SignedIn();

            var response   = await http.PutAsync(Resource, JSONBody(new JProperty("serialNumber", 4711)));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());

        }

        #endregion

        #region TheSameIdentityAgainBuildsNothingAnew()

        /// <summary>
        /// Saving what the station says already is no change: the OCPP nodes
        /// stay, and nothing hangs up.
        /// </summary>
        [Test]
        public async Task TheSameIdentityAgainBuildsNothingAnew()
        {

            using var http = await SignedIn();

            await http.PutAsync(Resource, JSONBody(new JProperty("vendor", "Test Vendor")));

            var nodes      = Station.OCPPv2_1Node;

            var response   = await http.PutAsync(Resource, JSONBody(new JProperty("vendor", "Test Vendor")));

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,    Is.EqualTo(HttpStatusCode.OK));
                Assert.That(Station.OCPPv2_1Node,   Is.SameAs(nodes));
            });

        }

        #endregion

    }


    /// <summary>
    /// A station whose file says what it is says so from its start.
    /// </summary>
    [TestFixture]
    public class NameplateFromTheFileTests : AChargingStationTests
    {

        protected override JObject Configuration

            => new (
                   new JProperty("nts", TestStations.Offline["nts"]!.DeepClone()),
                   new JProperty("nameplate", new JObject(
                       new JProperty("vendor",           "File Vendor"),
                       new JProperty("model",            "File Model"),
                       new JProperty("serialNumber",     "SN-FILE"),
                       new JProperty("firmwareVersion",  "fw-file")
                   ))
               );

        [Test]
        public void TheOCPPNodesSayWhatTheFileSays()

            => Assert.Multiple(() => {
                   Assert.That(Station.Nameplate.VendorToSay,                     Is.EqualTo("File Vendor"));
                   Assert.That(Station.OCPPv1_6Node.ChargePointModel,            Is.EqualTo("File Model"));
                   Assert.That(Station.OCPPv1_6Node.ChargePointSerialNumber,     Is.EqualTo("SN-FILE"));
                   Assert.That(Station.OCPPv2_1Node.VendorName,                  Is.EqualTo("File Vendor"));
                   Assert.That(Station.OCPPv2_1Node.FirmwareVersion,             Is.EqualTo("fw-file"));
               });

    }

}
