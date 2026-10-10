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

using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso20;
using cloud.charging.open.protocols.ISO15118.T1S.Monitoring;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A station with a Megawatt Charging System coupler: it offers MCS, and
    /// what the temperature sensors in the coupler's pins say reaches the
    /// charge loop - a warm pin halves the current, an overloaded one or one
    /// nobody hears any more ends the charging.
    /// </summary>
    [TestFixture]
    public class V2GMegawattCouplerTests
    {

        private static readonly TimeSpan timeout = TimeSpan.FromSeconds(60);


        #region Which station is one with a megawatt coupler

        [Test]
        public void ADCStationWithABusIsOne()
        {

            Assert.Multiple(() => {

                foreach (var transport in new[] { T1STransportKind.Auto, T1STransportKind.AfPacket, T1STransportKind.UDP })
                    Assert.That(V2GLink.IsMegawattCoupler(new V2GOptions { Mode = PowerMode.Dc, T1S = new T1SOptions(transport) }),
                                Is.True, $"a DC station with a bus on '{transport}'");

            });

        }

        [Test]
        public void AStationWithoutABusIsNone()
        {

            Assert.Multiple(() => {
                Assert.That(V2GLink.IsMegawattCoupler(new V2GOptions { Mode = PowerMode.Dc }),                                                      Is.False);
                Assert.That(V2GLink.IsMegawattCoupler(new V2GOptions { Mode = PowerMode.Dc, T1S = new T1SOptions(T1STransportKind.None) }),         Is.False);
            });

        }

        [Test]
        public void AnACStationIsNoneWithABusAsWithout()
        {
            Assert.That(V2GLink.IsMegawattCoupler(new V2GOptions { Mode = PowerMode.Ac, T1S = new T1SOptions(T1STransportKind.UDP) }), Is.False);
        }

        #endregion

        #region Which session it runs

        [Test]
        public void AMegawattCouplerRunsMCSOnDC()
        {

            Assert.Multiple(() => {

                Assert.That(V2GLink.NewSecc20(PowerMode.Dc, MegawattCoupler: true,  timeout, TimeProvider.System), Is.TypeOf<Secc20Mcs>());
                Assert.That(V2GLink.NewSecc20(PowerMode.Dc, MegawattCoupler: false, timeout, TimeProvider.System), Is.TypeOf<Secc20Dc>());
                Assert.That(V2GLink.NewSecc20(PowerMode.Ac, MegawattCoupler: false, timeout, TimeProvider.System), Is.TypeOf<Secc20Ac>());

            });

        }

        [Test]
        public void MCSHasTheMegawattEnvelope()
        {

            var mcs = (Secc20Dc) V2GLink.NewSecc20(PowerMode.Dc, MegawattCoupler: true,  timeout, TimeProvider.System);
            var dc  = (Secc20Dc) V2GLink.NewSecc20(PowerMode.Dc, MegawattCoupler: false, timeout, TimeProvider.System);

            Assert.That(mcs.MaximumCurrent_A, Is.GreaterThan(dc.MaximumCurrent_A));

        }

        #endregion

        #region What the pins mean for the charging

        [Test]
        public void AllNormalIsTheFullCurrent()
        {
            Assert.That(V2GLink.ThermalAction(ThermalState.Normal, 3000), Is.EqualTo(((Double?) null, false)));
        }

        [Test]
        public void AWarmPinHalvesTheCurrent()
        {
            Assert.That(V2GLink.ThermalAction(ThermalState.Warning, 3000), Is.EqualTo(((Double?) 1500, false)));
        }

        [TestCase(ThermalState.Overload)]
        [TestCase(ThermalState.Lost)]
        public void AnOverloadedOrALostPinEndsTheCharging(ThermalState State)
        {

            var (limit, terminate) = V2GLink.ThermalAction(State, 3000);

            Assert.Multiple(() => {
                Assert.That(terminate,  Is.True);
                Assert.That(limit,      Is.EqualTo(0));
            });

        }

        #endregion

    }

}
