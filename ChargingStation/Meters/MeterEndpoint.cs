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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Where a meter is read: a SunSpec meter speaking Modbus/TLS, at a host, a
    /// port and a unit address - or speaking Modbus over a WebSocket or over
    /// SSH, at the URL of a connection of this station to an energy meter.
    /// </summary>
    /// <remarks>
    /// Over a WebSocket or over SSH the meter is read over the connection of the
    /// station's store of that URL, of the type "EnergyMeter": signed in with
    /// what that connection signs in with, its server believed as it believes
    /// it, and what the station may do there is the role the meter gives its
    /// login. The TLS identities of the meters are Modbus/TLS's alone.
    /// </remarks>
    /// <param name="Host">The name or the address the meter is reached at, which its certificate has to be issued for.</param>
    /// <param name="Port">Its port; 802, Modbus/TLS's own, by default.</param>
    /// <param name="Unit">The Modbus unit address it answers on; 1 by default.</param>
    /// <param name="Over">The ws://, wss:// or ssh:// URL of the connection it is read over; null for Modbus/TLS.</param>
    public sealed record MeterEndpoint(String  Host,
                                       IPPort  Port,
                                       Byte    Unit  = MeterEndpoint.DefaultUnit,
                                       URL?    Over  = null)
    {

        #region Data

        /// <summary>
        /// The port of Modbus/TLS, "mbaps".
        /// </summary>
        public static readonly IPPort  DefaultPort    = IPPort.ModbusTLS;

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

        /// <summary>
        /// Whether it is read over a connection of the station - a WebSocket or
        /// SSH - rather than over Modbus/TLS.
        /// </summary>
        public Boolean IsOverAConnection
            => Over is not null;

        #endregion


        #region (static) TryParse(JSON, Path, out Endpoint, out Error)

        /// <summary>
        /// Where a meter is read, from the entry that says so - its "host",
        /// "port" and "unit": <c>{ "host": "10.0.0.5", "port": 802, "unit": 1 }</c> -
        /// or its "url" and "unit": <c>{ "url": "ssh://cs001@10.0.0.5:2222" }</c>,
        /// "mbaps://", "ws://", "wss://" or "ssh://".
        /// </summary>
        /// <remarks>
        /// An entry without a host, or with a blank one, is no meter to read: a
        /// form whose host somebody cleared means just that. "mbaps://" is
        /// Modbus/TLS as a host and a port say it.
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

            URL? over = null;

            if (JSON["url"] is JToken urlToken && urlToken.Type != JTokenType.Null)
            {

                if (urlToken.Type != JTokenType.String)
                {
                    Error = $"'{Path}.url' must be the URL the meter is read at: mbaps://, ws://, wss:// or ssh://.";
                    return false;
                }

                if (JSON["host"] is JToken hostToo && hostToo.Type != JTokenType.Null ||
                    JSON["port"] is JToken portToo && portToo.Type != JTokenType.Null)
                {
                    Error = $"'{Path}' says where the meter is twice: by its 'url', and by its 'host' or 'port'. Say it once.";
                    return false;
                }

                var text = urlToken.Value<String>()!.Trim();

                if (text.Length == 0)
                    return true;

                var modbusTLS = text.StartsWith("mbaps://", StringComparison.OrdinalIgnoreCase);

                if (URL.TryParse(modbusTLS ? "tls://" + text["mbaps://".Length..] : text) is not URL url ||
                    (!modbusTLS && url.Scheme != URIScheme.ws && url.Scheme != URIScheme.wss && url.Scheme != URIScheme.ssh))
                {
                    Error = $"'{Path}.url' is '{text}', which is no URL a meter can be read at: mbaps://, ws://, wss:// or ssh://.";
                    return false;
                }

                if (!modbusTLS)
                    over = url;

                JSON = new JObject(JSON) {
                           ["host"] = url.Host.ToString(),
                           ["port"] = (url.Port ?? (modbusTLS                      ? DefaultPort
                                                  : url.Scheme == URIScheme.ssh  ? IPPort.SSH
                                                  : url.Scheme == URIScheme.wss  ? IPPort.HTTPS
                                                                                 : IPPort.HTTP)).ToUInt16()
                       };

                JSON.Remove("url");

            }

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
                port = IPPort.Parse((UInt16) portToken.Value<Int64>());
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

            Endpoint = new MeterEndpoint(host, port, unit, over);
            return true;

        }

        #endregion

        #region WriteInto(JSON)

        /// <summary>
        /// Write where the meter is read into its entry.
        /// </summary>
        public void WriteInto(JObject JSON)
        {

            if (Over is URL over)
            {
                JSON.Remove("host");
                JSON.Remove("port");
                JSON["url"]   = over.ToString();
            }

            else
            {
                JSON.Remove("url");
                JSON["host"]  = Host;
                JSON["port"]  = Port.ToUInt16();
            }

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
                   new JProperty("port",  Port.ToUInt16()),
                   new JProperty("unit",  Unit),
                   new JProperty("url",   Over?.ToString())
               );

        #endregion

        #region (override) ToString()

        /// <summary>
        /// "10.0.0.5:802, unit 1" - an address with a colon of its own in brackets.
        /// </summary>
        public override String ToString()

            => Over is URL over
                   ? $"{over}, unit {Unit}"
                   : $"{(Host.Contains(':') ? $"[{Host}]" : Host)}:{Port}, unit {Unit}";

        #endregion

    }

}
