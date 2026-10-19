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
    /// How the registers of a meter model are laid out.
    /// </summary>
    public enum MeterLayout
    {

        /// <summary>
        /// SunSpec 201 to 204: integers, each quantity with a scale factor - a
        /// power of ten - of its own.
        /// </summary>
        Integer,

        /// <summary>
        /// SunSpec 211 to 214: 32-bit floats, two registers each.
        /// </summary>
        Float,

        /// <summary>
        /// The 24 registers Hermod's simulated meter answers under model 213:
        /// integers with scale factors, and not the floats SunSpec's 213 has -
        /// what a ModbusTLSEnergyMeter is read by.
        /// </summary>
        HermodDemo

    }


    /// <summary>
    /// What a SunSpec meter said when it was last read: who it is, and what it
    /// measures - from whichever of SunSpec's meter models it is read by.
    /// </summary>
    /// <remarks>
    /// A value the meter does not implement - SunSpec's 0x8000 for a signed
    /// integer, a scale factor that is one, a float that is not a number - is
    /// null here rather than a number nobody measured.
    /// </remarks>
    /// <param name="At">When it was read.</param>
    /// <param name="Manufacturer">The manufacturer the meter names.</param>
    /// <param name="Model">Its model.</param>
    /// <param name="Version">Its firmware version.</param>
    /// <param name="SerialNumber">Its serial number.</param>
    /// <param name="Power_W">The real power, in W.</param>
    /// <param name="Current_A">The current, in A.</param>
    /// <param name="Voltage_V">The voltage between each phase and neutral, in V.</param>
    /// <param name="Frequency_Hz">The frequency, in Hz.</param>
    /// <param name="Imported_Wh">The energy it counted flowing in, in Wh.</param>
    /// <param name="Exported_Wh">The energy it counted flowing back, in Wh.</param>
    /// <param name="SunSpecModel">The SunSpec meter model it was read by.</param>
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
                                      Decimal?        Exported_Wh,
                                      UInt16          SunSpecModel)
    {

        #region Data

        /// <summary>
        /// How many registers of model 213 Hermod's simulated meter answers.
        /// </summary>
        public const UInt16  HermodDemoLength  = SunSpecMeterMap.MeterModelLength;

        // Where a quantity is in a model of 201 to 204, counted from the first
        // register after its length - SunSpec's own order, the same for all
        // four.
        private const Int32  I_A      =  0,  I_AphA   =  1,  I_AphB   =  2,  I_AphC   =  3,  I_A_SF   =  4;
        private const Int32  I_PhV    =  5,  I_PhVphA =  6,  I_PhVphB =  7,  I_PhVphC =  8,  I_V_SF   = 13;
        private const Int32  I_Hz     = 14,  I_Hz_SF  = 15;
        private const Int32  I_W      = 16,  I_WphA   = 17,  I_WphB   = 18,  I_WphC   = 19,  I_W_SF   = 20;
        private const Int32  I_WhExp  = 36,  I_WhImp  = 44,  I_Wh_SF  = 52;

        // And in a model of 211 to 214, two registers to a float.
        private const Int32  F_A      =  0,  F_AphA   =  2,  F_AphB   =  4,  F_AphC   =  6;
        private const Int32  F_PhV    =  8,  F_PhVphA = 10,  F_PhVphB = 12,  F_PhVphC = 14;
        private const Int32  F_Hz     = 24;
        private const Int32  F_W      = 26,  F_WphA   = 28,  F_WphB   = 30,  F_WphC   = 32;
        private const Int32  F_WhExp  = 58,  F_WhImp  = 66;

        // And in Hermod's 24, from its map: counted from the first register
        // after the length, as the others are.
        private const Int32  H        = SunSpecMeterMap.OffMeterId + 2;

        #endregion


        #region (static) LayoutOf(Model)

        /// <summary>
        /// How a meter model is laid out, where it is one this station can read:
        /// long enough for every quantity it reads from it.
        /// </summary>
        public static MeterLayout? LayoutOf(SunSpecModel Model)

            => Model.Id switch {
                   213 when Model.Length == HermodDemoLength          => MeterLayout.HermodDemo,
                   >= 201 and <= 204 when Model.Length > I_Wh_SF      => MeterLayout.Integer,
                   >= 211 and <= 214 when Model.Length > F_WhImp + 1  => MeterLayout.Float,
                   _                                                  => null
               };

        #endregion

        #region (static) TryDecode(Device, Block, At, out Reading, out Error)

        /// <summary>
        /// The registers of a device's meter model, as what they mean - or why
        /// they do not mean anything.
        /// </summary>
        /// <param name="Device">The device, and the model it is read by.</param>
        /// <param name="Block">Every register of that model, its id and length included.</param>
        /// <param name="At">When they were read.</param>
        /// <param name="Reading">What they say.</param>
        /// <param name="Error">Why they say nothing.</param>
        public static Boolean TryDecode(SunSpecDevice                            Device,
                                        IReadOnlyList<UInt16>                    Block,
                                        DateTimeOffset                           At,
                                        [NotNullWhen(true)]  out MeterReading?  Reading,
                                        [NotNullWhen(false)] out String?        Error)
        {

            Reading = null;

            var meter = Device.Meter;

            if (Block.Count < meter.Registers || Block[0] != meter.Id || Block[1] != meter.Length)
            {
                Error = $"Where model {meter.Id} with {meter.Length} registers was, it answered model " +
                        $"{(Block.Count > 0 ? Block[0].ToString() : "-")} with {(Block.Count > 1 ? Block[1].ToString() : "-")}: " +
                         "the device rearranged its models.";
                return false;
            }

            if (LayoutOf(meter) is not MeterLayout layout)
            {
                Error = $"Model {meter.Id} with {meter.Length} registers is no layout this station can read.";
                return false;
            }

            // The quantities, from the first register after the length.
            var data = Block.Skip(2).ToArray();

            Reading = layout switch {

                          MeterLayout.Float       => Float  (Device, data, At),

                          MeterLayout.HermodDemo  => Integer(Device, data, At,
                                                             A:   (SunSpecMeterMap.OffMeterA        - H, SunSpecMeterMap.OffMeterAphA     - H, SunSpecMeterMap.OffMeterAphB   - H, SunSpecMeterMap.OffMeterAphC   - H, SunSpecMeterMap.OffMeterA_SF - H),
                                                             V:   (SunSpecMeterMap.OffMeterPhV      - H, SunSpecMeterMap.OffMeterPhVphA   - H, SunSpecMeterMap.OffMeterPhVphB - H, SunSpecMeterMap.OffMeterPhVphC - H, SunSpecMeterMap.OffMeterV_SF - H),
                                                             W:   (SunSpecMeterMap.OffMeterW        - H, SunSpecMeterMap.OffMeterWphA     - H, SunSpecMeterMap.OffMeterWphB   - H, SunSpecMeterMap.OffMeterWphC   - H, SunSpecMeterMap.OffMeterW_SF - H),
                                                             Hz:  (SunSpecMeterMap.OffMeterHz       - H, SunSpecMeterMap.OffMeterHz_SF    - H),
                                                             Wh:  (SunSpecMeterMap.OffMeterTotWhImp - H, SunSpecMeterMap.OffMeterTotWhExp - H, SunSpecMeterMap.OffMeterWh_SF  - H)),

                          _                       => Integer(Device, data, At,
                                                             A:   (I_A,     I_AphA,   I_AphB,   I_AphC,   I_A_SF),
                                                             V:   (I_PhV,   I_PhVphA, I_PhVphB, I_PhVphC, I_V_SF),
                                                             W:   (I_W,     I_WphA,   I_WphB,   I_WphC,   I_W_SF),
                                                             Hz:  (I_Hz,    I_Hz_SF),
                                                             Wh:  (I_WhImp, I_WhExp,  I_Wh_SF))

                      };

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
                   new JProperty("sunSpecModel",  SunSpecModel),
                   new JProperty("power_W",       Power_W.  ToJSON()),
                   new JProperty("current_A",     Current_A.ToJSON()),
                   new JProperty("voltage_V",     Voltage_V.ToJSON()),
                   new JProperty("frequency_Hz",  Frequency_Hz),
                   new JProperty("imported_Wh",   Imported_Wh),
                   new JProperty("exported_Wh",   Exported_Wh)
               );

        #endregion


        #region (private static) Integer(Device, Data, At, A, V, W, Hz, Wh)

        /// <summary>
        /// A model of integers, each quantity scaled by a power of ten of its
        /// own, at the given places.
        /// </summary>
        private static MeterReading Integer(SunSpecDevice                                         Device,
                                            UInt16[]                                              Data,
                                            DateTimeOffset                                        At,
                                            (Int32 All, Int32 L1, Int32 L2, Int32 L3, Int32 SF)   A,
                                            (Int32 All, Int32 L1, Int32 L2, Int32 L3, Int32 SF)   V,
                                            (Int32 All, Int32 L1, Int32 L2, Int32 L3, Int32 SF)   W,
                                            (Int32 Value, Int32 SF)                               Hz,
                                            (Int32 Imported, Int32 Exported, Int32 SF)            Wh)
        {

            Decimal? Scaled(Int32 Offset, Int32 ScaleFactorOffset)
            {

                var raw          = Data[Offset];
                var scaleFactor  = Data[ScaleFactorOffset];

                return raw == 0x8000 || scaleFactor == 0x8000
                           ? null
                           : Scale((Int16) raw, (Int16) scaleFactor);

            }

            // An accumulator that is not implemented reads zero in SunSpec; a
            // scale factor that is not reads 0x8000.
            Decimal? Counter(Int32 Offset)

                => Data[Wh.SF] == 0x8000
                       ? null
                       : Scale(((UInt32) Data[Offset] << 16) | Data[Offset + 1], (Int16) Data[Wh.SF]);

            PhaseValues Phases((Int32 All, Int32 L1, Int32 L2, Int32 L3, Int32 SF) Where)

                => new (Scaled(Where.All, Where.SF), Scaled(Where.L1, Where.SF), Scaled(Where.L2, Where.SF), Scaled(Where.L3, Where.SF));

            return new MeterReading(
                       At,
                       Device.Manufacturer,
                       Device.Model,
                       Device.Version,
                       Device.SerialNumber,
                       Phases(W),
                       Phases(A),
                       Phases(V),
                       Scaled(Hz.Value, Hz.SF),
                       Counter(Wh.Imported),
                       Counter(Wh.Exported),
                       Device.Meter.Id
                   );

        }

        #endregion

        #region (private static) Float(Device, Data, At)

        /// <summary>
        /// A model of 32-bit floats, two registers each.
        /// </summary>
        private static MeterReading Float(SunSpecDevice   Device,
                                          UInt16[]        Data,
                                          DateTimeOffset  At)
        {

            Decimal? Value(Int32 Offset)
            {

                var value = BitConverter.Int32BitsToSingle((Data[Offset] << 16) | Data[Offset + 1]);

                // Not a number is SunSpec's "not implemented"; nothing too large
                // for a decimal is a measurement either.
                return Single.IsFinite(value) && Math.Abs(value) < 1e15f
                           ? Math.Round((Decimal) value, 3)
                           : null;

            }

            PhaseValues Phases(Int32 All, Int32 L1, Int32 L2, Int32 L3)

                => new (Value(All), Value(L1), Value(L2), Value(L3));

            return new MeterReading(
                       At,
                       Device.Manufacturer,
                       Device.Model,
                       Device.Version,
                       Device.SerialNumber,
                       Phases(F_W,   F_WphA,   F_WphB,   F_WphC),
                       Phases(F_A,   F_AphA,   F_AphB,   F_AphC),
                       Phases(F_PhV, F_PhVphA, F_PhVphB, F_PhVphC),
                       Value(F_Hz),
                       Value(F_WhImp),
                       Value(F_WhExp),
                       Device.Meter.Id
                   );

        }

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

    }

}
