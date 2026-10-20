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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Reads registers of a device: the first, and how many.
    /// </summary>
    public delegate Task<UInt16[]> ReadRegisters(UInt16 First, UInt16 Count);


    /// <summary>
    /// A device that answers, but is not a SunSpec meter this station can read:
    /// no marker, no meter among its models, a chain of models that does not
    /// hold together.
    /// </summary>
    public sealed class NotASunSpecMeter(String Message) : Exception(Message);


    /// <summary>
    /// One model of a SunSpec device: its id, where its id register is, and how
    /// many registers follow its length register.
    /// </summary>
    public sealed record SunSpecModel(UInt16 Id,
                                      UInt16 Address,
                                      UInt16 Length)
    {

        /// <summary>
        /// Every register of it, its id and its length included.
        /// </summary>
        public Int32 Registers
            => Length + 2;

        public JObject ToJSON()

            => new (
                   new JProperty("id",       Id),
                   new JProperty("address",  Address),
                   new JProperty("length",   Length)
               );

    }


    /// <summary>
    /// A SunSpec device as it describes itself: where its registers begin,
    /// the chain of models it has, who it says it is, and the meter model this
    /// station reads it by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SunSpec device begins with "SunS" at 40000, at 50000 or at 0, and then
    /// lists its models one after the other: an id, a length, and as many
    /// registers as the length says - up to the id 0xFFFF that ends the list.
    /// Where a model is, is known only by walking the list from its beginning:
    /// a meter puts the common model first and its meter model wherever it
    /// pleases after it, often behind models of its own.
    /// </para>
    /// <para>
    /// Walked once, when a connection is made; a device does not rearrange its
    /// models while it is connected to.
    /// </para>
    /// </remarks>
    /// <param name="Base">Where its registers begin: where "SunS" is.</param>
    /// <param name="Models">Its models, in the order it lists them.</param>
    /// <param name="Manufacturer">Who made it, as its common model says.</param>
    /// <param name="Model">What it is called there.</param>
    /// <param name="Version">Its version there.</param>
    /// <param name="SerialNumber">Its serial number there.</param>
    /// <param name="Meter">The meter model it is read by.</param>
    /// <param name="Sessions">The session model charging sessions are signed through, where it has one - see <see cref="MeterSessionModel"/>.</param>
    public sealed record SunSpecDevice(UInt16                       Base,
                                       IReadOnlyList<SunSpecModel>  Models,
                                       String                       Manufacturer,
                                       String                       Model,
                                       String                       Version,
                                       String                       SerialNumber,
                                       SunSpecModel                 Meter,
                                       SunSpecModel?                Sessions   = null)
    {

        #region Data

        /// <summary>
        /// Where a SunSpec device may begin, in the order they are tried.
        /// </summary>
        public static readonly IReadOnlyList<UInt16>  Bases          = [ 40000, 50000, 0 ];

        /// <summary>
        /// "SunS", as two registers.
        /// </summary>
        public const UInt16  MarkerHigh     = 0x5375;
        public const UInt16  MarkerLow      = 0x6E53;

        /// <summary>
        /// The id that ends the list of models.
        /// </summary>
        public const UInt16  EndOfModels    = 0xFFFF;

        /// <summary>
        /// The common model: who the device says it is.
        /// </summary>
        public const UInt16  CommonModel    = 1;

        /// <summary>
        /// The meter models: single phase, split phase, wye and delta, with
        /// integers and scale factors (201 to 204) or with floats (211 to 214).
        /// The first of them in the list is the one a device is read by.
        /// </summary>
        public static readonly IReadOnlySet<UInt16>  MeterModels   = new HashSet<UInt16> { 201, 202, 203, 204, 211, 212, 213, 214 };

        /// <summary>
        /// The most models a list is walked for, and the most registers one
        /// request asks for, as Modbus has it.
        /// </summary>
        public const Int32   MaxModels      = 64;
        public const UInt16  MaxRead        = 125;

        #endregion


        #region (static) Discover(Read)

        /// <summary>
        /// Find where a device begins, walk its list of models, and read who it
        /// says it is.
        /// </summary>
        /// <exception cref="NotASunSpecMeter">Where it is no SunSpec meter this station can read, with why.</exception>
        public static async Task<SunSpecDevice> Discover(ReadRegisters Read)
        {

            UInt16?          found    = null;
            ModbusException? refused  = null;
            var              answered = false;

            foreach (var candidate in Bases)
            {

                UInt16[] marker;

                try
                {
                    marker = await Read(candidate, 2);
                }
                catch (ModbusException e)
                {
                    // An address it does not have, and the marker is not
                    // there - or a request it refuses whatever the address,
                    // which is anything but IllegalDataAddress.
                    if (e.Code != ModbusExceptionCode.IllegalDataAddress)
                        refused ??= e;
                    continue;
                }

                answered = true;

                if (marker.Length >= 2 && marker[0] == MarkerHigh && marker[1] == MarkerLow)
                {
                    found = candidate;
                    break;
                }

            }

            // Refused at every address it may begin at: that is a meter saying
            // no to whoever asks, and not one saying it is no SunSpec device.
            if (found is null && !answered && refused is not null)
                throw refused;

            if (found is not UInt16 @base)
                throw new NotASunSpecMeter($"There is no \"SunS\" at {String.Join(", ", Bases)}, where a SunSpec device begins, so this is no SunSpec device.");

            var models  = new List<SunSpecModel>();
            var address = @base + 2;

            while (true)
            {

                if (address + 1 > UInt16.MaxValue)
                    throw new NotASunSpecMeter($"Its list of models runs past the last register, after {Listed(models)}.");

                var header = await Read((UInt16) address, 2);

                if (header.Length < 2)
                    throw new NotASunSpecMeter($"It answered {header.Length} register(s) for the header of a model at {address}.");

                if (header[0] == EndOfModels)
                    break;

                if (models.Count >= MaxModels)
                    throw new NotASunSpecMeter($"Its list of models does not end after {MaxModels} of them: {Listed(models)}.");

                models.Add(new SunSpecModel(header[0], (UInt16) address, header[1]));

                address += 2 + header[1];

            }

            var common = models.FirstOrDefault(model => model.Id == CommonModel)
                             ?? throw new NotASunSpecMeter($"It has no common model, only {Listed(models)}.");

            var meter  = models.FirstOrDefault(model => MeterModels.Contains(model.Id))
                             ?? throw new NotASunSpecMeter($"It is a SunSpec device, but none of its models is a meter: {Listed(models)}.");

            if (MeterReading.LayoutOf(meter) is null)
                throw new NotASunSpecMeter($"Its meter model is {meter.Id} with {meter.Length} registers, which is no layout of model {meter.Id} this station can read.");

            if (common.Length < 64)
                throw new NotASunSpecMeter($"Its common model has {common.Length} registers, where it has 65 or 66.");

            var registers = await ReadBlock(Read, common.Address, common.Registers);

            return new SunSpecDevice(
                       @base,
                       models,
                       Text(registers,  2, 16),
                       Text(registers, 18, 16),
                       Text(registers, 42,  8),
                       Text(registers, 50, 16),
                       meter,

                       // One that is shorter than the model is not this model.
                       models.FirstOrDefault(model => model.Id     == MeterSessionModel.Id &&
                                                      model.Length >= MeterSessionModel.Length)
                   );

        }

        #endregion

        #region ReadMeter(Read)

        /// <summary>
        /// Every register of the meter model, its id and length included - in
        /// as many requests as that takes.
        /// </summary>
        public Task<UInt16[]> ReadMeter(ReadRegisters Read)

            => ReadBlock(Read, Meter.Address, Meter.Registers);

        #endregion

        #region (static) ReadBlock(Read, First, Count)

        /// <summary>
        /// A block of registers, in requests of at most 125.
        /// </summary>
        public static async Task<UInt16[]> ReadBlock(ReadRegisters  Read,
                                                     UInt16         First,
                                                     Int32          Count)
        {

            var block = new List<UInt16>(Count);

            while (block.Count < Count)
            {

                var count = (UInt16) Math.Min(MaxRead, Count - block.Count);
                var read  = await Read((UInt16) (First + block.Count), count);

                if (read.Length != count)
                    throw new NotASunSpecMeter($"Asked for {count} registers from {First + block.Count}, it answered {read.Length}.");

                block.AddRange(read);

            }

            return [.. block];

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// How it describes itself, as the Meters page shows it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("base",    Base),
                   new JProperty("models",    new JArray(Models.Select(model => model.ToJSON()))),
                   new JProperty("meter",     Meter.Id),
                   new JProperty("sessions",  Sessions is not null)
               );

        #endregion


        #region (private static) Listed(Models) / Text(Registers, Offset, Length)

        private static String Listed(IEnumerable<SunSpecModel> Models)
        {
            var ids = Models.Select(model => model.Id.ToString()).ToArray();
            return ids.Length == 0 ? "none" : String.Join(", ", ids);
        }

        /// <summary>
        /// A SunSpec string: ASCII, two characters to a register, padded with
        /// NUL - what is no printable character left out.
        /// </summary>
        private static String Text(IReadOnlyList<UInt16>  Registers,
                                   Int32                  Offset,
                                   Int32                  Length)
        {

            var text = new StringBuilder(Length * 2);

            for (var i = 0; i < Length && Offset + i < Registers.Count; i++)
                foreach (var character in new[] { (Char) (Registers[Offset + i] >> 8), (Char) (Registers[Offset + i] & 0xFF) })
                    if (character is >= ' ' and <= '~')
                        text.Append(character);

            return text.ToString().Trim();

        }

        #endregion

    }

}
