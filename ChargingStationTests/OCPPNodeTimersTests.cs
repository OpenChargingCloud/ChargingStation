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

using System.Reflection;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.ChargingStation.EVSEs;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether a charging station that is let go of stops the timers of its
    /// OCPP nodes, and whether its nodes bring no timers of their own.
    /// </summary>
    /// <remarks>
    /// An OCPP node's heartbeat and maintenance timers tick until the node is
    /// stopped, and its maintenance ticked every second, holding a thread of
    /// the thread pool while it waited for a lock every node of the process
    /// shared. A station used to leave its two nodes ticking when it was let go
    /// of, and those of every change of its EVSEs as well. A test run makes a
    /// station for nearly every test; on GitHub's Windows runners, with four
    /// cores, the thread pool ran dry and the run hung, in whichever test came
    /// next.
    ///
    /// The timers are the node's own, and nothing a node says in public tells
    /// whether it was stopped, so these tests look at the timers themselves:
    /// a timer that was disposed of answers a change with false. A DNS client
    /// a node made itself brings a cache whose timer nothing stops, so the
    /// nodes are asked whose DNS client they have.
    /// </remarks>
    [TestFixture]
    public class OCPPNodeTimersTests
    {

        #region Data

        private String directory = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("timers");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region (private static) TicksStill(Node, TimerName)

        /// <summary>
        /// Whether a timer of a node still ticks: one that does is held where
        /// it is, which for one that should have been stopped is no loss.
        /// </summary>
        /// <param name="Node">The node.</param>
        /// <param name="TimerName">The name of its timer.</param>
        private static Boolean TicksStill(AWWCPNetworkingNode  Node,
                                          String               TimerName)
        {

            var timer = typeof(AWWCPNetworkingNode).GetField(TimerName, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(Node) as Timer;

            Assert.That(timer, Is.Not.Null, $"An OCPP node has no timer '{TimerName}' any more: this test has to learn where its timers are now.");

            return timer!.Change(Timeout.Infinite, Timeout.Infinite);

        }

        #endregion

        #region (private static) StillTicking(Nodes)

        /// <summary>
        /// The timers of the given nodes that still tick, by node and name.
        /// </summary>
        private static String[] StillTicking(IEnumerable<AWWCPNetworkingNode> Nodes)

            => [.. Nodes.SelectMany(node => new[] { "MaintenanceTimer", "SendHeartbeatsTimer" }.
                                                Where (timer => TicksStill(node, timer)).
                                                Select(timer => $"{node.Id} {timer}"))];

        #endregion


        #region AStationLetGoOfStopsTheTimersOfItsOCPPNodes()

        /// <summary>
        /// Both OCPP nodes of a station that is let go of are stopped: their
        /// heartbeat and maintenance timers no longer tick.
        /// </summary>
        [Test]
        public async Task AStationLetGoOfStopsTheTimersOfItsOCPPNodes()
        {

            var station  = TestStations.New(directory);

            AWWCPNetworkingNode[] nodes = [ station.OCPPv1_6Node, station.OCPPv2_1Node ];

            await station.DisposeAsync();

            Assert.That(StillTicking(nodes), Is.Empty);

        }

        #endregion

        #region TheNodesAChangeOfTheEVSEsReplacedAreStoppedWithTheStation()

        /// <summary>
        /// The OCPP nodes a change of the EVSEs replaced are stopped when the
        /// station is let go of, with the two it has then.
        /// </summary>
        [Test]
        public async Task TheNodesAChangeOfTheEVSEsReplacedAreStoppedWithTheStation()
        {

            var station  = TestStations.New(directory);
            var nodes    = new List<AWWCPNetworkingNode>();

            try
            {

                for (var change = 0; change < 2; change++)
                {

                    nodes.AddRange([ station.OCPPv1_6Node, station.OCPPv2_1Node ]);

                    var switched = new JObject(
                                       new JProperty("evses", new JArray(
                                           station.EVSEs.Select(evse => {
                                               var json = evse.ToJSON();
                                               json["operative"] = !evse.Operative;
                                               return json;
                                           })
                                       ))
                                   );

                    Assert.That(station.TryUpdateEVSEConfiguration(switched, _ => true, out var kind, out var error, out _), Is.True, error);
                    Assert.That(kind,                  Is.Not.EqualTo(EVSEChange.None), "switching the EVSEs is a change");
                    Assert.That(station.OCPPv2_1Node,  Is.Not.SameAs(nodes[^1]),        "the nodes were built anew");

                }

                nodes.AddRange([ station.OCPPv1_6Node, station.OCPPv2_1Node ]);

            }
            finally
            {
                await station.DisposeAsync();
            }

            Assert.That(StillTicking(nodes), Is.Empty);

        }

        #endregion

        #region BothOCPPNodesResolveNamesWithTheStationsDNSClient()

        /// <summary>
        /// Both OCPP nodes resolve names with the station's DNS client, and so
        /// do the nodes a change of the EVSEs builds anew.
        /// </summary>
        /// <remarks>
        /// The OCPP 1.6 node was handed none, and made one of its own: a cache
        /// of its own with a timer of its own, which nothing stopped - one more
        /// ticking timer for every station, and for every change of its EVSEs.
        /// </remarks>
        [Test]
        public async Task BothOCPPNodesResolveNamesWithTheStationsDNSClient()
        {

            var station = TestStations.New(directory);

            try
            {

                Assert.Multiple(() => {
                    Assert.That(station.OCPPv1_6Node.DNSClient,  Is.SameAs(station.DNSClient), "the OCPP 1.6 node has a DNS client of its own");
                    Assert.That(station.OCPPv2_1Node.DNSClient,  Is.SameAs(station.DNSClient), "the OCPP 2.1 node has a DNS client of its own");
                });

                var switched = new JObject(
                                   new JProperty("evses", new JArray(
                                       station.EVSEs.Select(evse => {
                                           var json = evse.ToJSON();
                                           json["operative"] = !evse.Operative;
                                           return json;
                                       })
                                   ))
                               );

                Assert.That(station.TryUpdateEVSEConfiguration(switched, _ => true, out _, out var error, out _), Is.True, error);

                Assert.That(station.OCPPv1_6Node.DNSClient, Is.SameAs(station.DNSClient), "the OCPP 1.6 node built anew has a DNS client of its own");

            }
            finally
            {
                await station.DisposeAsync();
            }

        }

        #endregion

    }

}
