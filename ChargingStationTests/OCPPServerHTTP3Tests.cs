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

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The OCPP server of a station over HTTP/3: off unless the file says
    /// otherwise, since it opens a UDP port beside each TCP port; switched on
    /// and off while the station runs; never without TLS, and then said; and
    /// what a CSMS dials over that signs in with a one-time password bound to
    /// its TLS session, which nothing but HTTP/3 lets a server check.
    /// </summary>
    [TestFixture]
    public class OCPPServerHTTP3Tests
    {

        #region Data

        private const String                    Name       = "csms-01";

        private          String                 directory  = "";
        private readonly List<ChargingStation>  stations   = [];
        private readonly List<String>           said       = [];

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void Setup()
        {

            lock (said)
                said.Clear();

            directory = TestStations.TemporaryDirectory("ocpp-server-http3");
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

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        /// <summary>
        /// A port nobody has, over TCP and over UDP: HTTP/3 takes the UDP port
        /// of the number its server has over TCP.
        /// </summary>
        /// <remarks>
        /// Asked for on one address after the other, each let go before the
        /// next: Linux gives a UDP port held on a loopback address to nobody
        /// who asks for it on every interface, where Windows does.
        /// </remarks>
        private static IPPort APortFreeForBoth()
        {

            for (var attempt = 0; attempt < 200; attempt++)
            {

                var port = Random.Shared.Next(20000, 32768);

                try
                {

                    foreach (var address in new[] { System.Net.IPAddress.Loopback, System.Net.IPAddress.IPv6Loopback, System.Net.IPAddress.Any })
                    {
                        using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(address, port));
                    }

                    var tcp4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,     port);
                    tcp4.Start();
                    tcp4.Stop();

                    var tcp6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, port);
                    tcp6.Start();
                    tcp6.Stop();

                    var tcp  = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any,          port);
                    tcp.Start();
                    tcp.Stop();

                    return IPPort.Parse((UInt16) port);

                }
                catch (System.Net.Sockets.SocketException)
                { }

            }

            throw new InvalidOperationException("No port was free over both TCP and UDP.");

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
        /// A station with an OCPP server for OCPP 2.1 on the given port, with
        /// or without HTTPS, what its file says of HTTP/3 as given - not said
        /// at all for null -, told what the test writes down, and started.
        /// </summary>
        private async Task<ChargingStation> Started(IPPort                    Port,
                                                    Boolean                   HTTPS,
                                                    Boolean?                  HTTP3,
                                                    Action<ChargingStation>?  WriteDown = null)
        {

            var arguments = HTTPS
                                ? NodeArguments.Parse([ "--https", "--import-certificate", $"tlsServerIdentity={AnIdentityFile()}" ])
                                : NodeArguments.Parse([]);

            Assert.That(arguments.Problem, Is.Null);

            var station = await TestPorts.StartedOnFreshPorts(() => {

                var ocppServer     = new JObject(
                                         new JProperty("enabled", true),
                                         new JProperty("port",    Port.ToUInt16())
                                     );

                if (HTTP3.HasValue)
                    ocppServer.Add("http3", HTTP3.Value);

                var configuration  = TestStations.Offline;

                configuration.Add("ocppServer", ocppServer);

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
                    WriteDown?.Invoke(made);

                return made;

            });

            stations.Add(station);

            return station;

        }

        private static JObject ServerOf(ChargingStation Station)
            => (JObject) Station.OCPPServerJSON()["servers"]![0]!;

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
        /// A CSMS that dials the station over HTTP/3 and believes whatever
        /// certificate it is shown.
        /// </summary>
        private static WebSocketClient ACSMSOverHTTP3(IPPort                Port,
                                                      IHTTPAuthentication?  Authentication  = null,
                                                      TOTPConfig?           TOTP            = null)

            => new (
                   URL.Parse($"wss://127.0.0.1:{Port}/{Name}"),
                   HTTPAuthentication:          Authentication,
                   SecWebSocketProtocols:       [ "ocpp2.1" ],
                   RemoteCertificateValidator:  (sender, certificate, chain, client, errors) => TLSValidationResult.Success(),
                   TOTPConfig:                  TOTP,
                   TOTPLogin:                   TOTP is not null ? Name : null
               ) {
                   WebSocketOver = WebSocketHTTPVersion.HTTP3
               };

        #endregion


        #region TheSectionSaysHTTP3_AndNothingOfItWhereItWasNotSaid()

        [Test]
        public void TheSectionSaysHTTP3_AndNothingOfItWhereItWasNotSaid()
        {

            Assert.Multiple(() => {

                Assert.That(OCPPServerSection.TryParse(JObject.Parse("""{ "enabled": true, "port": 9443 }"""), out var silent, out var error), Is.True, error);
                Assert.That(silent!.TakesHTTP3,                    Is.False, "HTTP/3 is on although the file says nothing of it");
                Assert.That(silent.ToJSON().ContainsKey("http3"),  Is.False);

                Assert.That(OCPPServerSection.TryParse(JObject.Parse("""{ "enabled": true, "port": 9443, "http3": true }"""), out var on, out error), Is.True, error);
                Assert.That(on!.TakesHTTP3,                            Is.True);
                Assert.That(on.ToJSON().Value<Boolean>("http3"),       Is.True);
                Assert.That(on.ToString(),                             Does.Contain("HTTP/3"));

                Assert.That(OCPPServerSection.TryParse(JObject.Parse("""{ "enabled": true, "port": 9443, "http3": false }"""), out var off, out error), Is.True, error);
                Assert.That(off!.TakesHTTP3,                           Is.False);
                Assert.That(off.ToJSON().Value<Boolean?>("http3"),     Is.False);

                Assert.That(OCPPServerSection.TryParse(JObject.Parse("""{ "enabled": true, "port": 9443, "http3": "yes" }"""), out _, out error), Is.False);
                Assert.That(error, Does.Contain("http3"));

                Assert.That(OCPPServerSection.TryParse(JObject.Parse("""{ "enabled": true, "port": 9443, "http4": true }"""), out _, out error), Is.False);
                Assert.That(error, Does.Contain("'enabled', 'port', 'port16' and 'http3'"));

            });

        }

        #endregion

        #region NoUDPPortIsOpened_WhereNobodyAskedForHTTP3()

        /// <summary>
        /// A station with HTTPS and an OCPP server, and nothing said of
        /// HTTP/3: served over TCP, and no UDP port beside it.
        /// </summary>
        [Test]
        public async Task NoUDPPortIsOpened_WhereNobodyAskedForHTTP3()
        {

            var port     = APortFreeForBoth();
            var station  = await Started(port, HTTPS: true, HTTP3: null);

            // What a server that takes HTTP/3 would have done by now.
            await Task.Delay(1500);

            var json     = station.OCPPServerJSON();
            var server   = ServerOf(station);

            Assert.Multiple(() => {
                Assert.That(json.Value<Boolean>("tls"),             Is.True);
                Assert.That(json.Value<Boolean>("http3"),           Is.False);
                Assert.That(server.Value<Boolean>("served"),        Is.True);
                Assert.That(server["http3Port"]!.Type,              Is.EqualTo(JTokenType.Null), server.ToString());
                Assert.That(server.ContainsKey("http3Problem"),     Is.False, "a problem is said of something nobody asked for");
                Assert.That(Said(),                                 Does.Not.Contain("over HTTP/3"));
            });

            // The UDP port is nobody's: it can be had, on every address.
            Assert.DoesNotThrow(() => {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.ExclusiveAddressUse = true;
                udp.Client.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, port.ToUInt16()));
            }, "the UDP port of the OCPP server is taken although HTTP/3 is off");

        }

        #endregion

        #region AskedFor_TheServerTakesHTTP3_AndIsSwitchedOffAndOnWhileItRuns()

        [Test]
        public async Task AskedFor_TheServerTakesHTTP3_AndIsSwitchedOffAndOnWhileItRuns()
        {

            var port     = APortFreeForBoth();
            var station  = await Started(port, HTTPS: true, HTTP3: true);

            await Until(() => ServerOf(station).Value<UInt16?>("http3Port") == port.ToUInt16(),
                        "Taking WebSockets over HTTP/3", () => ServerOf(station).ToString() + "\n" + Said());

            Assert.Multiple(() => {
                Assert.That(station.OCPPServerJSON().Value<Boolean>("http3"),   Is.True);
                Assert.That(ServerOf(station).ContainsKey("http3Problem"),      Is.False, ServerOf(station).ToString());
                Assert.That(Said(),                                             Does.Contain($"over HTTP/3 as well, on UDP port {port}"));
            });

            // Off: written to the file, and the UDP port is let go of.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(
                                                                     new JProperty("enabled", true),
                                                                     new JProperty("port",    port.ToUInt16()),
                                                                     new JProperty("http3",   false)
                                                                 ),
                                                                 out var error, out _),
                        Is.True, error);

            await Until(() => ServerOf(station).Value<Boolean>("served"), "Being served again", () => ServerOf(station).ToString());
            await Task.Delay(1000);

            Assert.Multiple(() => {
                Assert.That(station.OCPPServerJSON().Value<Boolean>("http3"),  Is.False);
                Assert.That(ServerOf(station)["http3Port"]!.Type,              Is.EqualTo(JTokenType.Null), ServerOf(station).ToString());
                Assert.That(JObject.Parse(File.ReadAllText(station.ConfigFile.Path))["ocppServer"]!.Value<Boolean?>("http3"), Is.False);
            });

            // And on again, without a restart.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(
                                                                     new JProperty("enabled", true),
                                                                     new JProperty("port",    port.ToUInt16()),
                                                                     new JProperty("http3",   true)
                                                                 ),
                                                                 out error, out _),
                        Is.True, error);

            await Until(() => ServerOf(station).Value<UInt16?>("http3Port") == port.ToUInt16(),
                        "Taking WebSockets over HTTP/3 again", () => ServerOf(station).ToString() + "\n" + Said());

            Assert.That(JObject.Parse(File.ReadAllText(station.ConfigFile.Path))["ocppServer"]!.Value<Boolean?>("http3"), Is.True);

        }

        #endregion

        #region WithoutTLS_NoHTTP3IsTaken_AndThatIsSaid()

        [Test]
        public async Task WithoutTLS_NoHTTP3IsTaken_AndThatIsSaid()
        {

            var port     = APortFreeForBoth();
            var station  = await Started(port, HTTPS: false, HTTP3: true);

            var json     = station.OCPPServerJSON();
            var server   = ServerOf(station);

            Assert.Multiple(() => {
                Assert.That(json.Value<Boolean>("tls"),               Is.False);
                Assert.That(json.Value<Boolean>("http3"),             Is.True, "what the file says");
                Assert.That(server.Value<Boolean>("served"),          Is.True, "the server is not served over TCP either");
                Assert.That(server["http3Port"]!.Type,                Is.EqualTo(JTokenType.Null));
                Assert.That(server.Value<String>("http3Problem"),     Does.Contain("QUIC"));
                Assert.That(Said(),                                   Does.Contain("takes no WebSockets over HTTP/3"));
            });

        }

        #endregion

        #region ACSMSDialsInOverHTTP3()

        /// <summary>
        /// A CSMS that opens its WebSocket over HTTP/3 and signs in with its
        /// password is let in as over TCP.
        /// </summary>
        [Test]
        public async Task ACSMSDialsInOverHTTP3()
        {

            var port      = APortFreeForBoth();
            var password  = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            var id        = "";

            var station   = await Started(port, HTTPS: true, HTTP3: true, made => {

                Assert.That(made.Connections.TryAddAuthentication("What the CSMS signs in with", "basic", Name, password,
                                                                  out var login, out var error),
                            Is.True, error);

                Assert.That(made.Connections.TryAddConnection("The CSMS, dialling in", $"wss://127.0.0.1:{port}/{Name}", "CSMS", true, login, null,
                                                              out var written, out error,
                                                              Inbound: true),
                            Is.True, error);

                id = written!;

            });

            await Until(() => ServerOf(station).Value<UInt16?>("http3Port") == port.ToUInt16(),
                        "Taking WebSockets over HTTP/3", () => ServerOf(station).ToString() + "\n" + Said());

            await using var csms = ACSMSOverHTTP3(port, HTTPBasicAuthentication.Create(Name, password));

            var (_, response) = await csms.Connect();

            Assert.Multiple(() => {
                Assert.That(response.HTTPStatusCode,  Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in over HTTP/3.");
                Assert.That(csms.HTTPVersionInUse,    Is.EqualTo(HTTPVersion.HTTP_3_0));
            });

            await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

        }

        #endregion

        #region AOneTimePasswordBoundToItsTLSSession_SignsInOverHTTP3_AndNotOverTCP()

        /// <summary>
        /// What a station with HTTP/3 is for: a one-time password bound to the
        /// TLS session it is sent over is checked, and lets its CSMS in - and
        /// the same connection is refused where the server takes no HTTP/3,
        /// and says so rather than waiting for somebody who can not come.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordBoundToItsTLSSession_SignsInOverHTTP3_AndNotOverTCP()
        {

            const String secret = "0123456789abcdefghijklmnopqrstuv";

            var port      = APortFreeForBoth();
            var id        = "";

            var station   = await Started(port, HTTPS: true, HTTP3: true, made => {

                Assert.That(made.Connections.TryAddAuthentication("The one-time password of the CSMS", "totp", Name, secret,
                                                                  out var login, out var error,
                                                                  TLSChannelBinding: true),
                            Is.True, error);

                Assert.That(made.Connections.TryAddConnection("The CSMS, dialling in", $"wss://127.0.0.1:{port}/{Name}", "CSMS", true, login, null,
                                                              out var written, out error,
                                                              Inbound: true),
                            Is.True, error);

                id = written!;

            });

            await Until(() => ServerOf(station).Value<UInt16?>("http3Port") == port.ToUInt16(),
                        "Taking WebSockets over HTTP/3", () => ServerOf(station).ToString() + "\n" + Said());

            await Until(() => StatusOf(station, id) == "waiting", "Waiting for the CSMS", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            Assert.That(SaidOf(station, id), Does.Contain($"Waiting for '{Name}' to dial in"));

            // Bound, over HTTP/3: in.
            await using (var csms = ACSMSOverHTTP3(port, TOTP: new TOTPConfig(secret, UseTLSExporterMaterial: true)))
            {

                var (_, response) = await csms.Connect();

                Assert.That(response.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.SwitchingProtocols), "The CSMS was not let in with its bound one-time password.");

                await Until(() => StatusOf(station, id) == "connected", "Being connected", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

                await csms.Close();

            }

            await Until(() => StatusOf(station, id) == "waiting", "Waiting again", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            // The same password, not bound, opens nothing.
            await using (var unbound = ACSMSOverHTTP3(port, TOTP: new TOTPConfig(secret, UseTLSExporterMaterial: false)))
            {
                Assert.That((await unbound.Connect()).Item2.HTTPStatusCode, Is.EqualTo(HTTPStatusCode.Unauthorized), "a one-time password that is not bound was taken for one that is");
            }

            // HTTP/3 switched off: nobody can sign in that way, and that is said.
            Assert.That(station.TryUpdateOCPPServerConfiguration(new JObject(
                                                                     new JProperty("enabled", true),
                                                                     new JProperty("port",    port.ToUInt16()),
                                                                     new JProperty("http3",   false)
                                                                 ),
                                                                 out var problem, out _),
                        Is.True, problem);

            await Until(() => SaidOf(station, id).Contains("HTTP/3"), "Saying that it can not sign in", () => $"{StatusOf(station, id)}: {SaidOf(station, id)}");

            Assert.That(SaidOf(station, id), Does.Contain("can not sign in"));

        }

        #endregion

    }

}
