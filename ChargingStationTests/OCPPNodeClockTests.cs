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

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What time an OCPP networking node thinks it is.
    /// </summary>
    /// <remarks>
    /// A node used to have no clock of its own at all: everything inside it
    /// read the one global Timestamp.Now, so a station moved through time
    /// took its display and its configuration along but left its OCPP side
    /// standing in the present. KioskTests says as much and works around it
    /// by starting its fixture at the real now.
    ///
    /// These are the tests of the seam that ends that - of the node having a
    /// clock, not yet of every timestamp inside it reading that clock.
    /// </remarks>
    [TestFixture]
    public class OCPPNodeClockTests
    {

        #region Data

        /// <summary>
        /// A clock standing still at a moment somebody picked.
        /// </summary>
        private sealed class StoppedClock(DateTimeOffset At) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => At;
        }

        private static readonly DateTimeOffset LastMarch = new (2026, 3, 14, 9, 26, 53, TimeSpan.Zero);

        private static protocols.OCPPv2_1.CS.TestChargingStationNode Station(TimeProvider? Clock = null)

            => new (Id:           NetworkingNode_Id.Parse("clock01"),
                    VendorName:   "gef",
                    Model:        "cs1",
                    Clock:        Clock);

        #endregion


        #region ANodeReadsTheClockItWasGiven()

        /// <summary>
        /// A node handed a clock says what that clock says.
        /// </summary>
        [Test]
        public void ANodeReadsTheClockItWasGiven()
        {

            var node = Station(new StoppedClock(LastMarch));

            Assert.That(node.Now,  Is.EqualTo(LastMarch),
                        "A node was handed a clock standing in March and did not read it.");

        }

        #endregion

        #region TheClockReachesEveryAdapter()

        /// <summary>
        /// And so does everything the node sends, receives or forwards through.
        /// </summary>
        /// <remarks>
        /// Not decoration: 2309 of the timestamps in this protocol stack are
        /// written inside these three adapters, which is why they are the ones
        /// that have to be able to reach the node's clock at all.
        /// </remarks>
        [Test]
        public void TheClockReachesEveryAdapter()
        {

            var node = Station(new StoppedClock(LastMarch));

            Assert.Multiple(() => {
                Assert.That(node.OCPP.Now,          Is.EqualTo(LastMarch), "The OCPP adapter is not on the node's clock.");
                Assert.That(node.OCPP.IN.Now,       Is.EqualTo(LastMarch), "Incoming is not on the node's clock.");
                Assert.That(node.OCPP.OUT.Now,      Is.EqualTo(LastMarch), "Outgoing is not on the node's clock.");
                Assert.That(node.OCPP.FORWARD.Now,  Is.EqualTo(LastMarch), "Forwarding is not on the node's clock.");
            });

        }

        #endregion

        #region ANodeWithoutAClockKeepsTravellingInTime()

        /// <summary>
        /// A node handed no clock behaves exactly as it did before it could be
        /// handed one - time travel included.
        /// </summary>
        /// <remarks>
        /// This is the test of the default value, and the default value is the
        /// part worth testing. TimeProvider.System is the obvious choice and
        /// the wrong one: it reads the system clock directly, so every node
        /// that nobody passed a clock to would have quietly stopped following
        /// Timestamp.TravelBackInTime. Nothing in this repository travels in
        /// time today, which is precisely what would have made that the kind
        /// of change found a year later by somebody else.
        /// </remarks>
        [Test]
        public void ANodeWithoutAClockKeepsTravellingInTime()
        {

            var node = Station();

            try
            {

                var before = node.Now;
                Assert.That((before - Timestamp.Now).Duration(), Is.LessThan(TimeSpan.FromSeconds(1)),
                            "A node with no clock of its own is not reading the global one.");

                Timestamp.TravelForwardInTime(TimeSpan.FromDays(400));

                Assert.That(node.Now - before, Is.GreaterThan(TimeSpan.FromDays(399)),
                            "The global clock travelled 400 days forward and the node stayed behind.");

            }
            finally
            {
                Timestamp.Reset();
            }

        }

        #endregion

    }

}
