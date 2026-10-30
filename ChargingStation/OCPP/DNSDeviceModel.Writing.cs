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
using System.Globalization;
using System.Text.RegularExpressions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// Change how a node resolves names, as WWCPNode.TryUpdateDNSConfiguration
    /// does: what the change does not mention stays as it is.
    /// </summary>
    /// <param name="Change">The change, in the shape of the "dns" section of the configuration file.</param>
    /// <param name="Error">Why nothing was changed.</param>
    /// <param name="NotSaved">True where the configuration file could not be read or written, and nothing was changed.</param>
    public delegate Boolean TryUpdateDNSConfigurationDelegate(JObject                           Change,
                                                              [NotNullWhen(false)] out String?  Error,
                                                              out Boolean                       NotSaved);

    /// <summary>
    /// Resolve names as a node would with a change of its name resolution in
    /// effect, without putting it into effect, as WWCPNode.ResolveWithAsync
    /// does.
    /// </summary>
    /// <param name="Change">The change, in the shape of the "dns" section; an empty one asks the configuration in effect.</param>
    /// <param name="Names">The names to resolve.</param>
    /// <param name="Within">How long all of them may take together.</param>
    /// <param name="CancellationToken">A cancellation token.</param>
    public delegate Task<DNSPreflight> DNSPreflightDelegate(JObject              Change,
                                                            IEnumerable<String>  Names,
                                                            TimeSpan             Within,
                                                            CancellationToken    CancellationToken);


    /// <summary>
    /// How a CSMS changes the name resolution of this station: the writes of a
    /// SetVariables request to DNSClientController and DNSServerController,
    /// put into the node as the web page puts a save.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The node replaces its list of name servers as a whole, so every change
    /// that touches a server sends the whole list, in slot order. Slots are
    /// held in a table of their own for the whole request: the node closes the
    /// gap an emptied slot leaves, and a later entry for the slot behind it
    /// must still mean the server it meant at the start. What each server
    /// was held to is sent along as the request found it ("pinsAsShown"), so
    /// that a pin learned on first use in the meantime is kept.
    /// </para>
    /// <para>
    /// With IndependentRequests every entry is a change of its own, in the
    /// order of the request, except that the entries of a slot unused at the
    /// start are one change, made where its Address stands: a new encrypted
    /// server never starts as a server over UDP. With ACID and BASE all
    /// entries handed over at once are one change, all of it or nothing.
    /// </para>
    /// <para>
    /// Before a change is put into effect, the names of the CSMS connections
    /// are resolved with it (SecureDNS §3.1.3): a name that resolves now and
    /// would not resolve then makes it CSMSUnreachable. A CSMS must not cut
    /// itself off, and nothing would show it before the next connection.
    /// SecureDNS-DeviceModel-Concept.md B.6 and B.9 say all of this at length.
    /// </para>
    /// </remarks>
    public sealed partial class DNSDeviceModel : ISetVariablesSource
    {

        #region Data

        /// <summary>
        /// How long the names of the CSMS connections may take to resolve
        /// before a change is put into effect.
        /// </summary>
        public static readonly TimeSpan PreflightTime = TimeSpan.FromSeconds(10);

        /// <summary>
        /// The most characters of the additionalInfo of a result (OCPP 2.1).
        /// </summary>
        private const Int32 MaxAdditionalInfo = 1024;

        private static readonly String[] writableClientVariables = [
            "Enabled", "QueryTimeout", "MaxRetries", "RecursionDesired", "UseCache", "DNSSECOK", "FollowCNAMEs", "MaxCNAMEFollows"
        ];

        private static readonly String[] writableServerVariables = [
            "Address", "Port", "Transport", "QueryTimeout", "CertificatePins", "RootPins", "OnPinMismatch", "TrustOnFirstUse"
        ];

        /// <summary>
        /// The keys of the "dns" section the variables of DNSClientController
        /// are written under.
        /// </summary>
        private static readonly Dictionary<String, String> clientKeys = new (StringComparer.OrdinalIgnoreCase) {
            ["Enabled"]          = "enabled",
            ["QueryTimeout"]     = "queryTimeoutSeconds",
            ["MaxRetries"]       = "maxRetries",
            ["RecursionDesired"] = "recursionDesired",
            ["UseCache"]         = "useCache",
            ["DNSSECOK"]         = "dnssecOK",
            ["FollowCNAMEs"]     = "followCNAMEs",
            ["MaxCNAMEFollows"]  = "maxCNAMEFollows"
        };

        private static readonly Regex serverInError = new (@"dns\.servers\[(\d*)\]", RegexOptions.CultureInvariant);

        private readonly TryUpdateDNSConfigurationDelegate?  update;
        private readonly DNSPreflightDelegate?               preflight;
        private readonly Func<IEnumerable<String>>?          namesToKeepResolving;
        private readonly Action<String>?                     log;

        /// <summary>
        /// One request at a time: two would each hold a slot table that the
        /// other one's changes make wrong.
        /// </summary>
        private readonly SemaphoreSlim                       oneRequestAtATime = new (1, 1);

        #endregion

        #region Properties

        IEnumerable<String> IDNSControllerSource.WritableClientVariables
            => update is not null ? writableClientVariables : [];

        IEnumerable<String> IDNSControllerSource.WritableServerVariables
            => update is not null ? writableServerVariables : [];

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The Device Model view of the name resolution a node describes, which
        /// a CSMS can change as well.
        /// </summary>
        /// <param name="Section">Where the node says how it resolves names, as WWCPNode.DNSConfigurationJSON does: asked anew at every read and at every request.</param>
        /// <param name="Update">How a change is put into the node, as WWCPNode.TryUpdateDNSConfiguration does it.</param>
        /// <param name="Preflight">How names are resolved with a change before it is put into effect, as WWCPNode.ResolveWithAsync does it.</param>
        /// <param name="NamesToKeepResolving">The names a change must not stop resolving: those the CSMS connections are dialled by.</param>
        /// <param name="Log">Where a change is said before it is made.</param>
        public DNSDeviceModel(Func<JObject>                      Section,
                              TryUpdateDNSConfigurationDelegate  Update,
                              DNSPreflightDelegate               Preflight,
                              Func<IEnumerable<String>>          NamesToKeepResolving,
                              Action<String>?                    Log   = null)

            : this(Section)

        {

            this.update                = Update;
            this.preflight             = Preflight;
            this.namesToKeepResolving  = NamesToKeepResolving;
            this.log                   = Log;

        }

        #endregion


        #region SetVariablesAsync(Entries, DataConsistencyModel, CancellationToken)

        /// <summary>
        /// Put the given writes to the DNS controllers into the node.
        /// </summary>
        /// <param name="Entries">Entries of DNSClientController and DNSServerController, in request order.</param>
        /// <param name="DataConsistencyModel">IndependentRequests, ACID or BASE.</param>
        /// <param name="CancellationToken">A cancellation token.</param>
        public async Task<IReadOnlyList<SetVariableResult>> SetVariablesAsync(IReadOnlyList<SetVariableEntry>  Entries,
                                                                              DataConsistencyModel             DataConsistencyModel,
                                                                              CancellationToken                CancellationToken)
        {

            var results = new SetVariableResult?[Entries.Count];

            if (update is null)
            {
                for (var index = 0; index < Entries.Count; index++)
                    results[index] = Result(Entries[index], SetVariableStatus.Rejected, "ReadOnly", $"'{Where(Entries[index])}' is read-only!");
                return [.. results.Select(result => result!)];
            }

            await oneRequestAtATime.WaitAsync(CancellationToken).ConfigureAwait(false);

            try
            {

                var read   = section();
                var table  = SlotServer.TableOf(read, SlotsIn(read));
                var items  = new List<Item>();

                #region Every entry by itself

                for (var index = 0; index < Entries.Count; index++)
                {

                    var entry  = Entries[index];
                    var slot   = (entry.Component as DNSServerController)?.Slot;
                    var name   = entry.Variable.Name;
                    var value  = entry.Data.AttributeValue ?? "";

                    var writes = (slot is null ? writableClientVariables : writableServerVariables).Contains(name, StringComparer.OrdinalIgnoreCase);

                    var fits   = slot is UInt16 inSlot
                                     ? DNSServerController.TryCheck(inSlot, name, value, out var refusal)
                                     : DNSClientController.TryCheck(        name, value, out refusal);

                    var now    = slot is UInt16 at
                                     ? ServerValueIn(read, at, name)
                                     : ClientValueIn(read,     name);

                    if (!writes)
                        results[index] = Result(entry, SetVariableStatus.Rejected, "ReadOnly", $"'{Where(entry)}' is read-only!");

                    else if (!fits)
                        results[index] = Result(entry, SetVariableStatus.Rejected, refusal!.ReasonCode, refusal.AdditionalInfo);

                    // Compare and set: the CSMS changes nothing it has not seen.
                    else if (entry.Data.OldAttributeValue is String old && old != now)
                        results[index] = Result(entry, SetVariableStatus.Rejected, "ValueChanged", $"'{Where(entry)}' is \"{now}\" now, not \"{old}\"!");

                    else
                        items.Add(new Item(index, entry, slot, name, value));

                }

                // Which of two values is meant cannot be told.
                foreach (var twice in items.GroupBy(item => (item.Slot, Name: item.Name.ToLowerInvariant())).Where(group => group.Count() > 1).ToArray())
                    foreach (var item in twice)
                    {
                        results[item.Index] = Result(item.Entry, SetVariableStatus.Rejected, "InvalidValue", $"'{Where(item.Entry)}' is set more than once in this request!");
                        items.Remove(item);
                    }

                #endregion

                if (DataConsistencyModel == DataConsistencyModel.ACID || DataConsistencyModel == DataConsistencyModel.BASE)
                {

                    // All of it or nothing: what was not refused itself is rolled
                    // back, as the entries refused before anything was tried.
                    if (results.Any(result => result is not null))
                    {
                        foreach (var item in items)
                            results[item.Index] = Result(item.Entry, SetVariableStatus.Rolledback, "Unspecified", "Not set, because another entry of the same change could not be!");
                    }

                    else if (items.Count > 0)
                        await ApplyAsync(items, table, results, true, CancellationToken).ConfigureAwait(false);

                }

                else
                    foreach (var change in IndependentChanges(items, table, results))
                        await ApplyAsync(change, table, results, false, CancellationToken).ConfigureAwait(false);

                return [.. results.Select(result => result!)];

            }
            finally
            {
                oneRequestAtATime.Release();
            }

        }

        #endregion


        #region (private) IndependentChanges(Items, Table, Results)

        /// <summary>
        /// The changes of a request of independent entries: every entry by
        /// itself, except that the entries of a slot unused at the start are
        /// one change, made where its Address stands. Without an Address they
        /// are refused: a slot without a server has nothing to set them on.
        /// </summary>
        private static IEnumerable<IReadOnlyList<Item>> IndependentChanges(IReadOnlyList<Item>     Items,
                                                                          SlotServer?[]           Table,
                                                                          SetVariableResult?[]    Results)
        {

            var addressOf = Items.Where (item => item.Slot is UInt16 slot && Table[slot - 1] is null && item.IsAddress && item.Value.Length > 0).
                                  GroupBy(item => item.Slot!.Value).
                                  ToDictionary(group => group.Key, group => group.First());

            foreach (var item in Items)
            {

                if (item.Slot is not UInt16 slot || Table[slot - 1] is not null)
                {
                    yield return [ item ];
                    continue;
                }

                if (addressOf.TryGetValue(slot, out var address))
                {
                    if (ReferenceEquals(item, address))
                        yield return [.. Items.Where(other => other.Slot == slot)];
                    continue;
                }

                // An unused slot emptied: nothing to do, and nothing wrong.
                if (item.IsAddress)
                    yield return [ item ];

                else
                    Results[item.Index] = Result(item.Entry, SetVariableStatus.Rejected, "InvalidValue",
                                                 $"'{ServerComponent}[{slot}]' is unused: set its Address in the same request!");

            }

        }

        #endregion

        #region (private) ApplyAsync(Change, Table, Results, Atomic, CancellationToken)

        /// <summary>
        /// Put one change into the node, after the pre-flight, and say for each
        /// of its entries what came of it. The slot table is what the node holds
        /// afterwards, where the change went in.
        /// </summary>
        private async Task ApplyAsync(IReadOnlyList<Item>   Change,
                                      SlotServer?[]         Table,
                                      SetVariableResult?[]  Results,
                                      Boolean               Atomic,
                                      CancellationToken     CancellationToken)
        {

            var candidate   = Table.Select(server => server?.Clone()).ToArray();
            var section     = new JObject();
            var transported = new Dictionary<UInt16, String>();

            #region The change, in the shape of the "dns" section

            // Addresses first, so that a server is there before anything is
            // said about it.
            foreach (var item in Change.OrderBy(item => item.IsAddress ? 0 : 1))
            {

                if (item.Slot is not UInt16 slot)
                {
                    section[clientKeys[item.Name]] = ClientJSON(item.Name, item.Value);
                    continue;
                }

                var at = slot - 1;

                if (item.IsAddress)
                {

                    if      (item.Value.Length == 0)       candidate[at] = null;
                    else if (candidate[at] is null)        candidate[at] = new SlotServer(item.Value);
                    else                                   candidate[at]!.Address = item.Value;

                    continue;

                }

                if (candidate[at] is not SlotServer server)
                {
                    Refuse(Change, [ item ], Results, Atomic, "InvalidValue", $"'{ServerComponent}[{slot}]' is unused: set its Address in the same request!");
                    return;
                }

                switch (item.Name.ToLowerInvariant())
                {

                    case "port":
                        server.Port            = item.Value.Length > 0 ? UInt16.Parse(item.Value, CultureInfo.InvariantCulture) : null;
                        break;

                    // The values of the paper in its own case: UDP, TLS, HTTPS_GET.
                    case "transport":
                        transported.TryAdd(slot, server.Transport);
                        server.Transport       = item.Value.ToUpperInvariant();
                        break;

                    case "querytimeout":
                        server.QueryTimeout    = item.Value.Length > 0 ? Decimal.Parse(item.Value, CultureInfo.InvariantCulture) : null;
                        break;

                    case "certificatepins":
                        server.CertificatePins = ListOf(item.Value);
                        break;

                    case "rootpins":
                        server.RootPins        = ListOf(item.Value);
                        break;

                    case "onpinmismatch":
                        server.OnMismatch      = item.Value.ToLowerInvariant();
                        break;

                    case "trustonfirstuse":
                        server.TrustOnFirstUse = item.Value.ToLowerInvariant();
                        break;

                }

            }

            // The port rule: a server switched to another transport without
            // being given a port keeps its port only where it was not the one
            // of its old transport - "UDP on 53" must not become TLS on 53.
            foreach (var (slot, before) in transported)
                if (candidate[slot - 1] is SlotServer server &&
                    !Change.Any(item => item.Slot == slot && item.Is("Port")) &&
                    server.Port == DefaultPortOf(before))
                {
                    server.Port = null;
                }

            if (Change.Any(item => item.Slot is not null))
            {

                if (candidate.All(server => server is null))
                {
                    Refuse(Change, [.. Change.Where(item => item.IsAddress)], Results, Atomic, "InvalidValue",
                           "Every slot would be unused, and without a name server this charging station cannot reach its CSMS by name!");
                    return;
                }

                section["servers"] = new JArray(candidate.OfType<SlotServer>().Select(server => server.ToJSON()));

            }

            #endregion

            if (!SomethingChanges(section, Table))
            {
                foreach (var item in Change)
                    Results[item.Index] = Result(item.Entry, SetVariableStatus.Accepted);
                return;
            }

            #region The pre-flight

            var names = namesToKeepResolving?.Invoke().ToArray() ?? [];

            if (names.Length > 0)
            {

                if (section["enabled"]?.Type == JTokenType.Boolean && !section.Value<Boolean>("enabled"))
                {
                    Refuse(Change, Change, Results, false, "CSMSUnreachable",
                           $"Name resolution cannot be switched off while the CSMS is dialled by its name: '{String.Join("', '", names)}'!");
                    return;
                }

                var now    = preflight!(new JObject(), names, PreflightTime, CancellationToken);
                var then   = preflight!(section,       names, PreflightTime, CancellationToken);

                await Task.WhenAll(now, then).ConfigureAwait(false);

                if (then.Result.Error is String error)
                {
                    Refuse(Change, CausedBy(Change, error, candidate), Results, Atomic, "InvalidValue", Rewritten(error, candidate));
                    return;
                }

                var lost = names.Where(name => now.Result.Resolves(name) && !then.Result.Resolves(name)).ToArray();

                if (lost.Length > 0)
                {

                    var servers = then.Result.Judgements.Count > 0
                                      ? String.Join(", ", then.Result.Judgements.Select(judgement => $"{judgement.Server} {judgement.Outcome}"))
                                      : "no name server answered";

                    Refuse(Change, Change, Results, false, "CSMSUnreachable",
                           $"'{String.Join("', '", lost)}' would not resolve: {servers}!");

                    return;

                }

            }

            #endregion

            log?.Invoke($"A CSMS changes the name resolution of this charging station: {section.ToString(Formatting.None)}");

            if (!update!(section, out var refused, out var notSaved))
            {

                if (notSaved)
                    Refuse(Change, Change, Results, false, "InternalError",
                           $"The name resolution could not be saved, and nothing of it was put into effect: {refused}");

                else
                    Refuse(Change, CausedBy(Change, refused, candidate), Results, Atomic, "InvalidValue", Rewritten(refused, candidate));

                return;

            }

            // In effect: from now on the servers are held to what was sent.
            for (var at = 0; at < Table.Length; at++)
            {
                Table[at] = candidate[at];
                Table[at]?.ShownAsSent();
            }

            foreach (var item in Change)
                Results[item.Index] = Result(item.Entry, SetVariableStatus.Accepted);

        }

        #endregion

        #region (private static) SomethingChanges(Section, Table)

        /// <summary>
        /// Whether the given change says anything the node does not have yet:
        /// a setting of the client, or another list of servers.
        /// </summary>
        private static Boolean SomethingChanges(JObject        Section,
                                                SlotServer?[]  Table)

            => Section.Properties().Any(property => property.Name != "servers") ||
               (Section["servers"] is JArray servers &&
                !JToken.DeepEquals(servers, new JArray(Table.OfType<SlotServer>().Select(server => server.ToJSON()))));

        #endregion

        #region (private static) Refuse(Change, Causing, Results, Atomic, ReasonCode, AdditionalInfo)

        /// <summary>
        /// Refuse a change: the entries that caused it are rejected, and with ACID
        /// and BASE the others are rolled back - without them, all of it is
        /// rejected.
        /// </summary>
        private static void Refuse(IReadOnlyList<Item>   Change,
                                   IReadOnlyList<Item>   Causing,
                                   SetVariableResult?[]  Results,
                                   Boolean               Atomic,
                                   String                ReasonCode,
                                   String                AdditionalInfo)
        {

            var causing = Causing.Count > 0 ? Causing : Change;
            var info    = AdditionalInfo.Length > MaxAdditionalInfo
                              ? AdditionalInfo[..(MaxAdditionalInfo - 1)] + "…"
                              : AdditionalInfo;

            foreach (var item in Change)
                Results[item.Index] = !Atomic || causing.Contains(item)
                                          ? Result(item.Entry, SetVariableStatus.Rejected,   ReasonCode,    info)
                                          : Result(item.Entry, SetVariableStatus.Rolledback, "Unspecified", $"Not set, because the same change could not be made: {info}");

        }

        #endregion

        #region (private static) CausedBy(Change, Error, Candidate)

        /// <summary>
        /// The entries of the server a refusal of the node names, by its place
        /// in the list that was sent - or none, where it names none.
        /// </summary>
        private static IReadOnlyList<Item> CausedBy(IReadOnlyList<Item>  Change,
                                                    String               Error,
                                                    SlotServer?[]        Candidate)
        {

            var match = serverInError.Match(Error);

            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                return [];

            var slot = SlotOf(index, Candidate);

            return slot is UInt16 named
                       ? [.. Change.Where(item => item.Slot == named)]
                       : [];

        }

        #endregion

        #region (private static) Rewritten(Error, Candidate)

        /// <summary>
        /// A refusal of the node in the words of the Device Model: the server it
        /// names by its place in the list that was sent becomes the slot it is in.
        /// </summary>
        private static String Rewritten(String         Error,
                                        SlotServer?[]  Candidate)

            => serverInError.Replace(Error, match =>
                   Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
                   SlotOf(index, Candidate) is UInt16 slot
                       ? $"{ServerComponent}[{slot}]"
                       : ServerComponent);

        #endregion

        #region (private static) SlotOf(Index, Candidate)

        /// <summary>
        /// The slot of the server at the given place of the list that was sent,
        /// in which the unused slots are left out.
        /// </summary>
        private static UInt16? SlotOf(Int32          Index,
                                      SlotServer?[]  Candidate)
        {

            var used = 0;

            for (var at = 0; at < Candidate.Length; at++)
                if (Candidate[at] is not null && used++ == Index)
                    return (UInt16) (at + 1);

            return null;

        }

        #endregion

        #region (private static) ClientJSON(Variable, Value)

        /// <summary>
        /// A value of DNSClientController as its key in the "dns" section says
        /// it: "Default" recursion as the null that leaves it to the server.
        /// </summary>
        private static JToken ClientJSON(String  Variable,
                                         String  Value)

            => Variable.ToLowerInvariant() switch {

                   "querytimeout"      => new JValue(Decimal.Parse(Value, CultureInfo.InvariantCulture)),
                   "maxretries"        => new JValue(Int64.  Parse(Value, CultureInfo.InvariantCulture)),
                   "maxcnamefollows"   => new JValue(Int64.  Parse(Value, CultureInfo.InvariantCulture)),

                   "recursiondesired"  => Value.ToLowerInvariant() switch {
                                              "set"    => new JValue(true),
                                              "unset"  => new JValue(false),
                                              _        => JValue.CreateNull()
                                          },

                   _                   => new JValue(String.Equals(Value, "true", StringComparison.OrdinalIgnoreCase))

               };

        #endregion

        #region (private static) ListOf(Value), DefaultPortOf(Transport)

        /// <summary>
        /// The entries of a comma separated list.
        /// </summary>
        private static List<String> ListOf(String Value)

            => [.. Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

        /// <summary>
        /// The port a name server is asked on when nothing else is said, by its
        /// transport, as the node's DNS client has it.
        /// </summary>
        private static UInt16 DefaultPortOf(String Transport)

            => Transport.ToUpperInvariant() switch {
                   "TLS"                             => 853,
                   var http when http.StartsWith("HTTPS", StringComparison.Ordinal) => 443,
                   var http when http.StartsWith("HTTP",  StringComparison.Ordinal) => 80,
                   _                                 => 53
               };

        #endregion

        #region (private static) Where(Entry), Result(Entry, Status, ReasonCode = null, AdditionalInfo = null)

        private static String Where(SetVariableEntry Entry)

            => Entry.Component is DNSServerController server
                   ? $"{ServerComponent}[{server.Slot}].{Entry.Variable.Name}"
                   : $"{ClientComponent}.{Entry.Variable.Name}";

        private static SetVariableResult Result(SetVariableEntry   Entry,
                                                SetVariableStatus  Status,
                                                String?            ReasonCode       = null,
                                                String?            AdditionalInfo   = null)

            => new (
                   AttributeStatus:       Status,
                   Component:             Entry.Data.Component,
                   Variable:              Entry.Data.Variable,
                   AttributeType:         Entry.Variable.Attributes?.Type ?? AttributeTypes.Actual,
                   AttributeStatusInfo:   ReasonCode is not null
                                              ? new StatusInfo(ReasonCode, AdditionalInfo)
                                              : null
               );

        #endregion


        #region (private) Item

        /// <summary>
        /// An entry that may be tried: which slot, or the client, and what.
        /// </summary>
        private sealed record Item(Int32             Index,
                                   SetVariableEntry  Entry,
                                   UInt16?           Slot,
                                   String            Name,
                                   String            Value)
        {

            public Boolean IsAddress
                => Slot is not null && Is("Address");

            public Boolean Is(String Variable)
                => String.Equals(Name, Variable, StringComparison.OrdinalIgnoreCase);

        }

        #endregion

        #region (private) SlotServer

        /// <summary>
        /// The server in one slot, as the node's file says it - and what it was
        /// held to when the request found it, or since this request set it.
        /// </summary>
        private sealed class SlotServer(String Address)
        {

            public String        Address          { get; set; } = Address;
            public UInt16?       Port             { get; set; }
            public String        Transport        { get; set; } = "UDP";
            public Decimal?      QueryTimeout     { get; set; }
            public List<String>  CertificatePins  { get; set; } = [];
            public List<String>  RootPins         { get; set; } = [];
            public String        OnMismatch       { get; set; } = "refuse";
            public String        TrustOnFirstUse  { get; set; } = "none";

            /// <summary>
            /// What the server was held to as last seen: null for a server new
            /// in this request, which the node has nothing to compare with.
            /// </summary>
            public JObject?      AsShown          { get; private set; }


            public static SlotServer?[] TableOf(JObject  Section,
                                                UInt16   Slots)
            {

                var table   = new SlotServer?[Slots];
                var servers = Section["servers"] as JArray ?? [];

                for (var at = 0; at < Slots && at < servers.Count; at++)
                {

                    if (servers[at] is not JObject json)
                        continue;

                    var server = new SlotServer(json.Value<String>("address") ?? "") {
                                     Port             = json["port"]?.Type == JTokenType.Integer ? json.Value<UInt16>("port") : null,
                                     Transport        = json.Value<String>("transport") ?? "UDP",
                                     QueryTimeout     = json["queryTimeoutSeconds"]?.Type is JTokenType.Integer or JTokenType.Float ? json.Value<Decimal>("queryTimeoutSeconds") : null,
                                     CertificatePins  = ListOf(PinsOf(json, "certificateFingerprint")),
                                     RootPins         = ListOf(PinsOf(json, "rootFingerprint")),
                                     OnMismatch       = json.Value<String>("onMismatch")      ?? "refuse",
                                     TrustOnFirstUse  = json.Value<String>("trustOnFirstUse") ?? "none"
                                 };

                    server.ShownAsSent();

                    table[at] = server;

                }

                return table;

            }

            public SlotServer Clone()

                => new (Address) {
                       Port             = Port,
                       Transport        = Transport,
                       QueryTimeout     = QueryTimeout,
                       CertificatePins  = [.. CertificatePins],
                       RootPins         = [.. RootPins],
                       OnMismatch       = OnMismatch,
                       TrustOnFirstUse  = TrustOnFirstUse,
                       AsShown          = AsShown
                   };

            /// <summary>
            /// From now on, what the server is held to is what it was shown with.
            /// </summary>
            public void ShownAsSent()
            {
                AsShown = PinsJSON();
            }

            /// <summary>
            /// What it is held to, in the keys of the node's file. A mismatch
            /// only where it can come about: with a pin, or one to be learned.
            /// </summary>
            private JObject PinsJSON()
            {

                var json = new JObject();

                if (CertificatePins.Count > 0)
                    json["certificateFingerprints"] = new JArray(CertificatePins);

                if (RootPins.Count > 0)
                    json["rootFingerprints"]        = new JArray(RootPins);

                if (TrustOnFirstUse != "none")
                    json["trustOnFirstUse"]         = TrustOnFirstUse;

                if (OnMismatch != "refuse" && json.Count > 0)
                    json["onMismatch"]              = OnMismatch;

                return json;

            }

            public JObject ToJSON()
            {

                var json = new JObject(
                               new JProperty("address",    Address),
                               new JProperty("transport",  Transport)
                           );

                if (Port.HasValue)
                    json["port"]                 = Port.Value;

                if (QueryTimeout.HasValue)
                    json["queryTimeoutSeconds"]  = QueryTimeout.Value;

                json.Merge(PinsJSON());

                if (AsShown is not null)
                    json[ServerPins.AsShownKey]  = AsShown.DeepClone();

                return json;

            }

        }

        #endregion

    }

}
