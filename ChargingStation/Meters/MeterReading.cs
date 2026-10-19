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

using System.Diagnostics.CodeAnalysis;
using System.Text;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Three values of one quantity, one for each phase, and the one for all of
    /// them together - a total for power and current, the average for voltage.
    /// </summary>
    public sealed record PhaseValues(Decimal?  All,
                                     Decimal?  L1,
                                     Decimal?  L2,
                                     Decimal?  L3)
    {

        public JObject ToJSON()

            => new (
                   new JProperty("all",  All),
                   new JProperty("L1",   L1),
                   new JProperty("L2",   L2),
                   new JProperty("L3",   L3)
               );

    }


    /// <summary>
    /// What a SunSpec meter said when it was last read: who it is, and what it
    /// measures - SunSpec's common model and its three-phase meter model 213.
    /// </summary>
    /// <remarks>
    /// A value the meter does not implement - SunSpec's 0x8000 for a signed
    /// register, or a scale factor that is one - is null here rather than a
    /// number nobody measured.
    /// </remarks>
    /// <param name="At">When it was read.</param>
    /// <param name="Manufacturer">The manufacturer the meter names.</param>
    /// <param name="Model">Its model.</param>
    /// <param name="Version">Its firmware version.</param>
    /// <param name="SerialNumber">Its serial number.</param>
    /// <param name="Power_W">The real power, in W: positive where energy flows out of the grid, towards the vehicle.</param>
    /// <param name="Current_A">The current, in A.</param>
    /// <param name="Voltage_V">The voltage between each phase and neutral, in V.</param>
    /// <param name="Frequency_Hz">The frequency, in Hz.</param>
    /// <param name="Imported_Wh">The energy it counted flowing in, in Wh.</param>
    /// <param name="Exported_Wh">The energy it counted flowing back, in Wh.</param>
    public sealed record MeterReading(DateTimeOffset  At,
                                      String          Manufacturer,
                                      String          Model,
                                      String          Version,
                                      String          SerialNumber,
                                      PhaseValues     Power_W,
                                      PhaseValues     Current_A,
                                      PhaseValues     Voltage_V,
                                      Decimal?        Frequency_Hz,
                                      Decimal?        Imported_Wh,
                                      Decimal?        Exported_Wh)
    {

        #region Data

        /// <summary>
        /// Where a SunSpec device's registers begin, and with them its marker.
        /// </summary>
        public const UInt16  FirstRegister  = SunSpecMeterMap.BaseAddress;

        /// <summary>
        /// How many registers are read at once: the marker, the common model and
        /// the meter model, up to the end marker.
        /// </summary>
        public const UInt16  RegisterCount  = SunSpecMeterMap.RegisterCount;

        /// <summary>
        /// The three-phase wye meter model of SunSpec.
        /// </summary>
        public const UInt16  MeterModel     = SunSpecMeterMap.MeterModelId;

        #endregion


        #region (static) TryDecode(Registers, At, out Reading, out Error)

        /// <summary>
        /// The registers a meter answered with, as what they mean - or why they
        /// do not mean a SunSpec three-phase meter.
        /// </summary>
        /// <param name="Registers">The registers from <see cref="FirstRegister"/> on.</param>
        /// <param name="At">When they were read.</param>
        /// <param name="Reading">What they say.</param>
        /// <param name="Error">Why they say nothing.</param>
        public static Boolean TryDecode(IReadOnlyList<UInt16>                    Registers,
                                        DateTimeOffset                           At,
                                        [NotNullWhen(true)]  out MeterReading?  Reading,
                                        [NotNullWhen(false)] out String?        Error)
        {

            Reading = null;

            if (Registers.Count < RegisterCount)
            {
                Error = $"It answered with {Registers.Count} registers, where a SunSpec meter has {RegisterCount}.";
                return false;
            }

            if (Registers[SunSpecMeterMap.OffMarker]     != (UInt16) (SunSpecMeterMap.SunSpecMarker >> 16) ||
                Registers[SunSpecMeterMap.OffMarker + 1] != (UInt16) (SunSpecMeterMap.SunSpecMarker & 0xFFFF))
            {
                Error = $"Register {FirstRegister} does not begin with \"SunS\", so this is no SunSpec device.";
                return false;
            }

            if (Registers[SunSpecMeterMap.OffMeterId] != MeterModel)
            {
                Error = $"It is a SunSpec device, but its first model is {Registers[SunSpecMeterMap.OffMeterId]} and not the three-phase meter {MeterModel}.";
                return false;
            }

            Decimal? Scaled(UInt16 Offset, UInt16 ScaleFactorOffset)
            {

                var raw          = Registers[Offset];
                var scaleFactor  = Registers[ScaleFactorOffset];

                if (raw == 0x8000 || scaleFactor == 0x8000)
                    return null;

                return Scale((Int16) raw, (Int16) scaleFactor);

            }

            Decimal? ScaledCounter(UInt16 Offset)
            {

                var raw          = ((UInt32) Registers[Offset] << 16) | Registers[Offset + 1];
                var scaleFactor  = Registers[SunSpecMeterMap.OffMeterWh_SF];

                // An accumulator that is not implemented reads zero in SunSpec;
                // a scale factor that is not reads 0x8000.
                if (scaleFactor == 0x8000)
                    return null;

                return Scale(raw, (Int16) scaleFactor);

            }

            PhaseValues Phases(UInt16 All, UInt16 L1, UInt16 L2, UInt16 L3, UInt16 ScaleFactor)

                => new (Scaled(All, ScaleFactor),
                        Scaled(L1,  ScaleFactor),
                        Scaled(L2,  ScaleFactor),
                        Scaled(L3,  ScaleFactor));

            Reading = new MeterReading(
                          At,
                          Text(Registers, SunSpecMeterMap.OffCommonMn,  16),
                          Text(Registers, SunSpecMeterMap.OffCommonMd,  16),
                          Text(Registers, SunSpecMeterMap.OffCommonVr,   8),
                          Text(Registers, SunSpecMeterMap.OffCommonSN,  16),
                          Phases(SunSpecMeterMap.OffMeterW,   SunSpecMeterMap.OffMeterWphA,   SunSpecMeterMap.OffMeterWphB,   SunSpecMeterMap.OffMeterWphC,   SunSpecMeterMap.OffMeterW_SF),
                          Phases(SunSpecMeterMap.OffMeterA,   SunSpecMeterMap.OffMeterAphA,   SunSpecMeterMap.OffMeterAphB,   SunSpecMeterMap.OffMeterAphC,   SunSpecMeterMap.OffMeterA_SF),
                          Phases(SunSpecMeterMap.OffMeterPhV, SunSpecMeterMap.OffMeterPhVphA, SunSpecMeterMap.OffMeterPhVphB, SunSpecMeterMap.OffMeterPhVphC, SunSpecMeterMap.OffMeterV_SF),
                          Scaled(SunSpecMeterMap.OffMeterHz, SunSpecMeterMap.OffMeterHz_SF),
                          ScaledCounter(SunSpecMeterMap.OffMeterTotWhImp),
                          ScaledCounter(SunSpecMeterMap.OffMeterTotWhExp)
                      );

            Error = null;
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// What the meter said, as the EVSEs page shows it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("at",            At.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                   new JProperty("manufacturer",  Manufacturer),
                   new JProperty("model",         Model),
                   new JProperty("version",       Version),
                   new JProperty("serialNumber",  SerialNumber),
                   new JProperty("power_W",       Power_W.  ToJSON()),
                   new JProperty("current_A",     Current_A.ToJSON()),
                   new JProperty("voltage_V",     Voltage_V.ToJSON()),
                   new JProperty("frequency_Hz",  Frequency_Hz),
                   new JProperty("imported_Wh",   Imported_Wh),
                   new JProperty("exported_Wh",   Exported_Wh)
               );

        #endregion


        #region (private static) Scale(Value, ScaleFactor)

        /// <summary>
        /// A SunSpec value and its power of ten, as the number it stands for -
        /// in decimals, so that 229.6 V is 229.6 and not 229.59999.
        /// </summary>
        private static Decimal Scale(Int64  Value,
                                     Int16  ScaleFactor)
        {

            var value = (Decimal) Value;

            for (var i = 0; i < Math.Abs((Int32) ScaleFactor) && i < 10; i++)
                value = ScaleFactor > 0 ? value * 10 : value / 10;

            return value;

        }

        #endregion

        #region (private static) Text(Registers, Offset, Length)

        /// <summary>
        /// A SunSpec string: ASCII, two characters to a register, padded with NUL.
        /// </summary>
        private static String Text(IReadOnlyList<UInt16>  Registers,
                                   UInt16                 Offset,
                                   Int32                  Length)
        {

            var bytes = new Byte[Length * 2];

            for (var i = 0; i < Length; i++)
            {
                bytes[2 * i]     = (Byte) (Registers[Offset + i] >> 8);
                bytes[2 * i + 1] = (Byte) (Registers[Offset + i] & 0xFF);
            }

            return Encoding.ASCII.GetString(bytes).TrimEnd('\0', ' ');

        }

        #endregion

    }

}
