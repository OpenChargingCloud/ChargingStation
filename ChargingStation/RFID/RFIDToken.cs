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

#endregion

namespace cloud.charging.open.ChargingStation.RFID
{

    /// <summary>
    /// The unique identifier of a card somebody held against a reader.
    /// </summary>
    /// <remarks>
    /// Kept as the hexadecimal it is read as, in upper case and without
    /// separators, so that the same card read twice is the same string both
    /// times - readers differ about colons, dashes and case, and a station that
    /// passed those on as it found them would report one card as several.
    ///
    /// A UID is not a secret and is not an authorisation. It is the number
    /// printed into the card, readable by anybody standing next to it, which is
    /// why what it is worth is decided elsewhere and not here.
    /// </remarks>
    public sealed record RFIDToken
    {

        #region Data

        /// <summary>
        /// The shortest a UID can be: four bytes, which is the single-size UID
        /// of ISO 14443.
        /// </summary>
        public const Int32  MinimumLength  = 8;

        /// <summary>
        /// The longest this station reads. Ten bytes covers the triple-size
        /// UIDs, and the ISO 15693 and Calypso identifiers that are longer.
        /// </summary>
        public const Int32  MaximumLength  = 40;

        #endregion

        #region Properties

        /// <summary>
        /// The UID, in upper-case hexadecimal without separators.
        /// </summary>
        public String  UID  { get; }

        #endregion

        #region Constructor(s)

        private RFIDToken(String UID)
        {
            this.UID = UID;
        }

        #endregion


        #region (static) TryParse(Text, out Token, out Error)

        /// <summary>
        /// A UID as somebody typed or a reader reported it.
        /// </summary>
        public static Boolean TryParse(String?                             Text,
                                       [NotNullWhen(true)]  out RFIDToken?  Token,
                                       [NotNullWhen(false)] out String?     Error)
        {

            Token  = null;
            Error  = null;

            // Readers disagree about separators, and so do the numbers printed
            // on the back of cards: "04 A2 2B" and "04-A2-2B" and "04a22b" are
            // one card.
            var text = new String((Text ?? "").Where(character => character is not (' ' or ':' or '-' or '.')).ToArray()).
                           ToUpperInvariant();

            if (text.Length == 0)
            {
                Error = "An RFID UID is needed.";
                return false;
            }

            if (!text.All(Uri.IsHexDigit))
            {
                Error = "An RFID UID is hexadecimal - the digits 0 to 9 and the letters A to F.";
                return false;
            }

            if (text.Length % 2 != 0)
            {
                Error = "An RFID UID has two hexadecimal digits per byte, so an even number of them.";
                return false;
            }

            if (text.Length < MinimumLength || text.Length > MaximumLength)
            {
                Error = $"An RFID UID is between {MinimumLength / 2} and {MaximumLength / 2} bytes long.";
                return false;
            }

            Token = new RFIDToken(text);
            return true;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => UID;

        #endregion

    }

}
