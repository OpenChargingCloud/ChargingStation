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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Server;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPP.Node;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What only a charging station does with a name server over SSH (white
    /// paper "DNS over SSH", chapter 7): its key of the purpose DNS in
    /// SSHClientController, its own identity as the login where a server names
    /// none, and the host of a connection of OCPP over SSH as an SSH peer a
    /// name server may share the connection of - before it was ever dialled.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class DNSOverSSHOnAStationTests
    {

        #region Data

        private static readonly String  PinA  = "SHA256:" + new String('A', 43);

        private String            directory  = "";
        private ChargingStation?  station;
        private SshServer?        ssh;
        private DNSServer?        dns;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void AStation()
        {

            directory  = TestStations.TemporaryDirectory("station-dns-ssh");

            var configuration = TestStations.Offline;

            // One name server, at an address of the documentation range, that
            // is given up on at once: nothing here leaves this machine.
            configuration["dns"] = JObject.Parse("""
                                       { "servers": [ { "id": "plain", "address": "192.0.2.1", "queryTimeoutSeconds": 0.2 } ], "queryTimeoutSeconds": 5, "maxRetries": 0 }
                                       """);

            station    = TestStations.New(directory, configuration, WithDisplay: false);

        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (ssh is not null)
                await ssh.DisposeAsync();

            if (dns is not null)
                await dns.Stop();

            ssh = null;
            dns = null;

            if (station is not null)
                await station.DisposeAsync();

            station = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (helpers) Set(Model, Entries) / KeyOf(Purpose)

        private async Task<OCPPv2_1.SetVariableResult[]> Set(OCPPv2_1.DataConsistencyModel?                           Model,
                                                             params (String? Server, String Variable, String Value)[]  Entries)
        {

            var node    = station!.OCPPv2_1Node;

            var request = new OCPPv2_1.CSMS.SetVariablesRequest(
                              Destination:           SourceRouting.To(node.Id),
                              VariableData:          [.. Entries.Select(entry => new OCPPv2_1.SetVariableData(
                                                                                     Component:       new OCPPv2_1.Component(Name:     entry.Server is null ? DNSDeviceModel.ClientComponent : DNSDeviceModel.ServerComponent,
                                                                                                                             Instance: entry.Server),
                                                                                     Variable:        new OCPPv2_1.Variable(Name: entry.Variable),
                                                                                     AttributeValue:  entry.Value
                                                                                 ))],
                              DataConsistencyModel:  Model
                          );

            return [.. await OCPPv2_1.DeviceModel.SetVariablesAsync(request, (name, evse) => node.GetComponentConfigs(name, evse), default, node.NewInstanceOf)];

        }

        /// <summary>
        /// SSHClientController.ClientPublicKey of the given purpose, as a CSMS reads it.
        /// </summary>
        private OCPPv2_1.GetVariableResult KeyOf(String Purpose, String Variable = "ClientPublicKey")
        {

            var node = station!.OCPPv2_1Node;

            return OCPPv2_1.DeviceModel.GetVariables([ new OCPPv2_1.GetVariableData(new OCPPv2_1.Component(nameof(OCPPv2_1.SSHClientController)), new OCPPv2_1.Variable(Variable, Purpose)) ],
                                                     (name, evse) => node.GetComponentConfigs(name, evse)).Single();

        }

        private static String Said(IEnumerable<OCPPv2_1.SetVariableResult> Results)

            => String.Join(" | ", Results.Select(result => $"{result.Component.Instance ?? "client"}.{result.Variable.Name}: {result.AttributeStatus} {result.AttributeStatusInfo?.ReasonCode} {result.AttributeStatusInfo?.AdditionalInfo}"));

        #endregion


        #region TheKeyOfThePurposeDNSIsTheStationsKeyForNameServers()

        /// <summary>
        /// SSHClientController.ClientPublicKey[DNS] (section 7.3): the key the
        /// station signs in with at every name server over SSH with a
        /// connection of its own - made at the first read, a key of its own
        /// beside those of the other purposes, and the one
        /// DNSServerController.SSHClientPublicKey reads. Like the key of NTS it
        /// is the node's: shown, and neither aged nor rotated by the station.
        /// </summary>
        [Test]
        public async Task TheKeyOfThePurposeDNSIsTheStationsKeyForNameServers()
        {

            var dnsKey  = KeyOf("DNS");
            var others  = new[] { "Station", "NTS", "SFTP" }.Select(purpose => KeyOf(purpose).AttributeValue).ToArray();

            var added   = await Set(OCPPv2_1.DataConsistencyModel.ACID,
                                    (null,    "Servers",         "plain,cpo-1"),
                                    ("cpo-1", "Address",         "192.0.2.53"),
                                    ("cpo-1", "Transport",       "SSH"),
                                    ("cpo-1", "SSHHostKeyPins",  PinA));

            var shown   = OCPPv2_1.DeviceModel.GetVariables([ new OCPPv2_1.GetVariableData(new OCPPv2_1.Component(DNSDeviceModel.ServerComponent, "cpo-1"), new OCPPv2_1.Variable("SSHClientPublicKey")) ],
                                                            (name, evse) => station!.OCPPv2_1Node.GetComponentConfigs(name, evse)).Single().AttributeValue;

            Assert.Multiple(() => {

                Assert.That(dnsKey.AttributeStatus,                          Is.EqualTo(OCPPv2_1.GetVariableStatus.Accepted));
                Assert.That(dnsKey.AttributeValue,                           Does.StartWith("ssh-ed25519 "));
                Assert.That(dnsKey.AttributeValue,                           Is.EqualTo(station!.DNSSSHClientPublicKey));
                Assert.That(File.Exists(station.DNSSSHClientKeyPath),        Is.True, "the key was not kept");

                Assert.That(others,                                          Has.None.EqualTo(dnsKey.AttributeValue), "a key of another purpose");
                Assert.That(KeyOf("NTS").AttributeValue,                     Is.EqualTo(station.NTSKEClientPublicKey), "the key of NTS is another one than before");
                Assert.That(KeyOf("DNS", "ClientKeyMaxAge").AttributeValue,  Is.EqualTo("0"));
                Assert.That(KeyOf("DNS", "ClientPendingPublicKey").AttributeValue, Is.EqualTo(""));

                Assert.That(added.Select(result => result.AttributeStatus),  Is.All.EqualTo(OCPPv2_1.SetVariableStatus.Accepted), Said(added));
                Assert.That(shown,                                           Is.EqualTo(dnsKey.AttributeValue));

            });

        }

        #endregion

        #region AConnectionOfOCPPOverSSHIsAnSSHPeerANameServerMayShare_BeforeItWasDialled()

        /// <summary>
        /// The host of a network connection profile with an ssh:// URL is an
        /// SSH peer of the station (section 7.2): a name server may be given
        /// SSHConnection Shared with that host as its Address before the
        /// connection was ever dialled - a station that starts cold has its
        /// names over that connection first. Another host is refused.
        /// </summary>
        [Test]
        public async Task AConnectionOfOCPPOverSSHIsAnSSHPeerANameServerMayShare_BeforeItWasDialled()
        {

            var before  = station!.NameServerSSHPeerHosts;

            Assert.That(station.Connections.TryAddConnection("A Local Controller over SSH", "ssh://lc.example:22", "CSMS", false, null, null,
                                                             out _, out var error, OCPPVersion: "2.1"),
                        Is.True,
                        error);

            var after   = station.NameServerSSHPeerHosts;
            var dialled = station.SSHPeerTo(DomainName.Parse("lc.example"));

            var other   = await Set(OCPPv2_1.DataConsistencyModel.ACID,
                                    (null, "Servers",        "plain,lc"),
                                    ("lc", "Address",        "other.example"),
                                    ("lc", "Transport",      "SSH"),
                                    ("lc", "SSHConnection",  "Shared"));

            var shared  = await Set(OCPPv2_1.DataConsistencyModel.ACID,
                                    (null, "Servers",        "plain,lc"),
                                    ("lc", "Address",        "lc.example"),
                                    ("lc", "Transport",      "SSH"),
                                    ("lc", "SSHConnection",  "Shared"));

            Assert.Multiple(() => {

                Assert.That(before,                                          Has.None.EqualTo("lc.example"));
                Assert.That(after,                                           Has.Member("lc.example"));
                Assert.That(dialled,                                         Is.Null, "the connection was dialled already, and its host a peer by that");

                Assert.That(other.Where (result => result.Component.Instance == "lc").
                                  Select(result => result.AttributeStatusInfo?.ReasonCode),
                                                                             Is.All.EqualTo("InvalidValue"), Said(other));
                Assert.That(other.Select(result => result.AttributeStatus),  Has.None.EqualTo(OCPPv2_1.SetVariableStatus.Accepted));

                Assert.That(shared.Select(result => result.AttributeStatus), Is.All.EqualTo(OCPPv2_1.SetVariableStatus.Accepted), Said(shared));
                Assert.That(((JArray) station.DNSConfigurationJSON()["servers"]!).OfType<JObject>().Single(server => server.Value<String>("id") == "lc")["ssh"]?["connection"]?.Value<String>(),
                                                                             Is.EqualTo("shared"));

            });

        }

        #endregion

        #region AStationSignsInAtANameServerUnderItsOwnIdentity()

        /// <summary>
        /// The default login is the station's identity (decision 5 of the white
        /// paper): what it calls itself in OCPP - which views, limits and logs
        /// per station need - with its key of the purpose DNS.
        /// </summary>
        [Test]
        public async Task AStationSignsInAtANameServerUnderItsOwnIdentity()
        {

            var hostKey  = SshHostKey.GenerateEd25519();
            var itsKey   = SshPublicKey.Parse(station!.DNSSSHClientPublicKey!).Blob;
            var audit    = new CollectingAuditSink();
            var asked    = 0;

            dns          = new DNSServer(
                               new AuthoritativeDNSRequestHandler(
                                   new InMemoryDNSZone().
                                       AddZoneFileString("example.test. 300 IN SOA ns.example.test. hostmaster.example.test. 1 3600 600 86400 300", Origin: DomainName.Parse("example.test.")).
                                       AddZoneFileString("csms.example.test. 300 IN A 192.0.2.10",                                                    Origin: DomainName.Parse("example.test."))
                               ),
                               new DNSServerOptions {
                                   EnableUDPUnicast    = false,
                                   EnableUDPMulticast  = false,
                                   EnableTCPUnicast    = false
                               }
                           );

            dns.OnDNSRequestReceived += (timestamp, server, serverType, request, ct) => {
                Interlocked.Increment(ref asked);
                return Task.CompletedTask;
            };

            ssh          = new SshServer(new SshServerOptions {
                               HostKeys       = [ hostKey ],
                               Authenticator  = SshUserAuthenticator.ForAuthorizedKeys(itsKey),
                               AuditSink      = audit,
                               Subsystems     = (session, name, ct) => ValueTask.FromResult<SshSubsystemHandler?>(
                                                    name == DNSServer.SSHSubsystemName
                                                        ? async (context, token) => {
                                                              try   { await dns.ServeSSHSubsystemAsync(context, token); }
                                                              catch (OperationCanceledException) { }
                                                          }
                                                        : null
                                                )
                           });

            await ssh.StartAsync(new IPSocket(IPv4Address.Localhost, IPPort.Auto));

            var added    = await Set(OCPPv2_1.DataConsistencyModel.ACID,
                                     (null,    "Servers",         "cpo-1"),
                                     ("cpo-1", "Address",         "127.0.0.1"),
                                     ("cpo-1", "Port",            ssh.LocalEndPoint.Port.ToInt32().ToString()),
                                     ("cpo-1", "Transport",       "SSH"),
                                     ("cpo-1", "SSHHostKeyPins",  SshFingerprint.Sha256(hostKey.PublicKeyBlob)));

            var answer   = await station.ResolveAsync("csms.example.test", [ DNSResourceRecordTypes.A ]);
            var identity = station.OCPPv2_1Node.Id.ToString();

            Assert.Multiple(() => {
                Assert.That(added.Select(result => result.AttributeStatus),  Is.All.EqualTo(OCPPv2_1.SetVariableStatus.Accepted), Said(added));
                Assert.That((answer["answers"] as JArray)?.Select(record => record["value"]?.Value<String>()), Is.EqualTo(new[] { "192.0.2.10" }), answer.ToString());
                Assert.That(asked,                                           Is.EqualTo(1), "the resolver was not asked over SSH");
                Assert.That(identity,                                        Is.Not.Empty);
                Assert.That(audit.Events.OfType<AuthenticationSucceededEvent>().Select(login => login.Username),
                                                                             Is.EqualTo(new[] { identity }), "the login is not what the station calls itself in OCPP");
            });

        }

        #endregion

    }

}
