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

using cloud.charging.open.ChargingStation.EVSEs;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the Configuration pages of the web interface read and write: one
    /// resource per thing that can be configured, rather than one big document.
    /// </summary>
    /// <remarks>
    /// Each of them says which of its fields may be changed and which only
    /// describe what is there - a client that is handed its servers at
    /// construction cannot be given different ones afterwards, and a page that
    /// offered to try would be lying about what the button does.
    /// </remarks>
    public partial class ChargingStation
    {

        #region DNS

        #region DNSConfigurationJSON()

        /// <summary>
        /// How this station resolves names.
        /// </summary>
        public JObject DNSConfigurationJSON()

            => new (

                   // What was decided when the client was made, and cannot be
                   // decided again without making another one.
                   new JProperty("servers",           new JArray(
                       dnsClient.DNSServers.Select(server => new JObject(
                           new JProperty("address",       server.IPAddress?.ToString()),
                           new JProperty("domainName",    server.DomainName?.ToString()),
                           new JProperty("port",          server.Port.ToUInt16()),
                           new JProperty("transport",     server.Transport.ToString()),
                           new JProperty("queryTimeout",  server.QueryTimeout?.ToString())
                       ))
                   )),
                   new JProperty("queryTimeout",      dnsClient.QueryTimeout.ToString()),
                   new JProperty("udpPayloadSize",    dnsClient.UDPPayloadSize),
                   new JProperty("ednsOptions",       dnsClient.EDNSOptions.Count),
                   new JProperty("clientSubnet",      dnsClient.ClientSubnet is null
                                                          ? null
                                                          : $"{dnsClient.ClientSubnet.Address}/{dnsClient.ClientSubnet.SourcePrefixLength}"),

                   // And what a request may still change.
                   new JProperty("settings",          new JObject(
                       new JProperty("recursionDesired",  dnsClient.RecursionDesired),
                       new JProperty("useCache",          dnsClient.UseCache),
                       new JProperty("dnssecOK",          dnsClient.DnssecOK),
                       new JProperty("followCNAMEs",      dnsClient.FollowCNAMEs),
                       new JProperty("maxCNAMEFollows",   dnsClient.MaxCNAMEFollows),
                       new JProperty("maxRetries",        dnsClient.MaxRetries)
                   )),

                   new JProperty("cache",             new JObject(
                       new JProperty("cleanUpEvery",      dnsClient.DNSCache.CleanUpEvery.    ToString()),
                       new JProperty("negativeCacheTTL",  dnsClient.DNSCache.NegativeCacheTTL.ToString())
                   ))

               );

        #endregion

        #region TryUpdateDNSConfiguration(JSON, out Error)

        /// <summary>
        /// Change what may be changed about the name resolution. Every field is
        /// optional; what is not named stays as it is.
        /// </summary>
        /// <param name="JSON">The fields to change.</param>
        /// <param name="Error">What is wrong with them, when something is.</param>
        public Boolean TryUpdateDNSConfiguration(JObject                          JSON,
                                                 [NotNullWhen(false)] out String? Error)
        {

            Error = null;

            // Read everything before writing anything: half an applied change
            // is worse than none, and the browser would have no way of knowing
            // which half.
            if (!TryReadBoolean(JSON, "recursionDesired", out var recursionDesired, out Error) ||
                !TryReadBoolean(JSON, "useCache",         out var useCache,         out Error) ||
                !TryReadBoolean(JSON, "dnssecOK",         out var dnssecOK,         out Error) ||
                !TryReadBoolean(JSON, "followCNAMEs",     out var followCNAMEs,     out Error) ||
                !TryReadByte   (JSON, "maxCNAMEFollows",  out var maxCNAMEFollows,  out Error) ||
                !TryReadByte   (JSON, "maxRetries",       out var maxRetries,       out Error))
            {
                return false;
            }

            var changed = new List<String>();

            if (recursionDesired.HasValue && dnsClient.RecursionDesired != recursionDesired)
            {
                dnsClient.RecursionDesired = recursionDesired;
                changed.Add($"recursion desired = {recursionDesired}");
            }

            if (useCache.HasValue && dnsClient.UseCache != useCache.Value)
            {
                dnsClient.UseCache = useCache.Value;
                changed.Add($"use cache = {useCache.Value}");
            }

            if (dnssecOK.HasValue && dnsClient.DnssecOK != dnssecOK.Value)
            {
                dnsClient.DnssecOK = dnssecOK.Value;
                changed.Add($"DNSSEC OK = {dnssecOK.Value}");
            }

            if (followCNAMEs.HasValue && dnsClient.FollowCNAMEs != followCNAMEs.Value)
            {
                dnsClient.FollowCNAMEs = followCNAMEs.Value;
                changed.Add($"follow CNAMEs = {followCNAMEs.Value}");
            }

            if (maxCNAMEFollows.HasValue && dnsClient.MaxCNAMEFollows != maxCNAMEFollows.Value)
            {
                dnsClient.MaxCNAMEFollows = maxCNAMEFollows.Value;
                changed.Add($"max CNAME follows = {maxCNAMEFollows.Value}");
            }

            if (maxRetries.HasValue && dnsClient.MaxRetries != maxRetries.Value)
            {
                dnsClient.MaxRetries = maxRetries.Value;
                changed.Add($"max retries = {maxRetries.Value}");
            }

            if (changed.Count > 0)
                Log.Notice($"DNS configuration changed: {String.Join(", ", changed)}.", "dns", "config");

            return true;

        }

        #endregion

        #endregion


        #region NTS

        #region NTSConfigurationJSON()

        /// <summary>
        /// Where this station gets the time from, and how the key exchange
        /// behind it is doing.
        /// </summary>
        public JObject NTSConfigurationJSON()
        {

            var pool = ntsClient.CookiePoolDiagnostics;
            var last = ntsClient.LastNTSKEResponse;

            return new JObject(

                       new JProperty("server",       new JObject(
                           new JProperty("hostname",              ntsClient.Hostname.ToString()),
                           new JProperty("ntsKEPort",             ntsClient.NTSKE_Port.ToUInt16()),
                           new JProperty("ntpPort",               ntsClient.NTP_Port.  ToUInt16()),
                           new JProperty("ipVersionPreference",   ntsClient.IPVersionPreference.ToString()),
                           new JProperty("clientId",              ntsClient.Id)
                       )),

                       // The one thing a request may change; everything else is
                       // handed to the client when it is made.
                       new JProperty("settings",     new JObject(
                           new JProperty("timeoutSeconds",        ntsClient.Timeout?.TotalSeconds)
                       )),

                       new JProperty("cookies",      new JObject(
                           new JProperty("available",             pool.AvailableCookieCount),
                           new JProperty("maxPoolSize",           pool.MaxCookiePoolSize),
                           new JProperty("lowWatermark",          pool.LowWatermark),
                           new JProperty("seeded",                pool.SeededCookieCount),
                           new JProperty("received",              pool.CookiesReceived),
                           new JProperty("consumed",              pool.CookiesConsumed),
                           new JProperty("dropped",               pool.DroppedCookieCount),
                           new JProperty("isLow",                 pool.IsLow),
                           new JProperty("isEmpty",               pool.IsEmpty),
                           new JProperty("isFull",                pool.IsFull)
                       )),

                       new JProperty("policy",       new JObject(
                           new JProperty("targetCookieCount",             ntsClient.CookiePoolPolicy.TargetCookieCount),
                           new JProperty("maxPlaceholders",               ntsClient.CookiePoolPolicy.MaxPlaceholders),
                           new JProperty("renegotiateWhenExhausted",      ntsClient.CookiePoolPolicy.RenegotiateWhenExhausted),
                           new JProperty("minimumRenegotiationInterval",  ntsClient.CookiePoolPolicy.MinimumRenegotiationInterval.ToString())
                       )),

                       new JProperty("keyExchange",  new JObject(
                           new JProperty("automatic",                 ntsClient.AutomaticKeyExchanges),
                           new JProperty("aeadAlgorithms",            new JArray(ntsClient.OfferedAEADAlgorithms.Select(algorithm => algorithm.ToString()))),
                           new JProperty("compliantExporterContext",  ntsClient.CompliantAES128GCMSIVExporterContext),
                           new JProperty("lastExchange",              last is null
                                                                          ? null
                                                                          : new JObject(
                                                                                new JProperty("error",     last.ErrorMessage),
                                                                                new JProperty("warnings",  new JArray(last.WarningMessages)),
                                                                                new JProperty("servers",   new JArray(last.NTPv4ServerNames))
                                                                            ))
                       ))

                   );

        }

        #endregion

        #region TryUpdateNTSConfiguration(JSON, out Error)

        /// <summary>
        /// Change what may be changed about the time client, which is its
        /// timeout and nothing else: the server, the ports and the cookie pool
        /// policy are all handed to it when it is made.
        /// </summary>
        public Boolean TryUpdateNTSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (!JSON.TryGetValue("timeoutSeconds", out var token))
                return true;

            if (token.Type == JTokenType.Null)
            {

                if (ntsClient.Timeout is not null)
                {
                    ntsClient.Timeout = null;
                    Log.Notice("NTS configuration changed: waiting for an answer without a timeout.", "nts", "config");
                }

                return true;

            }

            if (token.Type is not (JTokenType.Integer or JTokenType.Float))
            {
                Error = "'timeoutSeconds' must be a number of seconds, or null for none.";
                return false;
            }

            var seconds = token.Value<Double>();

            // An hour is not a timeout any more, and zero is not one either.
            if (seconds <= 0 || seconds > 3600)
            {
                Error = "'timeoutSeconds' must be more than 0 and at most 3600.";
                return false;
            }

            var timeout = TimeSpan.FromSeconds(seconds);

            if (ntsClient.Timeout != timeout)
            {
                ntsClient.Timeout = timeout;
                Log.Notice($"NTS configuration changed: timeout = {timeout}.", "nts", "config");
            }

            return true;

        }

        #endregion

        #endregion


        #region EVSEs

        #region EVSEConfigurationJSON()

        /// <summary>
        /// The EVSEs of this charging station: what it was started with, what
        /// the file says now, and what may be plugged into one.
        /// </summary>
        public JObject EVSEConfigurationJSON()
        {

            // What is on disk may already differ from what the station is
            // running, because the OCPP nodes were built at the start. The page
            // shows both rather than pretending they are the same.
            var saved = EVSEFile.TryLoad(out var fromFile, out _) && fromFile is not null
                            ? fromFile
                            : EVSEs;

            return new JObject(

                       new JProperty("evses",            new JArray(saved.Select(evse => evse.ToJSON()))),

                       new JProperty("running",          new JArray(EVSEs.Select(evse => evse.ToJSON()))),

                       // True while somebody has saved something the OCPP nodes
                       // have not been rebuilt for - which is every save, since
                       // they are built once at the start.
                       new JProperty("restartRequired",  !SameEVSEs(saved, EVSEs)),

                       new JProperty("file",             EVSEFile.Path),
                       new JProperty("maxEVSEs",         EVSEConfig.MaxEVSEs),
                       new JProperty("maxPower_kW",      EVSEConfig.MaxPowerLimit_kW),

                       // The whole vocabulary, so that the page can offer it
                       // instead of asking somebody to spell "cUltraChaoJi".
                       new JProperty("connectorTypes",   new JArray(EVSEConfig.KnownConnectorTypes))

                   );

        }

        #endregion

        #region TryUpdateEVSEConfiguration(JSON, out Error)

        /// <summary>
        /// Replace the EVSEs of this charging station, all of them at once.
        /// </summary>
        /// <remarks>
        /// The whole list rather than one EVSE at a time, because they are only
        /// valid together: OCPP numbers them from 1 upwards without gaps, so
        /// removing the third of four is not a change to one EVSE but to two.
        ///
        /// Written to the file and not to the running station: the OCPP nodes
        /// are told how many EVSEs they have when they are built, and telling
        /// them otherwise afterwards would leave a CSMS with a picture of this
        /// station that no longer matches it. The answer says so.
        /// </remarks>
        public Boolean TryUpdateEVSEConfiguration(JObject                           JSON,
                                                  [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (JSON["evses"] is not JArray array)
            {
                Error = "The request must hold an 'evses' array.";
                return false;
            }

            if (!EVSEConfigFile.TryParse(array, out var evses, out Error))
                return false;

            try
            {
                EVSEFile.Save(evses);
            }
            catch (Exception e)
            {
                Error = $"The EVSEs could not be written to '{EVSEFile.Path}': {e.Message}";
                return false;
            }

            Log.Notice(
                $"EVSE configuration saved: {evses.Count} EVSE(s) - {String.Join("; ", evses.Select(evse => evse.ToString()))}." +
                (SameEVSEs(evses, EVSEs) ? "" : " The station has to be restarted for the OCPP nodes to be rebuilt."),
                "evse", "config"
            );

            return true;

        }

        #endregion

        #region (private static) SameEVSEs(A, B)

        /// <summary>
        /// Whether two lists of EVSEs describe the same station.
        /// </summary>
        private static Boolean SameEVSEs(IReadOnlyList<EVSEConfig> A,
                                         IReadOnlyList<EVSEConfig> B)
        {

            if (A.Count != B.Count)
                return false;

            for (var i = 0; i < A.Count; i++)
            {

                // The record's own equality would compare the two connector
                // lists by reference, which is never true for two lists read
                // from different places.
                if (A[i] with { ConnectorTypes = [] } != (B[i] with { ConnectorTypes = [] }) ||
                    !A[i].ConnectorTypes.SequenceEqual(B[i].ConnectorTypes))
                {
                    return false;
                }

            }

            return true;

        }

        #endregion

        #endregion


        #region (private static) TryReadBoolean(...) / TryReadByte(...)

        /// <summary>
        /// An optional boolean: absent leaves the value alone, present and of
        /// the wrong kind is an error rather than a silent nothing.
        /// </summary>
        private static Boolean TryReadBoolean(JObject                           JSON,
                                              String                            Name,
                                              out Boolean?                      Value,
                                              [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Boolean)
            {
                Error = $"'{Name}' must be true or false.";
                return false;
            }

            Value = token.Value<Boolean>();
            return true;

        }


        /// <summary>
        /// An optional count between 0 and 255.
        /// </summary>
        private static Boolean TryReadByte(JObject                           JSON,
                                           String                            Name,
                                           out Byte?                         Value,
                                           [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            if (!JSON.TryGetValue(Name, out var token) || token.Type == JTokenType.Null)
                return true;

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{Name}' must be a whole number.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < 0 || number > Byte.MaxValue)
            {
                Error = $"'{Name}' must be between 0 and {Byte.MaxValue}.";
                return false;
            }

            Value = (Byte) number;
            return true;

        }

        #endregion

    }

}
