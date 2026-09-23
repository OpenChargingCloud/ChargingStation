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

using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.NetworkInterfaces;
using cloud.charging.open.protocols.ISO15118.SDP.Messages;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What this station tells a vehicle about itself over SDP.
    /// </summary>
    /// <remarks>
    /// One question, asked of a handful of values rather than of a socket:
    /// does what the station offers match what it accepts?
    ///
    /// It did not, and nothing said so. A station without a certificate
    /// advertised NoTLS - correctly, since advertising TLS it cannot speak
    /// sends a vehicle into a handshake that cannot finish - while keeping the
    /// library's CRA/NIS2 default of dropping every request that asks for
    /// NoTLS. So it answered nobody, in exactly the configuration that
    /// "--v2g" without "--v2g-cert" produces, and the only sign of it was a
    /// warning on one side and a timeout on the other.
    /// </remarks>
    [TestFixture]
    public class SDPSecurityPolicyTests
    {

        #region (private) Interface()

        private static V2GNetworkInterface Interface()

            => new (12,
                    "plc0",
                    IPAddress.Parse("fe80::223:5ff:fe42:1"),
                    [ 0x00, 0x23, 0x05, 0x42, 0x01, 0x01 ]);

        #endregion


        #region AStationWithoutACertificateAnswersAVehicleAskingForPlainTCP()

        /// <summary>
        /// The case that was broken, and the one a bench is in by default.
        /// </summary>
        [Test]
        public void AStationWithoutACertificateAnswersAVehicleAskingForPlainTCP()
        {

            var options = V2GLink.SDPOptionsFor(Interface(), 16000, UsesTLS: false, MulticastLoopback: false);

            Assert.Multiple(() => {

                Assert.That(options.OfferedSecurity,      Is.EqualTo(SDP_Security.NoTLS),
                            "a station without a certificate that advertised TLS would send vehicles into a handshake that cannot finish");

                Assert.That(options.RejectNoTlsRequests,  Is.False,
                            "it advertised no TLS and then dropped every vehicle that asked for no TLS - discoverable by nobody");

            });

        }

        #endregion

        #region AStationWithACertificateTurnsAwayADowngrade()

        /// <summary>
        /// And the posture the library defaults to is kept where it belongs.
        /// </summary>
        [Test]
        public void AStationWithACertificateTurnsAwayADowngrade()
        {

            var options = V2GLink.SDPOptionsFor(Interface(), 15118, UsesTLS: true, MulticastLoopback: false);

            Assert.Multiple(() => {
                Assert.That(options.OfferedSecurity,      Is.EqualTo(SDP_Security.TLS));
                Assert.That(options.RejectNoTlsRequests,  Is.True,  "ISO 15118-20 asks for TLS, and a downgrade is not this station's to grant");
            });

        }

        #endregion

        #region WhatItOffersIsWhatItAccepts()

        /// <summary>
        /// The invariant itself, rather than its two instances: a station
        /// refuses a plain request exactly when it has something better to
        /// offer.
        /// </summary>
        [Test]
        public void WhatItOffersIsWhatItAccepts()
        {

            Assert.Multiple(() => {

                foreach (var usesTLS in new[] { true, false })
                {

                    var options = V2GLink.SDPOptionsFor(Interface(), 15118, usesTLS, MulticastLoopback: false);

                    Assert.That(options.RejectNoTlsRequests,
                                Is.EqualTo(options.OfferedSecurity == SDP_Security.TLS),
                                $"a station with UsesTLS={usesTLS} offers {options.OfferedSecurity} and rejects no-TLS requests: {options.RejectNoTlsRequests}");

                }

            });

        }

        #endregion

        #region TheRestIsCarriedThrough()

        [Test]
        public void TheRestIsCarriedThrough()
        {

            var options = V2GLink.SDPOptionsFor(Interface(), 16000, UsesTLS: false, MulticastLoopback: true);

            Assert.Multiple(() => {

                // The port SDP advertises is the one the endpoint actually got,
                // which is why it is handed in rather than looked up.
                Assert.That(options.SeccPort,           Is.EqualTo((UInt16) 16000));
                Assert.That(options.Interface.Name,     Is.EqualTo("plc0"));

                // Off in the field; on for a bench where the car runs here too.
                Assert.That(options.MulticastLoopback,  Is.True);

            });

        }

        #endregion

    }

}
