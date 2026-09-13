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

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;

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
    /// <see cref="Web.UserRole"/>.
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

            var changed  = new List<String>();

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


        #region EVSEs

        #region EVSEConfigurationJSON()

        /// <summary>
        /// The EVSEs of this charging station, and what may be plugged into one.
        /// </summary>
        public JObject EVSEConfigurationJSON()

            => new (

                   new JProperty("evses",                   new JArray(EVSEs.Select(evse => evse.ToJSON()))),

                   new JProperty("maxEVSEs",                EVSEConfig.MaxEVSEs),
                   new JProperty("maxPower_kW",             EVSEConfig.MaxPowerLimit_kW),
                   new JProperty("maxConnectorTypeLength",  EVSEConfig.MaxConnectorTypeLength),

                   // The vocabulary OCPP 2.1 names itself, so that the page can
                   // offer it. Not a closed list: anything may be typed, and a
                   // connector this station has never heard of is still a
                   // connector somebody can plug a car into.
                   new JProperty("connectorTypes",          new JArray(EVSEConfig.KnownConnectorTypes)),

                   new JProperty("file",                    ConfigFile.Path)

               );

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
        /// The OCPP nodes are rebuilt from the new list, because they are told
        /// what they are made of when they are built and there is no way to
        /// tell them otherwise afterwards. Nothing is connected to a CSMS yet,
        /// so this costs nothing today; the day it does, this is the one place
        /// that has to learn to say goodbye first.
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

            if (!EVSEConfig.TryParseList(array, out var evses, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (SameEVSEs(evses, EVSEs))
                    return true;

                if (!ConfigFile.TryReplaceSection(
                         StationConfiguration.EVSEsSectionName,
                         new JArray(evses.Select(evse => evse.ToJSON())),
                         out Error))
                {
                    return false;
                }

                var previous = EVSEs;

                EVSEs = evses;

                try
                {
                    (cs01, cs02) = BuildOCPPNodes(evses);
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
                    $"EVSE configuration changed: {evses.Count} EVSE(s) - {String.Join("; ", evses)}.",
                    "evse", "config"
                );

                LogCustomConnectorTypes(evses);

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

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
        /// </remarks>
        private (OCPPv1_6.TestChargePointNode, OCPPv2_1.CS.TestChargingStationNode) BuildOCPPNodes(IReadOnlyList<EVSEConfig> EVSEs)
        {

            var chargePoint     = new OCPPv1_6.TestChargePointNode(
                                      ChargeBoxId:               NetworkingNode_Id.Parse("test01"),
                                      Connectors:                [.. EVSEs.Select(evse =>
                                                                    new OCPPv1_6.CP.ConnectorSpec(
                                                                        Availability:        evse.Operative
                                                                                                 ? OCPPv1_6.Availabilities.Operative
                                                                                                 : OCPPv1_6.Availabilities.Inoperative,
                                                                        PhysicalReference:   evse.PhysicalReference,
                                                                        MaxPower:            Watt.FromKW(evse.MaxPower_kW),
                                                                        MaxEnergy:           null,
                                                                        EnergyMeter:         null
                                                                    ))],
                                      Description:               null,
                                      ChargePointVendor:         null,
                                      ChargePointModel:          null,
                                      ChargePointSerialNumber:   null,
                                      ChargeBoxSerialNumber:     null,
                                      FirmwareVersion:           null,
                                      Iccid:                     null,
                                      IMSI:                      null,
                                      UplinkEnergyMeter:         null
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
                                      DNSClient:                      dnsClient
                                  );

            return (chargePoint, chargingStation);

        }

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

    }

}
