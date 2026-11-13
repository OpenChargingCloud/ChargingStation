/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of ChargingStation <https://github.com/OpenChargingCloud/ChargingStation>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
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
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPP.WebSockets;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.ChargingStation.OCPP;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The station program against a CSMS node of WWCP_OCPP - no stand-in, the
    /// node a CSMS is built from - over "ocpp2.1cbor" and over "ocpp2.1": the
    /// station registers, the CSMS reads what it says with the classes of the
    /// messages, asks the station, and is answered.
    /// </summary>
    [TestFixture]
    public class AgainstACSMSNodeOverCBORTests
    {

        #region Data

        private String                               directory  = "";
        private ChargingStation?                     station;
        private OCPPv2_1.CSMS.TestCSMSNode?               csms;
        private OCPPWebSocketServer?                 server;

        private readonly ConcurrentList<OCPPv2_1.CS.BootNotificationRequest>  boots       = [];
        private readonly ConcurrentList<OCPPv2_1.CS.HeartbeatRequest>         heartbeats  = [];
        private readonly ConcurrentList<String>                               binary      = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStationAndACSMS()
        {

            directory  = TestStations.TemporaryDirectory("against-a-csms");
            station    = TestStations.New(directory, TestStations.Offline);

            boots.     Clear();
            heartbeats.Clear();
            binary.    Clear();

            csms       = new OCPPv2_1.CSMS.TestCSMSNode(
                             Id:                NetworkingNode_Id.Parse("csms01"),
                             VendorName:        "GraphDefined",
                             Model:             "A CSMS node",
                             HTTPAPI_Disabled:  true
                         );

            server     = csms.AttachWebSocketServer(
                             RequireAuthentication:  false,
                             DisableWebSocketPings:  true,
                             AutoStart:              true
                         );

            csms.OCPP.IN.OnBootNotificationRequestReceived += (timestamp, sender, connection, request, cancellationToken) => {
                boots.TryAdd(request);
                return Task.CompletedTask;
            };

            csms.OCPP.IN.OnHeartbeatRequestReceived += (timestamp, sender, connection, request, cancellationToken) => {
                heartbeats.TryAdd(request);
                return Task.CompletedTask;
            };

            // A message of the binary format is none of these: CBOR is told of as JSON.
            csms.OCPP.IN.OnBinaryRequestMessageReceived += (timestamp, sender, connection, message, cancellationToken) => {
                binary.TryAdd(message.Action.ToString());
                return Task.CompletedTask;
            };

        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (server is not null)
                await server.Shutdown();

            if (csms is not null)
                await csms.Stop();

            if (server is not null)
                await server.DisposeAsync();

            station  = null;
            csms     = null;
            server   = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private String Address
            => $"ws://127.0.0.1:{server!.IPPort}/cs001";

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Otherwise = null)
        {

            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within 20 s.{(Otherwise is not null ? " " + Otherwise() : "")}");

                await Task.Delay(20);

            }

        }

        private String SaidOf(String Id)
            => station!.DialledConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

        private Boolean IsConnected(String Id)
            => station!.ConnectionStates.TryGetValue(Id, out var state) && state.Status == ConnectionStatus.Connected;

        #endregion


        #region TheStationRegisters_IsAsked_AndAnswers(Named, Agreed)

        [TestCase(null,   "ocpp2.1")]
        [TestCase("cbor", "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1cbor")]
        public async Task TheStationRegisters_IsAsked_AndAnswers(String? Named, String Agreed)
        {

            station = await TestStations.Started(station!, directory);

            Assert.That(station.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, null, null,
                                                             out var id, out var error,
                                                             Encoding: Named),
                        Is.True, error);

            await Until(() => IsConnected(id!), "the connection", () => SaidOf(id!));
            await Until(() => boots.Count > 0,  "the BootNotification at the CSMS");

            var boot = boots.First();

            Assert.Multiple(() => {

                Assert.That(station.SubprotocolInUseAt(URL.Parse(Address)),   Is.EqualTo(Agreed));

                // Read by the class of the message, whatever the wire carried.
                Assert.That(boot.ChargingStation.VendorName,                  Is.Not.Empty);
                Assert.That(boot.ChargingStation.Model,                       Is.Not.Empty);
                Assert.That(boot.NetworkPath.Source.ToString(),               Is.EqualTo("cs001"));

            });

            // The CSMS asks the station, and the station does what it was asked.
            var before   = heartbeats.Count;

            var response = await csms!.TriggerMessage(
                                     Destination:        SourceRouting.To(boot.NetworkPath.Source),
                                     RequestedMessage:   OCPPv2_1.MessageTrigger.Heartbeat
                                 );

            Assert.Multiple(() => {
                Assert.That(response.Result.ResultCode,   Is.EqualTo(cloud.charging.open.protocols.OCPP.ResultCode.OK), response.Result.ToString());
                Assert.That(response.Status,              Is.EqualTo(OCPPv2_1.TriggerMessageStatus.Accepted));
            });

            await Until(() => heartbeats.Count > before, "the Heartbeat the CSMS asked for");

            Assert.That(binary, Is.Empty, "A message was told of as one of the binary format.");

        }

        #endregion

    }

}
