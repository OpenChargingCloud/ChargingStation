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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.Certificates;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A ws:// or wss:// connection says which version of HTTP its WebSocket
    /// is opened over - "1.1", "2", "3", or "auto", which it is where it says
    /// nothing: the station then offers HTTP/2 and HTTP/1.1 and its server
    /// chooses, and goes over HTTP/3 once its server said it is reached over
    /// it. Named, a version is that and nothing else. The station says which
    /// one it came to.
    /// </summary>
    [TestFixture]
    public class DiallingOverHTTPVersionsTests
    {

        #region (class) CSMS

        /// <summary>
        /// A back end that answers a BootNotification and says which version
        /// of HTTP each WebSocket it took was opened over.
        /// </summary>
        private sealed class CSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>       received    = new ();

            public WebSocketServer               Server      { get; }
            public IPPort                        Port        { get; }
            public ConcurrentQueue<HTTPVersion>  OpenedOver  { get; } = new ();

            public CSMS(X509Certificate2 Certificate, Boolean AcceptHTTP2, Boolean AcceptHTTP3 = false)
            {

                this.Port  = IPPort.Parse(AcceptHTTP3 ? APortFreeForBoth() : TestPorts.Free());

                Server = new WebSocketServer(HTTPPort:                   Port,
                                             RequireAuthentication:      false,
                                             ServerCertificateSelector:  (tcpServer, tcpClient) => Certificate,
                                             AutoStart:                  true) {
                             AcceptHTTP2 = AcceptHTTP2,
                             AcceptHTTP3 = AcceptHTTP3
                         };

                if (AcceptHTTP3)
                {
                    // The listener tells of its start in its own time.
                    SpinWait.SpinUntil(() => Server.HTTP3Port is not null || Server.WhyNoHTTP3 is not null, TimeSpan.FromSeconds(10));
                    Assert.That(Server.HTTP3Port, Is.EqualTo(Port), "The back end takes no WebSockets over HTTP/3: " + Server.WhyNoHTTP3);
                }

                Server.OnNewWebSocketConnection += (timestamp, server, connection, sharedSubprotocols, selectedSubprotocol, eventTrackingId, cancellationToken) => {
                    OpenedOver.Enqueue(connection.HTTPVersion);
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
            /// A port that is free for UDP as for TCP, on both loopback
            /// addresses: Windows keeps ranges of ports for itself, for UDP and
            /// TCP apart, so the UDP port of the number of a free TCP port may
            /// not be there to have.
            /// </summary>
            private static UInt16 APortFreeForBoth()
            {

                for (var attempt = 0; attempt < 200; attempt++)
                {

                    var port = Random.Shared.Next(20000, 32768);

                    try
                    {

                        using var udp4 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback,     port));
                        using var udp6 = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.IPv6Loopback, port));

                        var tcp4 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,     port);
                        tcp4.Start();
                        tcp4.Stop();

                        var tcp6 = new System.Net.Sockets.TcpListener(System.Net.IPAddress.IPv6Loopback, port);
                        tcp6.Start();
                        tcp6.Stop();

                        return (UInt16) port;

                    }
                    catch (System.Net.Sockets.SocketException)
                    { }

                }

                throw new InvalidOperationException("No port is free for UDP and TCP.");

            }

            public Int32 Boots
                => received.Count(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "BootNotification");

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             csms;
        private X509Certificate2  ca         = default!;
        private X509Certificate2  leaf       = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {

            directory  = TestStations.TemporaryDirectory("dialling-httpversions");
            station    = TestStations.New(directory, TestStations.Offline);

            using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var caRequest   = new CertificateRequest("CN=The operator's CA", caKey, HashAlgorithmName.SHA256);
            caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            ca              = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            using var key   = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request     = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            var names       = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));
            request.CertificateExtensions.Add(names.Build());

            using var issued   = request.Create(ca, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30), Guid.NewGuid().ToByteArray());
            using var withKey  = issued.CopyWithPrivateKey(key);
            leaf               = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            // The CA of the back end, kept by the station for its OCPP connections.
            Assert.That(station.Certificates.Import(System.Text.Encoding.ASCII.GetBytes(ca.ExportCertificatePem()),
                                                    CertificateKind.TLSRoot, null, "The operator's CA", null, out _, out var error),
                        Is.True, error);

        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (csms is not null)
            {
                // What a back end said of itself is kept for the process, and this one is gone.
                WebSocketAlternativeServices.Forget(URL.Parse(Address));
                await csms.DisposeAsync();
            }

            ca.  Dispose();
            leaf.Dispose();

            station  = null;
            csms     = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private String Address
            => $"wss://127.0.0.1:{csms!.Port}/cs001";

        private void ACSMS(Boolean AcceptHTTP2, Boolean AcceptHTTP3 = false)
            => csms = new CSMS(leaf, AcceptHTTP2, AcceptHTTP3);

        private async Task Start()
            => station = await TestStations.Started(station!, directory);

        private String Added(String? HTTPVersion)
        {

            Assert.That(station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              HTTPVersion: HTTPVersion),
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
            => station!.HTTPVersionInUseAt(URL.Parse(Address));

        private static String Steps(JObject Result)
            => String.Join("\n", Result["steps"]!.Select(step => step.Value<String>("text")));

        #endregion


        #region AConnectionOpensItsWebSocketOverWhatItNames_OrWhatItsServerChooses(Named, AcceptHTTP2, Expected)

        /// <summary>
        /// Saying nothing is "auto": a back end that takes HTTP/2 is spoken to
        /// over it, one that does not over HTTP/1.1. A version that is named
        /// is that one, whatever else the back end would take. OCPP goes over
        /// either.
        /// </summary>
        [TestCase(null,    true,   "2")]
        [TestCase("auto",  true,   "2")]
        [TestCase(null,    false,  "1.1")]
        [TestCase("2",     true,   "2")]
        [TestCase("1.1",   true,   "1.1")]
        [TestCase("1.1",   false,  "1.1")]
        public async Task AConnectionOpensItsWebSocketOverWhatItNames_OrWhatItsServerChooses(String? Named, Boolean AcceptHTTP2, String Expected)
        {

            ACSMS(AcceptHTTP2);

            var id = Added(Named);

            await Start();

            await Until(() => IsConnected(id),     "Connecting",                      () => SaidOf(id));
            await Until(() => csms!.Boots > 0,     "A BootNotification over wss://");

            Assert.Multiple(() => {

                Assert.That(csms!.OpenedOver,  Is.EqualTo(new[] { Expected == "2" ? HTTPVersion.HTTP_2_0 : HTTPVersion.HTTP_1_1 }),  "what the back end says the WebSocket was opened over");
                Assert.That(InUse,             Is.EqualTo(Expected),                                                                "what the station says it was opened over");

                Assert.That(ConnectionEntry.AsText(Entry.HTTPVersion),                         Is.EqualTo(Named ?? "auto"));
                Assert.That(Entry.ToJSON().Value<String>(ConnectionEntry.HTTPVersionKey),      Is.EqualTo(Named is null or "auto" ? null : Named),  "what is written down");

            });

        }

        #endregion

        #region AConnectionThatNamesHTTP3OpensItsWebSocketOverIt()

        /// <summary>
        /// Over QUIC, on the UDP port of the number of the back end's TCP
        /// port, with the back end believed by the root the station keeps for
        /// it - and OCPP goes over it.
        /// </summary>
        [Test]
        public async Task AConnectionThatNamesHTTP3OpensItsWebSocketOverIt()
        {

            ACSMS(AcceptHTTP2: true, AcceptHTTP3: true);

            var id = Added("3");

            await Start();

            await Until(() => IsConnected(id),     "Connecting",                      () => SaidOf(id));
            await Until(() => csms!.Boots > 0,     "A BootNotification over HTTP/3");

            Assert.Multiple(() => {
                Assert.That(csms!.OpenedOver,  Is.EqualTo(new[] { HTTPVersion.HTTP_3_0 }),  "what the back end says the WebSocket was opened over");
                Assert.That(InUse,             Is.EqualTo("3"),                             "what the station says it was opened over");
                Assert.That(Entry.ToJSON().Value<String>(ConnectionEntry.HTTPVersionKey),  Is.EqualTo("3"),  "what is written down");
            });

        }

        #endregion

        #region AOneTimePasswordIsBoundToAConnectionThatNamesHTTP3()

        /// <summary>
        /// A one-time password that is to be bound to the TLS session is, on
        /// a connection that names HTTP/3: the station makes it with keying
        /// material of the connection, says it is bound, and a back end that
        /// asks for the binding lets it in. Nothing is said against the
        /// connection where it is written down.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordIsBoundToAConnectionThatNamesHTTP3()
        {

            const String secret = "0123456789abcdefghijklmnopqrstuv";

            ACSMS(AcceptHTTP2: true, AcceptHTTP3: true);

            csms!.Server.RequireAuthentication      = true;
            csms. Server.ClientTOTPConfig["cs001"]  = new TOTPConfig(secret, UseTLSExporterMaterial: true);

            var sent = new ConcurrentQueue<HTTPTOTPAuthentication>();

            csms.Server.OnHTTPRequest += (timestamp, server, request, cancellationToken) => {
                if (request.Authorization is HTTPTOTPAuthentication totp)
                    sent.Enqueue(totp);
                return Task.CompletedTask;
            };

            Assert.That(station!.Connections.TryAddAuthentication("CSMS one-time password", "totp", "cs001", secret,
                                                                  out var login, out var error,
                                                                  TLSChannelBinding: true),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, login, null,
                                                             out var id, out error,
                                                             HTTPVersion: "3"),
                        Is.True, error);

            Assert.That(Entry.Warnings, Has.None.Contains("TLS session"),
                        "A password that can be bound on this connection was complained about.");

            await Start();

            await Until(() => IsConnected(id!),    "Connecting",                      () => SaidOf(id!));
            await Until(() => csms!.Boots > 0,     "A BootNotification over HTTP/3");

            var (before, now, next, _, _) = TOTPGenerator.GenerateTOTPs(secret);

            Assert.Multiple(() => {
                Assert.That(csms!.OpenedOver,   Is.EqualTo(new[] { HTTPVersion.HTTP_3_0 }));
                Assert.That(sent.First().Type,  Is.EqualTo(TOTPHTTPHeaderType.TLSChannelBinding),  "the password does not say it is bound");
                Assert.That(new[] { before, now, next },  Has.None.EqualTo(sent.First().TOTP),     "the password sent as bound is the unbound one");
            });

        }

        #endregion

        #region AOneTimePasswordToBeBoundIsNotDialledWhereTheVersionIsNotHTTP3(Named)

        /// <summary>
        /// Left the choice of the version, or told HTTP/1.1 or HTTP/2, the
        /// connection may be, or is, one whose TLS exports nothing: it is not
        /// dialled, and says that naming HTTP/3 would do.
        /// </summary>
        [TestCase(null)]
        [TestCase("1.1")]
        [TestCase("2")]
        public async Task AOneTimePasswordToBeBoundIsNotDialledWhereTheVersionIsNotHTTP3(String? Named)
        {

            // Nothing is asked of it, so it need not take HTTP/3 - whose UDP port
            // is not always there to have.
            ACSMS(AcceptHTTP2: true);

            Assert.That(station!.Connections.TryAddAuthentication("CSMS one-time password", "totp", "cs001", "0123456789abcdefghijklmnopqrstuv",
                                                                  out var login, out var error,
                                                                  TLSChannelBinding: true),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("The CSMS", Address, "CSMS", true, login, null,
                                                             out var id, out error,
                                                             HTTPVersion: Named),
                        Is.True, error);

            Assert.That(Entry.Warnings, Has.Some.Contains("names HTTP/3"),
                        "Where it is written down, nothing says what would do.");

            await Start();

            await Until(() => SaidOf(id!) != "nothing yet", "Saying what came of it");

            Assert.Multiple(() => {
                Assert.That(SaidOf(id!),       Does.StartWith("Not dialled"));
                Assert.That(SaidOf(id!),       Does.Contain("Name HTTP/3"));
                Assert.That(csms!.OpenedOver,  Is.Empty);
            });

        }

        #endregion

        #region NothingButHTTP3WillDo_AndTheBackEndDoesNotTakeIt()

        [Test]
        public async Task NothingButHTTP3WillDo_AndTheBackEndDoesNotTakeIt()
        {

            ACSMS(AcceptHTTP2: true, AcceptHTTP3: false);

            var id = Added("3");

            await Start();

            await Until(() => station!.ConnectionStates.TryGetValue(id, out var state) &&
                              state.Status is not ConnectionStatus.Connected and not ConnectionStatus.NotDialled,
                        "Giving up", () => SaidOf(id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),   Is.False,  "It was reached some other way.");
                Assert.That(csms!.OpenedOver,  Is.Empty,  "A WebSocket was opened over something else.");
                Assert.That(InUse,             Is.Null);
            });

        }

        #endregion

        #region ABackEndTheStationDoesNotBelieveIsNotAskedOverHTTP3Either()

        /// <summary>
        /// What its server is held to is the same over HTTP/3: a back end
        /// whose certificate no root of the station stands behind is not asked.
        /// </summary>
        [Test]
        public async Task ABackEndTheStationDoesNotBelieveIsNotAskedOverHTTP3Either()
        {

            using var key      = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request        = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256);
            var names          = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(System.Net.IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());

            using var signed   = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            using var someone  = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pfx, "x"), "x", X509KeyStorageFlags.UserKeySet);

            csms = new CSMS(someone, AcceptHTTP2: true, AcceptHTTP3: true);

            var id = Added("3");

            await Start();

            await Until(() => station!.ConnectionStates.TryGetValue(id, out var state) &&
                              state.Status is not ConnectionStatus.Connected and not ConnectionStatus.NotDialled,
                        "Giving up", () => SaidOf(id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),   Is.False);
                Assert.That(csms!.OpenedOver,  Is.Empty,  "A back end that was not believed was asked.");
                Assert.That(csms.Boots,        Is.Zero);
            });

        }

        #endregion

        #region WhereItsServerChoosesItIsHTTP3OnceTheBackEndSaidSo()

        /// <summary>
        /// "auto" comes to HTTP/3 by the back end's own word (Alt-Svc): the
        /// first time it is asked over TCP, the next time over HTTP/3 - and
        /// the test of a connection says so.
        /// </summary>
        [Test]
        public async Task WhereItsServerChoosesItIsHTTP3OnceTheBackEndSaidSo()
        {

            ACSMS(AcceptHTTP2: true, AcceptHTTP3: true);

            var first   = await station!.TestConnection("The CSMS", Address, "CSMS");
            var second  = await station. TestConnection("The CSMS", Address, "CSMS");
            var named   = await station. TestConnection("The CSMS", Address, "CSMS", HTTPVersion: "3");
            var other   = await station. TestConnection("The CSMS", Address, "CSMS", HTTPVersion: "2");

            Assert.Multiple(() => {

                Assert.That(first.Value<Boolean>("ok"),   Is.True,  Steps(first));
                Assert.That(Steps(first),                 Does.Contain("over HTTP/2 or HTTP/1.1 as the server chooses"));
                Assert.That(Steps(first),                 Does.Contain("The WebSocket was opened over HTTP/2, which the server chose."));

                Assert.That(second.Value<Boolean>("ok"),  Is.True,  Steps(second));
                Assert.That(Steps(second),                Does.Contain("over HTTP/3, where the server said before that it is reached"));
                Assert.That(Steps(second),                Does.Contain("The WebSocket was opened over HTTP/3, which the server chose."));

                Assert.That(named.Value<Boolean>("ok"),   Is.True,  Steps(named));
                Assert.That(Steps(named),                 Does.Contain("over HTTP/3 and nothing else"));
                Assert.That(Steps(named),                 Does.Contain("The WebSocket was opened over HTTP/3."));

                // A connection that names its version is not moved by what the back end said.
                Assert.That(other.Value<Boolean>("ok"),   Is.True,  Steps(other));
                Assert.That(Steps(other),                 Does.Contain("The WebSocket was opened over HTTP/2."));

                Assert.That(csms!.OpenedOver,  Is.EqualTo(new[] { HTTPVersion.HTTP_2_0, HTTPVersion.HTTP_3_0, HTTPVersion.HTTP_3_0, HTTPVersion.HTTP_2_0 }));

            });

        }

        #endregion

        #region NothingButHTTP2WillDo_AndTheBackEndDoesNotTakeIt()

        [Test]
        public async Task NothingButHTTP2WillDo_AndTheBackEndDoesNotTakeIt()
        {

            ACSMS(AcceptHTTP2: false);

            var id = Added("2");

            await Start();

            await Until(() => station!.ConnectionStates.TryGetValue(id, out var state) &&
                              state.Status is not ConnectionStatus.Connected and not ConnectionStatus.NotDialled,
                        "Giving up", () => SaidOf(id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),       Is.False);
                Assert.That(csms!.OpenedOver,      Is.Empty,  "The WebSocket was opened over something else.");
                Assert.That(InUse,                 Is.Null);
            });

        }

        #endregion

        #region AChangeOfTheVersionDialsAgain_AndAChangeThatDoesNotSayKeepsIt()

        [Test]
        public async Task AChangeOfTheVersionDialsAgain_AndAChangeThatDoesNotSayKeepsIt()
        {

            ACSMS(AcceptHTTP2: true);

            var id = Added(null);

            await Start();
            await Until(() => IsConnected(id) && csms!.OpenedOver.Count == 1, "Connecting", () => SaidOf(id));

            // Named now: dialled again, over that.
            Assert.That(station!.Connections.TryUpdateConnection(id, "The CSMS", Address, "CSMS", true, null, null, out var error, HTTPVersion: "1.1"), Is.True, error);

            await Until(() => csms!.OpenedOver.Count == 2 && IsConnected(id) && InUse == "1.1", "Connecting again over HTTP/1.1", () => SaidOf(id));

            // A change of something else that says nothing of it: kept, and not dialled again.
            Assert.That(station.Connections.TryUpdateConnection(id, "Still the CSMS", Address, "CSMS", true, null, null, out error), Is.True, error);

            await Task.Delay(TimeSpan.FromSeconds(1));

            // And read again as it was written.
            Assert.That(ConnectionEntry.TryParse(Entry.ToJSON(), out var readAgain, out error), Is.True, error);

            Assert.Multiple(() => {
                Assert.That(csms!.OpenedOver,        Is.EqualTo(new[] { HTTPVersion.HTTP_2_0, HTTPVersion.HTTP_1_1 }));
                Assert.That(Entry.Description,       Is.EqualTo("Still the CSMS"));
                Assert.That(Entry.HTTPVersion,       Is.EqualTo(WebSocketHTTPVersion.HTTP1_1));
                Assert.That(readAgain?.HTTPVersion,  Is.EqualTo(WebSocketHTTPVersion.HTTP1_1));
            });

        }

        #endregion

        #region WhatIsNoVersionIsRefusedInWords(Named, Address, Expected)

        [TestCase("3",         "ws://csms.example.org/cs001",   "a WebSocket over HTTP/3 needs a wss:// address")]
        [TestCase("h2",        "wss://csms.example.org/cs001",  "is no version of HTTP a WebSocket is opened over")]
        [TestCase("1.0",       "wss://csms.example.org/cs001",  "is no version of HTTP a WebSocket is opened over")]
        [TestCase("2",         "ssh://csms.example.org/",       "an ssh:// connection speaks no HTTP")]
        public void WhatIsNoVersionIsRefusedInWords(String Named, String Address, String Expected)
        {

            var parsed  = ConnectionEntry.TryParseHTTPVersion(URL.Parse(Address), Named, out var version, out var error);
            var added   = station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", false, null, null, out _, out var addError, HTTPVersion: Named);

            Assert.Multiple(() => {
                Assert.That(parsed,     Is.False);
                Assert.That(version,    Is.Null);
                Assert.That(error,      Does.Contain(Expected));
                Assert.That(added,      Is.False);
                Assert.That(addError,   Does.Contain(Expected));
                Assert.That(station.Connections.Connections,  Is.Empty);
            });

        }

        #endregion

        #region ATestSaysWhichVersionItCameTo()

        [Test]
        public async Task ATestSaysWhichVersionItCameTo()
        {

            ACSMS(AcceptHTTP2: true);

            var chosen  = await station!.TestConnection("The CSMS", Address, "CSMS");
            var named   = await station. TestConnection("The CSMS", Address, "CSMS", HTTPVersion: "1.1");
            var wrong   = await station. TestConnection("The CSMS", Address, "CSMS", HTTPVersion: "h2");

            Assert.Multiple(() => {

                Assert.That(chosen.Value<Boolean>("ok"),  Is.True,  Steps(chosen));
                Assert.That(Steps(chosen),                Does.Contain("over HTTP/2 or HTTP/1.1 as the server chooses"));
                Assert.That(Steps(chosen),                Does.Contain("The WebSocket was opened over HTTP/2, which the server chose."));

                Assert.That(named.Value<Boolean>("ok"),   Is.True,  Steps(named));
                Assert.That(Steps(named),                 Does.Contain("over HTTP/1.1 and nothing else"));
                Assert.That(Steps(named),                 Does.Contain("The WebSocket was opened over HTTP/1.1."));

                Assert.That(wrong.Value<Boolean>("ok"),   Is.False);
                Assert.That(Steps(wrong),                 Does.Contain("is no version of HTTP a WebSocket is opened over"));

                Assert.That(station.Connections.Connections,  Is.Empty,  "a test wrote something down");

            });

        }

        #endregion

        #region TheWebInterface_TakesTheVersion_AndSaysWhichOneItCameTo()

        [Test]
        public async Task TheWebInterface_TakesTheVersion_AndSaysWhichOneItCameTo()
        {

            ACSMS(AcceptHTTP2: true);

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

            JObject Connection(String? HTTPVersion, String Description = "The CSMS", String? Id = null)
            {

                var json = new JObject(new JProperty("description",     Description),
                                       new JProperty("url",             Address),
                                       new JProperty("connectionType",  "CSMS"),
                                       new JProperty("autoConnect",     true));

                if (Id          is not null)  json.Add(new JProperty("id",           Id));
                if (HTTPVersion is not null)  json.Add(new JProperty("httpVersion",  HTTPVersion));

                return json;

            }

            var refused      = await http.PostAsync("api/v1/configuration/connections", Body(Connection("h2")));
            var refusal      = await refused.Content.ReadAsStringAsync();

            var added        = await http.PostAsync("api/v1/configuration/connections", Body(Connection("2")));
            var addedJSON    = JObject.Parse(await added.Content.ReadAsStringAsync());
            var id           = addedJSON.Value<String>("id")!;

            await Until(() => IsConnected(id), "Connecting", () => SaidOf(id));

            // A save of something else, which says nothing of the version.
            var updated      = await http.PostAsync("api/v1/configuration/connections/update", Body(Connection(null, "Still the CSMS", id)));
            var updatedJSON  = JObject.Parse(await updated.Content.ReadAsStringAsync());
            var shown        = updatedJSON["connections"]?.OfType<JObject>().FirstOrDefault() ??
                               updatedJSON["connections"]?["connections"]?.OfType<JObject>().FirstOrDefault();

            var tested       = await http.PostAsync("api/v1/configuration/connections/test", Body(Connection("1.1")));
            var testedJSON   = JObject.Parse(await tested.Content.ReadAsStringAsync());

            Assert.Multiple(() => {

                Assert.That((Int32) refused.StatusCode,             Is.EqualTo(400));
                Assert.That(refusal,                                Does.Contain("is no version of HTTP a WebSocket is opened over"));

                Assert.That(added.IsSuccessStatusCode,              Is.True,  addedJSON.ToString());
                Assert.That(updated.IsSuccessStatusCode,            Is.True,  updatedJSON.ToString());

                Assert.That(shown?.Value<String>("description"),       Is.EqualTo("Still the CSMS"),  updatedJSON.ToString());
                Assert.That(shown?.Value<String>("httpVersion"),       Is.EqualTo("2"),               "the version it names");
                Assert.That(shown?.Value<String>("httpVersionInUse"),  Is.EqualTo("2"),               "the version it came to");

                Assert.That(tested.IsSuccessStatusCode,             Is.True,  testedJSON.ToString());
                Assert.That(Steps(testedJSON),                      Does.Contain("The WebSocket was opened over HTTP/1.1."));

                Assert.That(csms!.OpenedOver.First(),               Is.EqualTo(HTTPVersion.HTTP_2_0));

            });

        }

        #endregion

    }

}
