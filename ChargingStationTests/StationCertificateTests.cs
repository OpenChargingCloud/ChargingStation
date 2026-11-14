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
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A charging station that is brought the certificate it shows its CSMS,
    /// and the roots of that CSMS, over OCPP 2.1: TriggerMessage of
    /// SignChargingStationCertificate makes a key and a SignCertificate,
    /// CertificateSigned brings the certificate and the connection is dialled
    /// again with it - and goes back where its CSMS does not let it in; a
    /// certificate for no key of the station is rejected; the station asks by
    /// itself before its certificate runs out; and InstallCertificate keeps a
    /// CSMSRootCertificate where a handshake asks for it.
    /// </summary>
    /// <remarks>
    /// A Hermod WebSocket server stands in for the CSMS, over wss:// with a
    /// certificate of a CA made in memory, asking every client for a
    /// certificate and writing down which one it was shown.
    /// </remarks>
    [TestFixture]
    public class StationCertificateTests
    {

        #region (class) CSMS

        /// <summary>
        /// A CSMS of the least kind that asks for a client certificate, sends
        /// requests of its own, and may turn a certificate away.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>          received  = new ();
            private readonly ConcurrentQueue<String>          shown     = new ();
            private volatile WebSocketServerConnection?       latest;

            public WebSocketServer   Server                 { get; }
            public IPPort            Port                   { get; }

            /// <summary>
            /// The certificate it shows at the next handshake.
            /// </summary>
            public X509Certificate2  Certificate            { get; set; }

            /// <summary>
            /// The serial number of a client certificate it turns away.
            /// </summary>
            public String?           TurnsAway              { get; set; }

            /// <summary>
            /// What it answers to SignCertificate.
            /// </summary>
            public String            SignCertificateStatus  { get; set; } = "Accepted";

            public CSMS(X509Certificate2 Certificate)
            {

                this.Certificate  = Certificate;
                this.Port         = IPPort.Parse(TestPorts.Free());

                Server = new WebSocketServer(HTTPPort:                    Port,
                                             RequireAuthentication:       false,
                                             ServerCertificateSelector:   (tcpServer, tcpClient) => this.Certificate,
                                             ClientCertificateValidator:  (sender, certificate, chain, tlsServer, policyErrors) => {

                                                                              if (certificate is null)
                                                                                  return TLSValidationResult.Failed("No client certificate was shown.");

                                                                              if (certificate.SerialNumber == TurnsAway)
                                                                                  return TLSValidationResult.Failed("This certificate is not let in.");

                                                                              shown.Enqueue(certificate.SerialNumber);

                                                                              return TLSValidationResult.Success();

                                                                          },
                                             ClientCertificateRequired:   true,
                                             AutoStart:                   true);

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    // The connection the station speaks OCPP on - and not one
                    // of a connection test, which is told to ask nothing here
                    // and goes again.
                    latest = connection;

                    received.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer = new JArray(3, message[1], message[2].Value<String>() switch {
                                     "BootNotification"  => new JObject(
                                                                new JProperty("status",       "Accepted"),
                                                                new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                new JProperty("interval",     300)
                                                            ),
                                     "SignCertificate"   => new JObject(
                                                                new JProperty("status",       SignCertificateStatus)
                                                            ),
                                     _                   => new JObject()
                                 });

                    await Server.SendTextMessage(connection, answer.ToString(Formatting.None));

                };

            }

            /// <summary>
            /// The serial numbers of the client certificates it let in, in the
            /// order of their handshakes.
            /// </summary>
            public String[] Shown
                => [.. shown];

            /// <summary>
            /// The requests of the given action it was sent so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == Action)];

            /// <summary>
            /// Send a request over the connection the station last said something
            /// on, and wait for what it answers.
            /// </summary>
            public async Task<JObject> Ask(String Action, JObject Payload)
            {

                var id = Guid.NewGuid().ToString("N");

                Assert.That(latest, Is.Not.Null, "No station is connected to this CSMS.");

                await Server.SendTextMessage(latest!, new JArray(2, id, Action, Payload).ToString(Formatting.None));

                var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(20);

                while (DateTimeOffset.UtcNow < until)
                {

                    var answer = received.FirstOrDefault(message => message[0].Value<Int32>() != 2 && message[1].Value<String>() == id);

                    if (answer is not null)
                    {
                        Assert.That(answer[0].Value<Int32>(), Is.EqualTo(3), $"{Action} was answered with an error: {answer.ToString(Formatting.None)}");
                        return (JObject) answer[2];
                    }

                    await Task.Delay(20);

                }

                Assert.Fail($"{Action} was not answered within 20 s.");
                return [];

            }

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
        private X509Certificate2  clientCA   = default!;
        private ECDsa             csmsKey    = default!;
        private X509Certificate2  leaf       = default!;

        private readonly List<X509Certificate2> certificates = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStationAndACSMS()
        {

            directory  = TestStations.TemporaryDirectory("station-certificate");
            station    = TestStations.New(directory, TestStations.Offline);

            ca         = CA("The operator's CA for its servers");
            otherCA    = CA("The operator's next CA for its servers");
            clientCA   = CA("The operator's CA for its stations");
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

            ca.      Dispose();
            otherCA. Dispose();
            clientCA.Dispose();
            leaf.    Dispose();
            csmsKey. Dispose();

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

        /// <summary>
        /// What the CA for the stations makes of a signing request: a client
        /// certificate, valid for the given number of days.
        /// </summary>
        private X509Certificate2 Issued(String CSR, Int32 Days = 365)
        {

            var request = CertificateRequest.LoadSigningRequestPem(CSR,
                                                                   HashAlgorithmName.SHA256,
                                                                   CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);

            using var caKey = clientCA.GetECDsaPrivateKey()!;

            var certificate = request.Create(clientCA.SubjectName,
                                             X509SignatureGenerator.CreateForECDsa(caKey),
                                             DateTimeOffset.UtcNow.AddDays(-1),
                                             DateTimeOffset.UtcNow.AddDays(Days),
                                             Guid.NewGuid().ToByteArray());

            certificates.Add(certificate);

            return certificate;

        }

        private static Byte[] Pem(X509Certificate2 Certificate)
            => System.Text.Encoding.ASCII.GetBytes(Certificate.ExportCertificatePem());

        private String URL
            => $"wss://127.0.0.1:{csms!.Port}/cs001";

        /// <summary>
        /// Started, and quick about a new certificate: a test does not wait two
        /// seconds for an answer to be on its way.
        /// </summary>
        private async Task Start(TimeSpan? GetsThroughWithin = null)
        {

            station = await TestStations.Started(station!, directory);

            station.SwitchesToANewCertificateAfter    = TimeSpan.FromMilliseconds(200);
            station.ANewCertificateGetsThroughWithin  = GetsThroughWithin ?? TimeSpan.FromSeconds(15);

        }

        /// <summary>
        /// Keep the given CA as a TLS root of the station's own store, for
        /// every use.
        /// </summary>
        private void Kept(X509Certificate2 Root)
            => Assert.That(station!.Certificates.Import(Pem(Root), CertificateKind.TLSRoot, null, Root.GetNameInfo(X509NameType.SimpleName, false),
                                                        null, out _, out var error),
                           Is.True, error);

        /// <summary>
        /// A key made on the station, which waits for its certificate in the
        /// station's certificate store.
        /// </summary>
        private SigningRequest AKey(String Subject, String? Algorithm = null)
        {

            Assert.That(station!.Certificates.TryCreateRequest(CertificateKind.TLSIdentity, Subject, Algorithm, null, null, null, out var request, out var error, out _),
                        Is.True, error);

            return request!;

        }

        /// <summary>
        /// An identity of the station - a key made on it, with a certificate
        /// of the CA for the stations - as somebody commissioning it brings
        /// one: what a connection names, by the handle the store gives it.
        /// </summary>
        private (String KeyId, X509Certificate2 Certificate) AKeyWithACertificate(Int32 Days = 365)
        {

            var request      = AKey("cs001");
            var certificate  = Issued(request.CSR, Days);

            Assert.That(station!.Certificates.TryAnswerRequest(certificate.ExportCertificatePem(), out var identity, out var answered, out _, out var error, out _),
                        Is.True, error);

            Assert.Multiple(() => {
                Assert.That(answered,      Is.EqualTo(request.Id));
                Assert.That(identity?.Id,  Is.EqualTo(IdOf(certificate)));
            });

            return (identity!.Id, certificate);

        }

        /// <summary>
        /// What the certificate store calls the identity a certificate is.
        /// </summary>
        private static String IdOf(X509Certificate2 Certificate)
            => CertificateEntry.ThumbprintOf(Certificate)[..CertificateEntry.IdLength];

        /// <summary>
        /// The identities of the station, and the keys that wait for theirs.
        /// </summary>
        private IEnumerable<String> Identities
            => station!.Certificates.ByKind(CertificateKind.TLSIdentity).Select(entry => entry.Id);

        private IEnumerable<String> WaitingKeys
            => station!.Certificates.Requests.Select(request => request.Id);

        /// <summary>
        /// A connection to the CSMS, written down before the station starts,
        /// that shows the certificate of the given key.
        /// </summary>
        private String Added(String KeyId)
        {
            Assert.That(station!.Connections.TryAddConnection("The CSMS", URL, "CSMS", true, null, KeyId, out var id, out var error),
                        Is.True, error);
            return id!;
        }

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Otherwise = null)
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
            while (!Condition())
            {
                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within 30 s.{(Otherwise is not null ? " " + Otherwise() : "")}");
                await Task.Delay(20);
            }
        }

        private Boolean IsConnected(String Id)
            => station!.ConnectionStates.TryGetValue(Id, out var state) && state.Status == ConnectionStatus.Connected;

        private ConnectionEntry Entry
            => station!.Connections.Connections.Single();

        private String WhatHappened
            => $"The connection shows {Entry.CertificateId}; the CSMS was shown {String.Join(", ", csms!.Shown)}; " +
               $"waiting: {String.Join(", ", station!.KeysWaitingForTheirCertificate)}.";

        /// <summary>
        /// A station with a certificate, connected to its CSMS, which was shown
        /// that certificate.
        /// </summary>
        private async Task<(String Connection, String KeyId, X509Certificate2 Certificate)> Connected(Int32      Days                = 365,
                                                                                                    TimeSpan?  GetsThroughWithin   = null)
        {

            Kept(ca);

            var (keyId, certificate)  = AKeyWithACertificate(Days);
            var connection            = Added(keyId);

            await Start(GetsThroughWithin);

            await Until(() => IsConnected(connection) && csms!.Shown.Length > 0 && csms.Requests("BootNotification").Length > 0,
                        "Connecting with the certificate it was commissioned with");

            Assert.That(csms!.Shown.Last(), Is.EqualTo(certificate.SerialNumber));

            return (connection, keyId, certificate);

        }

        private static JObject Trigger(String Message)
            => new (new JProperty("requestedMessage", Message));

        private static JObject Signed(X509Certificate2 Certificate, String? Type = "ChargingStationCertificate")
        {

            var signed = new JObject(new JProperty("certificateChain", Certificate.ExportCertificatePem()));

            if (Type is not null)
                signed.Add("certificateType", Type);

            return signed;

        }

        #endregion


        #region ATriggerMakesAKey_AndCertificateSignedDialsWithItsCertificate()

        /// <summary>
        /// Use case A02, as its CSMS starts it: the station makes a key of its
        /// own and shows its signing request in SignCertificate, goes on as
        /// before until the certificate comes, and is dialled again with it once
        /// CertificateSigned brought it. The key it replaces is kept.
        /// </summary>
        [Test]
        public async Task ATriggerMakesAKey_AndCertificateSignedDialsWithItsCertificate()
        {

            var (connection, keyId, _) = await Connected();

            var triggered = await csms!.Ask("TriggerMessage", Trigger("SignChargingStationCertificate"));

            Assert.That(triggered["status"]?.Value<String>(), Is.EqualTo("Accepted"));

            await Until(() => csms.Requests("SignCertificate").Length == 1 && station!.KeysWaitingForTheirCertificate.Count == 1,
                        "Asking for a certificate in SignCertificate");

            var asked   = (JObject) csms.Requests("SignCertificate")[0][3];
            var waiting = station!.KeysWaitingForTheirCertificate.Single();

            Assert.Multiple(() => {
                Assert.That(asked["certificateType"]?.Value<String>(),     Is.EqualTo("ChargingStationCertificate"));
                Assert.That(asked["csr"]?.Value<String>(),                 Does.Contain("BEGIN CERTIFICATE REQUEST"));
                Assert.That(waiting,                                       Is.Not.EqualTo(keyId),   "the key it shows was asked a certificate for, and no new one made");
                Assert.That(Entry.CertificateId,                           Is.EqualTo(keyId),       "the connection was switched before there was a certificate to switch to");
                Assert.That(IsConnected(connection),                       Is.True,                 "asking for a certificate cost the connection");
            });

            var next = Issued(asked["csr"]!.Value<String>()!);

            Assert.That(next.GetNameInfo(X509NameType.SimpleName, false), Is.EqualTo("cs001"), "the new key does not say who the station is, as the one before did");

            var signed = await csms.Ask("CertificateSigned", Signed(next));

            Assert.That(signed["status"]?.Value<String>(), Is.EqualTo("Accepted"), signed.ToString(Formatting.None));

            await Until(() => Entry.CertificateId == IdOf(next) && IsConnected(connection) && csms.Shown.Last() == next.SerialNumber,
                        "Being dialled again with the new certificate", () => WhatHappened);

            Assert.Multiple(() => {
                Assert.That(station.KeysWaitingForTheirCertificate,                              Is.Empty);
                Assert.That(WaitingKeys,                                                         Is.Empty,              "the key whose certificate came still waits for one");
                Assert.That(Identities,                                                          Does.Contain(keyId),   "the identity it replaced is gone, and with it the way back");
                Assert.That(csms.Requests("SignCertificate"),                                    Has.Length.EqualTo(1), "a certificate good for a year was asked to be renewed");
            });

        }

        #endregion

        #region ACertificateItsCSMSDoesNotLetIn_IsLeftForTheOneBefore()

        /// <summary>
        /// A new certificate that does not get through must not be the end of a
        /// station that had one that worked: the connection goes back to it.
        /// </summary>
        [Test]
        public async Task ACertificateItsCSMSDoesNotLetIn_IsLeftForTheOneBefore()
        {

            var (connection, keyId, certificate) = await Connected(GetsThroughWithin: TimeSpan.FromSeconds(3));

            await csms!.Ask("TriggerMessage", Trigger("SignChargingStationCertificate"));

            await Until(() => csms.Requests("SignCertificate").Length == 1, "Asking for a certificate in SignCertificate");

            var next    = Issued(csms.Requests("SignCertificate")[0][3]["csr"]!.Value<String>()!);
            var waiting = station!.KeysWaitingForTheirCertificate.Single();

            csms.TurnsAway = next.SerialNumber;

            var signed = await csms.Ask("CertificateSigned", Signed(next));

            Assert.That(signed["status"]?.Value<String>(), Is.EqualTo("Accepted"));

            await Until(() => Entry.CertificateId == IdOf(next), "Being switched to the new certificate", () => WhatHappened);

            var shownBefore = csms.Shown.Length;

            await Until(() => Entry.CertificateId == keyId && IsConnected(connection) && csms.Shown.Length > shownBefore,
                        "Going back to the certificate that worked", () => WhatHappened);

            Assert.Multiple(() => {
                Assert.That(csms.Shown.Last(),                                              Is.EqualTo(certificate.SerialNumber));
                Assert.That(Identities,                                                     Does.Contain(IdOf(next)), "the new certificate is gone, although its CSMS may let it in tomorrow");
            });

        }

        #endregion

        #region WhatIsNoCertificateOfThisStation_IsRejected_AndSoIsOneForAVehicle()

        /// <summary>
        /// A certificate is taken only where it answers a signing request made
        /// here, and only as the station's own: this station has no key a
        /// vehicle is shown.
        /// </summary>
        [Test]
        public async Task WhatIsNoCertificateOfThisStation_IsRejected_AndSoIsOneForAVehicle()
        {

            var (connection, keyId, _) = await Connected();

            using var somebodysKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var somebodys  = Issued(new CertificateRequest("CN=cs001", somebodysKey, HashAlgorithmName.SHA256).CreateSigningRequestPem());

            var foreign    = await csms!.Ask("CertificateSigned", Signed(somebodys));
            var vehicle    = await csms. Ask("CertificateSigned", Signed(somebodys, "V2GCertificate"));
            var triggered  = await csms. Ask("TriggerMessage",    Trigger("SignV2GCertificate"));

            Assert.Multiple(() => {

                Assert.That(foreign  ["status"]?.Value<String>(),                     Is.EqualTo("Rejected"));
                Assert.That(foreign  ["statusInfo"]?["reasonCode"]?.Value<String>(),  Is.EqualTo("InvalidCertificate"));
                Assert.That(foreign  ["statusInfo"]?["additionalInfo"]?.Value<String>(), Does.Contain("signing request made here"));

                Assert.That(vehicle  ["status"]?.Value<String>(),                     Is.EqualTo("Rejected"));
                Assert.That(vehicle  ["statusInfo"]?["reasonCode"]?.Value<String>(),  Is.EqualTo("NotSupported"));

                Assert.That(triggered["status"]?.Value<String>(),                     Is.EqualTo("Rejected"));

                Assert.That(Entry.CertificateId,                                      Is.EqualTo(keyId));
                Assert.That(IsConnected(connection),                                  Is.True);
                Assert.That(csms.Requests("SignCertificate"),                         Is.Empty);

            });

        }

        #endregion

        #region ACSMSThatPromisesNoCertificate_LeavesNoKeyBehind()

        /// <summary>
        /// A key nobody will bring a certificate for is not kept.
        /// </summary>
        [Test]
        public async Task ACSMSThatPromisesNoCertificate_LeavesNoKeyBehind()
        {

            var (_, keyId, _) = await Connected();

            csms!.SignCertificateStatus = "Rejected";

            var (accepted, said) = await station!.AskForAStationCertificate("a test asked for it");

            Assert.Multiple(() => {
                Assert.That(accepted,                                                       Is.False);
                Assert.That(said,                                                           Does.Contain("Rejected"));
                Assert.That(csms.Requests("SignCertificate"),                               Has.Length.EqualTo(1));
                Assert.That(station.KeysWaitingForTheirCertificate,                         Is.Empty);
                Assert.That(WaitingKeys,                                                    Is.Empty);
                Assert.That(Identities,                                                     Is.EqualTo(new[] { keyId }));
            });

        }

        #endregion

        #region AKeyMadeByHand_IsSentToTheCSMS_AndShownOnceItsCertificateComes()

        /// <summary>
        /// A key somebody made - with the subject and the template they chose
        /// - goes to the CSMS as it is: its request in SignCertificate, and
        /// the connection switched to it once CertificateSigned brings its
        /// certificate.
        /// </summary>
        [Test]
        public async Task AKeyMadeByHand_IsSentToTheCSMS_AndShownOnceItsCertificateComes()
        {

            var (connection, keyId, _) = await Connected();

            var made    = AKey("cs-by-hand.example.org", "ecdsa-p384");
            var byHand  = made.Id;
            var csr     = made.CSR;

            var (accepted, said) = await station!.SendSigningRequestToCSMS(byHand, "a test sent it");

            Assert.Multiple(() => {
                Assert.That(accepted,                                                           Is.True, said);
                Assert.That(csms!.Requests("SignCertificate"),                                  Has.Length.EqualTo(1));
                Assert.That(csms.Requests("SignCertificate")[0][3]["csr"]?.Value<String>(),     Is.EqualTo(csr), "the request was not sent as it was made");
                Assert.That(station.KeysWaitingForTheirCertificate,                             Is.EqualTo(new[] { byHand }));
                Assert.That(station.SigningRequestsSentToCSMS.Keys,                             Is.EqualTo(new[] { byHand }));
                Assert.That(Entry.CertificateId,                                                Is.EqualTo(keyId), "the connection was switched before there was something to switch to");
            });

            var next    = Issued(csr!);
            var signed  = await csms!.Ask("CertificateSigned", Signed(next));

            Assert.That(signed["status"]?.Value<String>(), Is.EqualTo("Accepted"));

            await Until(() => Entry.CertificateId == IdOf(next) && IsConnected(connection) && csms.Shown.Last() == next.SerialNumber,
                        "Being dialled again with the certificate of the key made by hand", () => WhatHappened);

            Assert.Multiple(() => {
                Assert.That(station.KeysWaitingForTheirCertificate,  Is.Empty);
                Assert.That(WaitingKeys,                             Is.Empty, "the key is an identity now, and waits all the same");
                Assert.That(Identities,                              Is.EquivalentTo(new[] { keyId, IdOf(next) }));
            });

        }

        #endregion

        #region AKeyMadeByHand_IsKept_WhateverTheCSMSSays()

        /// <summary>
        /// A key the station made by itself goes where no certificate is
        /// promised for it. One somebody made is theirs: it may still be
        /// taken to a certificate authority on foot.
        /// </summary>
        [Test]
        public async Task AKeyMadeByHand_IsKept_WhateverTheCSMSSays()
        {

            var (_, keyId, _) = await Connected();

            var byHand = AKey("cs-by-hand.example.org").Id;

            csms!.SignCertificateStatus = "Rejected";

            var (accepted, said) = await station!.SendSigningRequestToCSMS(byHand, "a test sent it");

            Assert.Multiple(() => {
                Assert.That(accepted,                                                       Is.False);
                Assert.That(said,                                                           Does.Contain("Rejected"));
                Assert.That(station.KeysWaitingForTheirCertificate,                         Is.Empty);
                Assert.That(WaitingKeys,                                                    Is.EqualTo(new[] { byHand }));
                Assert.That(Identities,                                                     Is.EqualTo(new[] { keyId }));
            });

            // Nor does the station's own asking take it away.
            csms.SignCertificateStatus = "Accepted";

            Assert.That((await station.SendSigningRequestToCSMS(byHand, "a test sent it again")).Accepted,    Is.True);
            Assert.That((await station.AskForAStationCertificate("a test asked for it")).Accepted,            Is.True);

            Assert.Multiple(() => {
                Assert.That(WaitingKeys,                                                    Does.Contain(byHand));
                Assert.That(station.KeysWaitingForTheirCertificate,                         Does.Not.Contain(byHand), "two keys wait to replace the same certificate");
            });

            // What has its certificate is not sent again, and what is not here is not sent at all.
            // (Sent to nobody, where no connection stands: see ARequestIsSentToNobody_WhereNoConnectionStands.)
            Assert.Multiple(async () => {
                Assert.That((await station.SendSigningRequestToCSMS(keyId,     "a test")).Said,  Does.Contain("already"));
                Assert.That((await station.SendSigningRequestToCSMS("nothing", "a test")).Said,  Does.Contain("no key"));
            });

        }

        #endregion

        #region ARequestIsSentToNobody_WhereNoConnectionStands()

        /// <summary>
        /// Without a CSMS to ask, the request is not sent - and the station
        /// says so, rather than that a CSMS refused it.
        /// </summary>
        [Test]
        public async Task ARequestIsSentToNobody_WhereNoConnectionStands()
        {

            var byHand = AKey("cs-by-hand.example.org").Id;

            var (accepted, said) = await station!.SendSigningRequestToCSMS(byHand, "a test sent it");

            Assert.Multiple(() => {
                Assert.That(accepted,                                                       Is.False);
                Assert.That(said,                                                           Does.Contain("not connected"));
                Assert.That(station.KeysWaitingForTheirCertificate,                         Is.Empty);
                Assert.That(WaitingKeys,                                                    Does.Contain(byHand));
            });

        }

        #endregion

        #region TheKeysAStationOfBeforeKept_AreMovedIntoItsCertificateStore()

        /// <summary>
        /// A station of before kept the keys it dials with in a directory of
        /// their own. They are moved into the certificate store at the first
        /// start that finds them: a key with its certificate is an identity
        /// there, a key whose request is still out waits there - and the
        /// connection that showed the one shows the identity it became.
        /// </summary>
        [Test]
        public async Task TheKeysAStationOfBeforeKept_AreMovedIntoItsCertificateStore()
        {

            var former   = Path.Combine(directory, ChargingStation.FormerClientKeysDirectoryName);
            var madeAt   = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

            // Where keys of the kind are made, to have some as they were kept.
            var scratch  = new CertificateStore(Path.Combine(directory, "scratch"),
                                                new cloud.charging.open.protocols.WWCP.Node.Logging.EventLog(),
                                                [ CertificateKind.TLSIdentity ],
                                                RequestKinds: [ CertificateKind.TLSIdentity ]);

            String AKeyOfBefore(String Subject, Func<String, X509Certificate2?> With)
            {

                Assert.That(scratch.TryCreateRequest(CertificateKind.TLSIdentity, Subject, null, null, null, null, out var request, out var error, out _),
                            Is.True, error);

                Directory.CreateDirectory(former);

                foreach (var extension in new[] { "key.pem", "csr.pem" })
                    File.Copy(Path.Combine(scratch.Directory, CertificateStore.RequestsDirectoryName, $"{request!.Id}.{extension}"),
                              Path.Combine(former, $"{request.Id}.{extension}"));

                File.WriteAllText(Path.Combine(former, $"{request!.Id}.json"),
                                  new JObject(
                                      new JProperty("id",         request.Id),
                                      new JProperty("algorithm",  "ecdsa-p256"),
                                      new JProperty("createdAt",  madeAt.ToString("o")),
                                      new JProperty("subject",    request.Subject)
                                  ).ToString());

                if (With(request.CSR) is X509Certificate2 certificate)
                    File.WriteAllText(Path.Combine(former, $"{request.Id}.cert.pem"), certificate.ExportCertificatePem());

                return request.Id;

            }

            X509Certificate2? certificate = null;

            var shown    = AKeyOfBefore("cs001", csr => certificate = Issued(csr));
            var waiting  = AKeyOfBefore("cs002", _   => null);

            // And a connection that showed the certificate of the first, as
            // it was written down then: by the name of its key.
            station!.Connections.KnownCertificateIds = () => [ shown ];

            Assert.That(station.Connections.TryAddConnection("CSMS", "wss://127.0.0.1:1/cs001", "CSMS", false, null, shown, out var connection, out var problem),
                        Is.True, problem);

            await station.DisposeAsync();

            // The station of today starts on what it left.
            station = TestStations.New(directory, TestStations.Offline);

            var identity = station.Certificates.ByKind(CertificateKind.TLSIdentity).SingleOrDefault();

            Assert.Multiple(() => {

                Assert.That(identity?.Id,                                                Is.EqualTo(IdOf(certificate!)), "the key with its certificate is no identity of the store");
                Assert.That(identity?.HasPrivateKey,                                     Is.True);

                Assert.That(WaitingKeys,                                                 Is.EqualTo(new[] { waiting }), "the key whose request was out does not wait in the store");
                Assert.That(station.Certificates.Request(waiting)?.CreatedAt,            Is.EqualTo(madeAt), "when it was made was forgotten");

                Assert.That(station.Connections.CopyOf(connection!)?.CertificateId,      Is.EqualTo(IdOf(certificate!)), "the connection does not show the identity its key became");

                Assert.That(Directory.Exists(former),                                    Is.False, "private keys were left where they had been");

            });

            // And nothing is moved, or lost, the next time.
            await station.DisposeAsync();

            station = TestStations.New(directory, TestStations.Offline);

            Assert.Multiple(() => {
                Assert.That(Identities,                                                  Is.EqualTo(new[] { IdOf(certificate!) }));
                Assert.That(WaitingKeys,                                                 Is.EqualTo(new[] { waiting }));
                Assert.That(station.Connections.CopyOf(connection!)?.CertificateId,      Is.EqualTo(IdOf(certificate!)));
            });

        }

        #endregion

        #region ACertificateThatRunsOutSoon_IsAskedToBeRenewedByTheStationItself()

        /// <summary>
        /// Use case A03: nobody has to remember. A station whose certificate
        /// runs out within thirty days asks for the next one when it connects.
        /// </summary>
        [Test]
        public async Task ACertificateThatRunsOutSoon_IsAskedToBeRenewedByTheStationItself()
        {

            var (connection, keyId, _) = await Connected(Days: 10);

            await Until(() => csms!.Requests("SignCertificate").Length == 1, "Asking for the next certificate by itself");

            var next    = Issued(csms!.Requests("SignCertificate")[0][3]["csr"]!.Value<String>()!);
            var signed  = await csms.Ask("CertificateSigned", Signed(next, Type: null));

            Assert.That(signed["status"]?.Value<String>(), Is.EqualTo("Accepted"), "a CertificateSigned that names no type is one for the station's own certificate");

            await Until(() => Entry.CertificateId != keyId && IsConnected(connection) && csms.Shown.Last() == next.SerialNumber,
                        "Being dialled again with the next certificate", () => WhatHappened);

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.That(csms.Requests("SignCertificate"), Has.Length.EqualTo(1), "connected with a certificate good for a year, it asked again");

        }

        #endregion

        #region InstallCertificate_KeepsTheRootOfItsCSMSWhereAHandshakeAsks()

        /// <summary>
        /// A CSMSRootCertificate is a TLS root of the station's own store, for
        /// "ocpp": it is on the certificate page, listed to the CSMS, vouches
        /// for a server at the next handshake - and no longer once it is
        /// deleted. A root for every use is not deleted for the back ends alone.
        /// </summary>
        [Test]
        public async Task InstallCertificate_KeepsTheRootOfItsCSMSWhereAHandshakeAsks()
        {

            var (_, keyId, _) = await Connected();

            async Task<JObject> Test()
                => await station!.TestConnection("A test", URL, "CSMS", "OCPP2.1", CertificateId: keyId, OCPPRequest: ChargingStation.TestAsksNothing);

            // The server the CSMS moves to has a certificate of its next CA,
            // which nobody here knows yet.
            csms!.Certificate = Leaf(otherCA, csmsKey);

            Assert.That((await Test())["ok"]?.Value<Boolean>(), Is.False, "a server of a CA nobody knows was believed");

            var installed = await csms.Ask("InstallCertificate", new JObject(
                                                                     new JProperty("certificateType",  "CSMSRootCertificate"),
                                                                     new JProperty("certificate",      otherCA.ExportCertificatePem())
                                                                 ));

            Assert.That(installed["status"]?.Value<String>(), Is.EqualTo("Accepted"), installed.ToString(Formatting.None));

            var root = station!.Certificates.ByKind(CertificateKind.TLSRoot).
                                             SingleOrDefault(entry => entry.Thumbprint.Equals(CertificateEntry.ThumbprintOf(otherCA), StringComparison.OrdinalIgnoreCase));

            Assert.Multiple(() => {
                Assert.That(root,                                              Is.Not.Null, "the root is not in the station's certificate store");
                Assert.That(root?.Usages?.Select(usage => usage.ToString()),   Is.EqualTo(new[] { ChargingStation.OCPPCertificateUsage }));
            });

            var after = await Test();

            Assert.That(after["ok"]?.Value<Boolean>(), Is.True, $"the root its CSMS installed does not vouch for its server: {after["steps"]?.ToString(Formatting.None)}");

            var listed = await csms.Ask("GetInstalledCertificateIds", new JObject(
                                                                          new JProperty("certificateType", new JArray("CSMSRootCertificate"))
                                                                      ));

            var chains = (JArray) listed["certificateHashDataChain"]!;

            JObject HashesOf(X509Certificate2 Root)
                => (JObject) chains.Single(chain => chain["certificateHashData"]!["serialNumber"]!.Value<String>()!.TrimStart('0').
                                                        Equals(Root.SerialNumber.TrimStart('0'), StringComparison.OrdinalIgnoreCase))["certificateHashData"]!;

            Assert.That(chains, Has.Count.EqualTo(2), "listed are the root it installed and the one kept for every use");

            var deleted  = await csms.Ask("DeleteCertificate", new JObject(new JProperty("certificateHashData", HashesOf(otherCA))));
            var kept     = await csms.Ask("DeleteCertificate", new JObject(new JProperty("certificateHashData", HashesOf(ca))));

            Assert.Multiple(() => {
                Assert.That(deleted["status"]?.Value<String>(),   Is.EqualTo("Accepted"));
                Assert.That(kept   ["status"]?.Value<String>(),   Is.EqualTo("Failed"),   "a root for every use was deleted for the back ends alone");
                Assert.That(station.Certificates.ByKind(CertificateKind.TLSRoot).Count(),  Is.EqualTo(1));
            });

            Assert.That((await Test())["ok"]?.Value<Boolean>(), Is.False, "a deleted root still vouches");

        }

        #endregion

    }

}
