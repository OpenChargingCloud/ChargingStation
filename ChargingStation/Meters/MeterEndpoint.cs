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

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Where a meter is read: a SunSpec meter speaking Modbus/TLS, at a host, a
    /// port and a unit address.
    /// </summary>
    /// <param name="Host">The name or the address the meter is reached at, which its certificate has to be issued for.</param>
    /// <param name="Port">Its port; 802, Modbus/TLS's own, by default.</param>
    /// <param name="Unit">The Modbus unit address it answers on; 1 by default.</param>
    public sealed record MeterEndpoint(String  Host,
                                       UInt16  Port  = MeterEndpoint.DefaultPort,
                                       Byte    Unit  = MeterEndpoint.DefaultUnit)
    {

        #region Data

        /// <summary>
        /// The port of Modbus/TLS, "mbaps".
        /// </summary>
        public const UInt16  DefaultPort    = 802;

        /// <summary>
        /// The unit address a meter answers on where nobody changed it.
        /// </summary>
        public const Byte    DefaultUnit    = 1;

        /// <summary>
        /// The longest a host name may be, as DNS has it.
        /// </summary>
        public const Int32   MaxHostLength  = 253;

        #endregion

        #region Properties

        /// <summary>
        /// The meter as the node's judgements of servers name it: "10.0.0.5:802".
        /// The port too, because two meters behind one address are two servers.
        /// </summary>
        public String  Server
            => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}";

        #endregion


        #region (static) TryParse(JSON, Path, out Endpoint, out Error)

        /// <summary>
        /// Where a meter is read, from the entry that says so - its "host",
        /// "port" and "unit": <c>{ "host": "10.0.0.5", "port": 802, "unit": 1 }</c>.
        /// </summary>
        /// <remarks>
        /// An entry without a host, or with a blank one, is no meter to read: a
        /// form whose host somebody cleared means just that.
        /// </remarks>
        /// <param name="JSON">The entry.</param>
        /// <param name="Path">Where it is, for the sentence of a refusal: "meters[0]".</param>
        /// <param name="Endpoint">Where the meter is read, or null for nowhere.</param>
        /// <param name="Error">Why it was refused.</param>
        public static Boolean TryParse(JObject                                JSON,
                                       String                                 Path,
                                       out MeterEndpoint?                     Endpoint,
                                       [NotNullWhen(false)] out String?       Error)
        {

            Endpoint  = null;
            Error     = null;

            if (JSON["host"] is JToken given && given.Type is not JTokenType.String and not JTokenType.Null)
            {
                Error = $"'{Path}.host' must be the name or the address the meter is reached at.";
                return false;
            }

            var host = JSON.Value<String>("host")?.Trim();

            if (String.IsNullOrEmpty(host))
                return true;

            if (host.Length > MaxHostLength || host.Any(Char.IsWhiteSpace) || host.Contains('/'))
            {
                Error = $"'{Path}.host' is '{host}', which is no host a meter can be reached at; give its name or its address, without a port or a path.";
                return false;
            }

            // An address in brackets is written that way in a URL, not here.
            if (host.StartsWith('[') && host.EndsWith(']'))
                host = host[1..^1];

            var port = DefaultPort;

            if (JSON["port"] is JToken portToken && portToken.Type != JTokenType.Null)
            {
                if (portToken.Type != JTokenType.Integer || portToken.Value<Int64>() is < 1 or > 65535)
                {
                    Error = $"'{Path}.port' must be a whole number between 1 and 65535.";
                    return false;
                }
                port = (UInt16) portToken.Value<Int64>();
            }

            var unit = DefaultUnit;

            if (JSON["unit"] is JToken unitToken && unitToken.Type != JTokenType.Null)
            {
                if (unitToken.Type != JTokenType.Integer || unitToken.Value<Int64>() is < 0 or > 255)
                {
                    Error = $"'{Path}.unit' must be a Modbus unit address, a whole number between 0 and 255.";
                    return false;
                }
                unit = (Byte) unitToken.Value<Int64>();
            }

            Endpoint = new MeterEndpoint(host, port, unit);
            return true;

        }

        #endregion

        #region WriteInto(JSON)

        /// <summary>
        /// Write where the meter is read into its entry.
        /// </summary>
        public void WriteInto(JObject JSON)
        {
            JSON["host"]  = Host;
            JSON["port"]  = Port;
            JSON["unit"]  = Unit;
        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// Where the meter is read, as a page reads it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("host",  Host),
                   new JProperty("port",  Port),
                   new JProperty("unit",  Unit)
               );

        #endregion

        #region (override) ToString()

        /// <summary>
        /// "10.0.0.5:802, unit 1" - an address with a colon of its own in brackets.
        /// </summary>
        public override String ToString()

            => $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}, unit {Unit}";

        #endregion

    }

}
