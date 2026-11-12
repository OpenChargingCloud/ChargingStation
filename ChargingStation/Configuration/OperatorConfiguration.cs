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
    /// Whose charging station this is, and whose customers may be shown a name
    /// on its display.
    /// </summary>
    /// <remarks>
    /// The operator is the fallback throughout: somebody charging ad hoc has no
    /// provider, and the name over that session is the operator's, because that
    /// is who they are buying from.
    /// </remarks>
    /// <param name="Name">The operator of this station, as the display shows it.</param>
    /// <param name="Logo">A URL to the operator's logo, or a data: URI holding it.</param>
    /// <param name="Language">What the display speaks, e.g. "de".</param>
    /// <param name="EMPs">The e-mobility providers whose cards this station recognises.</param>
    public sealed record OperatorConfiguration(String?                           Name       = null,
                                               String?                           Logo       = null,
                                               String?                           Language   = null,
                                               IReadOnlyList<EMobilityProvider>? EMPs       = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName        = "operator";

        /// <summary>
        /// The longest a language tag may be written.
        /// </summary>
        public const Int32   MaxLanguageLength  = 12;

        /// <summary>
        /// What "offlineCards" says where a card this station cannot ask its
        /// back end about is turned away - which is also what it means where
        /// the file says nothing.
        /// </summary>
        public const String  DenyOfflineCards   = "deny";

        /// <summary>
        /// What "offlineCards" says where such a card may start a session.
        /// </summary>
        public const String  AllowOfflineCards  = "allow";

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => Name is null && Logo is null && Language is null && EMPs is null && OfflineCards is null;

        /// <summary>
        /// What becomes of a card while this station cannot ask its back end
        /// about it - "deny" or "allow" - or null where the file does not say,
        /// which is "deny".
        /// </summary>
        /// <remarks>
        /// A station dials a back end because that one decides which cards may
        /// charge. While it cannot be asked - the line is down, or it has not
        /// accepted this station - a station that started a session for every
        /// card would charge for whoever pulled its network cable. So it starts
        /// none, unless its operator says here that being of use while offline
        /// matters more to them: OCPP's OfflineTxForUnknownIdEnabled.
        /// </remarks>
        public String? OfflineCards { get; init; }

        /// <summary>
        /// Whether a card may start a session while this station cannot ask
        /// its back end about it. Not unless the operator said so.
        /// </summary>
        public Boolean AllowsOfflineCards
            => OfflineCards == AllowOfflineCards;


        /// <summary>
        /// The language without its region, e.g. "de" for "de-AT".
        /// </summary>
        /// <remarks>
        /// What a display is written in and what a region says about money and
        /// dates are two different questions, and only the first one is asked
        /// here: a station in Austria shows the same German words as one in
        /// Germany.
        /// </remarks>
        public String? PrimaryLanguage

            => Language?.Split('-')[0].ToLowerInvariant() is { Length: > 0 } primary
                   ? primary
                   : null;

        #endregion


        #region ProviderOf(Token)

        /// <summary>
        /// The provider a card looks like it belongs to, or null when none of
        /// them claims it.
        /// </summary>
        /// <remarks>
        /// The longest matching prefix wins, so a provider that named four
        /// bytes is preferred over one that named one - otherwise the order of
        /// the list would decide, and the order of a list in a file is not
        /// something anybody should have to think about.
        /// </remarks>
        public EMobilityProvider? ProviderOf(RFID.RFIDToken Token)

            => EMPs?.Where    (provider => provider.Matches(Token)).
                     OrderByDescending(provider => provider.TokenPrefixes.
                                                       Where (prefix => Token.UID.StartsWith(prefix, StringComparison.Ordinal)).
                                                       Max   (prefix => prefix.Length)).
                     FirstOrDefault();

        #endregion

        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The operator section of the configuration file.
        /// </summary>
        public static Boolean TryParse(JObject                                         JSON,
                                       [NotNullWhen(true)]  out OperatorConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?                Error)
        {

            Configuration  = null;
            Error          = null;

            var name = JSON.Value<String>("name")?.Trim();

            if (String.IsNullOrEmpty(name))
                name = null;

            else if (name.Length > EMobilityProvider.MaxNameLength)
            {
                Error = $"'{SectionName}.name' may be at most {EMobilityProvider.MaxNameLength} characters long.";
                return false;
            }

            if (!EMobilityProvider.TryParseLogo(JSON.Value<String>("logo"), SectionName, out var logo, out Error))
                return false;

            #region Language

            var language = JSON.Value<String>("language")?.Trim();

            if (String.IsNullOrEmpty(language))
                language = null;

            else
            {

                if (language.Length > MaxLanguageLength)
                {
                    Error = $"'{SectionName}.language' may be at most {MaxLanguageLength} characters long.";
                    return false;
                }

                // A language tag, not a sentence: letters and hyphens, which is
                // what "de", "de-AT" and "pt-BR" are made of.
                if (!language.All(character => Char.IsAsciiLetter(character) || character == '-'))
                {
                    Error = $"'{SectionName}.language' is a language tag such as \"de\" or \"de-AT\".";
                    return false;
                }

            }

            #endregion

            #region EMPs

            List<EMobilityProvider>? emps = null;

            if (JSON["emps"] is JToken empsToken && empsToken.Type != JTokenType.Null)
            {

                if (empsToken is not JArray array)
                {
                    Error = $"'{SectionName}.emps' must be an array of e-mobility providers.";
                    return false;
                }

                emps = [];

                foreach (var token in array)
                {

                    if (!EMobilityProvider.TryParse(token, out var provider, out Error))
                        return false;

                    if (emps.Any(other => String.Equals(other.Id, provider.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        Error = $"There are two e-mobility providers called '{provider.Id}'.";
                        return false;
                    }

                    emps.Add(provider);

                }

                if (emps.Count > EMobilityProvider.MaxProviders)
                {
                    Error = $"This station holds at most {EMobilityProvider.MaxProviders} e-mobility providers.";
                    return false;
                }

            }

            #endregion

            #region Offline cards

            var offlineCards = JSON.Value<String>("offlineCards")?.Trim().ToLowerInvariant();

            if (String.IsNullOrEmpty(offlineCards))
                offlineCards = null;

            else if (offlineCards is not (DenyOfflineCards or AllowOfflineCards))
            {
                Error = $"'{SectionName}.offlineCards' is \"{DenyOfflineCards}\" or \"{AllowOfflineCards}\".";
                return false;
            }

            #endregion

            Configuration = new OperatorConfiguration(name, logo, language, emps) { OfflineCards = offlineCards };
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file - only what it has to say.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Name is not null)
                json.Add("name", Name);

            if (Logo is not null)
                json.Add("logo", Logo);

            if (Language is not null)
                json.Add("language", Language);

            if (EMPs is not null)
                json.Add("emps", new JArray(EMPs.Select(provider => provider.ToJSON())));

            if (OfflineCards is not null)
                json.Add("offlineCards", OfflineCards);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => IsEmpty
                   ? "no operator configured"
                   : $"{Name ?? "unnamed operator"}{(EMPs is null ? "" : $", {EMPs.Count} provider(s)")}";

        #endregion

    }

}
