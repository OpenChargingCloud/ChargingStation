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
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.ChargingStation.Meters;

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
    /// page and on the display; who the station is to it, for a read and for a
    /// write, and whom it believes it to be - held to a certificate, learned on
    /// first use; a meter read another way and an EVSE that goes, while the
    /// other EVSE's meter is left alone; and a request asked of it by hand.
    /// </summary>
    [TestFixture]
    public class MeterTests : AChargingStationTests
    {

        #region Data

        public const String  CardOfKnown  = "04A2112233";

        /// <summary>
        /// The register that says what kind of meter it is - net, import only,
        /// export only - which GridServiceSunSpec may write and ReadOnlySunSpec
        /// may not.
        /// </summary>
        private static readonly Int32  MeterModeRegister = SunSpecMeterMap.Addr(SunSpecMeterMap.OffMeterMeterMode);

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
        /// as the store keeps a TLS identity - and its id there.
        /// </summary>
        private String ImportIdentity(String Role)
        {

            using var leaf  = X509Certificate2.CreateFromPemFile(Path.Combine(pki, $"client-{Role}.crt"),
                                                                 Path.Combine(pki, $"client-{Role}.key"));

            var chain = new X509Certificate2Collection();
            chain.ImportFromPemFile(Path.Combine(pki, $"client-{Role}.chain.crt"));

            var bag = new X509Certificate2Collection { leaf };

            foreach (var certificate in chain)
                if (certificate.Thumbprint != leaf.Thumbprint)
                    bag.Add(certificate);

            Assert.That(Station.Certificates.Import(bag.Export(X509ContentType.Pkcs12)!, CertificateKind.TLSIdentity, null, $"meter {Role}", out var entry, out var error),
                        Is.True, error);

            return entry!.Id;

        }

        /// <summary>
        /// The root of the bench's PKI, as a TLS root for the given usages.
        /// </summary>
        private void ImportRoot(params String[] Usages)

            => Assert.That(Station.Certificates.Import(File.ReadAllBytes(Path.Combine(pki, "ca.crt")), CertificateKind.TLSRoot, null, "meters' root",
                                                       Usages, out _, out var error),
                           Is.True, error);

        /// <summary>
        /// The SHA-256 fingerprint of the certificate the bench's meters show.
        /// </summary>
        private String MetersCertificate()
        {
            using var certificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(pki, "server.crt"));
            return CertificateEntry.ThumbprintOf(certificate);
        }

        /// <summary>
        /// The meter of one EVSE read as the given entry says - as the Meters
        /// page saves it.
        /// </summary>
        private async Task ReadTheMeterOf(Byte EVSE, String Host, UInt16 Port, params JProperty[] More)
        {

            var json = new JObject(new JProperty("evse", EVSE), new JProperty("host", Host), new JProperty("port", Port));

            foreach (var property in More)
                json.Add(property);

            var (success, error, _) = await Station.UpdateMeterConfiguration(json);

            Assert.That(success, Is.True, error);

        }

        private static JProperty Identities(String? Read, String? Write)

            => new ("identities", new JObject(new JProperty("read", Read), new JProperty("write", Write)));

        private JObject? MeterOfEVSE(Byte EVSE)

            => (Station.MetersStatusJSON()["meters"] as JArray)!.
                   OfType<JObject>().
                   FirstOrDefault(meter => meter.Value<Byte>("evse") == EVSE);

        private String? StateOf(Byte EVSE)
            => MeterOfEVSE(EVSE)?.Value<String>("state");

        private JObject? EntryInTheFile(Byte EVSE)

            => (JObject.Parse(File.ReadAllText(Path.Combine(Directory, "configuration.json")))["meters"] as JArray)?.
                   OfType<JObject>().
                   FirstOrDefault(entry => entry.Value<Byte>("evse") == EVSE);

        private static ModbusRequest Request(params JProperty[] Properties)
        {
            Assert.That(ModbusRequest.TryParse(new JObject(Properties), out var request, out var error), Is.True, error);
            return request!;
        }

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


        #region What a meter measures, and where that is said

        [Test]
        public async Task AMeterThatIsReadSaysWhatItMeasuresAndOverWhat()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            var meter = MeterOfEVSE(1)!;

            Assert.That(meter.Value<String>("label"),                                 Is.EqualTo("A"));
            Assert.That(meter["reading"]!.Value<String>("manufacturer"),             Is.EqualTo("Vanaheimr"));
            Assert.That(meter["reading"]!.Value<String>("serialNumber"),             Is.EqualTo("EnergyMeter01"));
            Assert.That(meter["reading"]!["voltage_V"]!.Value<Decimal?>("L1"),       Is.GreaterThan(100), "no voltage was said");
            Assert.That(meter["reading"]!.Value<Decimal?>("frequency_Hz"),           Is.InRange(45m, 65m));

            Assert.That(meter["connection"]!.Value<String>("protocol"),              Does.StartWith("Tls1"));
            Assert.That(meter["connection"]!["identity"]!.Value<String>("role"),     Is.EqualTo(SunSpecRoles.ReadOnly));
            Assert.That(meter["connection"]!["meter"]!.Value<String>("subject"),     Does.Contain("EnergyMeter01"));
            Assert.That(meter["connection"]!.Value<String>("judged"),                Is.EqualTo("accepted"));

            Assert.That(MeterOfEVSE(2), Is.Null, "the other EVSE's meter is said, and it is configured nowhere");

        }

        [Test]
        public async Task TheEVSEsSayThatTheirMeterIsReadAndTheMetersWhatItMeasures()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            var evse = (Station.EVSEsStatusJSON()["evses"] as JArray)!.OfType<JObject>().First(json => json.Value<Byte>("id") == 1);

            Assert.That(evse["meter"]!.Value<Boolean>("read"), Is.True);
            Assert.That(evse["meter"]!.ToString(),             Does.Not.Contain("voltage").And.Not.Contain("reading"),
                        "what the meter measures is said with the EVSE");

            // Once each has said what it says now, the EVSEs have nothing new
            // to say while the meter is read, and the meters have.
            Station.ReportEVSEChanges();
            Station.ReportMeterChanges();

            await Task.Delay(Station.MeterReadInterval * 3);

            Assert.That(Station.ReportEVSEChanges(),   Is.False, "the EVSEs changed because their meter was read");
            Assert.That(Station.ReportMeterChanges(),  Is.True,  "the meters did not change while one was read");

        }

        [Test]
        public async Task WhatAMeterMeasuresComesDownTheEventStreamAsAMetersEvent()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            using var http    = await SignedIn();
            using var events  = await EventStream.OpenAndSettle(Station, http);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            // A key of what it measures, and not its serial number: the log line
            // that says the meter is read names that too, on the same stream.
            Assert.That(await events.ReadUntil("\"manufacturer\""), Is.True,
                        "What the meter measures never reached the event stream.");

            Assert.That(events.Count("event: meters"), Is.GreaterThanOrEqualTo(1),
                        "It came down the stream, but not as a \"meters\" event.");

        }

        [Test]
        public async Task TheDisplayShowsWhatTheMeterMeasuresWhileACarCharges()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => Station.MeterOf(1)?.Reading is not null, "the meter was not read: " + MeterOfEVSE(1));

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var error), Is.True, error);

            var evse     = (Station.KioskJSON()["evses"] as JArray)!.OfType<JObject>().First(json => json.Value<Byte>("id") == 1);
            var measured = Math.Round(Station.MeterOf(1)!.Reading!.Power_W.All!.Value / 1000m, 1);

            Assert.That(evse.Value<Boolean>("powerIsSimulated"),  Is.False, "the display says the power is simulated, and a meter measures it");
            Assert.That(evse.Value<Decimal>("currentPower_kW"),   Is.EqualTo(measured));

        }

        [Test]
        public async Task AMeterInTheFileIsReadFromTheStart()
        {

            var directory = TestStations.TemporaryDirectory("tests");

            try
            {

                var configuration = Configuration;
                configuration["meters"] = new JArray(new JObject(new JProperty("evse", 2), new JProperty("host", "127.0.0.1"), new JProperty("port", meterB.Port)));

                await using var station = await TestPorts.StartedOnFreshPorts(() => TestStations.New(directory, configuration));

                // Its store is empty, so it has nothing to show the meter - which
                // it says only of a meter it began to read.
                await Until(() => (station.MetersStatusJSON()["meters"] as JArray)!.OfType<JObject>().Any(meter => meter.Value<String>("state") == "noIdentity"),
                            "the meter in the file was not read: " + station.MetersStatusJSON());

            }
            finally
            {
                TestStations.Remove(directory);
            }

        }

        #endregion

        #region Whom the station is to the meter

        [Test]
        public async Task WithoutAnIdentityOfARoleItSaysSoAndReadsOnceOneIsImported()
        {

            // Long enough that only the store changing can make it try again
            // within the test.
            Station.MeterRetryInterval = TimeSpan.FromMinutes(10);

            ImportRoot(ChargingStation.MeterCertificateUsage);

            // A certificate without a role is no identity for a meter.
            ImportIdentity("NO-ROLE");

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "noIdentity", "a station with no identity of a SunSpec role did not say so: " + MeterOfEVSE(1));

            Assert.That(MeterOfEVSE(1)!.Value<String>("error"), Does.Contain("SunSpec role"));

            ImportIdentity(SunSpecRoles.ReadOnly);

            await Until(() => StateOf(1) == "reading", "the identity imported was not used: " + MeterOfEVSE(1));

        }

        [Test]
        public void WithoutOneConfiguredTheLeastRoleThatMayReadIsShownToRead()
        {

            ImportIdentity(SunSpecRoles.SuperAdministrator);
            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportIdentity(SunSpecRoles.GridService);

            using var identity = Station.LeastIdentityThatMayRead();

            Assert.That(identity?.Role, Is.EqualTo(SunSpecRoles.ReadOnly));

        }

        [Test]
        public async Task TheIdentityConfiguredToReadIsTheOneTheMeterIsReadAs()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            var gridService = ImportIdentity(SunSpecRoles.GridService);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, Identities(gridService, null));

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            Assert.That(MeterOfEVSE(1)!["connection"]!["identity"]!.Value<String>("role"), Is.EqualTo(SunSpecRoles.GridService));

        }

        [Test]
        public async Task AnIdentityThatIsNoneIsRefusedBeforeAnythingIsWritten()
        {

            var (success, error, _) = await Station.UpdateMeterConfiguration(new JObject(
                                                 new JProperty("evse", 1), new JProperty("host", "127.0.0.1"), new JProperty("port", meterA.Port),
                                                 Identities(null, "0123456789abcdef")));

            Assert.That(success, Is.False);
            Assert.That(error,   Does.Contain("0123456789abcdef"));
            Assert.That(EntryInTheFile(1), Is.Null, "a refused change was written");

        }

        #endregion

        #region Whom the station believes the meter to be

        [Test]
        public async Task AMeterWhoseCertificateChainsToNoRootForMetersIsNotBelieved()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);

            // A root of the store, but one for the time servers only.
            ImportRoot("nts");

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

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
            await ReadTheMeterOf(1, "::ffff:127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) is "untrusted" or "unreachable", "a meter by the wrong name was read: " + MeterOfEVSE(1));

            Assume.That(StateOf(1), Is.EqualTo("untrusted"), "::ffff:127.0.0.1 does not reach the meter here");

            Assert.That(MeterOfEVSE(1)!.Value<String>("error"), Does.Contain("not issued for"));

        }

        [Test]
        public async Task AMeterHeldToAnotherCertificateIsNotBelievedAndOneHeldToItsOwnIs()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, new JProperty("certificateFingerprint", new String('a', 64)));

            await Until(() => StateOf(1) == "untrusted", "a meter that showed another certificate than it is held to was read: " + MeterOfEVSE(1));

            Assert.That(MeterOfEVSE(1)!.Value<String>("error"), Does.Contain("Held to certificate"));

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, new JProperty("certificateFingerprint", MetersCertificate()));

            await Until(() => StateOf(1) == "reading", "a meter held to the certificate it shows was not read: " + MeterOfEVSE(1));

        }

        [Test]
        public async Task AMeterLearnsTheCertificateItIsFirstBelievedWithAndKeepsItAcrossASave()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, new JProperty("trustOnFirstUse", "certificate"));

            await Until(() => EntryInTheFile(1)?.Value<String>("certificateFingerprint") is not null, "nothing was learned: " + EntryInTheFile(1));

            Assert.That(EntryInTheFile(1)!.Value<String>("certificateFingerprint"), Is.EqualTo(MetersCertificate()));
            Assert.That(Station.MetersFile.MeterOf(1)!.Pins!.Certificates,           Is.EqualTo(new[] { MetersCertificate() }));

            // A page that was open before it learned, and saves the unit: it
            // showed the meter held to nothing but learning, and says so.
            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, new JProperty("unit", 1),
                                 new JProperty("trustOnFirstUse", "certificate"),
                                 new JProperty("pinsAsShown", new JObject(new JProperty("trustOnFirstUse", "certificate"))));

            Assert.That(EntryInTheFile(1)!.Value<String>("certificateFingerprint"), Is.EqualTo(MetersCertificate()),
                        "the pin learned while the page was open went with its save");

        }

        #endregion

        #region Meters read another way, and EVSEs that go

        [Test]
        public async Task AMeterReadAnotherWayIsReadAnewAndTheOtherEVSEsIsLeftAlone()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);
            await ReadTheMeterOf(2, "localhost", meterB.Port);

            await Until(() => StateOf(1) == "reading" && StateOf(2) == "reading", "the two meters were not read: " + MeterOfEVSE(1) + MeterOfEVSE(2));

            var second = Station.MeterOf(2);

            await ReadTheMeterOf(1, "localhost", meterA.Port);

            await Until(() => Station.MeterOf(1)?.Endpoint.Host == "localhost", "the meter is still read where it was");
            await Until(() => StateOf(1) == "reading", "the meter is not read where it is now: " + MeterOfEVSE(1));

            Assert.That(Station.MeterOf(2), Is.SameAs(second), "changing the first EVSE's meter began the second's anew");

        }

        [Test]
        public async Task AMeterWhoseWriteIdentityAloneChangedGoesOnBeingRead()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            var gridService = ImportIdentity(SunSpecRoles.GridService);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            var reading = Station.MeterOf(1);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port, Identities(null, gridService));

            Assert.That(Station.MeterOf(1), Is.SameAs(reading), "the meter was read anew, and only what it is written with changed");

        }

        [Test]
        public async Task AMeterReadNowhereIsNoLongerRead()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            await ReadTheMeterOf(1, "  ", meterA.Port);

            Assert.That(Station.MeterOf(1),  Is.Null, "a meter read nowhere is still read");
            Assert.That(EntryInTheFile(1),   Is.Null, "a meter read nowhere is still in the file");

        }

        [Test]
        public async Task AnEVSEThatGoesTakesItsMeterAlong()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(2, "127.0.0.1", meterB.Port);

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

            await ReadTheMeterOf(1, "127.0.0.1", meterA.Port);

            await Until(() => StateOf(1) == "reading", "the meter was not read: " + MeterOfEVSE(1));

            await Station.Stop();

            Assert.That(Station.Meters, Is.Empty, "a stopped station still reads its meter");

        }

        #endregion

        #region A request asked by hand

        [Test]
        public async Task AReadIsAskedAsTheReadIdentityAndAWriteAsTheWriteIdentity()
        {

            var readOnly    = ImportIdentity(SunSpecRoles.ReadOnly);
            var gridService = ImportIdentity(SunSpecRoles.GridService);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(2, "127.0.0.1", meterB.Port, Identities(readOnly, gridService));

            await Until(() => StateOf(2) == "reading", "the meter was not read: " + MeterOfEVSE(2));

            var read = await Station.AskMeter(2, Request(new JProperty("function", 3), new JProperty("address", 40000), new JProperty("count", 2)))!;

            Assert.That(read.Value<String>("outcome"),                                         Is.EqualTo("answered"), read.ToString());
            Assert.That(read["identity"]!.Value<String>("role"),                                Is.EqualTo(SunSpecRoles.ReadOnly));
            Assert.That(read["registers"]!.Select(register => register.Value<Int32>("value")),  Is.EqualTo(new[] { 0x5375, 0x6E53 }), "not \"SunS\"");
            Assert.That(read.Value<String>("request"),                                          Is.EqualTo("039C400002"));

            try
            {

                var write = await Station.AskMeter(2, Request(new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", 0)))!;

                Assert.That(write.Value<String>("outcome"),             Is.EqualTo("answered"), write.ToString());
                Assert.That(write["identity"]!.Value<String>("role"),   Is.EqualTo(SunSpecRoles.GridService));
                Assert.That(meterB.Device.Mode,                         Is.EqualTo(SunSpecMeterMode.Net), "the meter was not written");

            }
            finally
            {
                await Station.AskMeter(2, Request(new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", (Int32) SunSpecMeterMode.ImportOnly)))!;
            }

        }

        [Test]
        public async Task AWriteAsAnIdentityWhoseRoleMayNotIsRefusedByTheMeter()
        {

            var readOnly = ImportIdentity(SunSpecRoles.ReadOnly);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(2, "127.0.0.1", meterB.Port, Identities(null, readOnly));

            var write = await Station.AskMeter(2, Request(new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", 0)))!;

            Assert.That(write.Value<String>("outcome"),  Is.EqualTo("exception"), write.ToString());
            Assert.That(meterB.Device.Mode,              Is.EqualTo(SunSpecMeterMode.ImportOnly), "a role that may not write wrote");

        }

        [Test]
        public async Task WithoutAWriteIdentityNothingIsWritten()
        {

            ImportIdentity(SunSpecRoles.ReadOnly);
            ImportIdentity(SunSpecRoles.SuperAdministrator);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(2, "127.0.0.1", meterB.Port);

            var write = await Station.AskMeter(2, Request(new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", 0)))!;

            Assert.That(write.Value<String>("outcome"),  Is.EqualTo("noIdentity"), write.ToString());
            Assert.That(write.Value<String>("error"),    Does.Contain("configured to write"));
            Assert.That(meterB.Device.Mode,              Is.EqualTo(SunSpecMeterMode.ImportOnly));

        }

        [Test]
        public async Task TheOperatorMayAskAReadAndNotAWriteAndTheInstallerMayAskBoth()
        {

            var readOnly    = ImportIdentity(SunSpecRoles.ReadOnly);
            var gridService = ImportIdentity(SunSpecRoles.GridService);
            ImportRoot(ChargingStation.MeterCertificateUsage);

            await ReadTheMeterOf(2, "127.0.0.1", meterB.Port, Identities(readOnly, gridService));

            using var http = await SignedIn();

            var holding = await Become("cpo", "systemadmin");

            using (var read  = await http.PostAsync("api/v1/meters/modbus", JSONBody(new JProperty("evse", 2), new JProperty("function", 3), new JProperty("address", 40000), new JProperty("count", 2))))
                Assert.That(read.StatusCode,  Is.EqualTo(System.Net.HttpStatusCode.OK), "the operator may not read a meter");

            using (var write = await http.PostAsync("api/v1/meters/modbus", JSONBody(new JProperty("evse", 2), new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", 1))))
                Assert.That(write.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.Forbidden), "the operator may write to a meter");

            await Become("installer", holding);

            using (var write = await http.PostAsync("api/v1/meters/modbus", JSONBody(new JProperty("evse", 2), new JProperty("function", 6), new JProperty("address", MeterModeRegister), new JProperty("value", 1))))
            {
                Assert.That(write.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.OK), "the installer may not write to a meter");
                Assert.That(JObject.Parse(await write.Content.ReadAsStringAsync()).Value<String>("outcome"), Is.EqualTo("answered"));
            }

        }

        [Test]
        public async Task AskingAnEVSEWhoseMeterIsReadNowhereIsSaidSo()
        {

            using var http     = await SignedIn();
            using var response = await http.PostAsync("api/v1/meters/modbus", JSONBody(new JProperty("evse", 1), new JProperty("function", 3), new JProperty("address", 40000), new JProperty("count", 2)));

            Assert.That(response.StatusCode, Is.EqualTo(System.Net.HttpStatusCode.NotFound));

        }

        #endregion

    }

}
