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

using OCPPServerConfiguration = cloud.charging.open.protocols.OCPP.Node.Serving.OCPPServerConfiguration;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// Whether this station has an OCPP server of its own: a WebSocket server
    /// a CSMS or a local controller dials, where the station cannot dial them.
    /// The section "ocppServer" every node that serves OCPP has, read as a
    /// charging station reads it.
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
    /// <para>
    /// The record is WWCP_OCPP_Node's, which a local controller and a gateway
    /// read as well, with what a server of theirs has beside it. A station
    /// serves with the certificate of its web interface, on every interface
    /// it listens on, and reads 'enabled', 'port', 'port16' and 'http3' alone.
    /// </para>
    /// </remarks>
    public static class OCPPServerSection
    {

        #region Data

        /// <summary>
        /// The fields a charging station reads of the section.
        /// </summary>
        public static readonly String[] Fields = [ "enabled", "port", "port16", "http3" ];

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

            if (JSON.Properties().FirstOrDefault(property => !Fields.Contains(property.Name)) is JProperty unknown)
            {
                Error = $"'{OCPPServerConfiguration.SectionName}.{unknown.Name}' is not known: the section has 'enabled', 'port', 'port16' and 'http3'.";
                return false;
            }

            if (!OCPPServerConfiguration.TryParse(JSON, out var section, out Error))
                return false;

            if (section.IsEnabled && !section.TCPPort.HasValue && !section.Port16.HasValue)
            {
                Error = $"'{OCPPServerConfiguration.SectionName}' is switched on and names no port: say 'port' for OCPP 2.1 and 2.0.1, 'port16' for OCPP 1.6, or both.";
                return false;
            }

            Configuration = section;
            return true;

        }

        #endregion

    }

}
