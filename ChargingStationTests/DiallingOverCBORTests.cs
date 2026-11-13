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
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.OCPP.WebSockets;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using OCPPCBOR = cloud.charging.open.protocols.OCPP.OCPPCBORExtensions;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A connection says how its OCPP messages are written - "json", which it
    /// is where it says nothing, "cbor" or "auto": the station then offers
    /// "ocpp2.1cbor" alone, or first, and speaks CBOR in binary frames where
    /// the back end took it. The station says what it came to, and its
    /// connection test asks in the way the connection speaks.
    /// </summary>
    [TestFixture]
    public class DiallingOverCBORTests
    {

        #region (class) CSMS

        /// <summary>
        /// A back end that takes the subprotocols it is given, answers a
        /// BootNotification and a Heartbeat the way the connection speaks, and
        /// says what it was offered, what it took and what it received.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            public WebSocketServer                    Server    { get; }
            public IPPort                             Port      { get; }

            /// <summary>What each connection agreed on.</summary>
            public ConcurrentQueue<String?>           Agreed    { get; } = new ();

            /// <summary>What each connection offered.</summary>
            public ConcurrentQueue<String>            Offered   { get; } = new ();

            /// <summary>The messages that came as text.</summary>
            public ConcurrentQueue<JArray>            AsJSON    { get; } = new ();

            /// <summary>The messages that came as CBOR, as the JSON they stand for.</summary>
            public ConcurrentQueue<JArray>            AsCBOR    { get; } = new ();

            public CSMS(params String[] Speaks)
            {

                this.Port  = IPPort.Parse(TestPorts.Free());

                Server     = new WebSocketServer(HTTPPort:               Port,
                                                 RequireAuthentication:  false,
                                                 SecWebSocketProtocols:  Speaks,
                                                 AutoStart:              true);

                Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {
                    Agreed. Enqueue(selectedSubprotocol);
                    Offered.Enqueue(connection.HTTPRequest?.SecWebSocketProtocol is { } offered ? String.Join(", ", offered) : "");
                    return Task.CompletedTask;
                };

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);
                    AsJSON.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer = new JArray(3, message[1], message[2].Value<String>() switch {
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

                    await Server.SendTextMessage(connection, answer.ToString(Formatting.None));

                };

                Server.OnBinaryMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, bytes, cancellationToken) => {

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

                    await Server.SendBinaryMessage(connection,
                                                   OCPP_CBORFrame.Join(new JArray(3, message[1], new JObject()), payload).ToByteArray());

                };

            }

            private static CBORValue Map(params (String Key, CBORValue Value)[] Entries)
                => CBORValue.FromMap(Entries.Select(entry => new KeyValuePair<CBORValue, CBORValue>(CBORValue.FromText(entry.Key), entry.Value)));

            public Int32 BootsAsJSON
                => AsJSON.Count(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "BootNotification");

            public Int32 BootsAsCBOR
                => AsCBOR.Count(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "BootNotification");

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("dialling-cbor");
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

        private String Address
            => $"ws://127.0.0.1:{csms!.Port}/cs001";

        private async Task Start()
            => station = await TestStations.Started(station!, directory);

        private String Added(String? Encoding, String? OCPPVersion = null)
        {

            Assert.That(station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              OCPPVersion:  OCPPVersion,
                                                              Encoding:     Encoding),
                        Is.True, error);

            return id!;

        }

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

        private ConnectionEntry Entry
            => station!.Connections.Connections.Single();

        private String? InUse
            => station!.SubprotocolInUseOf(Entry);

        private static String Steps(JObject Result)
            => String.Join("\n", Result["steps"]!.Select(step => step.Value<String>("text")));

        #endregion


        #region AConnectionSpeaksWhatItNames_OrWhatItsBackEndChooses(Named, Speaks, Offered, Agreed)

        /// <summary>
        /// Saying nothing is "json". "cbor" offers the CBOR subprotocol and
        /// nothing else; "auto" offers it first, and falls back to JSON by
        /// itself where the back end does not speak it.
        /// </summary>
        [TestCase(null,   "ocpp2.1cbor,ocpp2.1",  "ocpp2.1, ocpp2.0.1",               "ocpp2.1")]
        [TestCase("json", "ocpp2.1cbor,ocpp2.1",  "ocpp2.1, ocpp2.0.1",               "ocpp2.1")]
        [TestCase("cbor", "ocpp2.1cbor,ocpp2.1",  "ocpp2.1cbor",                      "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1cbor,ocpp2.1",  "ocpp2.1cbor, ocpp2.1, ocpp2.0.1",  "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1",              "ocpp2.1cbor, ocpp2.1, ocpp2.0.1",  "ocpp2.1")]
        public async Task AConnectionSpeaksWhatItNames_OrWhatItsBackEndChooses(String? Named, String Speaks, String Offered, String Agreed)
        {

            csms = new CSMS(Speaks.Split(','));

            await Start();

            var id = Added(Named);

            await Until(() => IsConnected(id), "the connection", () => SaidOf(id));

            var cbor = Agreed.EndsWith("cbor");

            await Until(() => (cbor ? csms.BootsAsCBOR : csms.BootsAsJSON) > 0, "the BootNotification");

            Assert.Multiple(() => {

                Assert.That(csms.Offered.First().Replace(" ", ""),          Is.EqualTo(Offered.Replace(" ", "")));
                Assert.That(csms.Agreed. First(),                           Is.EqualTo(Agreed));
                Assert.That(InUse,                                          Is.EqualTo(Agreed));

                // One way and nothing of the other.
                Assert.That(cbor ? csms.AsJSON : csms.AsCBOR,               Is.Empty);

                Assert.That(ConnectionEntry.AsText(Entry.Encoding),         Is.EqualTo(Named ?? "json"));

                // Said in the file where it is not what OCPP defines.
                Assert.That(Entry.ToJSON()[ConnectionEntry.EncodingKey]?.Value<String>(),
                            Is.EqualTo(Named is null or "json" ? null : Named));

            });

            // What a BootNotification says came through whole.
            var boot = (cbor ? csms.AsCBOR : csms.AsJSON).First(message => message[2].Value<String>() == "BootNotification");

            Assert.That(boot[3]?["chargingStation"]?["vendorName"]?.Value<String>(),  Is.Not.Null.And.Not.Empty);

        }

        #endregion

        #region ABackEndWithoutCBOR_IsNotConnectedToByAConnectionThatSaysCBOR()

        [Test]
        public async Task ABackEndWithoutCBOR_IsNotConnectedToByAConnectionThatSaysCBOR()
        {

            csms = new CSMS("ocpp2.1", "ocpp2.0.1");

            await Start();

            var id = Added("cbor");

            await Until(() => station!.ConnectionStates.TryGetValue(id, out var state) && state.Status == ConnectionStatus.Refused,
                        "the refusal", () => SaidOf(id));

            await Task.Delay(500);

            Assert.Multiple(() => {
                Assert.That(csms.Offered.First(), Is.EqualTo("ocpp2.1cbor"));
                Assert.That(SaidOf(id),           Does.Contain("without agreeing on ocpp2.1cbor"));
                Assert.That(IsConnected(id),      Is.False, "a back end that speaks no CBOR was connected to");
                Assert.That(InUse,                Is.Null);
                Assert.That(csms.BootsAsJSON,     Is.EqualTo(0), "a connection that says CBOR spoke JSON");
                Assert.That(csms.BootsAsCBOR,     Is.EqualTo(0));
            });

        }

        #endregion

        #region WhatIsNoEncoding_IsRefused_ForEveryVersion(Named, OCPPVersion, Taken)

        [TestCase("json",  "2.1",  true)]
        [TestCase("CBOR",  "2.1",  true)]
        [TestCase(" auto", "2.1",  true)]
        [TestCase("xml",   "2.1",  false)]
        [TestCase("json",  "1.6",  true)]
        [TestCase("cbor",  "1.6",  true)]
        [TestCase("auto",  "1.6",  true)]
        [TestCase("xml",   "1.6",  false)]
        public void WhatIsNoEncoding_IsRefused_ForEveryVersion(String Named, String OCPPVersion, Boolean Taken)
        {

            csms = new CSMS("ocpp2.1");

            var added = station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", false, null, null,
                                                              out _, out var error,
                                                              OCPPVersion:  OCPPVersion,
                                                              Encoding:     Named);

            Assert.Multiple(() => {

                Assert.That(added,  Is.EqualTo(Taken), error);

                if (!Taken)
                    Assert.That(error,  Does.Contain("is no way the messages"));

            });

        }

        #endregion

        #region AChangeKeepsWhatItDoesNotSay_AndIsReadAgain()

        [Test]
        public void AChangeKeepsWhatItDoesNotSay_AndIsReadAgain()
        {

            csms = new CSMS("ocpp2.1cbor");

            var id = Added("auto");

            // A change that says nothing of it keeps it.
            Assert.That(station!.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", true, null, null, out var e1),
                        Is.True, e1);

            Assert.That(Entry.Encoding,  Is.EqualTo(MessageEncodings.Auto));

            // One that says so changes it.
            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", true, null, null, out var e2,
                                                                Encoding: "cbor"),
                        Is.True, e2);

            Assert.That(Entry.Encoding,  Is.EqualTo(MessageEncodings.CBOR));

            // What is written is read again.
            Assert.That(ConnectionEntry.TryParse(Entry.ToJSON(), out var readAgain, out var e3),  Is.True, e3);
            Assert.That(readAgain?.Encoding,  Is.EqualTo(MessageEncodings.CBOR));

            // OCPP 1.6 has its CBOR as well: a connection that becomes one keeps what it said.
            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", true, null, null, out var e4,
                                                                OCPPVersion: "1.6"),
                        Is.True, e4);

            Assert.That(Entry.Encoding,  Is.EqualTo(MessageEncodings.CBOR));

        }

        #endregion

        #region AConnectionTest_SaysThatABackEndWithoutCBORWouldNotBeKept()

        [Test]
        public async Task AConnectionTest_SaysThatABackEndWithoutCBORWouldNotBeKept()
        {

            csms = new CSMS("ocpp2.1");

            await Start();

            var result = await station!.TestConnection("The CSMS", Address, "CSMS", Encoding: "cbor");
            var steps  = Steps(result);

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),  Is.False, steps);
                Assert.That(steps,                        Does.Contain("It did not agree on ocpp2.1cbor"));
                Assert.That(csms.AsJSON,                  Is.Empty, "a test of a connection that says CBOR asked in JSON");
                Assert.That(csms.AsCBOR,                  Is.Empty);
            });

        }

        #endregion

        #region AConnectionTest_AsksTheWayTheConnectionSpeaks(Named, Agreed)

        [TestCase(null,   "ocpp2.1")]
        [TestCase("cbor", "ocpp2.1cbor")]
        [TestCase("auto", "ocpp2.1cbor")]
        public async Task AConnectionTest_AsksTheWayTheConnectionSpeaks(String? Named, String Agreed)
        {

            csms = new CSMS("ocpp2.1cbor", "ocpp2.1");

            await Start();

            var result = await station!.TestConnection("The CSMS", Address, "CSMS", Encoding: Named);
            var steps  = Steps(result);
            var cbor   = Agreed.EndsWith("cbor");

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"),                    Is.True, steps);
                Assert.That(csms.Agreed.Single(),                           Is.EqualTo(Agreed));
                Assert.That(steps,                                          Does.Contain($"The handshake was accepted for {Agreed}."));

                Assert.That((cbor ? csms.AsCBOR : csms.AsJSON).Count(message => message[2].Value<String>() == "Heartbeat"),  Is.EqualTo(1), steps);
                Assert.That(cbor ? csms.AsJSON : csms.AsCBOR,               Is.Empty);

                Assert.That(steps,                                          Does.Contain("Answered after"));
                Assert.That(steps,                                          Does.Not.Contain("byte(s) of binary"));

                if (cbor)
                    Assert.That(steps,                                      Does.Contain("Its messages are CBOR in binary frames."));

            });

        }

        #endregion

    }

}
