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

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The screen on the front of the station: the hours it keeps, whether its
    /// picture walks, and the port it is served on.
    /// </summary>
    /// <remarks>
    /// A display in a car park runs at full brightness through the night at
    /// nobody, which costs electricity, throws light where a neighbour may not
    /// want it, and wears the panel out.
    ///
    /// Which hours are quiet is a fact about the site and not about charging
    /// stations: a motorway service area has none, a courtyard between flats
    /// has them from ten. So the station is told rather than guessing, and a
    /// station nobody has told does not dim - a screen that went dark on its
    /// own would be read as a fault.
    ///
    /// It says only whether it is a quiet hour, never how dark the screen
    /// should be at this moment: the display wakes for anybody who comes near
    /// it, and only the display knows that somebody has.
    ///
    /// Whether the picture walks against burn-in is a fact about the panel:
    /// one that shows the same thing for months keeps it as a ghost, one that
    /// does not is only made to twitch. Off unless it is switched on.
    /// </remarks>
    /// <param name="DimFrom">When the quiet hours begin, in the station's own local time.</param>
    /// <param name="DimUntil">When they end. Earlier than DimFrom is the ordinary case: they cross midnight.</param>
    /// <param name="DimTo">How bright the screen is while nothing is happening, as a fraction of full.</param>
    /// <param name="KeepMoving">Whether the picture walks a small ring against burn-in; off where not said.</param>
    /// <param name="Port">The TCP port the display is served on, where the command line gives none; ChargingStation.DefaultKioskPort where not said.</param>
    public sealed record DisplayConfiguration(TimeOnly?  DimFrom     = null,
                                              TimeOnly?  DimUntil    = null,
                                              Double?    DimTo       = null,
                                              Boolean?   KeepMoving  = null,
                                              IPPort?    Port        = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName    = "display";

        /// <summary>
        /// The darkest a screen will be asked to go.
        /// </summary>
        /// <remarks>
        /// Not off. A charging station whose display is black is a charging
        /// station somebody walks past, and one that cannot be woken by walking
        /// up to it - which is exactly what somebody arriving at two in the
        /// morning would have to do. At a tenth it is plainly asleep and
        /// plainly working.
        /// </remarks>
        public const Double  DarkestDimTo   = 0.1;

        /// <summary>
        /// What a station that names the hours but not a level dims to.
        /// </summary>
        public const Double  DefaultDimTo   = 0.3;

        /// <summary>
        /// The same number for a reader, written the way this file writes
        /// English rather than the way the machine's region does.
        /// </summary>
        /// <remarks>
        /// Interpolating a Double picks up the current culture, so a station
        /// running in Germany answered "must be between 0,1 and 1" to an API
        /// whose every other word is English. What a caller parses should not
        /// depend on where the station stands.
        /// </remarks>
        private static String Darkest
            => DarkestDimTo.ToString(System.Globalization.CultureInfo.InvariantCulture);

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => DimFrom is null && DimUntil is null && DimTo is null && KeepMoving is null && Port is null;

        /// <summary>
        /// Whether this station has been given quiet hours to keep.
        /// </summary>
        public Boolean DimsAtNight
            => DimFrom.HasValue && DimUntil.HasValue;

        /// <summary>
        /// Whether the picture walks against burn-in.
        /// </summary>
        public Boolean KeepsMoving
            => KeepMoving == true;

        #endregion


        #region IsAQuietHour(Now)

        /// <summary>
        /// Whether the given moment falls in the quiet hours.
        /// </summary>
        /// <remarks>
        /// In the station's own local time, because that is the time somebody
        /// standing in front of it is keeping. Quiet hours that cross midnight
        /// are the ordinary case and not the exception: ten at night until six
        /// in the morning is one window, not two.
        ///
        /// A window whose ends are the same moment is no window at all rather
        /// than the whole day - "from ten until ten" is far more likely to be
        /// a mistake than a request for a screen that is dim for ever.
        /// </remarks>
        public Boolean IsAQuietHour(DateTimeOffset Now)
        {

            if (!DimsAtNight || DimFrom == DimUntil)
                return false;

            var at = TimeOnly.FromDateTime(Now.ToLocalTime().DateTime);

            return DimFrom < DimUntil
                       ? at >= DimFrom && at < DimUntil
                       : at >= DimFrom || at < DimUntil;

        }

        #endregion

        #region HowDim

        /// <summary>
        /// How bright the screen should be in the quiet hours.
        /// </summary>
        public Double HowDim
            => DimTo ?? DefaultDimTo;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The display section of the configuration file.
        /// </summary>
        public static Boolean TryParse(JObject                                        JSON,
                                       [NotNullWhen(true)]  out DisplayConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?               Error)
        {

            Configuration  = null;
            Error          = null;

            if (!TryParseTimeOfDay(JSON, "dimFrom",  out var from,  out Error) ||
                !TryParseTimeOfDay(JSON, "dimUntil", out var until, out Error))
            {
                return false;
            }

            // One end of a window is not a window, and guessing the other end
            // would be this station deciding when the neighbours go to bed.
            if (from.HasValue != until.HasValue)
            {
                Error = $"'{SectionName}' needs both 'dimFrom' and 'dimUntil', or neither.";
                return false;
            }

            Double? dimTo = null;

            if (JSON["dimTo"] is JToken token && token.Type != JTokenType.Null)
            {

                if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                {
                    Error = $"'{SectionName}.dimTo' must be a number between {Darkest} and 1.";
                    return false;
                }

                var value = token.Value<Double>();

                if (value < DarkestDimTo || value > 1)
                {
                    Error = $"'{SectionName}.dimTo' must be between {Darkest} and 1 - " +
                            "a display that goes dark is one nobody can tell from a broken one.";
                    return false;
                }

                dimTo = value;

            }

            if (!ConfigurationReader.TryReadBoolean(JSON, "keepMoving", SectionName, out var keepMoving, out Error) ||
                !ConfigurationReader.TryReadPort   (JSON, "port",       SectionName, out var port,       out Error))
            {
                return false;
            }

            Configuration = new DisplayConfiguration(from, until, dimTo, keepMoving, port);
            return true;

        }

        #endregion

        #region (static) TryParseTimeOfDay(JSON, Name, out Time, out Error)

        /// <summary>
        /// One time of day, written the way a person writes it: "22:00".
        /// </summary>
        private static Boolean TryParseTimeOfDay(JObject                           JSON,
                                                 String                            Name,
                                                 out TimeOnly?                     Time,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            Time   = null;
            Error  = null;

            if (JSON[Name] is not JToken token || token.Type == JTokenType.Null)
                return true;

            var text = token.Value<String>()?.Trim();

            if (String.IsNullOrEmpty(text))
                return true;

            if (!TimeOnly.TryParseExact(text, "HH:mm", out var time) &&
                !TimeOnly.TryParseExact(text, "H:mm",  out time))
            {
                Error = $"'{SectionName}.{Name}' is a time of day such as \"22:00\".";
                return false;
            }

            Time = time;
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

            if (DimFrom.HasValue)     json.Add("dimFrom",     DimFrom.Value.ToString("HH\\:mm"));
            if (DimUntil.HasValue)    json.Add("dimUntil",    DimUntil.Value.ToString("HH\\:mm"));
            if (DimTo.HasValue)       json.Add("dimTo",       DimTo.Value);
            if (KeepMoving.HasValue)  json.Add("keepMoving",  KeepMoving.Value);
            if (Port.HasValue)        json.Add("port",        Port.Value.ToUInt16());

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => (DimsAtNight
                    ? $"dimmed to {HowDim:P0} between {DimFrom!.Value:HH\\:mm} and {DimUntil!.Value:HH\\:mm}"
                    : "always at full brightness") +
               (KeepsMoving
                    ? ", its picture walking against burn-in"
                    : ", its picture standing still");

        #endregion

    }

}
