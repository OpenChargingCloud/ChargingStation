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

using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.ChargingStation.Configuration;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What the "dns" section may say about the name servers - and what it may
    /// not, said as a sentence about the file rather than as an exception.
    /// </summary>
    /// <remarks>
    /// The log and the banner name a name server as "udp://10.0.0.1:53", which
    /// is not a form this section has ever taken - here, or in the vehicle and
    /// the energy meter whose sections it shares. A station given it stopped
    /// at its start with an ArgumentException out of IPAddress.TryParse, whose
    /// message named neither the file nor the key, and the DNS page got an
    /// internal server error for it: the parser found an address somewhere in
    /// the text and handed all of the text to one that threw on the rest.
    /// </remarks>
    [TestFixture]
    public class DNSConfigurationTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("dns");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region AnAddressIsANameServerOverUDP(Entry)

        /// <summary>
        /// The section as the README writes it, and the short form beside it:
        /// an address, which is a name server asked over UDP on port 53.
        /// </summary>
        [TestCase("""{ "address": "9.9.9.9" }""")]
        [TestCase("\"9.9.9.9\"")]
        public void AnAddressIsANameServerOverUDP(String Entry)
        {

            var section = JObject.Parse($$"""{ "enabled": true, "servers": [ {{Entry}} ], "useCache": true }""");

            Assert.That(DNSConfiguration.TryParse(section, out var read, out var error),  Is.True,  error);

            var server = read!.Servers!.Single();

            Assert.Multiple(() => {
                Assert.That(read.Enabled,                  Is.True);
                Assert.That(server.IPAddress?.ToString(),  Is.EqualTo("9.9.9.9"));
                Assert.That(server.Port.ToUInt16(),        Is.EqualTo(53));
                Assert.That(server.Transport,              Is.EqualTo(DNSTransport.UDP));
            });

        }

        #endregion

        #region TheLongFormIsWhatThePageWritesBack()

        /// <summary>
        /// The object for a server with more to say about it is read, and
        /// written back as it was - which is what makes it the form the DNS
        /// page saves the list in.
        /// </summary>
        [Test]
        public void TheLongFormIsWhatThePageWritesBack()
        {

            var entry = JObject.Parse("""{ "address": "9.9.9.9", "port": 853, "transport": "TLS", "queryTimeoutSeconds": 2 }""");

            Assert.That(DNSConfiguration.TryParseServer(entry, out var server, out var error),  Is.True,  error);

            var written = DNSConfiguration.ServerJSON(server!);

            Assert.Multiple(() => {
                Assert.That(written.Value<String>("address"),              Is.EqualTo("9.9.9.9"));
                Assert.That(written.Value<Int32> ("port"),                 Is.EqualTo(853));
                Assert.That(written.Value<String>("transport"),            Is.EqualTo("TLS"));
                Assert.That(written.Value<Double>("queryTimeoutSeconds"),  Is.EqualTo(2));
            });

        }

        #endregion

        #region TheFormTheLogNamesAServerInIsRefusedWithASentence(Entry)

        /// <summary>
        /// How the log names a name server, and an address with its port: not
        /// forms the section takes, so refused - with the entry named, and not
        /// with an exception out of the parser, which is what all of them were.
        /// </summary>
        [TestCase("udp://213.133.98.98:53")]
        [TestCase("udp://[2a01:4f8:0:1::add:1010]:53")]
        [TestCase("213.133.98.98:53")]
        [TestCase("[2001:db8::1]:53")]
        public void TheFormTheLogNamesAServerInIsRefusedWithASentence(String Entry)
        {

            foreach (var server in new JToken[] { Entry, new JObject(new JProperty("address", Entry)) })
            {

                var                section  = new JObject(new JProperty("servers", new JArray(server)));
                var                parsed   = true;
                DNSConfiguration?  read     = null;
                String?            error    = null;

                Assert.That(() => parsed = DNSConfiguration.TryParse(section, out read, out error),  Throws.Nothing,  server.ToString());

                Assert.Multiple(() => {
                    Assert.That(parsed,  Is.False,  server.ToString());
                    Assert.That(read,    Is.Null,   server.ToString());
                    Assert.That(error,   Does.Contain("'dns.servers'").And.Contain(Entry));
                });

            }

        }

        #endregion

        #region ANameWithAnAddressInItIsAName()

        /// <summary>
        /// A domain name that merely begins with an address - which is what
        /// services like nip.io hand out - is a name server named by its name,
        /// to be resolved like any other. It was an exception as well: the
        /// address was found in it, and the whole of it handed to the parser
        /// for addresses.
        /// </summary>
        [Test]
        public void ANameWithAnAddressInItIsAName()
        {

            var section = JObject.Parse("""{ "servers": [ "10.0.0.1.nip.io" ] }""");

            DNSConfiguration?  read   = null;
            String?            error  = null;

            Assert.That(() => DNSConfiguration.TryParse(section, out read, out error),  Throws.Nothing);
            Assert.That(read,  Is.Not.Null,  error);

            var server = read!.Servers!.Single();

            Assert.Multiple(() => {
                Assert.That(server.IPAddress,                                       Is.Null);
                Assert.That(server.DomainName?.ToString().TrimEnd('.'),             Is.EqualTo("10.0.0.1.nip.io"));
            });

        }

        #endregion

        #region AStationWhoseFileSaysSoStopsWithASentence()

        /// <summary>
        /// And at a start: the station stops over the file the way it stops
        /// over any file it cannot read, saying what is wrong and where -
        /// rather than with an ArgumentException naming neither, which is how
        /// it stopped before.
        /// </summary>
        /// <remarks>
        /// Built and never started: the constructor is what reads the file.
        /// </remarks>
        [Test]
        public void AStationWhoseFileSaysSoStopsWithASentence()
        {

            var file     = Path.Combine(directory, "configuration.json");

            var problem  = Assert.Throws<InvalidOperationException>(() => TestStations.New(
                                                                              directory,
                                                                              JObject.Parse("""{ "dns": { "servers": [ "udp://213.133.98.98:53" ] } }""")
                                                                          ));

            Assert.That(problem?.Message,  Does.Contain("'dns.servers'").And.Contain("udp://213.133.98.98:53").And.Contain(file));

        }

        #endregion

    }

}
