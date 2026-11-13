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

using System.Collections.Concurrent;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Server;

using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.protocols.OCPP.WebSockets;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPCBOR = cloud.charging.open.protocols.OCPP.OCPPCBORExtensions;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// An ssh:// connection agrees on the subprotocols a WebSocket agrees on:
    /// one that says "cbor" or "auto" speaks OCPP 2.1 as CBOR in the binary
    /// messages of the SSH subsystem where its server takes "ocpp2.1cbor".
    /// </summary>
    [TestFixture]
    public class DiallingOverSSHWithCBORTests
    {

        #region (class) SshCSMS

        /// <summary>
        /// A back end over SSH that takes the subprotocols it is given, and
        /// answers a BootNotification and a Heartbeat the way the connection speaks.
        /// </summary>
        private sealed class SshCSMS : IAsyncDisposable
        {

            public SshServer                Server    { get; }
            public SshMessageServer         Messages  { get; }

            /// <summary>The messages that came as text.</summary>
            public ConcurrentQueue<JArray>  AsJSON    { get; } = new ();

            /// <summary>The messages that came as CBOR, as the JSON they stand for.</summary>
            public ConcurrentQueue<JArray>  AsCBOR    { get; } = new ();

            public UInt16 Port
                => Server.LocalEndPoint.Port.ToUInt16();

            public SshCSMS(params String[] Speaks)
            {

                Messages  = new SshMessageServer(new SshMessageServerOptions {
                                Subsystem     = "ocpp@open.charging.cloud",
                                Subprotocols  = Speaks
                            });

                Messages.OnTextMessageReceived += async (timestamp, sender, connection, text, cancellationToken) => {

                    var message = JArray.Parse(text);
                    AsJSON.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer  = new JArray(3, message[1], message[2].Value<String>() switch {
                                      "BootNotification"  => new JObject(
                                                                 new JProperty("status",       "Accepted"),
                                                                 new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                 new JProperty("interval",     300)
                                                             ),
                                      "Heartbeat"         => new JObject(
                                                                 new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                                             ),
                                      _                   => new JObject()
                                  });

                    await connection.SendTextMessage(answer.ToString(Formatting.None), cancellationToken);

                };

                Messages.OnBinaryMessageReceived += async (timestamp, sender, connection, bytes, cancellationToken) => {

                    if (!OCPP_CBORFrame.TryAsJSON(bytes, out var message, out _))
                        return;

                    AsCBOR.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var now     = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, TimeSpan.Zero);

                    var payload = message[2].Value<String>() switch {
                                      "BootNotification"  => Map(("status",       CBORValue.FromText("Accepted")),
                                                                 ("currentTime",  OCPPCBOR.ToCBOR(now)),
                                                                 ("interval",     OCPPCBOR.ToCBOR(TimeSpan.FromSeconds(300)))),
                                      "Heartbeat"         => Map(("currentTime",  OCPPCBOR.ToCBOR(now))),
                                      _                   => Map()
                                  };

                    await connection.SendBinaryMessage(OCPP_CBORFrame.Join(new JArray(3, message[1], new JObject()), payload).ToByteArray(),
                                                       cancellationToken);

                };

                Server    = new SshServer(new SshServerOptions {
                                HostKeys       = [ SshHostKey.GenerateEd25519() ],
                                Authenticator  = new SshUserAuthenticator((request, cancellationToken) => ValueTask.FromResult(true)),
                                Subsystems     = (session, name, cancellationToken) =>
                                                     ValueTask.FromResult(name == Messages.Subsystem ? Messages.Handler : null)
                            });

            }

            private static CBORValue Map(params (String Key, CBORValue Value)[] Entries)
                => CBORValue.FromMap(Entries.Select(entry => new KeyValuePair<CBORValue, CBORValue>(CBORValue.FromText(entry.Key), entry.Value)));

            public Task Start()
                => Server.StartAsync(new IPSocket(IPv4Address.Localhost, IPPort.Auto), CancellationToken.None).AsTask();

            public Int32 Boots(ConcurrentQueue<JArray> Messages)
                => Messages.Count(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "BootNotification");

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private SshCSMS?          csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("dialling-ssh-cbor");
            station    = TestStations.New(directory, TestStations.Offline);
        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            station  = null;
            csms     = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private String SshURL
            => $"ssh://cs001@127.0.0.1:{csms!.Port}";

        private async Task ACSMS(params String[] Speaks)
        {
            csms = new SshCSMS(Speaks);
            await csms.Start();
        }

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Otherwise = null)
        {

            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);

            while (!Condition())
            {

                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within 15 s.{(Otherwise is not null ? " " + Otherwise() : "")}");

                await Task.Delay(20);

            }

        }

        private String SaidOf(String Id)
            => station!.DialledConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

        private ConnectionStatus? StatusOf(String Id)
            => station!.ConnectionStates.TryGetValue(Id, out var state) ? state.Status : null;

        private String Added(String? Encoding, String? SignatureFormats = null)
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL, "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              SshTrust:          new SshTrustSettings("HostKey", null, null),
                                                              Encoding:          Encoding,
                                                              SignatureFormats:  SignatureFormats),
                        Is.True, error);

            return id!;

        }

        #endregion


        #region AStationSpeaksOverSSHWhatItsConnectionNames(Named, Speaks, Agreed)

        [TestCase(null,   "ocpp2.1cbor,ocpp2.1",  "ocpp2.1")]
        [TestCase("cbor", "ocpp2.1cbor,ocpp2.1",  "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1cbor,ocpp2.1",  "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1",              "ocpp2.1")]
        public async Task AStationSpeaksOverSSHWhatItsConnectionNames(String? Named, String Speaks, String Agreed)
        {

            await ACSMS(Speaks.Split(','));

            var id   = Added(Named);
            var cbor = Agreed.EndsWith("cbor");

            station = await TestStations.Started(station!, directory);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, "Connecting over SSH", () => SaidOf(id));
            await Until(() => csms!.Boots(cbor ? csms.AsCBOR : csms.AsJSON) > 0, "A BootNotification over SSH");

            Assert.Multiple(() => {

                Assert.That(csms!.Messages.Connections.Single().Subprotocol,  Is.EqualTo(Agreed));

                // Said by the station as well, as of a WebSocket.
                Assert.That(station.SubprotocolInUseOf(station.Connections.Connections.Single()),  Is.EqualTo(Agreed));

                // One way and nothing of the other.
                Assert.That(cbor ? csms.AsJSON : csms.AsCBOR,                 Is.Empty);

                var boot = (cbor ? csms.AsCBOR : csms.AsJSON).First(message => message[2].Value<String>() == "BootNotification");

                Assert.That(boot[3]?["chargingStation"]?["vendorName"]?.Value<String>(),  Is.Not.Null.And.Not.Empty);

            });

        }

        #endregion

        #region WhichSignaturesAnSSHConnectionWrites_IsToldTheNodeOfTheStation(Named, Encoding, Expected)

        /// <summary>
        /// What an ssh:// connection says of its signatures is told the OCPP
        /// node of the station as what a WebSocket says is - whatever its
        /// messages are written as - and taken back by "policy".
        /// </summary>
        [TestCase(null,   null,   null)]
        [TestCase("cose", null,   cloud.charging.open.protocols.WWCP.SignatureFormats.COSE)]
        [TestCase("both", "cbor", cloud.charging.open.protocols.WWCP.SignatureFormats.Both)]
        [TestCase("jws",  "auto", cloud.charging.open.protocols.WWCP.SignatureFormats.JWS)]
        public async Task WhichSignaturesAnSSHConnectionWrites_IsToldTheNodeOfTheStation(String?                                               Named,
                                                                                         String?                                               Encoding,
                                                                                         cloud.charging.open.protocols.WWCP.SignatureFormats?  Expected)
        {

            await ACSMS("ocpp2.1cbor", "ocpp2.1");

            var id = Added(Encoding, Named);

            station = await TestStations.Started(station!, directory);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected, "Connecting over SSH", () => SaidOf(id));

            var entry = station.Connections.Connections.Single();

            Assert.Multiple(() => {
                Assert.That(entry.SignatureFormats,             Is.EqualTo(Expected));
                Assert.That(station.SignatureFormatsOf(entry),  Is.EqualTo(Expected), "the OCPP node was not told what the connection says");
            });

            // "policy" takes it back, on the connection that is dialled again for the change.
            Assert.That(station.Connections.TryUpdateConnection(id, "CSMS over SSH", SshURL, "CSMS", true, null, null, out var error,
                                                                SignatureFormats: "policy"),
                        Is.True, error);

            await Until(() => StatusOf(id) == ConnectionStatus.Connected &&
                              station.SignatureFormatsOf(station.Connections.Connections.Single()) is null,
                        "taking it back", () => SaidOf(id));

        }

        #endregion

        #region AServerWithoutCBOR_IsNotSpokenToByAConnectionThatSaysCBOR()

        [Test]
        public async Task AServerWithoutCBOR_IsNotSpokenToByAConnectionThatSaysCBOR()
        {

            await ACSMS("ocpp2.1", "ocpp2.0.1");

            var id = Added("cbor");

            station = await TestStations.Started(station!, directory);

            await Until(() => StatusOf(id) is ConnectionStatus.Refused or ConnectionStatus.Failed or ConnectionStatus.Trying,
                        "the refusal", () => SaidOf(id));

            await Task.Delay(500);

            Assert.Multiple(() => {
                Assert.That(station.SubprotocolInUseOf(station.Connections.Connections.Single()),  Is.Null);
                Assert.That(StatusOf(id),   Is.Not.EqualTo(ConnectionStatus.Connected), SaidOf(id));
                Assert.That(csms!.AsJSON,   Is.Empty, "a connection that says CBOR spoke JSON");
                Assert.That(csms.AsCBOR,    Is.Empty);
            });

        }

        #endregion

        #region ATestOverSSH_AsksTheWayTheConnectionSpeaks(Named, Agreed)

        [TestCase(null,   "ocpp2.1")]
        [TestCase("cbor", "ocpp2.1cbor")]
        public async Task ATestOverSSH_AsksTheWayTheConnectionSpeaks(String? Named, String Agreed)
        {

            await ACSMS("ocpp2.1cbor", "ocpp2.1");

            var result = await station!.TestConnection("CSMS over SSH", SshURL, "CSMS", Encoding: Named);
            var steps  = String.Join("\n", result["steps"]!.Select(step => step.Value<String>("text")));
            var cbor   = Agreed.EndsWith("cbor");

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"),                   Is.True, steps);
                Assert.That(steps,                                          Does.Contain($"took {Agreed}."));
                Assert.That(result["ocpp"]?.Value<Boolean>("answered"),     Is.True, steps);

                Assert.That((cbor ? csms!.AsCBOR : csms!.AsJSON).Count(message => message[2].Value<String>() == "Heartbeat"),  Is.EqualTo(1), steps);
                Assert.That(cbor ? csms.AsJSON : csms.AsCBOR,               Is.Empty);
                Assert.That(steps,                                          Does.Not.Contain("byte(s) of binary"));

            });

        }

        #endregion

    }

}
