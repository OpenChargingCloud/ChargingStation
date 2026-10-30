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

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Configuration;

using cloud.charging.open.ChargingStation.OCPP;

using OCPP21 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a CSMS changes of the station's name resolution through
    /// SetVariables, against what the station's node then holds.
    /// </summary>
    /// <remarks>
    /// The first tests go through the station's own OCPP 2.1 node into its own
    /// node, built and not started: nothing is asked of a name server, and the
    /// station has no CSMS connection whose name a change could cut off. The
    /// pre-flight, which needs one, is tried against a node of words: a
    /// section, a change that is taken or not, and names that resolve or not.
    /// </remarks>
    [TestFixture]
    public class DNSDeviceModelWritingTests
    {

        #region Data

        private const String  ARoot            = "9d2e4f6a8b0c1d3e5f7a9b1c3d5e7f9a0b2c4d6e8f0a1b3c5d7e9f1a2b4c6d8e";
        private const String  AnotherRoot      = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        private String            directory  = "";
        private ChargingStation?  station;
        private DNSDeviceModel    model      = default!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void AStationWithTwoNameServers()
        {

            directory  = TestStations.TemporaryDirectory("dns-device-model-writing");

            var configuration = TestStations.Offline;

            configuration["dns"] = new JObject(
                                       new JProperty("servers", new JArray(
                                           new JObject(
                                               new JProperty("address",          "192.0.2.53"),
                                               new JProperty("transport",        "TLS"),
                                               new JProperty("rootFingerprint",  ARoot)
                                           ),
                                           new JObject(
                                               new JProperty("address",          "2001:db8::53"),
                                               new JProperty("transport",        "UDP")
                                           )
                                       )),
                                       new JProperty("maxRetries", 2)
                                   );

            station    = TestStations.New(directory, configuration, WithDisplay: false);
            model      = new DNSDeviceModel(station.DNSConfigurationJSON);

        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            station = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region TheClientIsSetAsTheFileWouldSayIt()

        /// <summary>
        /// Settings of DNSClientController go into the node, and are what the
        /// next read says: "Unset" recursion as the node's false.
        /// </summary>
        [Test]
        public async Task TheClientIsSetAsTheFileWouldSayIt()
        {

            var results = await Set(null,
                                    (null, "DNSSECOK",         "true"),
                                    (null, "MaxRetries",       "3"),
                                    (null, "RecursionDesired", "Unset"));

            Assert.Multiple(() => {

                Assert.That(StatusesOf(results),                     Is.All.EqualTo(OCPP21.SetVariableStatus.Accepted));

                Assert.That(model.ClientValue("DNSSECOK"),           Is.EqualTo("true"));
                Assert.That(model.ClientValue("MaxRetries"),         Is.EqualTo("3"));
                Assert.That(model.ClientValue("RecursionDesired"),   Is.EqualTo("Unset"));

                Assert.That(model.ServerValue(1, "Address"),         Is.EqualTo("192.0.2.53"), "the servers are kept");
                Assert.That(model.ServerValue(1, "RootPins"),        Is.EqualTo(ARoot));

            });

        }

        #endregion

        #region ANewServerComesWithItsAddress()

        /// <summary>
        /// The entries of an unused slot are one change, made where its Address
        /// stands: a server over TLS held to a root, from the start.
        /// </summary>
        [Test]
        public async Task ANewServerComesWithItsAddress()
        {

            var results = await Set(null,
                                    ("3", "Transport", "TLS"),
                                    ("3", "RootPins",  AnotherRoot),
                                    ("3", "Address",   "203.0.113.53"));

            Assert.Multiple(() => {

                Assert.That(StatusesOf(results),                     Is.All.EqualTo(OCPP21.SetVariableStatus.Accepted));

                Assert.That(model.ServerValue(3, "Address"),         Is.EqualTo("203.0.113.53"));
                Assert.That(model.ServerValue(3, "Transport"),       Is.EqualTo("TLS"));
                Assert.That(model.ServerValue(3, "Port"),            Is.EqualTo("853"));
                Assert.That(model.ServerValue(3, "RootPins"),        Is.EqualTo(AnotherRoot));

            });

        }

        #endregion

        #region AnUnusedSlotHasNothingToSetWithoutAnAddress()

        /// <summary>
        /// A port for a slot no server is in, without an address in the same
        /// request, is refused and named by its slot.
        /// </summary>
        [Test]
        public async Task AnUnusedSlotHasNothingToSetWithoutAnAddress()
        {

            var results = await Set(null, ("3", "Port", "5353"));

            Assert.Multiple(() => {
                Assert.That(results.Single().AttributeStatus,                          Is.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Single().AttributeStatusInfo?.ReasonCode,          Is.EqualTo("InvalidValue"));
                Assert.That(results.Single().AttributeStatusInfo?.AdditionalInfo,      Does.Contain("DNSServerController[3]"));
                Assert.That(model.ServerValue(3, "Address"),                           Is.EqualTo(""));
            });

        }

        #endregion

        #region ASlotMeansItsServerForTheWholeRequest()

        /// <summary>
        /// Slot 1 emptied and slot 2 changed in the same request: the change is
        /// the server's that was in slot 2, although the node moved it up into
        /// slot 1 before.
        /// </summary>
        [Test]
        public async Task ASlotMeansItsServerForTheWholeRequest()
        {

            var results = await Set(null,
                                    ("1", "Address", ""),
                                    ("2", "Port",    "5353"));

            Assert.Multiple(() => {

                Assert.That(StatusesOf(results),                     Is.All.EqualTo(OCPP21.SetVariableStatus.Accepted));

                Assert.That(model.ServerValue(1, "Address"),         Is.EqualTo("2001:db8::53"));
                Assert.That(model.ServerValue(1, "Port"),            Is.EqualTo("5353"));
                Assert.That(model.ServerValue(2, "Address"),         Is.EqualTo(""));

            });

        }

        #endregion

        #region ThePortOfTheOldTransportGoesWithIt()

        /// <summary>
        /// A server switched to TLS without a port is asked on 853, not on the
        /// 53 it had as the default of UDP; a port chosen by hand stays.
        /// </summary>
        [Test]
        public async Task ThePortOfTheOldTransportGoesWithIt()
        {

            var toTLS     = await Set(null, ("2", "Transport", "TLS"));
            var portNow   = model.ServerValue(2, "Port");

            var byHand    = await Set(null, ("2", "Port",      "5353"));
            var toTCP     = await Set(null, ("2", "Transport", "TCP"));

            Assert.Multiple(() => {

                Assert.That(StatusesOf([.. toTLS, .. byHand, .. toTCP]), Is.All.EqualTo(OCPP21.SetVariableStatus.Accepted));

                Assert.That(portNow,                                 Is.EqualTo("853"));
                Assert.That(model.ServerValue(2, "Port"),            Is.EqualTo("5353"));
                Assert.That(model.ServerValue(2, "Transport"),       Is.EqualTo("TCP"));

            });

        }

        #endregion

        #region EveryAddressEmptiedIsRefused()

        /// <summary>
        /// Emptying every slot at once is refused, and the servers are kept
        /// (SecureDNS T14).
        /// </summary>
        [Test]
        public async Task EveryAddressEmptiedIsRefused()
        {

            var results = await Set(OCPP21.DataConsistencyModel.ACID,
                                    ("1", "Address", ""),
                                    ("2", "Address", ""));

            Assert.Multiple(() => {
                Assert.That(StatusesOf(results),                                       Is.All.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Select(result => result.AttributeStatusInfo?.ReasonCode), Is.All.EqualTo("InvalidValue"));
                Assert.That(model.ServerValue(1, "Address"),                           Is.EqualTo("192.0.2.53"));
                Assert.That(model.ServerValue(2, "Address"),                           Is.EqualTo("2001:db8::53"));
            });

        }

        #endregion

        #region ARefusalOfTheNodeRollsTheRestBack()

        /// <summary>
        /// With ACID, a pin for a server over UDP is refused by the node, in
        /// the words of the Device Model; the other entry is rolled back, and
        /// nothing changed.
        /// </summary>
        [Test]
        public async Task ARefusalOfTheNodeRollsTheRestBack()
        {

            var results = await Set(OCPP21.DataConsistencyModel.ACID,
                                    (null, "MaxRetries",      "4"),
                                    ("2",  "CertificatePins", AnotherRoot));

            Assert.Multiple(() => {

                Assert.That(StatusesOf(results),                     Is.EqualTo(new[] { OCPP21.SetVariableStatus.Rolledback, OCPP21.SetVariableStatus.Rejected }));
                Assert.That(results[1].AttributeStatusInfo?.ReasonCode,      Is.EqualTo("InvalidValue"));
                Assert.That(results[1].AttributeStatusInfo?.AdditionalInfo,  Does.Contain("'DNSServerController[2]'"));
                Assert.That(results[1].AttributeStatusInfo?.AdditionalInfo,  Does.Not.Contain("dns.servers"));

                Assert.That(model.ClientValue("MaxRetries"),         Is.EqualTo("2"));
                Assert.That(model.ServerValue(2, "CertificatePins"), Is.EqualTo(""));

            });

        }

        #endregion

        #region AChangeTheCSMSHasNotSeenIsNotMade()

        /// <summary>
        /// Compare and set: an entry whose old value is not the value now is
        /// refused, and a CSMS overwrites nothing it has not seen.
        /// </summary>
        [Test]
        public async Task AChangeTheCSMSHasNotSeenIsNotMade()
        {

            var node     = station!.OCPPv2_1Node;

            var request  = new OCPP21.CSMS.SetVariablesRequest(
                               Destination:   SourceRouting.To(node.Id),
                               VariableData:  [
                                                  new OCPP21.SetVariableData(
                                                      Component:          new OCPP21.Component(Name: DNSDeviceModel.ServerComponent, Instance: "2"),
                                                      Variable:           new OCPP21.Variable (Name: "Port"),
                                                      AttributeValue:     "5353",
                                                      OldAttributeValue:  "5300"
                                                  )
                                              ]
                           );

            var results  = await OCPP21.DeviceModel.SetVariablesAsync(request, (name, evse) => node.GetComponentConfigs(name, evse));

            Assert.Multiple(() => {
                Assert.That(results.Single().AttributeStatus,                          Is.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Single().AttributeStatusInfo?.ReasonCode,          Is.EqualTo("ValueChanged"));
                Assert.That(model.ServerValue(2, "Port"),                              Is.EqualTo("53"));
            });

        }

        #endregion

        #region SwitchedOffWhileTheCSMSIsDialledByNameIsRefused()

        /// <summary>
        /// Name resolution is not switched off while a CSMS connection is
        /// dialled by its name (SecureDNS T19).
        /// </summary>
        [Test]
        public async Task SwitchedOffWhileTheCSMSIsDialledByNameIsRefused()
        {

            Assert.That(station!.Connections.TryAddConnection(
                            "By name",
                            "ws://csms.example.test/cs001",
                            "CSMS",
                            false, null, null, out _, out var error),
                        Is.True, error);

            var results = await Set(null, (null, "Enabled", "false"));

            Assert.Multiple(() => {
                Assert.That(results.Single().AttributeStatus,                          Is.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Single().AttributeStatusInfo?.ReasonCode,          Is.EqualTo("CSMSUnreachable"));
                Assert.That(results.Single().AttributeStatusInfo?.AdditionalInfo,      Does.Contain("csms.example.test"));
                Assert.That(model.ClientValue("Enabled"),                              Is.EqualTo("true"));
            });

        }

        #endregion


        #region (class) ANodeOfWords

        /// <summary>
        /// What the source needs of a node, written down: a "dns" section with
        /// two servers, changes that are taken or refused as told, and names
        /// that resolve now and with a change as told.
        /// </summary>
        private sealed class ANodeOfWords
        {

            public JObject              Section          { get; set; } = new (
                                                                             new JProperty("enabled", true),
                                                                             new JProperty("servers", new JArray(
                                                                                 new JObject(new JProperty("address", "192.0.2.53"),   new JProperty("port", 853), new JProperty("transport", "TLS")),
                                                                                 new JObject(new JProperty("address", "2001:db8::53"), new JProperty("port", 53),  new JProperty("transport", "UDP"))
                                                                             )),
                                                                             new JProperty("limits", new JObject(new JProperty("maxServers", 16)))
                                                                         );

            public List<JObject>        Changes          { get; } = [];

            public Boolean              NotSaved         { get; set; }

            public Boolean              ResolvesNow      { get; set; } = true;

            public Boolean              ResolvesThen     { get; set; } = true;

            public String[]             Names            { get; set; } = [ "csms.example.test" ];

            public Boolean TryUpdate(JObject                           Change,
                                     [NotNullWhen(false)] out String?  Error,
                                     out Boolean                       NotSaved)
            {

                NotSaved = this.NotSaved;
                Error    = NotSaved ? "The file is read-only." : null;

                if (!NotSaved)
                    Changes.Add(Change);

                return Error is null;

            }

            public Task<DNSPreflight> ResolveWith(JObject              Change,
                                                  IEnumerable<String>  Names,
                                                  TimeSpan             Within,
                                                  CancellationToken    CancellationToken)

                => Task.FromResult(new DNSPreflight(
                                       Names.ToDictionary(name => name, _ => Change.Count == 0 ? ResolvesNow : ResolvesThen),
                                       Change.Count == 0 || ResolvesThen
                                           ? []
                                           : [ new ServerJudgement(
                                                   Service:                 "dns",
                                                   Server:                  "tls://203.0.113.53:853",
                                                   At:                      DateTimeOffset.UtcNow,
                                                   Accepted:                false,
                                                   Outcome:                 "untrusted",
                                                   CertificateFingerprint:  AnotherRoot,
                                                   RootFingerprint:         null,
                                                   AnchoredBy:              null,
                                                   HeldTo:                  null,
                                                   Learned:                 TrustOnFirstUse.None,
                                                   Previously:              null,
                                                   FirstSeen:               false,
                                                   Steps:                   []
                                               ) ]
                                   ));

            public DNSDeviceModel Source()

                => new (() => Section, TryUpdate, ResolveWith, () => Names);

        }

        #endregion

        #region AChangeThatCutsTheCSMSOffIsRefused()

        /// <summary>
        /// A name of a CSMS connection that resolves now and would not resolve
        /// with the change makes all of it CSMSUnreachable, saying the name and
        /// what each server came to; nothing is put into the node (SecureDNS T20).
        /// </summary>
        [Test]
        public async Task AChangeThatCutsTheCSMSOffIsRefused()
        {

            var node    = new ANodeOfWords { ResolvesThen = false };

            var results = await SetIn(node.Source(), null,
                                      ("1", "Address",   "203.0.113.53"),
                                      ("1", "RootPins",  AnotherRoot));

            Assert.Multiple(() => {
                Assert.That(StatusesOf(results),                                       Is.All.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Select(result => result.AttributeStatusInfo?.ReasonCode), Is.All.EqualTo("CSMSUnreachable"));
                Assert.That(results[0].AttributeStatusInfo?.AdditionalInfo,            Does.Contain("'csms.example.test' would not resolve"));
                Assert.That(results[0].AttributeStatusInfo?.AdditionalInfo,            Does.Contain("tls://203.0.113.53:853 untrusted"));
                Assert.That(node.Changes,                                              Is.Empty);
            });

        }

        #endregion

        #region ANameThatDoesNotResolveNowBlocksNothing()

        /// <summary>
        /// A name that does not resolve now does not block a change, so that a
        /// CSMS can repair a broken configuration.
        /// </summary>
        [Test]
        public async Task ANameThatDoesNotResolveNowBlocksNothing()
        {

            var node    = new ANodeOfWords { ResolvesNow = false, ResolvesThen = false };

            var results = await SetIn(node.Source(), null, ("2", "Address", "198.51.100.53"));

            Assert.Multiple(() => {
                Assert.That(results.Single().AttributeStatus,                          Is.EqualTo(OCPP21.SetVariableStatus.Accepted));
                Assert.That(node.Changes.Single()["servers"]?[1]?["address"]?.Value<String>(), Is.EqualTo("198.51.100.53"));
            });

        }

        #endregion

        #region WhatCannotBeSavedIsAnInternalError()

        /// <summary>
        /// A change the node cannot save is refused as an internal error, and
        /// nothing of it was put into effect (SecureDNS T17).
        /// </summary>
        [Test]
        public async Task WhatCannotBeSavedIsAnInternalError()
        {

            var node    = new ANodeOfWords { NotSaved = true };

            var results = await SetIn(node.Source(), OCPP21.DataConsistencyModel.ACID,
                                      (null, "UseCache",  "false"),
                                      ("2",  "Port",      "5353"));

            Assert.Multiple(() => {
                Assert.That(StatusesOf(results),                                       Is.All.EqualTo(OCPP21.SetVariableStatus.Rejected));
                Assert.That(results.Select(result => result.AttributeStatusInfo?.ReasonCode), Is.All.EqualTo("InternalError"));
                Assert.That(node.Changes,                                              Is.Empty);
            });

        }

        #endregion

        #region TheWholeListIsSentWithWhatItWasShownHeldTo()

        /// <summary>
        /// A change of one server sends the whole list, in slot order, each
        /// server with what the request found it held to - so that a pin learned
        /// on first use in the meantime is not lost - and a mismatch only where
        /// a pin can bring one about.
        /// </summary>
        [Test]
        public async Task TheWholeListIsSentWithWhatItWasShownHeldTo()
        {

            var node = new ANodeOfWords();

            ((JObject) node.Section["servers"]![0]!)["rootFingerprint"] = ARoot;
            ((JObject) node.Section["servers"]![0]!)["onMismatch"]      = "record";

            var results = await SetIn(node.Source(), null, ("2", "Port", "5353"));

            var sent    = node.Changes.Single()["servers"] as JArray;

            Assert.Multiple(() => {

                Assert.That(results.Single().AttributeStatus,                          Is.EqualTo(OCPP21.SetVariableStatus.Accepted));
                Assert.That(sent?.Count,                                               Is.EqualTo(2));

                Assert.That(sent?[0]?["address"]?.Value<String>(),                     Is.EqualTo("192.0.2.53"));
                Assert.That(sent?[0]?["rootFingerprints"]?.Values<String>(),           Is.EqualTo(new[] { ARoot }));
                Assert.That(sent?[0]?["onMismatch"]?.Value<String>(),                  Is.EqualTo("record"));
                Assert.That(sent?[0]?[ServerPins.AsShownKey]?["rootFingerprints"]?.Values<String>(), Is.EqualTo(new[] { ARoot }));

                Assert.That(sent?[1]?["address"]?.Value<String>(),                     Is.EqualTo("2001:db8::53"));
                Assert.That(sent?[1]?["port"]?.Value<Int32>(),                         Is.EqualTo(5353));
                Assert.That(sent?[1]?["onMismatch"],                                   Is.Null, "a server held to nothing has no mismatch to come to");
                Assert.That(sent?[1]?[ServerPins.AsShownKey],                          Is.InstanceOf<JObject>().And.Empty);

            });

        }

        #endregion


        #region (private) Set(Model, params Entries)

        /// <summary>
        /// SetVariables of the DNS controllers as the station's OCPP 2.1 node
        /// answers it: (null, variable, value) for the client, (slot, ...) for a
        /// server.
        /// </summary>
        private async Task<OCPP21.SetVariableResult[]> Set(OCPP21.DataConsistencyModel?                             Model,
                                                           params (String? Slot, String Variable, String Value)[]  Entries)
        {

            var node    = station!.OCPPv2_1Node;

            var request = new OCPP21.CSMS.SetVariablesRequest(
                              Destination:           SourceRouting.To(node.Id),
                              VariableData:          [.. Entries.Select(entry => new OCPP21.SetVariableData(
                                                                                     Component:       new OCPP21.Component(Name:     entry.Slot is null ? DNSDeviceModel.ClientComponent : DNSDeviceModel.ServerComponent,
                                                                                                                           Instance: entry.Slot),
                                                                                     Variable:        new OCPP21.Variable(Name: entry.Variable),
                                                                                     AttributeValue:  entry.Value
                                                                                 ))],
                              DataConsistencyModel:  Model
                          );

            return [.. await OCPP21.DeviceModel.SetVariablesAsync(request, (name, evse) => node.GetComponentConfigs(name, evse))];

        }

        #endregion

        #region (private static) SetIn(Source, Model, params Entries)

        /// <summary>
        /// The given entries handed to the given source, as the Device Model
        /// hands them: found in controllers of that source.
        /// </summary>
        private static async Task<OCPP21.SetVariableResult[]> SetIn(DNSDeviceModel                                          Source,
                                                                    OCPP21.DataConsistencyModel?                            Model,
                                                                    params (String? Slot, String Variable, String Value)[]  Entries)
        {

            var client   = new OCPP21.DNSClientController(Source);
            var servers  = OCPP21.DNSServerController.AllOf(Source).ToArray();

            var entries  = Entries.Select(entry => {

                               OCPP21.ComponentConfig component = entry.Slot is null
                                                                      ? client
                                                                      : servers[Int32.Parse(entry.Slot) - 1];

                               return new OCPP21.SetVariableEntry(
                                          new OCPP21.SetVariableData(
                                              Component:       new OCPP21.Component(Name: component.Name, Instance: component.Instance),
                                              Variable:        new OCPP21.Variable(Name: entry.Variable),
                                              AttributeValue:  entry.Value
                                          ),
                                          component,
                                          component.VariableConfigs.Single(variable => String.Equals(variable.Name, entry.Variable, StringComparison.OrdinalIgnoreCase))
                                      );

                           }).ToArray();

            return [.. await Source.SetVariablesAsync(entries, Model ?? OCPP21.DataConsistencyModel.IndependentRequests, CancellationToken.None)];

        }

        #endregion

        #region (private static) StatusesOf(Results)

        private static OCPP21.SetVariableStatus[] StatusesOf(IEnumerable<OCPP21.SetVariableResult> Results)

            => [.. Results.Select(result => result.AttributeStatus)];

        #endregion

    }

}
