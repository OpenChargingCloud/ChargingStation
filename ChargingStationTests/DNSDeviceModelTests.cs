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

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a CSMS reads of the station's name resolution in the Device Model,
    /// against what the station's own file and its node say.
    /// </summary>
    /// <remarks>
    /// Read from a station that was built and not started, so that nothing is
    /// ever asked of a name server: the values come from the configuration
    /// file, through the same JSON the web page reads, and the verdicts of
    /// handshakes from the node's own record of one.
    /// </remarks>
    [TestFixture]
    public class DNSDeviceModelTests
    {

        #region Data

        /// <summary>
        /// A root fingerprint as somebody might type it, in capitals and with
        /// colons, which the node keeps in small letters and without them.
        /// </summary>
        private const String  ARootAsTyped     = "9D:2E:4F:6A:8B:0C:1D:3E:5F:7A:9B:1C:3D:5E:7F:9A:0B:2C:4D:6E:8F:0A:1B:3C:5D:7E:9F:1A:2B:4C:6D:8E";
        private const String  ARoot            = "9d2e4f6a8b0c1d3e5f7a9b1c3d5e7f9a0b2c4d6e8f0a1b3c5d7e9f1a2b4c6d8e";
        private const String  ACertificate     = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";
        private const String  AnotherOne      = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private String            directory  = "";
        private ChargingStation?  station;
        private DNSDeviceModel    model      = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void AStationWithTwoNameServers()
        {

            directory  = TestStations.TemporaryDirectory("dns-device-model");

            var configuration = TestStations.Offline;

            configuration["dns"] = new JObject(
                                       new JProperty("servers", new JArray(
                                           new JObject(
                                               new JProperty("address",              "192.0.2.53"),
                                               new JProperty("transport",            "TLS"),
                                               new JProperty("queryTimeoutSeconds",  2.5),
                                               new JProperty("rootFingerprint",      ARootAsTyped),
                                               new JProperty("onMismatch",           "record"),
                                               new JProperty("trustOnFirstUse",      "certificate")
                                           ),
                                           new JObject(
                                               new JProperty("address",              "2001:0db8:0000:0000:0000:0000:0000:0053"),
                                               new JProperty("transport",            "UDP")
                                           )
                                       )),
                                       new JProperty("queryTimeoutSeconds",  3),
                                       new JProperty("maxRetries",           2),
                                       new JProperty("dnssecOK",             true),
                                       new JProperty("useCache",             false)
                                   );

            station    = TestStations.New(directory, configuration, WithDisplay: false);
            model      = new DNSDeviceModel(station.DNSConfigurationJSON);

        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            station = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region TheClientReadsAsTheFileSaysIt()

        /// <summary>
        /// DNSClientController says what the file says, where it says
        /// something, and what the client does otherwise.
        /// </summary>
        [Test]
        public void TheClientReadsAsTheFileSaysIt()
        {

            Assert.Multiple(() => {

                Assert.That(model.ClientValue("Enabled"),           Is.EqualTo("true"));
                Assert.That(model.ClientValue("QueryTimeout"),      Is.EqualTo("3"));
                Assert.That(model.ClientValue("MaxRetries"),        Is.EqualTo("2"));
                Assert.That(model.ClientValue("DNSSECOK"),          Is.EqualTo("true"));
                Assert.That(model.ClientValue("UseCache"),          Is.EqualTo("false"));

                // What the file does not mention: the client's own.
                Assert.That(model.ClientValue("RecursionDesired"),  Is.EqualTo("Set"));
                Assert.That(model.ClientValue("FollowCNAMEs"),      Is.EqualTo("true"));
                Assert.That(model.ClientValue("ClientSubnet"),      Is.EqualTo(""),   "no subnet is sent");

                Assert.That(model.ClientValue("MaxServers"),        Is.EqualTo("16"));
                Assert.That(model.Slots,                            Is.EqualTo(16));

                Assert.That(Int32.Parse(model.ClientValue("UDPPayloadSize")!), Is.GreaterThan(0));
                Assert.That(Int32.Parse(model.ClientValue("MaxCNAMEFollows")!), Is.GreaterThan(0));

            });

        }

        #endregion

        #region EachServerIsASlot()

        /// <summary>
        /// The n-th server of the file is the slot n, with what it is held to,
        /// its port filled in for its transport and its address written short.
        /// </summary>
        [Test]
        public void EachServerIsASlot()
        {

            Assert.Multiple(() => {

                Assert.That(model.ServerValue(1, "Address"),          Is.EqualTo("192.0.2.53"));
                Assert.That(model.ServerValue(1, "Transport"),        Is.EqualTo("TLS"));
                Assert.That(model.ServerValue(1, "Port"),             Is.EqualTo("853"));
                Assert.That(model.ServerValue(1, "QueryTimeout"),     Is.EqualTo("2.5"));
                Assert.That(model.ServerValue(1, "RootPins"),         Is.EqualTo(ARoot), "in the form the node keeps");
                Assert.That(model.ServerValue(1, "CertificatePins"),  Is.EqualTo(""));
                Assert.That(model.ServerValue(1, "OnPinMismatch"),    Is.EqualTo("Record"));
                Assert.That(model.ServerValue(1, "TrustOnFirstUse"),  Is.EqualTo("Certificate"));

                Assert.That(model.ServerValue(2, "Address"),          Is.EqualTo("2001:db8::53"));
                Assert.That(model.ServerValue(2, "Transport"),        Is.EqualTo("UDP"));
                Assert.That(model.ServerValue(2, "Port"),             Is.EqualTo("53"));
                Assert.That(model.ServerValue(2, "QueryTimeout"),     Is.EqualTo(""), "the client's");
                Assert.That(model.ServerValue(2, "OnPinMismatch"),    Is.EqualTo("Refuse"));
                Assert.That(model.ServerValue(2, "TrustOnFirstUse"),  Is.EqualTo("None"));

                // No handshake yet.
                Assert.That(model.ServerValue(1, "Problem"),            Is.EqualTo("false"));
                Assert.That(model.ServerValue(1, "CertificateStatus"),  Is.EqualTo(""));
                Assert.That(model.ServerValue(1, "KnownSince"),         Is.EqualTo(""));

            });

        }

        #endregion

        #region AnEmptySlotIsThereAndSaysNothing()

        /// <summary>
        /// A slot no server is in answers "" for every variable, so that a CSMS
        /// can put a server there; a slot beyond the last and a variable the
        /// node cannot answer are not there at all.
        /// </summary>
        [Test]
        public void AnEmptySlotIsThereAndSaysNothing()
        {

            Assert.Multiple(() => {

                foreach (var variable in DNSDeviceModel.ServerVariables)
                {
                    Assert.That(model.ServerValue( 3, variable), Is.EqualTo(""), variable);
                    Assert.That(model.ServerValue(16, variable), Is.EqualTo(""), variable);
                    Assert.That(model.ServerValue( 0, variable), Is.Null,        variable);
                    Assert.That(model.ServerValue(17, variable), Is.Null,        variable);
                }

                foreach (var notYet in new[] { "AuthenticationName", "RootCAs", "Enabled", "Priority" })
                    Assert.That(model.ServerValue(1, notYet), Is.Null, notYet);

                foreach (var notYet in new[] { "ServerSource", "EncryptedOnly", "NoSuchThing" })
                    Assert.That(model.ClientValue(notYet), Is.Null, notYet);

            });

        }

        #endregion

        #region NamesAreReadInAnyCase()

        /// <summary>
        /// OCPP compares the names of variables without regard to case.
        /// </summary>
        [Test]
        public void NamesAreReadInAnyCase()
        {

            Assert.Multiple(() => {
                Assert.That(model.ClientValue("dnssecok"),         Is.EqualTo("true"));
                Assert.That(model.ServerValue(1, "TRANSPORT"),     Is.EqualTo("TLS"));
            });

        }

        #endregion

        #region AChangeElsewhereIsReadAtTheNextAsk()

        /// <summary>
        /// Nothing is kept: what the page changes is what the next GetVariables
        /// reads, the three ways recursion can be set included, and a server
        /// removed moves the one behind it up a slot.
        /// </summary>
        [Test]
        public void AChangeElsewhereIsReadAtTheNextAsk()
        {

            Assert.That(station!.TryUpdateDNSConfiguration(new JObject(new JProperty("recursionDesired", false)),              out var error), Is.True, error);
            Assert.That(model.ClientValue("RecursionDesired"), Is.EqualTo("Unset"));

            Assert.That(station.TryUpdateDNSConfiguration(new JObject(new JProperty("recursionDesired", JValue.CreateNull())), out error),     Is.True, error);
            Assert.That(model.ClientValue("RecursionDesired"), Is.EqualTo("Default"));

            Assert.That(station.TryUpdateDNSConfiguration(new JObject(
                                                              new JProperty("servers", new JArray(
                                                                  new JObject(
                                                                      new JProperty("address",                  "2001:db8::53"),
                                                                      new JProperty("transport",                "TLS"),
                                                                      new JProperty("certificateFingerprints",  new JArray(ACertificate, AnotherOne))
                                                                  )
                                                              ))
                                                          ),
                                                          out error),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(model.ServerValue(1, "Address"),          Is.EqualTo("2001:db8::53"));
                Assert.That(model.ServerValue(1, "CertificatePins"),  Is.EqualTo($"{ACertificate},{AnotherOne}"));
                Assert.That(model.ServerValue(2, "Address"),          Is.EqualTo(""));
            });

        }

        #endregion

        #region SwitchedOffTheServersStillRead()

        /// <summary>
        /// Name resolution switched off holds no servers in its client, yet the
        /// slots keep the servers the file has, which are what comes back when
        /// it is switched on again.
        /// </summary>
        [Test]
        public void SwitchedOffTheServersStillRead()
        {

            Assert.That(station!.TryUpdateDNSConfiguration(new JObject(new JProperty("enabled", false)), out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(model.ClientValue("Enabled"),     Is.EqualTo("false"));
                Assert.That(model.ServerValue(1, "Address"),  Is.EqualTo("192.0.2.53"));
                Assert.That(model.ServerValue(2, "Address"),  Is.EqualTo("2001:db8::53"));
            });

        }

        #endregion

        #region TheLastHandshakeReadsAsAVerdict()

        /// <summary>
        /// What the node made of a server's certificate last, as the node writes
        /// it down: a refusal is a problem, the outcome becomes an entry of the
        /// valuesList, and since when a server is known is written as OCPP
        /// writes a time.
        /// </summary>
        [Test]
        public void TheLastHandshakeReadsAsAVerdict()
        {

            var section  = station!.DNSConfigurationJSON();
            var server   = (JObject) section["servers"]![0]!;

            server["judgement"]  = new ServerJudgement(
                                       Service:                 "dns",
                                       Server:                  "tls://192.0.2.53:853",
                                       At:                      DateTimeOffset.UtcNow,
                                       Accepted:                false,
                                       Outcome:                 "pinMismatch",
                                       CertificateFingerprint:  ACertificate,
                                       RootFingerprint:         AnotherOne,
                                       AnchoredBy:              null,
                                       HeldTo:                  null,
                                       Learned:                 TrustOnFirstUse.None,
                                       Previously:              null,
                                       FirstSeen:               false,
                                       Steps:                   []
                                   ).ToJSON();

            server["known"]      = new JObject(
                                       new JProperty("certificate",  ACertificate),
                                       new JProperty("root",         AnotherOne),
                                       new JProperty("since",        new DateTimeOffset(2026, 10, 29, 14, 3, 7, TimeSpan.FromHours(1)).AddTicks(1234567).ToString("o"))
                                   );

            Assert.Multiple(() => {

                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "Problem"),                 Is.EqualTo("true"));
                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "CertificateStatus"),       Is.EqualTo("PinMismatch"));
                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "CertificateFingerprint"),  Is.EqualTo(ACertificate));
                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "RootFingerprint"),         Is.EqualTo(AnotherOne));
                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "KnownSince"),              Is.EqualTo("2026-10-29T13:03:07.123Z"));

                // A server believed is no problem.
                server["judgement"]!["accepted"] = true;
                server["judgement"]!["outcome"]  = "accepted";

                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "Problem"),                 Is.EqualTo("false"));
                Assert.That(DNSDeviceModel.ServerValueIn(section, 1, "CertificateStatus"),       Is.EqualTo("Accepted"));

            });

        }

        #endregion

    }

}
