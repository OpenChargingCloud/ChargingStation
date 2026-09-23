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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.ISO15118.T1S;
using cloud.charging.open.protocols.ISO15118.T1S.Monitoring;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.ISO15118;
using cloud.charging.open.ChargingStation.RFID;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the Configuration pages of the web interface read and write.
    /// </summary>
    /// <remarks>
    /// Every change here takes effect at once and is written to the
    /// configuration file, in that order of importance and in the opposite
    /// order of doing: the file is written first, because a change that was
    /// applied but not written down is a change that disappears at the next
    /// start without anybody noticing, and that is the worse of the two
    /// failures. A file that was written but could not be applied is the lesser
    /// one - it says so loudly, and a restart makes it true.
    ///
    /// Nothing here decides who may call it. That is the API's business, and it
    /// asks before it calls: see the permissions in
    /// <see cref="Web.UserRole"/>. The EVSEs are the one place where it cannot
    /// ask beforehand - the same request is a correction or a claim about the
    /// hardware depending on what this station currently has - so there the API
    /// hands in what it may do and is asked back, still without this file
    /// knowing what a permission is.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How the last time synchronisation went, as the web interface reads
        /// it, or null while none has been asked for.
        /// </summary>
        private JObject? lastTimeSync;

        /// <summary>
        /// The record types the DNS test offers, of the several hundred that
        /// exist. Anything else may still be typed - this is the list of what
        /// somebody is likely to want, not of what is allowed.
        /// </summary>
        private static readonly DNSResourceRecordTypes[] commonRecordTypes = [
            DNSResourceRecordTypes.A,
            DNSResourceRecordTypes.AAAA,
            DNSResourceRecordTypes.CNAME,
            DNSResourceRecordTypes.MX,
            DNSResourceRecordTypes.NS,
            DNSResourceRecordTypes.TXT,
            DNSResourceRecordTypes.SOA,
            DNSResourceRecordTypes.SRV,
            DNSResourceRecordTypes.PTR,
            DNSResourceRecordTypes.CAA,
            DNSResourceRecordTypes.TLSA,
            DNSResourceRecordTypes.DNSKEY,
            DNSResourceRecordTypes.DS,
            DNSResourceRecordTypes.HTTPS,
            DNSResourceRecordTypes.SVCB
        ];

        #endregion


        #region DNS

        #region DNSConfigurationJSON()

        /// <summary>
        /// How this station resolves names.
        /// </summary>
        public JObject DNSConfigurationJSON()

            => new (

                   new JProperty("enabled",           DNSEnabled),

                   // The servers this station would ask, which is not the same
                   // as the ones the client holds: switched off, it holds none.
                   new JProperty("servers",           new JArray(
                       configuredDNSServers.Select(DNSConfiguration.ServerJSON)
                   )),

                   new JProperty("settings",          new JObject(
                       new JProperty("queryTimeoutSeconds",  dnsClient.QueryTimeout.TotalSeconds),
                       new JProperty("recursionDesired",     dnsClient.RecursionDesired),
                       new JProperty("useCache",             dnsClient.UseCache),
                       new JProperty("dnssecOK",             dnsClient.DnssecOK),
                       new JProperty("followCNAMEs",         dnsClient.FollowCNAMEs),
                       new JProperty("maxCNAMEFollows",      dnsClient.MaxCNAMEFollows),
                       new JProperty("maxRetries",           dnsClient.MaxRetries)
                   )),

                   // What was decided when the client was made and is not on
                   // offer here; shown so that the page does not read as if
                   // these were the only settings there are.
                   new JProperty("fixed",             new JObject(
                       new JProperty("udpPayloadSize",       dnsClient.UDPPayloadSize),
                       new JProperty("ednsOptions",          dnsClient.EDNSOptions.Count),
                       new JProperty("clientSubnet",         dnsClient.ClientSubnet is null
                                                                 ? null
                                                                 : $"{dnsClient.ClientSubnet.Address}/{dnsClient.ClientSubnet.SourcePrefixLength}"),
                       new JProperty("cacheCleanUpEvery",    dnsClient.DNSCache.CleanUpEvery.    ToString()),
                       new JProperty("negativeCacheTTL",     dnsClient.DNSCache.NegativeCacheTTL.ToString())
                   )),

                   new JProperty("limits",            new JObject(
                       new JProperty("maxServers",           DNSConfiguration.MaxServers),
                       new JProperty("maxQueryTimeout",      DNSConfiguration.MaxQueryTimeoutSeconds),
                       new JProperty("transports",           new JArray(Enum.GetNames<DNSTransport>())),
                       new JProperty("recordTypes",          new JArray(commonRecordTypes.Select(recordType => recordType.ToString())))
                   )),

                   new JProperty("file",              ConfigFile.Path)

               );

        #endregion

        #region TryUpdateDNSConfiguration(JSON, out Error)

        /// <summary>
        /// Change how this station resolves names, at once and for everything
        /// that was handed its DNS client.
        /// </summary>
        /// <remarks>
        /// The request has the same shape as the "dns" section of the
        /// configuration file, on purpose: one vocabulary for the file and for
        /// the web interface means one parser, and nothing that is expressible
        /// in one and not in the other.
        ///
        /// What the request does not mention is not changed and not erased from
        /// the file - a page that only offers the checkboxes may send only the
        /// checkboxes without taking the name servers with it.
        /// </remarks>
        public Boolean TryUpdateDNSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!DNSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            if (configuration.Servers is { Count: 0 })
            {
                Error = "A charging station that resolves no names cannot reach anything. Switch name resolution off instead of emptying the list.";
                return false;
            }

            // Under the same lock as every other change to this station:
            // writing a section is a read, a change and a write of one file,
            // and two browsers saving different sections at the same moment
            // would otherwise leave one of the two changes in neither.
            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(DNSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyDNSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyDNSConfiguration(Configuration)

        /// <summary>
        /// Put a DNS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyDNSConfiguration(DNSConfiguration Configuration)
        {

            var changed = new List<String>();

            if (Configuration.Servers is not null &&
                !configuredDNSServers.SequenceEqual(Configuration.Servers))
            {
                configuredDNSServers = Configuration.Servers;
                changed.Add($"servers = {String.Join(", ", configuredDNSServers)}");
            }

            if (Configuration.Enabled.HasValue && DNSEnabled != Configuration.Enabled.Value)
            {
                DNSEnabled = Configuration.Enabled.Value;
                changed.Add(DNSEnabled ? "switched on" : "switched off");
            }

            // Always, not only when one of the two above changed: the client
            // must end up holding exactly the servers this station means it to
            // hold, and working that out from which halves changed is how the
            // two drift apart.
            dnsClient.SetDNSServers(DNSEnabled ? configuredDNSServers : []);

            if (Configuration.QueryTimeout.HasValue && dnsClient.QueryTimeout != Configuration.QueryTimeout.Value)
            {
                dnsClient.QueryTimeout = Configuration.QueryTimeout.Value;
                changed.Add($"query timeout = {dnsClient.QueryTimeout}");
            }

            if (Configuration.RecursionDesired.HasValue && dnsClient.RecursionDesired != Configuration.RecursionDesired)
            {
                dnsClient.RecursionDesired = Configuration.RecursionDesired;
                changed.Add($"recursion desired = {Configuration.RecursionDesired}");
            }

            if (Configuration.UseCache.HasValue && dnsClient.UseCache != Configuration.UseCache.Value)
            {
                dnsClient.UseCache = Configuration.UseCache.Value;
                changed.Add($"use cache = {dnsClient.UseCache}");
            }

            if (Configuration.DnssecOK.HasValue && dnsClient.DnssecOK != Configuration.DnssecOK.Value)
            {
                dnsClient.DnssecOK = Configuration.DnssecOK.Value;
                changed.Add($"DNSSEC OK = {dnsClient.DnssecOK}");
            }

            if (Configuration.FollowCNAMEs.HasValue && dnsClient.FollowCNAMEs != Configuration.FollowCNAMEs.Value)
            {
                dnsClient.FollowCNAMEs = Configuration.FollowCNAMEs.Value;
                changed.Add($"follow CNAMEs = {dnsClient.FollowCNAMEs}");
            }

            if (Configuration.MaxCNAMEFollows.HasValue && dnsClient.MaxCNAMEFollows != Configuration.MaxCNAMEFollows.Value)
            {
                dnsClient.MaxCNAMEFollows = Configuration.MaxCNAMEFollows.Value;
                changed.Add($"max CNAME follows = {dnsClient.MaxCNAMEFollows}");
            }

            if (Configuration.MaxRetries.HasValue && dnsClient.MaxRetries != Configuration.MaxRetries.Value)
            {
                dnsClient.MaxRetries = Configuration.MaxRetries.Value;
                changed.Add($"max retries = {dnsClient.MaxRetries}");
            }

            if (changed.Count > 0)
                Log.Notice($"DNS configuration changed: {String.Join(", ", changed)}.", "dns", "config");

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

                       new JProperty("enabled",      NTSEnabled),

                       new JProperty("server",       new JObject(
                           new JProperty("hostname",              ntsClient.Hostname.ToString()),
                           new JProperty("ntsKEPort",             ntsClient.NTSKE_Port.ToUInt16()),
                           new JProperty("ntpPort",               ntsClient.NTP_Port.  ToUInt16()),
                           new JProperty("ipVersionPreference",   ntsClient.IPVersionPreference.ToString()),
                           new JProperty("clientId",              ntsClient.Id)
                       )),

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
                       )),

                       // What the group is actually doing, which is what
                       // checks this station's clock. The single client
                       // reported above is the one the detailed test
                       // configures itself from, and its cookie pool is not
                       // what a check spends.
                       new JProperty("timeSources",  new JArray(
                           timeSources.Bands().SelectMany(band => band).Select(source => {

                               var held = timeEngine.KeyExchanges.TryGetValue(source.Hostname, out var state) ? state : null;

                               return new JObject(
                                          new JProperty("hostname",       source.Hostname.ToString()),
                                          new JProperty("priority",       source.Priority),
                                          new JProperty("enabled",        source.Enabled),
                                          new JProperty("cookies",        held?.RemainingCookies),
                                          new JProperty("lastExchange",   held?.LastRefreshed.ToString("o")),
                                          new JProperty("aeadAlgorithm",  held?.NTSKEResponse?.AEADAlgorithm.ToString())
                                      );

                           })
                       )),

                       new JProperty("group",        new JObject(
                           new JProperty("name",                 timeSources.Name),
                           new JProperty("minServers",           timeSources.MinServers),
                           new JProperty("maxDeviationSeconds",  timeSources.MaxDeviation.TotalSeconds)
                       )),

                       new JProperty("lastSync",     lastTimeSync),

                       new JProperty("limits",       new JObject(
                           new JProperty("maxTimeout",  NTSConfiguration.MaxTimeoutSeconds)
                       )),

                       new JProperty("file",         ConfigFile.Path)

                   );

        }

        #endregion

        #region TryUpdateNTSConfiguration(JSON, out Error)

        /// <summary>
        /// Change where this station reads the time.
        /// </summary>
        /// <remarks>
        /// Pointing the station at another server replaces the client rather
        /// than reconfiguring it: the cookies and the keys an NTS client holds
        /// were issued by the host it was made for, and carrying them to a
        /// different one would at best fail and at worst send one server the
        /// key material of another.
        /// </remarks>
        public Boolean TryUpdateNTSConfiguration(JObject                           JSON,
                                                 [NotNullWhen(false)] out String?  Error)
        {

            if (!NTSConfiguration.TryParse(JSON, out var configuration, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (!ConfigFile.TryMergeSection(NTSConfiguration.SectionName, configuration.ToJSON(), out Error))
                    return false;

                ApplyNTSConfiguration(configuration);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) ApplyNTSConfiguration(Configuration)

        /// <summary>
        /// Put an NTS section into effect. What it does not mention is left as
        /// it is.
        /// </summary>
        private void ApplyNTSConfiguration(NTSConfiguration Configuration)
        {

            // Kept whole: what this method does with the client is only half of
            // it, and the other half - how often to check, and what the
            // operator claims about the server - is read from elsewhere and
            // much later. See ChargingStation.Clock.cs.
            ntsSettings = Configuration;

            var changed  = new List<String>();

            #region The group of time servers

            // Only when the section says something about them. That is this
            // method's rule everywhere else, and it earns its place here now
            // that the servers have a default worth keeping: a section
            // mentioning nothing but "enabled" would otherwise quietly reduce
            // four servers to one.
            if (Configuration.Servers is not null ||
                Configuration.Hostname is not null)
            {

                // Rebuilt from the section rather than patched: it is a list, and
                // working out which entry changed in order to report it would say
                // less than naming the servers, which is what happens below.
                var wasAsking  = String.Join(", ", timeSources.Bands().SelectMany(band => band).Select(source => source.Hostname.Trimmed));

                timeSources    = Configuration.ToGroup(Configuration.Hostname ?? ntsClient.Hostname);

                var nowAsking  = String.Join(", ", timeSources.Bands().SelectMany(band => band).Select(source => source.Hostname.Trimmed));

                if (wasAsking != nowAsking)
                    changed.Add($"time servers = {nowAsking}");

            }

            #endregion

            var hostname = Configuration.Hostname  ?? ntsClient.Hostname;
            var ntsKE    = Configuration.NTSKEPort ?? ntsClient.NTSKE_Port;
            var ntp      = Configuration.NTPPort   ?? ntsClient.NTP_Port;

            if (hostname != ntsClient.Hostname ||
                ntsKE    != ntsClient.NTSKE_Port ||
                ntp      != ntsClient.NTP_Port)
            {

                ntsClient = new NTSClient(
                                hostname,
                                NTSKE_Port:    ntsKE,
                                NTP_Port:      ntp,
                                Timeout:       Configuration.Timeout ?? ntsClient.Timeout,
                                DNSClient:     dnsClient,
                                TimeProvider:  TimeProvider
                            );

                // The old client's cookies went with it, so what the page shows
                // about the last exchange belongs to a server this station no
                // longer asks.
                lastTimeSync = null;

                changed.Add($"server = {hostname}:{ntsKE} (NTS-KE), :{ntp} (NTP)");

            }

            else if (Configuration.Timeout.HasValue && ntsClient.Timeout != Configuration.Timeout.Value)
            {
                ntsClient.Timeout = Configuration.Timeout.Value;
                changed.Add($"timeout = {Configuration.Timeout.Value}");
            }

            if (Configuration.Enabled.HasValue && NTSEnabled != Configuration.Enabled.Value)
            {
                NTSEnabled = Configuration.Enabled.Value;
                changed.Add(NTSEnabled ? "switched on" : "switched off");
            }

            if (changed.Count > 0)
                Log.Notice($"NTS configuration changed: {String.Join(", ", changed)}.", "nts", "config");

        }

        #endregion

        #endregion


        #region V2G

        #region V2GConfigurationJSON()

        /// <summary>
        /// What this station offers a vehicle on the wire below the charging
        /// cable: what it was told to offer, and what actually came up.
        /// </summary>
        /// <remarks>
        /// Both, because they are not the same thing and the difference is the
        /// whole point of the page. A station told to answer SDP on a machine
        /// with no IPv6 link-local address comes up with SDP not running, and
        /// a page that only showed the setting would say everything is fine.
        /// </remarks>
        public JObject V2GConfigurationJSON()

            => new (

                   new JProperty("enabled",         V2GOptions.Enabled),
                   new JProperty("sdp",             V2GOptions.SDP),
                   new JProperty("loopback",        V2GOptions.MulticastLoopback),
                   new JProperty("interface",       V2GOptions.InterfaceName),
                   new JProperty("port",            V2GOptions.V2GPort),
                   new JProperty("evseId",          V2GOptions.EVSEId),
                   new JProperty("slac",            V2GOptions.SlacTransport.ToString().ToLowerInvariant()),

                   // Not settable from here on purpose: a certificate is a file
                   // and a password. The page still has to know whether there
                   // is one, because that - and nothing on this page - decides
                   // whether the endpoint speaks TLS and what SDP advertises.
                   new JProperty("certificate",     V2GOptions.ServerCertificate is not null),

                   new JProperty("slacTransports",  new JArray(Enum.GetNames<SlacTransportKind>().
                                                                   Select(name => name.ToLowerInvariant()))),

                   // The bus below a megawatt coupler, as it is configured.
                   // What is actually on it - who joined, what the pins read -
                   // is in "link" below, for the same reason the rest of this
                   // object carries both: a bus that was asked for and did not
                   // come up is the thing worth seeing.
                   new JProperty("t1sTransport",    (V2GOptions.T1S?.Transport ?? T1STransportKind.None).Write()),
                   new JProperty("t1sBus",          V2GOptions.T1S?.Group?.ToString()),
                   new JProperty("t1sInterface",    V2GOptions.T1S?.InterfaceName),
                   new JProperty("t1sName",         V2GOptions.T1S?.Name),
                   new JProperty("t1sCycleMs",      Math.Round((V2GOptions.T1S?.CycleGap ?? T1SConstants.DefaultCycleGap).TotalMilliseconds)),
                   new JProperty("t1sWarningC",     (V2GOptions.T1S?.Thermal ?? CableThermalMonitorOptions.Default).Warning_C),
                   new JProperty("t1sOverloadC",    (V2GOptions.T1S?.Thermal ?? CableThermalMonitorOptions.Default).Overload_C),
                   new JProperty("t1sDefaultBus",   T1SConstants.DefaultMulticastEndpoint.ToString()),
                   new JProperty("t1sTransports",   new JArray(T1STransportKinds.Words)),

                   new JProperty("running",         started),
                   new JProperty("link",            V2G?.ToJSON()),

                   new JProperty("file",            ConfigFile.Path)

               );

        #endregion

        #region UpdateV2GConfiguration(JSON, CancellationToken = default)

        /// <summary>
        /// Change what this station offers below the cable, and put it into
        /// effect at once.
        /// </summary>
        /// <remarks>
        /// Not a TryUpdate like the sections above it, because this one cannot
        /// be done without awaiting: putting it into effect means taking down
        /// a raw socket, a multicast membership and a TCP listener and opening
        /// them again, and pretending that is synchronous would only move the
        /// waiting somewhere less honest.
        ///
        /// The link is always torn down and rebuilt rather than adjusted. Every
        /// setting here - the interface, the port, the EVSE identification SDP
        /// hands out - is read while the sockets are being opened, so there is
        /// no such thing as changing one of them on a running link.
        /// </remarks>
        public async Task<(Boolean Success, String? Error)> UpdateV2GConfiguration(JObject            JSON,
                                                                                   CancellationToken  CancellationToken   = default)
        {

            if (!V2GConfiguration.TryParse(JSON, out var configuration, out var error))
                return (false, error);

            await reconfigureLock.WaitAsync(CancellationToken);

            try
            {

                if (!ConfigFile.TryMergeSection(V2GConfiguration.SectionName, configuration.ToJSON(), out error))
                    return (false, error);

                V2GOptions = configuration.Apply(V2GOptions);

                await RestartV2GLink(CancellationToken);

                Log.Notice($"V2G configuration changed: {configuration}.", "15118", "config");

                return (true, null);

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) RestartV2GLink(CancellationToken)

        /// <summary>
        /// Take the wire below the cable down and bring it up again as it is
        /// now configured.
        /// </summary>
        /// <remarks>
        /// Does nothing at all before Start(), so that a station reconfigured
        /// between construction and start does not open sockets early - the
        /// settings are simply what Start() will then read.
        ///
        /// A vehicle in the middle of a SLAC match or an open V2G session goes
        /// down with the link. That is the honest outcome of changing which
        /// interface a station listens on, and the log says so rather than the
        /// page pretending the change was free.
        /// </remarks>
        private async Task RestartV2GLink(CancellationToken CancellationToken)
        {

            if (!started)
                return;

            if (V2G is not null)
            {

                var sessions = V2G.ActiveSLACSessions;

                if (sessions > 0)
                    Log.Warning(
                        $"The V2G link is being restarted with {sessions} SLAC session(s) open; they end here.",
                        "15118", "config"
                    );

                await V2G.DisposeAsync();
                V2G = null;

            }

            V2G = await V2GLink.TryStart(V2GOptions, Log, CancellationToken);

        }

        #endregion

        #endregion


        #region Power

        #region PowerConfigurationJSON()

        /// <summary>
        /// What this charging station may draw from the grid, and what it
        /// could deliver if nothing held it back.
        /// </summary>
        /// <remarks>
        /// The sum of the EVSEs is sent along because the uplink limit means
        /// nothing without it: it is entirely normal for a station to be able
        /// to deliver more than its connection allows - that is what load
        /// management is for - and the page should be able to say so rather
        /// than look like it found a mistake.
        /// </remarks>
        public JObject PowerConfigurationJSON()

            => new (

                   new JProperty("uplinkPowerLimit_kW",  UplinkPowerLimit_kW),

                   new JProperty("evses",                new JArray(EVSEs.Select(evse => new JObject(
                                                             new JProperty("id",           evse.Id),
                                                             new JProperty("maxPower_kW",  evse.MaxPower_kW)
                                                         )))),

                   new JProperty("evsesTotal_kW",        EVSEs.Sum(evse => evse.MaxPower_kW)),

                   new JProperty("limits",               new JObject(
                                                             new JProperty("maxUplinkPowerLimit_kW",  PowerConfiguration.MaxUplinkPowerLimit_kW),
                                                             new JProperty("maxEVSEPowerLimit_kW",    EVSEConfig.MaxPowerLimit_kW)
                                                         )),

                   new JProperty("file",                 ConfigFile.Path)

               );

        #endregion

        #region DisplayConfigurationJSON() / TryUpdateDisplayConfiguration(JSON, out Error)

        /// <summary>
        /// The quiet hours the display keeps, as the web interface reads them.
        /// </summary>
        public JObject DisplayConfigurationJSON()

            => new (

                   new JProperty("dimFrom",   Display.DimFrom?.ToString("HH\\:mm")),
                   new JProperty("dimUntil",  Display.DimUntil?.ToString("HH\\:mm")),
                   new JProperty("dimTo",     Display.DimTo),

                   // Whether it is one of them right now, so that somebody
                   // setting the hours can see what they have just done without
                   // walking round to the front of the station.
                   new JProperty("quietNow",  Display.IsAQuietHour(TimeProvider.GetUtcNow())),

                   new JProperty("limits",    new JObject(
                                                  new JProperty("darkestDimTo",  DisplayConfiguration.DarkestDimTo),
                                                  new JProperty("defaultDimTo",  DisplayConfiguration.DefaultDimTo)
                                              )),

                   new JProperty("file",      ConfigFile.Path)

               );

        /// <summary>
        /// Change the quiet hours the display keeps.
        /// </summary>
        /// <remarks>
        /// The whole section at once rather than a field at a time, because its
        /// fields are not independent: one end of a window is not a window, and
        /// a level without hours is a number with nothing to apply it to. An
        /// empty object is therefore how dimming is turned off - a file with no
        /// opinion, and a display at full brightness around the clock.
        ///
        /// It takes effect at the display's next poll, which is two seconds
        /// away. Nothing has to be restarted and nothing has to be told: the
        /// answer the display reads is worked out from this whenever it asks.
        /// </remarks>
        public Boolean TryUpdateDisplayConfiguration(JObject                           JSON,
                                                     [NotNullWhen(false)] out String?  Error)
        {

            if (!DisplayConfiguration.TryParse(JSON, out var wanted, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (wanted == Display)
                    return true;

                if (!ConfigFile.TryReplaceSection(
                         DisplayConfiguration.SectionName,
                         wanted.ToJSON(),
                         out Error))
                {
                    return false;
                }

                Display = wanted;

                Log.Notice(
                    wanted.DimsAtNight
                        ? $"The display is now {wanted}."
                        : "The display is now at full brightness around the clock.",
                    "kiosk", "config"
                );

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region TryUpdatePowerConfiguration(JSON, out Error)

        /// <summary>
        /// Change what this station may draw from the grid.
        /// </summary>
        /// <remarks>
        /// A field that is absent leaves the limit alone; a field that is
        /// explicitly null takes it away. Taking it away writes a section with
        /// nothing in it, which is a file with no opinion - so a limit handed
        /// to the constructor would speak again at the next start. That is the
        /// same precedence this whole file follows, and it is why clearing a
        /// limit says so in the log.
        /// </remarks>
        public Boolean TryUpdatePowerConfiguration(JObject                           JSON,
                                                   [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (!JSON.ContainsKey("uplinkPowerLimit_kW"))
                return true;

            if (!PowerConfiguration.TryParsePowerLimit(JSON,
                                                       "uplinkPowerLimit_kW",
                                                       null,
                                                       out var uplink,
                                                       out Error))
            {
                return false;
            }

            reconfigureLock.Wait();

            try
            {

                if (uplink == UplinkPowerLimit_kW)
                    return true;

                var configuration = new PowerConfiguration(uplink);

                if (!ConfigFile.TryReplaceSection(
                         PowerConfiguration.SectionName,
                         configuration.ToJSON(),
                         out Error))
                {
                    return false;
                }

                var previous = UplinkPowerLimit_kW;

                UplinkPowerLimit_kW = uplink;

                Log.Notice(
                    uplink.HasValue
                        ? $"The grid connection limit is now {uplink.Value} kW" +
                          (previous.HasValue ? $", and was {previous.Value} kW." : ", and was not configured before.")
                        : $"The grid connection limit of {previous!.Value} kW was taken away; this station no longer knows what it may draw.",
                    "power", "config"
                );

                LogPowerLimits();

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) LogPowerLimits()

        /// <summary>
        /// Say what this station may draw and what it could deliver, and how
        /// the two compare.
        /// </summary>
        /// <remarks>
        /// Not a warning either way. A station that could deliver more than its
        /// connection allows is the ordinary case and needs load management; a
        /// station whose connection is larger than everything behind it has
        /// room for another EVSE. Both are worth one line and neither is worth
        /// an alarm.
        /// </remarks>
        private void LogPowerLimits()
        {

            var evsesTotal = EVSEs.Sum(evse => evse.MaxPower_kW);

            if (!UplinkPowerLimit_kW.HasValue)
            {
                Log.Info(
                    $"No grid connection limit is configured; the {EVSEs.Count} EVSE(s) could draw {evsesTotal} kW together.",
                    "power", "config"
                );
                return;
            }

            var uplink = UplinkPowerLimit_kW.Value;

            Log.Info(
                $"Grid connection: up to {uplink} kW. The {EVSEs.Count} EVSE(s) could draw {evsesTotal} kW together, " +
                (evsesTotal > uplink
                     ? $"which is {evsesTotal - uplink} kW more than the connection allows - charging them all at once needs load management."
                     : $"which the connection covers."),
                "power", "config"
            );

        }

        #endregion

        #endregion

        #region Calibration

        #region CalibrationConfigurationJSON()

        /// <summary>
        /// The calibration certificates this station runs under, with what was
        /// read out of each of them.
        /// </summary>
        public JObject CalibrationConfigurationJSON()
        {

            var now = TimeProvider.GetUtcNow();

            return new JObject(

                       new JProperty("certificates",  new JArray(CalibrationCertificates.Select(certificate => certificate.ToJSON(now)))),

                       new JProperty("limits",        new JObject(
                                                          new JProperty("maxCertificates",       CalibrationCertificate.MaxCertificates),
                                                          new JProperty("maxIdLength",           CalibrationCertificate.MaxIdLength),
                                                          new JProperty("maxDescriptionLength",  CalibrationCertificate.MaxDescriptionLength),
                                                          new JProperty("maxPEMLength",          CalibrationCertificate.MaxPEMLength),
                                                          new JProperty("expiryWarningDays",     (Int32) CalibrationCertificate.ExpiryWarningTime.TotalDays)
                                                      )),

                       new JProperty("file",          ConfigFile.Path)

                   );

        }

        #endregion

        #region TryUpdateCalibrationConfiguration(JSON, out Error)

        /// <summary>
        /// Replace the calibration certificates of this station, all of them at
        /// once.
        /// </summary>
        /// <remarks>
        /// The whole list rather than one at a time, for the same reason as the
        /// EVSEs: what this station is certified for is one statement, and a
        /// certificate added and one removed in the same breath is one change
        /// to it rather than two.
        ///
        /// A certificate that has already run out is written anyway and said so
        /// about, rather than refused. The station that is not allowed to hold
        /// its own expired certificate is the station that cannot show what it
        /// was running under last month.
        /// </remarks>
        public Boolean TryUpdateCalibrationConfiguration(JObject                           JSON,
                                                         [NotNullWhen(false)] out String?  Error)
        {

            Error = null;

            if (JSON["certificates"] is not JArray array)
            {
                Error = "The request must hold a 'certificates' array.";
                return false;
            }

            if (!CalibrationCertificate.TryParseList(array, out var certificates, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (certificates.Count           == CalibrationCertificates.Count &&
                    certificates.Zip(CalibrationCertificates).All(pair => pair.First == pair.Second))
                {
                    return true;
                }

                if (!ConfigFile.TryReplaceSection(
                         StationConfiguration.CalibrationSectionName,
                         new JArray(certificates.Select(certificate => certificate.ToJSON())),
                         out Error))
                {
                    return false;
                }

                var previous = CalibrationCertificates;

                CalibrationCertificates = certificates;

                foreach (var gone in previous.Where(old => !certificates.Any(now => now.ThumbprintSHA256 == old.ThumbprintSHA256)))
                    Log.Notice($"Calibration certificate taken off this station: {gone}.", "calibration", "config");

                foreach (var added in certificates.Where(now => !previous.Any(old => old.ThumbprintSHA256 == now.ThumbprintSHA256)))
                    Log.Notice($"Calibration certificate put on this station: {added}.", "calibration", "config");

                LogCalibrationCertificates(certificates);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) LogCalibrationCertificates(Certificates)

        /// <summary>
        /// Say what this station is certified for, and say it louder when one
        /// of the certificates is about to stop being true.
        /// </summary>
        /// <remarks>
        /// A calibration certificate running out does not stop a station from
        /// charging. It stops what it charged from being billable, which is
        /// noticed a month later by somebody who was not there - so the station
        /// says so while there is still time to do something about it.
        /// </remarks>
        private void LogCalibrationCertificates(IReadOnlyList<CalibrationCertificate> Certificates)
        {

            var now = TimeProvider.GetUtcNow();

            if (Certificates.Count == 0)
            {
                Log.Info("No calibration certificates are configured.", "calibration", "config");
                return;
            }

            Log.Info(
                $"{Certificates.Count} calibration certificate(s): {String.Join("; ", Certificates)}.",
                "calibration", "config"
            );

            foreach (var certificate in Certificates)
            {

                if (certificate.IsExpired(now))
                    Log.Warning(
                        $"The calibration certificate '{certificate.Id}' ran out on {certificate.NotAfter:yyyy-MM-dd}. " +
                        $"This station keeps it - what it was running under is worth knowing - but it no longer covers anything.",
                        "calibration", "config"
                    );

                else if (certificate.IsNotYetValid(now))
                    Log.Warning(
                        $"The calibration certificate '{certificate.Id}' is not valid before {certificate.NotBefore:yyyy-MM-dd}.",
                        "calibration", "config"
                    );

                else if (certificate.RunsOutWithin(now, CalibrationCertificate.ExpiryWarningTime))
                    Log.Warning(
                        $"The calibration certificate '{certificate.Id}' runs out on {certificate.NotAfter:yyyy-MM-dd}, " +
                        $"in {(Int32) Math.Floor((certificate.NotAfter - now).TotalDays)} day(s).",
                        "calibration", "config"
                    );

            }

        }

        #endregion

        #endregion

        #region EVSEs

        #region EVSEConfigurationJSON()

        /// <summary>
        /// The EVSEs of this charging station, and what may be plugged into one.
        /// </summary>
        public JObject EVSEConfigurationJSON()

            => new (

                   new JProperty("evses",                   new JArray(EVSEs.Select(evse => evse.ToJSON()))),

                   new JProperty("maxEVSEs",                EVSEConfig.MaxEVSEs),
                   new JProperty("maxConnectors",           ConnectorConfig.MaxConnectors),
                   new JProperty("maxPower_kW",             EVSEConfig.MaxPowerLimit_kW),
                   new JProperty("maxConnectorTypeLength",  EVSEConfig.MaxConnectorTypeLength),
                   new JProperty("uplinkPowerLimit_kW",     UplinkPowerLimit_kW),

                   // The vocabulary OCPP 2.1 names itself, so that the page can
                   // offer it. Not a closed list: anything may be typed, and a
                   // connector this station has never heard of is still a
                   // connector somebody can plug a car into.
                   new JProperty("connectorTypes",          new JArray(EVSEConfig.KnownConnectorTypes)),

                   new JProperty("file",                    ConfigFile.Path)

               );

        #endregion

        #region ClassifyEVSEChange(EVSEs)

        /// <summary>
        /// What kind of change the given list would be to the one this station
        /// is running with.
        /// </summary>
        public EVSEChange ClassifyEVSEChange(IReadOnlyList<EVSEConfig> EVSEs)
        {

            // A different number of EVSEs is a different station, and there is
            // nothing to compare switch by switch: the lists do not line up.
            if (EVSEs.Count != this.EVSEs.Count)
                return EVSEChange.Hardware;

            var change = EVSEChange.None;

            if (!EVSEConfig.SameHardware    (EVSEs, this.EVSEs))  change |= EVSEChange.Hardware;
            if (!EVSEConfig.SameAvailability(EVSEs, this.EVSEs))  change |= EVSEChange.Availability;
            if (!EVSEConfig.SamePowerLimits (EVSEs, this.EVSEs))  change |= EVSEChange.PowerLimits;

            return change;

        }

        #endregion

        #region TryUpdateEVSEConfiguration(JSON, IsAllowed, out Change, out Error, out Forbidden)

        /// <summary>
        /// Replace the EVSEs of this charging station, all of them at once.
        /// </summary>
        /// <remarks>
        /// The whole list rather than one EVSE at a time, because they are only
        /// valid together: OCPP numbers them from 1 upwards without gaps, so
        /// removing the third of four is not a change to one EVSE but to two.
        ///
        /// Because the whole list is sent, what is being asked for can only be
        /// seen by comparing it with what this station has - somebody who
        /// changed one number sends the same document as somebody who invented
        /// a socket. So the caller does not say what it wants to do; it hands
        /// in what it may do, and is asked once the difference is known. The
        /// question is asked under the same lock that then applies the change,
        /// so nothing can slip in between being allowed and being done.
        ///
        /// The OCPP nodes are rebuilt from the new list, because they are told
        /// what they are made of when they are built and there is no way to
        /// tell them otherwise afterwards. Nothing is connected to a CSMS yet,
        /// so this costs nothing today; the day it does, this is the one place
        /// that has to learn to say goodbye first.
        /// </remarks>
        /// <param name="JSON">The new list.</param>
        /// <param name="IsAllowed">Asked with the kind of change this turns out to be.</param>
        /// <param name="Change">What kind of change it was.</param>
        /// <param name="Error">What is wrong with it, or what stood in the way.</param>
        /// <param name="Forbidden">Whether the answer to <paramref name="IsAllowed"/> was no.</param>
        public Boolean TryUpdateEVSEConfiguration(JObject                           JSON,
                                                  Func<EVSEChange, Boolean>         IsAllowed,
                                                  out EVSEChange                    Change,
                                                  [NotNullWhen(false)] out String?  Error,
                                                  out Boolean                       Forbidden)
        {

            Change     = EVSEChange.None;
            Error      = null;
            Forbidden  = false;

            if (JSON["evses"] is not JArray array)
            {
                Error = "The request must hold an 'evses' array.";
                return false;
            }

            if (!EVSEConfig.TryParseList(array, out var evses, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                Change = ClassifyEVSEChange(evses);

                if (Change == EVSEChange.None)
                    return true;

                if (!IsAllowed(Change))
                {
                    Forbidden  = true;
                    Error      = $"This changes the {Describe(Change)} of this station.";
                    return false;
                }

                if (!ConfigFile.TryReplaceSection(
                         StationConfiguration.EVSEsSectionName,
                         new JArray(evses.Select(evse => evse.ToJSON())),
                         out Error))
                {
                    return false;
                }

                var previous     = EVSEs;

                // Captured before the nodes are thrown away. Everything the old
                // node knew that is not in the configuration file lives in it,
                // and the new one starts knowing nothing.
                var wasReserved  = cs02.Reservations.ToArray();
                var wasSaying    = cs02.DisplayMessages.ToArray();
                var wasCharging  = sessions.Keys.ToArray();

                EVSEs = evses;

                try
                {

                    (cs01, cs02) = BuildOCPPNodes(evses);

                    CarryOverToNewNode(wasCharging, wasReserved, wasSaying, Change);

                }
                catch (Exception e)
                {

                    // The file is right and this station is not, which is the
                    // one way round that a restart repairs. Saying so is the
                    // whole of what can be done here: putting the old nodes
                    // back would leave a station that disagrees with its own
                    // configuration file and says nothing about it.
                    Log.Critical(
                        $"The EVSEs were saved, but the OCPP nodes could not be rebuilt from them: {e.Message} " +
                        $"This station is still running with its previous {previous.Count} EVSE(s); restart it.",
                        "evse", "ocpp", "config"
                    );

                    EVSEs  = previous;
                    Error  = $"The EVSEs were written to '{ConfigFile.Path}', but this station could not be rebuilt from them: {e.Message} Restart it to pick them up.";
                    return false;

                }

                Log.Notice(
                    Change.HasFlag(EVSEChange.Hardware)
                        ? $"EVSE configuration changed: {evses.Count} EVSE(s) - {String.Join("; ", evses)}."
                        : $"EVSE {Describe(Change)} changed: {String.Join("; ", evses)}.",
                    "evse", "config"
                );

                LogCustomConnectorTypes(evses);
                LogPowerLimits();

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private static) Describe(Change)

        /// <summary>
        /// What a change to the EVSEs amounts to, in the words a refusal and a
        /// log line both need.
        /// </summary>
        private static String Describe(EVSEChange Change)
        {

            var parts = new List<String>();

            if (Change.HasFlag(EVSEChange.Hardware))      parts.Add("equipment");
            if (Change.HasFlag(EVSEChange.PowerLimits))   parts.Add("power limits");
            if (Change.HasFlag(EVSEChange.Availability))  parts.Add("availability");

            return parts.Count switch {
                       0  => "nothing",
                       1  => parts[0],
                       _  => String.Join(" and ", [ String.Join(", ", parts.SkipLast(1)), parts[^1] ])
                   };

        }

        #endregion

        #endregion

        #region RFID

        #region RFIDConfigurationJSON()

        /// <summary>
        /// The card readers this station has, and where they sit.
        /// </summary>
        public JObject RFIDConfigurationJSON()

            => new (

                   new JProperty("readers",      new JArray(RFIDReaders.Select(reader => new JObject(
                                                     new JProperty("id",         reader.Id),
                                                     new JProperty("kind",       reader.Kind),
                                                     new JProperty("evse",       reader.EVSEId.HasValue ? reader.EVSEId.Value : null),
                                                     new JProperty("enabled",    reader.Enabled),
                                                     // Which of them this station can actually do anything
                                                     // with, so that a page can say so rather than leave
                                                     // somebody wondering why nothing happens.
                                                     new JProperty("hasDriver",  reader.HasDriver),
                                                     new JProperty("fake",       reader.IsFake)
                                                 )))),

                   new JProperty("evses",        new JArray(EVSEs.Select(evse => new JObject(
                                                     new JProperty("id",     evse.Id),
                                                     new JProperty("label",  evse.PhysicalReference)
                                                 )))),

                   new JProperty("kinds",        new JArray(RFIDReaderConfig.KnownKinds)),
                   new JProperty("fakeKind",     RFIDReaderConfig.FakeKind),
                   new JProperty("maxReaders",   RFIDReaderConfig.MaxReaders),

                   new JProperty("file",         ConfigFile.Path)

               );

        #endregion

        #region ClassifyRFIDChange(Readers)

        /// <summary>
        /// What kind of change the given list would be to the readers this
        /// station is running with.
        /// </summary>
        public RFIDChange ClassifyRFIDChange(IReadOnlyList<RFIDReaderConfig> Readers)
        {

            if (Readers.Count != RFIDReaders.Count)
                return RFIDChange.Placement;

            var change = RFIDChange.None;

            for (var i = 0; i < Readers.Count; i++)
            {

                if (!Readers[i].SamePlacementAs(RFIDReaders[i]))
                    change |= RFIDChange.Placement;

                if (Readers[i].Enabled != RFIDReaders[i].Enabled)
                    change |= RFIDChange.Availability;

            }

            return change;

        }

        #endregion

        #region TryUpdateRFIDConfiguration(JSON, IsAllowed, out Change, out Error, out Forbidden)

        /// <summary>
        /// Replace the card readers of this station, all of them at once.
        /// </summary>
        /// <remarks>
        /// The same shape and the same reason as the EVSEs: the whole list is
        /// sent, so what is being asked for can only be seen by comparing it
        /// with what this station has, and the caller hands in what it may do
        /// rather than saying what it wants.
        ///
        /// Readers are not the OCPP nodes and nothing is rebuilt from them. A
        /// reader taken away or switched off stops being read at once, which
        /// for the only kind of reader this station has a driver for means the
        /// display stops offering it.
        /// </remarks>
        public Boolean TryUpdateRFIDConfiguration(JObject                           JSON,
                                                  Func<RFIDChange, Boolean>         IsAllowed,
                                                  out RFIDChange                    Change,
                                                  [NotNullWhen(false)] out String?  Error,
                                                  out Boolean                       Forbidden)
        {

            Change     = RFIDChange.None;
            Error      = null;
            Forbidden  = false;

            if (JSON["readers"] is not JArray array)
            {
                Error = "The request must hold a 'readers' array.";
                return false;
            }

            if (!RFIDReaderConfig.TryParseList(array, out var readers, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                // A reader at an EVSE this station does not have is a reader
                // nobody can walk up to. Checked here rather than in the parser
                // because it depends on what this station is made of today.
                foreach (var reader in readers.Where(reader => reader.EVSEId.HasValue))
                    if (!EVSEs.Any(evse => evse.Id == reader.EVSEId))
                    {
                        Error = $"The RFID reader '{reader.Id}' is at EVSE {reader.EVSEId}, and this station has {EVSEs.Count} EVSE(s).";
                        return false;
                    }

                Change = ClassifyRFIDChange(readers);

                if (Change == RFIDChange.None)
                    return true;

                if (!IsAllowed(Change))
                {
                    Forbidden  = true;
                    Error      = Change.HasFlag(RFIDChange.Placement)
                                     ? "This changes which card readers this station has and where they sit."
                                     : "This switches a card reader on or off.";
                    return false;
                }

                if (!ConfigFile.TryReplaceSection(
                         StationConfiguration.RFIDSectionName,
                         new JArray(readers.Select(reader => reader.ToJSON())),
                         out Error))
                {
                    return false;
                }

                RFIDReaders = readers;

                Log.Notice(
                    readers.Count == 0
                        ? "This station now has no RFID readers."
                        : $"RFID readers changed: {String.Join("; ", readers)}.",
                    "rfid", "config"
                );

                LogRFIDReaders(readers);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

        #region (private) LogRFIDReaders(Readers)

        /// <summary>
        /// Say which readers this station has, and which of them it can do
        /// nothing with.
        /// </summary>
        /// <remarks>
        /// A configured reader this station has no driver for is not an error -
        /// describing a station truthfully before the software can talk to all
        /// of it is the right way round - but it is the difference between "the
        /// reader is broken" and "this station was never going to read it", and
        /// only the log can tell somebody which of the two they have.
        /// </remarks>
        private void LogRFIDReaders(IReadOnlyList<RFIDReaderConfig> Readers)
        {

            if (Readers.Count == 0)
            {
                Log.Info("No RFID readers are configured.", "rfid", "config");
                return;
            }

            Log.Info($"{Readers.Count} RFID reader(s): {String.Join("; ", Readers)}.", "rfid", "config");

            foreach (var reader in Readers.Where(reader => reader.Enabled && !reader.HasDriver))
                Log.Warning(
                    $"This station has no driver for the RFID reader '{reader.Id}' of kind '{reader.Kind}'; " +
                    "it is configured, it is shown, and it will read nothing.",
                    "rfid", "config"
                );

            foreach (var reader in Readers.Where(reader => reader.Enabled && reader.IsFake))
                Log.Warning(
                    $"The RFID reader '{reader.Id}' is a {RFIDReaderConfig.FakeKind}: its cards are typed into the display, " +
                    "by anybody standing in front of it. For testing, and not for a station in a car park.",
                    "rfid", "config"
                );

        }

        #endregion

        #region (private) ConfigureWebPayments(Node)

        /// <summary>
        /// Give OCPP 2.1's web payments controllers the same settings the
        /// display works its QR codes out from.
        /// </summary>
        /// <remarks>
        /// The display does not read the URL out of these controllers - it
        /// computes it, see WebPaymentURL - but the two must not be able to
        /// disagree: a station that shows one URL and reports another to a back
        /// end would be a station where the code on the screen is not the code
        /// that was paid for. Same template, same secret, same validity, same
        /// length, set in the one place the nodes are built.
        /// </remarks>
        private void ConfigureWebPayments(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            if (webPayments?.Enabled != true || !webPayments.URLTemplate.HasValue)
                return;

            foreach (var evse in Node.EVSEs)
            {

                var controller = evse.WebPaymentsController;

                if (controller is null)
                    continue;

                controller.Enabled        = true;
                controller.URLTemplate    = webPayments.URLTemplate;
                controller.EnableQRCodes  = true;

                if (webPayments.ValidityTime.HasValue)  controller.ValidityTime  = webPayments.ValidityTime.Value;
                if (webPayments.SharedSecret is not null) controller.SharedSecret = webPayments.SharedSecret;
                if (webPayments.TOTPLength.HasValue)    controller.Length        = webPayments.TOTPLength.Value;

            }

        }

        #endregion

        #region (private) CarryOverToNewNode(WasCharging, WasReserved, Change)

        /// <summary>
        /// Put onto the freshly built OCPP node everything the old one knew
        /// that the configuration file does not hold.
        /// </summary>
        /// <remarks>
        /// A node is told what it is made of when it is built, so changing the
        /// EVSEs means building another one - and the new one starts knowing
        /// nothing. Without this, correcting one cable's limit would quietly
        /// drop every reservation this station had promised and forget that
        /// anybody was charging.
        ///
        /// What survives depends on what changed. A change to the hardware is a
        /// different station: an outlet may have stopped existing or become a
        /// different socket, and a reservation for "EVSE 2" would afterwards be
        /// a promise about something else. Those are let go of, out loud. A
        /// corrected number or a switch is the same station and everything
        /// carries over - except a reservation on an outlet that has just been
        /// taken out of service, which is a promise this station can no longer
        /// keep.
        ///
        /// A session that is already running is not one of those promises: it
        /// is a car on a cable. Taking an outlet out of service stops new
        /// sessions and lets go of what was held for later; it takes effect on
        /// the running one when that one ends, which is what OCPP means when it
        /// answers a ChangeAvailability with Scheduled, and what an operator
        /// means when they tick the box the evening before the work.
        /// </remarks>
        private void CarryOverToNewNode(IReadOnlyList<Byte>                      WasCharging,
                                        IReadOnlyList<OCPPv2_1.CS.Reservation>   WasReserved,
                                        IReadOnlyList<OCPPv2_1.MessageInfo>      WasSaying,
                                        EVSEChange                               Change)
        {

            #region Who is charging

            if (Change.HasFlag(EVSEChange.Hardware))
                StopAllSessions("the EVSEs of this station were changed");

            else
                foreach (var evseId in WasCharging)
                {

                    var evse = EVSEs.FirstOrDefault(candidate => candidate.Id == evseId);

                    if (evse is null)
                    {
                        if (sessions.TryRemove(evseId, out var gone))
                            Log.Notice($"The session at EVSE {evseId} was ended: {gone} - that EVSE is no longer there.", "kiosk");
                        continue;
                    }

                    SetCharging(evseId, true);

                    // Out of service from now on for everybody else, and for
                    // this car when it unplugs. Said out loud because it is the
                    // one thing about the change that does not happen at once.
                    if (!evse.Operative)
                        Log.Notice(
                            $"EVSE {evseId} goes out of service when the session on it ends: {sessions[evseId]}. " +
                            "Taking an EVSE out of service does not stop a car that is already charging.",
                            "kiosk"
                        );

                }

            #endregion

            #region What is held

            foreach (var reservation in WasReserved)
            {

                if (Change.HasFlag(EVSEChange.Hardware))
                {
                    Log.Notice(
                        $"The reservation {reservation.Id} was let go: the EVSEs of this station were changed, " +
                        "and it was a promise about the ones it used to have.",
                        "reservation"
                    );
                    continue;
                }

                if (reservation.EVSEId.HasValue &&
                    !EVSEs.Any(evse => evse.Id == reservation.EVSEId.Value.Value && evse.Operative))
                {
                    Log.Notice(
                        $"The reservation {reservation.Id} at EVSE {reservation.EVSEId.Value.Value} was let go: " +
                        "that EVSE was taken out of service.",
                        "reservation"
                    );
                    continue;
                }

                cs02.Restore(reservation);

            }

            #endregion

            #region What it was saying

            // Unlike a reservation, a message is not a promise to anybody and
            // survives a change to the hardware: "the car park closes at ten"
            // is as true after an outlet was renamed as before. Only one tied
            // to an outlet that has stopped existing has nowhere left to be
            // shown.
            foreach (var message in WasSaying)
            {

                if (message.Display?.EVSE is not null &&
                    !EVSEs.Any(evse => evse.Id == message.Display.EVSE.Id.Value))
                {
                    Log.Notice(
                        $"The display message {message.Id} was dropped: it was for EVSE {message.Display.EVSE.Id.Value}, " +
                        "and this station no longer has one.",
                        "message"
                    );
                    continue;
                }

                cs02.RestoreDisplayMessage(message);

            }

            #endregion

        }

        #endregion

        #region (private) BuildOCPPNodes(EVSEs)

        /// <summary>
        /// The two OCPP nodes that speak for the given EVSEs.
        /// </summary>
        /// <remarks>
        /// The one place that knows how an EVSE of this station is spelled in
        /// each of the two protocols: OCPP 1.6 has connectors and no EVSEs,
        /// OCPP 2.1 has EVSEs. Written once and called from the constructor and
        /// from every later change, so that a station which was reconfigured
        /// and one which was started with the same list are the same station.
        ///
        /// A connector of OCPP 1.6 is a cable, so an EVSE with two of them
        /// becomes two - each with the limit of the cable it stands for, which
        /// is the only place in either protocol where the per-cable limits
        /// currently arrive. OCPP 2.1 is told the shapes of the connectors but
        /// has nowhere in its EVSE specification to put their limits.
        /// </remarks>
        private (OCPPv1_6.TestChargePointNode, OCPPv2_1.CS.TestChargingStationNode) BuildOCPPNodes(IReadOnlyList<EVSEConfig> EVSEs)
        {

            var chargePoint     = new OCPPv1_6.TestChargePointNode(
                                      ChargeBoxId:               NetworkingNode_Id.Parse("test01"),
                                      Connectors:                [.. EVSEs.SelectMany(evse => evse.Connectors.Select(connector =>
                                                                    new OCPPv1_6.CP.ConnectorSpec(
                                                                        Availability:        evse.Operative
                                                                                                 ? OCPPv1_6.Availabilities.Operative
                                                                                                 : OCPPv1_6.Availabilities.Inoperative,
                                                                        PhysicalReference:   PhysicalReferenceOf(evse, connector),
                                                                        MaxPower:            Watt.FromKW(connector.MaxPower_kW),
                                                                        MaxEnergy:           null,
                                                                        EnergyMeter:         null
                                                                    )))],
                                      Description:               null,
                                      ChargePointVendor:         null,
                                      ChargePointModel:          null,
                                      ChargePointSerialNumber:   null,
                                      ChargeBoxSerialNumber:     null,
                                      FirmwareVersion:           null,
                                      Iccid:                     null,
                                      IMSI:                      null,
                                      UplinkEnergyMeter:         null,
                                      Clock:                     TimeProvider
                                  );

            var chargingStation = new OCPPv2_1.CS.TestChargingStationNode(
                                      Id:                             NetworkingNode_Id.Parse("test02"),
                                      VendorName:                     "gef",
                                      Model:                          "cs1",
                                      Description:                    I18NString.Empty,
                                      SerialNumber:                   null,
                                      FirmwareVersion:                null,
                                      Modem:                          null,

                                      EVSEs:                          [.. EVSEs.Select(evse =>
                                                                          new OCPPv2_1.CS.EVSESpec(
                                                                              AdminStatus:         evse.Operative
                                                                                                       ? OCPPv2_1.OperationalStatus.Operative
                                                                                                       : OCPPv2_1.OperationalStatus.Inoperative,
                                                                              ConnectorTypes:      evse.OCPPConnectorTypes,
                                                                              MeterType:           evse.MeterType         ?? "",
                                                                              MeterSerialNumber:   evse.MeterSerialNumber ?? "",
                                                                              MeterPublicKey:      ""
                                                                          ))],
                                      UplinkEnergyMeter:              null,

                                      DefaultRequestTimeout:          null,

                                      SignaturePolicy:                null,
                                      ForwardingSignaturePolicy:      null,

                                      HTTPAPI_Disabled:               true,
                                      HTTPAPI_Port:                   null,
                                      HTTPAPI_ServerName:             null,
                                      HTTPAPI_ServiceName:            null,
                                      HTTPAPI_RobotEMailAddress:      null,
                                      HTTPAPI_RobotGPGPassphrase:     null,
                                      HTTPAPI_EventLoggingDisabled:   true,

                                      WebAPI:                         null,
                                      WebAPI_Disabled:                true,
                                      WebAPI_Path:                    null,

                                      ControlWebSocketServer:         null,

                                      DisableSendHeartbeats:          true,
                                      SendHeartbeatsEvery:            null,

                                      DisableMaintenanceTasks:        true,
                                      MaintenanceEvery:               null,

                                      CustomData:                     null,
                                      DNSClient:                      dnsClient,

                                      // The whole point of the clock the station was handed: its
                                      // OCPP side has to agree with it about when "now" is, or a
                                      // fixture that moves the station finds its own reservations
                                      // already expired and measures that disagreement instead.
                                      Clock:                          TimeProvider
                                  );

            ConfigureWebPayments(chargingStation);

            return (chargePoint, chargingStation);

        }

        #endregion

        #region (private static) PhysicalReferenceOf(EVSE, Connector)

        /// <summary>
        /// What is written on the housing beside one cable.
        /// </summary>
        /// <remarks>
        /// An EVSE with one cable is labelled "A" and its cable is the same
        /// thing, so it keeps the label as it is. An EVSE with two of them has
        /// two things to point at, and "A1" and "A2" is how they are written on
        /// every housing that has them.
        /// </remarks>
        private static String? PhysicalReferenceOf(EVSEConfig       EVSE,
                                                   ConnectorConfig  Connector)

            => EVSE.PhysicalReference is null
                   ? null
                   : EVSE.Connectors.Count == 1
                         ? EVSE.PhysicalReference
                         : $"{EVSE.PhysicalReference}{Connector.Id}";

        #endregion

        #region (private) LogCustomConnectorTypes(EVSEs)

        /// <summary>
        /// Say once, in the log, which connector types are not ones OCPP 2.1
        /// names itself.
        /// </summary>
        /// <remarks>
        /// Not a warning and not a refusal: a new plug is a real thing and this
        /// station should be able to describe one. But a plug nobody has heard
        /// of and a plug somebody mistyped look exactly alike from here, and
        /// only the person who typed it can tell which of the two it was - so
        /// they are shown it.
        /// </remarks>
        private void LogCustomConnectorTypes(IReadOnlyList<EVSEConfig> EVSEs)
        {

            var custom = EVSEs.SelectMany(evse => evse.CustomConnectorTypes).
                               Distinct().
                               Order().
                               ToArray();

            if (custom.Length > 0)
                Log.Info(
                    $"Connector type(s) OCPP 2.1 does not name itself, and which this station will pass on as written: {String.Join(", ", custom)}.",
                    "evse", "ocpp", "config"
                );

        }

        #endregion

        #endregion

    }

}
