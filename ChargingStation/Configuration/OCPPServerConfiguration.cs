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

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// Whether this station has an OCPP server of its own: a WebSocket server
    /// a CSMS or a local controller dials, where the station cannot dial them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OCPP as it always is, and only who opens the WebSocket is the other way
    /// round: what dials in is written down on the Connections page as a
    /// connection the other end makes, signs in under its name with the
    /// credentials that connection names, and is told that this station
    /// started as a back end it dialled is.
    /// </para>
    /// <para>
    /// Off unless the file says otherwise, and without a port there is nothing
    /// to switch on: a station that has never been told to listen for OCPP
    /// does not. One port for OCPP 2.1 and 2.0.1 and another for OCPP 1.6,
    /// since the station is two nodes and a port is one's; a version without a
    /// port is not served.
    /// </para>
    /// <para>
    /// Over HTTP/1.1 and HTTP/2, on TCP, and over HTTP/3 as well where the
    /// file says so: HTTP/3 is QUIC, and opens a UDP port of the same number
    /// beside each TCP port - which nobody is given who did not ask. It is
    /// what a one-time password bound to its TLS session is checked over.
    /// </para>
    /// </remarks>
    /// <param name="Enabled">Whether the server is on; off where not said.</param>
    /// <param name="Port">The TCP port OCPP 2.1 and 2.0.1 are served on; not served where not said.</param>
    /// <param name="Port16">The TCP port OCPP 1.6 is served on; not served where not said.</param>
    /// <param name="HTTP3">Whether WebSockets are taken over HTTP/3 as well, on the UDP port of the same number; not where not said.</param>
    public sealed record OCPPServerConfiguration(Boolean?  Enabled   = null,
                                                 IPPort?   Port      = null,
                                                 IPPort?   Port16    = null,
                                                 Boolean?  HTTP3     = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String SectionName = "ocppServer";

        #endregion

        #region Properties

        /// <summary>
        /// Whether the server is on.
        /// </summary>
        public Boolean IsEnabled
            => Enabled == true;

        /// <summary>
        /// Whether WebSockets are taken over HTTP/3 as well.
        /// </summary>
        public Boolean TakesHTTP3
            => HTTP3 == true;

        #endregion

        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The OCPP server section of the configuration file.
        /// </summary>
        /// <remarks>
        /// A field it does not know is an error rather than one passed over:
        /// somebody who wrote another believed they had configured something.
        /// </remarks>
        public static Boolean TryParse(JObject                                           JSON,
                                       [NotNullWhen(true)]  out OCPPServerConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?                  Error)
        {

            Configuration = null;

            if (JSON.Properties().FirstOrDefault(property => property.Name is not ("enabled" or "port" or "port16" or "http3")) is JProperty unknown)
            {
                Error = $"'{SectionName}.{unknown.Name}' is not known: the section has 'enabled', 'port', 'port16' and 'http3'.";
                return false;
            }

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled", SectionName, out var enabled, out Error))
                return false;

            if (!ConfigurationReader.TryReadPort(JSON, "port",   SectionName, out var port,   out Error))
                return false;

            if (!ConfigurationReader.TryReadPort(JSON, "port16", SectionName, out var port16, out Error))
                return false;

            if (!ConfigurationReader.TryReadBoolean(JSON, "http3", SectionName, out var http3, out Error))
                return false;

            if (port.HasValue && port16.HasValue && port.Value == port16.Value)
            {
                Error = $"'{SectionName}.port' and '{SectionName}.port16' are the same port, and a port is one server's: " +
                        "OCPP 2.1 and OCPP 1.6 are two nodes of this station, each with a server of its own.";
                return false;
            }

            if (enabled == true && !port.HasValue && !port16.HasValue)
            {
                Error = $"'{SectionName}' is switched on and names no port: say 'port' for OCPP 2.1 and 2.0.1, 'port16' for OCPP 1.6, or both.";
                return false;
            }

            Configuration = new OCPPServerConfiguration(enabled, port, port16, http3);
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

            if (Enabled.HasValue)
                json.Add("enabled", Enabled.Value);

            if (Port.HasValue)
                json.Add("port",    Port.Value.ToUInt16());

            if (Port16.HasValue)
                json.Add("port16",  Port16.Value.ToUInt16());

            if (HTTP3.HasValue)
                json.Add("http3",   HTTP3.Value);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()
            => IsEnabled
                   ? $"an OCPP server on {String.Join(" and ", new[] { Port.HasValue ? $"port {Port} (2.1)" : null, Port16.HasValue ? $"port {Port16} (1.6)" : null }.Where(port => port is not null))}{(TakesHTTP3 ? ", over HTTP/3 as well" : "")}"
                   : "no OCPP server";

        #endregion

    }

}
