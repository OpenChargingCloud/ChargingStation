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

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Where an EVSE's meter is read, as the file and the web interface say it,
    /// and what a SunSpec meter's registers mean.
    /// </summary>
    [TestFixture]
    public class MeterEndpointTests
    {

        private static EVSEConfig EVSEWith(JToken? MeterEndpoint)
        {

            var json = EVSEConfig.Default(1).ToJSON();
            json["meterEndpoint"] = MeterEndpoint;

            Assert.That(EVSEConfig.TryParse(json, out var evse, out var error), Is.True, error);

            return evse!;

        }

        private static String RefusalOf(JToken MeterEndpoint)
        {

            var json = EVSEConfig.Default(1).ToJSON();
            json["meterEndpoint"] = MeterEndpoint;

            Assert.That(EVSEConfig.TryParse(json, out _, out var error), Is.False, "it was taken");

            return error!;

        }


        [Test]
        public void AHostAloneIsReadOnModbusTLSsPortAndUnitOne()
        {

            var evse = EVSEWith(new JObject(new JProperty("host", " meter-a.local ")));

            Assert.That(evse.MeterEndpoint, Is.EqualTo(new MeterEndpoint("meter-a.local", 802, 1)));

        }

        [Test]
        public void WhatItIsGivenIsKeptAndWrittenBackAsGiven()
        {

            var evse  = EVSEWith(new JObject(new JProperty("host", "[fd00::5]"), new JProperty("port", 5802), new JProperty("unit", 7)));
            var again = EVSEWith(evse.ToJSON()["meterEndpoint"]);

            Assert.That(evse.MeterEndpoint,             Is.EqualTo(new MeterEndpoint("fd00::5", 5802, 7)));
            Assert.That(again.MeterEndpoint,            Is.EqualTo(evse.MeterEndpoint));
            Assert.That(evse.MeterEndpoint!.ToString(), Is.EqualTo("[fd00::5]:5802, unit 7"));

        }

        [Test]
        public void NothingAndABlankHostAreNoMeterToRead()
        {

            Assert.That(EVSEWith(null).MeterEndpoint,                                                Is.Null);
            Assert.That(EVSEWith(JValue.CreateNull()).MeterEndpoint,                                 Is.Null);
            Assert.That(EVSEWith(new JObject(new JProperty("host", "  "), new JProperty("port", 802))).MeterEndpoint, Is.Null);

        }

        [Test]
        public void WhatIsNoEndpointIsRefusedWithWhy()
        {

            Assert.That(RefusalOf(new JObject(new JProperty("host", "meter:802"),    new JProperty("port", 0))),     Does.Contain("'port'"));
            Assert.That(RefusalOf(new JObject(new JProperty("host", "meter"),        new JProperty("port", 70000))), Does.Contain("'port'"));
            Assert.That(RefusalOf(new JObject(new JProperty("host", "meter"),        new JProperty("unit", 256))),   Does.Contain("'unit'"));
            Assert.That(RefusalOf(new JObject(new JProperty("host", "http://meter/"))),                              Does.Contain("no host"));
            Assert.That(RefusalOf(new JObject(new JProperty("host", 42))),                                           Does.Contain("'host'"));
            Assert.That(RefusalOf(new JValue("meter")),                                                              Does.Contain("'meterEndpoint'"));

        }

        [Test]
        public void AMeterReadSomewhereElseIsOtherHardware()
        {

            var here   = EVSEWith(new JObject(new JProperty("host", "10.0.0.5")));
            var there  = EVSEWith(new JObject(new JProperty("host", "10.0.0.6")));
            var same   = EVSEWith(new JObject(new JProperty("host", "10.0.0.5"), new JProperty("port", 802)));

            Assert.That(here.SameHardwareAs(there), Is.False);
            Assert.That(here.SameHardwareAs(same),  Is.True);

        }


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
