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

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.NTS;
using org.GraphDefined.Vanaheimr.Norn.TimeSync;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Making this charging station try something, to find out whether it can:
    /// resolving a name, and asking its time server what time it is.
    /// </summary>
    /// <remarks>
    /// Both of these write to the event log as they go, and not only at the
    /// end. That is the point of them: somebody who cannot reach their time
    /// server wants to know which step failed - the name, the TLS handshake,
    /// the key exchange, the NTP packet - and a single line saying "it did not
    /// work" tells them to go and find out somewhere else.
    ///
    /// Because they are in the log, they are also in the live stream every open
    /// browser is hanging on. So the Logs page of somebody watching shows what
    /// somebody else pressed, which is the right way round for a machine that
    /// several people look after.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// The most record types one test query may ask for at a time.
        /// </summary>
        public const Int32  MaxQueryRecordTypes  = 8;

        /// <summary>
        /// The most answers one test query reports. A zone transfer is not a
        /// test, and a browser is not a place to render one.
        /// </summary>
        public const Int32  MaxQueryAnswers      = 200;

        #endregion


        #region (static) AsAQuestion(Text, out Name, out Says)

        /// <summary>
        /// What somebody typed, turned into something that can actually be
        /// asked.
        /// </summary>
        /// <remarks>
        /// An address is a perfectly reasonable thing to type into a box that
        /// asks for a name, and it is nearly always a reverse lookup that was
        /// meant - "who is 192.168.178.1". Asking for it as written would send
        /// "192.168.178.1" out as a domain name and come back with nothing,
        /// which looks like a broken resolver rather than a misunderstanding.
        ///
        /// So it is turned around here and said out loud: what came back names
        /// the question that was actually asked, because a page that quietly
        /// asked something else is worse than one that refused.
        /// </remarks>
        public static Boolean AsAQuestion(String                           Text,
                                          out String                       Name,
                                          out String?                      Says)
        {

            Name  = Text.Trim();
            Says  = null;

            if (!System.Net.IPAddress.TryParse(Name, out var address))
                return false;

            var bytes = address.GetAddressBytes();

            Name = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6

                       // Each nibble, lowest first, one label each (RFC 3596 section 2.5).
                       // High nibble then low, so that reversing the whole
                       // sequence puts the lowest nibble of the last byte
                       // first - which is the order, and the one that is easy
                       // to write backwards.
                       ? String.Join(".", bytes.SelectMany(one => new[] { one >> 4, one & 0x0F }).
                                                Reverse().
                                                Select(nibble => nibble.ToString("x"))) + ".ip6.arpa"

                       // Each octet, lowest first (RFC 1035 section 3.5).
                       : String.Join(".", bytes.Reverse()) + ".in-addr.arpa";

            Says = $"'{Text.Trim()}' is an address, so it was turned around and '{Name}' was asked for instead.";

            return true;

        }

        #endregion

        #region ResolveAsync(Name, RecordTypes, Server = null, CancellationToken = default)

        /// <summary>
        /// Look a name up, and say what came back.
        /// </summary>
        /// <param name="Name">The name to resolve.</param>
        /// <param name="RecordTypes">What to ask for; A and AAAA when nothing is said.</param>
        /// <param name="CancellationToken">A cancellation token.</param>
        /// <param name="Server">
        /// Which of the configured name servers to ask, by its place in the
        /// list - or null for all of them, the way this station resolves
        /// anything else.
        /// </param>
        public async Task<JObject> ResolveAsync(String                                Name,
                                                IEnumerable<DNSResourceRecordTypes>?  RecordTypes         = null,
                                                Int32?                                Server              = null,
                                                CancellationToken                     CancellationToken   = default)
        {

            var typed        = Name?.Trim() ?? "";
            var turnedAround = AsAQuestion(typed, out var name, out var saying);

            var recordTypes  = RecordTypes?.Distinct().ToArray() is { Length: > 0 } given
                                   ? given
                                   // An address asked about by itself means
                                   // "who is this", and that is a PTR.
                                   : turnedAround
                                         ? [ DNSResourceRecordTypes.PTR ]
                                         : [ DNSResourceRecordTypes.A, DNSResourceRecordTypes.AAAA ];

            var asked        = String.Join(", ", recordTypes);

            if (!DNSEnabled)
            {
                Log.Warning($"DNS test for '{name}' was not run: name resolution is switched off.", "dns", "test");
                return Failed(name, asked, "Name resolution is switched off on this charging station.");
            }

            #region One server, or all of them

            DNSServerConfig? only = null;

            if (Server is Int32 which)
            {

                if (which < 0 || which >= configuredDNSServers.Count)
                    return Failed(name, asked, $"This station has no name server number {which + 1}.");

                only = configuredDNSServers[which];

                if (only.IPAddress is null)
                    return Failed(name, asked,
                                  $"'{only}' is configured by name rather than by address, so it cannot be asked on its own " +
                                   "without first resolving it - which is the thing being tested.");

            }

            #endregion

            if (!DNSServiceName.TryParse(name, out var serviceName, out var problem))
            {
                Log.Warning($"DNS test: \"{name}\" is not a name that can be looked up: {problem}", "dns", "test");
                return Failed(name, asked, $"\"{name}\" is not a name that can be looked up: {problem}");
            }

            Log.Info(
                only is null
                    ? $"DNS test: asking {configuredDNSServers.Count} server(s) for {asked} of '{serviceName}' ..."
                    : $"DNS test: asking {only} alone for {asked} of '{serviceName}' ...",
                "dns", "test"
            );

            var stopwatch = Stopwatch.StartNew();

            try
            {

                // A client of its own when one server is meant, and without a
                // cache: an answer out of the cache says nothing about whether
                // that server would have given it, which is the whole question
                // being asked. Everything else is taken from the station's own
                // client, so what is being tested is this station's settings
                // and not a fresh set of defaults.
                var asking   = only is null
                                   ? dnsClient
                                   : new DNSClient(
                                         only.IPAddress!,
                                         only.Port,
                                         QueryTimeout:   only.QueryTimeout ?? dnsClient.QueryTimeout,
                                         UseQueryCache:  false
                                     );

                var answer   = await asking.Query(
                                         serviceName,
                                         recordTypes,
                                         RecursionDesired:   dnsClient.RecursionDesired,
                                         ForceUpdate:        only is not null,
                                         CancellationToken:  CancellationToken
                                     );

                stopwatch.Stop();

                var records  = answer.Answers.Take(MaxQueryAnswers).ToArray();

                Log.Notice(
                    $"DNS test: '{serviceName}' {asked} -> {answer.ResponseCode} from {answer.Origin}, " +
                    $"{answer.Answers.Count()} answer(s) in {stopwatch.ElapsedMilliseconds} ms" +
                    (records.Length > 0
                         ? $": {String.Join("; ", records.Select(record => record.ToString()))}"
                         : "."),
                    "dns", "test"
                );

                return new JObject(

                           new JProperty("name",          serviceName.ToString()),
                           new JProperty("asked",         only?.ToString()),
                           new JProperty("turnedAround",  saying),
                           new JProperty("recordTypes",   new JArray(recordTypes.Select(recordType => recordType.ToString()))),
                           new JProperty("ok",            answer.ResponseCode == DNSResponseCodes.NoError),
                           new JProperty("responseCode",  answer.ResponseCode.ToString()),
                           new JProperty("server",        answer.Origin.ToString()),
                           new JProperty("runtime_ms",    stopwatch.ElapsedMilliseconds),
                           new JProperty("authoritative", answer.AuthoritativeAnswer),
                           new JProperty("truncated",     answer.IsTruncated),
                           new JProperty("dnssec",        answer.DNSSECStatus?.ToString()),
                           new JProperty("timedOut",      answer.IsTimeout),

                           new JProperty("answers",       new JArray(
                               records.Select(record => new JObject(
                                   new JProperty("name",         record.DomainName.ToString()),
                                   new JProperty("type",         record.Type.ToString()),
                                   new JProperty("timeToLive",   (Int64) record.TimeToLive.TotalSeconds),
                                   new JProperty("value",        record.RText ?? record.ToString())
                               ))
                           )),

                           new JProperty("more",          Math.Max(0, answer.Answers.Count() - records.Length))

                       );

            }
            catch (Exception e)
            {

                stopwatch.Stop();

                Log.Error($"DNS test for '{serviceName}' failed after {stopwatch.ElapsedMilliseconds} ms: {e.Message}", "dns", "test");

                return Failed(serviceName.ToString(), asked, e.Message);

            }


            static JObject Failed(String Name, String RecordTypes, String Error)

                => new (
                       new JProperty("name",         Name),
                       new JProperty("recordTypes",  new JArray(RecordTypes.Split(", "))),
                       new JProperty("ok",           false),
                       new JProperty("error",        Error),
                       new JProperty("answers",      new JArray())
                   );

        }

        #endregion

        #region TryParseRecordTypes(JSON, out RecordTypes, out Error)

        /// <summary>
        /// The record types of a test query, as the web interface names them.
        /// </summary>
        /// <remarks>
        /// By name or by number, because the several hundred that exist are not
        /// all in this enumeration and somebody testing a resolver may well want
        /// one that is not.
        /// </remarks>
        public static Boolean TryParseRecordTypes(JToken?                                                 JSON,
                                                  out IEnumerable<DNSResourceRecordTypes>?                RecordTypes,
                                                  [NotNullWhen(false)] out String?                        Error)
        {

            RecordTypes  = null;
            Error        = null;

            if (JSON is null || JSON.Type == JTokenType.Null)
                return true;

            if (JSON is not JArray array)
            {
                Error = "'recordTypes' must be an array.";
                return false;
            }

            if (array.Count > MaxQueryRecordTypes)
            {
                Error = $"One query may ask for at most {MaxQueryRecordTypes} record types.";
                return false;
            }

            var parsed = new List<DNSResourceRecordTypes>();

            foreach (var token in array)
            {

                var text = token.Value<String>()?.Trim() ?? "";

                if (text.Length == 0)
                    continue;

                if (Enum.TryParse<DNSResourceRecordTypes>(text, ignoreCase: true, out var recordType))
                    parsed.Add(recordType);

                else if (UInt16.TryParse(text, out var number))
                    parsed.Add((DNSResourceRecordTypes) number);

                else
                {
                    Error = $"'{text}' is not a DNS resource record type.";
                    return false;
                }

            }

            RecordTypes = parsed;
            return true;

        }

        #endregion


        #region SyncTimeAsync(CancellationToken = default)

        #region TestTimeServerAsync(Host = null, CancellationToken = default)

        /// <summary>
        /// Ask one time server everything there is to ask, and write down each
        /// answer as it comes.
        /// </summary>
        /// <remarks>
        /// "Sync now" says whether the whole thing worked. This says where it
        /// got to: the name resolved to these addresses, the TCP connection
        /// took this long, the TLS handshake that long, the key exchange
        /// agreed on this algorithm and handed over that many cookies and
        /// named these NTP servers, and the authenticated NTP request went to
        /// this endpoint and came back that far off. A server that fails does
        /// so at one of those, and which one is the whole of what somebody
        /// needs.
        ///
        /// <b>Two kinds of "which server".</b> A name that is not the
        /// configured one is treated as a time server in its own right: its own
        /// key exchange, its own time request. An address is treated as one of
        /// the servers the configured exchange named - the key exchange happens
        /// where it must, with the host that has a certificate, and the
        /// authenticated request is then directed at that address with the
        /// cookies that exchange issued. Which is what RFC 8915 section 4.1.7
        /// describes: the negotiated server is the one "that will accept the
        /// supplied cookies".
        ///
        /// An address cannot have a key exchange of its own - the TLS
        /// certificate has to be checked against a name - and a key exchange
        /// very commonly names addresses, so this is the ordinary case rather
        /// than the awkward one.
        ///
        /// The clock of this station is not stepped by any of it, the same as
        /// "Sync now".
        /// </remarks>
        /// <param name="Host">
        /// Which time server: a name to ask in its own right, an address to ask
        /// among the ones the configured exchange named, or nothing for the
        /// configured server itself.
        /// </param>
        public async Task<JObject> TestTimeServerAsync(String?            Host                = null,
                                                       CancellationToken  CancellationToken   = default)
        {

            var clock  = Stopwatch.StartNew();
            var steps  = new JArray();

            void Step(String Level, String Text)
                => steps.Add(new JObject(
                       new JProperty("at_ms",  clock.ElapsedMilliseconds),
                       new JProperty("level",  Level),
                       new JProperty("text",   Text)
                   ));

            JObject Done(String Where, Boolean OK)
            {
                clock.Stop();
                return new JObject(
                           new JProperty("host",        Where),
                           new JProperty("ok",          OK),
                           new JProperty("runtime_ms",  clock.ElapsedMilliseconds),
                           new JProperty("steps",       steps)
                       );
            }

            #region Which server, and with which settings

            if (!NTSEnabled)
            {
                Step("error", "Time synchronisation is switched off on this charging station, so nothing was asked.");
                return Done(Host ?? "", false);
            }

            var configured  = ntsClient;
            var wanted      = Host?.Trim();
            var host        = configured.Hostname;

            /// Set when the time request is to go somewhere other than the host
            /// the key exchange happens with.
            String? directedAt = null;

            if (!String.IsNullOrEmpty(wanted) &&
                !String.Equals(wanted.TrimEnd('.'), host.ToString().TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            {

                if (System.Net.IPAddress.TryParse(wanted.Trim('[', ']'), out _))
                    // An address cannot have a key exchange of its own: the TLS
                    // certificate is issued for a name. So the exchange stays
                    // with the configured host, and the time request is
                    // directed at this address with the cookies that exchange
                    // issued - which is what a negotiated server is for.
                    directedAt = wanted.Trim('[', ']');

                else if (!DomainName.TryParse(wanted.TrimEnd('.'), out var other, out _))
                {
                    Step("error", $"'{wanted}' is neither a name nor an address that can be asked.");
                    return Done(wanted, false);
                }

                else
                    host = other;

            }

            var where = directedAt ?? $"{host}";

            // A server of the group is asked on its own ports, which need not
            // be those of the client above: the page tests each server from
            // its own row, and a server with a port of its own was asked on
            // the usual one and reported as not answering.
            var entry      = directedAt is null
                                 ? timeSources.Sources.FirstOrDefault(source => source.Hostname.Equals(host))
                                 : null;

            var ntsKEPort  = entry?.NTSKEPort ?? configured.NTSKE_Port;
            var ntpPort    = entry?.NTPPort   ?? configured.NTP_Port;

            Step("info", directedAt is null
                             ? String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                             "Asking {0}: key exchange on port {1}, time on port {2}, {3:0.#} second(s) allowed.",
                                             host, ntsKEPort, ntpPort, configured.Timeout?.TotalSeconds ?? 0)
                             : $"Asking {directedAt} for the time, with cookies from a key exchange with {host} - " +
                                "an address cannot have a key exchange of its own, because the TLS certificate is " +
                                "issued for a name.");

            Log.Info($"NTS test: asking {host} ...", "nts", "test");

            #endregion

            #region Does the name resolve

            if (!System.Net.IPAddress.TryParse(host.ToString().TrimEnd('.'), out _))
            {
                try
                {

                    var lookedUp  = await dnsClient.Query(
                                              DNSServiceName.Parse(host.ToString()),
                                              [ DNSResourceRecordTypes.A, DNSResourceRecordTypes.AAAA ],
                                              CancellationToken: CancellationToken
                                          );

                    // The address and not the whole record. A resource
                    // record writes itself out with its class, its time to
                    // live and the moment it expires, which in a line that is
                    // about "does this name resolve" is four facts nobody
                    // asked for and one they did.
                    var addresses = lookedUp.Answers.Take(8).
                                        Select(record => (record.RText ?? record.ToString()).Split(',')[0].Trim()).
                                        ToArray();

                    Step(addresses.Length > 0 ? "info" : "warning",
                         addresses.Length > 0
                             ? $"'{host}' resolves to {String.Join(", ", addresses)}."
                             : $"'{host}' resolved to nothing ({lookedUp.ResponseCode}).");

                }
                catch (Exception e)
                {
                    Step("warning", $"'{host}' could not be looked up here: {e.Message}. Asking anyway.");
                }
            }

            #endregion

            var asking = new NTSClient(
                             host,
                             NTSKE_Port:    ntsKEPort,
                             NTP_Port:      ntpPort,
                             Timeout:       configured.Timeout,
                             DNSClient:     dnsClient,
                             TimeProvider:  TimeProvider
                         );

            try
            {

                #region The key exchange

                Step("info", "Key exchange over TLS ...");

                var keyExchange = await asking.GetNTSKERecords(CancellationToken: CancellationToken);

                if (keyExchange.Response?.TimingInfo is NTSKE_TimingInfo timing)
                {

                    if (timing.ConnectedIPAddress is not null)
                        Step("info", $"Connected to {timing.ConnectedIPAddress}" +
                                     (timing.ResolvedIPAddresses.Any()
                                          ? $", of {timing.ResolvedIPAddresses.Count()} address(es) that were offered"
                                          : "") + ".");

                    Step("info", "Where the time went: " +
                                 String.Join(", ", new[] {
                                     timing.DNSLookupDuration      is TimeSpan dns  ? $"name {dns.TotalMilliseconds:0} ms"      : null,
                                     timing.TCPConnectDuration     is TimeSpan tcp  ? $"TCP {tcp.TotalMilliseconds:0} ms"       : null,
                                     timing.TLSHandshakeDuration   is TimeSpan tls  ? $"TLS {tls.TotalMilliseconds:0} ms"       : null,
                                     timing.NTSKEProtocolDuration  is TimeSpan ke   ? $"key exchange {ke.TotalMilliseconds:0} ms" : null
                                 }.Where(one => one is not null)) + ".");

                }

                if (!keyExchange.Success || keyExchange.Response is null)
                {
                    Step("error", $"The key exchange failed ({keyExchange.ErrorCategory}): {keyExchange.ErrorMessage}");
                    Log.Warning($"NTS test: the key exchange with {host} failed: {keyExchange.ErrorMessage}", "nts", "ntske", "test");
                    return Done(where, false);
                }

                var response = keyExchange.Response;

                foreach (var warning in response.WarningMessages)
                    Step("warning", $"The key exchange warned: {warning}");

                Step("notice", $"The key exchange succeeded: {response.AEADAlgorithm}, {response.Cookies.Count()} cookie(s).");

                Step("info", response.NTPv4ServerNames.Any()
                                 ? $"It named these NTP servers: {String.Join(", ", response.NTPv4ServerNames)}."
                                 : "It named no NTP server of its own, so the time is asked of this host.");

                if (directedAt is not null &&
                    !response.NTPv4ServerNames.Any(named => String.Equals(named.Trim('[', ']').TrimEnd('.'),
                                                                          directedAt,
                                                                          StringComparison.OrdinalIgnoreCase)))
                {
                    Step("error", $"This exchange did not name {directedAt}, so the cookies it issued were not said to be " +
                                   "accepted there. Nothing was sent: a cookie spent on a server holding different master " +
                                   "keys is wasted, and the refusal it earns is reported against the wrong machine.");
                    return Done(where, false);
                }

                #endregion

                #region The authenticated time request

                Step("info", "Authenticated NTP request ...");

                var query = await asking.QueryTime(NTSKEResponse:      response,
                                                   NTPServer:          directedAt,
                                                   CancellationToken:  CancellationToken);

                if (!query.Success || query.Response is null)
                {
                    Step("error", $"The NTP request to {query.RemoteDescription} failed " +
                                  $"({query.ErrorCategory}): {query.ErrorMessage}");
                    Log.Warning($"NTS test: the NTP request to {host} failed: {query.ErrorMessage}", "nts", "ntp", "test");
                    return Done(where, false);
                }

                Step("info", $"Answered by {query.RemoteDescription}" +
                             (query.Attempts > 1 ? $", after {query.Attempts} attempts" : "") +
                             $"; {query.RemainingCookiesAfterQuery} cookie(s) left" +
                             (query.NewCookieReceived ? ", and a fresh one came back" : "") + ".");

                if (query.StopwatchRoundTripTime is TimeSpan roundTrip)
                    Step("info", String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                               "Round trip {0:0.0} ms.", roundTrip.TotalMilliseconds));

                var offset = query.Response.ClockOffset;

                // Invariant, so that a decimal point stays a point: these
                // sentences are English, and a station in a German locale
                // otherwise wrote "+148,0 ms" in the middle of one.
                Step("notice", offset.HasValue
                                   ? String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                                   "This station's clock is {0:+0.0;-0.0;0} ms off what {1} says.",
                                                   offset.Value.TotalMilliseconds, host)
                                   : $"{host} answered, but said nothing this station could take an offset from.");

                #endregion

                Step("info", "The clock was not stepped: that is a different thing, with meter readings and " +
                             "certificates hanging off it, and not something a test does by surprise.");

                Log.Notice($"NTS test: {host} answered in {clock.ElapsedMilliseconds} ms.", "nts", "test");

                return Done(where, true);

            }
            catch (Exception e)
            {
                Step("error", $"{e.GetType().Name}: {e.Message}");
                Log.Warning($"NTS test: asking {host} failed: {e.Message}", "nts", "test");
                return Done(where, false);
            }

        }

        #endregion

        /// <summary>
        /// Ask this station's group of time servers what the time is.
        /// </summary>
        /// <remarks>
        /// The group and not the single client, because a clock a charge is
        /// billed by should not move on the word of one server. What comes back
        /// is the group's verdict - the median of the servers that answered and
        /// authenticated, how many there were, how far apart they were - and a
        /// line for each server, because a log book records what was asked and
        /// what each one said, not only the conclusion.
        ///
        /// The detailed test beside this is the other question and keeps its
        /// own path: one server, its key exchange, its cookies, its round trip.
        /// A group cannot answer that, having four of each.
        /// </remarks>
        public async Task<JObject> SyncTimeAsync(CancellationToken CancellationToken = default)
        {

            if (!NTSEnabled)
            {
                Log.Warning("Time synchronisation was not run: NTS is switched off on this charging station.", "nts", "test");
                return Failed("NTS is switched off on this charging station.");
            }

            var group      = timeSources;
            var asked      = group.Bands().SelectMany(band => band).Select(source => source.Hostname.ToString()).ToArray();
            var asking     = asked.Select(hostname => hostname.TrimEnd('.')).ToArray();
            var stopwatch  = Stopwatch.StartNew();

            Log.Info($"NTS: asking the {asked.Length} time server(s) of group '{group.Name}' ...", "nts", "test");

            try
            {

                var verdict = await group.Measure(timeEngine, dnsClient, CancellationToken);

                stopwatch.Stop();

                #region What the group concluded, and what each server said

                var servers = new JArray(
                                  verdict.Results.Select(result => new JObject(
                                      new JProperty("hostname",       result.ServerHostname.ToString()),
                                      new JProperty("ok",             TimeSyncVerdict.CanBeTrusted(result)),
                                      new JProperty("offset_ms",      result.NTP?.Offset.TotalMilliseconds),
                                      new JProperty("roundTrip_ms",   result.NTP?.RoundTripDelay.TotalMilliseconds),
                                      new JProperty("authenticated",  result.NTP?.NTSAuthenticationValid),
                                      new JProperty("keyExchange",    result.NTSKEFromCache ? "reused" : "new"),
                                      new JProperty("error",          result.ErrorMessage?.ToString())
                                  ))
                              );

                var groupJSON = new JObject(
                                    new JProperty("name",               group.Name),
                                    new JProperty("answered",           verdict.Answered),
                                    new JProperty("required",           verdict.Required),
                                    new JProperty("offset_ms",          verdict.Offset?.TotalMilliseconds),
                                    new JProperty("spread_ms",          verdict.Spread?.TotalMilliseconds),
                                    new JProperty("deviationExceeded",  verdict.DeviationExceeded)
                                );

                #endregion

                if (!verdict.IsUsable)
                {

                    Log.Error($"NTS: group '{group.Name}' produced no time after {stopwatch.ElapsedMilliseconds} ms: {verdict}.", "nts", "test");

                    return Remember(Failed(
                               verdict.Outcome == TimeSyncOutcome.NothingAnswered
                                   ? "No time server answered."
                                   : $"Only {verdict.Answered} of {verdict.Required} time server(s) answered.",
                               new JProperty("runtime_ms",  stopwatch.ElapsedMilliseconds),
                               new JProperty("group",       groupJSON),
                               new JProperty("servers",     servers)
                           ));

                }

                // What the asking was actually for. The clock of this station
                // is not stepped by it - see the remarks on this method - so
                // the offset is the whole of the result: it is the difference
                // between what this station believes and what servers that know
                // were saying at the same moment.
                lastTimeCheck          = TimeProvider.GetUtcNow();
                lastTimeCheckOffset    = verdict.Offset;
                lastTimeCheckAsked     = asked.Length;
                lastTimeCheckAnswered  = verdict.Answered;

                // A name only where naming one is the truth. Four servers
                // answering is not "checked against ptbtime1", and picking one
                // of them to print would be the nicer-looking lie.
                lastTimeCheckServer    = asked.Length == 1
                                             ? asked[0]
                                             : null;

                // Written down rather than acted on, which is what the white
                // paper asks for: the disagreement belongs in the metrological
                // log book, and the time is still a time.
                //
                // Invariant, like the lines of the test above, and the agreed
                // deviation with as many places as it has: it may be set as low
                // as a millisecond, and a whole-second format wrote that as
                // "0 s".
                if (verdict.DeviationExceeded)
                    Log.Warning(
                        String.Format(System.Globalization.CultureInfo.InvariantCulture,
                                      "NTS: the time servers of group '{0}' disagree by {1:F1} ms, " +
                                      "which reaches the agreed deviation of {2:0.###} s.",
                                      group.Name,
                                      verdict.Spread!.Value.TotalMilliseconds,
                                      group.MaxDeviation.TotalSeconds),
                        "nts", "test"
                    );

                Log.Notice($"NTS: group '{group.Name}' answered in {stopwatch.ElapsedMilliseconds} ms - {verdict}.", "nts", "test");

                return Remember(new JObject(
                           new JProperty("ok",          true),
                           new JProperty("server",      $"{group.Name}: {String.Join(", ", asking)}"),
                           new JProperty("at",          TimeProvider.GetUtcNow().ToString("o")),
                           new JProperty("runtime_ms",  stopwatch.ElapsedMilliseconds),
                           new JProperty("offset_ms",   verdict.Offset?.TotalMilliseconds),
                           new JProperty("group",       groupJSON),
                           new JProperty("servers",     servers)
                       ));

            }
            catch (Exception e)
            {

                stopwatch.Stop();
                Log.Error($"NTS: asking group '{group.Name}' failed after {stopwatch.ElapsedMilliseconds} ms: {e.Message}", "nts", "test");
                return Remember(Failed(e.Message));
            }


            JObject Failed(String Error, params JProperty[] More)
            {

                var json = new JObject(
                               new JProperty("ok",      false),
                               new JProperty("server",  timeSources.Name),
                               new JProperty("at",      TimeProvider.GetUtcNow().ToString("o")),
                               new JProperty("error",   Error)
                           );

                foreach (var property in More)
                    json.Add(property);

                return json;

            }

            JObject Remember(JObject Result)
            {
                lastTimeSync = Result;
                return Result;
            }

        }

        #endregion

    }

}
