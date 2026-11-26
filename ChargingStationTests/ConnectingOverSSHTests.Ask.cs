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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Connections;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Trust on first use by asking over SSH (mDNS-Concept.md, B.4 and B.8):
    /// the host key shown waits for an admin and nothing goes over the
    /// connection - not even the login -, an admin confirms it and the
    /// station registers, or turns it down and the station stays away; a CSMS
    /// found as lc1.local by Multicast DNS asks without being told to (Q2);
    /// and the command line that does it.
    /// </summary>
    public partial class ConnectingOverSSHTests
    {

        private String Asking(String? Trust = "Ask", String? URL = null)
        {

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("CSMS over SSH"), org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.Parse(URL ?? SshURL()), "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              SshTrust: Trust is not null ? new SshTrustSettings(Trust, null, null) : null),
                        Is.True, error);

            return id!;

        }

        private Boolean StandsAt(String Id, ConnectionStatus Status)
            => station!.ConnectionStates.TryGetValue(Id, out var state) && state.Status == Status;


        #region AHostKeyThatAsks_WaitsForAnAdmin_AndConfirmedTheStationRegisters()

        /// <summary>
        /// "Ask": the host key shown is written down, the station does not sign
        /// in and says that it waits; confirmed by an admin it is pinned, with
        /// who confirmed it, and the station connects again by itself and
        /// registers.
        /// </summary>
        [Test]
        public async Task AHostKeyThatAsks_WaitsForAnAdmin_AndConfirmedTheStationRegisters()
        {

            var id = Asking();

            await Start();

            await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));

            var entry = station!.Connections.Connections.Single();

            Assert.Multiple(() => {
                Assert.That(entry.SshAwaitingHostKey,                    Is.EqualTo(csms!.Fingerprint));
                Assert.That(entry.SshHostKey,                            Is.Null);
                Assert.That(csms.Logins,                                 Is.Empty, "The station signed in at a server nobody confirmed.");
                Assert.That(csms.Requests("BootNotification"),           Is.Empty);
                Assert.That(SaidOf(id),                                  Does.Contain("waits for an admin to confirm the host key").And.Contain(csms.Fingerprint));
                Assert.That(station.AwaitingKeyNotices(),                Has.Exactly(1).Contains(csms.Fingerprint).And.Contains("Connections page"),
                            "Nobody who may confirm it is told above every page.");
            });

            Assert.That(station.Connections.TryConfirmKey(id, "alice", csms.Fingerprint, out _, out var error), Is.True, error);

            await Until(() => StandsAt(id, ConnectionStatus.Connected),       "Connecting once confirmed", () => SaidOf(id));
            await Until(() => csms.Requests("BootNotification").Length > 0,   "A BootNotification once confirmed");

            entry = station.Connections.Connections.Single();

            Assert.Multiple(() => {
                Assert.That(entry.SshHostKey,          Is.EqualTo(csms.Fingerprint));
                Assert.That(entry.SshAwaitingHostKey,  Is.Null);
                Assert.That(entry.KeyConfirmedBy,      Is.EqualTo("alice"));
                Assert.That(station.AwaitingKeyNotices(), Is.Empty, "A key confirmed is still said to wait.");
            });

        }

        #endregion

        #region AHostKeyTurnedDown_KeepsTheStationAway()

        /// <summary>
        /// Turned down by an admin: forgotten, the connection refused with who
        /// did it, and nothing signed in to - until it is saved again.
        /// </summary>
        [Test]
        public async Task AHostKeyTurnedDown_KeepsTheStationAway()
        {

            var id = Asking();

            await Start();

            await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));

            Assert.That(station!.Connections.TryRejectKey(id, "bob", out _, out var error), Is.True, error);

            await Until(() => StandsAt(id, ConnectionStatus.Refused), "Refusing the connection turned down", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(station.Connections.Connections.Single().KeyRejected,  Is.EqualTo(csms!.Fingerprint));
                Assert.That(SaidOf(id),                                            Does.Contain("bob turned down the key"));
                Assert.That(csms.Logins,                                           Is.Empty);
            });

        }

        #endregion

        #region ACSMSFoundByMulticastDNS_AsksWithoutBeingToldTo()

        /// <summary>
        /// ssh://cs001@lc1.local - found by Multicast DNS, which says nothing of
        /// who answered: without a pin and without a word of trust on first use it
        /// asks (Q2), and confirmed it connects.
        /// </summary>
        [Test]
        public async Task ACSMSFoundByMulticastDNS_AsksWithoutBeingToldTo()
        {

            var network    = new InMemoryMulticastDNSNetwork();
            var transport  = network.CreateTransport(IPv4Address.Parse("10.0.0.7"));
            var responder  = new MulticastDNSResponder(transport);

            try
            {

                await responder.StartAsync();
                await responder.PublishAsync([ new A(DomainName.Parse("lc1.local."), DNSQueryClasses.IN, TimeSpan.FromSeconds(120), IPv4Address.Localhost) ],
                                             Probe: false);

                station!.MulticastDNSTransportFactory = options => network.CreateTransport(IPv4Address.Parse("10.0.0.2"));

                Assert.That(station.TryUpdateDNSConfiguration(JObject.Parse("""{ "multicast": { "enabled": true } }"""), out var problem), Is.True, problem);

                var id = Asking(Trust: null, URL: $"ssh://cs001@lc1.local:{csms!.Port}");

                Assert.That(station.Connections.Connections.Single().EffectiveSshTrustOnFirstUse, Is.EqualTo(SshTrustOnFirstUse.Ask));

                await Start();

                await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));

                Assert.That(station.Connections.TryConfirmKey(id, "alice", null, out _, out var error), Is.True, error);

                await Until(() => StandsAt(id, ConnectionStatus.Connected),       "Connecting to lc1.local once confirmed", () => SaidOf(id));
                await Until(() => csms.Requests("BootNotification").Length > 0,   "A BootNotification to lc1.local");

            }
            finally
            {
                await responder.DisposeAsync();
                await transport.DisposeAsync();
            }

        }

        #endregion

        #region TheCommandLineConfirmsAKeyThatWaits()

        /// <summary>
        /// "connection list" names the key that waits and how to confirm it;
        /// "connection confirm-key" confirms it, and the station registers.
        /// </summary>
        [Test]
        public async Task TheCommandLineConfirmsAKeyThatWaits()
        {

            var id = Asking();

            await Start();

            await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));

            using var cli = new NodeCLI(station!);

            var listed    = await cli.Execute("connection list");
            var confirmed = await cli.Execute($"connection confirm-key {id} {csms!.Fingerprint}");

            Assert.Multiple(() => {
                Assert.That(listed,        Has.Some.Contains("AwaitingConfirmation"));
                Assert.That(listed,        Has.Some.EqualTo($"    waits for an admin to confirm {csms.Fingerprint}: connection confirm-key {id} {csms.Fingerprint}"));
                Assert.That(confirmed[0],  Does.StartWith($"Confirmed {csms.Fingerprint} for '{id}'"));
            });

            await Until(() => csms.Requests("BootNotification").Length > 0, "A BootNotification once confirmed at the command line");

            Assert.That(station!.Connections.Connections.Single().KeyConfirmedBy, Is.EqualTo("the console"));

        }

        #endregion

    }

}
