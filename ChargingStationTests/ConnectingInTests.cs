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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The OCPP server of a station: a CSMS that connects to the station, rather
    /// than being connected to by it. OCPP as it always is - the station says that
    /// it started, the CSMS asks and is answered - and nothing gets in that is
    /// not written down as a connection the other end makes.
    /// </summary>
    [TestFixture]
    public class ConnectingInTests
    {

        #region Data

        private static readonly User_Id Name      = User_Id.Parse("csms-01");

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

            directory  = TestStations.TemporaryDirectory("connecting-in");
            port       = TestPorts.Free();
            // OCPP 1.6 on the same port: the subprotocol it asks for decides.
            port16     = port;

            var configuration = TestStations.Offline;

            configuration.Add("webSocketServer", new JObject(
                                                new JProperty("enabled", true),
                                                new JProperty("port",    port.ToUInt16())
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
        /// The connection the CSMS makes, written down, and the WebSocket
        /// login of its name with the password it signs in with.
        /// </summary>
        private String WrittenDown(String Password, Boolean LetIn = true)
        {

            Assert.That(station!.WebSocketLogins.TrySetPasswordAuthentication(Name, Password, null, I18NString.Create("What the CSMS signs in with"), out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), URL.Parse(Address), "CSMS", LetIn, null, null,
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
            => station!.OutboundConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

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
        /// A CSMS that connects: a WebSocket client that keeps what it is sent
        /// and accepts a BootNotification.
        /// </summary>
        private sealed class ConnectingCSMS : IAsyncDisposable
        {

            public WebSocketClient          Client    { get; }
            public ConcurrentQueue<JArray>  Received  { get; } = new ();

            public ConnectingCSMS(String URL, String Login, String Password, String Subprotocol = "ocpp2.1")
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


        #region ACSMSThatConnects_IsToldThatTheStationStarted_AndIsAnswered()

        [Test]
        public async Task ACSMSThatConnects_IsToldThatTheStationStarted_AndIsAnswered()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Booted(station!, directory);

            await Until(() => StatusOf(id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain($"Waiting for '{Name}' to connect"));

            await using var csms = new ConnectingCSMS(Address, Name.ToString(), password);

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

                Assert.That(Said(),                                            Does.Contain($"'{Name}' connected"));
                Assert.That(SaidOf(id),                                        Does.Contain("the CSMS accepted this station"));
                Assert.That(csms.Asked("BootNotification").Count(),            Is.EqualTo(1));
                Assert.That(csms.AnswerTo("from-the-csms-1")![0].Value<Int32>(), Is.EqualTo(3), csms.AnswerTo("from-the-csms-1")!.ToString(Formatting.None));

                Assert.That(station!.OCPPServerConfigurationJSON()["state"]!["connected"]!.Values<String>(), Is.EqualTo(new[] { Name.ToString() }));

            });

            // Gone, it is waited for again - and let in again.
            await csms.Client.Close();

            await Until(() => StatusOf(id) == "waiting", "Waiting again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain("went away"));

            await using var again = new ConnectingCSMS(Address, Name.ToString(), password);

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

            station       = await TestStations.Booted(station!, directory);

            // A login of its own, and no connection the other end makes under
            // its name: let in, as every node lets in its logins.
            Assert.That(station.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("csms-03"), password, null, I18NString.Create("A login and nothing else"), out var loginOnly),
                        Is.True, loginOnly);

            await using var wrongPassword = new ConnectingCSMS(Address, Name.ToString(), APassword());
            await using var unknown       = new ConnectingCSMS($"ws://127.0.0.1:{port}/csms-02", "csms-02", password);
            await using var asAnother     = new ConnectingCSMS($"ws://127.0.0.1:{port}/csms-02", Name.ToString(),      password);
            await using var noConnection  = new ConnectingCSMS($"ws://127.0.0.1:{port}/csms-03", "csms-03", password);

            var refused1  = (await wrongPassword.Client.Connect()).Item2.HTTPStatusCode;
            var refused2  = (await unknown.      Client.Connect()).Item2.HTTPStatusCode;
            var refused3  = (await asAnother.    Client.Connect()).Item2.HTTPStatusCode;
            var letIn4    = (await noConnection. Client.Connect()).Item2.HTTPStatusCode;

            Assert.Multiple(() => {
                Assert.That(refused1,      Is.EqualTo(HTTPStatusCode.Unauthorized), "a wrong password");
                Assert.That(refused2,      Is.EqualTo(HTTPStatusCode.Unauthorized), "a name nobody wrote down");
                Assert.That(refused3,      Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "under another name than its own");
                Assert.That(letIn4,        Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "a login, and no connection the other end makes under its name");
                Assert.That(StatusOf(id),  Is.EqualTo("waiting"));
            });

            // Switched off, the one that is written down is not let in either -
            // and one that is in is let go of.
            await using var csms = new ConnectingCSMS(Address, Name.ToString(), password);

            Assert.That((await csms.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(station!.Connections.TryUpdateConnection(id, I18NString.Create("The CSMS, connecting"), URL.Parse(Address), "CSMS", false, null, null,
                                                                 out var error),
                        Is.True, error);

            await Until(() => StatusOf(id) == "waiting", "Being let go of", () => $"{StatusOf(id)}: {SaidOf(id)}");

            Assert.That(SaidOf(id), Does.Contain("switched off"));

            // Not a no, a not now: it signed in rightly, and is asked to come
            // back by itself once the connection is switched on again.
            await using var later = new ConnectingCSMS(Address, Name.ToString(), password);

            var turnedAway = (await later.Client.Connect()).Item2;

            Assert.Multiple(() => {
                Assert.That(turnedAway.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.ServiceUnavailable), "switched off");
                Assert.That(turnedAway.RetryAfter,     Is.Not.Null,                                   "when to try again");
            });

        }

        #endregion

        #region TheCSMSTheStationConnectsTo_StaysTheOneInCharge_WhenAnotherConnects()

        /// <summary>
        /// The oldest connection is the one in charge, whoever opened it: a
        /// second back end that connects is told that the station started and
        /// is answered, and takes nothing of what the station says to its
        /// CSMS - neither while it is in, nor by going away.
        /// </summary>
        [Test]
        public async Task TheCSMSTheStationConnectsTo_StaysTheOneInCharge_WhenAnotherConnects()
        {

            // The CSMS this station connects to: it accepts the station and asks for a heartbeat every 5 s,
            // the shortest interval the station takes (ShortestHeartbeatInterval) - it used to ask
            // for one every second, which the station made 5 s of all the same.
            var uplinkPort  = TestPorts.Free();
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

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{uplinkPort}/cs001"), "CSMS", true, null, null,
                                                              out var uplinkId, out var error),
                        Is.True, error);

            // Written down after it, and so not the one in charge.
            await Task.Delay(20);

            var password  = APassword();
            var id        = WrittenDown(password);

            Assert.Multiple(() => {
                Assert.That(station.IsPrimary(station.Connections.Connections.Single(one => one.Id == uplinkId)), Is.True);
                Assert.That(station.IsPrimary(station.Connections.Connections.Single(one => one.Id == id)),       Is.False);
            });

            station       = await TestStations.Booted(station, directory);

            await Until(() => StatusOf(uplinkId!) == "connected" && Volatile.Read(ref heartbeats) > 0, "The uplink and its heartbeats", () => $"{StatusOf(uplinkId!)}: {SaidOf(uplinkId!)}");

            await using (var second = new ConnectingCSMS(Address, Name.ToString(), password))
            {

                Assert.That((await second.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

                await Until(() => second.Asked("BootNotification").Any(), "The BootNotification for the one that connected", Said);

                var before = Volatile.Read(ref heartbeats);

                // One is what says that they go on: two took 10 s more.
                await Until(() => Volatile.Read(ref heartbeats) >= before + 1, "Heartbeats going on to the CSMS while another is in", Said);

                // What it asks is answered all the same.
                await second.Client.SendTextMessage(new JArray(2, "from-the-second-1", "GetLocalListVersion", new JObject()).ToString(Formatting.None));

                await Until(() => second.AnswerTo("from-the-second-1") is not null, "The answer to the second back end's request", Said);

                Assert.Multiple(() => {
                    Assert.That(second.Asked("Heartbeat"),        Is.Empty, "what the station says to its CSMS went to the one that connected");
                    Assert.That(second.Asked("BootNotification").Count(), Is.EqualTo(1));
                    Assert.That(Volatile.Read(ref boots),         Is.EqualTo(1), "the CSMS was told again that the station started");
                });

                await second.Client.Close();

            }

            await Until(() => StatusOf(id) == "waiting", "Waiting for the second one again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // Gone, it took the way to the CSMS with it neither.
            var after = Volatile.Read(ref heartbeats);

            await Until(() => Volatile.Read(ref heartbeats) >= after + 1, "Heartbeats going on to the CSMS after the other one left", Said);

            Assert.That(StatusOf(uplinkId!), Is.EqualTo("connected"));

        }

        #endregion

        #region AOneTimePasswordLetsItIn_AndOCPP16ComesInOnTheSamePort()

        /// <summary>
        /// The other two things a connection the other end makes can be: one
        /// that signs in with a one-time password, and one that speaks OCPP
        /// 1.6 - on the same port, and handed by the subprotocol it asks for
        /// to the station's other node, whose BootNotification is OCPP 1.6's.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordLetsItIn_AndOCPP16ComesInOnTheSamePort()
        {

            var connections = station!.Connections;

            Assert.That(station.WebSocketLogins.TrySetTOTPAuthentication(Name, APassword() + APassword(), null, null, null, null, null, I18NString.Create("What the CSMS signs in with"),
                                                           out var error),
                        Is.True, error);

            Assert.That(connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), URL.Parse(Address), "CSMS", true, null, null,
                                                     out var id, out error,
                                                     Inbound: true),
                        Is.True, error);

            var password16 = APassword();

            Assert.That(station.WebSocketLogins.TrySetPasswordAuthentication(User_Id.Parse("cs-16"), password16, null, I18NString.Create("What the Central System signs in with"), out error),
                        Is.True, error);

            Assert.That(connections.TryAddConnection(I18NString.Create("The Central System, connecting"), URL.Parse($"ws://127.0.0.1:{port16}/cs-16"), "CSMS", true, null, null,
                                                     out var id16, out error,
                                                     OCPPVersion: "OCPP1.6",
                                                     Inbound:     true),
                        Is.True, error);

            station = await TestStations.Booted(station, directory);

            // What a client that knows nothing but Basic has of a TOTP secret: the one-time password, as its password.
            Assert.That(station.WebSocketLogins.TryGet(Name, out var totpLogin), Is.True);

            var totp = totpLogin!.TOTP!.ToTOTPConfig();

            await using var stale = new ConnectingCSMS(Address, Name.ToString(), "000000000000");
            await using var csms  = new ConnectingCSMS(Address, Name.ToString(), TOTPGenerator.GenerateTOTP(totp.SharedSecret, totp.ValidityTime, totp.Length).Current);

            Assert.Multiple(async () => {
                Assert.That((await stale.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.Unauthorized),       "something that is no one-time password of it");
                Assert.That((await csms. Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "its one-time password");
            });

            await Until(() => csms.Asked("BootNotification").Any(), "The BootNotification of the OCPP 2.1 node", Said);

            // OCPP 1.6: the other node, on the same port - by the subprotocol.
            await using var centralSystem  = new ConnectingCSMS($"ws://127.0.0.1:{port16}/cs-16", "cs-16", password16, "ocpp1.6");

            Assert.That(port16, Is.EqualTo(port));
            Assert.That((await centralSystem.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => centralSystem.Asked("BootNotification").Any(), "The BootNotification of the OCPP 1.6 node", Said);

            await Until(() => StatusOf(id!) == "connected" && StatusOf(id16!) == "connected", "Both being connected",
                        () => $"{StatusOf(id!)}: {SaidOf(id!)} / {StatusOf(id16!)}: {SaidOf(id16!)}");

            var said = station.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(centralSystem.Asked("BootNotification").First()[3]!["chargePointVendor"], Is.Not.Null, "what came was no BootNotification of OCPP 1.6");
                Assert.That(csms.Asked("BootNotification").First()[3]!["chargingStation"],            Is.Not.Null, "what came was no BootNotification of OCPP 2.1");
                Assert.That(said["state"]!["connected"]!.Values<String>(), Is.EquivalentTo(new[] { Name.ToString(), "cs-16" }), "both, on the one port");
            });

        }

        #endregion

        #region TheServerIsSwitchedOffAndOn_WithoutARestart()

        [Test]
        public async Task TheServerIsSwitchedOffAndOn_WithoutARestart()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Booted(station!, directory);

            var csms = new ConnectingCSMS(Address, Name.ToString(), password);

            Assert.That((await csms.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // Off: whoever is in is let go of, and nothing listens any more.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", false), new JProperty("port", port.ToUInt16())),
                                                                 out var error, out _),
                        Is.True, error);

            await Until(() => StatusOf(id) == "waiting", "Being let go of", () => $"{StatusOf(id)}: {SaidOf(id)}");

            var off = station.OCPPServerConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(SaidOf(id),                                        Does.Contain("serves no OCPP2.1 to whoever connects to it"));
                Assert.That(off.Value<Boolean>("enabled"),                     Is.False);
                Assert.That(off["state"]!.Value<Boolean>("running"),           Is.False);
                Assert.That(off.Value<Int32>("port"),                          Is.EqualTo(port.ToUInt16()), "the port is forgotten with the switch");
            });

            // The one that was in is gone for good: a client that comes back by itself would be in
            // again the moment the server is.
            await csms.DisposeAsync();

            // What is wrong with a section is said, and nothing is changed.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true), new JProperty("port", 9443), new JProperty("port16", 9444)), out var why, out _), Is.False);
            Assert.That(why, Does.Contain("'webSocketServer.port16' is no more"));

            // On again, on the port it had: the same CSMS connects again and is in.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(new JProperty("enabled", true), new JProperty("port", port.ToUInt16())),
                                                                 out error, out _),
                        Is.True, error);

            Assert.That(station.StationServers.Count(), Is.EqualTo(2), "OCPP 1.6 is not served on the one port");

            await Until(() => SaidOf(id).Contains("Waiting for"), "Waiting for it again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            await using var again = new ConnectingCSMS(Address, Name.ToString(), password);

            Assert.That((await again.Client.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols));

            await Until(() => StatusOf(id) == "connected", "Being connected again", () => $"{StatusOf(id)}: {SaidOf(id)}");

            // And the file says what the station does.
            var file = JObject.Parse(File.ReadAllText(Path.Combine(directory, "configuration.json")));

            Assert.That(file["webSocketServer"]!.ToString(Formatting.None), Is.EqualTo($"{{\"enabled\":true,\"port\":{port.ToUInt16()}}}"));

        }

        #endregion

        #region WhoGuessesTooOften_IsToldToWait()

        [Test]
        public async Task WhoGuessesTooOften_IsToldToWait()
        {

            var password  = APassword();
            var id        = WrittenDown(password);

            station       = await TestStations.Booted(station!, directory);

            var answers   = new List<HTTPStatusCode>();

            for (var guess = 0; guess < protocols.WWCP.Node.SignInBrake.DefaultMaxFailedPerMinute + 1; guess++)
            {
                await using var guessing = new ConnectingCSMS(Address, Name.ToString(), APassword());
                answers.Add((await guessing.Client.Connect()).Item2.HTTPStatusCode);
            }

            // And the right password does not open it either, while the address waits.
            await using var csms = new ConnectingCSMS(Address, Name.ToString(), password);
            var afterwards = (await csms.Client.Connect()).Item2.HTTPStatusCode;

            Assert.Multiple(() => {
                Assert.That(answers.Take((Int32) protocols.WWCP.Node.SignInBrake.DefaultMaxFailedPerMinute), Is.All.EqualTo(HTTPStatusCode.Unauthorized));
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

            // Credentials of this station: what it proves itself with where it connects to.
            Assert.That(connections.TryAddPasswordAuthentication(Name, APassword(), I18NString.Create("csms-01"), out var login, out var error), Is.True, error);

            Boolean Add(String URL, String? Credentials, out String? Why, String? HTTPVersion = null)
                => connections.TryAddConnection(I18NString.Create("The CSMS, connecting"), org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(URL), "CSMS", true, Credentials, null, out _, out Why,
                                                HTTPVersion: HTTPVersion, Inbound: true);

            Assert.Multiple(() => {

                Assert.That(Add($"ws://127.0.0.1:{port}/",              null,    out var why), Is.False);
                Assert.That(why, Does.Contain("ends with the name"));

                Assert.That(Add($"ws://127.0.0.1:{port}/CSMS",          null,    out why),     Is.False);
                Assert.That(why, Does.Contain("keeps for itself"));

                Assert.That(Add($"ssh://127.0.0.1:{port}",              null,    out why),     Is.False);
                Assert.That(why, Does.Contain("names the login the other end signs in with"));

                Assert.That(Add(Address,                                login,   out why),     Is.False);
                Assert.That(why, Does.Contain("signs in with its WebSocket login"));

                Assert.That(Add(Address,                                null,    out why, HTTPVersion: "2"), Is.False);
                Assert.That(why, Does.Contain("chosen by whoever connects"));

                Assert.That(Add(Address,                                null,    out why),     Is.True, why);

                Assert.That(Add(Address,                                null,    out why),     Is.False);
                Assert.That(why, Does.Contain("comes under already"));

            });

            var entry = connections.Connections.Single();

            Assert.Multiple(() => {
                Assert.That(entry.Inbound,                                Is.True);
                Assert.That(entry.InboundName,                            Is.EqualTo(Name.ToString()));
                Assert.That(entry.ToJSON().Value<Boolean>("inbound"),     Is.True);
                Assert.That(entry.ToJSON().Value<String>("inboundName"),  Is.EqualTo(Name.ToString()));
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

            station = await TestStations.Booted(station!, directory);

            Assert.That(station.OCPPServerRunning, Is.True, "The station port did not start.");

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
