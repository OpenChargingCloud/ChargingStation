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
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A charging station that dials its Local Controller over wss:// and
    /// believes it by the key pin of its key ("Key Pins and Raw Public Keys",
    /// work package K2): pinned at commissioning, it connects to a server whose
    /// self-signed certificate names something else and no CA vouches for, and
    /// again after a restart with its clock far from that certificate's
    /// validity; it refuses the same server with another key, and never asks a
    /// CA instead; it learns a key pin on first use only where told to, keeps
    /// it on probation, and forgets it where no signed pin set confirms it.
    /// </summary>
    /// <remarks>
    /// A Hermod WebSocket server stands in for the Local Controller, with a
    /// carrier certificate made in memory for a name the station does not dial
    /// - nothing goes into a certificate store - and the least of a CSMS behind
    /// it: it answers a BootNotification with Accepted, and every other request
    /// with an empty answer, and keeps what it was sent.
    /// </remarks>
    [TestFixture]
    public partial class DiallingWithKeyPinsTests
    {

        #region (class) LocalController

        /// <summary>
        /// A Local Controller of the least kind, over wss://, whose certificate
        /// can be changed between two handshakes.
        /// </summary>
        private sealed class LocalController : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>  received  = new ();
            private          Int32                    accepted;

            public WebSocketServer   Server       { get; }
            public IPPort            Port         { get; }

            /// <summary>
            /// The certificate it shows at the next handshake.
            /// </summary>
            public X509Certificate2  Certificate  { get; set; }

            public LocalController(X509Certificate2 Certificate)
            {

                this.Certificate  = Certificate;
                this.Port         = IPPort.Parse(TestPorts.Free());

                Server = new WebSocketServer(HTTPPort:                   Port,
                                             RequireAuthentication:      false,
                                             ServerCertificateSelector:  (tcpServer, tcpClient) => this.Certificate,
                                             AutoStart:                  true);

                Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {
                    Interlocked.Increment(ref accepted);
                    return Task.CompletedTask;
                };

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

            /// <summary>
            /// How many WebSocket connections it took, ever.
            /// </summary>
            public Int32 Accepted
                => Volatile.Read(ref accepted);

            /// <summary>
            /// The requests of the given action it was sent so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == Action)];

            /// <summary>
            /// Ask the station something over the connection it has now, and wait
            /// for the answer: a CALLRESULT or a CALLERROR.
            /// </summary>
            /// <param name="Id">The message id, for a request signed for it; a new one otherwise.</param>
            public async Task<JArray> Ask(String Action, JObject Payload, String? Id = null)
            {

                var id = Id ?? Guid.NewGuid().ToString();

                await Server.SendTextMessage(Server.WebSocketConnections.Last(), new JArray(2, id, Action, Payload).ToString(Formatting.None));

                var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

                while (DateTimeOffset.UtcNow < until)
                {
                    if (received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == id) is JArray answer)
                        return answer;
                    await Task.Delay(20);
                }

                throw new TimeoutException($"The station did not answer '{Action}' within 10 s.");

            }

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region (class) ShiftedClock

        /// <summary>
        /// The real clock, shifted: a station whose clock was never set.
        /// </summary>
        private sealed class ShiftedClock(TimeSpan By) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + By;
        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private LocalController?  lc;

        private ECDsa             lcKey      = default!;
        private ECDsa             otherKey   = default!;

        private readonly List<X509Certificate2> certificates = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStationAndALocalController()
        {

            directory  = TestStations.TemporaryDirectory("dialling-keypins");
            station    = TestStations.New(directory, TestStations.Offline);

            lcKey      = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            otherKey   = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            lc         = new LocalController(Carrier(lcKey));

        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (lc is not null)
                await lc.DisposeAsync();

            foreach (var certificate in certificates)
                certificate.Dispose();

            certificates.Clear();

            lcKey.   Dispose();
            otherKey.Dispose();

            station  = null;
            lc       = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        /// <summary>
        /// A self-signed carrier certificate for the given key - CA:FALSE, no
        /// keyCertSign, a name nobody dials - that SslStream can serve on Windows,
        /// valid when it is told or from yesterday for a month.
        /// </summary>
        private X509Certificate2 Carrier(ECDsa            Key,
                                         DateTimeOffset?  NotBefore  = null,
                                         DateTimeOffset?  NotAfter   = null)
        {

            var request = new CertificateRequest("CN=carrier.invalid", Key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));

            using var made = request.CreateSelfSigned(NotBefore ?? DateTimeOffset.UtcNow.AddDays(-1), NotAfter ?? DateTimeOffset.UtcNow.AddDays(30));

            var certificate = X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            certificates.Add(certificate);

            return certificate;

        }

        private String URL
            => $"wss://127.0.0.1:{lc!.Port}/cs001";

        private async Task Start()
            => station = await TestStations.Started(station!, directory);

        private String Added(String? Pins, ServerKeyPinSettings? Trust = null)
        {
            Assert.That(station!.Connections.TryAddConnection("The local controller", URL, "LocalController", true, null, null,
                                                              out var id, out var error, ServerKeyPins: Pins, KeyPinTrust: Trust),
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

        /// <summary>
        /// The security events of the given type the station reported: waiting
        /// in its queue, or sent to the Local Controller.
        /// </summary>
        private JObject[] Reported(String Type)

            => [.. station!.OCPPv2_1Node.OfflineQueue.Pending.
                       Where (message => message.Kind == "SecurityEventNotification" && message.Payload.Value<String>("type") == Type).
                       Select(message => message.Payload).
                   Concat(lc!.Requests("SecurityEventNotification").
                       Select(request => (JObject) request[3]!).
                       Where (payload => payload.Value<String>("type") == Type))];

        #endregion


        #region APinnedStationConnectsByKeyPinAlone_AndSendsItsBootNotification()

        /// <summary>
        /// Pinned at commissioning, the station connects to a server whose
        /// certificate no CA vouches for and names another host than the one
        /// dialled, and registers with it.
        /// </summary>
        [Test]
        public async Task APinnedStationConnectsByKeyPinAlone_AndSendsItsBootNotification()
        {

            var id = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();

            await Until(() => IsConnected(id),                                "Connecting by key pin", () => SaidOf(id));
            await Until(() => lc!.Requests("BootNotification").Length > 0,   "A BootNotification over wss://");

            Assert.Multiple(() => {
                Assert.That(station!.KeyPinTrustOf(id)?.LastValidation?.Outcome,  Is.EqualTo(KeyPinOutcome.Accepted));
                Assert.That(Reported("InvalidCsmsCertificate"),                    Is.Empty);
                Assert.That(Entry.ServerKeyPinState,                               Is.EqualTo(KeyPinState.Configured));
            });

        }

        #endregion

        #region TheSameServerWithAnotherKey_IsRefused_ReportedOnce_AndNotConnected()

        /// <summary>
        /// The same server with another key is refused at every handshake: no
        /// WebSocket connection is made, it is reported as InvalidCsmsCertificate
        /// naming the endpoint and the key pin shown - once, not at every attempt
        /// - and the pin is not changed by it.
        /// </summary>
        [Test]
        public async Task TheSameServerWithAnotherKey_IsRefused_ReportedOnce_AndNotConnected()
        {

            lc!.Certificate = Carrier(otherKey);

            var id = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();

            await Until(() => (station!.KeyPinTrustOf(id)?.Handshakes ?? 0) >= 2, "Two handshakes", () => SaidOf(id));

            var refused = Reported("InvalidCsmsCertificate");

            Assert.Multiple(() => {

                Assert.That(lc.Accepted,                Is.Zero, "a WebSocket connection was made to a server with another key");
                Assert.That(IsConnected(id),            Is.False);
                Assert.That(SaidOf(id),                 Does.Contain("was not believed").And.Contain(KeyPin.FromKey(otherKey).ToHex()));

                Assert.That(refused.Length,             Is.EqualTo(1), "not reported once per change of the verdict");
                Assert.That(refused.FirstOrDefault()?.Value<String>("techInfo"), Does.Contain(KeyPin.FromKey(otherKey).ToHex()).And.Contain(id));

                Assert.That(Entry.ServerKeyPins,        Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }), "the pin was changed by a server with another key");

            });

        }

        #endregion

        #region AKeyPinnedEndpointNeverFallsBackToTheCA()

        /// <summary>
        /// The validator a key-pinned connection is dialled with refuses a key that
        /// matches none of its pins even where a CA vouches for the certificate,
        /// and believes the pinned key where none does.
        /// </summary>
        /// <remarks>
        /// A certificate a CA vouches for is one SslStream hands over without a
        /// policy error. These tests put no root into a certificate store, so the
        /// handshake with the real server is the refused one, and the validator
        /// it was refused by is then asked again as SslStream would ask it of a
        /// CA-valid certificate.
        /// </remarks>
        [Test]
        public async Task AKeyPinnedEndpointNeverFallsBackToTheCA()
        {

            var other  = Carrier(otherKey);
            lc!.Certificate = other;

            var id     = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();

            await Until(() => station!.KeyPinTrustOf(id)?.Refused == true, "Refusing the other key", () => SaidOf(id));

            var validator = station!.KeyPinTrustOf(id)!.Validator;

            await using var client = new WebSocketClient(org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(URL));

            Assert.Multiple(() => {
                Assert.That(validator(this, other,             null, client, SslPolicyErrors.None).IsValid, Is.False,
                            "a certificate of another key was believed because a CA would have vouched for it");
                Assert.That(validator(this, Carrier(lcKey),    null, client, SslPolicyErrors.RemoteCertificateChainErrors |
                                                                                SslPolicyErrors.RemoteCertificateNameMismatch).IsValid, Is.True,
                            "the pinned key was refused for its chain or its name");
                Assert.That(lc.Accepted, Is.Zero);
            });

        }

        #endregion

        #region AfterARestart_ItConnectsFromThePeers_WithoutASynchronizedClock()

        /// <summary>
        /// The station pins its Local Controller at commissioning and survives a
        /// restart without a synchronized clock: made again from its directory,
        /// with its clock decades from the validity of a certificate that expired
        /// long ago, it connects again by the key pin in peers.json.
        /// </summary>
        [Test]
        public async Task AfterARestart_ItConnectsFromThePeers_WithoutASynchronizedClock()
        {

            // Valid for one year, 2001: expired for this machine, not yet valid
            // for a station whose clock stands in 1996.
            lc!.Certificate = Carrier(lcKey, new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero),
                                             new DateTimeOffset(2001, 12, 31, 0, 0, 0, TimeSpan.Zero));

            var id = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();

            await Until(() => IsConnected(id), "Connecting at commissioning", () => SaidOf(id));
            await Until(() => lc.Requests("BootNotification").Length == 1, "The first BootNotification");

            await station!.DisposeAsync();

            var clock    = new ShiftedClock(new DateTimeOffset(1996, 6, 1, 0, 0, 0, TimeSpan.Zero) - DateTimeOffset.UtcNow);

            var again    = TestStations.New(directory, TestStations.Offline, Clock: clock);
            station      = again;

            Assert.That(again.Connections.Connections.Single().ServerKeyPins, Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }),
                        "the key pin was not read back from peers.json");

            var attempt  = 0;

            station      = await TestPorts.StartedOnFreshPorts(() => attempt++ == 0 ? again : TestStations.New(directory, TestStations.Offline, Clock: clock));

            await Until(() => IsConnected(id), "Connecting again after the restart", () => SaidOf(id));
            await Until(() => lc.Requests("BootNotification").Length == 2, "The BootNotification after the restart");

            Assert.That(station.TimeProvider.GetUtcNow().Year, Is.EqualTo(1996), "the station's clock was set meanwhile");

        }

        #endregion

        #region AKeyPinIsLearnedOnFirstUseOnlyWhereConfigured_AndIsOnProbation()

        /// <summary>
        /// Where trust on first use is configured and nothing pinned, the key pin
        /// of the first key shown is learned, written down, reported, and on
        /// probation: nothing the server asks is executed but a pin set.
        /// </summary>
        [Test]
        public async Task AKeyPinIsLearnedOnFirstUseOnlyWhereConfigured_AndIsOnProbation()
        {

            var id = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPinLearned,          Is.EqualTo(KeyPin.FromKey(lcKey)));
                Assert.That(Entry.ServerKeyPins,                Is.Empty, "a learned key pin was taken for a pin, without a signed pin set");
                Assert.That(Entry.ServerKeyPinState,            Is.EqualTo(KeyPinState.Learned));
            });

            await Until(() => Reported("KeyPinLearned").Length > 0, "Reporting the key pin learned");

            Assert.That(Reported("KeyPinLearned")[0].Value<String>("techInfo"), Does.Contain(KeyPin.FromKey(lcKey).ToHex()).And.Contain(id));

            // On probation: what the server asks is not executed, but a pin set of
            // the TLSClientController instance of this connection is let through.
            var reset   = await lc!.Ask("Reset", new JObject(new JProperty("type", "Immediate")));
            var pinSet  = await lc.Ask("SetVariables", new JObject(new JProperty("setVariableData", new JArray(
                                           new JObject(new JProperty("component",      new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", id))),
                                                       new JProperty("variable",       new JObject(new JProperty("name", "ServerKeyPins"))),
                                                       new JProperty("attributeValue", KeyPin.FromKey(lcKey).ToHex())),
                                           new JObject(new JProperty("component",      new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", id))),
                                                       new JProperty("variable",       new JObject(new JProperty("name", "ServerKeyPinsVersion"))),
                                                       new JProperty("attributeValue", "1"))))));

            Assert.Multiple(() => {
                Assert.That(reset[0].Value<Int32>(),   Is.EqualTo(4), $"a request was executed on probation: {reset}");
                Assert.That(reset[2].Value<String>(),  Is.EqualTo("SecurityError"));
                Assert.That(reset[3].Value<String>(),  Does.Contain("on probation").And.Contain(KeyPin.FromKey(lcKey).ToHex()));
                Assert.That(pinSet[0].Value<Int32>() == 4 && pinSet[2].Value<String>() == "SecurityError", Is.False,
                            $"a pin set was kept from the station on probation: {pinSet}");
            });

        }

        /// <summary>
        /// Where it is not configured, nothing is learned: the connection is
        /// checked by its CA, which vouches for no carrier certificate.
        /// </summary>
        [Test]
        public async Task WithoutTrustOnFirstUse_NothingIsLearned_AndTheCAIsAsked()
        {

            var id = Added(null);

            await Start();

            await Until(() => station!.ConnectionStates.TryGetValue(id, out var state) && state.Status != ConnectionStatus.Connected,
                        "Being turned away by the CA check", () => SaidOf(id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),                Is.False, "a certificate no CA vouches for was believed");
                Assert.That(Entry.ServerKeyPinLearned,      Is.Null,  "a key pin was learned without trust on first use");
                Assert.That(station!.KeyPinTrustOf(id),     Is.Null,  "a connection without key pins was dialled key-pinned");
                Assert.That(lc!.Accepted,                   Is.Zero);
            });

        }

        #endregion

        #region AProbationThatRunsOut_ForgetsTheKeyPin_AndLearnsAgain()

        /// <summary>
        /// Where no signed pin set confirms a learned key pin within the
        /// probation, it is forgotten and reported, the connection is dropped and
        /// made anew, and the key pin of the next first handshake is learned.
        /// </summary>
        [Test]
        public async Task AProbationThatRunsOut_ForgetsTheKeyPin_AndLearnsAgain()
        {

            var id = Added(null, new ServerKeyPinSettings(null, "Key", 2));

            await Start();

            await Until(() => Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var learnedAt = Entry.ServerKeyPinLearnedAt;

            await Until(() => Entry.ServerKeyPinLearnedAt is DateTimeOffset at && at > learnedAt!.Value,
                        "Learning it again after the probation ran out", () => SaidOf(id));

            await Until(() => lc!.Accepted >= 2, "The connection made anew");

            await Until(() => Reported("KeyPinProbationExpired").Length > 0, "Reporting the probation run out");

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPinLearned,  Is.EqualTo(KeyPin.FromKey(lcKey)));
                Assert.That(Reported("KeyPinProbationExpired")[0].Value<String>("techInfo"), Does.Contain(KeyPin.FromKey(lcKey).ToHex()));
            });

        }

        #endregion

        #region ASignedPinSet_ConfirmsTheLearnedKeyPin_AndEndsTheProbation()

        /// <summary>
        /// A signed pin set with the learned key pin confirms it: the probation
        /// ends, what the server asks is executed again, and a replay of it is
        /// refused.
        /// </summary>
        [Test]
        public async Task ASignedPinSet_ConfirmsTheLearnedKeyPin_AndEndsTheProbation()
        {

            var id = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            Assert.That(station!.TryTakeSignedServerKeyPins(id, KeyPin.FromKey(lcKey).ToHex(), 1, "The CSMS", out var error), Is.True, error);

            var heartbeat = await lc!.Ask("TriggerMessage", new JObject(new JProperty("requestedMessage", "Heartbeat")));

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPinState,     Is.EqualTo(KeyPinState.Confirmed));
                Assert.That(Entry.ServerKeyPins,         Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }));
                Assert.That(Entry.ServerKeyPinsVersion,  Is.EqualTo(1));
                Assert.That(heartbeat[0].Value<Int32>(), Is.EqualTo(3), $"a request was refused after the probation ended: {heartbeat}");
                Assert.That(IsConnected(id),             Is.True, "a confirmed connection was dropped");
                Assert.That(station.TryTakeSignedServerKeyPins(id, KeyPin.FromKey(lcKey).ToHex(), 1, "The CSMS", out _), Is.False, "a replay was taken");
            });

            await Until(() => Reported("KeyPinConfirmed").Length > 0, "Reporting the key pin confirmed");

        }

        #endregion

        #region ASignedPinSetWithoutTheLearnedKeyPin_ReplacesIt_AndTheImpostorIsRefused()

        /// <summary>
        /// A signed pin set without the learned key pin replaces it: reported,
        /// the connection dropped, and the next one held to the signed pins - the
        /// server that answered first is refused, and the Local Controller the
        /// CSMS knows is believed once it answers.
        /// </summary>
        [Test]
        public async Task ASignedPinSetWithoutTheLearnedKeyPin_ReplacesIt_AndTheImpostorIsRefused()
        {

            // The impostor answers first.
            lc!.Certificate = Carrier(otherKey);

            var id = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the impostor's key pin", () => SaidOf(id));

            Assert.That(station!.TryTakeSignedServerKeyPins(id, KeyPin.FromKey(lcKey).ToHex(), 1, "The CSMS", out var error), Is.True, error);

            await Until(() => Reported("KeyPinRejected").Length > 0, "Reporting the key pin rejected");

            await Until(() => Reported("InvalidCsmsCertificate").Length > 0 && !IsConnected(id),
                        "Refusing the impostor at the next handshake", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPins,         Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }));
                Assert.That(Entry.ServerKeyPinLearned,   Is.Null);
                Assert.That(lc.Accepted,                 Is.EqualTo(1), "the impostor was believed again");
            });

            // The real one answers.
            lc.Certificate = Carrier(lcKey);

            await Until(() => IsConnected(id) && lc.Accepted == 2, "Believing the Local Controller the CSMS knows", () => SaidOf(id));

        }

        #endregion

        #region ALearnedKeyPinThatCouldNotBeWrittenDown_IsJudgedByASignedPinSetAllTheSame()

        /// <summary>
        /// A key pin learned while peers.json could not be written is held only in
        /// memory, on probation all the same. A signed pin set without it still
        /// rejects it: the connection is dropped, not let off the gate.
        /// </summary>
        [Test]
        public async Task ALearnedKeyPinThatCouldNotBeWrittenDown_IsJudgedByASignedPinSetAllTheSame()
        {

            // The impostor answers first, and nothing can be written while it is learned.
            lc!.Certificate = Carrier(otherKey);

            var id      = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var blocker = Path.Combine(station!.Connections.Path, ConnectionStore.PeersFileName + ".tmp");

            Directory.CreateDirectory(blocker);

            try
            {

                await Start();

                await Until(() => IsConnected(id) && station!.KeyPinTrustOf(id)?.Learned is not null,
                            "Learning the impostor's key pin without writing it down", () => SaidOf(id));

                Assert.That(Entry.ServerKeyPinLearned, Is.Null, "the learned key pin was written down after all");

            }
            finally
            {
                Directory.Delete(blocker);
            }

            Assert.That(station!.TryTakeSignedServerKeyPins(id, KeyPin.FromKey(lcKey).ToHex(), 1, "The CSMS", out var error), Is.True, error);

            await Until(() => Reported("KeyPinRejected").Length > 0, "Reporting the key pin rejected");

            await Until(() => !IsConnected(id) || lc.Accepted > 1, "Dropping the connection to the impostor", () => SaidOf(id));

            Assert.That(lc.Accepted, Is.EqualTo(1), "the impostor was believed again");

        }

        #endregion

        #region RawPublicKeysRequired_IsNotDialled()

        [Test]
        public async Task RawPublicKeysRequired_IsNotDialled()
        {

            var id = Added(KeyPin.FromKey(lcKey).ToHex(), new ServerKeyPinSettings("Require", null, null));

            await Start();

            Assert.Multiple(() => {
                Assert.That(station!.ConnectionStates[id].Status,  Is.EqualTo(ConnectionStatus.NotDialled), SaidOf(id));
                Assert.That(SaidOf(id),                            Does.Contain("requires raw public keys"));
                Assert.That(Reported("InvalidCsmsCertificate"),    Is.Empty, "the server's certificate was reported for what this station cannot do");
            });

        }

        #endregion

        #region ATestSaysWhichKeyPinItSaw_AndWritesNothing()

        /// <summary>
        /// A test says which key pin the server showed - believed, refused, or
        /// only looked at where the CA decides - and writes nothing down.
        /// </summary>
        [Test]
        public async Task ATestSaysWhichKeyPinItSaw_AndWritesNothing()
        {

            var shown     = KeyPin.FromKey(lcKey).ToHex();

            var learning  = await station!.TestConnection("The local controller", URL, "LocalController",
                                                          KeyPinTrust: new ServerKeyPinSettings(null, "Key", null));

            var refused   = await station.TestConnection("The local controller", URL, "LocalController",
                                                         ServerKeyPins: KeyPin.FromKey(otherKey).ToHex());

            var byTheCA   = await station.TestConnection("The local controller", URL, "LocalController");

            String Steps(JObject Result)
                => String.Join("\n", Result["steps"]!.Select(step => step.Value<String>("text")));

            Assert.Multiple(() => {

                Assert.That(learning.Value<Boolean>("ok"),           Is.True,  Steps(learning));
                Assert.That(learning.Value<String>("serverKeyPin"),  Is.EqualTo(shown));
                Assert.That(Steps(learning),                         Does.Contain($"key pin is {shown}"));

                Assert.That(refused.Value<Boolean>("ok"),            Is.False, Steps(refused));
                Assert.That(refused.Value<String>("serverKeyPin"),   Is.EqualTo(shown));
                Assert.That(Steps(refused),                          Does.Contain("Not believed").And.Contain(shown));

                Assert.That(byTheCA.Value<Boolean>("ok"),            Is.False, $"a certificate no CA vouches for was believed: {Steps(byTheCA)}");
                Assert.That(byTheCA.Value<String>("serverKeyPin"),   Is.EqualTo(shown));

                Assert.That(station.Connections.Connections,          Is.Empty, "a test wrote something down");
                Assert.That(File.Exists(Path.Combine(station.Connections.Path, ConnectionStore.PeersFileName)), Is.False, "a test wrote peers.json");
                Assert.That(Reported("KeyPinLearned"),                Is.Empty, "a test reported a key pin learned");
                Assert.That(Reported("InvalidCsmsCertificate"),       Is.Empty, "a test reported a key refused");

            });

        }

        #endregion

        #region TheWebInterface_TakesKeyPinsAsAList_AndATestSaysWhichItSaw()

        /// <summary>
        /// The web interface takes the key pins of a connection as a list, in any
        /// notation, gives them back as 64 lowercase hexadecimal digits, refuses
        /// them on a ws:// URL in words, and a test over it says which key pin
        /// the server showed.
        /// </summary>
        [Test]
        public async Task TheWebInterface_TakesKeyPinsAsAList_AndATestSaysWhichItSaw()
        {

            await Start();

            using var http = new HttpClient(new HttpClientHandler { CookieContainer = new System.Net.CookieContainer(), UseCookies = true }) {
                                 BaseAddress = new Uri(station!.WebInterfaceURL.ToString())
                             };

            var signedIn = await http.PostAsync($"{ChargingStation.ExtAPIPath.ToString().TrimEnd('/')}/login",
                                                new FormUrlEncodedContent([
                                                    new KeyValuePair<String, String>("login",     ChargingStation.DefaultAdminUser),
                                                    new KeyValuePair<String, String>("password",  station.GeneratedPassword!)
                                                ]));

            Assert.That(signedIn.IsSuccessStatusCode, Is.True, $"Signing in failed with {(Int32) signedIn.StatusCode}.");

            static StringContent Body(JObject JSON)
                => new (JSON.ToString(), System.Text.Encoding.UTF8, "application/json");

            JObject Connection(String URL, JArray Pins)
                => new (new JProperty("description",                "The local controller"),
                        new JProperty("url",                        URL),
                        new JProperty("connectionType",             "LocalController"),
                        new JProperty("autoConnect",                false),
                        new JProperty("serverKeyPins",              Pins),
                        new JProperty("trustOnFirstUse",            "None"),
                        new JProperty("rawPublicKeys",              "Off"),
                        new JProperty("serverKeyPinProbationTime",  600));

            var pin       = KeyPin.FromKey(lcKey);
            var added     = await http.PostAsync("api/v1/configuration/connections",
                                                 Body(Connection(URL, new JArray(pin.ToHexWithColons(), KeyPin.FromKey(otherKey).ToCurl()))));
            var addedJSON = JObject.Parse(await added.Content.ReadAsStringAsync());

            var refused   = await http.PostAsync("api/v1/configuration/connections",
                                                 Body(Connection($"ws://127.0.0.1:{lc!.Port}/cs001", new JArray(pin.ToHex()))));

            var refusal   = await refused.Content.ReadAsStringAsync();

            var tested    = JObject.Parse(await (await http.PostAsync("api/v1/configuration/connections/test",
                                                                      Body(Connection(URL, new JArray(pin.ToHex()))))).Content.ReadAsStringAsync());

            Assert.Multiple(() => {

                Assert.That(added.IsSuccessStatusCode, Is.True, addedJSON.ToString());
                Assert.That(addedJSON["connections"]?["connections"]?[0]?["serverKeyPins"]?.Values<String>(),
                            Is.EqualTo(new[] { pin.ToHex(), KeyPin.FromKey(otherKey).ToHex() }));

                Assert.That((Int32) refused.StatusCode, Is.EqualTo(400));
                Assert.That(refusal,                    Does.Contain("makes no TLS handshake"));

                Assert.That(tested.Value<Boolean>("ok"),           Is.True, tested.ToString());
                Assert.That(tested.Value<String>("serverKeyPin"),  Is.EqualTo(pin.ToHex()));

                Assert.That(station.Connections.Connections.Count, Is.EqualTo(1), "the test or the refused one was written down");

            });

        }

        #endregion

    }

}
