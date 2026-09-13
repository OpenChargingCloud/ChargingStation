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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.ChargingStation.EVSEs
{

    /// <summary>
    /// The EVSEs this charging station has, in a file beside it.
    /// </summary>
    /// <remarks>
    /// How many outlets a charging station has and what can be plugged into
    /// them is a property of the hardware, not of a command line - it does not
    /// change between starts, and it should not have to be repeated at every
    /// one. So it is written down, and the web interface edits the file rather
    /// than a copy of it in memory.
    ///
    /// Unlike the web login this holds nothing secret, so it is written as an
    /// ordinary file and may be read by anybody who can read the directory.
    /// </remarks>
    public sealed class EVSEConfigFile
    {

        #region Data

        /// <summary>
        /// The default file name, below the repository root.
        /// </summary>
        public const String DefaultFileName = "evses.json";

        #endregion

        #region Properties

        /// <summary>
        /// The full path of the file.
        /// </summary>
        public String   Path      { get; }

        /// <summary>
        /// Whether the file exists.
        /// </summary>
        public Boolean  Exists
            => File.Exists(Path);

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The EVSE file at the given path; it need not exist yet.
        /// </summary>
        public EVSEConfigFile(String Path)
        {

            if (String.IsNullOrWhiteSpace(Path))
                throw new ArgumentException("The path of the EVSE file must not be empty!", nameof(Path));

            this.Path = System.IO.Path.GetFullPath(Path);

        }

        #endregion


        #region TryLoad(out EVSEs, out Error)

        /// <summary>
        /// The EVSEs from the file. False without an error when there is no
        /// file, false with one when the file cannot be read or does not hold
        /// a list of EVSEs.
        /// </summary>
        public Boolean TryLoad(out IReadOnlyList<EVSEConfig>?  EVSEs,
                               out String?                     Error)
        {

            EVSEs  = null;
            Error  = null;

            if (!File.Exists(Path))
                return false;

            try
            {

                var json = JObject.Parse(File.ReadAllText(Path));

                if (json["evses"] is not JArray array)
                {
                    Error = $"'{Path}' has no 'evses' array.";
                    return false;
                }

                if (!TryParse(array, out EVSEs, out var problem))
                {
                    Error = $"'{Path}': {problem}";
                    return false;
                }

                return true;

            }
            catch (Exception e)
            {
                Error = $"'{Path}' could not be read: {e.Message}";
                return false;
            }

        }

        #endregion

        #region (static) TryParse(JSON, out EVSEs, out Error)

        /// <summary>
        /// A list of EVSEs, with their numbering checked: OCPP counts them from
        /// 1 upwards without gaps, and a station that told a CSMS about an EVSE
        /// 4 it has no 3 for would be describing hardware nobody can find.
        /// </summary>
        public static Boolean TryParse(JArray                                          JSON,
                                       [NotNullWhen(true)]  out IReadOnlyList<EVSEConfig>? EVSEs,
                                       [NotNullWhen(false)] out String?                    Error)
        {

            EVSEs  = null;
            Error  = null;

            var parsed = new List<EVSEConfig>();

            foreach (var token in JSON)
            {

                if (!EVSEConfig.TryParse(token, out var evse, out Error))
                    return false;

                parsed.Add(evse);

            }

            if (parsed.Count == 0)
            {
                Error = "A charging station needs at least one EVSE.";
                return false;
            }

            if (parsed.Count > EVSEConfig.MaxEVSEs)
            {
                Error = $"A charging station may have at most {EVSEConfig.MaxEVSEs} EVSEs here.";
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

        #region Save(EVSEs)

        /// <summary>
        /// Write the EVSEs, replacing what was there.
        /// </summary>
        public void Save(IEnumerable<EVSEConfig> EVSEs)
        {

            var directory = System.IO.Path.GetDirectoryName(Path);

            if (!String.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(
                Path,
                new JObject(
                    new JProperty("evses", new JArray(EVSEs.OrderBy(evse => evse.Id).Select(evse => evse.ToJSON())))
                ).ToString(Formatting.Indented) + Environment.NewLine
            );

        }

        #endregion

    }

}
