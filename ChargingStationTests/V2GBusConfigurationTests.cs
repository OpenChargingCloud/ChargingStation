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

using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The 10BASE-T1S bus of a megawatt coupler, as the "v2g" section of the
    /// configuration file carries it.
    /// </summary>
    /// <remarks>
    /// No bus is opened here and no socket is bound: everything below is the
    /// section being read and laid over the options a station already has.
    /// A bus that really carries frames is the T1S library's own suite, which
    /// runs whole buses over real multicast sockets.
    /// </remarks>
    [TestFixture]
    public class V2GBusConfigurationTests
    {

        #region (private static) Parse(Text) / Refuse(Text)

        private static V2GConfiguration Parse(String Text)
        {

            Assert.That(V2GConfiguration.TryParse(JObject.Parse(Text), out var configuration, out var error),
                        Is.True, $"'{Text}' was refused: {error}");

            return configuration!;

        }

        private static String Refuse(String Text)
        {

            Assert.That(V2GConfiguration.TryParse(JObject.Parse(Text), out _, out var error),
                        Is.False, $"'{Text}' was accepted and should not have been.");

            return error!;

        }

        #endregion


        #region A section that says nothing about the bus

        [Test]
        public void ASectionWithoutTheBusLeavesItAlone()
        {

            var asked = new V2GOptions {
                            T1S = new T1SOptions(T1STransportKind.UDP, Name: "EVSE-7")
                        };

            var after = Parse("""{ "sdp": true }""").Apply(asked);

            Assert.That(after.T1S,       Is.SameAs(asked.T1S));
            Assert.That(after.T1S!.Name, Is.EqualTo("EVSE-7"));

        }

        [Test]
        public void AStationWithoutABusStillHasNone()
        {

            var after = Parse("""{ "sdp": true }""").Apply(new V2GOptions());

            Assert.That(after.T1S, Is.Null);

        }

        #endregion

        #region Naming one

        [Test]
        public void ATransportAndAGroupMakeABus()
        {

            var after = Parse("""
                              {
                                  "t1sTransport":  "udp",
                                  "t1sBus":        "239.151.18.1:2354",
                                  "t1sName":       "EVSE-1",
                                  "t1sCycleMs":    250
                              }
                              """).Apply(new V2GOptions());

            Assert.Multiple(() => {
                Assert.That(after.T1S,                Is.Not.Null);
                Assert.That(after.T1S!.Transport,     Is.EqualTo(T1STransportKind.UDP));
                Assert.That(after.T1S.Group?.Port,    Is.EqualTo(2354));
                Assert.That(after.T1S.Name,           Is.EqualTo("EVSE-1"));
                Assert.That(after.T1S.CycleGap,       Is.EqualTo(TimeSpan.FromMilliseconds(250)));
            });

        }

        [Test]
        public void AGroupAloneMeansTheEmulatedMedium()
        {

            // The same rule the vehicle follows: a group is a thing only the
            // emulated medium has, so naming one says which medium without a
            // second field.
            var after = Parse("""{ "t1sBus": "239.151.18.1:16118" }""").Apply(new V2GOptions());

            Assert.That(after.T1S?.Transport, Is.EqualTo(T1STransportKind.UDP));

        }

        [Test]
        public void AnAdapterNeedsNoGroup()
        {

            var after = Parse("""
                              {
                                  "t1sTransport":  "afpacket",
                                  "t1sInterface":  "t1s0"
                              }
                              """).Apply(new V2GOptions());

            Assert.Multiple(() => {
                Assert.That(after.T1S?.Transport,      Is.EqualTo(T1STransportKind.AfPacket));
                Assert.That(after.T1S?.InterfaceName,  Is.EqualTo("t1s0"));
                Assert.That(after.T1S?.Group,          Is.Null, "the library's default group, which AF_PACKET ignores anyway");
            });

        }

        #endregion

        #region Taking one away

        [Test]
        public void NoneIsHowABusIsTakenAway()
        {

            var asked = new V2GOptions {
                            T1S = new T1SOptions(T1STransportKind.UDP)
                        };

            Assert.That(Parse("""{ "t1sTransport": "none" }""").Apply(asked).T1S, Is.Null);

        }

        [Test]
        public void RenamingTheCoordinatorDoesNotTakeTheBusAway()
        {

            // A station that stopped coordinating because somebody renamed it
            // would be a surprise nobody asked for.
            var asked = new V2GOptions {
                            T1S = new T1SOptions(T1STransportKind.UDP, Name: "EVSE")
                        };

            var after = Parse("""{ "t1sName": "Coupler A" }""").Apply(asked);

            Assert.Multiple(() => {
                Assert.That(after.T1S,             Is.Not.Null);
                Assert.That(after.T1S!.Transport,  Is.EqualTo(T1STransportKind.UDP));
                Assert.That(after.T1S.Name,        Is.EqualTo("Coupler A"));
            });

        }

        [Test]
        public void SettingsForABusThatDoesNotExistChangeNothing()
        {

            // A cycle for a medium nobody named is a setting with nothing to
            // settle on, and inventing a bus out of it would be worse than
            // ignoring it.
            Assert.That(Parse("""{ "t1sCycleMs": 250 }""").Apply(new V2GOptions()).T1S, Is.Null);

        }

        #endregion

        #region What the file says wins, field by field

        [Test]
        public void EverythingIsLaidOverWhatTheStationHad()
        {

            var asked = new V2GOptions {
                            T1S = new T1SOptions(
                                      Transport:      T1STransportKind.UDP,
                                      InterfaceName:  "eth0",
                                      Name:           "EVSE",
                                      CycleGap:       TimeSpan.FromMilliseconds(200)
                                  )
                        };

            var after = Parse("""
                              {
                                  "t1sInterface":  "eth1",
                                  "t1sWarningC":   60,
                                  "t1sOverloadC":  85
                              }
                              """).Apply(asked);

            Assert.Multiple(() => {
                Assert.That(after.T1S!.Transport,          Is.EqualTo(T1STransportKind.UDP), "not mentioned, so kept");
                Assert.That(after.T1S.InterfaceName,       Is.EqualTo("eth1"));
                Assert.That(after.T1S.Name,                Is.EqualTo("EVSE"));
                Assert.That(after.T1S.CycleGap,            Is.EqualTo(TimeSpan.FromMilliseconds(200)));
                Assert.That(after.T1S.Thermal?.Warning_C,  Is.EqualTo(60));
                Assert.That(after.T1S.Thermal?.Overload_C, Is.EqualTo(85));
            });

        }

        [Test]
        public void WhatIsWrittenIsReadBack()
        {

            var written = new V2GConfiguration(
                              T1STransport:   T1STransportKind.AfPacket,
                              T1SBus:         "239.151.18.9:2000",
                              T1SInterface:   "t1s0",
                              T1SName:        "Coupler B",
                              T1SCycle:       TimeSpan.FromMilliseconds(400),
                              T1SWarning_C:   65,
                              T1SOverload_C:  95
                          ).ToJSON();

            Assert.That(written.Value<String>("t1sTransport"), Is.EqualTo("afpacket"));
            Assert.That(written.Value<Double>("t1sCycleMs"),   Is.EqualTo(400));

            Assert.That(V2GConfiguration.TryParse(written, out var read, out var error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(read!.T1STransport,   Is.EqualTo(T1STransportKind.AfPacket));
                Assert.That(read.T1SBus,          Is.EqualTo("239.151.18.9:2000"));
                Assert.That(read.T1SInterface,    Is.EqualTo("t1s0"));
                Assert.That(read.T1SName,         Is.EqualTo("Coupler B"));
                Assert.That(read.T1SCycle,        Is.EqualTo(TimeSpan.FromMilliseconds(400)));
                Assert.That(read.T1SWarning_C,    Is.EqualTo(65));
                Assert.That(read.T1SOverload_C,   Is.EqualTo(95));
                Assert.That(read.T1SBusEndpoint?.Port, Is.EqualTo(2000));
            });

        }

        #endregion

        #region Refused by name

        [Test]
        public void AnUnknownTransportIsRefusedAndTheChoicesAreListed()
        {

            var error = Refuse("""{ "t1sTransport": "pcap" }""");

            Assert.That(error, Does.Contain("v2g.t1sTransport"));
            Assert.That(error, Does.Contain("afpacket"));

        }

        [TestCase("127.0.0.1:2354",        "not a multicast group")]
        [TestCase("239.151.18.1",          "no port")]
        [TestCase("[ff02::1]:2354",        "IPv6, which the emulation is not")]
        [TestCase("bus.example.org:2354",  "a name rather than an address")]
        [TestCase("239.151.18.1:0",        "port zero")]
        public void ABusThatIsNotAnIPv4GroupIsRefused(String Written, String Why)
        {

            var error = Refuse($$"""{ "t1sBus": "{{Written}}" }""");

            Assert.That(error, Does.Contain("v2g.t1sBus"), Why);
            Assert.That(error, Does.Contain("multicast"));

        }

        [TestCase(10)]
        [TestCase(60000)]
        public void ACycleOutsideWhatABusCouldDoIsRefused(Int32 Milliseconds)
        {

            Assert.That(Refuse($$"""{ "t1sCycleMs": {{Milliseconds}} }"""), Does.Contain("v2g.t1sCycleMs"));

        }

        [Test]
        public void HalfAPairOfThermalLimitsIsRefused()
        {

            // They make sense only against each other, and nothing in a merge
            // could catch a warning that ended up above the overload.
            Assert.That(Refuse("""{ "t1sWarningC": 70 }"""),  Does.Contain("belong together"));
            Assert.That(Refuse("""{ "t1sOverloadC": 90 }"""), Does.Contain("belong together"));

        }

        [Test]
        public void AWarningAboveTheOverloadIsRefused()
        {

            var error = Refuse("""{ "t1sWarningC": 95, "t1sOverloadC": 90 }""");

            Assert.That(error, Does.Contain("must be below"));

        }

        #endregion

    }

}
