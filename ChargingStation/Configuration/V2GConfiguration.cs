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

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// The "v2g" section of the configuration file: what this charging station
    /// offers a vehicle on the wire below the charging cable.
    /// </summary>
    /// <remarks>
    /// As in the DNS and NTS sections, null means "the file does not say", and
    /// what is missing keeps whatever the station was given at construction -
    /// which for this section is whatever the command line asked for.
    ///
    /// Two things are deliberately not here. The V2G server certificate is a
    /// file and a password and belongs where the other certificates are, not
    /// in a settings document; without one the endpoint speaks plain TCP and
    /// says so out loud. And the simulated SLAC medium over UDP - its endpoint
    /// and its peers - stays on the command line, because it exists for a
    /// bench that has no powerline modem, and a station configured from a file
    /// is not that bench.
    /// </remarks>
    /// <param name="Enabled">Whether anything at all comes up below the cable.</param>
    /// <param name="InterfaceName">The powerline interface, or null to let the station pick one.</param>
    /// <param name="V2GPort">The TCP port of the V2G endpoint; 0 lets the system pick one, which is what SDP then advertises.</param>
    /// <param name="SDP">Whether the SECC Discovery Protocol answers vehicles looking for that endpoint.</param>
    /// <param name="Loopback">Whether SDP also answers a vehicle running on this same machine - for a bench, and off in the field.</param>
    /// <param name="SlacTransport">Which medium the SLAC listener listens on.</param>
    /// <param name="EVSEId">The EVSE identification SLAC hands to a vehicle.</param>
    public sealed record V2GConfiguration(Boolean?            Enabled         = null,
                                          String?             InterfaceName   = null,
                                          UInt16?             V2GPort         = null,
                                          Boolean?            SDP             = null,
                                          Boolean?            Loopback        = null,
                                          SlacTransportKind?  SlacTransport   = null,
                                          String?             EVSEId          = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName             = "v2g";

        /// <summary>
        /// The longest a network interface name may be written. Linux stops at
        /// 16, Windows hands out names that are a GUID with decoration around
        /// them, and this is comfortably longer than either.
        /// </summary>
        public const Int32   MaxInterfaceNameLength  = 128;

        /// <summary>
        /// The longest an EVSE identification may be: HomePlug carries exactly
        /// this many bytes of it.
        /// </summary>
        public const Int32   MaxEVSEIdLength         = V2GLink.EVSEIdSize;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "v2g" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                     JSON,
                                       [NotNullWhen(true)]  out V2GConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",    SectionName, out var enabled,       out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "sdp",        SectionName, out var sdp,           out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "loopback",   SectionName, out var loopback,      out Error) ||
                !ConfigurationReader.TryReadString (JSON, "interface",  SectionName, MaxInterfaceNameLength, out var interfaceName, out Error) ||
                !TryReadV2GPort                    (JSON, "port",       SectionName, out var port,          out Error) ||
                !TryReadSlacTransport              (JSON, "slac",       SectionName, out var transport,     out Error) ||
                !ConfigurationReader.TryReadString (JSON, "evseId",     SectionName, MaxEVSEIdLength,       out var evseId,        out Error))
            {
                return false;
            }

            Configuration = new V2GConfiguration(
                                enabled,
                                interfaceName,
                                port,
                                sdp,
                                loopback,
                                transport,
                                evseId
                            );

            return true;

        }

        #endregion

        #region (static) TryReadV2GPort(JSON, Name, Path, out Port, out Error)

        /// <summary>
        /// The port of the V2G endpoint, where zero is a real answer.
        /// </summary>
        /// <remarks>
        /// ConfigurationReader.TryReadPort refuses zero on purpose, because
        /// every other port in this file belongs to a client and zero means
        /// nothing to one. This port belongs to a listener, where zero is how
        /// "any free one" is spelled - and SDP exists precisely so that a
        /// vehicle can still find it afterwards.
        /// </remarks>
        public static Boolean TryReadV2GPort(JObject                           JSON,
                                             String                            Name,
                                             String?                           Path,
                                             out UInt16?                       Port,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Port   = null;
            Error  = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path is null ? Name : $"{Path}.{Name}";

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{where}' must be a port number, or 0 to let the system pick one.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < 0 || number > UInt16.MaxValue)
            {
                Error = $"'{where}' must be between 0 and {UInt16.MaxValue}.";
                return false;
            }

            Port = (UInt16) number;
            return true;

        }

        #endregion

        #region (static) TryReadSlacTransport(JSON, Name, Path, out Transport, out Error)

        /// <summary>
        /// Which medium the SLAC listener listens on, written the way somebody
        /// would write it.
        /// </summary>
        /// <remarks>
        /// Matched without regard to case, and the message lists the choices,
        /// because "must be a valid SlacTransportKind" over a settings file
        /// has told nobody anything.
        /// </remarks>
        public static Boolean TryReadSlacTransport(JObject                           JSON,
                                                   String                            Name,
                                                   String?                           Path,
                                                   out SlacTransportKind?            Transport,
                                                   [NotNullWhen(false)] out String?  Error)
        {

            Transport  = null;
            Error      = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path is null ? Name : $"{Path}.{Name}";

            if (token.Type != JTokenType.String)
            {
                Error = $"'{where}' must be one of {Choices()}.";
                return false;
            }

            var text = token.Value<String>()?.Trim() ?? "";

            if (text.Length == 0)
                return true;

            if (!Enum.TryParse<SlacTransportKind>(text, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                Error = $"'{where}' is \"{text}\", which is not one of {Choices()}.";
                return false;
            }

            Transport = parsed;
            return true;

        }

        #region (private static) Choices()

        /// <summary>
        /// The transports, listed the way a sentence lists things.
        /// </summary>
        private static String Choices()

            => String.Join(", ", Enum.GetNames<SlacTransportKind>().
                                      Select(name => $"\"{name.ToLowerInvariant()}\""));

        #endregion

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this station was not
        /// told about is not written.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.HasValue)           json.Add("enabled",    Enabled.Value);
            if (InterfaceName is not null)  json.Add("interface",  InterfaceName);
            if (V2GPort.HasValue)           json.Add("port",       V2GPort.Value);
            if (SDP.HasValue)               json.Add("sdp",        SDP.Value);
            if (Loopback.HasValue)          json.Add("loopback",   Loopback.Value);
            if (SlacTransport.HasValue)     json.Add("slac",       SlacTransport.Value.ToString().ToLowerInvariant());
            if (EVSEId is not null)         json.Add("evseId",     EVSEId);

            return json;

        }

        #endregion

        #region Apply(Options)

        /// <summary>
        /// This section laid on top of the options a station already has: what
        /// the file says wins, and what it does not mention is left alone.
        /// </summary>
        public V2GOptions Apply(V2GOptions Options)

            => Options with {
                   Enabled        = Enabled        ?? Options.Enabled,
                   InterfaceName  = InterfaceName  ?? Options.InterfaceName,
                   V2GPort        = V2GPort        ?? Options.V2GPort,
                   SDP                = SDP       ?? Options.SDP,
                   MulticastLoopback  = Loopback  ?? Options.MulticastLoopback,
                   SlacTransport  = SlacTransport  ?? Options.SlacTransport,
                   EVSEId         = EVSEId         ?? Options.EVSEId
               };

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Enabled == false
                   ? "V2G off"
                   : String.Join(", ",
                         new[] {
                             Enabled       == true     ? "V2G on"                              : null,
                             InterfaceName is not null ? $"on \"{InterfaceName}\""             : null,
                             V2GPort.HasValue          ? V2GPort.Value == 0
                                                             ? "on any free port"
                                                             : $"on port {V2GPort.Value}"      : null,
                             SDP.HasValue              ? SDP.Value ? "SDP answering"
                                                                   : "SDP silent"              : null,
                             Loopback      == true     ? "SDP hearing this machine too"       : null,
                             SlacTransport.HasValue    ? $"SLAC over {SlacTransport.Value}"    : null,
                             EVSEId        is not null ? $"as \"{EVSEId}\""                    : null
                         }.Where(part => part is not null));

        #endregion

    }

}
