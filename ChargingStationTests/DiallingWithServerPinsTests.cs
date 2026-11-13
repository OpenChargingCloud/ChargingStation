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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A charging station that dials its CSMS over wss:// and checks it by its
    /// CA, as it checks a time server, a name server and a meter: a root of
    /// its own store vouches for a CSMS the machine knows no CA of; the server
    /// is held to its certificate or to its root, by fingerprint or by the pin
    /// of the key, to a group of roots, and to what it is first believed with;
    /// a signed pin set holds it to the roots of its CSMS; and none of it goes
    /// beside key pins.
    /// </summary>
    /// <remarks>
    /// A Hermod WebSocket server stands in for the CSMS, with a certificate
    /// issued for the address the station dials by a CA made in memory, and the
    /// least of a CSMS behind it.
    /// </remarks>
    [TestFixture]
    public class DiallingWithServerPinsTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind, over wss://, whose certificate can be
        /// changed between two handshakes.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>  received  = new ();
            private          Int32                    accepted;

            public WebSocketServer   Server       { get; }
            public IPPort            Port         { get; }

            /// <summary>
            /// The certificate it shows at the next handshake.
            /// </summary>
            public X509Certificate2  Certificate  { get; set; }

            public CSMS(X509Certificate2 Certificate)
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

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             csms;

        private X509Certificate2  ca         = default!;
        private X509Certificate2  otherCA    = default!;
        private ECDsa             csmsKey    = default!;
        private X509Certificate2  leaf       = default!;

        private readonly List<X509Certificate2> certificates = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStationAndACSMS()
        {

            directory  = TestStations.TemporaryDirectory("dialling-serverpins");
            station    = TestStations.New(directory, TestStations.Offline);

            ca         = CA("The operator's CA");
            otherCA    = CA("Somebody else's CA");
            csmsKey    = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            leaf       = Leaf(ca, csmsKey);

            csms       = new CSMS(leaf);

        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            foreach (var certificate in certificates)
                certificate.Dispose();

            certificates.Clear();

            ca.     Dispose();
            otherCA.Dispose();
            leaf.   Dispose();
            csmsKey.Dispose();

            station  = null;
            csms     = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        /// <summary>
        /// A CA made in memory, which no machine knows.
        /// </summary>
        private X509Certificate2 CA(String Name)
        {

            var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            certificates.Add(certificate);

            return certificate;

        }

        /// <summary>
        /// A server certificate for 127.0.0.1, which is what the station dials,
        /// issued by the given CA for the given key - and one SslStream can
        /// serve on Windows.
        /// </summary>
        private X509Certificate2 Leaf(X509Certificate2 Issuer, ECDsa Key)
        {

            var request = new CertificateRequest("CN=127.0.0.1", Key, HashAlgorithmName.SHA256);
            var names   = new SubjectAlternativeNameBuilder();

            names.AddIpAddress(System.Net.IPAddress.Loopback);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            request.CertificateExtensions.Add(names.Build());

            using var issued  = request.Create(Issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), Guid.NewGuid().ToByteArray());
            using var withKey = issued.CopyWithPrivateKey(Key);

            var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            certificates.Add(certificate);

            return certificate;

        }

        private static Byte[] Pem(X509Certificate2 Certificate)
            => System.Text.Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem());

        private static String Fingerprint(X509Certificate2 Certificate)
            => CertificateEntry.ThumbprintOf(Certificate);

        private static String KeyPinOf(X509Certificate2 Certificate)
            => ServerPins.KeyPinOf(Certificate);

        private String URL
            => $"wss://127.0.0.1:{csms!.Port}/cs001";

        private async Task Start()
            => station = await TestStations.Started(station!, directory);

        /// <summary>
        /// Keep the given CA as a TLS root of the station's own store: for
        /// every use, or for the given ones.
        /// </summary>
        private CertificateEntry Kept(X509Certificate2 Root, params String[] Usages)
        {
            Assert.That(station!.Certificates.Import(Pem(Root), CertificateKind.TLSRoot, null, Root.GetNameInfo(X509NameType.SimpleName, false),
                                                     Usages.Length > 0 ? Usages : null, out var entry, out var error),
                        Is.True, error);
            return entry!;
        }

        private String Added(ServerPins? Pins = null)
        {
            Assert.That(station!.Connections.TryAddConnection("The CSMS", URL, "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              Pins: Pins is not null ? new ServerPinsChange(Pins) : null),
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

        /// <summary>
        /// Dialled, turned away, and still not connected a second later.
        /// </summary>
        private async Task TurnedAway(String Id, String Why)
        {

            await Until(() => station!.ConnectionStates.TryGetValue(Id, out var state) && state.Status != ConnectionStatus.Connected &&
                              station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1") is { Accepted: false },
                        "Being turned away", () => SaidOf(Id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(Id),   Is.False,  Why);
                Assert.That(csms!.Accepted,    Is.Zero,   Why);
            });

        }

        private ConnectionEntry Entry
            => station!.Connections.Connections.Single();

        private String JudgedAs
            => String.Join(" | ", station!.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1")?.Steps.Select(step => step.Text) ?? [ "not judged" ]);

        #endregion


        #region ARootOfTheStationsOwnVouchesForACSMSTheMachineKnowsNoCAOf()

        /// <summary>
        /// A CSMS whose CA the machine does not know is refused - and believed
        /// once that CA is a TLS root of the station's own store, without
        /// anything installed on the machine.
        /// </summary>
        [Test]
        public async Task ARootOfTheStationsOwnVouchesForACSMSTheMachineKnowsNoCAOf()
        {

            Kept(ca);

            var id = Added();

            await Start();

            await Until(() => IsConnected(id),                                 "Connecting through a root of the station's own", () => $"{SaidOf(id)} / {JudgedAs}");
            await Until(() => csms!.Requests("BootNotification").Length > 0,  "A BootNotification over wss://");

            var judgement = station!.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1");

            Assert.Multiple(() => {
                Assert.That(judgement?.Accepted,           Is.True);
                Assert.That(judgement?.AnchoredBy,         Is.EqualTo("The operator's CA"));
                Assert.That(judgement?.RootFingerprint,    Is.EqualTo(Fingerprint(ca)));
                Assert.That(judgement?.CertificateKeyPin,  Is.EqualTo(KeyPinOf(leaf)));
                Assert.That(station.KeyPinTrustOf(id),     Is.Null,  "a connection checked by its CA was dialled key-pinned");
            });

        }

        #endregion

        #region ACSMSWhoseRootNobodyKnowsIsRefused()

        /// <summary>
        /// Without that root in the store, and with a root kept for the meters
        /// alone, the same CSMS is refused: no WebSocket connection is made.
        /// </summary>
        [Test]
        public async Task ACSMSWhoseRootNobodyKnowsIsRefused()
        {

            Kept(ca, ChargingStation.MeterCertificateUsage);

            var id = Added();

            await Start();

            await TurnedAway(id, "a CA kept for the meters alone vouched for the CSMS");

            Assert.That(JudgedAs, Does.Contain("its chain ends at no root this machine or this"));

        }

        #endregion

        #region HeldToTheKeyOfItsRoot_ItIsBelievedThroughThatRootAlone()

        /// <summary>
        /// Held to the key of the root of its CSMS, the station connects - and
        /// held to the key of another root, it does not, although the chain
        /// ends at a root the station trusts: a pin narrows what the CA check
        /// believes.
        /// </summary>
        [Test]
        public async Task HeldToTheKeyOfItsRoot_ItIsBelievedThroughThatRootAlone()
        {

            Kept(ca);
            Kept(otherCA);

            var id = Added(new ServerPins([], [ KeyPinOf(ca) ]));

            await Start();

            await Until(() => IsConnected(id), "Connecting, held to the key of its root", () => $"{SaidOf(id)} / {JudgedAs}");

            Assert.That(JudgedAs, Does.Contain($"Held to root {KeyPinOf(ca)}: its chain ends there."));

            // Held to another root by somebody's hand: dialled again, and refused.
            var accepted = csms!.Accepted;

            Assert.That(station!.Connections.TryUpdateConnection(id, "The CSMS", URL, "CSMS", true, null, null, out var error,
                                                                 Pins: new ServerPinsChange(new ServerPins([], [ KeyPinOf(otherCA) ]))),
                        Is.True, error);

            await Until(() => station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1") is { Outcome: "pinMismatch" },
                        "Being refused, held to another root", () => $"{SaidOf(id)} / {JudgedAs}");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),   Is.False,  "a CSMS whose chain ends at another root than the one it is held to was believed");
                Assert.That(csms.Accepted,     Is.EqualTo(accepted));
                Assert.That(JudgedAs,          Does.Contain($"Held to root {KeyPinOf(otherCA)}, and its chain ends at {Fingerprint(ca)}, with the key {KeyPinOf(ca)}."));
            });

        }

        #endregion

        #region HeldToTheKeyOfItsCertificate_ARenewalIsBelieved_AndHeldToItsFingerprint_ItIsNot()

        /// <summary>
        /// Held to the key of the certificate of its CSMS, the station believes
        /// a renewal of that key; held to the fingerprint of the certificate it
        /// had, it does not.
        /// </summary>
        [Test]
        public async Task HeldToTheKeyOfItsCertificate_ARenewalIsBelieved_AndHeldToItsFingerprint_ItIsNot()
        {

            Kept(ca);

            var renewed       = Leaf(ca, csmsKey);

            csms!.Certificate = renewed;

            var id = Added(new ServerPins([ KeyPinOf(leaf) ], []));

            await Start();

            await Until(() => IsConnected(id), "Connecting to a renewal, held to the key", () => $"{SaidOf(id)} / {JudgedAs}");

            var accepted = csms.Accepted;

            Assert.That(station!.Connections.TryUpdateConnection(id, "The CSMS", URL, "CSMS", true, null, null, out var error,
                                                                 Pins: new ServerPinsChange(new ServerPins([ Fingerprint(leaf) ], []))),
                        Is.True, error);

            await Until(() => station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1") is { Outcome: "pinMismatch" },
                        "Being refused, held to the fingerprint of the certificate it had", () => $"{SaidOf(id)} / {JudgedAs}");

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),   Is.False);
                Assert.That(csms.Accepted,     Is.EqualTo(accepted));
            });

        }

        #endregion

        #region HeldToAGroupOfRoots_OnlyARootMarkedForItVouches()

        /// <summary>
        /// Held to a group of roots, the CSMS is believed through a root marked
        /// "ocppca-&lt;group&gt;" in the store and through no other - not through
        /// one kept for every use.
        /// </summary>
        [Test]
        public async Task HeldToAGroupOfRoots_OnlyARootMarkedForItVouches()
        {

            var root = Kept(ca);
            var id   = Added(new ServerPins([], [], RootGroup: "backend"));

            await Start();

            await TurnedAway(id, "a root not marked for the group vouched for a CSMS held to it");

            Assert.That(JudgedAs, Does.Contain("held to the roots of group backend").And.Contain("'ocppca-backend'"));

            Assert.That(station!.Certificates.SetUsages(root.Id, [ ChargingStation.OCPPCertificateUsage, ServerPins.MarkOf(ChargingStation.OCPPCertificateUsage, "backend") ], out _, out var error),
                        Is.True, error);

            await Until(() => IsConnected(id), "Connecting through a root of its group", () => $"{SaidOf(id)} / {JudgedAs}");

        }

        #endregion

        #region ItsRootIsLearnedOnFirstUse_WrittenDown_AndHeldToFromThenOn()

        /// <summary>
        /// Where the connection says so, the root its CSMS is first believed
        /// with is written into its entry, and the server is held to it from
        /// then on - as for a time server.
        /// </summary>
        [Test]
        public async Task ItsRootIsLearnedOnFirstUse_WrittenDown_AndHeldToFromThenOn()
        {

            Kept(ca);

            var id = Added(new ServerPins([], [], TrustOnFirstUse: TrustOnFirstUse.Root));

            await Start();

            await Until(() => IsConnected(id),                 "Connecting",            () => $"{SaidOf(id)} / {JudgedAs}");
            await Until(() => Entry.Pins?.Roots.Count == 1,    "Learning its root");

            var file = JObject.Parse(File.ReadAllText(Path.Combine(station!.Connections.Path, ConnectionStore.PeersFileName))).ToString(Formatting.None);

            Assert.Multiple(() => {
                Assert.That(Entry.Pins!.Roots,                  Is.EqualTo(new[] { Fingerprint(ca) }));
                Assert.That(file,                               Does.Contain($"\"rootFingerprint\":\"{Fingerprint(ca)}\""));
                Assert.That(file,                               Does.Contain("\"trustOnFirstUse\":\"root\""));
                Assert.That(Entry.ServerKeyPinLearned,          Is.Null,  "a key pin was learned by a connection checked by its CA");
                Assert.That(station.ConnectionStates[id].Status, Is.EqualTo(ConnectionStatus.Connected),  "learning its root dropped the connection");
            });

        }

        #endregion

        #region OneWayOrTheOther_NeverKeyPinsAndPins()

        /// <summary>
        /// A connection is checked by its CA or known by the key of its server:
        /// key pins, or learning a key, beside a certificate, a root or a group
        /// of roots is refused in words - written down, changed, and tested -
        /// and so is any of it on a ws:// connection.
        /// </summary>
        [Test]
        public async Task OneWayOrTheOther_NeverKeyPinsAndPins()
        {

            var keyPin  = KeyPin.FromKey(csmsKey).ToHex();
            var pins    = new ServerPinsChange(new ServerPins([], [ KeyPinOf(ca) ]));

            var both     = station!.Connections.TryAddConnection("The CSMS", URL, "CSMS", false, null, null, out _, out var bothError,
                                                                 ServerKeyPins: keyPin, Pins: pins);

            var learning = station.Connections.TryAddConnection("The CSMS", URL, "CSMS", false, null, null, out _, out var learningError,
                                                                KeyPinTrust: new ServerKeyPinSettings(null, "Key", null),
                                                                Pins: new ServerPinsChange(new ServerPins([], [], RootGroup: "backend")));

            var plain    = station.Connections.TryAddConnection("The CSMS", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", false, null, null, out _, out var plainError,
                                                                Pins: pins);

            Assert.That(station.Connections.TryAddConnection("The CSMS", URL, "CSMS", false, null, null, out var id, out var error, ServerKeyPins: keyPin),
                        Is.True, error);

            var changed  = station.Connections.TryUpdateConnection(id, "The CSMS", URL, "CSMS", false, null, null, out var changedError, Pins: pins);

            var tested   = await station.TestConnection("The CSMS", URL, "CSMS", ServerKeyPins: keyPin,
                                                        Pins: new JObject(new JProperty("rootFingerprint", KeyPinOf(ca))));

            Assert.Multiple(() => {

                Assert.That(both,           Is.False);
                Assert.That(bothError,      Does.Contain("checked by its CA or known by the key of its server, not both"));

                Assert.That(learning,       Is.False);
                Assert.That(learningError,  Does.Contain("not both"));

                Assert.That(plain,          Is.False);
                Assert.That(plainError,     Does.Contain("makes no TLS handshake"));

                Assert.That(changed,        Is.False);
                Assert.That(changedError,   Does.Contain("not both"));

                Assert.That(tested.Value<Boolean>("ok"),  Is.False);
                Assert.That(tested["steps"]!.Select(step => step.Value<String>("text")),  Has.Some.Contains("not both"));

                Assert.That(station.Connections.Connections.Single().Pins,  Is.Null);

            });

        }

        #endregion

        #region ASignedPinSet_HoldsItToTheRootsOfItsCSMS_AndOnlyAHigherVersionIsTaken()

        /// <summary>
        /// A signed pin set holds the connection to the roots it names, by the
        /// pins of their keys: with another root than the one its CSMS chains
        /// to, the connection is dialled again and refused; an older pin set is
        /// not taken; a higher one with the right root brings the connection
        /// back; and a connection known by the key of its server takes none.
        /// </summary>
        [Test]
        public async Task ASignedPinSet_HoldsItToTheRootsOfItsCSMS_AndOnlyAHigherVersionIsTaken()
        {

            Kept(ca);
            Kept(otherCA);

            var id = Added();

            await Start();

            await Until(() => IsConnected(id), "Connecting", () => $"{SaidOf(id)} / {JudgedAs}");

            var other  = KeyPin.FromCertificate(otherCA);
            var right  = KeyPin.FromCertificate(ca);

            Assert.That(station!.TryTakeSignedServerCAKeyPins(id, other.ToHexWithColons(), 2, "the CSMS", out var error), Is.True, error);

            await Until(() => station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1") is { Outcome: "pinMismatch" },
                        "Being refused, held to the roots of the signed pin set", () => $"{SaidOf(id)} / {JudgedAs}");

            await Until(() => !IsConnected(id), "Letting go of a CSMS whose chain ends elsewhere");

            var older  = station.TryTakeSignedServerCAKeyPins(id, right.ToHex(), 2, "the CSMS", out var olderError);
            var none   = station.TryTakeSignedServerCAKeyPins(id, "",            3, "the CSMS", out var noneError);
            var wrong  = station.TryTakeSignedServerCAKeyPins(id, "abcd",        3, "the CSMS", out var wrongError);

            Assert.Multiple(() => {
                Assert.That(Entry.Pins?.Roots,   Is.EqualTo(new[] { ServerPins.KeyPinPrefix + other.ToHex().ToLowerInvariant() }));
                Assert.That(Entry.PinsVersion,   Is.EqualTo(2));
                Assert.That(older,               Is.False);
                Assert.That(olderError,          Does.Contain("only a higher one is taken"));
                Assert.That(none,                Is.False);
                Assert.That(noneError,           Does.Contain("held to no root"));
                Assert.That(wrong,               Is.False);
                Assert.That(wrongError,          Does.Contain("is not a key pin"));
            });

            Assert.That(station.TryTakeSignedServerCAKeyPins(id, $"{right.ToCurl()},{other.ToHex()}", 3, "the CSMS", out error), Is.True, error);

            await Until(() => IsConnected(id), "Connecting again, held to the right root", () => $"{SaidOf(id)} / {JudgedAs}");

            Assert.That(Entry.PinsVersion, Is.EqualTo(3));

            // One known by the key of its server asks no CA.
            Assert.That(station.Connections.TryAddConnection("By its key", URL, "CSMS", false, null, null, out var keyPinned, out error,
                                                             ServerKeyPins: KeyPin.FromKey(csmsKey).ToHex()),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(station.TryTakeSignedServerCAKeyPins(keyPinned!, right.ToHex(), 1, "the CSMS", out var refused),  Is.False);
                Assert.That(refused,  Does.Contain("asks no CA"));
            });

        }

        #endregion

        #region ATestSaysWhatItMadeOfTheCertificate_AndWritesNothing()

        /// <summary>
        /// A test of a connection checked by its CA says which certificate and
        /// which root it saw, by fingerprint and by key, and what the form
        /// holds the server to came to - and learns and writes nothing.
        /// </summary>
        [Test]
        public async Task ATestSaysWhatItMadeOfTheCertificate_AndWritesNothing()
        {

            Kept(ca);

            String Steps(JObject Result)
                => String.Join("\n", Result["steps"]!.Select(step => step.Value<String>("text")));

            var believed  = await station!.TestConnection("The CSMS", URL, "CSMS",
                                                          Pins: new JObject(new JProperty("rootFingerprint",  KeyPinOf(ca)),
                                                                            new JProperty("trustOnFirstUse",  "certificate")));

            var refused   = await station.TestConnection("The CSMS", URL, "CSMS",
                                                         Pins: new JObject(new JProperty("rootFingerprints", new JArray(KeyPinOf(otherCA)))));

            var wrong     = await station.TestConnection("The CSMS", URL, "CSMS",
                                                         Pins: new JObject(new JProperty("rootFingerprint", "spki:abcd")));

            Assert.Multiple(() => {

                Assert.That(believed.Value<Boolean>("ok"),  Is.True,  Steps(believed));
                Assert.That(Steps(believed),                Does.Contain($"Held to root {KeyPinOf(ca)}: its chain ends there."));
                Assert.That(Steps(believed),                Does.Contain($"its chain ends at the root {Fingerprint(ca)} ({KeyPinOf(ca)})"));
                Assert.That(Steps(believed),                Does.Contain("A test writes nothing down"));
                Assert.That(believed.Value<String>("serverKeyPin"),  Is.EqualTo(KeyPin.FromKey(csmsKey).ToHex()));

                Assert.That(refused.Value<Boolean>("ok"),   Is.False, Steps(refused));
                Assert.That(Steps(refused),                 Does.Contain($"Held to root {KeyPinOf(otherCA)}, and its chain ends at {Fingerprint(ca)}"));

                Assert.That(wrong.Value<Boolean>("ok"),     Is.False);
                Assert.That(Steps(wrong),                   Does.Contain("and not the pin of a key either"));

                Assert.That(station.Connections.Connections,  Is.Empty,  "a test wrote something down");
                Assert.That(station.LastJudgementOf(ChargingStation.OCPPCertificateUsage, "127.0.0.1"),  Is.Null,  "a test was kept as the judgement of a connection");

            });

        }

        #endregion

        #region TheWebInterface_TakesWhatAServerIsHeldTo_AndKeepsWhatWasLearnedMeanwhile()

        /// <summary>
        /// The web interface takes what the server of a connection is held to
        /// under "pins", in the words every server of the station is held to
        /// something with, gives it back as it is kept, and refuses what is
        /// none in the same sentence; and a save that says what the page showed
        /// keeps a root learned since.
        /// </summary>
        [Test]
        public async Task TheWebInterface_TakesWhatAServerIsHeldTo_AndKeepsWhatWasLearnedMeanwhile()
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

            JObject Connection(JObject? Pins, JObject? AsShown = null, String? Id = null)
            {

                var json = new JObject(new JProperty("description",     "The CSMS"),
                                       new JProperty("url",             URL),
                                       new JProperty("connectionType",  "CSMS"),
                                       new JProperty("autoConnect",     false));

                if (Id      is not null)  json.Add(new JProperty("id",           Id));
                if (Pins    is not null)  json.Add(new JProperty("pins",         Pins));
                if (AsShown is not null)  json.Add(new JProperty("pinsAsShown",  AsShown));

                return json;

            }

            var curl      = KeyPin.FromCertificate(ca).ToCurl();

            var added     = await http.PostAsync("api/v1/configuration/connections",
                                                 Body(Connection(new JObject(new JProperty("certificateFingerprints",  new JArray(KeyPinOf(leaf), Fingerprint(leaf).ToUpperInvariant())),
                                                                             new JProperty("onMismatch",               "record"),
                                                                             new JProperty("trustOnFirstUse",          "root")))));
            var addedJSON = JObject.Parse(await added.Content.ReadAsStringAsync());
            var id        = addedJSON.Value<String>("id")!;

            var refused   = await http.PostAsync("api/v1/configuration/connections",
                                                 Body(Connection(new JObject(new JProperty("rootFingerprint", "spki:abcd")))));
            var refusal   = await refused.Content.ReadAsStringAsync();

            // Learned while the page is open ...
            Assert.That(station.Connections.TryLearnPin(id, org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(URL), TrustOnFirstUse.Root, Fingerprint(ca)), Is.True);

            // ... which sends back what it showed, with the mismatch changed.
            var shown     = new JObject(new JProperty("certificateFingerprints",  new JArray(KeyPinOf(leaf), Fingerprint(leaf))),
                                        new JProperty("onMismatch",               "record"),
                                        new JProperty("trustOnFirstUse",          "root"));

            var sent      = new JObject(new JProperty("certificateFingerprints",  new JArray(KeyPinOf(leaf), Fingerprint(leaf))),
                                        new JProperty("rootFingerprint",          curl),
                                        new JProperty("onMismatch",               "refuse"),
                                        new JProperty("trustOnFirstUse",          "root"));

            var updated   = await http.PostAsync("api/v1/configuration/connections/update", Body(Connection(sent, shown, id)));
            var updatedJSON = await updated.Content.ReadAsStringAsync();

            Assert.Multiple(() => {

                Assert.That(added.IsSuccessStatusCode, Is.True, addedJSON.ToString());
                Assert.That(addedJSON["connections"]?["connections"]?[0]?["pins"]?.ToString(Formatting.None),
                            Is.EqualTo($$"""{"certificateFingerprints":["{{KeyPinOf(leaf)}}","{{Fingerprint(leaf)}}"],"onMismatch":"record","trustOnFirstUse":"root"}"""));

                Assert.That((Int32) refused.StatusCode, Is.EqualTo(400));
                Assert.That(refusal,                    Does.Contain("'pins.rootFingerprint' holds 'spki:abcd'").And.Contain("and not the pin of a key either"));

                Assert.That(updated.IsSuccessStatusCode, Is.True, updatedJSON);
                Assert.That(Entry.Pins?.Roots,           Is.EqualTo(new[] { Fingerprint(ca), KeyPinOf(ca) }),  "the root learned while the page was open was taken away by its save");
                Assert.That(Entry.Pins?.OnMismatch,      Is.EqualTo(PinMismatch.Refuse));
                Assert.That(Entry.Pins?.Certificates,    Has.Count.EqualTo(2));

            });

        }

        #endregion

    }

}
