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

using cloud.charging.open.ChargingStation.EVSEs;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// The "v2g" section of the configuration file: what each EVSE of this
    /// charging station offers a vehicle on the wire below its cable, one link
    /// per EVSE.
    /// </summary>
    /// <remarks>
    /// One link per EVSE, because that is what is below a cable: a powerline
    /// modem or a 10BASE-T1S bus of its own, a V2G endpoint a vehicle is sent to
    /// over SDP, and an EVSE identification of its own. Two cables behind one
    /// endpoint would be two cars in one socket.
    ///
    /// Written as
    /// <code>"v2g": { "links": [ { "evse": 1, "enabled": true, ... }, ... ] }</code>
    /// and read that way. A section written before there was more than one link
    /// - its settings directly in "v2g" - is read as the link of EVSE 1, which
    /// is what it always was; it is written as a list the next time the page
    /// saves it.
    /// </remarks>
    /// <param name="Links">The settings of each link, by the EVSE it is below.</param>
    public sealed record V2GLinksConfiguration(IReadOnlyDictionary<Byte, V2GConfiguration> Links)
    {

        #region Data

        /// <summary>
        /// The name of the list in the section.
        /// </summary>
        public const String LinksName = "links";

        /// <summary>
        /// The name of the field that says which EVSE a link is below.
        /// </summary>
        public const String EVSEName  = "evse";

        /// <summary>
        /// Nothing said about any link.
        /// </summary>
        public static V2GLinksConfiguration Empty { get; } = new (new Dictionary<Byte, V2GConfiguration>());

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "v2g" section, as a list or as it was written before, or the one
        /// sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                          JSON,
                                       [NotNullWhen(true)]  out V2GLinksConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?                 Error)
        {

            Configuration  = null;
            Error          = null;

            var section    = V2GConfiguration.SectionName;
            var links      = JSON[LinksName];

            #region Written before there was a list: the link of EVSE 1

            if (links is null || links.Type == JTokenType.Null)
            {

                if (!V2GConfiguration.TryParse(JSON, out var flat, out Error))
                    return false;

                Configuration = flat == new V2GConfiguration()
                                    ? Empty
                                    : new V2GLinksConfiguration(new Dictionary<Byte, V2GConfiguration> { [1] = flat });

                return true;

            }

            #endregion

            // Both at once would be two answers to the question which settings
            // EVSE 1 has.
            if (JSON.Properties().Any(property => property.Name != LinksName))
            {
                Error = $"'{section}' is either a list of links in '{section}.{LinksName}' or the settings of one, not both: " +
                        $"'{section}.{JSON.Properties().First(property => property.Name != LinksName).Name}' is beside the list.";
                return false;
            }

            if (links is not JArray list)
            {
                Error = $"'{section}.{LinksName}' must be a list of links, one for each EVSE that has one.";
                return false;
            }

            var parsed = new Dictionary<Byte, V2GConfiguration>();

            for (var index = 0; index < list.Count; index++)
            {

                var path = $"{section}.{LinksName}[{index}]";

                if (list[index] is not JObject item)
                {
                    Error = $"'{path}' must be an object with the EVSE it is below in '{EVSEName}'.";
                    return false;
                }

                if (!TryReadEVSE(item, path, out var evse, out Error))
                    return false;

                if (parsed.ContainsKey(evse))
                {
                    Error = $"'{path}' is a second link below EVSE {evse}; an EVSE has one.";
                    return false;
                }

                var settings = (JObject) item.DeepClone();
                settings.Remove(EVSEName);

                if (!V2GConfiguration.TryParse(settings, out var link, out Error, path))
                    return false;

                parsed[evse] = link;

            }

            Configuration = new V2GLinksConfiguration(parsed);
            return true;

        }

        #endregion

        #region (static) TryReadEVSE(JSON, Path, out EVSE, out Error)

        /// <summary>
        /// Which EVSE a link is below, by its id - from 1, as OCPP counts.
        /// </summary>
        public static Boolean TryReadEVSE(JObject                           JSON,
                                          String                            Path,
                                          out Byte                          EVSE,
                                          [NotNullWhen(false)] out String?  Error)
        {

            EVSE   = 0;
            Error  = null;

            var token = JSON[EVSEName];

            if (token is null || token.Type != JTokenType.Integer ||
                token.Value<Int64>() < 1 || token.Value<Int64>() > EVSEConfig.MaxEVSEs)
            {
                Error = $"'{Path}.{EVSEName}' must say which EVSE it is below: its id, from 1 to {EVSEConfig.MaxEVSEs}.";
                return false;
            }

            EVSE = (Byte) token.Value<Int64>();
            return true;

        }

        #endregion

        #region With(EVSE, Change)

        /// <summary>
        /// These links, with what a change says about the one below the given
        /// EVSE laid over what it said before.
        /// </summary>
        public V2GLinksConfiguration With(Byte              EVSE,
                                          V2GConfiguration  Change)
        {

            var links = new Dictionary<Byte, V2GConfiguration>(Links);

            links[EVSE] = links.TryGetValue(EVSE, out var before)
                              ? before.MergedWith(Change)
                              : Change;

            return new V2GLinksConfiguration(links);

        }

        #endregion

        #region LinkOf(EVSE)

        /// <summary>
        /// What the file says about the link below the given EVSE; nothing,
        /// where it says nothing.
        /// </summary>
        public V2GConfiguration LinkOf(Byte EVSE)

            => Links.TryGetValue(EVSE, out var link)
                   ? link
                   : new V2GConfiguration();

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file: always a list, by EVSE.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty(LinksName, new JArray(
                       Links.OrderBy (link => link.Key).
                             Select  (link => {
                                 var json = link.Value.ToJSON();
                                 json.AddFirst(new JProperty(EVSEName, link.Key));
                                 return json;
                             })
                   ))
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Links.Count == 0
                   ? "no V2G links"
                   : String.Join("; ", Links.OrderBy(link => link.Key).
                                             Select (link => $"EVSE {link.Key}: {link.Value}"));

        #endregion

    }

}
