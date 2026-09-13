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

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// Somebody whose customers charge here, as far as the display is
    /// concerned: a name, maybe a logo, and how their cards are recognised.
    /// </summary>
    /// <remarks>
    /// Deliberately only what the display needs. This is not a roaming
    /// database and must not grow into one: a charging station knows who its
    /// operator is and what it can print on a screen, and everything else
    /// about a contract is somebody else's to know.
    ///
    /// Cards are recognised by the start of their UID, which is what the card
    /// itself carries - the first bytes of an ISO 14443 UID are the
    /// manufacturer, so this is a coarse rule rather than a contract lookup. It
    /// is enough to put a name on a screen and is not enough to bill anybody,
    /// which is the right way round for a thing on a wall in public.
    /// </remarks>
    /// <param name="Id">What this provider is called here.</param>
    /// <param name="Name">What the display shows.</param>
    /// <param name="Logo">A URL to a logo, or a data: URI holding one.</param>
    /// <param name="TokenPrefixes">The UID prefixes that belong to this provider.</param>
    public sealed record EMobilityProvider(String                 Id,
                                           String                 Name,
                                           String?                Logo,
                                           IReadOnlyList<String>  TokenPrefixes)
    {

        #region Data

        /// <summary>
        /// The most providers this station will hold.
        /// </summary>
        public const Int32  MaxProviders      = 64;

        /// <summary>
        /// The most UID prefixes one provider may be recognised by.
        /// </summary>
        public const Int32  MaxPrefixes       = 32;

        /// <summary>
        /// The longest a name may be. A display is a display.
        /// </summary>
        public const Int32  MaxNameLength     = 60;

        /// <summary>
        /// The most logo this station will hold, whether it is a URL or the
        /// picture itself as a data: URI.
        /// </summary>
        /// <remarks>
        /// Generous enough for an SVG or a small PNG and nowhere near enough
        /// for a photograph, because this ends up in a configuration file that
        /// somebody has to be able to read.
        /// </remarks>
        public const Int32  MaxLogoLength     = 64 * 1024;

        #endregion


        #region Matches(Token)

        /// <summary>
        /// Whether the given card looks like one of this provider's.
        /// </summary>
        public Boolean Matches(RFID.RFIDToken Token)

            => TokenPrefixes.Any(prefix => Token.UID.StartsWith(prefix, StringComparison.Ordinal));

        #endregion

        #region (static) TryParse(JSON, out Provider, out Error)

        /// <summary>
        /// One provider as the file keeps it.
        /// </summary>
        public static Boolean TryParse(JToken                                     JSON,
                                       [NotNullWhen(true)]  out EMobilityProvider? Provider,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Provider  = null;
            Error     = null;

            if (JSON is not JObject json)
            {
                Error = "An e-mobility provider must be a JSON object.";
                return false;
            }

            var id = json.Value<String>("id")?.Trim();

            if (String.IsNullOrEmpty(id) || id.Length > MaxNameLength)
            {
                Error = $"Every e-mobility provider needs an 'id' of at most {MaxNameLength} characters.";
                return false;
            }

            var name = json.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                name = id;

            if (name.Length > MaxNameLength)
            {
                Error = $"The name of '{id}' may be at most {MaxNameLength} characters long.";
                return false;
            }

            if (!TryParseLogo(json.Value<String>("logo"), id, out var logo, out Error))
                return false;

            #region TokenPrefixes

            var prefixes = new List<String>();

            if (json["tokenPrefixes"] is JArray array)
            {
                foreach (var token in array)
                {

                    var prefix = token.Value<String>()?.Trim().ToUpperInvariant();

                    if (String.IsNullOrEmpty(prefix))
                        continue;

                    if (!prefix.All(Uri.IsHexDigit))
                    {
                        Error = $"The token prefix '{prefix}' of '{id}' is not hexadecimal.";
                        return false;
                    }

                    if (prefix.Length > RFID.RFIDToken.MaximumLength)
                    {
                        Error = $"The token prefix '{prefix}' of '{id}' is longer than a UID.";
                        return false;
                    }

                    if (!prefixes.Contains(prefix, StringComparer.Ordinal))
                        prefixes.Add(prefix);

                }
            }

            if (prefixes.Count > MaxPrefixes)
            {
                Error = $"'{id}' may be recognised by at most {MaxPrefixes} token prefixes.";
                return false;
            }

            #endregion

            Provider = new EMobilityProvider(id, name, logo, prefixes);
            return true;

        }

        #endregion

        #region (static) TryParseLogo(Text, Whose, out Logo, out Error)

        /// <summary>
        /// A logo, which is either somewhere on the web or the picture itself.
        /// </summary>
        /// <remarks>
        /// Only http, https and data: are accepted, and that is the point of
        /// checking at all: this string ends up in the src of an image tag on a
        /// page that is served without a login to a screen in public, so the
        /// set of things it may be should be short and boring.
        /// </remarks>
        public static Boolean TryParseLogo(String?                           Text,
                                           String                            Whose,
                                           out String?                       Logo,
                                           [NotNullWhen(false)] out String?  Error)
        {

            Logo   = null;
            Error  = null;

            var logo = Text?.Trim();

            if (String.IsNullOrEmpty(logo))
                return true;

            if (logo.Length > MaxLogoLength)
            {
                Error = $"The logo of '{Whose}' is longer than the {MaxLogoLength} characters this station holds for one.";
                return false;
            }

            if (!logo.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !logo.StartsWith("http://",  StringComparison.OrdinalIgnoreCase) &&
                !logo.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                Error = $"The logo of '{Whose}' must be an http(s) URL or a data:image/... URI.";
                return false;
            }

            Logo = logo;
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The provider as the file keeps it and the display reads it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",             Id),
                   new JProperty("name",           Name),
                   new JProperty("logo",           Logo),
                   new JProperty("tokenPrefixes",  new JArray(TokenPrefixes))
               );

        #endregion

        #region (override) ToString()

        public override String ToString()
            => Name;

        #endregion

    }

}
