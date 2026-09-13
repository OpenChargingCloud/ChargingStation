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
        /// Every connector type OCPP 2.1 defines.
        /// </summary>
        /// <remarks>
        /// By reflection over the static properties of
        /// <see cref="OCPPv2_1.ConnectorType"/>, because it is a set of
        /// predefined strings rather than an enumeration: its own TryParse
        /// accepts anything non-empty, so it says whether a string is a
        /// connector type in the sense of "is a string", not in the sense of
        /// "is one of these". An EVSE offering "tpye2" would be an EVSE no
        /// vehicle ever matches, and nothing further down would have complained.
        ///
        /// Copying the list into this file instead would mean a station whose
        /// web interface knows a different set of plugs than the protocol stack
        /// underneath it, and the two would drift apart at the first new
        /// standard.
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
        /// The connector types as OCPP 2.1 knows them.
        /// </summary>
        public IEnumerable<OCPPv2_1.ConnectorType> OCPPConnectorTypes
            => ConnectorTypes.Select(OCPPv2_1.ConnectorType.Parse);

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

                    // Checked against the vocabulary and not merely parsed:
                    // see KnownConnectorTypes for why parsing proves nothing.
                    var known = KnownConnectorTypes.FirstOrDefault(candidate => String.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));

                    if (known is null)
                    {
                        Error = $"EVSE {id}: '{text}' is not a connector type OCPP 2.1 defines.";
                        return false;
                    }

                    // The spelling the protocol uses, not the one that was
                    // typed: "stype2" and "sType2" are the same plug.
                    text = known;

                    if (!connectorTypes.Contains(text))
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
