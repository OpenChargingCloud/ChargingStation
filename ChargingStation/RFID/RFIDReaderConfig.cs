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

#endregion

namespace cloud.charging.open.ChargingStation.RFID
{

    /// <summary>
    /// One RFID reader this charging station has, as it is configured to have
    /// it.
    /// </summary>
    /// <remarks>
    /// A station may have one reader for the whole housing or one per EVSE, and
    /// which of the two it is changes what the display has to say: a reader
    /// that belongs to the station has to ask which outlet the card is for, and
    /// a reader beside an outlet does not. So the placement is configuration
    /// rather than something to be discovered, and <see cref="EVSEId"/> being
    /// null is the station-wide case.
    ///
    /// The kinds are an open set for the same reason connector types are: a
    /// reader this station has no driver for is still a reader somebody bolted
    /// on, and refusing to write it down would not make it go away. A kind
    /// without a driver is configured, said so about once in the log, and does
    /// nothing.
    /// </remarks>
    /// <param name="Id">What this reader is called here.</param>
    /// <param name="Kind">What kind of reader it is.</param>
    /// <param name="EVSEId">Which EVSE it belongs to, or null when it serves the whole station.</param>
    /// <param name="Enabled">Whether it is read at all.</param>
    public sealed record RFIDReaderConfig(String    Id,
                                          String    Kind,
                                          Byte?     EVSEId,
                                          Boolean   Enabled = true)
    {

        #region Data

        /// <summary>
        /// The kind of reader that has no hardware behind it: the one whose
        /// cards are typed in.
        /// </summary>
        /// <remarks>
        /// Written out in full, with the vendor in front, because "fake" on its
        /// own in a configuration file read a year later is not obviously a
        /// deliberate choice by somebody who knew what they were doing.
        /// </remarks>
        public const String  FakeKind       = "GraphDefined.FakeRFID";

        /// <summary>
        /// The most readers one station may be given here.
        /// </summary>
        public const Int32   MaxReaders     = 65;

        /// <summary>
        /// The longest an id or a kind may be written.
        /// </summary>
        public const Int32   MaxNameLength  = 64;

        /// <summary>
        /// The kinds this station knows by name. Not a closed list - see the
        /// remarks on this record - but what the web interface offers.
        /// </summary>
        public static readonly IReadOnlyList<String> KnownKinds = [

            // The only one with anything behind it today.
            FakeKind,

            // Names of real readers, offered so that a station can be
            // described truthfully before this software can talk to one.
            "PC/SC",
            "Wiegand",
            "OSDP",
            "ISO14443"

        ];

        #endregion

        #region Properties

        /// <summary>
        /// Whether this reader serves the whole station rather than one EVSE.
        /// </summary>
        public Boolean IsStationWide
            => EVSEId is null;

        /// <summary>
        /// Whether this is the reader whose cards are typed in.
        /// </summary>
        public Boolean IsFake
            => String.Equals(Kind, FakeKind, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Whether this station has a driver for this kind of reader.
        /// </summary>
        /// <remarks>
        /// Exactly one today, and being honest about that is the point: a
        /// station that quietly did nothing with a configured reader would look
        /// like a station with a broken reader.
        /// </remarks>
        public Boolean HasDriver
            => IsFake;

        #endregion


        #region SamePlacementAs(Other)

        /// <summary>
        /// Whether this reader and the other one are the same device in the
        /// same place - everything except whether it is switched on.
        /// </summary>
        /// <remarks>
        /// The same split as the EVSEs: where a reader sits is a statement
        /// about the installation, and switching it off is not. See
        /// <see cref="Web.Permissions"/>.
        /// </remarks>
        public Boolean SamePlacementAs(RFIDReaderConfig Other)

            => String.Equals(Id,   Other.Id,   StringComparison.Ordinal) &&
               String.Equals(Kind, Other.Kind, StringComparison.Ordinal) &&
               EVSEId == Other.EVSEId;

        #endregion

        #region (static) TryParse(JSON, out Reader, out Error)

        /// <summary>
        /// One reader as the web interface sends it, or as the file keeps it.
        /// </summary>
        public static Boolean TryParse(JToken                                  JSON,
                                       [NotNullWhen(true)]  out RFIDReaderConfig? Reader,
                                       [NotNullWhen(false)] out String?           Error)
        {

            Reader  = null;
            Error   = null;

            if (JSON is not JObject json)
            {
                Error = "An RFID reader must be a JSON object.";
                return false;
            }

            #region Id

            var id = json.Value<String>("id")?.Trim();

            if (String.IsNullOrEmpty(id))
            {
                Error = "Every RFID reader needs an 'id'.";
                return false;
            }

            if (id.Length > MaxNameLength)
            {
                Error = $"An RFID reader id may be at most {MaxNameLength} characters long.";
                return false;
            }

            if (!id.All(character => Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                Error = $"The RFID reader id '{id}' may hold only letters, digits, '-', '_' and '.'.";
                return false;
            }

            #endregion

            #region Kind

            var kind = json.Value<String>("kind")?.Trim();

            if (String.IsNullOrEmpty(kind))
            {
                Error = $"The RFID reader '{id}' needs a 'kind'.";
                return false;
            }

            if (kind.Length > MaxNameLength)
            {
                Error = $"An RFID reader kind may be at most {MaxNameLength} characters long.";
                return false;
            }

            if (kind.Any(Char.IsControl))
            {
                Error = $"The kind of the RFID reader '{id}' may not hold control characters.";
                return false;
            }

            // Anything is a kind - a reader this station has no driver for is
            // still a reader somebody bolted on - but one this station names
            // itself is written the way this station writes it.
            kind = KnownKinds.FirstOrDefault(candidate => String.Equals(candidate, kind, StringComparison.OrdinalIgnoreCase))
                       ?? kind;

            #endregion

            #region EVSE

            Byte? evseId = null;

            if (json["evse"] is JToken evseToken && evseToken.Type != JTokenType.Null)
            {

                var value = evseToken.Type == JTokenType.Integer
                                ? evseToken.Value<Int64>()
                                : -1;

                if (value < 1 || value > EVSEs.EVSEConfig.MaxEVSEs)
                {
                    Error = $"The RFID reader '{id}': 'evse' is the number of the EVSE it belongs to, or null for the whole station.";
                    return false;
                }

                evseId = (Byte) value;

            }

            #endregion

            Reader = new RFIDReaderConfig(
                         id,
                         kind,
                         evseId,
                         json.Value<Boolean?>("enabled") ?? true
                     );

            return true;

        }

        #endregion

        #region (static) TryParseList(JSON, out Readers, out Error)

        /// <summary>
        /// All of them, with the ids checked for being told apart and at most
        /// one reader per place.
        /// </summary>
        public static Boolean TryParseList(JArray                                                   JSON,
                                           [NotNullWhen(true)]  out IReadOnlyList<RFIDReaderConfig>? Readers,
                                           [NotNullWhen(false)] out String?                          Error)
        {

            Readers  = null;
            Error    = null;

            var parsed = new List<RFIDReaderConfig>();

            foreach (var token in JSON)
            {

                if (!TryParse(token, out var reader, out Error))
                    return false;

                if (parsed.Any(other => String.Equals(other.Id, reader.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Error = $"There are two RFID readers called '{reader.Id}'; an id is how one of them is taken off again.";
                    return false;
                }

                // Two readers at one outlet is not a station anybody built, and
                // it would leave the display with two symbols and no way to say
                // which card goes where.
                if (parsed.Any(other => other.EVSEId == reader.EVSEId))
                {
                    Error = reader.IsStationWide
                                ? "A charging station has at most one RFID reader for the whole housing."
                                : $"EVSE {reader.EVSEId} already has an RFID reader.";
                    return false;
                }

                parsed.Add(reader);

            }

            if (parsed.Count > MaxReaders)
            {
                Error = $"A charging station may have at most {MaxReaders} RFID readers.";
                return false;
            }

            Readers = parsed;
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The reader as the web interface reads it and the file keeps it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",       Id),
                   new JProperty("kind",     Kind),
                   new JProperty("evse",     EVSEId.HasValue ? EVSEId.Value : null),
                   new JProperty("enabled",  Enabled)
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Id} ({Kind}) " +
               (IsStationWide ? "for the whole station" : $"at EVSE {EVSEId}") +
               (Enabled ? "" : ", switched off");

        #endregion

    }

}
