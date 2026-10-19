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
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// What a request does to a meter, which decides whom the station is to
    /// it: one identity to read, another to write.
    /// </summary>
    public enum ModbusOperation
    {

        /// <summary>
        /// Registers read: function 03 or 04.
        /// </summary>
        Read,

        /// <summary>
        /// Registers written: function 06 or 16.
        /// </summary>
        Write

    }


    /// <summary>
    /// One Modbus request somebody asked a meter, as the Modbus client of the
    /// Meters page sends it: <c>{ "function": 3, "address": 40000, "count": 2 }</c>,
    /// <c>{ "function": 6, "address": 40094, "value": 1 }</c> or
    /// <c>{ "function": 16, "address": 40094, "values": [ 1, 2 ] }</c>.
    /// </summary>
    /// <remarks>
    /// The addresses as they go on the wire: SunSpec's are absolute, so the
    /// marker of a SunSpec device is read at 40000 and not at 39999.
    /// </remarks>
    /// <param name="Function">Which Modbus function: 3, 4, 6 or 16.</param>
    /// <param name="Address">The first register.</param>
    /// <param name="Count">How many registers are read; for a write, how many are written.</param>
    /// <param name="Values">What is written; empty for a read.</param>
    public sealed record ModbusRequest(Byte                   Function,
                                       UInt16                 Address,
                                       UInt16                 Count,
                                       IReadOnlyList<UInt16>  Values)
    {

        #region Data

        /// <summary>
        /// Read holding registers.
        /// </summary>
        public const Byte  ReadHoldingRegisters    = 0x03;

        /// <summary>
        /// Read input registers.
        /// </summary>
        public const Byte  ReadInputRegisters      = 0x04;

        /// <summary>
        /// Write one register.
        /// </summary>
        public const Byte  WriteSingleRegister     = 0x06;

        /// <summary>
        /// Write several registers.
        /// </summary>
        public const Byte  WriteMultipleRegisters  = 0x10;

        /// <summary>
        /// The most registers one read may ask for, as Modbus has it.
        /// </summary>
        public const UInt16  MaxRead   = 125;

        /// <summary>
        /// The most registers one write may carry, as Modbus has it.
        /// </summary>
        public const UInt16  MaxWrite  = 123;

        #endregion

        #region Properties

        /// <summary>
        /// Whether it reads or writes - and so which identity is shown for it.
        /// </summary>
        public ModbusOperation  Operation
            => Function is ReadHoldingRegisters or ReadInputRegisters
                   ? ModbusOperation.Read
                   : ModbusOperation.Write;

        /// <summary>
        /// What the function is called.
        /// </summary>
        public String  Name
            => NameOf(Function);

        #endregion


        #region (static) TryParse(JSON, out Request, out Error)

        /// <summary>
        /// A request as the page sends it, or why it is none.
        /// </summary>
        public static Boolean TryParse(JObject                                  JSON,
                                       [NotNullWhen(true)]  out ModbusRequest?  Request,
                                       [NotNullWhen(false)] out String?         Error)
        {

            Request = null;

            if (!TryReadNumber(JSON, "function", 0, 255, out var function, out Error))
                return false;

            if (function is not ReadHoldingRegisters and not ReadInputRegisters and not WriteSingleRegister and not WriteMultipleRegisters)
            {
                Error = $"Function {function} is not one this client asks: 3 reads holding registers, 4 input registers, 6 writes one register and 16 several.";
                return false;
            }

            if (!TryReadNumber(JSON, "address", 0, UInt16.MaxValue, out var address, out Error))
                return false;

            switch (function)
            {

                case ReadHoldingRegisters or ReadInputRegisters:

                    if (!TryReadNumber(JSON, "count", 1, MaxRead, out var count, out Error))
                        return false;

                    if (address + count - 1 > UInt16.MaxValue)
                    {
                        Error = $"{count} registers from {address} on run past the last one, 65535.";
                        return false;
                    }

                    Request = new ModbusRequest((Byte) function, (UInt16) address, (UInt16) count, []);
                    return true;

                case WriteSingleRegister:

                    if (!TryReadNumber(JSON, "value", 0, UInt16.MaxValue, out var value, out Error))
                        return false;

                    Request = new ModbusRequest((Byte) function, (UInt16) address, 1, [ (UInt16) value ]);
                    return true;

                default:

                    if (JSON["values"] is not JArray values || values.Count is 0 or > MaxWrite)
                    {
                        Error = $"'values' must be a list of 1 to {MaxWrite} register values to write.";
                        return false;
                    }

                    var written = new List<UInt16>();

                    foreach (var token in values)
                    {
                        if (token.Type != JTokenType.Integer || token.Value<Int64>() is < 0 or > UInt16.MaxValue)
                        {
                            Error = "Every one of 'values' must be a register value, a whole number between 0 and 65535.";
                            return false;
                        }
                        written.Add((UInt16) token.Value<Int64>());
                    }

                    if (address + written.Count - 1 > UInt16.MaxValue)
                    {
                        Error = $"{written.Count} registers from {address} on run past the last one, 65535.";
                        return false;
                    }

                    Request = new ModbusRequest((Byte) function, (UInt16) address, (UInt16) written.Count, written);
                    return true;

            }

        }

        #endregion

        #region ToPDU()

        /// <summary>
        /// The request as it goes inside the frame.
        /// </summary>
        public Byte[] ToPDU()
        {

            switch (Function)
            {

                case WriteSingleRegister:
                    return ModbusPDU.BuildWriteSingleRegister(Address, Values[0]);

                case WriteMultipleRegisters:
                    return ModbusPDU.BuildWriteMultipleRegisters(Address, [.. Values]);

                default:
                    var pdu = new Byte[5];
                    pdu[0] = Function;
                    BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(1, 2), Address);
                    BinaryPrimitives.WriteUInt16BigEndian(pdu.AsSpan(3, 2), Count);
                    return pdu;

            }

        }

        #endregion

        #region (static) NameOf(Function)

        /// <summary>
        /// What a function is called, by its code.
        /// </summary>
        public static String NameOf(Byte Function)

            => (Function & 0x7F) switch {
                   ReadHoldingRegisters    => "Read Holding Registers",
                   ReadInputRegisters      => "Read Input Registers",
                   WriteSingleRegister     => "Write Single Register",
                   WriteMultipleRegisters  => "Write Multiple Registers",
                   var other               => $"function {other}"
               };

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Operation == ModbusOperation.Read
                   ? $"{Name} ({Function:D2}): {Count} from {Address}"
                   : $"{Name} ({Function:D2}): {String.Join(", ", Values)} at {Address}";

        #endregion

        #region (private static) TryReadNumber(JSON, Name, Min, Max, out Number, out Error)

        private static Boolean TryReadNumber(JObject                           JSON,
                                             String                            Name,
                                             Int64                             Min,
                                             Int64                             Max,
                                             out Int64                         Number,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Number = 0;
            Error  = null;

            if (JSON[Name] is not JToken token || token.Type != JTokenType.Integer ||
                token.Value<Int64>() < Min || token.Value<Int64>() > Max)
            {
                Error = $"'{Name}' must be a whole number between {Min} and {Max}.";
                return false;
            }

            Number = token.Value<Int64>();
            return true;

        }

        #endregion

    }

}
