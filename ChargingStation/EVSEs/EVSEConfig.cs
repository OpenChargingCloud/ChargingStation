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
using System.Reflection;

using Newtonsoft.Json.Linq;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.EVSEs
{

    /// <summary>
    /// One place a vehicle can be plugged in, as this charging station is
    /// configured to have it.
    /// </summary>
    /// <remarks>
    /// Deliberately this project's own record rather than an OCPP one: an EVSE
    /// is a thing the station has, and both protocol stacks are told about it
    /// afterwards - OCPP 2.1 as an EVSE, OCPP 1.6 as connectors. Writing it in
    /// either protocol's words would make the other one a translation.
    ///
    /// An EVSE serves one vehicle at a time through one of its connectors, so
    /// its own limit is what the power stage behind them all can deliver and a
    /// connector's is what that cable can carry. Neither implies the other, and
    /// a cable may not be configured above the EVSE feeding it.
    /// </remarks>
    /// <param name="Id">Which one it is, counting from 1 as OCPP does.</param>
    /// <param name="Connectors">What can be plugged into it, and what each of those may deliver.</param>
    /// <param name="MaxPower_kW">The most this EVSE can deliver, through whichever connector is in use.</param>
    /// <param name="Operative">Whether it is meant to be usable at all.</param>
    /// <param name="PhysicalReference">What is written on it, e.g. "A" - the label somebody reads off the housing.</param>
    /// <param name="MeterType">The energy meter built into it, if any.</param>
    /// <param name="MeterSerialNumber">That meter's serial number.</param>
    public sealed record EVSEConfig(Byte                            Id,
                                    IReadOnlyList<ConnectorConfig>  Connectors,
                                    Decimal                         MaxPower_kW,
                                    Boolean                         Operative           = true,
                                    String?                         PhysicalReference   = null,
                                    String?                         MeterType           = null,
                                    String?                         MeterSerialNumber   = null)
    {

        #region Data

        /// <summary>
        /// The most EVSEs one station may be given here. Not a limit of OCPP -
        /// a limit of what is plausible, so that a typo in a request does not
        /// build ten thousand of them.
        /// </summary>
        public const Int32    MaxEVSEs         = 64;

        /// <summary>
        /// The most power an EVSE may be configured for, in kW. Megawatt
        /// charging is a real thing, so this is generous rather than tight.
        /// </summary>
        public const Decimal  MaxPowerLimit_kW = 4_000;

        /// <summary>
        /// The longest a connector type may be written.
        /// </summary>
        /// <remarks>
        /// Not a rule of OCPP but a guard against a paste accident becoming a
        /// connector type: a name nobody would type is almost certainly not one
        /// somebody meant.
        /// </remarks>
        public const Int32    MaxConnectorTypeLength = 50;

        /// <summary>
        /// The connector types OCPP 2.1 names itself.
        /// </summary>
        /// <remarks>
        /// A vocabulary to offer, not one to enforce.
        /// <see cref="OCPPv2_1.ConnectorType"/> is a set of predefined strings
        /// and not an enumeration, and that is the point of it: a connector
        /// this station has never heard of is still a connector somebody can
        /// plug a car into, and a station that refused to describe it would be
        /// useless at exactly the moment a new plug arrives. So anything is
        /// accepted, and what matches one of these is written the way the
        /// protocol writes it - "stype2" and "sType2" are the same socket, and
        /// sending both to a back end would make them look like two.
        ///
        /// By reflection rather than a copied list, so that the web interface
        /// offers the same plugs the protocol stack underneath it knows,
        /// instead of the two drifting apart at the first new standard.
        /// </remarks>
        public static readonly IReadOnlyList<String> KnownConnectorTypes =
            [.. typeof(OCPPv2_1.ConnectorType).
                    GetProperties(BindingFlags.Public | BindingFlags.Static).
                    Where   (property => property.PropertyType == typeof(OCPPv2_1.ConnectorType)).
                    Select  (property => property.GetValue(null)?.ToString()).
                    Where   (name     => !String.IsNullOrEmpty(name)).
                    Cast<String>().
                    OrderBy (name     => name, StringComparer.OrdinalIgnoreCase)];

        #endregion

        #region Properties

        /// <summary>
        /// What can be plugged into this EVSE, in OCPP 2.1's vocabulary.
        /// </summary>
        public IEnumerable<String> ConnectorTypes
            => Connectors.Select(connector => connector.Type);

        /// <summary>
        /// The connector types as OCPP 2.1 carries them.
        /// </summary>
        public IEnumerable<OCPPv2_1.ConnectorType> OCPPConnectorTypes
            => Connectors.Select(connector => connector.OCPPType);

        /// <summary>
        /// The connector types of this EVSE that OCPP 2.1 does not name itself.
        /// </summary>
        /// <remarks>
        /// Perfectly allowed - see <see cref="KnownConnectorTypes"/> - but
        /// worth one line in the log, because a plug nobody has heard of and a
        /// plug somebody mistyped look exactly alike from here, and only the
        /// person who typed it can tell them apart.
        /// </remarks>
        public IEnumerable<String> CustomConnectorTypes

            => Connectors.Where (connector => !connector.IsKnownType).
                          Select(connector =>  connector.Type);

        #endregion


        #region (static) Default(Id)

        /// <summary>
        /// What an EVSE looks like when somebody has only said that they want
        /// one: a 22 kW type 2 socket, which is what most of them are.
        /// </summary>
        public static EVSEConfig Default(Byte Id)

            => new (
                   Id,
                   [ new ConnectorConfig(1, OCPPv2_1.ConnectorType.sType2.ToString(), 22) ],
                   22,
                   Operative:          true,
                   PhysicalReference:  ((Char) ('A' + Id - 1)).ToString()
               );

        #endregion

        #region (static) TryParse(JSON, out EVSE, out Error)

        /// <summary>
        /// One EVSE as the web interface sends it, or as the file keeps it.
        /// </summary>
        /// <remarks>
        /// Two spellings of the connectors are accepted. The one this station
        /// writes is <c>"connectors": [{ "type": "sType2", "maxPower_kW": 22 }]</c>;
        /// the one it used to write, and the one a file is quickest to type by
        /// hand, is <c>"connectorTypes": ["sType2"]</c>, where every cable is
        /// whatever the EVSE is. A file written before this station could tell
        /// the two apart therefore still means what it meant.
        /// </remarks>
        public static Boolean TryParse(JToken                            JSON,
                                       [NotNullWhen(true)]  out EVSEConfig? EVSE,
                                       [NotNullWhen(false)] out String?     Error)
        {

            EVSE   = null;
            Error  = null;

            if (JSON is not JObject json)
            {
                Error = "An EVSE must be a JSON object.";
                return false;
            }

            #region Id

            var id = json.Value<Int64?>("id");

            if (id is null || id < 1 || id > MaxEVSEs)
            {
                Error = $"Every EVSE needs an 'id' between 1 and {MaxEVSEs}.";
                return false;
            }

            #endregion

            #region MaxPower_kW

            // Read before the connectors, because a connector that does not say
            // what it may deliver is whatever its EVSE may deliver.
            var maxPower = json.Value<Decimal?>("maxPower_kW");

            if (maxPower is null || maxPower <= 0 || maxPower > MaxPowerLimit_kW)
            {
                Error = $"EVSE {id}: 'maxPower_kW' must be more than 0 and at most {MaxPowerLimit_kW}.";
                return false;
            }

            #endregion

            #region Connectors

            var source = json["connectors"] as JArray
                             ?? json["connectorTypes"] as JArray;

            if (source is null)
            {
                Error = $"EVSE {id} needs a 'connectors' array.";
                return false;
            }

            var connectors = new List<ConnectorConfig>();

            foreach (var token in source)
            {

                if (token.Type == JTokenType.Null)
                    continue;

                if (!ConnectorConfig.TryParse(token, id, maxPower.Value, out var connector, out Error))
                    return false;

                if (connectors.Any(other => String.Equals(other.Type, connector.Type, StringComparison.Ordinal)))
                {
                    Error = $"EVSE {id} has the connector type '{connector.Type}' twice; one EVSE has each shape of plug at most once.";
                    return false;
                }

                if (connector.MaxPower_kW > maxPower.Value)
                {
                    Error = $"EVSE {id}, connector '{connector.Type}': a cable may not be configured for more " +
                            $"({connector.MaxPower_kW} kW) than the EVSE feeding it ({maxPower.Value} kW).";
                    return false;
                }

                // Numbered by where it stands in the list rather than by what
                // the request said: the order is the truth, and an id somebody
                // typed can only disagree with it.
                connectors.Add(connector with { Id = (Byte) (connectors.Count + 1) });

            }

            if (connectors.Count == 0)
            {
                Error = $"EVSE {id} needs at least one connector.";
                return false;
            }

            if (connectors.Count > ConnectorConfig.MaxConnectors)
            {
                Error = $"EVSE {id} may have at most {ConnectorConfig.MaxConnectors} connectors.";
                return false;
            }

            #endregion

            EVSE = new EVSEConfig(
                       (Byte) id.Value,
                       connectors,
                       maxPower.Value,
                       json.Value<Boolean?>("operative") ?? true,
                       Trimmed(json, "physicalReference"),
                       Trimmed(json, "meterType"),
                       Trimmed(json, "meterSerialNumber")
                   );

            return true;

        }

        #endregion

        #region (static) TryParseList(JSON, out EVSEs, out Error)

        /// <summary>
        /// A whole list of EVSEs, with its numbering checked: OCPP counts them
        /// from 1 upwards without gaps, and a station that told a CSMS about an
        /// EVSE 4 it has no 3 for would be describing hardware nobody can find.
        /// </summary>
        public static Boolean TryParseList(JArray                                             JSON,
                                           [NotNullWhen(true)]  out IReadOnlyList<EVSEConfig>? EVSEs,
                                           [NotNullWhen(false)] out String?                    Error)
        {

            EVSEs  = null;
            Error  = null;

            var parsed = new List<EVSEConfig>();

            foreach (var token in JSON)
            {

                if (!TryParse(token, out var evse, out Error))
                    return false;

                parsed.Add(evse);

            }

            if (parsed.Count == 0)
            {
                Error = "A charging station needs at least one EVSE.";
                return false;
            }

            if (parsed.Count > MaxEVSEs)
            {
                Error = $"A charging station may have at most {MaxEVSEs} EVSEs here.";
                return false;
            }

            var expected = 1;

            foreach (var evse in parsed.OrderBy(evse => evse.Id))
            {

                if (evse.Id != expected)
                {
                    Error = $"The EVSEs must be numbered 1 to {parsed.Count} without gaps or repeats; {expected} is missing.";
                    return false;
                }

                expected++;

            }

            EVSEs = [.. parsed.OrderBy(evse => evse.Id)];
            return true;

        }

        #endregion


        #region SameAvailabilityAs(Other) / SamePowerLimitsAs(Other) / SameHardwareAs(Other)

        /// <summary>
        /// Whether this EVSE and the other one are both meant to be usable, or
        /// both not.
        /// </summary>
        public Boolean SameAvailabilityAs(EVSEConfig Other)

            => Operative == Other.Operative;

        /// <summary>
        /// Whether this EVSE and the other one may deliver the same, down to
        /// every cable.
        /// </summary>
        public Boolean SamePowerLimitsAs(EVSEConfig Other)

            => MaxPower_kW == Other.MaxPower_kW &&
               Connectors.Count == Other.Connectors.Count &&
               Connectors.Zip(Other.Connectors).All(pair => pair.First.MaxPower_kW == pair.Second.MaxPower_kW);

        /// <summary>
        /// Whether this EVSE and the other one describe the same equipment -
        /// everything except what it may deliver and whether it is in service.
        /// </summary>
        /// <remarks>
        /// The whole point of the three: a list that differs only in its
        /// switches is an EVSE taken out of service, a list that differs only
        /// in its numbers is a correction, and a list that differs in anything
        /// else is a claim about what is bolted to the wall. Only the last one
        /// needs the hardware permission. See <see cref="Web.Permissions"/>.
        /// </remarks>
        public Boolean SameHardwareAs(EVSEConfig Other)

            => Id                 == Other.Id                &&
               PhysicalReference  == Other.PhysicalReference &&
               MeterType          == Other.MeterType         &&
               MeterSerialNumber  == Other.MeterSerialNumber &&
               Connectors.Count   == Other.Connectors.Count  &&
               Connectors.Zip(Other.Connectors).All(pair => String.Equals(pair.First.Type, pair.Second.Type, StringComparison.Ordinal));

        #endregion

        #region (static) SameHardware(A, B) / SameAvailability(A, B) / SamePowerLimits(A, B)

        /// <summary>
        /// Whether two lists of EVSEs describe the same equipment, whatever
        /// they say it may deliver or whether it is in service.
        /// </summary>
        public static Boolean SameHardware(IReadOnlyList<EVSEConfig> A,
                                           IReadOnlyList<EVSEConfig> B)

            => A.Count == B.Count &&
               A.Zip(B).All(pair => pair.First.SameHardwareAs(pair.Second));

        /// <summary>
        /// Whether two lists of EVSEs are in service the same way.
        /// </summary>
        public static Boolean SameAvailability(IReadOnlyList<EVSEConfig> A,
                                               IReadOnlyList<EVSEConfig> B)

            => A.Count == B.Count &&
               A.Zip(B).All(pair => pair.First.SameAvailabilityAs(pair.Second));

        /// <summary>
        /// Whether two lists of EVSEs may deliver the same.
        /// </summary>
        public static Boolean SamePowerLimits(IReadOnlyList<EVSEConfig> A,
                                              IReadOnlyList<EVSEConfig> B)

            => A.Count == B.Count &&
               A.Zip(B).All(pair => pair.First.SamePowerLimitsAs(pair.Second));

        #endregion


        #region ToJSON()

        /// <summary>
        /// The EVSE as the web interface reads it and the file keeps it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",                 Id),
                   new JProperty("connectors",         new JArray(Connectors.Select(connector => connector.ToJSON()))),
                   new JProperty("maxPower_kW",        MaxPower_kW),
                   new JProperty("operative",          Operative),
                   new JProperty("physicalReference",  PhysicalReference),
                   new JProperty("meterType",          MeterType),
                   new JProperty("meterSerialNumber",  MeterSerialNumber)
               );

        #endregion

        #region (private static) Trimmed(JSON, Name)

        /// <summary>
        /// An optional string, with a blank one counting as absent - a field
        /// somebody cleared in a form should not become an empty serial number.
        /// </summary>
        private static String? Trimmed(JObject JSON, String Name)
        {

            var text = JSON.Value<String>(Name)?.Trim();

            return String.IsNullOrEmpty(text) ? null : text;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"EVSE {Id}: {String.Join(", ", Connectors)}, up to {MaxPower_kW} kW" +
               (Operative ? "" : ", inoperative");

        #endregion

    }

}
