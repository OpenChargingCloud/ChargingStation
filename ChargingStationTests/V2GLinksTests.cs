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

using System.Net;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using cloud.charging.open.protocols.ISO15118.Sap;
using cloud.charging.open.protocols.ISO15118.SDP.Messages;
using cloud.charging.open.protocols.ISO15118.SDP.Server;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.StateMachines.Iso2;
using cloud.charging.open.protocols.ISO15118.Timing;
using cloud.charging.open.protocols.ISO15118.Transport;
using cloud.charging.open.protocols.ISO15118.V2GTP;
using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A V2G link below each EVSE: the second EVSE's comes with defaults of its
    /// own, is switched on without touching the first one's, cannot take what
    /// the first one holds, and goes when its EVSE goes. And what a link is
    /// doing - SDP, the vehicle connected and over what - on the EVSEs page.
    /// </summary>
    /// <remarks>
    /// No link here answers SDP or listens for SLAC, and every endpoint is on a
    /// port the system hands out: nothing goes looking for a powerline modem,
    /// and nothing joins a multicast group of the machine it runs on.
    /// </remarks>
    [TestFixture]
    public class V2GLinksTests : AChargingStationTests
    {

        #region Data

        private const String Resource = "/api/v1/configuration/v2g";

        #endregion

        #region A station with two EVSEs, and a link below the first

        protected override JObject Configuration
            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("groups", new JObject(
                           new JProperty("legal", new JObject(
                               new JProperty("enabled", false)
                           ))
                       ))
                   )),
                   new JProperty("evses", new JArray(
                       new JObject(
                           new JProperty("id",                 1),
                           new JProperty("connectors",         new JArray("sType2")),
                           new JProperty("maxPower_kW",        22.0),
                           new JProperty("physicalReference",  "A")
                       ),
                       new JObject(
                           new JProperty("id",                 2),
                           new JProperty("connectors",         new JArray("cCCS2")),
                           new JProperty("maxPower_kW",        150.0),
                           new JProperty("physicalReference",  "B")
                       )
                   )),
                   new JProperty("v2g", new JObject(
                       new JProperty("links", new JArray(
                           new JObject(
                               new JProperty("evse",     1),
                               new JProperty("enabled",  true),
                               new JProperty("port",     0),
                               new JProperty("sdp",      false),
                               new JProperty("slac",     "none")
                           )
                       ))
                   ))
               );

        #endregion

        #region (private) Helpers

        private static JObject Link(JObject V2G, Byte EVSE)

            => (V2G["links"] as JArray)!.
                   OfType<JObject>().
                   First(link => link.Value<Byte>("evse") == EVSE);

        private JObject? V2GOfEVSE(Byte EVSE)

            => (Station.EVSEsStatusJSON()["evses"] as JArray)!.
                   OfType<JObject>().
                   First(evse => evse.Value<Byte>("id") == EVSE)["v2g"] as JObject;

        private JObject FileSection()

            => (JObject.Parse(File.ReadAllText(Path.Combine(Directory, "configuration.json")))["v2g"] as JObject)!;

        private static async Task Until(Func<Boolean> What, String Failure)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(15);
            while (!What())
            {
                if (DateTime.UtcNow > until)
                    Assert.Fail(Failure);
                await Task.Delay(25);
            }
        }

        #endregion


        #region The second EVSE's link

        [Test]
        public void TheSecondEVSEsLinkHasDefaultsOfItsOwn()
        {

            var second = Station.V2GOptionsOf(2);

            Assert.Multiple(() => {
                Assert.That(second.Enabled,  Is.False, "nobody switched it on");
                Assert.That(second.V2GPort,  Is.EqualTo((UInt16) (V2GOptions.DefaultV2GPort + 1)));
                Assert.That(second.EVSEId,   Is.EqualTo("DE*GEF*E0001*2"));
            });

        }

        [Test]
        public void TheFirstLinkIsUpAndTheSecondIsNot()
        {

            Assert.Multiple(() => {
                Assert.That(Station.V2GLinkOf(1),             Is.Not.Null);
                Assert.That(Station.V2GLinkOf(2),             Is.Null);
                Assert.That(V2GOfEVSE(1)!.Value<Boolean>("up"), Is.True);
                Assert.That(V2GOfEVSE(1)!.Value<String>("endpoint"), Is.Not.Null.And.Not.Empty);
                Assert.That(V2GOfEVSE(2),                     Is.Null, "an EVSE that is to have no link says none");
            });

        }

        [Test]
        public async Task SwitchingOnTheSecondLeavesTheFirstAlone()
        {

            var first      = Station.V2GLinkOf(1);

            using var http = await SignedIn();

            var response   = await http.PutAsync(Resource, JSONBody(
                                                               new JProperty("evse",     2),
                                                               new JProperty("enabled",  true),
                                                               new JProperty("port",     0),
                                                               new JProperty("sdp",      false),
                                                               new JProperty("slac",     "none")
                                                           ));

            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());

            var answered   = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(Station.V2GLinkOf(2),                         Is.Not.Null, "the second link did not come up");
                Assert.That(Station.V2GLinkOf(1),                         Is.SameAs(first), "the first link was taken down for a change to the second");
                Assert.That(Link(answered, 2).Value<Boolean>("enabled"),  Is.True);
                Assert.That(Link(answered, 2)["link"]!.Type,              Is.EqualTo(JTokenType.Object));
                Assert.That(Link(answered, 2).Value<String>("label"),     Is.EqualTo("B"));
                Assert.That(FileSection()["links"]!.Select(link => link.Value<Int32>("evse")), Is.EqualTo(new[] { 1, 2 }));
            });

        }

        [Test]
        public async Task ALinkThatWouldTakeThePortOfAnotherIsRefused()
        {

            using var http = await SignedIn();

            var port = (Int32) TestPorts.Free();

            var first  = await http.PutAsync(Resource, JSONBody(new JProperty("evse", 1), new JProperty("port", port)));
            Assert.That(first.IsSuccessStatusCode, Is.True, await first.Content.ReadAsStringAsync());

            var second = await http.PutAsync(Resource, JSONBody(
                                                           new JProperty("evse",     2),
                                                           new JProperty("enabled",  true),
                                                           new JProperty("port",     port),
                                                           new JProperty("sdp",      false),
                                                           new JProperty("slac",     "none")
                                                       ));

            var said = await second.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(second.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), said);
                Assert.That(said,               Does.Contain("EVSE A and EVSE B").And.Contain($"port {port}"));
                Assert.That(Station.V2GLinkOf(2),  Is.Null);
                Assert.That(FileSection()["links"]!.Select(link => link.Value<Int32>("evse")), Is.EqualTo(new[] { 1 }), "the refused change was written");
            });

        }

        [Test]
        public async Task AnEVSEThatIsNotThereHasNoCable()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(Resource, JSONBody(new JProperty("evse", 7), new JProperty("enabled", true)));

            Assert.Multiple(async () => {
                Assert.That(response.StatusCode,                          Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(await response.Content.ReadAsStringAsync(),   Does.Contain("no EVSE 7"));
            });

        }

        [Test]
        public async Task AnEVSEThatGoesTakesItsLinkAlong()
        {

            using var http = await SignedIn();

            await http.PutAsync(Resource, JSONBody(new JProperty("evse", 2), new JProperty("enabled", true), new JProperty("port", 0),
                                                   new JProperty("sdp", false), new JProperty("slac", "none")));

            Assert.That(Station.V2GLinkOf(2), Is.Not.Null, "the second link did not come up, so there is nothing to see go");

            var onlyTheFirst = new JObject(new JProperty("evses", new JArray(Station.EVSEs.Where(evse => evse.Id == 1).Select(evse => evse.ToJSON()))));

            Assert.That(Station.TryUpdateEVSEConfiguration(onlyTheFirst, _ => true, out _, out var error, out _), Is.True, error);

            await Until(() => Station.V2GLinkOf(2) is null, "EVSE 2 is gone, and the link below it is still up.");

            Assert.That(Station.V2GLinkOf(1), Is.Not.Null, "the first EVSE's link went too");

        }

        #endregion

        #region What a link is doing

        [Test]
        public async Task AVehicleOnTheEndpointIsSaidWithItsProtocolAndThenHowItsSessionEnded()
        {

            var link = Station.V2GLinkOf(1)!;
            var port = link.V2GEndpoint!.Port;

            await using (var stream = await TcpV2GClient.ConnectAsync("::1", port))
            {

                await Until(() => V2GOfEVSE(1)?["connection"] is JObject, "the vehicle on the endpoint was not said");

                Assert.That(V2GOfEVSE(1)!["connection"]!.Value<String>("from"), Does.Contain("::1"));
                Assert.That(V2GOfEVSE(1)!["connection"]!["tls"]!.Type,           Is.EqualTo(JTokenType.Null), "plain TCP is said as TLS");

                await SapHandshake.RunEvccSideAsync(stream, ProtocolVariant.Iso15118_2, mode: PowerMode.Ac, transport: TransportSecurity.None);

                await Until(() => V2GOfEVSE(1)?["connection"]?.Value<String>("protocol") == "ISO 15118-2",
                            "what the vehicle and the station agreed on was not said");

                await new Evcc2(stream, PowerMode.Ac, TimeProvider.System, new TaskAsyncDelay(), TimeSpan.FromSeconds(10)).RunAsync();

            }

            await Until(() => V2GOfEVSE(1)?["lastSession"] is JObject, "how the session ended was not said");

            Assert.Multiple(() => {
                Assert.That(V2GOfEVSE(1)!["lastSession"]!.Value<String>("protocol"),  Is.EqualTo("ISO 15118-2"));
                Assert.That(V2GOfEVSE(1)!["connection"]!.Type,                         Is.EqualTo(JTokenType.Null), "a vehicle that left is still said to be there");
            });

        }

        [Test]
        public void WhatAVehicleAskedOverSDPAndWhatItWasToldAreSaid()
        {

            var link    = Station.V2GLinkOf(1)!;
            var vehicle = new IPEndPoint(IPAddress.Parse("fe80::1"), 51234);

            link.Heard   (new SDP_RequestReceivedEventArgs(DateTimeOffset.UtcNow, vehicle,
                                                           new V2GTP_Header(0x01, 0xFE, V2GTP_PayloadType.SdpRequest, SDP_Request.PayloadSize),
                                                           new SDP_Request(SDP_Security.NoTLS, SDP_TransportProtocol.TCP),
                                                           true, null));

            link.Answered(new SDP_ResponseSentEventArgs(DateTimeOffset.UtcNow, vehicle,
                                                        new SDP_Response(IPAddress.Parse("fe80::2"), 15118, SDP_Security.NoTLS, SDP_TransportProtocol.TCP),
                                                        28));

            var sdp = V2GOfEVSE(1)!["lastSDP"]!;

            Assert.Multiple(() => {
                Assert.That(sdp.Value<String>("from"),                Does.Contain("fe80::1"));
                Assert.That(sdp.Value<String>("security"),            Is.EqualTo("NoTLS"));
                Assert.That(sdp.Value<Boolean>("accepted"),           Is.True);
                Assert.That(sdp["answer"]!.Value<String>("address"),  Is.EqualTo("fe80::2"));
                Assert.That(sdp["answer"]!.Value<Int32>("port"),      Is.EqualTo(15118));
            });

        }

        [Test]
        public void ARefusedSDPRequestIsSaidWithWhy()
        {

            var link = Station.V2GLinkOf(1)!;

            link.Heard(new SDP_RequestReceivedEventArgs(DateTimeOffset.UtcNow, new IPEndPoint(IPAddress.Parse("fe80::3"), 51235),
                                                        new V2GTP_Header(0x01, 0xFE, V2GTP_PayloadType.SdpRequest, SDP_Request.PayloadSize),
                                                        new SDP_Request(SDP_Security.TLS, SDP_TransportProtocol.TCP),
                                                        false, "TLS asked of an endpoint without a certificate"));

            var sdp = V2GOfEVSE(1)!["lastSDP"]!;

            Assert.Multiple(() => {
                Assert.That(sdp.Value<Boolean>("accepted"),      Is.False);
                Assert.That(sdp.Value<String>("rejectReason"),   Does.Contain("without a certificate"));
                Assert.That(sdp["answer"]!.Type,                 Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

        #region Over TLS

        [Test]
        public async Task OverTLSTheProtocolAndTheCertificateAreSaid()
        {

            using var certificate = SelfSigned("CN=EVSE A of a test station");

            await using var link  = (await V2GLink.TryStart(new V2GOptions {
                                                                 Enabled            = true,
                                                                 V2GPort            = 0,
                                                                 SDP                = false,
                                                                 SlacTransport      = SlacTransportKind.None,
                                                                 ServerCertificate  = certificate
                                                             },
                                                             new EventLog(),
                                                             About: "EVSE A"))!;

            await using var stream = await TcpV2GClient.ConnectAsync("::1", link.V2GEndpoint!.Port, new TlsOptions {
                                                                         EnabledSslProtocols          = SslProtocols.Tls13,
                                                                         ServerCertificateValidation  = (_, _, _, _) => true
                                                                     });

            await Until(() => link.StatusJSON()["connection"]?["tls"] is JObject, "the TLS connection was not said");

            var status = link.StatusJSON();

            Assert.Multiple(() => {
                Assert.That(status["connection"]!["tls"]!.Value<String>("protocol"),  Is.EqualTo("Tls13"));
                Assert.That(status["connection"]!["tls"]!["vehicle"]!.Type,           Is.EqualTo(JTokenType.Null), "no vehicle certificate was presented");
                Assert.That(status["certificate"]!.Value<String>("subject"),          Is.EqualTo("CN=EVSE A of a test station"));
                Assert.That(status["certificate"]!.Value<String>("sha256"),           Has.Length.EqualTo(64));
            });

        }

        /// <summary>
        /// A certificate the platform's TLS can serve: made, then written out
        /// and read back, because a key that only ever lived in memory is one
        /// Windows' TLS will not sign a handshake with.
        /// </summary>
        private static X509Certificate2 SelfSigned(String Subject)
        {

            using var key     = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request       = new CertificateRequest(Subject, key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));

            using var made    = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));

            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);

        }

        #endregion

    }

}
