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

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using cloud.charging.open.protocols.OCPPv1_6.CS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The station program against a central system node of WWCP_OCPP in OCPP
    /// 1.6, over "ocpp1.6cbor" and over "ocpp1.6": the station registers, the
    /// central system reads what it says with the classes of the messages,
    /// asks the station, and is answered.
    /// </summary>
    [TestFixture]
    public class AgainstACentralSystemNodeOverCBORTests
    {

        #region Data

        private String                            directory  = "";
        private ChargingStation?                  station;
        private OCPPv1_6.TestCentralSystemNode?   centralSystem;
        private OCPPWebSocketServer?              server;

        private readonly ConcurrentList<OCPPv1_6.CP.BootNotificationRequest>  boots   = [];
        private readonly ConcurrentList<String>                               binary  = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStationAndACentralSystem()
        {

            directory      = TestStations.TemporaryDirectory("against-a-central-system");
            station        = TestStations.New(directory, TestStations.Offline);

            boots. Clear();
            binary.Clear();

            centralSystem  = new OCPPv1_6.TestCentralSystemNode(
                                 Id:                      NetworkingNode_Id.Parse("cs01"),
                                 VendorName:              "GraphDefined",
                                 Model:                   "A central system node",
                                 HTTPAPI_Disabled:        true,
                                 HTTPUploadAPI_Disabled:  true
                             );

            server         = centralSystem.AttachWebSocketServer(
                                 RequireAuthentication:  false,
                                 DisableWebSocketPings:  true,
                                 AutoStart:              true
                             );

            centralSystem.OCPP.IN.OnBootNotificationRequestReceived += (timestamp, sender, connection, request, cancellationToken) => {
                boots.TryAdd(request);
                return Task.CompletedTask;
            };

            // A message of the binary format is none of these: CBOR is told of as JSON.
            centralSystem.OCPP.IN.OnBinaryRequestMessageReceived += (timestamp, sender, connection, message, cancellationToken) => {
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

            if (centralSystem is not null)
                await centralSystem.Stop();

            if (server is not null)
                await server.DisposeAsync();

            station        = null;
            centralSystem  = null;
            server         = null;

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

        [TestCase(null,   "ocpp1.6")]
        [TestCase("cbor", "ocpp1.6cbor")]
        [TestCase("auto", "ocpp1.6cbor")]
        public async Task TheStationRegisters_IsAsked_AndAnswers(String? Named, String Agreed)
        {

            station = await TestStations.Started(station!, directory);

            Assert.That(station.Connections.TryAddConnection("The central system", Address, "CSMS", true, null, null,
                                                             out var id, out var error,
                                                             OCPPVersion:  "1.6",
                                                             Encoding:     Named),
                        Is.True, error);

            await Until(() => IsConnected(id!), "the connection", () => SaidOf(id!));
            await Until(() => boots.Count > 0,  "the BootNotification at the central system");

            var boot = boots.First();

            Assert.Multiple(() => {

                Assert.That(station.SubprotocolInUseOf(station.Connections.Connections.Single()),  Is.EqualTo(Agreed));

                // Read by the class of the message, whatever the wire carried.
                Assert.That(boot.ChargePointVendor,                 Is.Not.Empty);
                Assert.That(boot.ChargePointModel,                  Is.Not.Empty);
                Assert.That(boot.NetworkPath.Source.ToString(),     Is.EqualTo("cs001"));

            });

            // The central system asks the station, and is answered.
            var response = await centralSystem!.GetConfiguration(
                                     Destination:  SourceRouting.To(boot.NetworkPath.Source)
                                 );

            Assert.Multiple(() => {
                Assert.That(response.Result.ResultCode,     Is.EqualTo(cloud.charging.open.protocols.OCPP.ResultCode.OK), response.Result.Description);
                Assert.That(response.ConfigurationKeys,     Is.Not.Empty);
                Assert.That(binary,                         Is.Empty, "A message was told of as one of the binary format.");
            });

        }

        #endregion

    }

}
