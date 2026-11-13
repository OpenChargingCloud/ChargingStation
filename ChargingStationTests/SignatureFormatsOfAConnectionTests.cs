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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using SignatureFormats = cloud.charging.open.protocols.WWCP.SignatureFormats;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A connection says which signatures the messages the station signs for it
    /// get - "jws", "cose" or "both" - and where it says nothing, or "policy",
    /// the signature policy of the station decides. The OCPP node of the
    /// station is told when the connection is made.
    /// </summary>
    [TestFixture]
    public class SignatureFormatsOfAConnectionTests
    {

        #region (class) CSMS

        /// <summary>
        /// A back end that answers a BootNotification.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>  received  = new ();

            public WebSocketServer  Server  { get; }
            public IPPort           Port    { get; }

            public CSMS()
            {

                this.Port  = IPPort.Parse(TestPorts.Free());

                Server     = new WebSocketServer(HTTPPort:               Port,
                                                 RequireAuthentication:  false,
                                                 SecWebSocketProtocols:  [ "ocpp2.1", "ocpp1.6" ],
                                                 AutoStart:              true);

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);
                    received.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer = new JArray(3, message[1], message[2].Value<String>() switch {
                                     "BootNotification"  => new JObject(
                                                                new JProperty("status",       "Accepted"),
                                                                new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                new JProperty("interval",     300)
                                                            ),
                                     _                   => new JObject()
                                 });

                    await Server.SendTextMessage(connection, answer.ToString(Formatting.None));

                };

            }

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
        public void MakeAStationAndACSMS()
        {
            directory  = TestStations.TemporaryDirectory("signature-formats");
            station    = TestStations.New(directory, TestStations.Offline);
            csms       = new CSMS();
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

        private ConnectionEntry Entry
            => station!.Connections.Connections.Single();

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


        #region WhatAConnectionSays_IsToldTheNodeOfTheStation(Named, Expected)

        [TestCase(null,     null)]
        [TestCase("policy", null)]
        [TestCase("jws",    SignatureFormats.JWS)]
        [TestCase("cose",   SignatureFormats.COSE)]
        [TestCase("both",   SignatureFormats.Both)]
        public async Task WhatAConnectionSays_IsToldTheNodeOfTheStation(String? Named, SignatureFormats? Expected)
        {

            station = await TestStations.Started(station!, directory);

            Assert.That(station.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, null, null,
                                                             out var id, out var error,
                                                             SignatureFormats: Named),
                        Is.True, error);

            await Until(() => IsConnected(id!), "the connection", () => SaidOf(id!));

            Assert.Multiple(() => {

                Assert.That(Entry.SignatureFormats,                Is.EqualTo(Expected));
                Assert.That(station.SignatureFormatsOf(Entry),     Is.EqualTo(Expected), "the OCPP node was not told what the connection says");

                // Said in the file where the connection says it.
                Assert.That(Entry.ToJSON()[ConnectionEntry.SignatureFormatsKey]?.Value<String>(),
                            Is.EqualTo(Expected.HasValue ? Named : null));

            });

        }

        #endregion

        #region AChange_IsToldTheNodeAsWell()

        [Test]
        public async Task AChange_IsToldTheNodeAsWell()
        {

            station = await TestStations.Started(station!, directory);

            Assert.That(station.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, null, null,
                                                             out var id, out var e1,
                                                             SignatureFormats: "cose"),
                        Is.True, e1);

            await Until(() => IsConnected(id!) && station.SignatureFormatsOf(Entry) == SignatureFormats.COSE, "the connection", () => SaidOf(id!));

            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS", Address, "CSMS", true, null, null, out var e2,
                                                                SignatureFormats: "both"),
                        Is.True, e2);

            await Until(() => IsConnected(id!) && station.SignatureFormatsOf(Entry) == SignatureFormats.Both,
                        "the change", () => $"{SaidOf(id!)}; the node says {station.SignatureFormatsOf(Entry)?.ToString() ?? "nothing"}");

            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS", Address, "CSMS", true, null, null, out var e3,
                                                                SignatureFormats: "policy"),
                        Is.True, e3);

            await Until(() => IsConnected(id!) && station.SignatureFormatsOf(Entry) is null,
                        "taking it back", () => $"{SaidOf(id!)}; the node says {station.SignatureFormatsOf(Entry)?.ToString() ?? "nothing"}");

        }

        #endregion

        #region WhatNamesNoSignatures_IsRefused_ForEveryVersion(Named, OCPPVersion, Taken)

        [TestCase("jws",    "2.1",  true)]
        [TestCase("COSE",   "2.1",  true)]
        [TestCase(" both",  "2.1",  true)]
        [TestCase("policy", "2.1",  true)]
        [TestCase("pgp",    "2.1",  false)]
        [TestCase("jws",    "1.6",  true)]
        [TestCase("policy", "1.6",  true)]
        [TestCase("cose",   "1.6",  true)]
        [TestCase("both",   "1.6",  true)]
        [TestCase("pgp",    "1.6",  false)]
        public void WhatNamesNoSignatures_IsRefused_ForEveryVersion(String Named, String OCPPVersion, Boolean Taken)
        {

            var added = station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", false, null, null,
                                                              out _, out var error,
                                                              OCPPVersion:       OCPPVersion,
                                                              SignatureFormats:  Named);

            Assert.Multiple(() => {

                Assert.That(added,  Is.EqualTo(Taken), error);

                if (!Taken)
                    Assert.That(error,  Does.Contain("names no signatures"));

            });

        }

        #endregion

        #region AChangeKeepsWhatItDoesNotSay_AndIsReadAgain()

        [Test]
        public void AChangeKeepsWhatItDoesNotSay_AndIsReadAgain()
        {

            Assert.That(station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", false, null, null,
                                                              out var id, out var e0,
                                                              SignatureFormats: "both"),
                        Is.True, e0);

            // A change that says nothing of it keeps it.
            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", false, null, null, out var e1),
                        Is.True, e1);

            Assert.That(Entry.SignatureFormats,  Is.EqualTo(SignatureFormats.Both));

            // What is written is read again.
            Assert.That(ConnectionEntry.TryParse(Entry.ToJSON(), out var readAgain, out var e2),  Is.True, e2);
            Assert.That(readAgain?.SignatureFormats,  Is.EqualTo(SignatureFormats.Both));

            // OCPP 1.6 has its COSE signature as well: a connection that becomes one keeps what it said.
            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", false, null, null, out var e3,
                                                                OCPPVersion: "1.6"),
                        Is.True, e3);

            Assert.That(Entry.SignatureFormats,  Is.EqualTo(SignatureFormats.Both));

            // "policy" takes back what a connection said.
            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", false, null, null, out var e4,
                                                                OCPPVersion: "2.1", SignatureFormats: "cose"),
                        Is.True, e4);

            Assert.That(Entry.SignatureFormats,  Is.EqualTo(SignatureFormats.COSE));

            Assert.That(station.Connections.TryUpdateConnection(id, "The CSMS, renamed", Address, "CSMS", false, null, null, out var e5,
                                                                SignatureFormats: "policy"),
                        Is.True, e5);

            Assert.Multiple(() => {
                Assert.That(Entry.SignatureFormats,                                   Is.Null);
                Assert.That(Entry.ToJSON()[ConnectionEntry.SignatureFormatsKey],      Is.Null);
            });

        }

        #endregion

    }

}
