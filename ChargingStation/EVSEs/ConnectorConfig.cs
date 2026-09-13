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

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.EVSEs
{

    /// <summary>
    /// One cable or socket of an EVSE: what shape it has, and the most it may
    /// deliver.
    /// </summary>
    /// <remarks>
    /// Its own limit and not the EVSE's, because an EVSE with two of these is
    /// the ordinary case and they are rarely the same cable: a DC charger with
    /// CCS on one side and CHAdeMO on the other is one EVSE - only one vehicle
    /// charges at a time - with a 300 kW cable and a 50 kW one. Giving both the
    /// EVSE's number would tell a CHAdeMO vehicle it may draw six times what
    /// the cable is rated for.
    ///
    /// The shape and the limit are deliberately two different kinds of fact.
    /// What plug is fitted is a statement about the installation and takes the
    /// hardware permission; what it may deliver is a number arrived at from the
    /// fuse behind it, gets corrected, and takes the power-limit permission.
    /// See <see cref="Web.Permissions"/>.
    /// </remarks>
    /// <param name="Id">Which one it is, counting from 1 within its EVSE.</param>
    /// <param name="Type">What can be plugged into it, in OCPP 2.1's vocabulary.</param>
    /// <param name="MaxPower_kW">The most this cable may deliver.</param>
    public sealed record ConnectorConfig(Byte     Id,
                                         String   Type,
                                         Decimal  MaxPower_kW)
    {

        #region Data

        /// <summary>
        /// The most cables one EVSE may be given here. Not a limit of OCPP - a
        /// limit of what fits on a housing.
        /// </summary>
        public const Int32  MaxConnectors = 8;

        #endregion

        #region Properties

        /// <summary>
        /// The connector type as OCPP 2.1 carries it.
        /// </summary>
        public OCPPv2_1.ConnectorType OCPPType
            => OCPPv2_1.ConnectorType.Parse(Type);

        /// <summary>
        /// Whether OCPP 2.1 names this connector type itself.
        /// </summary>
        public Boolean IsKnownType
            => EVSEConfig.KnownConnectorTypes.Contains(Type, StringComparer.Ordinal);

        #endregion


        #region (static) TryParseType(Text, EVSEId, out Type, out Error)

        /// <summary>
        /// A connector type as somebody wrote it, in the spelling this station
        /// passes on.
        /// </summary>
        /// <remarks>
        /// Anything is a connector type - see
        /// <see cref="EVSEConfig.KnownConnectorTypes"/> - but one that OCPP 2.1
        /// names itself is written the way OCPP 2.1 writes it, so that "stype2"
        /// and "sType2" do not reach a back end as two different sockets.
        /// </remarks>
        public static Boolean TryParseType(String?                           Text,
                                           Object                            EVSEId,
                                           [NotNullWhen(true)]  out String?  Type,
                                           [NotNullWhen(false)] out String?  Error)
        {

            Type   = null;
            Error  = null;

            var text = Text?.Trim();

            if (String.IsNullOrEmpty(text))
            {
                Error = $"EVSE {EVSEId}: a connector needs a type.";
                return false;
            }

            if (text.Length > EVSEConfig.MaxConnectorTypeLength)
            {
                Error = $"EVSE {EVSEId}: a connector type may be at most {EVSEConfig.MaxConnectorTypeLength} characters long.";
                return false;
            }

            if (text.Any(Char.IsControl) || text.Any(Char.IsWhiteSpace))
            {
                Error = $"EVSE {EVSEId}: a connector type is one word without spaces or control characters.";
                return false;
            }

            Type = EVSEConfig.KnownConnectorTypes.FirstOrDefault(candidate => String.Equals(candidate, text, StringComparison.OrdinalIgnoreCase))
                       ?? text;

            return true;

        }

        #endregion

        #region (static) TryParse(JSON, EVSEId, DefaultPower_kW, out Connector, out Error)

        /// <summary>
        /// One connector as the web interface sends it, or as the file keeps it.
        /// </summary>
        /// <param name="JSON">The connector; a bare string counts as one of that type with the EVSE's limit.</param>
        /// <param name="EVSEId">Which EVSE it belongs to, for the error messages.</param>
        /// <param name="DefaultPower_kW">What it may deliver when it does not say - the EVSE's own limit.</param>
        /// <param name="Connector">The connector, with its id still to be assigned.</param>
        /// <param name="Error">What is wrong with it.</param>
        public static Boolean TryParse(JToken                                 JSON,
                                       Object                                 EVSEId,
                                       Decimal                                DefaultPower_kW,
                                       [NotNullWhen(true)]  out ConnectorConfig? Connector,
                                       [NotNullWhen(false)] out String?          Error)
        {

            Connector = null;

            // A bare string, which is how this station used to write the whole
            // list and how a hand-written file is easiest to type. Then the
            // cable is whatever the EVSE is.
            if (JSON.Type == JTokenType.String)
            {

                if (!TryParseType(JSON.Value<String>(), EVSEId, out var bareType, out Error))
                    return false;

                Connector = new ConnectorConfig(0, bareType, DefaultPower_kW);
                return true;

            }

            if (JSON is not JObject json)
            {
                Error = $"EVSE {EVSEId}: a connector is either a type or an object holding one.";
                return false;
            }

            if (!TryParseType(json.Value<String>("type"), EVSEId, out var type, out Error))
                return false;

            var maxPower = json.Value<Decimal?>("maxPower_kW") ?? DefaultPower_kW;

            if (maxPower <= 0 || maxPower > EVSEConfig.MaxPowerLimit_kW)
            {
                Error = $"EVSE {EVSEId}, connector '{type}': 'maxPower_kW' must be more than 0 and at most {EVSEConfig.MaxPowerLimit_kW}.";
                return false;
            }

            Connector = new ConnectorConfig(0, type, maxPower);
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The connector as the web interface reads it and the file keeps it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("id",           Id),
                   new JProperty("type",         Type),
                   new JProperty("maxPower_kW",  MaxPower_kW)
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Type} at {MaxPower_kW} kW";

        #endregion

    }

}
