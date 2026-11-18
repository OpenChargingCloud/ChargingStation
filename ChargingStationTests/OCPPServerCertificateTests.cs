/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of WWCP Charging Station <https://github.com/OpenChargingCloud/ChargingStation>
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

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A CSMS that dials the station and signs in with its client
    /// certificate: known by the pin of its key, or checked by its CA and
    /// issued to the name it comes under. A certificate opens nothing but the
    /// connection that names it - not another name, not a connection that
    /// names credentials, and not one of the other version of OCPP.
    /// </summary>
    [TestFixture]
    public class OCPPServerCertificateTests
    {

        #region Data

        private const String                     Name          = "csms-01";

        private          String                  directory     = "";
        private          IPPort                  port;
        private          IPPort                  port16;
        private readonly List<ChargingStation>   stations      = [];
        private readonly List<X509Certificate2>  certificates  = [];
        private readonly List<ECDsa>             keys          = [];

        /// <summary>
        /// The thumbprints of the CAs below a root a test made, to be taken
        /// out of the intermediate store of the user again.
        /// </summary>
        private readonly List<String>            issuingCAs    = [];
        private readonly List<String>            said          = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void Setup()
        {

            lock (said)
                said.Clear();

            directory  = TestStations.TemporaryDirectory("ocpp-server-certificates");
            port       = IPPort.Parse(TestPorts.Free());
            port16     = IPPort.Parse(TestPorts.Free());

            Directory.CreateDirectory(directory);

        }

        [TearDown]
        public async Task TearDown()
        {

            foreach (var station in stations)
            {
                try
                {
                    await station.DisposeAsync();
                }
                catch (Exception)
                { }
            }

            stations.Clear();

            foreach (var certificate in certificates)
                certificate.Dispose();

            certificates.Clear();

            foreach (var key in keys)
                key.Dispose();

            keys.Clear();

            TestStations.ForgetIssuingCAs(issuingCAs);
            issuingCAs.Clear();

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private ECDsa AKey()
        {
            var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            keys.Add(key);
            return key;
        }

        /// <summary>
        /// A CA made in memory, which no machine knows.
        /// </summary>
        private X509Certificate2 CA(String CommonName)
        {

            var request = new CertificateRequest($"CN={CommonName}", AKey(), HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            certificates.Add(certificate);

            return certificate;

        }

        /// <summary>
        /// A CA below the given one, which signs what is below it: with its
        /// key, to issue with.
        /// </summary>
        private X509Certificate2 AnIntermediate(String CommonName, X509Certificate2 Issuer)
        {

            var key     = AKey();
            var request = new CertificateRequest($"CN={CommonName}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

            // Ending with its issuer at the latest: made a second after it, it
            // would end a second after it, which is refused.
            using var issued = request.Create(Issuer, DateTimeOffset.UtcNow.AddDays(-1), Earlier(DateTimeOffset.UtcNow.AddDays(300), Issuer.NotAfter), Guid.NewGuid().ToByteArray());

            var certificate  = issued.CopyWithPrivateKey(key);

            certificates.Add(certificate);
            issuingCAs.  Add(certificate.Thumbprint);

            return certificate;

        }

        /// <summary>
        /// The earlier of a moment and the end of a certificate.
        /// </summary>
        private static DateTimeOffset Earlier(DateTimeOffset Wanted, DateTime NotAfter)
        {
            var end = new DateTimeOffset(NotAfter.ToUniversalTime(), TimeSpan.Zero);
            return Wanted < end ? Wanted : end;
        }

        /// <summary>
        /// The given certificate without its key: what is sent beside a
        /// certificate, and what is kept of somebody else's CA.
        /// </summary>
        private X509Certificate2 WithoutItsKey(X509Certificate2 Certificate)
        {

            var bare = X509CertificateLoader.LoadCertificate(Certificate.RawData);

            certificates.Add(bare);

            return bare;

        }

        /// <summary>
        /// A client certificate for the given key, issued to the given common
        /// name: by the given CA, or by itself.
        /// </summary>
        private X509Certificate2 AClientCertificate(String CommonName, ECDsa Key, X509Certificate2? Issuer = null)
        {

            var request = new CertificateRequest($"CN={CommonName}", Key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.2") ], false));

            X509Certificate2 withKey;

            if (Issuer is null)
                withKey = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            else
            {
                using var issued = request.Create(Issuer, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), Guid.NewGuid().ToByteArray());
                withKey = issued.CopyWithPrivateKey(Key);
            }

            // One an SslStream can show on Windows.
            var certificate = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            withKey.Dispose();

            certificates.Add(certificate);

            return certificate;

        }

        /// <summary>
        /// A TLS server identity for 127.0.0.1 in a PKCS#12 file.
        /// </summary>
        private String AnIdentityFile()
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request   = new CertificateRequest("CN=charging station web interface", key, HashAlgorithmName.SHA256);
            var names     = new SubjectAlternativeNameBuilder();

            names.AddIpAddress(System.Net.IPAddress.Loopback);
            names.AddDnsName("localhost");

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            var file = Path.Combine(directory, "station-web.p12");

            File.WriteAllBytes(file, certificate.Export(X509ContentType.Pkcs12));

            return file;

        }

        /// <summary>
        /// A station with an OCPP server for OCPP 2.1 and for OCPP 1.6, with
        /// or without HTTPS, told what the test writes down, and started.
        /// </summary>
        private async Task<ChargingStation> Started(Action<ChargingStation> WriteDown, Boolean HTTPS = true)
        {

            var arguments = HTTPS
                                ? NodeArguments.Parse([ "--https", "--import-certificate", $"tlsServerIdentity={AnIdentityFile()}" ])
                                : NodeArguments.Parse([]);

            Assert.That(arguments.Problem, Is.Null);

            var station = await TestPorts.StartedOnFreshPorts(() => {

                var configuration  = TestStations.Offline;

                configuration.Add("ocppServer", new JObject(
                                                    new JProperty("enabled", true),
                                                    new JProperty("port",    port.  ToUInt16()),
                                                    new JProperty("port16",  port16.ToUInt16())
                                                ));

                var stationDirectory = Path.Combine(directory, "station" + stations.Count);

                // Made again where a port was taken: what was written down the
                // first time is in its directory already.
                var again  = Directory.Exists(stationDirectory);

                var made   = TestStations.New(stationDirectory,
                                              configuration,
                                              WithDisplay:  false,
                                              Web:          arguments.Web);

                made.Log.OnLogged += entry => { lock (said) said.Add(entry.Message); };

                if (HTTPS)
                {
                    var refusal = new StringWriter();
                    Assert.That(made.ImportCertificates(arguments, out _, new StringWriter(), refusal), Is.Null, refusal.ToString());
                }

                if (!again)
                    WriteDown(made);

                return made;

            });

            stations.Add(station);

            return station;

        }

        /// <summary>
        /// A connection the other end makes, signed in to with a client
        /// certificate: known by the pins of its key, or held to roots.
        /// </summary>
        private static String WrittenDown(ChargingStation  Station,
                                          IPPort           Port,
                                          String           InboundName,
                                          String?          KeyPins  = null,
                                          ServerPins?      Pins     = null)
        {

            Assert.That(Station.Connections.TryAddConnection($"'{InboundName}', dialling in", $"wss://127.0.0.1:{Port}/{InboundName}", "CSMS", true, null, null,
                                                             out var id, out var error,
                                                             ServerKeyPins:  KeyPins,
                                                             Pins:           Pins is null ? null : new ServerPinsChange(Pins),
                                                             Inbound:        true),
                        Is.True, error);

            return id!;

        }

        private static void Kept(ChargingStation Station, X509Certificate2 Root, CertificateKind? As = null)
        {
            Assert.That(Station.Certificates.Import(System.Text.Encoding.ASCII.GetBytes(Root.ExportCertificatePem()),
                                                    As ?? CertificateKind.TLSRoot, null, Root.GetNameInfo(X509NameType.SimpleName, false),
                                                    null, out _, out var error),
                        Is.True, error);
        }

        private String Said()
        {
            lock (said) return String.Join("\n", said.TakeLast(40));
        }

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

        private static String StatusOf(ChargingStation Station, String Id)
            => Station.ConnectionStates.TryGetValue(Id, out var state)
                   ? OCPP.ConnectionState.AsText(state.Status)
                   : "nothing yet";

        private static String SaidOf(ChargingStation Station, String Id)
            => Station.DialledConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

        /// <summary>
        /// A CSMS that dials the station, believes whatever certificate it is
        /// shown, and shows the given one of its own - alone, or with the
        /// given certificates of the CAs above it sent along.
        /// </summary>
        private static WebSocketClient ACSMS(IPPort                           Port,
                                             String                           As,
                                             X509Certificate2?                Certificate     = null,
                                             IHTTPAuthentication?             Authentication  = null,
                                             String                           Subprotocol     = "ocpp2.1",
                                             IEnumerable<X509Certificate2>?   SentAlong       = null)

            => new (
                   URL.Parse($"wss://127.0.0.1:{Port}/{As}"),
                   HTTPAuthentication:          Authentication,
                   SecWebSocketProtocols:       [ Subprotocol ],
                   RemoteCertificateValidator:  (sender, certificate, chain, client, errors) => TLSValidationResult.Success(),
                   ClientCertificates:          Certificate is not null && SentAlong is null ? [ Certificate ] : null,
                   ClientCertificateChain:      Certificate is not null && SentAlong is not null ? [ Certificate, .. SentAlong ] : null
               );

        /// <summary>
        /// What the station answered - or nothing, where it did not get that
        /// far: a TLS handshake that was refused.
        /// </summary>
        private static async Task<HTTPStatusCode?> Dialled(WebSocketClient CSMS)
        {
            try
            {
                return (await CSMS.Connect()).Item2.HTTPStatusCode;
            }
            catch (Exception)
            {
                return null;
            }
        }

        #endregion


        #region KnownByTheKeyOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()

        /// <summary>
        /// By its key pin: the key is what is known, and what is written
        /// around it - a common name of its own making - names nobody. Another
        /// key opens nothing.
        /// </summary>
        [Test]
        public async Task KnownByTheKeyOfItsCertificate_ItIsLetIn_WhateverTheCertificateSays()
        {

            var key      = AKey();
            var id       = "";

            var station  = await Started(made => id = WrittenDown(made, port, Name, KeyPins: KeyPin.FromKey(key).ToHex()));

            await Until(() => StatusOf(station, id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            Assert.That(SaidOf(station, id), Does.Contain($"Waiting for '{Name}' to dial in"), "the station says that nobody can sign in with a certificate");

            await using (var stranger = ACSMS(port, Name, AClientCertificate(Name, AKey())))
            {
                Assert.That(await Dialled(stranger), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a certificate of another key, issued to the right name");
            }

            await using (var without = ACSMS(port, Name))
            {
                Assert.That(await Dialled(without), Is.EqualTo(HTTPStatusCode.Unauthorized), "no certificate at all");
            }

            Assert.That(StatusOf(station, id), Is.EqualTo("waiting"));

            await using var csms = ACSMS(port, Name, AClientCertificate("whatever its maker wrote", key));

            Assert.That(await Dialled(csms), Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in by the key of its certificate.\n" + Said());

            await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            Assert.That(station.OCPPServerJSON()["servers"]![0]!["connected"]!.Values<String>(), Is.EqualTo(new[] { Name }));

        }

        #endregion

        #region APinnedKey_OpensNothingButTheConnectionThatPinsIt()

        /// <summary>
        /// The key of one connection is not a way in under the name of
        /// another: neither one that pins another key, nor one that names a
        /// password.
        /// </summary>
        [Test]
        public async Task APinnedKey_OpensNothingButTheConnectionThatPinsIt()
        {

            var key       = AKey();
            var password  = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var other     = "";
            var withLogin = "";

            var station   = await Started(made => {

                WrittenDown(made, port, Name, KeyPins: KeyPin.FromKey(key).ToHex());

                other = WrittenDown(made, port, "csms-02", KeyPins: KeyPin.FromKey(AKey()).ToHex());

                Assert.That(made.Connections.TryAddAuthentication("What the third signs in with", "basic", "csms-03", password,
                                                                  out var login, out var error),
                            Is.True, error);

                Assert.That(made.Connections.TryAddConnection("The third, dialling in", $"wss://127.0.0.1:{port}/csms-03", "CSMS", true, login, null,
                                                              out var third, out error,
                                                              Inbound: true),
                            Is.True, error);

                withLogin = third!;

            });

            await Until(() => StatusOf(station, other) == "waiting" && StatusOf(station, withLogin) == "waiting", "Waiting for them");

            // Issued to the very name it asks for: the name is its maker's to write.
            await using (var asAnother = ACSMS(port, "csms-02", AClientCertificate("csms-02", key)))
            {
                Assert.That(await Dialled(asAnother), Is.EqualTo(HTTPStatusCode.Unauthorized), "under the name of a connection that pins another key");
            }

            await using (var asTheThird = ACSMS(port, "csms-03", AClientCertificate("csms-03", key)))
            {
                Assert.That(await Dialled(asTheThird), Is.EqualTo(HTTPStatusCode.Unauthorized), "under the name of a connection that names a password");
            }

            await using (var asNobody = ACSMS(port, "csms-04", AClientCertificate("csms-04", key)))
            {
                Assert.That(await Dialled(asNobody), Is.EqualTo(HTTPStatusCode.Unauthorized), "under a name nobody wrote down");
            }

            Assert.Multiple(() => {
                Assert.That(StatusOf(station, other),      Is.EqualTo("waiting"));
                Assert.That(StatusOf(station, withLogin),  Is.EqualTo("waiting"));
            });

        }

        #endregion

        #region CheckedByItsCA_ItIsLetInUnderTheNameItIsIssuedTo()

        /// <summary>
        /// By its CA: a certificate of a root this station keeps and the
        /// connection is held to, issued to the name it comes under - and
        /// neither one issued to another name, nor one of another CA.
        /// </summary>
        [Test]
        public async Task CheckedByItsCA_ItIsLetInUnderTheNameItIsIssuedTo()
        {

            var ca       = CA("The CA of the CSMS");
            var otherCA  = CA("Somebody else's CA");
            var id       = "";

            var station  = await Started(made => {

                Kept(made, ca);
                Kept(made, otherCA);

                id = WrittenDown(made, port, Name, Pins: new ServerPins([], [ ServerPins.KeyPinOf(ca) ]));

            });

            await Until(() => StatusOf(station, id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            await using (var ofAnotherCA = ACSMS(port, Name, AClientCertificate(Name, AKey(), otherCA)))
            {
                Assert.That(await Dialled(ofAnotherCA), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "issued to the right name by a CA the connection is not held to");
            }

            await using (var asAnother = ACSMS(port, Name, AClientCertificate("csms-02", AKey(), ca)))
            {
                Assert.That(await Dialled(asAnother), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "issued by the right CA to another name");
            }

            await using (var selfMade = ACSMS(port, Name, AClientCertificate(Name, AKey())))
            {
                Assert.That(await Dialled(selfMade), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "issued to the right name by itself");
            }

            Assert.That(StatusOf(station, id), Is.EqualTo("waiting"));

            await using var csms = ACSMS(port, Name, AClientCertificate(Name, AKey(), ca));

            Assert.That(await Dialled(csms), Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in by the certificate of its CA.\n" + Said());

            await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

        }

        #endregion

        #region IssuedThroughIntermediates_TheBackEndSendsThemAlong()

        /// <summary>
        /// A certificate issued by a CA below the root the connection is held
        /// to - one, or two of them, one below the other: the station has
        /// never seen them, the back end sends them beside its own, and the
        /// station checks the whole chain with them. Without them the chain
        /// ends nowhere, and with the intermediates of another root it ends
        /// at the wrong one.
        /// </summary>
        /// <remarks>
        /// On Windows, what a client sends along it first puts into the
        /// intermediate store of its user, where a server run by the same
        /// user finds it whether it was sent or not: there this says less
        /// than on Linux, where nothing is shared. Taken out of that store
        /// again when the test is over, see <see cref="TestStations.ForgetIssuingCAs"/>.
        /// </remarks>
        [TestCase(1)]
        [TestCase(2)]
        public async Task IssuedThroughIntermediates_TheBackEndSendsThemAlong(Int32 Intermediates)
        {

            var root     = CA("The root of the CSMS's operator");
            var other    = CA("Somebody else's root");
            var id       = "";

            var station  = await Started(made => {

                Kept(made, root);
                Kept(made, other);

                id = WrittenDown(made, port, Name, Pins: new ServerPins([], [ ServerPins.KeyPinOf(root) ]));

            });

            await Until(() => StatusOf(station, id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            X509Certificate2[] Below(X509Certificate2 Root)
            {

                var chain  = new List<X509Certificate2>();
                var issuer = Root;

                for (var level = 1; level <= Intermediates; level++)
                {
                    issuer = AnIntermediate($"Issuing CA {level} below {Root.GetNameInfo(X509NameType.SimpleName, false)}", issuer);
                    chain.Insert(0, issuer);
                }

                return [.. chain];

            }

            var above       = Below(root);
            var aboveOther  = Below(other);

            await using (var alone = ACSMS(port, Name, AClientCertificate(Name, AKey(), above[0])))
            {
                Assert.That(await Dialled(alone), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "its certificate alone, of a CA the station has never seen");
            }

            await using (var ofAnother = ACSMS(port, Name, AClientCertificate(Name, AKey(), aboveOther[0]), SentAlong: aboveOther.Select(WithoutItsKey)))
            {
                Assert.That(await Dialled(ofAnother), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "a whole chain, which ends at a root the connection is not held to");
            }

            Assert.That(StatusOf(station, id), Is.EqualTo("waiting"));

            await using var csms = ACSMS(port, Name, AClientCertificate(Name, AKey(), above[0]), SentAlong: above.Select(WithoutItsKey));

            Assert.That(await Dialled(csms), Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in with the intermediates it sent along.\n" + Said());

            await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

        }

        #endregion

        #region IssuedThroughIntermediates_TheStationKnowsThemAlready()

        /// <summary>
        /// The other way: the back end shows its certificate and nothing
        /// else, and the station knows the CAs between it and the root - kept
        /// in its certificate store as issuing CAs. They help to
        /// build the chain and vouch for nothing: one kept below a root the
        /// connection is not held to opens nothing.
        /// </summary>
        [TestCase(1)]
        [TestCase(2)]
        public async Task IssuedThroughIntermediates_TheStationKnowsThemAlready(Int32 Intermediates)
        {

            var root     = CA("The root of the CSMS's operator");
            var other    = CA("Somebody else's root");
            var id       = "";

            X509Certificate2[] Below(X509Certificate2 Root)
            {

                var chain  = new List<X509Certificate2>();
                var issuer = Root;

                for (var level = 1; level <= Intermediates; level++)
                {
                    issuer = AnIntermediate($"Issuing CA {level} below {Root.GetNameInfo(X509NameType.SimpleName, false)}", issuer);
                    chain.Insert(0, issuer);
                }

                return [.. chain];

            }

            var above       = Below(root);
            var aboveOther  = Below(other);

            var station  = await Started(made => {

                Kept(made, root);
                Kept(made, other);

                foreach (var intermediate in above.Concat(aboveOther))
                    Kept(made, intermediate, CertificateKind.TLSIntermediate);

                id = WrittenDown(made, port, Name, Pins: new ServerPins([], [ ServerPins.KeyPinOf(root) ]));

            });

            await Until(() => StatusOf(station, id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            await using (var ofAnother = ACSMS(port, Name, AClientCertificate(Name, AKey(), aboveOther[0])))
            {
                Assert.That(await Dialled(ofAnother), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "issued below a root the connection is not held to, through CAs the station keeps");
            }

            await using (var asAnother = ACSMS(port, Name, AClientCertificate("csms-02", AKey(), above[0])))
            {
                Assert.That(await Dialled(asAnother), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols), "issued to another name");
            }

            Assert.That(StatusOf(station, id), Is.EqualTo("waiting"));

            await using var csms = ACSMS(port, Name, AClientCertificate(Name, AKey(), above[0]));

            Assert.That(await Dialled(csms), Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in with its certificate alone, though the station knows the CAs above it.\n" + Said());

            await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

        }

        #endregion

        #region ACertificateOfOneVersionOfOCPP_OpensNothingOfTheOther()

        /// <summary>
        /// A name is one connection's within a version of OCPP only: the
        /// certificate "csms-01" signs in with over OCPP 1.6 opens nothing
        /// over OCPP 2.1, where "csms-01" names a password.
        /// </summary>
        [Test]
        public async Task ACertificateOfOneVersionOfOCPP_OpensNothingOfTheOther()
        {

            var ca        = CA("The CA of the CSMS");
            var password  = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var over16    = "";
            var over21    = "";

            var station   = await Started(made => {

                Kept(made, ca);

                Assert.That(made.Connections.TryAddConnection($"'{Name}' over OCPP 1.6", $"wss://127.0.0.1:{port16}/{Name}", "CSMS", true, null, null,
                                                              out var first, out var error,
                                                              OCPPVersion:  "OCPP1.6",
                                                              Pins:         new ServerPinsChange(new ServerPins([], [ ServerPins.KeyPinOf(ca) ])),
                                                              Inbound:      true),
                            Is.True, error);

                over16 = first!;

                Assert.That(made.Connections.TryAddAuthentication("What it signs in with over OCPP 2.1", "basic", Name, password,
                                                                  out var login, out error),
                            Is.True, error);

                Assert.That(made.Connections.TryAddConnection($"'{Name}' over OCPP 2.1", $"wss://127.0.0.1:{port}/{Name}", "CSMS", true, login, null,
                                                              out var second, out error,
                                                              Inbound: true),
                            Is.True, error);

                over21 = second!;

            });

            await Until(() => StatusOf(station, over16) == "waiting" && StatusOf(station, over21) == "waiting", "Waiting for both",
                        () => $"{SaidOf(station, over16)} / {SaidOf(station, over21)}");

            var certificate = AClientCertificate(Name, AKey(), ca);

            await using (var atTheOther = ACSMS(port, Name, certificate))
            {
                Assert.That(await Dialled(atTheOther), Is.Not.EqualTo(HTTPStatusCode.SwitchingProtocols),
                            "the certificate of the OCPP 1.6 connection opened the OCPP 2.1 one, which names a password");
            }

            Assert.That(StatusOf(station, over21), Is.EqualTo("waiting"));

            await using var csms = ACSMS(port16, Name, certificate, Subprotocol: "ocpp1.6");

            Assert.That(await Dialled(csms), Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in over OCPP 1.6.\n" + Said());

            await Until(() => StatusOf(station, over16) == "connected", "Being connected over OCPP 1.6", () => $"{StatusOf(station, over16)}: {SaidOf(station, over16)}");

            Assert.That(StatusOf(station, over21), Is.EqualTo("waiting"));

        }

        #endregion

        #region WithoutTLS_NobodySignsInWithACertificate_AndThatIsSaid()

        /// <summary>
        /// A station without HTTPS serves OCPP without TLS, and asks nobody
        /// for a certificate: a connection that signs in with one is not
        /// waited for, but said to be unable to.
        /// </summary>
        [Test]
        public async Task WithoutTLS_NobodySignsInWithACertificate_AndThatIsSaid()
        {

            var id       = "";

            var station  = await Started(made => id = WrittenDown(made, port, Name, KeyPins: KeyPin.FromKey(AKey()).ToHex()),
                                         HTTPS: false);

            await Until(() => SaidOf(station, id).Contains("can not sign in"), "Saying that it can not sign in", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            Assert.That(SaidOf(station, id), Does.Contain("asks for no client certificate"));

        }

        #endregion

    }

}
