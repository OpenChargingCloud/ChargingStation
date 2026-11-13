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
using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.OCPP.Node.TestKit;

using cloud.charging.open.ChargingStation.EVSEs;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    // WWCP_OCPP_Node_TestKit, asked of a charging station: what OCPP 2.1 and
    // 1.6 read and change of its name servers and time servers, through the
    // OCPP nodes it builds, the connections it keeps and the queues it keeps
    // on disk.

    [TestFixture] public class DNSDeviceModelOnAStation         : DNSDeviceModelTests          { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSDeviceModelWritingOnAStation  : DNSDeviceModelWritingTests   { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class SSHPeerDeviceModelOnAStation : SSHPeerDeviceModelTests { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class SSHPeerKeys16OnAStation : SSHPeerKeys16Tests { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSEquivalenceOnAStation         : DNSEquivalenceTests          { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSEventsOnAStation              : DNSEventsTests               { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSKeys16OnAStation              : DNSKeys16Tests               { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSOverSSHDeviceModelOnAStation  : DNSOverSSHDeviceModelTests   { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSRootCertificatesOnAStation    : DNSRootCertificatesTests     { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class DNSTestCriteriaOnAStation        : DNSTestCriteriaTests         { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class QueryDNSOnAStation               : QueryDNSTests                { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }

    [TestFixture] public class NTSDeviceModelOnAStation         : NTSDeviceModelTests          { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class NTSDeviceModelWritingOnAStation  : NTSDeviceModelWritingTests   { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class NTSEquivalenceOnAStation         : NTSEquivalenceTests          { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class NTSEventsOnAStation              : NTSEventsTests               { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class NTSKeys16OnAStation              : NTSKeys16Tests               { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }
    [TestFixture] public class NTSRootCertificatesOnAStation    : NTSRootCertificatesTests     { protected override IBridgedNodes Nodes { get; } = new StationNodes(); }


    /// <summary>
    /// How a charging station is made for the kit's tests: as TestStations
    /// makes it, without a display.
    /// </summary>
    internal sealed class StationNodes : IBridgedNodes
    {

        public IBridgedNode New(String         Directory,
                                JObject        Configuration,
                                TimeProvider?  Clock   = null)

            => new StationNode(TestStations.New(Directory, Configuration, WithDisplay: false, Clock: Clock));


        public async Task<IBridgedNode> Started(String   Directory,
                                                JObject  Configuration)

            // Made again, on fresh ports, where another test run on this
            // machine took one of them before the station could bind it.
            => new StationNode(await TestPorts.StartedOnFreshPorts(() => TestStations.New(Directory, Configuration, WithDisplay: false)));

    }


    /// <summary>
    /// A charging station, as the kit's tests see it.
    /// </summary>
    internal sealed class StationNode(ChargingStation Station) : IBridgedNode
    {

        public WWCPNode                             Node            => Station;

        public DeviceModelBridge                    Bridge          => Station.DeviceModelBridge;

        public OCPPv2_1.IDeviceModelNode            OCPPv2_1Node    => Station.OCPPv2_1Node;

        public OCPPv1_6.CP.IConfigurationKeysNode   OCPPv1_6Node    => Station.OCPPv1_6Node;


        /// <summary>
        /// A connection the station writes down and does not dial, since it
        /// was not started - or was, and dials only what it was told to before.
        /// </summary>
        public Boolean TryDial(String       URL,
                               String       OCPPVersion,
                               out String?  Error)

            => Station.Connections.TryAddConnection($"A CSMS of OCPP {OCPPVersion}", URL, "CSMS", false, null, null,
                                                    out _, out Error, OCPPVersion: OCPPVersion);


        /// <summary>
        /// The EVSEs taken out of service, or put back into it: a change of the
        /// EVSEs, for which the station builds its OCPP nodes anew.
        /// </summary>
        public void NewOCPPNodes()
        {

            var changed = new JObject(
                              new JProperty("evses", new JArray(
                                  Station.EVSEs.Select(evse => {
                                      var json = evse.ToJSON();
                                      json["operative"] = !evse.Operative;
                                      return json;
                                  })
                              ))
                          );

            Assert.That(Station.TryUpdateEVSEConfiguration(changed, _ => true, out var change, out var error, out _), Is.True, error);
            Assert.That(change, Is.Not.EqualTo(EVSEChange.None), "changing the EVSEs' service is a change");

        }


        public ValueTask DisposeAsync()
            => Station.DisposeAsync();

    }


    /// <summary>
    /// What only a charging station does with the bridges.
    /// </summary>
    [TestFixture]
    public class StationBridgeTests
    {

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void AStation()
        {
            directory  = TestStations.TemporaryDirectory("station-bridge");
            station    = TestStations.New(directory, TestStations.Offline, WithDisplay: false);
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


        #region AnEventForTheCSMSIsKeptInTheFileBesideTheConfiguration()

        /// <summary>
        /// A security event the bridge queues for the CSMS is kept in the file
        /// beside the configuration, so that it outlives a restart.
        /// </summary>
        [Test]
        public void AnEventForTheCSMSIsKeptInTheFileBesideTheConfiguration()
        {

            Assert.That(station!.Connections.TryAddConnection("A CSMS of OCPP 2.1", "ws://127.0.0.1:9/cs", "CSMS", false, null, null,
                                                              out _, out var error, OCPPVersion: "2.1"),
                        Is.True,
                        error);

            Assert.That(station.TryUpdateNTSConfiguration(station.NTSGroups.Single(group => group.IsLegal), JObject.Parse("""{ "minServers": 1 }"""),
                                                          out error, out _, ChangeOrigin.Web("root")),
                        Is.True,
                        error);

            Assert.Multiple(() => {
                Assert.That(station.OCPPv2_1Node.OfflineQueue.Pending.Count(message => message.Kind == "SecurityEventNotification"),  Is.EqualTo(1));
                Assert.That(File.Exists(Path.Combine(directory, ChargingStation.OCPPQueueDirectoryName, "queue-2.1.jsonl")),          Is.True);
            });

        }

        #endregion

    }

}
