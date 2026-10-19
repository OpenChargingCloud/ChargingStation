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

using System.Buffers.Binary;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The registers of a SunSpec device as a test writes them down - the
    /// marker, models one after the other, the end - asked as a station asks a
    /// meter, directly or as the backend of a Modbus/TLS frontend.
    /// </summary>
    /// <remarks>
    /// A register nobody wrote is not there: asking for it is answered with
    /// IllegalDataAddress, as a device answers for an address it does not have.
    /// </remarks>
    internal sealed class SunSpecImage : IModbusBackendFactory, IModbusBackend
    {

        private readonly Dictionary<UInt16, UInt16>  registers = [];
        private          Int32                       next;

        /// <summary>
        /// Every request asked of it, as (first, count).
        /// </summary>
        public List<(UInt16 First, UInt16 Count)>  Asked  { get; } = [];

        /// <summary>
        /// Refuses every request, as a meter refuses a role it does not know.
        /// </summary>
        public Boolean  RefusesEverything  { get; set; }


        /// <summary>
        /// A device that begins at the given address with "SunS".
        /// </summary>
        public SunSpecImage(UInt16 Base = 40000)
        {
            Write(Base, SunSpecDevice.MarkerHigh, SunSpecDevice.MarkerLow);
            next = Base + 2;
        }


        #region Write(Address, Values) / Model(Id, Length, Fill)

        /// <summary>
        /// Registers written where the test says.
        /// </summary>
        public SunSpecImage Write(Int32 Address, params UInt16[] Values)
        {
            for (var i = 0; i < Values.Length; i++)
                registers[(UInt16) (Address + i)] = Values[i];
            return this;
        }

        /// <summary>
        /// The next model of the list: its id, its length, and what it says,
        /// from the first register after its length.
        /// </summary>
        public SunSpecImage Model(UInt16 Id, UInt16 Length, Action<SunSpecImage, Int32>? Fill = null)
        {

            Write(next, Id, Length);

            for (var i = 0; i < Length; i++)
                registers[(UInt16) (next + 2 + i)] = 0;

            Fill?.Invoke(this, next + 2);

            next += 2 + Length;

            return this;

        }

        /// <summary>
        /// The end of the list.
        /// </summary>
        public SunSpecImage End()
        {
            Write(next, SunSpecDevice.EndOfModels, 0);
            return this;
        }

        #endregion

        #region Common(At, Manufacturer, Model, Version, SerialNumber)

        /// <summary>
        /// A common model, 66 registers long, saying who the device is.
        /// </summary>
        public SunSpecImage Common(String Manufacturer, String Model, String Version, String SerialNumber)

            => this.Model(SunSpecDevice.CommonModel, 66, (image, at) => {
                   image.Text(at,       Manufacturer, 16);
                   image.Text(at + 16,  Model,        16);
                   image.Text(at + 40,  Version,       8);
                   image.Text(at + 48,  SerialNumber, 16);
                   image.Write(at + 64, 1);
               });

        #endregion

        #region Text(Address, Text, Length) / Float(Address, Value) / Int(Address, Value) / Acc32(Address, Value)

        public void Text(Int32 Address, String Text, Int32 Length)
        {
            var bytes = new Byte[Length * 2];
            System.Text.Encoding.ASCII.GetBytes(Text).CopyTo(bytes, 0);
            for (var i = 0; i < Length; i++)
                registers[(UInt16) (Address + i)] = (UInt16) ((bytes[2 * i] << 8) | bytes[2 * i + 1]);
        }

        public void Float(Int32 Address, Single Value)
        {
            var bits = BitConverter.SingleToInt32Bits(Value);
            Write(Address, (UInt16) (bits >> 16), (UInt16) (bits & 0xFFFF));
        }

        public void Int(Int32 Address, Int16 Value)

            => Write(Address, unchecked((UInt16) Value));

        public void Acc32(Int32 Address, UInt32 Value)

            => Write(Address, (UInt16) (Value >> 16), (UInt16) (Value & 0xFFFF));

        #endregion


        #region Read(First, Count)

        /// <summary>
        /// Registers read, as a station reads them.
        /// </summary>
        /// <exception cref="ModbusException">IllegalDataAddress, for a register it does not have.</exception>
        public Task<UInt16[]> Read(UInt16 First, UInt16 Count)
        {

            Asked.Add((First, Count));

            if (RefusesEverything)
                throw new ModbusException(ModbusExceptionCode.IllegalFunction);

            var values = new UInt16[Count];

            for (var i = 0; i < Count; i++)
                if (!registers.TryGetValue((UInt16) (First + i), out values[i]))
                    throw new ModbusException(ModbusExceptionCode.IllegalDataAddress);

            return Task.FromResult(values);

        }

        #endregion

        #region IModbusBackendFactory / IModbusBackend

        public Task<IModbusBackend> CreateAsync(Int64 connectionId, String? role, CancellationToken ct)

            => Task.FromResult<IModbusBackend>(this);

        public async Task<Byte[]> ProcessRequestAsync(Byte unitId, ReadOnlyMemory<Byte> requestPdu, CancellationToken ct)
        {

            var pdu = requestPdu.ToArray();

            if (pdu.Length < 5 || pdu[0] != (Byte) ModbusFunctionCodes.ReadHoldingRegisters)
                return ModbusPDU.BuildException((ModbusFunctionCodes) pdu[0], ModbusExceptionCode.IllegalFunction);

            try
            {
                return ModbusPDU.BuildReadHoldingResponse(await Read(BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2)),
                                                                     BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2))));
            }
            catch (ModbusException e)
            {
                return ModbusPDU.BuildException(ModbusFunctionCodes.ReadHoldingRegisters, e.Code);
            }

        }

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;

        #endregion


        #region (static) Model203Meter(Base)

        /// <summary>
        /// A three-phase meter as SunSpec has one, with integers and scale
        /// factors: a common model, an inverter model in front of its meter
        /// model 203, and the end. 48.0 A, 230.1 V, 50.01 Hz, 11 040 W,
        /// 100 000 Wh imported - and a voltage of phase B it does not measure.
        /// </summary>
        public static SunSpecImage Model203Meter(UInt16 Base = 40000)

            => new SunSpecImage(Base).
                   Common("Acme", "EM-3", "2.1", "SN203").
                   Model(101, 50).
                   Model(203, 105, (image, at) => {
                       image.Write(at,      480, 160, 160, 160, unchecked((UInt16) (-1)));          // A, per phase, A_SF
                       image.Write(at +  5, 2301, 2301, 0x8000, 2302);                             // PhV, A, B not measured, C
                       image.Int  (at + 13, -1);                                                   // V_SF
                       image.Write(at + 14, 5001, unchecked((UInt16) (-2)));                       // Hz, Hz_SF
                       image.Write(at + 16, 1104, 368, 368, 368, 1);                               // W, per phase, W_SF
                       image.Acc32(at + 36, 7);                                                    // TotWhExp
                       image.Acc32(at + 44, 100_000);                                              // TotWhImp
                       image.Write(at + 52, 0);                                                    // TotWh_SF
                   }).
                   End();

        #endregion

        #region (static) Model213FloatMeter(Base)

        /// <summary>
        /// A three-phase meter as SunSpec has one, with floats: a common model,
        /// its meter model 213 of 124 registers - more than one request reads -
        /// and the end. 16.5 A on each phase, 229.6 V, 49.98 Hz, 11 385 W,
        /// 2 500 000 Wh imported - and a frequency that is not a number, where
        /// the test says so.
        /// </summary>
        public static SunSpecImage Model213FloatMeter(UInt16 Base = 40000, Boolean NoFrequency = false)

            => new SunSpecImage(Base).
                   Common("Floaty", "FM-213", "3.0", "SN213").
                   Model(213, 124, (image, at) => {
                       image.Float(at,      49.5f);
                       foreach (var phase in new[] { 2, 4, 6 })
                           image.Float(at + phase, 16.5f);
                       foreach (var phase in new[] { 8, 10, 12, 14 })
                           image.Float(at + phase, 229.6f);
                       image.Float(at + 24, NoFrequency ? Single.NaN : 49.98f);
                       image.Float(at + 26, 11385f);
                       foreach (var phase in new[] { 28, 30, 32 })
                           image.Float(at + phase, 3795f);
                       image.Float(at + 58, 12f);
                       image.Float(at + 66, 2_500_000f);
                   }).
                   End();

        #endregion

    }

}
