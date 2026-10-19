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

using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// How the meter of one EVSE is read: where, whom it is believed to be, and
    /// whom this station is to it for each kind of request.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Whom it is believed to be is said as a time server's or a name server's
    /// is: the certificates it may show and the roots its chain may end at,
    /// what showing another comes to, and what is learned the first time it is
    /// believed - see <see cref="ServerPins"/>. Beside what every meter is held
    /// to, a certificate issued for its host that chains to a root of the
    /// machine or a TLS root of the store for "meter".
    /// </para>
    /// <para>
    /// Whom this station is to it is a TLS identity of the store for reading
    /// and one for writing, each by its id: a SunSpec meter decides what a peer
    /// may do by the role in its certificate, and the role that lets a station
    /// read is not the one that lets it change a register. Without one for
    /// reading, the identity of the store with the least role that may read is
    /// shown; without one for writing, nothing is written.
    /// </para>
    /// </remarks>
    /// <param name="EVSE">The EVSE whose meter it is.</param>
    /// <param name="Endpoint">Where it is read.</param>
    /// <param name="Pins">What its certificate is held to, beyond what every meter's is; null for nothing.</param>
    /// <param name="ReadIdentity">The id of the TLS identity shown to it to read; null for the least that may.</param>
    /// <param name="WriteIdentity">The id of the TLS identity shown to it to write; null for writing nothing.</param>
    public sealed record MeterConfiguration(Byte           EVSE,
                                            MeterEndpoint  Endpoint,
                                            ServerPins?    Pins           = null,
                                            String?        ReadIdentity   = null,
                                            String?        WriteIdentity  = null)
    {

        #region Data

        /// <summary>
        /// The longest the id of an identity may be written.
        /// </summary>
        public const Int32  MaxIdentityLength  = 64;

        #endregion


        #region (static) TryParse(JSON, Path, out Meter, out Error)

        /// <summary>
        /// One entry of the "meters" section, or of a page's change to one -
        /// <c>{ "evse": 1, "host": "10.0.0.5", "port": 802, "unit": 1,
        /// "identities": { "read": "…", "write": "…" }, "rootFingerprint": "…",
        /// "trustOnFirstUse": "certificate" }</c>.
        /// </summary>
        /// <param name="JSON">The entry.</param>
        /// <param name="Path">Where it is, for the sentence of a refusal: "meters[0]".</param>
        /// <param name="Meter">How the meter is read; null where the entry has no host, which is no meter to read.</param>
        /// <param name="Error">Why it was refused.</param>
        public static Boolean TryParse(JObject                                JSON,
                                       String                                 Path,
                                       out MeterConfiguration?                Meter,
                                       [NotNullWhen(false)] out String?       Error)
        {

            Meter = null;

            if (!TryReadEVSE(JSON, Path, out var evse, out Error))
                return false;

            if (!MeterEndpoint.TryParse(JSON, Path, out var endpoint, out Error))
                return false;

            if (!ServerPins.TryParse(JSON, Path, out var pins, out Error))
                return false;

            String? read  = null;
            String? write = null;

            if (JSON["identities"] is JToken identitiesToken && identitiesToken.Type != JTokenType.Null)
            {

                if (identitiesToken is not JObject identities)
                {
                    Error = $"'{Path}.identities' must be an object: the id of the TLS identity shown to read, and of the one shown to write.";
                    return false;
                }

                if (!TryReadIdentity(identities, "read",  Path, out read,  out Error) ||
                    !TryReadIdentity(identities, "write", Path, out write, out Error))
                    return false;

            }

            if (endpoint is not null)
                Meter = new MeterConfiguration(evse, endpoint, pins, read, write);

            return true;

        }

        #endregion

        #region (static) TryReadEVSE(JSON, Path, out EVSE, out Error)

        /// <summary>
        /// The EVSE an entry is about.
        /// </summary>
        public static Boolean TryReadEVSE(JObject                           JSON,
                                          String                            Path,
                                          out Byte                          EVSE,
                                          [NotNullWhen(false)] out String?  Error)
        {

            EVSE  = 0;
            Error = null;

            if (JSON["evse"] is not JToken token || token.Type != JTokenType.Integer ||
                token.Value<Int64>() is < 1 or > EVSEConfig.MaxEVSEs)
            {
                Error = $"'{Path}.evse' must say whose meter this is: an EVSE between 1 and {EVSEConfig.MaxEVSEs}.";
                return false;
            }

            EVSE = (Byte) token.Value<Int64>();
            return true;

        }

        #endregion

        #region (private static) TryReadIdentity(Identities, Name, Path, out Id, out Error)

        private static Boolean TryReadIdentity(JObject                           Identities,
                                               String                            Name,
                                               String                            Path,
                                               out String?                       Id,
                                               [NotNullWhen(false)] out String?  Error)
        {

            Id    = null;
            Error = null;

            if (Identities[Name] is not JToken token || token.Type == JTokenType.Null)
                return true;

            var text = token.Type == JTokenType.String ? token.Value<String>()!.Trim() : null;

            if (text is null || text.Length > MaxIdentityLength || text.Any(Char.IsWhiteSpace))
            {
                Error = $"'{Path}.identities.{Name}' must be the id of a TLS identity of the certificate store.";
                return false;
            }

            Id = text.Length == 0 ? null : text.ToLowerInvariant();
            return true;

        }

        #endregion


        #region SameReadingAs(Other)

        /// <summary>
        /// Whether a meter read this way is read as the other way reads it -
        /// where, held to what, and shown which identity to read. A change to
        /// the identity it is written with alone leaves it being read as it is.
        /// </summary>
        public Boolean SameReadingAs(MeterConfiguration? Other)

            => Other is not null                                                          &&
               Endpoint == Other.Endpoint                                                 &&
               (Pins ?? ServerPins.None).SameAs(Other.Pins)                               &&
               String.Equals(ReadIdentity, Other.ReadIdentity, StringComparison.OrdinalIgnoreCase);

        #endregion

        #region ToJSON()

        /// <summary>
        /// The entry as the file keeps it.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject(new JProperty("evse", EVSE));

            Endpoint.WriteInto(json);

            if (ReadIdentity is not null || WriteIdentity is not null)
            {

                var identities = new JObject();

                if (ReadIdentity  is not null) identities["read"]  = ReadIdentity;
                if (WriteIdentity is not null) identities["write"] = WriteIdentity;

                json["identities"] = identities;

            }

            Pins?.WriteInto(json);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Endpoint}" +
               (ReadIdentity  is not null ? $", read as {ReadIdentity}"     : "") +
               (WriteIdentity is not null ? $", written as {WriteIdentity}" : ", never written") +
               (Pins?.Text    is String held ? $", held to {held}"          : "");

        #endregion

    }


    /// <summary>
    /// The "meters" section: how the meter of each EVSE that has one read is
    /// read, an entry for each - see <see cref="MeterConfiguration"/>.
    /// </summary>
    /// <param name="Meters">Each EVSE's, by its id.</param>
    public sealed record MetersConfiguration(IReadOnlyDictionary<Byte, MeterConfiguration> Meters)
    {

        #region Data

        /// <summary>
        /// The section of the configuration file.
        /// </summary>
        public const String  SectionName  = "meters";

        /// <summary>
        /// No meter read anywhere.
        /// </summary>
        public static readonly MetersConfiguration  Empty = new (new Dictionary<Byte, MeterConfiguration>());

        #endregion


        #region (static) TryParse(JSON, out Meters, out Error)

        /// <summary>
        /// The section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JToken                                          JSON,
                                       [NotNullWhen(true)]  out MetersConfiguration?   Meters,
                                       [NotNullWhen(false)] out String?                Error)
        {

            Meters = null;
            Error  = null;

            if (JSON is not JArray entries)
            {
                Error = $"'{SectionName}' must be a list: an entry for the meter of each EVSE that has one read.";
                return false;
            }

            var meters = new Dictionary<Byte, MeterConfiguration>();

            for (var index = 0; index < entries.Count; index++)
            {

                var path = $"{SectionName}[{index}]";

                if (entries[index] is not JObject entry)
                {
                    Error = $"'{path}' must be an object.";
                    return false;
                }

                if (!MeterConfiguration.TryParse(entry, path, out var meter, out Error))
                    return false;

                if (meter is null)
                {
                    Error = $"'{path}' says no 'host', so there is no meter to read; leave the entry out.";
                    return false;
                }

                if (meters.ContainsKey(meter.EVSE))
                {
                    Error = $"'{path}' is about EVSE {meter.EVSE}, which an entry before it is about already; one EVSE has one meter.";
                    return false;
                }

                meters[meter.EVSE] = meter;

            }

            Meters = new MetersConfiguration(meters);
            return true;

        }

        #endregion

        #region MeterOf(EVSE) / With(EVSE, Meter)

        /// <summary>
        /// How the meter of the given EVSE is read, where it is.
        /// </summary>
        public MeterConfiguration? MeterOf(Byte EVSE)

            => Meters.TryGetValue(EVSE, out var meter)
                   ? meter
                   : null;

        /// <summary>
        /// The same, with the meter of one EVSE read as given - or not at all.
        /// </summary>
        public MetersConfiguration With(Byte                 EVSE,
                                        MeterConfiguration?  Meter)
        {

            var meters = new Dictionary<Byte, MeterConfiguration>(Meters);

            if (Meter is null)
                meters.Remove(EVSE);
            else
                meters[EVSE] = Meter;

            return new MetersConfiguration(meters);

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as the file keeps it, by EVSE.
        /// </summary>
        public JArray ToJSON()

            => new (Meters.Values.OrderBy(meter => meter.EVSE).Select(meter => meter.ToJSON()));

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Meters.Count} meter(s) read";

        #endregion

    }

}
