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

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// The nameplate of this charging station: what it tells a CSMS it is, in
    /// its BootNotification - who made it, which model it is, its serial
    /// number and its firmware.
    /// </summary>
    /// <remarks>
    /// A CSMS learns these only from a BootNotification, so a change hangs up
    /// and dials again, as a change of the EVSEs does: the station registers
    /// anew and says what it is now.
    ///
    /// Each field is held to the shorter of what OCPP 1.6 and OCPP 2.1 take,
    /// and to printable ASCII, which is all OCPP 1.6 takes: a station that
    /// speaks either protocol says the same thing in both.
    /// </remarks>
    /// <param name="Vendor">Who made this station; GraphDefined where not said.</param>
    /// <param name="Model">Which model it is; ChargingStationCLI where not said.</param>
    /// <param name="SerialNumber">Its serial number; none where not said.</param>
    /// <param name="FirmwareVersion">Its firmware; the version and commit it was built from where not said.</param>
    public sealed record NameplateConfiguration(String?  Vendor            = null,
                                                String?  Model             = null,
                                                String?  SerialNumber      = null,
                                                String?  FirmwareVersion   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName                = "nameplate";

        /// <summary>
        /// Who made a station that does not say.
        /// </summary>
        public const String  DefaultVendor              = "GraphDefined";

        /// <summary>
        /// Which model a station is that does not say.
        /// </summary>
        public const String  DefaultModel               = "ChargingStationCLI";

        /// <summary>
        /// The longest vendor: OCPP 1.6 takes 20 characters, OCPP 2.1 takes 50.
        /// </summary>
        public const Int32   MaxVendorLength            = 20;

        /// <summary>
        /// The longest model: 20 characters in both.
        /// </summary>
        public const Int32   MaxModelLength             = 20;

        /// <summary>
        /// The longest serial number: 25 characters in both.
        /// </summary>
        public const Int32   MaxSerialNumberLength      = 25;

        /// <summary>
        /// The longest firmware version: 50 characters in both.
        /// </summary>
        public const Int32   MaxFirmwareVersionLength   = 50;

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => Vendor is null && Model is null && SerialNumber is null && FirmwareVersion is null;

        /// <summary>
        /// Who made this station, as a CSMS is told.
        /// </summary>
        public String VendorToSay
            => Vendor ?? DefaultVendor;

        /// <summary>
        /// Which model it is, as a CSMS is told.
        /// </summary>
        public String ModelToSay
            => Model  ?? DefaultModel;

        #endregion


        #region FirmwareVersionToSay(Built)

        /// <summary>
        /// Its firmware, as a CSMS is told: what the file says, or else what it
        /// was built from.
        /// </summary>
        /// <param name="Built">The version and commit this station was built from.</param>
        public String FirmwareVersionToSay(String Built)
            => FirmwareVersion ?? Built;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The nameplate section of the configuration file.
        /// </summary>
        /// <remarks>
        /// A field that is blank is a field not given, so a form whose serial
        /// number was cleared says none rather than an empty one.
        /// </remarks>
        public static Boolean TryParse(JObject                                          JSON,
                                       [NotNullWhen(true)]  out NameplateConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?                 Error)
        {

            Configuration = null;

            if (!TryRead(JSON, "vendor",           MaxVendorLength,           out var vendor,    out Error) ||
                !TryRead(JSON, "model",            MaxModelLength,            out var model,     out Error) ||
                !TryRead(JSON, "serialNumber",     MaxSerialNumberLength,     out var serial,    out Error) ||
                !TryRead(JSON, "firmwareVersion",  MaxFirmwareVersionLength,  out var firmware,  out Error))
            {
                return false;
            }

            Configuration = new NameplateConfiguration(vendor, model, serial, firmware);
            return true;

        }

        #endregion

        #region (private static) TryRead(JSON, Name, MaxLength, out Value, out Error)

        /// <summary>
        /// One field: text, no longer than both protocols take, and nothing but
        /// printable ASCII.
        /// </summary>
        private static Boolean TryRead(JObject                           JSON,
                                       String                            Name,
                                       Int32                             MaxLength,
                                       out String?                       Value,
                                       [NotNullWhen(false)] out String?  Error)
        {

            if (!ConfigurationReader.TryReadString(JSON, Name, SectionName, MaxLength, out Value, out Error))
                return false;

            if (Value is not null && Value.Any(character => character < ' ' || character > '~'))
            {
                Error = $"'{SectionName}.{Name}' may hold nothing but printable ASCII - it is all OCPP 1.6 takes.";
                Value = null;
                return false;
            }

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

            if (Vendor          is not null)  json.Add("vendor",           Vendor);
            if (Model           is not null)  json.Add("model",            Model);
            if (SerialNumber    is not null)  json.Add("serialNumber",     SerialNumber);
            if (FirmwareVersion is not null)  json.Add("firmwareVersion",  FirmwareVersion);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{VendorToSay} {ModelToSay}" +
               (SerialNumber    is not null ? $", serial number {SerialNumber}" : "") +
               (FirmwareVersion is not null ? $", firmware {FirmwareVersion}"   : "");

        #endregion

    }

}
