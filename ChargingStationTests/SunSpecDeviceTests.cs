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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A SunSpec device found where it begins, its list of models walked to the
    /// meter model among them, and that model read by its layout: integers and
    /// scale factors, floats, and the 24 registers Hermod's simulated meter
    /// answers under 213 - or why it is no meter this station can read.
    /// </summary>
    [TestFixture]
    public class SunSpecDeviceTests
    {

        private static async Task<MeterReading> ReadingOf(SunSpecImage Image)
        {

            var device = await SunSpecDevice.Discover(Image.Read);
            var block  = await device.ReadMeter(Image.Read);

            Assert.That(MeterReading.TryDecode(device, block, DateTimeOffset.UnixEpoch, out var reading, out var error), Is.True, error);

            return reading!;

        }

        private static async Task<String> RefusalOf(SunSpecImage Image)
        {

            var refused = await Assert.ThrowsAsync<NotASunSpecMeter>(() => SunSpecDevice.Discover(Image.Read));

            return refused!.Message;

        }


        #region Finding it, and its meter model

        [Test]
        public async Task ItsModelsAreWalkedToTheMeterModelBehindTheOthers()
        {

            var device = await SunSpecDevice.Discover(SunSpecImage.Model203Meter().Read);

            Assert.That(device.Base,                                Is.EqualTo(40000));
            Assert.That(device.Models.Select(model => model.Id),    Is.EqualTo(new UInt16[] { 1, 101, 203 }));
            Assert.That(device.Meter.Address,                       Is.EqualTo(40000 + 2 + 68 + 52), "the meter model is not where the list says");
            Assert.That(device.Manufacturer,                        Is.EqualTo("Acme"));
            Assert.That(device.Model,                               Is.EqualTo("EM-3"));
            Assert.That(device.Version,                             Is.EqualTo("2.1"));
            Assert.That(device.SerialNumber,                        Is.EqualTo("SN203"));

        }

        [Test]
        public async Task TheSessionModelIsFoundWhereTheMeterHasItAndOneTooShortIsNot()
        {

            var signing = await SunSpecDevice.Discover(new SigningMeter().Image.Read);
            var short_  = await SunSpecDevice.Discover(new SunSpecImage().Common("S", "S-1", "1", "SN").Model(203, 105).Model(MeterSessionModel.Id, 100).End().Read);
            var none    = await SunSpecDevice.Discover(SunSpecImage.Model203Meter().Read);

            Assert.Multiple(() => {
                Assert.That(signing.Sessions?.Id,               Is.EqualTo(MeterSessionModel.Id));
                Assert.That(signing.ToJSON().Value<Boolean>("sessions"), Is.True);
                Assert.That(short_.Sessions,                    Is.Null, "a model too short for the points is not the session model");
                Assert.That(none.Sessions,                      Is.Null);
            });

        }

        [Test]
        public async Task ADeviceThatBeginsAt50000IsFoundThere()
        {

            var device = await SunSpecDevice.Discover(SunSpecImage.Model203Meter(Base: 50000).Read);

            Assert.That(device.Base,            Is.EqualTo(50000));
            Assert.That(device.Meter.Address,   Is.EqualTo(50000 + 2 + 68 + 52));

        }

        [Test]
        public async Task AnAddressThatAnswersSomethingElseIsNoBeginning()
        {

            // Something at 40000 that is not "SunS", and the device at 50000.
            var image  = SunSpecImage.Model203Meter(Base: 50000).Write(40000, 0x1234, 0x5678);
            var device = await SunSpecDevice.Discover(image.Read);

            Assert.That(device.Base, Is.EqualTo(50000));

        }

        [Test]
        public async Task AMeterThatRefusesEveryRequestIsSaidToRefuseAndNotToBeNoSunSpecDevice()
        {

            var image = SunSpecImage.Model203Meter();
            image.RefusesEverything = true;

            var refused = await Assert.ThrowsAsync<ModbusException>(() => SunSpecDevice.Discover(image.Read));

            Assert.That(refused!.Code, Is.EqualTo(ModbusExceptionCode.IllegalFunction));

        }

        [Test]
        public async Task WhatIsNoSunSpecMeterThisStationCanReadIsSaidWithWhy()
        {

            Assert.That(await RefusalOf(new SunSpecImage(Base: 1234)),                                                  Does.Contain("no \"SunS\""));
            Assert.That(await RefusalOf(new SunSpecImage().Common("Inv", "I", "1", "S").Model(101, 50).End()),          Does.Contain("none of its models is a meter: 1, 101"));
            Assert.That(await RefusalOf(new SunSpecImage().Model(203, 105).End()),                                      Does.Contain("no common model"));
            Assert.That(await RefusalOf(new SunSpecImage().Common("M", "M", "1", "S").Model(203, 20).End()),             Does.Contain("203 with 20 registers"));
            Assert.That(await RefusalOf(new SunSpecImage().Common("M", "M", "1", "S").Model(213, 60).End()),             Does.Contain("213 with 60 registers"));

            var endless = new SunSpecImage();
            for (var i = 0; i < SunSpecDevice.MaxModels + 1; i++)
                endless.Model(64000, 0);

            Assert.That(await RefusalOf(endless.End()), Does.Contain($"does not end after {SunSpecDevice.MaxModels}"));

        }

        #endregion

        #region Reading it by its layout

        [Test]
        public async Task AModelOfIntegersIsReadWithItsScaleFactorsAndWhatItDoesNotMeasureIsNothing()
        {

            var reading = await ReadingOf(SunSpecImage.Model203Meter());

            Assert.That(reading.SunSpecModel,       Is.EqualTo(203));
            Assert.That(reading.SerialNumber,       Is.EqualTo("SN203"));
            Assert.That(reading.Current_A.All,      Is.EqualTo(48.0m));
            Assert.That(reading.Current_A.L2,       Is.EqualTo(16.0m));
            Assert.That(reading.Voltage_V.L1,       Is.EqualTo(230.1m));
            Assert.That(reading.Voltage_V.L2,       Is.Null, "a voltage the meter does not measure became a number");
            Assert.That(reading.Voltage_V.L3,       Is.EqualTo(230.2m));
            Assert.That(reading.Frequency_Hz,       Is.EqualTo(50.01m));
            Assert.That(reading.Power_W.All,        Is.EqualTo(11040m));
            Assert.That(reading.Power_W.L3,         Is.EqualTo(3680m));
            Assert.That(reading.Imported_Wh,        Is.EqualTo(100_000m));
            Assert.That(reading.Exported_Wh,        Is.EqualTo(7m));

        }

        [Test]
        public async Task AModelOfFloatsIsReadInMoreThanOneRequestAndANotANumberIsNothing()
        {

            var image   = SunSpecImage.Model213FloatMeter(NoFrequency: true);
            var reading = await ReadingOf(image);

            Assert.That(reading.SunSpecModel,       Is.EqualTo(213));
            Assert.That(reading.Manufacturer,       Is.EqualTo("Floaty"));
            Assert.That(reading.Current_A.L1,       Is.EqualTo(16.5m));
            Assert.That(reading.Voltage_V.All,      Is.EqualTo(229.6m));
            Assert.That(reading.Power_W.All,        Is.EqualTo(11385m));
            Assert.That(reading.Power_W.L2,         Is.EqualTo(3795m));
            Assert.That(reading.Imported_Wh,        Is.EqualTo(2_500_000m));
            Assert.That(reading.Exported_Wh,        Is.EqualTo(12m));
            Assert.That(reading.Frequency_Hz,       Is.Null, "a frequency that is not a number became one");

            Assert.That(image.Asked.Where(asked => asked.Count > 2).Max(asked => asked.Count), Is.LessThanOrEqualTo(SunSpecDevice.MaxRead),
                        "a request asked for more than Modbus lets one ask for");
            Assert.That(image.Asked.Select(asked => (asked.First, asked.Count)).TakeLast(2).ToArray(),
                        Is.EqualTo(new (UInt16, UInt16)[] { (40070, 125), (40195, 1) }), "the 126 registers of the model were not read in two requests");

        }

        [Test]
        public async Task HermodsSimulatedMeterIsReadByItsOwn24RegistersUnder213()
        {

            using var device  = new SunSpecMeterDevice("EnergyMeter01", SunSpecMeterMode.ImportOnly, runSimulation: false);
            var       backend = new SunSpecBackend(device);

            ReadRegisters read = async (first, count) =>
                ModbusPDU.DecodeReadResponse(await backend.ProcessRequestAsync(1, ModbusPDU.BuildReadHoldingRegisters(first, count), CancellationToken.None));

            var found   = await SunSpecDevice.Discover(read);
            var block   = await found.ReadMeter(read);

            Assert.That(MeterReading.LayoutOf(found.Meter), Is.EqualTo(MeterLayout.HermodDemo));
            Assert.That(MeterReading.TryDecode(found, block, DateTimeOffset.UnixEpoch, out var reading, out var error), Is.True, error);

            Assert.That(reading!.Manufacturer,     Is.EqualTo("Vanaheimr"));
            Assert.That(reading.SerialNumber,      Is.EqualTo("EnergyMeter01"));
            Assert.That(reading.Voltage_V.L1,      Is.InRange(200m, 260m));
            Assert.That(reading.Frequency_Hz,      Is.InRange(45m, 65m));

        }

        [Test]
        public async Task ADeviceThatRearrangedItsModelsIsNotReadByWhereTheyWere()
        {

            var image  = SunSpecImage.Model203Meter();
            var device = await SunSpecDevice.Discover(image.Read);

            image.Write(device.Meter.Address, 101);

            var block  = await device.ReadMeter(image.Read);

            Assert.That(MeterReading.TryDecode(device, block, DateTimeOffset.UnixEpoch, out _, out var error), Is.False);
            Assert.That(error, Does.Contain("rearranged"));

        }

        #endregion

    }

}
