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
    /// What this charging station may draw from the grid.
    /// </summary>
    /// <remarks>
    /// One number, and a section of its own for it, because it belongs to the
    /// building and not to the station: it is what the grid operator and the
    /// fuse behind the meter allow, and it stays the same while EVSEs are added
    /// and taken away in front of it.
    ///
    /// It is entirely normal for it to be below the sum of what the EVSEs could
    /// deliver - that is what load management is for, and a station with four
    /// 22 kW outlets on a 55 kW connection is the ordinary case rather than a
    /// misconfiguration. So the two are not checked against each other; the
    /// station only says in its log what it is looking at.
    /// </remarks>
    /// <param name="UplinkPowerLimit_kW">The most this station may draw in total, or null when nobody has said.</param>
    public sealed record PowerConfiguration(Decimal? UplinkPowerLimit_kW = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String   SectionName                = "power";

        /// <summary>
        /// The largest grid connection this station will believe in, in kW.
        /// </summary>
        /// <remarks>
        /// A plausibility guard and not a standard. Depot charging with several
        /// megawatt points reaches into the tens of megawatts, so this is
        /// generous; it exists so that a field somebody typed a unit into does
        /// not become a 50 GW grid connection.
        /// </remarks>
        public const Decimal  MaxUplinkPowerLimit_kW     = 50_000;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => UplinkPowerLimit_kW is null;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The power section of the configuration file.
        /// </summary>
        /// <remarks>
        /// An explicit <c>null</c> is how a limit is taken away again, and is
        /// therefore not the same as the field being absent: absent means the
        /// file has no opinion and whatever the station was handed stands.
        /// Telling the two apart is the caller's job - see
        /// <see cref="ChargingStation.TryUpdatePowerConfiguration"/>.
        /// </remarks>
        public static Boolean TryParse(JObject                                      JSON,
                                       [NotNullWhen(true)]  out PowerConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?             Error)
        {

            Configuration  = null;
            Error          = null;

            if (!TryParsePowerLimit(JSON,
                                    "uplinkPowerLimit_kW",
                                    $"{SectionName}.uplinkPowerLimit_kW",
                                    out var uplink,
                                    out Error))
            {
                return false;
            }

            Configuration = new PowerConfiguration(uplink);
            return true;

        }

        #endregion

        #region (static) TryParsePowerLimit(JSON, Name, Path, out Limit, out Error)

        /// <summary>
        /// One power limit in kW, where absent and JSON null both come back as
        /// null and anything that is not a plausible number is an error naming
        /// the field.
        /// </summary>
        public static Boolean TryParsePowerLimit(JObject                           JSON,
                                                 String                            Name,
                                                 String?                           Path,
                                                 out Decimal?                      Limit,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            Limit  = null;
            Error  = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path ?? Name;

            if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                Error = $"'{where}' must be a number of kW.";
                return false;
            }

            var value = token.Value<Decimal>();

            if (value <= 0 || value > MaxUplinkPowerLimit_kW)
            {
                Error = $"'{where}' must be more than 0 and at most {MaxUplinkPowerLimit_kW} kW.";
                return false;
            }

            Limit = value;
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

            if (UplinkPowerLimit_kW.HasValue)
                json.Add("uplinkPowerLimit_kW", UplinkPowerLimit_kW.Value);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => UplinkPowerLimit_kW.HasValue
                   ? $"up to {UplinkPowerLimit_kW.Value} kW from the grid"
                   : "no grid connection limit configured";

        #endregion

    }

}
