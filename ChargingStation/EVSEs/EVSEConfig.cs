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
    /// afterwards - OCPP 2.1 as an EVSE, OCPP 1.6 as a connector. Writing it in
    /// either protocol's words would make the other one a translation.
    /// </remarks>
    /// <param name="Id">Which one it is, counting from 1 as OCPP does.</param>
    /// <param name="ConnectorTypes">What can be plugged into it, in OCPP 2.1's vocabulary.</param>
    /// <param name="MaxPower_kW">The most it can deliver.</param>
    /// <param name="Operative">Whether it is meant to be usable at all.</param>
    /// <param name="PhysicalReference">What is written on it, e.g. "A" - the label somebody reads off the housing.</param>
    /// <param name="MeterType">The energy meter built into it, if any.</param>
    /// <param name="MeterSerialNumber">That meter's serial number.</param>
    public sealed record EVSEConfig(Byte                  Id,
                                    IReadOnlyList<String> ConnectorTypes,
                                    Decimal               MaxPower_kW,
                                    Boolean               Operative           = true,
                                    String?               PhysicalReference   = null,
                                    String?               MeterType           = null,
                                    String?               MeterSerialNumber   = null)
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
        /// The connector types as OCPP 2.1 carries them.
        /// </summary>
        public IEnumerable<OCPPv2_1.ConnectorType> OCPPConnectorTypes
            => ConnectorTypes.Select(OCPPv2_1.ConnectorType.Parse);

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

            => ConnectorTypes.Where(connectorType => !KnownConnectorTypes.Contains(connectorType, StringComparer.Ordinal));

        #endregion


        #region (static) Default(Id)

        /// <summary>
        /// What an EVSE looks like when somebody has only said that they want
        /// one: a 22 kW type 2 socket, which is what most of them are.
        /// </summary>
        public static EVSEConfig Default(Byte Id)

            => new (
                   Id,
                   [ OCPPv2_1.ConnectorType.sType2.ToString() ],
                   22,
                   Operative:          true,
                   PhysicalReference:  ((Char) ('A' + Id - 1)).ToString()
               );

        #endregion

        #region (static) TryParse(JSON, out EVSE, out Error)

        /// <summary>
        /// One EVSE as the web interface sends it.
        /// </summary>
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

            #region ConnectorTypes

            var connectorTypes = new List<String>();

            if (json["connectorTypes"] is JArray types)
            {
                foreach (var type in types)
                {

                    var text = type.Value<String>()?.Trim();

                    if (String.IsNullOrEmpty(text))
                        continue;

                    if (text.Length > MaxConnectorTypeLength)
                    {
                        Error = $"EVSE {id}: a connector type may be at most {MaxConnectorTypeLength} characters long.";
                        return false;
                    }

                    if (text.Any(Char.IsControl) || text.Any(Char.IsWhiteSpace))
                    {
                        Error = $"EVSE {id}: a connector type is one word without spaces or control characters.";
                        return false;
                    }

                    // Anything is a connector type - see KnownConnectorTypes -
                    // but one that OCPP 2.1 names itself is written the way
                    // OCPP 2.1 writes it, so that "stype2" and "sType2" do not
                    // reach a back end as two different sockets.
                    text = KnownConnectorTypes.FirstOrDefault(candidate => String.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
                               ?? text;

                    if (!connectorTypes.Contains(text, StringComparer.Ordinal))
                        connectorTypes.Add(text);

                }
            }

            if (connectorTypes.Count == 0)
            {
                Error = $"EVSE {id} needs at least one connector type.";
                return false;
            }

            #endregion

            #region MaxPower_kW

            var maxPower = json.Value<Decimal?>("maxPower_kW");

            if (maxPower is null || maxPower <= 0 || maxPower > MaxPowerLimit_kW)
            {
                Error = $"EVSE {id}: 'maxPower_kW' must be more than 0 and at most {MaxPowerLimit_kW}.";
                return false;
            }

            #endregion

            EVSE = new EVSEConfig(
                       (Byte) id.Value,
                       connectorTypes,
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

        #region ToJSON()

        /// <summary>
        /// The EVSE as the web interface reads it and the file keeps it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",                 Id),
                   new JProperty("connectorTypes",     new JArray(ConnectorTypes)),
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

            => $"EVSE {Id}: {String.Join(", ", ConnectorTypes)}, {MaxPower_kW} kW" +
               (Operative ? "" : ", inoperative");

        #endregion

    }

}
