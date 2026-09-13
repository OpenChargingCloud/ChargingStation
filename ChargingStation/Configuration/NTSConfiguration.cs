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
using org.GraphDefined.Vanaheimr.Hermod.DNS;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// The "nts" section of the configuration file: where this charging station
    /// reads the time, and how it proves that the answer came from there.
    /// </summary>
    /// <remarks>
    /// As in the DNS section, null means "the file does not say": what is
    /// missing keeps whatever the station was given at construction.
    /// </remarks>
    /// <param name="Enabled">Whether this station asks a time server at all.</param>
    /// <param name="Hostname">The NTS server.</param>
    /// <param name="NTSKEPort">Where its key exchange listens; 4460 unless said otherwise.</param>
    /// <param name="NTPPort">Where its NTP service listens; 123 unless said otherwise.</param>
    /// <param name="Timeout">How long one exchange may take.</param>
    public sealed record NTSConfiguration(Boolean?     Enabled     = null,
                                          DomainName?  Hostname    = null,
                                          IPPort?      NTSKEPort   = null,
                                          IPPort?      NTPPort     = null,
                                          TimeSpan?    Timeout     = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName          = "nts";

        /// <summary>
        /// The time server this station asks when nothing says otherwise.
        /// </summary>
        /// <remarks>
        /// The Physikalisch-Technische Bundesanstalt, which is one of the few
        /// public NTS servers that is also a legal time source somewhere.
        /// </remarks>
        public const String  DefaultHostname      = "ptbtime1.ptb.de";

        /// <summary>
        /// The longest an exchange may be allowed to take, in seconds. An hour
        /// is not a timeout any more, and zero is not one either.
        /// </summary>
        public const Double  MaxTimeoutSeconds    = 3600;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "nts" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                    JSON,
                                       [NotNullWhen(true)]  out NTSConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",         "nts",      out var enabled,   out Error) ||
                !ConfigurationReader.TryReadString (JSON, "hostname",        "nts", 253, out var hostname,  out Error) ||
                !ConfigurationReader.TryReadPort   (JSON, "ntsKEPort",       "nts",      out var ntsKEPort, out Error) ||
                !ConfigurationReader.TryReadPort   (JSON, "ntpPort",         "nts",      out var ntpPort,   out Error) ||
                !ConfigurationReader.TryReadSeconds(JSON, "timeoutSeconds",  "nts", 0.1, MaxTimeoutSeconds, out var timeout, out Error))
            {
                return false;
            }

            DomainName? domainName = null;

            if (hostname is not null && !DomainName.TryParse(hostname, out domainName, out var problem))
            {
                Error = $"'nts.hostname': {problem}";
                return false;
            }

            Configuration = new NTSConfiguration(
                                enabled,
                                domainName,
                                ntsKEPort,
                                ntpPort,
                                timeout
                            );

            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this station was not
        /// told about is not written.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.HasValue)      json.Add("enabled",         Enabled.  Value);
            if (Hostname is not null)  json.Add("hostname",        Hostname. ToString());
            if (NTSKEPort.HasValue)    json.Add("ntsKEPort",       NTSKEPort.Value.ToUInt16());
            if (NTPPort.  HasValue)    json.Add("ntpPort",         NTPPort.  Value.ToUInt16());
            if (Timeout.  HasValue)    json.Add("timeoutSeconds",  Timeout.  Value.TotalSeconds);

            return json;

        }

        #endregion

    }

}
