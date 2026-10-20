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

using System.Text;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// The session model of a meter that signs charging sessions: model 64900,
    /// behind the meter's own models, through which a session is begun with who
    /// charges, where and on which tariff, and ended into one signed OCMF
    /// document.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no standard for this. It is the model the Modbus/TLS energy
    /// meter of OpenChargingCloud puts in front of whatever device it measures
    /// with, and its README says it register by register; this is the station's
    /// side of it. A meter that has no such model is read, and signs nothing.
    /// </para>
    /// <para>
    /// What a connection writes before a command is that connection's own, and
    /// gone once the command was done: the parameters and the command go over
    /// one connection, shown the identity this station writes with.
    /// </para>
    /// <para>
    /// Texts are UTF-8, two bytes to a register with the first in the high
    /// byte, padded with NUL.
    /// </para>
    /// </remarks>
    public static class MeterSessionModel
    {

        #region Data

        /// <summary>The id of the model.</summary>
        public const UInt16  Id                        = 64900;

        // Points, counted from the register after the model's length.

        public const UInt16  Command                   = 0;
        public const UInt16  State                     = 1;
        public const UInt16  Error                     = 2;
        public const UInt16  ErrorText                 = 4;
        public const UInt16  ErrorTextLength           = 64;
        public const UInt16  SessionId                 = 68;
        public const UInt16  SessionIdLength           = 32;
        public const UInt16  IdentificationType        = 100;
        public const UInt16  IdentificationTypeLength  = 8;
        public const UInt16  Identification            = 108;
        public const UInt16  IdentificationLength      = 40;
        public const UInt16  ChargePointIdType         = 148;
        public const UInt16  ChargePointIdTypeLength   = 8;
        public const UInt16  ChargePointId             = 156;
        public const UInt16  ChargePointIdLength       = 40;
        public const UInt16  TariffText                = 196;
        public const UInt16  TariffTextLength          = 128;
        public const UInt16  CurrentSessionId          = 324;
        public const UInt16  PublicKeyLength           = 356;
        public const UInt16  PublicKey                 = 357;
        public const UInt16  PublicKeyRegisters        = 2600;
        public const UInt16  DocumentLength            = 2957;
        public const UInt16  Document                  = 2958;
        public const UInt16  DocumentRegisters         = 8192;

        /// <summary>
        /// The registers that follow the model's length: every point above has
        /// to be inside them.
        /// </summary>
        public const UInt16  Length                    = Document + DocumentRegisters;

        /// <summary>The command that begins a session.</summary>
        public const UInt16  Start                     = 1;

        /// <summary>The command that ends it.</summary>
        public const UInt16  Stop                      = 2;

        /// <summary>The states: none, running, stopped with its document there.</summary>
        public const UInt16  StateNone                 = 0;
        public const UInt16  StateRunning              = 1;
        public const UInt16  StateStopped              = 2;

        /// <summary>
        /// The most registers one write carries, as Modbus has it.
        /// </summary>
        public const Int32   MaxWrite                  = 123;

        #endregion


        #region (static) Encode(Text, Registers) / Decode(Registers)

        /// <summary>
        /// A text as registers: UTF-8, the first byte high, padded with NUL.
        /// </summary>
        /// <exception cref="ArgumentException">When it does not fit.</exception>
        public static UInt16[] Encode(String?  Text,
                                      Int32    Registers)
        {

            var bytes = Encoding.UTF8.GetBytes(Text ?? "");

            if (bytes.Length > Registers * 2)
                throw new ArgumentException($"{bytes.Length} bytes do not fit into {Registers} registers.", nameof(Text));

            var registers = new UInt16[Registers];

            for (var i = 0; i < bytes.Length; i++)
                registers[i / 2] |= (UInt16) (i % 2 == 0 ? bytes[i] << 8 : bytes[i]);

            return registers;

        }

        /// <summary>
        /// The text in some registers, up to the first NUL; null where it is not
        /// UTF-8.
        /// </summary>
        public static String? Decode(IReadOnlyList<UInt16> Registers)
        {

            var bytes = new List<Byte>(Registers.Count * 2);

            foreach (var register in Registers)
            {

                if (register >> 8 == 0)
                    break;

                bytes.Add((Byte) (register >> 8));

                if ((register & 0xFF) == 0)
                    break;

                bytes.Add((Byte) (register & 0xFF));

            }

            try
            {
                return new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
            catch (DecoderFallbackException)
            {
                return null;
            }

        }

        /// <summary>
        /// The number of bytes a text has, for the fields it has to fit into.
        /// </summary>
        public static Int32 BytesOf(String? Text)

            => Encoding.UTF8.GetByteCount(Text ?? "");

        #endregion

    }

}
