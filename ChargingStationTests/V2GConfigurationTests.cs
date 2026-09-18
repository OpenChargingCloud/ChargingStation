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

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The "v2g" section of the configuration file, on its own.
    /// </summary>
    /// <remarks>
    /// No station here, and no socket: everything below is the section being
    /// read and the section being laid over the options a station already has.
    /// What happens when a station acts on it is measured in
    /// <see cref="V2GConfigurationAPITests"/>, with the whole thing switched
    /// off - a test suite that bound UDP 15118 and joined a multicast group on
    /// whatever machine it ran on would be a suite nobody runs twice.
    /// </remarks>
    [TestFixture]
    public class V2GConfigurationTests
    {

        #region (private static) Parse(Text) / Refuse(Text)

        /// <summary>
        /// The section, when it is a section.
        /// </summary>
        private static V2GConfiguration Parse(String Text)
        {

            Assert.That(V2GConfiguration.TryParse(JObject.Parse(Text), out var configuration, out var error),
                        Is.True, $"'{Text}' was refused: {error}");

            return configuration!;

        }

        /// <summary>
        /// The one sentence that says what is wrong with it.
        /// </summary>
        private static String Refuse(String Text)
        {

            Assert.That(V2GConfiguration.TryParse(JObject.Parse(Text), out _, out var error),
                        Is.False, $"'{Text}' was accepted and should not have been.");

            return error!;

        }

        #endregion


        #region APortOfZeroIsARealAnswer()

        /// <summary>
        /// Zero is how "any free port" is spelled, and this section is the one
        /// place in the file where that is a thing somebody can mean.
        /// </summary>
        /// <remarks>
        /// ConfigurationReader.TryReadPort refuses zero deliberately, because
        /// every other port in this document belongs to a client and zero says
        /// nothing to one. This port belongs to a listener. Reading it with the
        /// shared helper would have refused the ordinary case - and SDP exists
        /// precisely so that a station need not pick a port at all.
        /// </remarks>
        [Test]
        public void APortOfZeroIsARealAnswer()
        {

            Assert.Multiple(() => {
                Assert.That(Parse("""{ "port": 0 }""").    V2GPort,  Is.EqualTo((UInt16) 0));
                Assert.That(Parse("""{ "port": 15118 }""").V2GPort,  Is.EqualTo((UInt16) 15118));
                Assert.That(Parse("""{ "port": 65535 }""").V2GPort,  Is.EqualTo((UInt16) 65535));
            });

        }

        #endregion

        #region APortThatIsNotOneIsRefusedByName()

        [Test]
        public void APortThatIsNotOneIsRefusedByName()
        {

            Assert.Multiple(() => {
                Assert.That(Refuse("""{ "port": -1 }"""),       Does.Contain("v2g.port"));
                Assert.That(Refuse("""{ "port": 65536 }"""),    Does.Contain("v2g.port"));
                Assert.That(Refuse("""{ "port": "15118" }"""),  Does.Contain("v2g.port"));
            });

        }

        #endregion

        #region AnEVSEIdentificationTooLongForHomePlugIsRefused()

        /// <summary>
        /// Seventeen characters is what HomePlug carries, and the eighteenth
        /// is refused rather than quietly dropped.
        /// </summary>
        /// <remarks>
        /// V2GOptions cuts or pads to seventeen bytes on its way to the wire,
        /// which is right there and wrong here: a file that silently means
        /// something other than what it says turns up as an identification a
        /// vehicle logged differently, weeks later, and nobody goes looking for
        /// it in a settings document.
        /// </remarks>
        [Test]
        public void AnEVSEIdentificationTooLongForHomePlugIsRefused()
        {

            var seventeen = new String('A', 17);
            var eighteen  = new String('A', 18);

            Assert.Multiple(() => {

                Assert.That(Parse($$"""{ "evseId": "{{seventeen}}" }""").EVSEId,  Is.EqualTo(seventeen));

                var error = Refuse($$"""{ "evseId": "{{eighteen}}" }""");

                Assert.That(error,  Does.Contain("v2g.evseId"),
                            "An EVSE identification was refused without saying which field it was.");

            });

        }

        #endregion

        #region AnUnknownSLACTransportIsRefusedAndTheChoicesAreListed()

        /// <summary>
        /// A transport nobody has is refused, and the message says what there
        /// is instead.
        /// </summary>
        /// <remarks>
        /// "must be a valid SlacTransportKind" over a settings file has told
        /// nobody anything, which is why the message carries the list.
        /// </remarks>
        [Test]
        public void AnUnknownSLACTransportIsRefusedAndTheChoicesAreListed()
        {

            var error = Refuse("""{ "slac": "carrier-pigeon" }""");

            Assert.Multiple(() => {

                Assert.That(error,  Does.Contain("v2g.slac"));
                Assert.That(error,  Does.Contain("carrier-pigeon"),
                            "The message did not repeat what was actually written.");

                foreach (var kind in Enum.GetNames<SlacTransportKind>())
                    Assert.That(error,  Does.Contain(kind.ToLowerInvariant()),
                                $"The message did not offer '{kind}' as a choice.");

            });

        }

        #endregion

        #region ASLACTransportIsReadHoweverItIsWritten()

        [Test]
        [TestCase("auto",      SlacTransportKind.Auto)]
        [TestCase("Auto",      SlacTransportKind.Auto)]
        [TestCase("AUTO",      SlacTransportKind.Auto)]
        [TestCase("afpacket",  SlacTransportKind.AfPacket)]
        [TestCase("AfPacket",  SlacTransportKind.AfPacket)]
        [TestCase("udp",       SlacTransportKind.UDP)]
        [TestCase("none",      SlacTransportKind.None)]
        public void ASLACTransportIsReadHoweverItIsWritten(String Written, SlacTransportKind Expected)
        {
            Assert.That(Parse($$"""{ "slac": "{{Written}}" }""").SlacTransport,  Is.EqualTo(Expected));
        }

        #endregion

        #region HearingThisMachineIsOffUntilTheFileAsks()

        /// <summary>
        /// A station answers a vehicle on its own controller only when told to.
        /// </summary>
        /// <remarks>
        /// The default is the one that matters. A station in the field that
        /// answered SDP requests from its own machine would answer whatever
        /// simulator or test tool somebody left running on it, and would do so
        /// without anybody having asked for a bench.
        /// </remarks>
        [Test]
        public void HearingThisMachineIsOffUntilTheFileAsks()
        {

            var plain = new V2GOptions { Enabled = true, SDP = true };

            Assert.Multiple(() => {

                Assert.That(plain.MulticastLoopback,                       Is.False,
                            "A station heard its own machine without being asked.");

                Assert.That(Parse("{ }").Apply(plain).MulticastLoopback,   Is.False,
                            "A file with no opinion turned it on.");

                Assert.That(Parse("""{ "loopback": true }""").Apply(plain).MulticastLoopback,  Is.True);
                Assert.That(Parse("""{ "loopback": false }""").Apply(plain with { MulticastLoopback = true }).MulticastLoopback,
                            Is.False,
                            "The file could not switch it off again.");

            });

        }

        #endregion

        #region WhatTheFileDoesNotSayIsLeftAlone()

        /// <summary>
        /// The precedence rule, which is the same one the DNS and NTS sections
        /// keep: the command line asked for something, and the file has the
        /// last word on what it actually mentions.
        /// </summary>
        [Test]
        public void WhatTheFileDoesNotSayIsLeftAlone()
        {

            var asked = new V2GOptions {
                            Enabled        = true,
                            InterfaceName      = "eth0",
                            V2GPort            = 15118,
                            SDP                = true,
                            MulticastLoopback  = true,
                            SlacTransport      = SlacTransportKind.AfPacket,
                            EVSEId             = "DE*GEF*E0001*1"
                        };

            // A section that mentions one thing changes that one thing.
            var after = Parse("""{ "sdp": false }""").Apply(asked);

            Assert.Multiple(() => {
                Assert.That(after.SDP,                Is.False,                        "The one field the file mentioned did not change.");
                Assert.That(after.Enabled,            Is.True,                         "'enabled' was not mentioned and changed anyway.");
                Assert.That(after.MulticastLoopback,  Is.True,                         "'loopback' was not mentioned and changed anyway.");
                Assert.That(after.InterfaceName,  Is.EqualTo("eth0"));
                Assert.That(after.V2GPort,        Is.EqualTo((UInt16) 15118));
                Assert.That(after.SlacTransport,  Is.EqualTo(SlacTransportKind.AfPacket));
                Assert.That(after.EVSEId,         Is.EqualTo("DE*GEF*E0001*1"));
            });

        }

        #endregion

        #region WhatTheFileSaysWins()

        [Test]
        public void WhatTheFileSaysWins()
        {

            var asked = new V2GOptions {
                            Enabled        = false,
                            InterfaceName  = "eth0",
                            V2GPort        = 15118,
                            SDP            = false,
                            SlacTransport  = SlacTransportKind.None,
                            EVSEId         = "DE*GEF*E0001*1"
                        };

            var after = Parse("""
                              {
                                  "enabled":    true,
                                  "interface":  "eth1",
                                  "port":       0,
                                  "sdp":        true,
                                  "slac":       "udp",
                                  "evseId":     "DE*GEF*E0002*7"
                              }
                              """).Apply(asked);

            Assert.Multiple(() => {
                Assert.That(after.Enabled,        Is.True);
                Assert.That(after.InterfaceName,  Is.EqualTo("eth1"));
                Assert.That(after.V2GPort,        Is.EqualTo((UInt16) 0));
                Assert.That(after.SDP,            Is.True);
                Assert.That(after.SlacTransport,  Is.EqualTo(SlacTransportKind.UDP));
                Assert.That(after.EVSEId,         Is.EqualTo("DE*GEF*E0002*7"));
            });

        }

        #endregion

        #region AnEmptySectionChangesNothing()

        /// <summary>
        /// An empty object is a file with no opinion, not a file asking for
        /// the defaults.
        /// </summary>
        [Test]
        public void AnEmptySectionChangesNothing()
        {

            var asked = new V2GOptions {
                            Enabled        = true,
                            InterfaceName  = "eth0",
                            V2GPort        = 15118,
                            SDP            = false,
                            SlacTransport  = SlacTransportKind.UDP,
                            EVSEId         = "DE*GEF*E0001*1"
                        };

            Assert.That(Parse("{ }").Apply(asked),  Is.EqualTo(asked));

        }

        #endregion

        #region AnExplicitNullIsTheSameAsSayingNothing()

        [Test]
        public void AnExplicitNullIsTheSameAsSayingNothing()
        {

            var configuration = Parse("""
                                      {
                                          "enabled":    null,
                                          "interface":  null,
                                          "port":       null,
                                          "sdp":        null,
                                          "slac":       null,
                                          "evseId":     null
                                      }
                                      """);

            Assert.Multiple(() => {
                Assert.That(configuration.Enabled,        Is.Null);
                Assert.That(configuration.InterfaceName,  Is.Null);
                Assert.That(configuration.V2GPort,        Is.Null);
                Assert.That(configuration.SDP,            Is.Null);
                Assert.That(configuration.SlacTransport,  Is.Null);
                Assert.That(configuration.EVSEId,         Is.Null);
            });

        }

        #endregion

        #region TheSectionComesBackOutAsItWentIn()

        /// <summary>
        /// What is written to the file reads back as the same section, and
        /// what the file was not told about is not written at all.
        /// </summary>
        [Test]
        public void TheSectionComesBackOutAsItWentIn()
        {

            var written = new V2GConfiguration(
                              Enabled:        true,
                              InterfaceName:  "eth1",
                              V2GPort:        0,
                              SDP:            false,
                              Loopback:       true,
                              SlacTransport:  SlacTransportKind.AfPacket,
                              EVSEId:         "DE*GEF*E0002*7"
                          );

            Assert.That(Parse(written.ToJSON().ToString()),  Is.EqualTo(written));

            // And nothing it was not told is invented on the way out.
            Assert.That(new V2GConfiguration(SDP: true).ToJSON().Properties().Select(one => one.Name),
                        Is.EquivalentTo(new[] { "sdp" }));

        }

        #endregion

    }

}
