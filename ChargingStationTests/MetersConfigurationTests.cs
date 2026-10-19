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

using Newtonsoft.Json.Linq;
using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// How a meter is read, as the "meters" section and the Meters page say it;
    /// a request asked by hand, as the page sends it; and what a SunSpec
    /// meter's registers mean.
    /// </summary>
    [TestFixture]
    public class MetersConfigurationTests
    {

        private static MeterConfiguration? MeterOf(JObject Entry)
        {

            Assert.That(MeterConfiguration.TryParse(Entry, "meters[0]", out var meter, out var error), Is.True, error);

            return meter;

        }

        private static String RefusalOf(JObject Entry)
        {

            Assert.That(MeterConfiguration.TryParse(Entry, "meters[0]", out _, out var error), Is.False, "it was taken");

            return error!;

        }

        private static JObject Entry(params JProperty[] Properties)

            => new (new JProperty("evse", 1), Properties);


        [Test]
        public void AHostAloneIsReadOnModbusTLSsPortAndUnitOneAsTheLeastIdentityAndWrittenAsNone()
        {

            var meter = MeterOf(Entry(new JProperty("host", " meter-a.local ")))!;

            Assert.That(meter.Endpoint,       Is.EqualTo(new MeterEndpoint("meter-a.local", 802, 1)));
            Assert.That(meter.ReadIdentity,   Is.Null);
            Assert.That(meter.WriteIdentity,  Is.Null);
            Assert.That(meter.Pins,           Is.Null);

        }

        [Test]
        public void WhatItIsGivenIsKeptAndWrittenBackAsGiven()
        {

            var meter = MeterOf(Entry(new JProperty("host",                "[fd00::5]"),
                                      new JProperty("port",                5802),
                                      new JProperty("unit",                7),
                                      new JProperty("identities",          new JObject(new JProperty("read", "AAAABBBBCCCCDDDD"), new JProperty("write", "1111222233334444"))),
                                      new JProperty("rootFingerprint",     new String('b', 64)),
                                      new JProperty("trustOnFirstUse",     "certificate")))!;

            var again = MeterOf(meter.ToJSON())!;

            Assert.That(meter.Endpoint,                  Is.EqualTo(new MeterEndpoint("fd00::5", 5802, 7)));
            Assert.That(meter.Endpoint.ToString(),       Is.EqualTo("[fd00::5]:5802, unit 7"));
            Assert.That(meter.Endpoint.Server,           Is.EqualTo("[fd00::5]:5802"));
            Assert.That(meter.ReadIdentity,              Is.EqualTo("aaaabbbbccccdddd"));
            Assert.That(meter.WriteIdentity,             Is.EqualTo("1111222233334444"));
            Assert.That(meter.Pins!.Roots,               Is.EqualTo(new[] { new String('b', 64) }));
            Assert.That(meter.Pins.TrustOnFirstUse,      Is.EqualTo(TrustOnFirstUse.Certificate));

            Assert.That(again,                           Is.EqualTo(meter with { Pins = again.Pins }));
            Assert.That(again.Pins!.SameAs(meter.Pins),  Is.True);

        }

        [Test]
        public void ABlankHostIsNoMeterToReadOnAPageAndRefusedInTheFile()
        {

            Assert.That(MeterOf(Entry(new JProperty("host", "  "), new JProperty("port", 802))), Is.Null);

            Assert.That(MetersConfiguration.TryParse(new JArray(Entry(new JProperty("host", ""))), out _, out var error), Is.False);
            Assert.That(error, Does.Contain("no 'host'"));

        }

        [Test]
        public void WhatIsNoMeterToReadIsRefusedWithWhereAndWhy()
        {

            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("port", 0))),      Does.Contain("'meters[0].port'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("port", 70000))),  Does.Contain("'meters[0].port'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("unit", 256))),    Does.Contain("'meters[0].unit'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "http://meter/"))),                                Does.Contain("no host"));
            Assert.That(RefusalOf(Entry(new JProperty("host", 42))),                                             Does.Contain("'meters[0].host'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("identities", "read"))),
                        Does.Contain("'meters[0].identities'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("identities", new JObject(new JProperty("write", 7))))),
                        Does.Contain("'meters[0].identities.write'"));
            Assert.That(RefusalOf(Entry(new JProperty("host", "meter"),         new JProperty("onMismatch", "record"))),
                        Does.Contain("held to none"));
            Assert.That(RefusalOf(new JObject(new JProperty("host", "meter"))),                                  Does.Contain("'meters[0].evse'"));

        }

        [Test]
        public void TwoEntriesForOneEVSEAreRefused()
        {

            var twice = new JArray(Entry(new JProperty("host", "a")), Entry(new JProperty("host", "b")));

            Assert.That(MetersConfiguration.TryParse(twice, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("'meters[1]'").And.Contain("EVSE 1"));

        }

        [Test]
        public void OnlyWhereItIsReadWhatItIsHeldToAndWhatItIsReadAsMakeItReadAnew()
        {

            var meter = MeterOf(Entry(new JProperty("host", "a")))!;

            Assert.That(meter.SameReadingAs(meter with { WriteIdentity = "1111222233334444" }),                         Is.True,  "the identity it is written with");
            Assert.That(meter.SameReadingAs(meter with { ReadIdentity  = "1111222233334444" }),                         Is.False, "the identity it is read with");
            Assert.That(meter.SameReadingAs(meter with { Endpoint      = meter.Endpoint with { Unit = 2 } }),           Is.False, "its unit");
            Assert.That(meter.SameReadingAs(meter with { Pins          = new ServerPins([ new String('c', 64) ], []) }), Is.False, "what it is held to");
            Assert.That(meter.SameReadingAs(null),                                                                      Is.False);

        }


        #region A request asked by hand

        private static ModbusRequest RequestOf(params JProperty[] Properties)
        {
            Assert.That(ModbusRequest.TryParse(new JObject(Properties), out var request, out var error), Is.True, error);
            return request!;
        }

        private static String RefusalOfRequest(params JProperty[] Properties)
        {
            Assert.That(ModbusRequest.TryParse(new JObject(Properties), out _, out var error), Is.False, "it was taken");
            return error!;
        }

        [Test]
        public void AReadIsAReadAndAWriteAWriteDownToTheBytes()
        {

            var holding  = RequestOf(new JProperty("function", 3),  new JProperty("address", 40000), new JProperty("count", 2));
            var input    = RequestOf(new JProperty("function", 4),  new JProperty("address", 30001), new JProperty("count", 1));
            var single   = RequestOf(new JProperty("function", 6),  new JProperty("address", 40094), new JProperty("value", 1));
            var several  = RequestOf(new JProperty("function", 16), new JProperty("address", 40094), new JProperty("values", new JArray(1, 0xCAFE)));

            Assert.That(holding.Operation,                         Is.EqualTo(ModbusOperation.Read));
            Assert.That(input.Operation,                           Is.EqualTo(ModbusOperation.Read));
            Assert.That(single.Operation,                          Is.EqualTo(ModbusOperation.Write));
            Assert.That(several.Operation,                         Is.EqualTo(ModbusOperation.Write));

            Assert.That(Convert.ToHexString(holding.ToPDU()),      Is.EqualTo("039C400002"));
            Assert.That(Convert.ToHexString(input.ToPDU()),        Is.EqualTo("0475310001"));
            Assert.That(Convert.ToHexString(single.ToPDU()),       Is.EqualTo("069C9E0001"));
            Assert.That(Convert.ToHexString(several.ToPDU()),      Is.EqualTo("109C9E0002040001CAFE"));

        }

        [Test]
        public void WhatIsNoRequestIsRefusedWithWhy()
        {

            Assert.That(RefusalOfRequest(new JProperty("function", 5),  new JProperty("address", 1),     new JProperty("count", 1)),   Does.Contain("Function 5"));
            Assert.That(RefusalOfRequest(new JProperty("function", 3),  new JProperty("address", 1),     new JProperty("count", 126)), Does.Contain("'count'"));
            Assert.That(RefusalOfRequest(new JProperty("function", 3),  new JProperty("address", 65535), new JProperty("count", 2)),   Does.Contain("past the last one"));
            Assert.That(RefusalOfRequest(new JProperty("function", 6),  new JProperty("address", 1),     new JProperty("value", -1)),  Does.Contain("'value'"));
            Assert.That(RefusalOfRequest(new JProperty("function", 16), new JProperty("address", 1),     new JProperty("values", new JArray())), Does.Contain("'values'"));
            Assert.That(RefusalOfRequest(new JProperty("function", 3),  new JProperty("count", 1)),                                    Does.Contain("'address'"));

        }

        #endregion


        #region What the registers mean

        private static UInt16[] Registers()
        {

            var registers = new UInt16[SunSpecMeterMap.RegisterCount];

            registers[SunSpecMeterMap.OffMarker]      = 0x5375;
            registers[SunSpecMeterMap.OffMarker + 1]  = 0x6E53;
            registers[SunSpecMeterMap.OffMeterId]     = SunSpecMeterMap.MeterModelId;

            // "Vanaheimr", two characters to a register.
            var name = "Vanaheimr\0"u8.ToArray();
            for (var i = 0; i < name.Length / 2; i++)
                registers[SunSpecMeterMap.OffCommonMn + i] = (UInt16) ((name[2 * i] << 8) | name[2 * i + 1]);

            registers[SunSpecMeterMap.OffMeterPhVphA] = 2296;
            registers[SunSpecMeterMap.OffMeterV_SF]   = unchecked((UInt16) (-1));
            registers[SunSpecMeterMap.OffMeterW]      = 1104;
            registers[SunSpecMeterMap.OffMeterW_SF]   = 1;
            registers[SunSpecMeterMap.OffMeterHz]     = 0x8000;
            registers[SunSpecMeterMap.OffMeterHz_SF]  = unchecked((UInt16) (-2));
            registers[SunSpecMeterMap.OffMeterTotWhImp]     = 0x0001;
            registers[SunSpecMeterMap.OffMeterTotWhImp + 1] = 0x0000;
            registers[SunSpecMeterMap.OffMeterWh_SF]  = 0;

            return registers;

        }

        [Test]
        public void RegistersAreTheNumbersTheyStandForAndWhatIsNotImplementedIsNothing()
        {

            Assert.That(MeterReading.TryDecode(Registers(), DateTimeOffset.UnixEpoch, out var reading, out var error), Is.True, error);

            Assert.That(reading!.Manufacturer,     Is.EqualTo("Vanaheimr"));
            Assert.That(reading.Voltage_V.L1,      Is.EqualTo(229.6m));
            Assert.That(reading.Power_W.All,       Is.EqualTo(11040m));
            Assert.That(reading.Frequency_Hz,      Is.Null, "a value the meter does not implement became a number");
            Assert.That(reading.Imported_Wh,       Is.EqualTo(65536m));

        }

        [Test]
        public void WhatIsNoSunSpecMeterIsSaidSo()
        {

            var registers = Registers();
            registers[SunSpecMeterMap.OffMarker] = 0;

            Assert.That(MeterReading.TryDecode(registers, DateTimeOffset.UnixEpoch, out _, out var noMarker), Is.False);
            Assert.That(noMarker, Does.Contain("SunS"));

            registers = Registers();
            registers[SunSpecMeterMap.OffMeterId] = 103;

            Assert.That(MeterReading.TryDecode(registers, DateTimeOffset.UnixEpoch, out _, out var inverter), Is.False);
            Assert.That(inverter, Does.Contain("103"));

            Assert.That(MeterReading.TryDecode(registers[..10], DateTimeOffset.UnixEpoch, out _, out var tooFew), Is.False);
            Assert.That(tooFew, Does.Contain("10 registers"));

        }

        #endregion

    }

}
