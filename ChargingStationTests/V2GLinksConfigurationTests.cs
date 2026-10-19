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

using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.StateMachines;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The "v2g" section with a link below each EVSE: read as a list, read as it
    /// was written before there was one - as the first EVSE's link - always
    /// written as a list, and refused by the path of what is wrong. And what two
    /// links cannot share.
    /// </summary>
    [TestFixture]
    public class V2GLinksConfigurationTests
    {

        #region (private static) Parse(JSON) / Refusal(JSON)

        private static V2GLinksConfiguration Parse(String JSON)
        {
            Assert.That(V2GLinksConfiguration.TryParse(JObject.Parse(JSON), out var configuration, out var error), Is.True, error);
            return configuration!;
        }

        private static String Refusal(String JSON)
        {
            Assert.That(V2GLinksConfiguration.TryParse(JObject.Parse(JSON), out _, out var error), Is.False, $"{JSON} was taken.");
            return error!;
        }

        #endregion


        #region Read

        [Test]
        public void ASectionWrittenBeforeTheListIsTheFirstEVSEsLink()
        {

            var links = Parse("""{ "enabled": true, "port": 15200, "evseId": "DE*GEF*E0007*3" }""");

            Assert.Multiple(() => {
                Assert.That(links.Links.Keys,                   Is.EqualTo(new Byte[] { 1 }));
                Assert.That(links.LinkOf(1).V2GPort,            Is.EqualTo((UInt16) 15200));
                Assert.That(links.LinkOf(1).EVSEId,             Is.EqualTo("DE*GEF*E0007*3"));
                Assert.That(links.LinkOf(2),                    Is.EqualTo(new V2GConfiguration()), "nothing was said about EVSE 2");
            });

        }

        [Test]
        public void AnEmptySectionSaysNothingAboutAnyLink()
        {
            Assert.That(Parse("{}").Links, Is.Empty);
        }

        [Test]
        public void AListSaysEachLinkByItsEVSE()
        {

            var links = Parse("""
                              { "links": [
                                  { "evse": 1, "enabled": true, "port": 15118 },
                                  { "evse": 2, "enabled": true, "port": 15119, "mode": "dc", "slac": "none" }
                              ] }
                              """);

            Assert.Multiple(() => {
                Assert.That(links.LinkOf(1).V2GPort,        Is.EqualTo((UInt16) 15118));
                Assert.That(links.LinkOf(2).V2GPort,        Is.EqualTo((UInt16) 15119));
                Assert.That(links.LinkOf(2).Mode,           Is.EqualTo(PowerMode.Dc));
                Assert.That(links.LinkOf(2).SlacTransport,  Is.EqualTo(SlacTransportKind.None));
            });

        }

        #endregion

        #region Refused, by the path of what is wrong

        [Test]
        public void ARefusalNamesTheLinkItIsIn()
        {
            Assert.That(Refusal("""{ "links": [ { "evse": 1 }, { "evse": 2, "port": 70000 } ] }"""),
                        Does.Contain("'v2g.links[1].port'"));
        }

        [Test]
        public void TwoLinksBelowOneEVSEAreRefused()
        {
            Assert.That(Refusal("""{ "links": [ { "evse": 2 }, { "evse": 2 } ] }"""),
                        Does.Contain("second link below EVSE 2"));
        }

        [TestCase("""{ "links": [ { "port": 15118 } ] }""")]
        [TestCase("""{ "links": [ { "evse": 0 } ] }""")]
        [TestCase("""{ "links": [ { "evse": 65 } ] }""")]
        [TestCase("""{ "links": [ { "evse": "A" } ] }""")]
        public void ALinkThatDoesNotSayItsEVSEIsRefused(String JSON)
        {
            Assert.That(Refusal(JSON), Does.Contain("'v2g.links[0].evse'"));
        }

        [Test]
        public void AListAndTheSettingsOfOneAreRefusedTogether()
        {
            Assert.That(Refusal("""{ "links": [ { "evse": 1 } ], "port": 15118 }"""),
                        Does.Contain("'v2g.port' is beside the list"));
        }

        [Test]
        public void AModeIsACOrDC()
        {
            Assert.Multiple(() => {
                Assert.That(Parse("""{ "mode": "AC" }""").LinkOf(1).Mode,  Is.EqualTo(PowerMode.Ac));
                Assert.That(Refusal("""{ "mode": "three-phase" }"""),      Does.Contain("'v2g.mode'"));
            });
        }

        #endregion

        #region Written

        [Test]
        public void ItIsWrittenAsAListWhateverItWasReadAs()
        {

            var written = Parse("""{ "enabled": true, "port": 15200 }""").With(2, new V2GConfiguration(Enabled: false)).ToJSON();

            Assert.Multiple(() => {
                Assert.That(written.Properties().Select(property => property.Name),  Is.EqualTo(new[] { "links" }));
                Assert.That(written["links"]![0]!.Value<Int32>("evse"),               Is.EqualTo(1));
                Assert.That(written["links"]![0]!.Value<Int32>("port"),               Is.EqualTo(15200));
                Assert.That(written["links"]![1]!.Value<Int32>("evse"),               Is.EqualTo(2));
                Assert.That(Parse(written.ToString()),                                 Is.Not.Null, "what is written cannot be read back");
            });

        }

        [Test]
        public void AChangeToALinkKeepsWhatItDidNotMention()
        {

            var changed = Parse("""{ "links": [ { "evse": 1, "port": 15200, "sdp": false } ] }""").
                              With(1, new V2GConfiguration(V2GPort: 15300));

            Assert.Multiple(() => {
                Assert.That(changed.LinkOf(1).V2GPort,  Is.EqualTo((UInt16) 15300));
                Assert.That(changed.LinkOf(1).SDP,      Is.False);
            });

        }

        #endregion

        #region What two links cannot share

        private static V2GOptions On(UInt16 Port, String? Interface = null, Boolean SDP = false, SlacTransportKind SLAC = SlacTransportKind.None)
            => new () { Enabled = true, V2GPort = Port, InterfaceName = Interface, SDP = SDP, SlacTransport = SLAC };

        [Test]
        public void TwoLinksCannotListenOnOnePort()
        {
            Assert.That(ChargingStation.ConflictBetween(On(15118), On(15118)), Does.Contain("port 15118"));
        }

        [Test]
        public void AnyFreePortIsNotOnePort()
        {
            Assert.That(ChargingStation.ConflictBetween(On(0), On(0)), Is.Null);
        }

        [Test]
        public void TwoLinksCannotBothAnswerSDPOnOneInterface()
        {
            Assert.Multiple(() => {
                Assert.That(ChargingStation.ConflictBetween(On(15118, "eth1", SDP: true), On(15119, "ETH1", SDP: true)), Does.Contain("SDP"));
                Assert.That(ChargingStation.ConflictBetween(On(15118, "eth1", SDP: true), On(15119, "eth2", SDP: true)), Is.Null);
            });
        }

        [Test]
        public void TwoLinksCannotBothListenForSLACOnOneInterface()
        {
            Assert.That(ChargingStation.ConflictBetween(On(15118, "plc0", SLAC: SlacTransportKind.Auto),
                                                        On(15119, "plc0", SLAC: SlacTransportKind.Auto)), Does.Contain("SLAC"));
        }

        [Test]
        public void ALinkThatIsOffSharesNothing()
        {
            Assert.That(ChargingStation.ConflictBetween(On(15118), On(15118) with { Enabled = false }), Is.Null);
        }

        #endregion

    }

}
