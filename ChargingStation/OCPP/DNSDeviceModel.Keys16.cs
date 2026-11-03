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

using System.Globalization;
using System.Text.RegularExpressions;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPP16 = cloud.charging.open.protocols.OCPPv1_6;
using OCPP21 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// The configuration keys of OCPP 1.6 the Secure DNS white paper gives the
    /// name servers (section 3.2): read from the node when a central system
    /// asks, and a change of one made as the CSMS of OCPP 2.1 makes it - so
    /// that what one version may change, and what it comes to, is the other's
    /// as well.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A key is turned into the entries of a SetVariables request of
    /// DNSClientController and the DNSServerController slots and handed to
    /// this model, with ACID where it is several: the same checks, the same
    /// pre-flight for the names of the CSMS, the same log line naming the
    /// central system. Where they are all accepted the key is, and rejected
    /// otherwise - OCPP 1.6 has no reason to give.
    /// </para>
    /// <para>
    /// A list (CSL) is per server, in the order of "dns.servers". A shorter
    /// list's last value holds for the servers after it, an empty entry takes
    /// the one before it - the default for the first - and a longer list is
    /// rejected. "dns.ports" and "dns.queryTimeouts" are the exceptions: an
    /// empty or missing entry there means the port of that server's transport
    /// and the timeout of "dns.queryTimeout", because one repeated from the
    /// server before would be the wrong one as soon as the servers differ.
    /// </para>
    /// <para>
    /// "dns.servers" changes the addresses at their places, adds new servers
    /// at the end, which take the transport, pin rules and group of roots of
    /// the last one as the lists say, but none of its pins, and takes away the
    /// ones beyond its end. "dns.encryptedOnly" is not listed and a change of
    /// it is NotSupported, as the node does not have it yet.
    /// </para>
    /// </remarks>
    public sealed partial class DNSDeviceModel : OCPP16.CP.IConfigurationKeySource
    {

        #region Data

        /// <summary>
        /// The keys of the paper the node can answer: the variable each stands
        /// for, whether it is a list per server, and the default of a list.
        /// </summary>
        private static readonly (String Key, String Variable, Boolean PerServer, String Default)[] keys16 = [
            ("dns.enabled",           "Enabled",           false,  ""),
            ("dns.servers",           "Address",           true,   ""),
            ("dns.ports",             "Port",              true,   ""),
            ("dns.transports",        "Transport",         true,   "UDP"),
            ("dns.queryTimeouts",     "QueryTimeout",      true,   ""),
            ("dns.queryTimeout",      "QueryTimeout",      false,  ""),
            ("dns.maxRetries",        "MaxRetries",        false,  ""),
            ("dns.recursionDesired",  "RecursionDesired",  false,  ""),
            ("dns.useCache",          "UseCache",          false,  ""),
            ("dns.dnssecOK",          "DNSSECOK",          false,  ""),
            ("dns.followCNAMEs",      "FollowCNAMEs",      false,  ""),
            ("dns.maxCNAMEFollows",   "MaxCNAMEFollows",   false,  ""),
            ("dns.onPinMismatch",     "OnPinMismatch",     true,   "Refuse"),
            ("dns.trustOnFirstUse",   "TrustOnFirstUse",   true,   "None"),
            ("dns.rootCAs",           "RootCAs",           true,   "")
        ];

        /// <summary>
        /// The pins of the n-th server, numbered from 1.
        /// </summary>
        private static readonly (String Key, String Variable)[] pinKeys16 = [
            ("dns.certificatePins",   "CertificatePins"),
            ("dns.rootPins",          "RootPins")
        ];

        /// <summary>
        /// The lists whose empty or missing entry is the server's own default
        /// rather than the entry before it.
        /// </summary>
        private static readonly String[] ownDefaults16 = [ "dns.ports", "dns.queryTimeouts" ];

        /// <summary>
        /// What a server added by "dns.servers" takes from the last one: the
        /// lists that hold their last value for the servers after it.
        /// </summary>
        private static readonly String[] repeatedForNewServers16 = [ "Transport", "OnPinMismatch", "TrustOnFirstUse", "RootCAs" ];

        private static readonly Regex pinKey16 = new (@"^(dns\.[A-Za-z]+)\.([1-9][0-9]*)$",
                                                      RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        #endregion


        #region Keys

        /// <summary>
        /// The keys of the name servers, with one pair of pin keys per server
        /// there is now.
        /// </summary>
        public IEnumerable<String> Keys
        {
            get
            {

                var keys  = keys16.Select(key => key.Key).ToList();
                var count = ServersIn(section());

                foreach (var (key, _) in pinKeys16)
                    for (var n = 1; n <= count; n++)
                        keys.Add($"{key}.{n}");

                return keys;

            }
        }

        #endregion

        #region Get(Key)

        /// <summary>
        /// The key of this name with what it says now, or null where it is none
        /// of the name servers' keys.
        /// </summary>
        public OCPP16.ConfigurationKey? Get(String Key)
        {

            if (!TryKey(Key, out var name, out var variable, out var perServer, out var number))
                return null;

            var now    = section();
            var count  = ServersIn(now);
            String? value;

            if (number is UInt16 n)
                value = n <= count ? ServerValueIn(now, n, variable) : null;

            else if (perServer)
                value = String.Join(",", Enumerable.Range(1, count).Select(slot => ServerValueIn(now, (UInt16) slot, variable) ?? ""));

            else
                value = ClientValueIn(now, variable);

            return value is null
                       ? null
                       : new OCPP16.ConfigurationKey(name, OCPP16.AccessRights.ReadWrite, value);

        }

        #endregion

        #region ChangeAsync(Key, Value, Sender, CancellationToken = default)

        /// <summary>
        /// Change the key of this name as the CSMS of OCPP 2.1 changes the
        /// variables it stands for - or answer null where it is none of the
        /// name servers' keys.
        /// </summary>
        public async Task<OCPP16.ConfigurationStatus?> ChangeAsync(String             Key,
                                                                   String             Value,
                                                                   String?            Sender,
                                                                   CancellationToken  CancellationToken = default)
        {

            if (Get(Key) is null ||
                !TryKey(Key, out var name, out var variable, out var perServer, out var number))
            {
                return null;
            }

            var now      = section();
            var count    = ServersIn(now);
            var entries  = new List<OCPP21.SetVariableData>();

            if (number is UInt16 n)
                entries.Add(ServerEntry(n, variable, Value.Trim()));

            else if (variable == "Address")
            {
                if (!TryServers(now, count, Value, entries))
                    return OCPP16.ConfigurationStatus.Rejected;
            }

            else if (perServer)
            {

                var ownDefault = ownDefaults16.Contains(name, StringComparer.OrdinalIgnoreCase);

                if (!TryList(Value, count, ownDefault ? null : DefaultOf(name), out var values))
                    return OCPP16.ConfigurationStatus.Rejected;

                for (var slot = 1; slot <= count; slot++)
                    entries.Add(ServerEntry((UInt16) slot, variable, values[slot - 1]));

            }

            else
                entries.Add(ClientEntry(variable, Value.Trim()));

            if (entries.Count == 0)
                return OCPP16.ConfigurationStatus.Accepted;

            OCPP21.ComponentConfig[] components = [ new OCPP21.DNSClientController(this), .. OCPP21.DNSServerController.AllOf(this) ];

            var results = await OCPP21.DeviceModel.SetVariablesAsync(
                                    new OCPP21.CSMS.SetVariablesRequest(
                                        Destination:           SourceRouting.CSMS,
                                        VariableData:          entries,
                                        DataConsistencyModel:  entries.Count > 1 ? OCPP21.DataConsistencyModel.ACID : null,
                                        NetworkPath:           NetworkingNode_Id.TryParse(Sender ?? "", out var sender)
                                                                   ? NetworkPath.From(sender)
                                                                   : NetworkPath.Empty
                                    ),
                                    (componentName, evse) => components.Where(component => String.Equals(component.Name, componentName, StringComparison.OrdinalIgnoreCase)),
                                    CancellationToken
                                );

            return results.All(result => result.AttributeStatus == OCPP21.SetVariableStatus.Accepted)
                       ? OCPP16.ConfigurationStatus.Accepted
                       : OCPP16.ConfigurationStatus.Rejected;

        }

        #endregion


        #region (private) TryServers(Section, Count, Value, Entries)

        /// <summary>
        /// The entries "dns.servers" stands for: a new address for a server at
        /// its place, a new server at the end with what the lists give it, and
        /// the slots beyond the end emptied.
        /// </summary>
        private static Boolean TryServers(JObject  Section,
                                          Int32                         Count,
                                          String                        Value,
                                          List<OCPP21.SetVariableData>  Entries)
        {

            var addresses = Value.Trim().Length == 0 ? [] : Value.Split(',').Select(entry => entry.Trim()).ToArray();

            if (addresses.Any(address => address.Length == 0) || addresses.Length > SlotsIn(Section))
                return false;

            for (var slot = 1; slot <= Math.Max(Count, addresses.Length); slot++)
            {

                if (slot > addresses.Length)
                    Entries.Add(ServerEntry((UInt16) slot, "Address", ""));

                else if (slot <= Count)
                {
                    if (!String.Equals(ServerValueIn(Section, (UInt16) slot, "Address"), addresses[slot - 1], StringComparison.OrdinalIgnoreCase))
                        Entries.Add(ServerEntry((UInt16) slot, "Address", addresses[slot - 1]));
                }

                else
                {

                    Entries.Add(ServerEntry((UInt16) slot, "Address", addresses[slot - 1]));

                    // "The last value repeats": what every list says of a
                    // server after its end - but no pins, whose keys are the
                    // server's own, and no port or timeout, whose missing
                    // entry is the server's default.
                    if (Count > 0)
                        foreach (var variable in repeatedForNewServers16)
                            if (ServerValueIn(Section, (UInt16) Count, variable) is String last && last.Length > 0)
                                Entries.Add(ServerEntry((UInt16) slot, variable, last));

                }

            }

            return true;

        }

        #endregion

        #region (private static) TryList(Value, Count, Default, out Values)

        /// <summary>
        /// A comma separated list of one value per server, as the paper reads
        /// it: where a default is given, an empty entry is the one before it -
        /// the default for the first - and a shorter list's last value holds for
        /// the rest; where none is, an empty or missing entry is "", each
        /// server's own default. A longer list is no list for these servers.
        /// </summary>
        private static Boolean TryList(String            Value,
                                       Int32             Count,
                                       String?           Default,
                                       out List<String>  Values)
        {

            Values = [];

            var written = Value.Trim().Length == 0 ? [] : Value.Split(',').Select(entry => entry.Trim()).ToArray();

            if (written.Length > Count)
                return false;

            var previous = Default ?? "";

            foreach (var entry in written)
            {
                previous = entry.Length > 0 || Default is null ? entry : previous;
                Values.Add(previous);
            }

            while (Values.Count < Count)
                Values.Add(Default is null ? "" : previous);

            return true;

        }

        #endregion

        #region (private static) TryKey(Key, out Name, out Variable, out PerServer, out Number)

        /// <summary>
        /// A key of the paper this station has: its name as the paper writes
        /// it, the variable it stands for, whether it is a list per server, and
        /// the number of the server for a pin key.
        /// </summary>
        private static Boolean TryKey(String       Key,
                                      out String   Name,
                                      out String   Variable,
                                      out Boolean  PerServer,
                                      out UInt16?  Number)
        {

            Name       = "";
            Variable   = "";
            PerServer  = false;
            Number     = null;

            var key    = Key.Trim();
            var match  = pinKey16.Match(key);

            if (match.Success)
            {

                if (pinKeys16.FirstOrDefault(pin => String.Equals(pin.Key, match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) is not { Key: not null } pin ||
                    !UInt16.TryParse(match.Groups[2].Value, out var number))
                {
                    return false;
                }

                Name      = $"{pin.Key}.{number}";
                Variable  = pin.Variable;
                Number    = number;
                return true;

            }

            if (keys16.FirstOrDefault(entry => String.Equals(entry.Key, key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } known)
            {
                Name       = known.Key;
                Variable   = known.Variable;
                PerServer  = known.PerServer;
                return true;
            }

            return false;

        }

        #endregion

        #region (private static) ServersIn / DefaultOf / ClientEntry / ServerEntry

        /// <summary>
        /// How many servers there are: the slots in use.
        /// </summary>
        private static Int32 ServersIn(JObject Section)

            => Section["servers"] is JArray servers
                   ? Math.Min(servers.Count, SlotsIn(Section))
                   : 0;

        private static String DefaultOf(String Key)

            => keys16.FirstOrDefault(entry => String.Equals(entry.Key, Key, StringComparison.OrdinalIgnoreCase)).Default ?? "";

        private static OCPP21.SetVariableData ClientEntry(String  Variable,
                                                          String  Value)

            => new (new OCPP21.Component(ClientComponent), new OCPP21.Variable(Variable), Value);

        private static OCPP21.SetVariableData ServerEntry(UInt16  Slot,
                                                          String  Variable,
                                                          String  Value)

            => new (new OCPP21.Component(ServerComponent, Slot.ToString(CultureInfo.InvariantCulture)), new OCPP21.Variable(Variable), Value);

        #endregion

    }

}
