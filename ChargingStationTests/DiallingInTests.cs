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

using System.Collections.Concurrent;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The OCPP server of a station: a CSMS that dials the station, rather
    /// than being dialled by it. OCPP as it always is - the station says that
    /// it started, the CSMS asks and is answered - and nothing gets in that is
    /// not written down as a connection the other end makes.
    /// </summary>
    [TestFixture]
    public class DiallingInTests
    {

        #region Data

        private const String      Name      = "csms-01";

        private String            directory = "";
        private ChargingStation?  station;
        private IPPort            port;
        private IPPort            port16;
        private readonly List<String>  said = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {

            lock (said)
                said.Clear();

            directory  = TestStations.TemporaryDirectory("dialling-in");
            port       = IPPort.Parse(TestPorts.Free());
            port16     = IPPort.Parse(TestPorts.Free());

            var configuration = TestStations.Offline;

            configuration.Add("ocppServer", new JObject(
                                                new JProperty("enabled", true),
                                                new JProperty("port",    port.ToUInt16()),
                                                new JProperty("port16",  port16.ToUInt16())
                                            ));

            station    = TestStations.New(directory, configuration);

            station.Log.OnLogged += entry => { lock (said) said.Add(entry.Message); };

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

        #region (private) Helpers

        private String Address
            => $"ws://127.0.0.1:{port}/{Name}";

        private static String APassword()
            => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

        /// <summary>
        /// The connection the CSMS makes, written down with the password it
        /// signs in with.
        /// </summary>
        private String WrittenDown(String Password, Boolean LetIn = true)
        {

            Assert.That(station!.Connections.TryAddAuthentication("What the CSMS signs in with", "basic", Name, Password,
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("The CSMS, dialling in", Address, "CSMS", LetIn, login, null,
                                                             out var id, out error,
                                                             Inbound: true),
                        Is.True, error);

            return id!;

        }

        private String Said()
        {
            lock (said) return String.Join("\n", said.TakeLast(40));
        }

        private String StatusOf(String Id)
            => station!.ConnectionStates.TryGetValue(Id, out var state)
                   ? OCPP.ConnectionState.AsText(state.Status)
                   : "nothing yet";

        private String SaidOf(String Id)
            => station!.DialledConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Instead = null)
        {

            var deadline = DateTime.UtcNow.AddSeconds(20);

            while (!Condition())
            {

                if (DateTime.UtcNow > deadline)
                    Assert.Fail($"{What} did not happen within 20 seconds{(Instead is not null ? $": {Instead()}" : "")}.");

                await Task.Delay(50);

            }

        }

        /// <summary>
        /// A CSMS that dials: a WebSocket client that keeps what it is sent
        /// and accepts a BootNotification.
        /// </summary>
        private sealed class DiallingCSMS : IAsyncDisposable
        {

            public WebSocketClient          Client    { get; }
            public ConcurrentQueue<JArray>  Received  { get; } = new ();

            public DiallingCSMS(String URL, String Login, String Password, String Subprotocol = "ocpp2.1")
            {

                Client = new WebSocketClient(
                             org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(URL),
                             HTTPAuthentication:     HTTPBasicAuthentication.Create(Login, Password),
                             SecWebSocketProtocols:  [ Subprotocol ]
                         );

                Client.OnTextMessageReceived += async (timestamp, client, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);
                    Received.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer = message[2].Value<String>() == "BootNotification"

                                     ? new JArray(3, message[1], new JObject(
                                                                     new JProperty("status",       "Accepted"),
                                                                     new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                                                                     new JProperty("interval",     300)
                                                                 ))

                                     : new JArray(3, message[1], message[2].Value<String>() == "Heartbeat"
                                                                     ? new JObject(new JProperty("currentTime", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")))
                                                                     : new JObject());

                    await Client.SendTextMessage(answer.ToString(Formatting.None));

                };

            }

            public IEnumerable<JArray> Asked(String Action)
                => Received.Where(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == Action);

            public JArray? AnswerTo(String RequestId)
                => Received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == RequestId);

            public ValueTask DisposeAsync()
                => Client.DisposeAsync();

        }

        #endregion


        #region ACSMSThatDialsIn_IsToldThatTheStationStarted_AndIsAnswered()

        [Test]
        public async Task ACSMSThatDialsIn_IsToldThatTheStationStarted_AndIsAnswered()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Started(station!, directory);

            await Until(() => StatusOf(id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain($"Waiting for '{Name}' to dial in"));

            await using var csms = new DiallingCSMS(Address, Name, password);

            var (_, response) = await csms.Client.Connect();

            Assert.That(response.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in.");

            await Until(() => StatusOf(id) == "connected",           "Being connected",          () => $"{StatusOf(id)}: {SaidOf(id)}");
            await Until(() => csms.Asked("BootNotification").Any(),  "The BootNotification",     () => String.Join(" | ", csms.Received.Select(message => message.ToString(Formatting.None))) + "\n" + Said());
            await Until(() => csms.Asked("StatusNotification").Any(), "The StatusNotifications", () => String.Join(" | ", csms.Received.Select(message => message.ToString(Formatting.None))));

            // What the CSMS asks is for this station, and is answered by it.
            await csms.Client.SendTextMessage(new JArray(2, "from-the-csms-1", "GetLocalListVersion", new JObject()).ToString(Formatting.None));

            await Until(() => csms.AnswerTo("from-the-csms-1") is not null, "The answer to the CSMS's request",
                        () => String.Join(" | ", csms.Received.Select(message => message.ToString(Formatting.None))));

            Assert.Multiple(() => {

                Assert.That(Said(),                                            Does.Contain($"'{Name}' dialled in"));
                Assert.That(SaidOf(id),                                        Does.Contain("the CSMS accepted this station"));
                Assert.That(csms.Asked("BootNotification").Count(),            Is.EqualTo(1));
                Assert.That(csms.AnswerTo("from-the-csms-1")![0].Value<Int32>(), Is.EqualTo(3), csms.AnswerTo("from-the-csms-1")!.ToString(Formatting.None));

                Assert.That(station!.OCPPServerJSON()["servers"]![0]!["connected"]!.Values<String>(), Is.EqualTo(new[] { Name }));

            });

            // Gone, it is waited for again - and let in again.
            await csms.Client.Close();

            await Until(() => StatusOf(id) == "waiting", "Waiting again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain("went away"));

            await using var again = new DiallingCSMS(Address, Name, password);

            Assert.That((await again.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected again", () => $"{StatusOf(id)}: {SaidOf(id)}");

        }

        #endregion

        #region NothingGetsIn_ThatIsNotWrittenDown()

        [Test]
        public async Task NothingGetsIn_ThatIsNotWrittenDown()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Started(station!, directory);

            await using var wrongPassword = new DiallingCSMS(Address, Name, APassword());
            await using var unknown       = new DiallingCSMS($"ws://127.0.0.1:{port}/csms-02", "csms-02", password);
            await using var asAnother     = new DiallingCSMS($"ws://127.0.0.1:{port}/csms-02", Name,      password);

            var refused1  = (await wrongPassword.Client.Connect()).Item2.HTTPStatusCode;
            var refused2  = (await unknown.      Client.Connect()).Item2.HTTPStatusCode;
            var refused3  = (await asAnother.    Client.Connect()).Item2.HTTPStatusCode;

            Assert.Multiple(() => {
                Assert.That(refused1,      Is.EqualTo(HTTPStatusCode.Unauthorized), "a wrong password");
                Assert.That(refused2,      Is.EqualTo(HTTPStatusCode.Unauthorized), "a name nobody wrote down");
                Assert.That(refused3,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "under another name than its own");
                Assert.That(StatusOf(id),  Is.EqualTo("waiting"));
            });

            // Switched off, the one that is written down is not let in either -
            // and one that is in is let go of.
            await using var csms = new DiallingCSMS(Address, Name, password);

            Assert.That((await csms.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(station!.Connections.TryUpdateConnection(id, "The CSMS, dialling in", Address, "CSMS", false,
                                                                 station.Connections.Connections.Single(one => one.Id == id).AuthenticationId, null,
                                                                 out var error),
                        Is.True, error);

            await Until(() => StatusOf(id) == "waiting", "Being let go of", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain("switched off"));

            await using var later = new DiallingCSMS(Address, Name, password);

            Assert.That((await later.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.Unauthorized), "switched off");

        }

        #endregion

        #region TheCSMSTheStationDials_StaysTheOneInCharge_WhenAnotherDialsIn()

        /// <summary>
        /// The oldest connection is the one in charge, whoever opened it: a
        /// second back end that dials in is told that the station started and
        /// is answered, and takes nothing of what the station says to its
        /// CSMS - neither while it is in, nor by going away.
        /// </summary>
        [Test]
        public async Task TheCSMSTheStationDials_StaysTheOneInCharge_WhenAnotherDialsIn()
        {

            // The CSMS this station dials: it accepts the station and asks for a heartbeat every 5 s,
            // the shortest interval the station takes (ShortestHeartbeatInterval) - it used to ask
            // for one every second, which the station made 5 s of all the same.
            var uplinkPort  = IPPort.Parse(TestPorts.Free());
            var heartbeats  = 0;
            var boots       = 0;

            await using var uplink = new WebSocketServer(HTTPPort:               uplinkPort,
                                                         RequireAuthentication:  false,
                                                         AutoStart:              true);

            uplink.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                var message = JArray.Parse(text);

                if (message[0].Value<Int32>() != 2)
                    return;

                var action = message[2].Value<String>();

                if (action == "Heartbeat")         Interlocked.Increment(ref heartbeats);
                if (action == "BootNotification")  Interlocked.Increment(ref boots);

                await uplink.SendTextMessage(connection,
                                             new JArray(3, message[1],
                                                        action == "BootNotification"
                                                            ? new JObject(new JProperty("status",       "Accepted"),
                                                                          new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                                                                          new JProperty("interval",     5))
                                                            : action == "Heartbeat"
                                                                  ? new JObject(new JProperty("currentTime", DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")))
                                                                  : new JObject()).ToString(Formatting.None));

            };

            Assert.That(station!.Connections.TryAddConnection("The CSMS", $"ws://127.0.0.1:{uplinkPort}/cs001", "CSMS", true, null, null,
                                                              out var dialled, out var error),
                        Is.True, error);

            // Written down after it, and so not the one in charge.
            await Task.Delay(20);

            var password  = APassword();
            var id        = WrittenDown(password);

            Assert.Multiple(() => {
                Assert.That(station.IsPrimary(station.Connections.Connections.Single(one => one.Id == dialled)), Is.True);
                Assert.That(station.IsPrimary(station.Connections.Connections.Single(one => one.Id == id)),      Is.False);
            });

            station       = await TestStations.Started(station, directory);

            await Until(() => StatusOf(dialled!) == "connected" && Volatile.Read(ref heartbeats) > 0, "The uplink and its heartbeats", () => $"{StatusOf(dialled!)}: {SaidOf(dialled!)}");

            await using (var second = new DiallingCSMS(Address, Name, password))
            {

                Assert.That((await second.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

                await Until(() => second.Asked("BootNotification").Any(), "The BootNotification for the one that dialled in", Said);

                var before = Volatile.Read(ref heartbeats);

                // One is what says that they go on: two took 10 s more.
                await Until(() => Volatile.Read(ref heartbeats) >= before + 1, "Heartbeats going on to the CSMS while another is in", Said);

                // What it asks is answered all the same.
                await second.Client.SendTextMessage(new JArray(2, "from-the-second-1", "GetLocalListVersion", new JObject()).ToString(Formatting.None));

                await Until(() => second.AnswerTo("from-the-second-1") is not null, "The answer to the second back end's request", Said);

                Assert.Multiple(() => {
                    Assert.That(second.Asked("Heartbeat"),        Is.Empty, "what the station says to its CSMS went to the one that dialled in");
                    Assert.That(second.Asked("BootNotification").Count(), Is.EqualTo(1));
                    Assert.That(Volatile.Read(ref boots),         Is.EqualTo(1), "the CSMS was told again that the station started");
                });

                await second.Client.Close();

            }

            await Until(() => StatusOf(id) == "waiting", "Waiting for the second one again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // Gone, it took the way to the CSMS with it neither.
            var after = Volatile.Read(ref heartbeats);

            await Until(() => Volatile.Read(ref heartbeats) >= after + 1, "Heartbeats going on to the CSMS after the other one left", Said);

            Assert.That(StatusOf(dialled!), Is.EqualTo("connected"));

        }

        #endregion

        #region AOneTimePasswordLetsItIn_AndOCPP16HasAPortOfItsOwn()

        /// <summary>
        /// The other two things a connection the other end makes can be: one
        /// that signs in with a one-time password, and one that speaks OCPP
        /// 1.6 - to the station's other node, on the other port.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordLetsItIn_AndOCPP16HasAPortOfItsOwn()
        {

            var connections = station!.Connections;

            Assert.That(connections.TryAddAuthentication("What the CSMS signs in with", "totp", Name, APassword() + APassword(),
                                                         out var totpLogin, out var error),
                        Is.True, error);

            Assert.That(connections.TryAddConnection("The CSMS, dialling in", Address, "CSMS", true, totpLogin, null,
                                                     out var id, out error,
                                                     Inbound: true),
                        Is.True, error);

            var password16 = APassword();

            Assert.That(connections.TryAddAuthentication("What the Central System signs in with", "basic", "cs-16", password16,
                                                         out var login16, out error),
                        Is.True, error);

            Assert.That(connections.TryAddConnection("The Central System, dialling in", $"ws://127.0.0.1:{port16}/cs-16", "CSMS", true, login16, null,
                                                     out var id16, out error,
                                                     OCPPVersion: "OCPP1.6",
                                                     Inbound:     true),
                        Is.True, error);

            station = await TestStations.Started(station, directory);

            // What a client that knows nothing but Basic has of a TOTP secret: the one-time password, as its password.
            var totp = connections.Authentications.Single(one => one.Id == totpLogin).ToTOTPConfig()!;

            await using var stale = new DiallingCSMS(Address, Name, "000000000000");
            await using var csms  = new DiallingCSMS(Address, Name, TOTPGenerator.GenerateTOTP(totp.SharedSecret, totp.ValidityTime, totp.Length).Current);

            Assert.Multiple(async () => {
                Assert.That((await stale.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.Unauthorized),       "something that is no one-time password of it");
                Assert.That((await csms. Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "its one-time password");
            });

            await Until(() => csms.Asked("BootNotification").Any(), "The BootNotification of the OCPP 2.1 node", Said);

            // OCPP 1.6: the other node, the other port - and not this one's.
            await using var onTheWrongPort = new DiallingCSMS($"ws://127.0.0.1:{port}/cs-16",   "cs-16", password16, "ocpp1.6");
            await using var centralSystem  = new DiallingCSMS($"ws://127.0.0.1:{port16}/cs-16", "cs-16", password16, "ocpp1.6");

            Assert.That((await onTheWrongPort.Client.Connect()).Item2.HTTPStatusCode, Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "OCPP 1.6 on the port of OCPP 2.1");
            Assert.That((await centralSystem. Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => centralSystem.Asked("BootNotification").Any(), "The BootNotification of the OCPP 1.6 node", Said);

            await Until(() => StatusOf(id!) == "connected" && StatusOf(id16!) == "connected", "Both being connected",
                        () => $"{StatusOf(id!)}: {SaidOf(id!)} / {StatusOf(id16!)}: {SaidOf(id16!)}");

            var said = station.OCPPServerJSON();

            Assert.Multiple(() => {
                Assert.That(centralSystem.Asked("BootNotification").First()[3]!["chargePointVendor"], Is.Not.Null, "what came was no BootNotification of OCPP 1.6");
                Assert.That(csms.Asked("BootNotification").First()[3]!["chargingStation"],            Is.Not.Null, "what came was no BootNotification of OCPP 2.1");
                Assert.That(said["servers"]![0]!["connected"]!.Values<String>(), Is.EqualTo(new[] { Name }));
                Assert.That(said["servers"]![1]!["connected"]!.Values<String>(), Is.EqualTo(new[] { "cs-16" }));
            });

        }

        #endregion

        #region TheServerIsSwitchedOffAndOn_WithoutARestart()

        [Test]
        public async Task TheServerIsSwitchedOffAndOn_WithoutARestart()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Started(station!, directory);

            var csms = new DiallingCSMS(Address, Name, password);

            Assert.That((await csms.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // Off: whoever is in is let go of, and nothing listens any more.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", false), new JProperty("port", port.ToUInt16())),
                                                                 out var error, out _),
                        Is.True, error);

            await Until(() => StatusOf(id) == "waiting", "Being let go of", () => $"{StatusOf(id)}: {SaidOf(id)}");

            var off = station.OCPPServerJSON();

            Assert.Multiple(() => {
                Assert.That(SaidOf(id),                                        Does.Contain("serves no OCPP2.1 to whoever dials it"));
                Assert.That(off.Value<Boolean>("enabled"),                     Is.False);
                Assert.That(off["servers"]![0]!.Value<Boolean>("served"),      Is.False);
                Assert.That(off["servers"]![0]!.Value<Int32>("port"),          Is.EqualTo(port.ToUInt16()), "the port is forgotten with the switch");
            });

            // The one that was in is gone for good: a client that comes back by itself would be in
            // again the moment the server is.
            await csms.DisposeAsync();

            // What is wrong with a section is said, and nothing is changed.
            Assert.Multiple(() => {

                Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true)), out var why, out _), Is.False);
                Assert.That(why, Does.Contain("names no port"));

                Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true), new JProperty("port", 9443), new JProperty("port16", 9443)), out why, out _), Is.False);
                Assert.That(why, Does.Contain("the same port"));

                Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true), new JProperty("tls", true)), out why, out _), Is.False);
                Assert.That(why, Does.Contain("is not known"));

            });

            // On again, on the port it had: the same CSMS dials again and is in.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true), new JProperty("port", port.ToUInt16())),
                                                                 out error, out _),
                        Is.True, error);

            Assert.That(station.OCPPServerJSON()["servers"]![1]!.Value<Boolean>("served"), Is.False, "OCPP 1.6 is served without a port");

            await Until(() => SaidOf(id).Contains("Waiting for"), "Waiting for it again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            await using var again = new DiallingCSMS(Address, Name, password);

            Assert.That((await again.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // And the file says what the station does.
            var file = JObject.Parse(File.ReadAllText(Path.Combine(directory, "configuration.json")));

            Assert.That(file["ocppServer"]!.ToString(Formatting.None), Is.EqualTo($"{{\"enabled\":true,\"port\":{port.ToUInt16()}}}"), "the port of OCPP 1.6 was not taken away");

        }

        #endregion

        #region WhoGuessesTooOften_IsToldToWait()

        [Test]
        public async Task WhoGuessesTooOften_IsToldToWait()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Started(station!, directory);

            var answers   = new List<HTTPStatusCode>();

            for (var guess = 0; guess < ChargingStation.MaxFailedOCPPSignInsPerMinute + 1; guess++)
            {
                await using var guessing = new DiallingCSMS(Address, Name, APassword());
                answers.Add((await guessing.Client.Connect()).Item2.HTTPStatusCode);
            }

            // And the right password does not open it either, while the address waits.
            await using var csms = new DiallingCSMS(Address, Name, password);
            var afterwards = (await csms.Client.Connect()).Item2.HTTPStatusCode;

            Assert.Multiple(() => {
                Assert.That(answers.Take((Int32) ChargingStation.MaxFailedOCPPSignInsPerMinute), Is.All.EqualTo(HTTPStatusCode.Unauthorized));
                Assert.That(answers.Last(),  Is.EqualTo(HTTPStatusCode.TooManyRequests), "one guess more than it is given");
                Assert.That(afterwards,      Is.EqualTo(HTTPStatusCode.TooManyRequests), "the right password, from an address that waits");
                Assert.That(StatusOf(id),    Is.EqualTo("waiting"));
            });

        }

        #endregion

        #region WhatIsWrittenDown_IsHeldToWhatAnInboundConnectionCanSay()

        [Test]
        public void WhatIsWrittenDown_IsHeldToWhatAnInboundConnectionCanSay()
        {

            var connections = station!.Connections;

            Assert.That(connections.TryAddAuthentication("csms-01", "basic", Name,      APassword(), out var login,   out var error), Is.True, error);
            Assert.That(connections.TryAddAuthentication("another", "basic", "another", APassword(), out var another, out error),     Is.True, error);

            Boolean Add(String URL, String? Credentials, out String? Why, String? HTTPVersion = null)
                => connections.TryAddConnection("The CSMS, dialling in", URL, "CSMS", true, Credentials, null, out _, out Why,
                                                HTTPVersion: HTTPVersion, Inbound: true);

            Assert.Multiple(() => {

                Assert.That(Add($"ws://127.0.0.1:{port}/",              login,   out var why), Is.False);
                Assert.That(why, Does.Contain("ends with the name"));

                Assert.That(Add($"ws://127.0.0.1:{port}/CSMS",          login,   out why),     Is.False);
                Assert.That(why, Does.Contain("keeps for itself"));

                Assert.That(Add($"ssh://{Name}@127.0.0.1:{port}",       login,   out why),     Is.False);
                Assert.That(why, Does.Contain("Over SSH"));

                Assert.That(Add(Address,                                null,    out why),     Is.False);
                Assert.That(why, Does.Contain("or credentials. Nothing gets in without"));

                Assert.That(Add(Address,                                another, out why),     Is.False);
                Assert.That(why, Does.Contain("the two have to agree"));

                Assert.That(Add(Address,                                login,   out why, HTTPVersion: "2"), Is.False);
                Assert.That(why, Does.Contain("chosen by whoever dials"));

                Assert.That(Add(Address,                                login,   out why),     Is.True, why);

                Assert.That(Add(Address,                                login,   out why),     Is.False);
                Assert.That(why, Does.Contain("comes under already"));

            });

            var entry = connections.Connections.Single();

            Assert.Multiple(() => {
                Assert.That(entry.Inbound,                                Is.True);
                Assert.That(entry.InboundName,                            Is.EqualTo(Name));
                Assert.That(entry.ToJSON().Value<Boolean>("inbound"),     Is.True);
                Assert.That(entry.ToJSON().Value<String>("inboundName"),  Is.EqualTo(Name));
            });

        }

        #endregion

        #region LetGoOf_ItsOCPPNodesStopCleanly_ThoughItsServerWentFirst()

        /// <summary>
        /// A station that served OCPP and is let go of stops its OCPP server
        /// first and its OCPP nodes after it. The server was the nodes', and a
        /// node stops its servers: one that was let go of already could not be
        /// stopped again, so that stopping the node failed there - "The
        /// CancellationTokenSource has been disposed" - and left its SSH
        /// servers and clients running. The server leaves its node first.
        /// </summary>
        [Test]
        public async Task LetGoOf_ItsOCPPNodesStopCleanly_ThoughItsServerWentFirst()
        {

            station = await TestStations.Started(station!, directory);

            Assert.That(Said("A CSMS or a local controller may dial this charging station"), Is.Not.Empty, "The OCPP server did not start.");

            await station.DisposeAsync();
            station = null;

            Assert.That(Said("Stopping the OCPP node"), Is.Empty);

        }

        private String[] Said(String Part)
        {
            lock (said)
                return [.. said.Where(message => message.Contains(Part))];
        }

        #endregion

    }

}
