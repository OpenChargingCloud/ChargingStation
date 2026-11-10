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

using System.Collections.Concurrent;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Server;
using org.GraphDefined.Vanaheimr.Norn.NTS;

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

using cloud.charging.open.ChargingStation.OCPP;
using cloud.charging.open.protocols.OCPP.Node;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A charging station that dials its CSMS over SSH: signed in with its
    /// own key, the server believed by its host key, and OCPP spoken over the
    /// subsystem "ocpp@open.charging.cloud".
    /// </summary>
    /// <remarks>
    /// An SSH server of the test's own stands in for the CSMS, with the
    /// message subsystem the OCPP nodes speak and the least of a CSMS behind
    /// it: it answers a BootNotification with Accepted, a Heartbeat with its
    /// time, and every other request with an empty answer, and it keeps who
    /// signed in, with which key, and what it was sent.
    /// </remarks>
    [TestFixture]
    public class DiallingOverSSHTests
    {

        #region (class) SshCSMS

        /// <summary>
        /// A CSMS of the least kind, over SSH.
        /// </summary>
        private sealed class SshCSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>                      received   = new ();
            private readonly ConcurrentQueue<(String User, Byte[] Key)>  logins     = new ();

            public ISshHostKey       HostKey        { get; }
            public SshServer         Server         { get; }
            public SshMessageServer  Messages       { get; }

            /// <summary>
            /// Whether it lets in whoever shows a key.
            /// </summary>
            public Boolean           LetsIn         { get; set; } = true;

            /// <summary>
            /// An NTS server whose key exchange it serves beside OCPP, as the
            /// subsystem "ntske/1@open.charging.cloud" on the same listener -
            /// section 8.3 of the white paper "NTS Key Establishment over SSH" -
            /// or none.
            /// </summary>
            public NTSServer?        Time           { get; set; }

            /// <summary>
            /// How many HELLOs it was sent so far.
            /// </summary>
            public Int32             Hellos         => hellos;
            private Int32            hellos;

            /// <summary>
            /// Asked of every HELLO, with how many came before it: a refusal, or
            /// null to welcome it.
            /// </summary>
            public Func<Int32, SshHelloVerdict?>?  RefuseHello  { get; set; }

            public UInt16            Port
                => Server.LocalEndPoint.Port.ToUInt16();

            public String            Fingerprint
                => SshFingerprint.Sha256(HostKey.PublicKeyBlob);

            public SshCSMS(ISshHostKey? Key = null)
            {

                this.HostKey = Key ?? SshHostKey.GenerateEd25519();

                Messages  = new SshMessageServer(new SshMessageServerOptions {
                                Subsystem     = "ocpp@open.charging.cloud",
                                Subprotocols  = [ "ocpp2.1", "ocpp2.0.1", "ocpp1.6" ]
                            });

                Messages.ValidateHello = (server, context, hello, subprotocol, cancellationToken) => {
                                             var before = Interlocked.Increment(ref hellos) - 1;
                                             return ValueTask.FromResult(RefuseHello?.Invoke(before) ?? SshHelloVerdict.Accept());
                                         };

                Messages.OnTextMessageReceived += async (timestamp, sender, connection, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    received.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var action  = message[2].Value<String>();

                    var answer  = new JArray(3, message[1], action switch {

                                      "BootNotification"  => new JObject(
                                                                 new JProperty("status",       "Accepted"),
                                                                 new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                 new JProperty("interval",     300)
                                                             ),

                                      "Heartbeat"         => new JObject(
                                                                 new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o"))
                                                             ),

                                      "SignSSHKey"        => new JObject(
                                                                 new JProperty("status",       "Accepted")
                                                             ),

                                      _                   => new JObject()

                                  });

                    await connection.SendTextMessage(answer.ToString(Formatting.None), cancellationToken);

                };

                Server    = new SshServer(new SshServerOptions {
                                HostKeys       = [ HostKey ],
                                Authenticator  = new SshUserAuthenticator((request, cancellationToken) => {
                                                     logins.Enqueue((request.Username, request.PublicKeyBlob));
                                                     return ValueTask.FromResult(LetsIn);
                                                 }),
                                Subsystems     = (session, name, cancellationToken) =>
                                                     ValueTask.FromResult(name == Messages.Subsystem
                                                                              ? Messages.Handler
                                                                              : name == NTSKE_SSH.SubsystemName && Time is NTSServer time
                                                                                    ? time.ServeSSHSubsystemAsync
                                                                                    : null)
                            });

            }

            public Task Start()
                => Server.StartAsync(new IPSocket(IPv4Address.Localhost, IPPort.Auto), CancellationToken.None).AsTask();

            /// <summary>
            /// The requests of the given action it was sent so far.
            /// </summary>
            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 &&
                                                 message[2].Value<String>() == Action)];

            /// <summary>
            /// Who tried to sign in, with which key.
            /// </summary>
            public (String User, Byte[] Key)[] Logins
                => [.. logins];

            /// <summary>
            /// Ask the station something over its connection, and wait for the
            /// answer: a CALLRESULT or a CALLERROR.
            /// </summary>
            public async Task<JArray> Ask(String Action, JObject Payload)
            {

                var id          = Guid.NewGuid().ToString();
                var connection  = Messages.Connections.First();

                await connection.SendTextMessage(new JArray(2, id, Action, Payload).ToString(Formatting.None));

                var until       = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

                while (DateTimeOffset.UtcNow < until)
                {
                    if (received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == id) is JArray answer)
                        return answer;
                    await Task.Delay(20);
                }

                throw new TimeoutException($"The station did not answer '{Action}' within 10 s.");

            }

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private SshCSMS?          csms;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task MakeAStationAndACSMS()
        {

            directory  = TestStations.TemporaryDirectory("dialling-ssh");
            station    = TestStations.New(directory, TestStations.Offline);

            csms       = new SshCSMS();
            await csms.Start();

        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (csms is not null)
                await csms.DisposeAsync();

            station  = null;
            csms     = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private String SshURL(String Login = "cs001")
            => $"ssh://{Login}@127.0.0.1:{csms!.Port}";

        private async Task Start()
            => station = await TestStations.Started(station!, directory);

        private static UInt16 FreeUDPPort()
        {
            using var udp = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
            return (UInt16) ((System.Net.IPEndPoint) udp.Client.LocalEndPoint!).Port;
        }

        private static async Task Until(Func<Boolean> Condition, String What, Func<String>? Otherwise = null)
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (!Condition())
            {
                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within 15 s.{(Otherwise is not null ? " " + Otherwise() : "")}");
                await Task.Delay(20);
            }
        }

        private String SaidOf(String Id)
            => station!.DialledConnections.TryGetValue(Id, out var said) ? said : "nothing yet";

        /// <summary>
        /// Make the station's name servers know the given name as 127.0.0.1 -
        /// a name of .test, which no name server on the Internet knows (RFC
        /// 6761 §6.2): it reaches the test's CSMS only through them. Not one of
        /// .invalid: that has no address, and a DNS client is not even asked
        /// for one (RFC 6761 §6.4).
        /// </summary>
        private void TheStationsNameServersKnow(String Name)
        {

            var name = DomainName.Parse(Name);

            station!.DNSClient.DNSCache.Add      (DNSServiceName.Parse(Name), new A(name, DNSQueryClasses.IN, TimeSpan.FromHours(1), IPv4Address.Localhost));
            station. DNSClient.DNSCache.AddNoData(DNSServiceName.Parse(Name), DNSResourceRecordTypes.AAAA, TimeSpan.FromHours(1));

        }

        #endregion


        #region AStationRegistersOverSSHWithItsOwnKeyAndLearnsTheHostKeyOnProbation()

        /// <summary>
        /// Dialled over SSH, a station signs in as the URL says with a key of
        /// its own, registers with its CSMS, and - where it trusts on first use
        /// and pins nothing - learns the host key it was shown, on probation:
        /// until a signed pin set confirms it, it executes nothing its server
        /// asks but such a pin set.
        /// </summary>
        [Test]
        public async Task AStationRegistersOverSSHWithItsOwnKeyAndLearnsTheHostKeyOnProbation()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              SshTrust: new SshTrustSettings("HostKey", null, null)),
                        Is.True, error);

            Assert.That(station.SshKey.Key, Is.Null, "A key was made before anything needed it.");

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Connected,
                        "Connecting over SSH", () => SaidOf(id!));

            await Until(() => csms!.Requests("BootNotification").Length > 0, "A BootNotification over SSH");

            var key = station.SshKey.Key;

            Assert.Multiple(() => {

                Assert.That(key, Is.Not.Null, "No key was made to sign in with.");

                Assert.That(csms!.Logins.Select(login => login.User).Distinct(), Is.EqualTo(new[] { "cs001" }),
                            "The station did not sign in as the URL says.");

                Assert.That(csms.Logins.Last().Key, Is.EqualTo(key!.PublicKeyBlob),
                            "The station signed in with another key than its own.");

                Assert.That(station.Connections.Connections.Single().SshLearnedHostKey, Is.EqualTo(csms.Fingerprint),
                            "The host key shown on the first connection was not learned.");

                Assert.That(station.Connections.Connections.Single().SshHostKey, Is.Null,
                            "A learned host key was taken for a pin, without a signed pin set.");

                Assert.That(File.Exists(station.SshKey.Path), Is.True, "The key was not kept.");

            });

            // On probation: what the server asks is not executed.
            var reset = await csms!.Ask("Reset", new JObject(new JProperty("type", "Immediate")));

            Assert.Multiple(() => {
                Assert.That(reset[0].Value<Int32>(),   Is.EqualTo(4), $"A request was executed on probation: {reset}");
                Assert.That(reset[2].Value<String>(),  Is.EqualTo("SecurityError"));
                Assert.That(reset[3].Value<String>(),  Does.Contain("on probation"));
            });

        }

        #endregion

        #region (private) PinSet(Connection, Pins, Version, Signer)

        /// <summary>
        /// A SetVariables request of SSHClientController for the given connection - its pins and their
        /// version - signed by the given key, or by none: as the CSMS sends it.
        /// </summary>
        private JObject PinSet(String       Connection,
                               String       Pins,
                               UInt64       Version,
                               ECCKeyPair?  Signer)
        {

            var request = new SetVariablesRequest(
                              SourceRouting.To(NetworkingNode_Id.Parse("cs001")),
                              [
                                  new SetVariableData(new Component("SSHClientController", Connection), new Variable("HostKeyPins"),        Pins),
                                  new SetVariableData(new Component("SSHClientController", Connection), new Variable("HostKeyPinsVersion"), Version.ToString())
                              ]
                          );

            if (Signer is not null)
                Assert.That(new SignaturePolicy().SignRequestMessage(request, request.ToJSON(true), out var notSigned,
                                                                     Signer.ToSignInfo1("CSMS", I18NString.Create("pin set"), DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            return request.ToJSON(false);

        }

        #endregion

        #region ASignedPinSetConfirmsTheLearnedKeyAndEndsTheProbation()

        /// <summary>
        /// A key learned on probation is confirmed by a pin set that a signer the station believes signed:
        /// the probation ends, and what the server asks is executed again. An unsigned pin set, and one
        /// signed by a stranger, are refused; a pin set of no higher version is refused.
        /// </summary>
        [Test]
        public async Task ASignedPinSetConfirmsTheLearnedKeyAndEndsTheProbation()
        {

            var signer    = ECCKeyPair.GenerateKeys()!;
            var stranger  = ECCKeyPair.GenerateKeys()!;

            Assert.That(station!.Connections.TryAddPinSetSigner(Convert.ToBase64String(signer.PublicKeyBytes), "The CSMS", out var error, out _),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                             out var id, out error,
                                                             SshTrust: new SshTrustSettings("HostKey", null, null)),
                        Is.True, error);

            await Start();

            await Until(() => station.Connections.Connections.Single().SshLearnedHostKey is not null, "Learning the host key", () => SaidOf(id!));

            var unsigned  = await csms!.Ask("SetVariables", PinSet(id!, csms.Fingerprint, 1, null));
            var strange   = await csms.  Ask("SetVariables", PinSet(id!, csms.Fingerprint, 1, stranger));

            Assert.Multiple(() => {
                Assert.That(unsigned[0].Value<Int32>(), Is.EqualTo(3), unsigned.ToString());
                Assert.That(unsigned[2]!["setVariableResult"]![0]!.Value<String>("attributeStatus"),  Is.EqualTo("Rejected"));
                Assert.That(unsigned[2]!["setVariableResult"]![0]!["attributeStatusInfo"]!.Value<String>("reasonCode"), Is.EqualTo("Unsigned"));
                Assert.That(strange [2]!["setVariableResult"]![0]!["attributeStatusInfo"]!.Value<String>("reasonCode"), Is.EqualTo("Unsigned"));
                Assert.That(station.Connections.Connections.Single().SshLearnedHostKey, Is.EqualTo(csms.Fingerprint), "An unsigned pin set ended the probation.");
            });

            var confirmed = await csms.Ask("SetVariables", PinSet(id!, csms.Fingerprint, 1, signer));

            Assert.Multiple(() => {
                Assert.That(confirmed[2]!["setVariableResult"]!.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), confirmed.ToString());
                Assert.That(station.Connections.Connections.Single().SshHostKey,         Is.EqualTo(csms.Fingerprint));
                Assert.That(station.Connections.Connections.Single().SshLearnedHostKey,  Is.Null);
                Assert.That(station.Connections.Connections.Single().SshHostKeysVersion, Is.EqualTo(1));
            });

            // Off probation: what the server asks is executed, and an older pin set does not come back.
            var heartbeat = await csms.Ask("TriggerMessage", new JObject(new JProperty("requestedMessage", "Heartbeat")));
            var replayed  = await csms.Ask("SetVariables", PinSet(id!, csms.Fingerprint, 1, signer));

            Assert.Multiple(() => {
                Assert.That(heartbeat[0].Value<Int32>(), Is.EqualTo(3), $"A request was refused after the probation ended: {heartbeat}");
                Assert.That(replayed[2]!["setVariableResult"]![0]!.Value<String>("attributeStatus"), Is.EqualTo("Rejected"));
                Assert.That(replayed[2]!["setVariableResult"]![0]!["attributeStatusInfo"]!.Value<String>("additionalInfo"), Does.Contain("only a higher one is taken"));
            });

        }

        #endregion

        #region AKeyIsRotatedWhenTheCSMSAsksAndConfirms()

        /// <summary>
        /// TriggerMessage "SignSSHKey": the station makes a next key and shows it in SignSSHKey, keeps
        /// signing in with the current one, and takes the next one once SSHKeySigned confirms it.
        /// </summary>
        [Test]
        public async Task AKeyIsRotatedWhenTheCSMSAsksAndConfirms()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: csms!.Fingerprint),
                        Is.True, error);

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Connected,
                        "Connecting over SSH", () => SaidOf(id!));

            var before   = station.SshKey.PublicKeyLine;
            var trigger  = await csms.Ask("TriggerMessage", new JObject(new JProperty("requestedMessage", "CustomTrigger"),
                                                                        new JProperty("customTrigger",    "SignSSHKey")));

            Assert.That(trigger[2]!.Value<String>("status"), Is.EqualTo("Accepted"), trigger.ToString());

            await Until(() => csms.Requests("SignSSHKey").Length > 0, "A SignSSHKey");

            var shown    = csms.Requests("SignSSHKey")[0][3]!;
            var next     = shown.Value<String>("publicKey")!;

            Assert.Multiple(() => {
                Assert.That(shown.Value<String>("purpose"),    Is.EqualTo("OCPP"));
                Assert.That(next,                              Is.Not.EqualTo(before));
                Assert.That(station.SshKey.PublicKeyLine,      Is.EqualTo(before), "The next key signed in before it was confirmed.");
            });

            var stranger = await csms.Ask("SSHKeySigned", new JObject(new JProperty("purpose", "OCPP"), new JProperty("publicKey", "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIStranger")));
            var signed   = await csms.Ask("SSHKeySigned", new JObject(new JProperty("purpose", "OCPP"), new JProperty("publicKey", next)));

            Assert.Multiple(() => {
                Assert.That(stranger[2]!.Value<String>("status"),  Is.EqualTo("Rejected"));
                Assert.That(signed  [2]!.Value<String>("status"),  Is.EqualTo("Accepted"), signed.ToString());
                Assert.That(station.SshKey.PublicKeyLine!.Split(' ')[1], Is.EqualTo(next.Split(' ')[1]), "The confirmed key was not taken.");
                Assert.That(File.Exists(station.SshKey.PendingPath), Is.False);
            });

        }

        #endregion

        #region ANoPinAndNoTrustOnFirstUseIsNotDialled()

        /// <summary>
        /// A connection that pins nothing and does not trust on first use is
        /// not dialled at all: nothing would say which server to believe.
        /// </summary>
        [Test]
        public async Task ANoPinAndNoTrustOnFirstUseIsNotDialled()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error),
                        Is.True, error);

            await Start();

            Assert.Multiple(() => {
                Assert.That(station.ConnectionStates[id!].Status,  Is.EqualTo(ConnectionStatus.NotDialled), SaidOf(id!));
                Assert.That(SaidOf(id!),                            Does.Contain("pins no host key and does not trust on first use"));
                Assert.That(csms!.Logins,                           Is.Empty);
            });

        }

        #endregion

        #region AProbationThatRunsOutForgetsTheKeyAndLearnsAgain()

        /// <summary>
        /// Where no signed pin set confirms a learned host key within the
        /// probation, it is forgotten, the connection is made anew, and the
        /// key of the next first handshake is learned.
        /// </summary>
        [Test]
        public async Task AProbationThatRunsOutForgetsTheKeyAndLearnsAgain()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error,
                                                              SshTrust: new SshTrustSettings("HostKey", null, 2)),
                        Is.True, error);

            await Start();

            await Until(() => station.Connections.Connections.Single().SshLearnedHostKey is not null, "Learning the host key", () => SaidOf(id!));

            var learnedAt = station.Connections.Connections.Single().SshLearnedAt;

            await Until(() => station.Connections.Connections.Single().SshLearnedAt is DateTimeOffset at && at > learnedAt!.Value,
                        "Learning it again after the probation ran out", () => SaidOf(id!));

            await Until(() => csms!.Hellos >= 2, "The connection made anew");

            Assert.That(station.Connections.Connections.Single().SshLearnedHostKey, Is.EqualTo(csms!.Fingerprint));

        }

        #endregion

        #region AMismatchIsBelievedOnlyWhereOnPinMismatchSaysSo()

        /// <summary>
        /// A host key that matches no pin is believed where OnPinMismatch says
        /// to record it - and never where a password may be sent.
        /// </summary>
        [Test]
        public async Task AMismatchIsBelievedOnlyWhereOnPinMismatchSaysSo()
        {

            var someoneElse = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            Assert.That(station!.Connections.TryAddAuthentication("A Basic login", "basic", "cs002", "not-the-real-one", out var basic, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("Recorded", SshURL(), "CSMS", true, null, null, out var recorded, out error,
                                                             SshHostKey: someoneElse, SshTrust: new SshTrustSettings(null, "Record", null)),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("With a password", SshURL("cs002"), "CSMS", true, basic, null, out var withPassword, out error,
                                                             SshHostKey: someoneElse, SshTrust: new SshTrustSettings(null, "Accept", null)),
                        Is.True, error);

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(recorded!, out var state) && state.Status == ConnectionStatus.Connected,
                        "Connecting with a mismatch recorded", () => SaidOf(recorded!));

            await Until(() => station.ConnectionStates.TryGetValue(withPassword!, out var state) && state.Status == ConnectionStatus.Refused,
                        "Refusing a mismatch where a password may be sent", () => SaidOf(withPassword!));

            Assert.Multiple(() => {
                Assert.That(SaidOf(withPassword!),  Does.Contain("a password is never sent to a server that is not believed"));
                Assert.That(csms!.Logins.Select(login => login.User), Has.None.EqualTo("cs002"), "A password went to a server that was not believed.");
            });

        }

        #endregion

        #region AServerThatKeepsTheOtherConnectionIsTriedAgainAfterItsRetryAfter()

        /// <summary>
        /// A server that keeps another connection of the station's identity
        /// answers "connection-exists" with when to come back: not final, and
        /// tried again no earlier than that.
        /// </summary>
        [Test]
        public async Task AServerThatKeepsTheOtherConnectionIsTriedAgainAfterItsRetryAfter()
        {

            csms!.RefuseHello = before => before == 0
                                              ? SshHelloVerdict.Refuse(SshMessageRefusal.ConnectionExists, "cs001 is connected, and answers.", TimeSpan.FromSeconds(2))
                                              : null;

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: csms.Fingerprint),
                        Is.True, error);

            var started = DateTimeOffset.UtcNow;

            await Start();

            Assert.That(station.ConnectionStates[id!].Status, Is.EqualTo(ConnectionStatus.Trying), SaidOf(id!));
            Assert.That(SaidOf(id!), Does.Contain("tried again in 2 s at the earliest"));

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Connected,
                        "Connecting after the retryAfter", () => SaidOf(id!));

            Assert.That(DateTimeOffset.UtcNow - started, Is.GreaterThanOrEqualTo(TimeSpan.FromSeconds(1.9)), "It came back before the server said.");

        }

        #endregion

        #region AServerShowingAnotherHostKeyIsNotBelieved()

        /// <summary>
        /// A server that shows another host key than the one pinned is not
        /// believed, nothing is signed in, and it is not tried again.
        /// </summary>
        [Test]
        public async Task AServerShowingAnotherHostKeyIsNotBelieved()
        {

            var someoneElse = SshFingerprint.Sha256(SshHostKey.GenerateEd25519().PublicKeyBlob);

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: someoneElse),
                        Is.True, error);

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Refused,
                        "Refusing the server", () => SaidOf(id!));

            Assert.Multiple(() => {

                Assert.That(SaidOf(id!), Does.Contain("was not believed").And.Contain(csms!.Fingerprint).And.Contain(someoneElse));

                Assert.That(csms.Logins, Is.Empty, "The station signed in to a server it did not believe.");

                Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(someoneElse),
                            "The pin was changed by a server that showed another key.");

            });

        }

        #endregion

        #region ALoginNotTakenIsFinal()

        /// <summary>
        /// A server that does not let the station in is answered no, and is
        /// not tried again - as a WebSocket's 401 is.
        /// </summary>
        [Test]
        public async Task ALoginNotTakenIsFinal()
        {

            csms!.LetsIn = false;

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: csms.Fingerprint),
                        Is.True, error);

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Refused,
                        "Being turned away", () => SaidOf(id!));

            Assert.That(SaidOf(id!), Does.Contain("did not let this station in").And.Contain("not tried again"));

        }

        #endregion

        #region AHostNameIsResolvedByTheStationsNameServers()

        /// <summary>
        /// The CSMS's host name is resolved with the station's DNS client - the
        /// name servers a CSMS and the web interface configure - and not with
        /// the resolver of the operating system, which does not know it.
        /// </summary>
        [Test]
        public async Task AHostNameIsResolvedByTheStationsNameServers()
        {

            TheStationsNameServersKnow("csms.test");

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", $"ssh://cs001@csms.test:{csms!.Port}", "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: csms.Fingerprint),
                        Is.True, error);

            // The test button first: it resolves as the dialling does.
            var test   = await station.TestConnection("CSMS over SSH", $"ssh://cs001@csms.test:{csms.Port}", "CSMS");
            var steps  = String.Join("\n", test["steps"]!.Select(step => step.Value<String>("text")));

            Assert.That(test.Value<Boolean>("ok"), Is.True, steps);
            Assert.That(steps,                      Does.Contain("'csms.test' resolves to 127.0.0.1"));

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Connected,
                        "Connecting to a name only the station's name servers know", () => SaidOf(id!));

        }

        #endregion

        #region ALoginTurnedAwayOnceTheCSMSIsUpIsFinal()

        /// <summary>
        /// A station that starts while its CSMS is down tries again by itself;
        /// a CSMS that then turns its login away is an answer, said as one, and
        /// not tried again. Before, attempts ahead of the first connection were
        /// heard of by nobody, and the station went on trying.
        /// </summary>
        [Test]
        public async Task ALoginTurnedAwayOnceTheCSMSIsUpIsFinal()
        {

            var port   = csms!.Port;
            var later  = SshHostKey.GenerateEd25519();

            await csms.DisposeAsync();

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", $"ssh://cs001@127.0.0.1:{port}", "CSMS", true, null, null,
                                                              out var id, out var error, SshHostKey: SshFingerprint.Sha256(later.PublicKeyBlob)),
                        Is.True, error);

            await Start();

            Assert.That(station.ConnectionStates[id!].Status, Is.EqualTo(ConnectionStatus.Trying), SaidOf(id!));

            csms        = new SshCSMS(later) { LetsIn = false };
            await csms.Server.StartAsync(new IPSocket(IPv4Address.Localhost, IPPort.Parse(port)), CancellationToken.None);

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Refused,
                        "Being turned away once the CSMS is up", () => SaidOf(id!));

            Assert.That(SaidOf(id!), Does.Contain("did not let this station in").And.Contain("not tried again"));

            var logins = csms.Logins.Length;

            await Task.Delay(TimeSpan.FromSeconds(3));

            Assert.That(csms.Logins.Length, Is.EqualTo(logins), "The station went on trying a login that was turned away.");

        }

        #endregion

        #region ATimeServerOfTheCSMSIsAskedOverTheConnectionOfOCPP()

        /// <summary>
        /// SSHUseOCPPConnection: the CSMS serves NTS-KE beside OCPP on its SSH
        /// listener, and the station's time server there makes its key exchange
        /// as one more channel of the connection the station dialled for OCPP -
        /// signed in once, with the station's OCPP key, and the host key the one
        /// OCPP believed. Before that connection there is no key exchange, and
        /// nothing else is tried; after it, the connection of OCPP goes on.
        /// </summary>
        [Test]
        public async Task ATimeServerOfTheCSMSIsAskedOverTheConnectionOfOCPP()
        {

            var ntpPort = FreeUDPPort();

            var time    = new NTSServer(NTSKEPort:           IPPort.Parse(TestPorts.Free()),
                                        NTSPort:             IPPort.Parse(ntpPort),
                                        MasterKeysFilePath:  null);

            try
            {

                await time.Start();

                csms!.Time = time;

                // A station whose one time server is the CSMS's host, over the
                // connection of OCPP.
                await station!.DisposeAsync();

                station = TestStations.New(directory, new JObject(
                              new JProperty("nts", new JObject(
                                  new JProperty("groups", new JObject(
                                      new JProperty("legal", new JObject(new JProperty("enabled", false))),
                                      new JProperty("local", new JObject(
                                          new JProperty("enabled",     true),
                                          new JProperty("minServers",  1),
                                          new JProperty("servers",     new JArray(new JObject(
                                              new JProperty("id",        "csms-time"),
                                              new JProperty("hostname",  "csms.test"),
                                              new JProperty("ntpPort",   ntpPort),
                                              new JProperty("ssh",       new JObject(new JProperty("useOCPPConnection", true)))
                                          )))
                                      ))
                                  ))
                              ))
                          ));

                TheStationsNameServersKnow("csms.test");

                Assert.That(station.Connections.TryAddConnection("CSMS over SSH", $"ssh://cs001@csms.test:{csms.Port}", "CSMS", true, null, null,
                                                                 out var id, out var error, SshHostKey: csms.Fingerprint),
                            Is.True, error);

                var model   = new NTSDeviceModel(station, Log: null);

                // Before OCPP is connected: no connection to share.
                var before  = await station.SyncTimeAsync(station.NTSGroupOf("local")!);

                await Start();

                await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Connected,
                            "Connecting over SSH", () => SaidOf(id!));

                // What OCPP's login asked of the authenticator - a key may be
                // asked about before it signs.
                var logins  = csms.Logins.Length;

                var after   = await station.SyncTimeAsync(station.NTSGroupOf("local")!);

                Assert.Multiple(() => {

                    Assert.That(before.Value<Boolean>("ok"),                                   Is.False, "a key exchange without a connection of OCPP");

                    Assert.That(after. Value<Boolean>("ok"),                                   Is.True,  after.ToString());
                    Assert.That(model.ClientValue("csms-time", "Mode"),                        Is.EqualTo("NTSv4SSH"));
                    Assert.That(model.ClientValue("csms-time", "SSHUseOCPPConnection"),        Is.EqualTo("true"));
                    Assert.That(model.ClientValue("csms-time", "SSHClientPublicKey"),          Is.EqualTo(station.SshKey.PublicKeyLine));
                    Assert.That(model.ClientValue("csms-time", "SSHHostKeyFingerprint"),       Is.EqualTo(csms.Fingerprint));
                    Assert.That(Int32.Parse(model.ClientValue("csms-time", "Cookies") ?? "0"), Is.GreaterThan(0));

                    Assert.That(csms.Logins.Length,                                            Is.EqualTo(logins), "the key exchange signed in again");
                    Assert.That(station.ConnectionStates[id!].Status,                          Is.EqualTo(ConnectionStatus.Connected), "the connection of OCPP went with the key exchange");
                    Assert.That(File.Exists(station.NTSKEClientKeyPath),                       Is.False, "a key of the node's own was made");

                });

            }
            finally
            {
                await time.ShutdownAsync();
            }

        }

        #endregion

        #region AWebSocketsNameIsAskedOfTheStationsNameServersOnly()

        /// <summary>
        /// The connection test of a WebSocket resolves with the station's name
        /// servers as well, and goes no further where they know nothing: its
        /// TCP probe used to hand the name to the operating system, and so did
        /// its WebSocket client, which was given no DNS client.
        /// </summary>
        [Test]
        public async Task AWebSocketsNameIsAskedOfTheStationsNameServersOnly()
        {

            var name = DomainName.Parse("nowhere.test");

            station!.DNSClient.DNSCache.AddNoData(DNSServiceName.Parse(name.ToString()), DNSResourceRecordTypes.A,    TimeSpan.FromHours(1));
            station. DNSClient.DNSCache.AddNoData(DNSServiceName.Parse(name.ToString()), DNSResourceRecordTypes.AAAA, TimeSpan.FromHours(1));

            var test   = await station.TestConnection("Nowhere", "ws://nowhere.test/cs001", "CSMS");
            var steps  = String.Join("\n", test["steps"]!.Select(step => step.Value<String>("text")));

            Assert.That(test.Value<Boolean>("ok"), Is.False, steps);
            Assert.That(steps, Does.Contain("could not be looked up with this station's name servers"));
            Assert.That(steps, Does.Not.Contain("TCP connection"), "Something was dialled although nothing was resolved.");

        }

        #endregion

        #region ATestOverSSHSaysWhichHostKeyItSawAndWritesNothing()

        /// <summary>
        /// A test of an ssh:// connection gets through, says which host key it
        /// was shown and which key this station signs in with, and writes
        /// nothing down.
        /// </summary>
        [Test]
        public async Task ATestOverSSHSaysWhichHostKeyItSawAndWritesNothing()
        {

            var result = await station!.TestConnection("CSMS over SSH", SshURL(), "CSMS");

            var steps  = String.Join("\n", result["steps"]!.Select(step => step.Value<String>("text")));

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"),  Is.True, steps);
                Assert.That(steps,                         Does.Contain(csms!.Fingerprint), "The host key shown was not said.");
                Assert.That(steps,                         Does.Contain(station.SshKey.PublicKeyLine!), "The key to hand the server was not said.");
                Assert.That(steps,                         Does.Contain("took ocpp2.1"));
                Assert.That(station.Connections.Connections, Is.Empty, "A test wrote something down.");

            });

        }

        #endregion

    }

}
