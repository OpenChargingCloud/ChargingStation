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

using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;
using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.PKI;

using cloud.charging.open.protocols.WWCP.Node.Certificates;

using cloud.charging.open.ChargingStation.EVSEs;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A SunSpec meter in the test's own process, speaking Modbus/TLS on a port
    /// the system handed out, with a PKI of its own: a root, a CA that issues
    /// the clients, and a client certificate for every SunSpec role.
    /// </summary>
    internal sealed class MeterOnTheBench : IAsyncDisposable
    {

        private readonly ModbusTlsFrontend        frontend;
        private readonly CancellationTokenSource  stopping;
        private readonly Task                     running;

        public SunSpecMeterDevice  Device  { get; }
        public UInt16              Port    { get; }

        private MeterOnTheBench(SunSpecMeterDevice Device, ModbusTlsFrontend Frontend, CancellationTokenSource Stopping, Task Running, UInt16 Port)
        {
            this.Device    = Device;
            this.frontend  = Frontend;
            this.stopping  = Stopping;
            this.running   = Running;
            this.Port      = Port;
        }

        /// <summary>
        /// A meter with the given serial number, its certificates from the PKI
        /// in the given directory, believing the clients of that PKI's root.
        /// </summary>
        public static async Task<MeterOnTheBench> Start(String PKI, String SerialNumber)
        {

            // Not simulated: what it measures stays what it measured at its
            // start, so that a test can hold the station to it.
            var device   = new SunSpecMeterDevice(SerialNumber, SunSpecMeterMode.ImportOnly, runSimulation: false);
            var server   = ServerCertificateChain.Load(Path.Combine(PKI, "server.chain.crt"), Path.Combine(PKI, "server.key"));
            var root     = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(PKI, "ca.crt"));

            var frontend = new ModbusTlsFrontend(
                               new ModbusTlsFrontendOptions(
                                   System.Net.IPAddress.Loopback, 0,
                                   ServerPfxPath:              null,
                                   ServerPfxPassword:          null,
                                   CaCertPath:                 null,
                                   HandshakeTimeout:           TimeSpan.FromSeconds(5),
                                   IdleTimeout:                TimeSpan.FromMinutes(1),
                                   WriteTimeout:               TimeSpan.FromSeconds(5),
                                   ServerCertificateSelector:  _ => server,
                                   ClientTrustAnchors:         () => [ root ]
                               ),
                               new SunSpecBackendFactory(device),
                               new AuthorizationPolicy(device)
                           );

            var stopping = new CancellationTokenSource();
            var running  = Task.Run(() => frontend.RunAsync(stopping.Token));
            var bound    = await frontend.BoundEndPoint;

            return new MeterOnTheBench(device, frontend, stopping, running, (UInt16) bound.Port);

        }

        public async ValueTask DisposeAsync()
        {

            stopping.Cancel();

            try { await running; } catch { }

            frontend.Dispose();
            Device.Dispose();
            stopping.Dispose();

        }

    }


    /// <summary>
    /// The meter of an EVSE, read over Modbus/TLS: what it measures on the EVSEs
    /// page and on the display; who the station is to it and whom it believes
    /// it to be, from the certificate store; and a meter that is moved or whose
    /// EVSE goes, while the other EVSE's meter is left alone.
    /// </summary>
    [TestFixture]
    public class MeterTests : AChargingStationTests
    {

        #region Data

        public const String  CardOfKnown  = "04A2112233";

        private String           pki     = default!;
        private MeterOnTheBench  meterA  = default!;
        private MeterOnTheBench  meterB  = default!;

        #endregion

        #region A station with two EVSEs, a card reader and an operator

        protected override JObject Configuration
            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   )),
                   new JProperty("evses", new JArray(
                       new JObject(
                           new JProperty("id",                  1),
                           new JProperty("connectors",          new JArray("sType2")),
                           new JProperty("maxPower_kW",         22.0),
                           new JProperty("physicalReference",   "A")
                       ),
                       new JObject(
                           new JProperty("id",                  2),
                           new JProperty("connectors",          new JArray("cCCS2")),
                           new JProperty("maxPower_kW",         150.0),
                           new JProperty("physicalReference",   "B")
                       )
                   )),
                   new JProperty("rfid", new JArray(
                       new JObject(
                           new JProperty("id",       "reader"),
                           new JProperty("kind",     "GraphDefined.FakeRFID"),
                           new JProperty("evse",     null),
                           new JProperty("enabled",  true)
                       )
                   )),
                   new JProperty("operator", new JObject(
                       new JProperty("name",      "Stadtwerke Musterstadt"),
                       new JProperty("language",  "de"),
                       new JProperty("emps",      new JArray(
                           new JObject(
                               new JProperty("id",             "emp-one"),
                               new JProperty("name",           "Elektro Mobil GmbH"),
                               new JProperty("tokenPrefixes",  new JArray("04A2"))
                           )
                       ))
                   ))
               );

        #endregion

        #region The meters on the bench, once for the fixture

        [OneTimeSetUp]
        public async Task StartTheMeters()
        {

            pki     = TestStations.TemporaryDirectory("meter-pki");

            await new ModbusPKI().BuildPKI(pki, "EnergyMeter01");

            meterA  = await MeterOnTheBench.Start(pki, "EnergyMeter01");
            meterB  = await MeterOnTheBench.Start(pki, "EnergyMeter02");

        }

        [OneTimeTearDown]
        public async Task StopTheMeters()
        {

            if (meterA is not null) await meterA.DisposeAsync();
            if (meterB is not null) await meterB.DisposeAsync();

            TestStations.Remove(pki);

        }

        [SetUp]
        public void ReadQuickly()
        {
            Station.MeterReadInterval   = TimeSpan.FromMilliseconds(100);
            Station.MeterRetryInterval  = TimeSpan.FromMilliseconds(200);
        }

        #endregion

        #region (private) Helpers

        /// <summary>
        /// A client certificate of the bench's PKI, with its key and its chain,
        /// as the store keeps a TLS identity.
        /// </summary>
        private void ImportIdentity(String Role)
        {

            using var leaf  = X509Certificate2.CreateFromPemFile(Path.Combine(pki, $"client-{Role}.crt"),
                                                                 Path.Combine(pki, $"client-{Role}.key"));

            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(Path.Combine(pki, $"client-{Role}.chain.crt"));

            var bag = new X509Certificate2Collection { leaf };

            foreach (var certificate in chain)
                if (certificate.Thumbprint != leaf.Thumbprint)
                    bag.Add(certificate);

            Assert.That(Station.Certificates.Import(bag.Export(X509ContentType.Pkcs12)!, CertificateKind.TLSIdentity, null, $"meter reader {Role}", out _, out var error),
                        Is.True, error);

        }

        /// <summary>
        /// The root of the bench's PKI, as a TLS root for the given usages.
        /// </summary>
        private void ImportRoot(params String[] Usages)

            => Assert.That(Station.Certificates.Import(File.ReadAllBytes(Path.Combine(pki, "ca.crt")), CertificateKind.TLSRoot, null, "meters' root",
                                                       Usages, out _, out var error),
                           Is.True, error);

        /// <summary>
        /// The EVSEs as they are, but with the meter of one read at the given
        /// endpoint - or nowhere.
        /// </summary>
        private void ReadTheMeterOf(Byte EVSE, MeterEndpoint? At)
        {

            var evses = Station.EVSEs.Select(evse => evse.Id == EVSE ? evse with { MeterEndpoint = At } : evse).
                                      Select(evse => evse.ToJSON());

            Assert.That(Station.TryUpdateEVSEConfiguration(new JObject(new JProperty("evses", new JArray(evses))), _ => true, out _, out var error, out _),
                        Is.True, error);

        }

        private JObject? MeterOfEVSE(Byte EVSE)

            => (Station.EVSEsStatusJSON()["evses"] as JArray)!.
                   OfType<JObject>().
                   First(evse => evse.Value<Byte>("id") == EVSE)["meter"] as JObject;

        private String? StateOf(Byte EVSE)
            => MeterOfEVSE(EVSE)?["read"]?.Type == JTokenType.Object
                   ? MeterOfEVSE(EVSE)!["read"]!.Value<String>("state")
                   : null;

        private static async Task Until(Func<Boolean> What, String Failure, Int32 Seconds = 15)
        {
            var until = DateTime.UtcNow + TimeSpan.FromSeconds(Seconds);
            while (!What())
            {
                if (DateTime.UtcNow > until)
                    Assert.Fail(Failure);
                await Task.Delay(25);
            }
        }

        #endregion


        #region What a meter measures

        [Test]
        public async Task AMeterThatIsReadIsOnTheEVSEsPageWithWhatItMeasuresAndOverWhat()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            var read = MeterOfEVSE(1)!["read"]!;

            Assert.That(read["reading"]!.Value<String>("manufacturer"),  Is.EqualTo("Vanaheimr"));
            Assert.That(read["reading"]!.Value<String>("serialNumber"),  Is.EqualTo("EnergyMeter01"));
            Assert.That(read["reading"]!["voltage_V"]!.Value<Decimal?>("L1"), Is.GreaterThan(100), "no voltage was said");
            Assert.That(read["reading"]!.Value<Decimal?>("frequency_Hz"), Is.InRange(45m, 65m));

            Assert.That(read["connection"]!.Value<String>("protocol"),               Does.StartWith("Tls1"));
            Assert.That(read["connection"]!["identity"]!.Value<String>("role"),      Is.EqualTo(SunSpecRoles.ReadOnly));
            Assert.That(read["connection"]!["meter"]!.Value<String>("subject"),      Does.Contain("EnergyMeter01"));
            Assert.That(read["connection"]!.Value<String>("judged"),                 Is.EqualTo("accepted"));

            Assert.That(StateOf(2), Is.Null, "the other EVSE's meter is said as read, and it is configured nowhere");

        }

        [Test]
        public async Task TheDisplayShowsWhatTheMeterMeasuresWhileACarCharges()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));

            await Until(() => Station.MeterOf(1)?.Reading is not null, "the meter was not read: " + MeterOfEVSE(1));

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var error), Is.True, error);

            var evse     = (Station.KioskJSON()["evses"] as JArray)!.OfType<JObject>().First(json => json.Value<Byte>("id") == 1);
            var measured = Math.Round(Station.MeterOf(1)!.Reading!.Power_W.All!.Value / 1000m, 1);

            Assert.That(evse.Value<Boolean>("powerIsSimulated"),  Is.False, "the display says the power is simulated, and a meter measures it");
            Assert.That(evse.Value<Decimal>("currentPower_kW"),   Is.EqualTo(measured));

        }

        #endregion

        #region Who the station is to the meter, and whom it believes it to be

        [Test]
        public async Task WithoutAnIdentityOfARoleItSaysSoAndReadsOnceOneIsImported()
        {

            // Long enough that only the store changing can make it try again
            // within the test.
            Station.MeterRetryInterval = TimeSpan.FromMinutes(10);

            ImportRoot(ChargingStation.MeterCertificateUsage);

            // A certificate without a role is no identity for a meter.
            ImportIdentity("NO-ROLE");

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));

            await Until(() => StateOf(1) == "noIdentity", "a station with no identity of a SunSpec role did not say so: " + MeterOfEVSE(1));

            Assert.That(MeterOfEVSE(1)!["read"]!.Value<String>("error"), Does.Contain("SunSpec role"));

            ImportIdentity(SunSpecRoles.ReadOnly);

            await Until(() => StateOf(1) == "reading", "the identity imported was not used: " + MeterOfEVSE(1));

        }

        [Test]
        public void TheLeastRoleThatMayReadIsShown()
        {

            ImportIdentity(SunSpecRoles.SuperAdministrator);
            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportIdentity(SunSpecRoles.GridService);

            using var identity = Station.MeterIdentity();

            Assert.That(identity?.Role, Is.EqualTo(SunSpecRoles.ReadOnly));

        }

        [Test]
        public async Task AMeterWhoseCertificateChainsToNoRootForMetersIsNotBelieved()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);

            // A root of the store, but one for the time servers only.
            ImportRoot("nts");

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));

            await Until(() => StateOf(1) == "untrusted", "a meter whose root is for something else was believed: " + MeterOfEVSE(1));

            ImportRoot(ChargingStation.MeterCertificateUsage);

            await Until(() => StateOf(1) == "reading", "a meter whose root is for meters now was not read: " + MeterOfEVSE(1));

        }

        [Test]
        public async Task AMeterReachedByANameItsCertificateIsNotForIsNotBelieved()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            // The meter itself, at an address that is not in its certificate:
            // 127.0.0.1 as IPv6 writes it.
            ReadTheMeterOf(1, new MeterEndpoint("::ffff:127.0.0.1", meterA.Port));

            await Until(() => StateOf(1) is "untrusted" or "unreachable", "a meter by the wrong name was read: " + MeterOfEVSE(1));

            Assume.That(StateOf(1), Is.EqualTo("untrusted"), "::ffff:127.0.0.1 does not reach the meter here");

            Assert.That(MeterOfEVSE(1)!["read"]!.Value<String>("error"), Does.Contain("not issued for"));

        }

        #endregion

        #region Meters that move, and EVSEs that go

        [Test]
        public async Task AMeterThatMovesIsReadWhereItIsNowAndTheOtherEVSEsIsLeftAlone()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));
            ReadTheMeterOf(2, new MeterEndpoint("localhost", meterB.Port));

            await Until(() => StateOf(1) == "reading" && StateOf(2) == "reading", "the two meters were not read: " + MeterOfEVSE(1) + MeterOfEVSE(2));

            var second = Station.MeterOf(2);

            ReadTheMeterOf(1, new MeterEndpoint("localhost", meterA.Port));

            await Until(() => Station.MeterOf(1)?.Endpoint.Host == "localhost", "the moved meter is still read where it was");
            await Until(() => StateOf(1) == "reading", "the moved meter is not read where it is now: " + MeterOfEVSE(1));

            Assert.That(Station.MeterOf(2), Is.SameAs(second), "moving the first EVSE's meter began the second's anew");

        }

        [Test]
        public async Task AnEVSEThatGoesTakesItsMeterAlong()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            ReadTheMeterOf(2, new MeterEndpoint("127.0.0.1", meterB.Port));

            await Until(() => StateOf(2) == "reading", "the meter was not read: " + MeterOfEVSE(2));

            var onlyTheFirst = new JObject(new JProperty("evses", new JArray(Station.EVSEs.Where(evse => evse.Id == 1).Select(evse => evse.ToJSON()))));

            Assert.That(Station.TryUpdateEVSEConfiguration(onlyTheFirst, _ => true, out _, out var error, out _), Is.True, error);

            await Until(() => Station.MeterOf(2) is null, "EVSE 2 is gone, and its meter is still read.");

        }

        [Test]
        public async Task AMeterIsLetGoOfWhenTheStationStops()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            ReadTheMeterOf(1, new MeterEndpoint("127.0.0.1", meterA.Port));

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            await Station.Stop();

            Assert.That(Station.Meters, Is.Empty, "a stopped station still reads its meter");

        }

        #endregion

    }

}
