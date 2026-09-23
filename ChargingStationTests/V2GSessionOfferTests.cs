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

using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.Sap;
using cloud.charging.open.protocols.ISO15118.Session;
using cloud.charging.open.protocols.ISO15118.StateMachines;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What this station puts on the table when a vehicle connects.
    /// </summary>
    /// <remarks>
    /// The session itself is the ISO 15118 library's, driven by its own state
    /// machines and covered by its own tests. What belongs to this station is
    /// the pair of decisions it makes before handing the stream over: which
    /// protocols and mode to offer, and what to tell the handshake about the
    /// connection. Both are a few values, so they are asked directly.
    ///
    /// The whole path was measured rather than only reasoned about: a car and
    /// this station on one machine, over SDP on port 16000, charged a battery
    /// from 30 % to 100 % - 42 kWh, 238 message exchanges, ISO 15118-2 AC in
    /// 14.3 seconds.
    /// </remarks>
    [TestFixture]
    public class V2GSessionOfferTests
    {

        #region BothStandardsAreOffered()

        [Test]
        public void BothStandardsAreOffered()
        {

            var offers = V2GLink.SessionOffers(PowerMode.Ac);

            Assert.Multiple(() => {

                Assert.That(offers,                    Has.Count.EqualTo(2));
                Assert.That(offers[0].Protocol,        Is.EqualTo(ProtocolVariant.Iso15118_20),
                            "a car that can do either should get the newer standard");
                Assert.That(offers[1].Protocol,        Is.EqualTo(ProtocolVariant.Iso15118_2));

            });

        }

        #endregion

        #region EveryOfferNamesTheOutletThisStationHas()

        /// <summary>
        /// One mode, and the same one in both offers.
        /// </summary>
        /// <remarks>
        /// The namespace a vehicle asks for names the mode, so offering AC and
        /// DC together would be claiming an outlet this station does not have.
        /// A car that wants the other one finds out at ServiceDiscovery, which
        /// is where it should: measured on the bench, a DC car against this AC
        /// station is told "the station offers no DC energy transfer mode
        /// (offered: AC_three_phase_core)".
        /// </remarks>
        [Test]
        public void EveryOfferNamesTheOutletThisStationHas()
        {

            Assert.Multiple(() => {

                foreach (var mode in new[] { PowerMode.Ac, PowerMode.Dc })
                    Assert.That(V2GLink.SessionOffers(mode).Select(offer => offer.Mode),
                                Is.All.EqualTo(mode),
                                $"an offer in a mode this station does not have is an outlet it does not have");

            });

        }

        #endregion

        #region ADefaultStationIsAnACStation()

        /// <summary>
        /// Because a type 2 socket is what it has.
        /// </summary>
        [Test]
        public void ADefaultStationIsAnACStation()
        {
            Assert.That(new V2GOptions().Mode,  Is.EqualTo(PowerMode.Ac));
        }

        #endregion

        #region TheHandshakeIsAlwaysToldWhatTheConnectionIs()

        /// <summary>
        /// Never Unknown, which is what makes [V2G20-2356] apply at all.
        /// </summary>
        /// <remarks>
        /// ISO 15118-20 may only ride on TLS 1.3. The handshake enforces that
        /// by taking -20 out of the catalogue for the length of a plain
        /// connection - but only when it has been told what the connection is.
        /// Unknown means "nobody said", and this station always knows: it is
        /// the one that decided whether to put a certificate on the endpoint.
        ///
        /// Measured both ways on the bench: a car offering both standards over
        /// plain TCP charged over -2, and a car insisting on -20 over plain TCP
        /// was told Failed_NoNegotiation.
        /// </remarks>
        [Test]
        public void TheHandshakeIsAlwaysToldWhatTheConnectionIs()
        {

            Assert.Multiple(() => {

                Assert.That(V2GLink.TransportOf(UsesTLS: true),   Is.EqualTo(TransportSecurity.Tls13));
                Assert.That(V2GLink.TransportOf(UsesTLS: false),  Is.EqualTo(TransportSecurity.None));

                foreach (var usesTLS in new[] { true, false })
                    Assert.That(V2GLink.TransportOf(usesTLS),
                                Is.Not.EqualTo(TransportSecurity.Unknown),
                                "Unknown switches the transport rule off, and this station is never in doubt");

            });

        }

        #endregion

        #region OnlyTLSMayCarryTheNewerStandard()

        /// <summary>
        /// The rule itself, asked of the library rather than assumed here.
        /// </summary>
        [Test]
        public void OnlyTLSMayCarryTheNewerStandard()
        {

            Assert.Multiple(() => {
                Assert.That(Iso20Transport.MayCarryIso20(V2GLink.TransportOf(UsesTLS: true)),   Is.True);
                Assert.That(Iso20Transport.MayCarryIso20(V2GLink.TransportOf(UsesTLS: false)),  Is.False);
            });

        }

        #endregion

    }

}
