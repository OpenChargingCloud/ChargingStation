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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Mail;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Server;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Commissioning over SSH: a tool speaking OCPP 2.1 to the station on the
    /// station's own SSH server, as an account that may run it.
    /// </summary>
    /// <remarks>
    /// The tool is a plain SSH message client, which says on the wire what a
    /// commissioning tool would and nothing more; the CSMS on the uplink, where
    /// there is one, is the least of a CSMS over SSH, as in the tests of the
    /// station dialling over SSH.
    /// </remarks>
    [TestFixture]
    public class CommissioningOverSSHTests
    {

        #region (class) Tool

        /// <summary>
        /// A commissioning tool, connected: what it was sent, and a way to ask.
        /// </summary>
        private sealed class Tool
        {

            private readonly ConcurrentQueue<JArray> received = new ();
            private          Int32                   next;

            public SshMessageClient      Client     { get; }
            public SshMessageConnection  Connection { get; private set; } = default!;

            public JArray[] Received
                => [.. received];

            /// <summary>How many messages came as CBOR.</summary>
            public Int32 InCBOR
                => inCBOR;

            private Int32 inCBOR;

            public Tool(SshMessageClient Client)
            {

                this.Client = Client;

                // Before the connection is there, so that nothing said on it
                // goes past unheard.
                Client.OnTextMessageReceived += (timestamp, sender, connection, text, cancellationToken) => {
                    received.Enqueue(JArray.Parse(text));
                    return Task.CompletedTask;
                };

                // What a tool that agreed on CBOR is told, as the JSON it stands for.
                Client.OnBinaryMessageReceived += (timestamp, sender, connection, bytes, cancellationToken) => {

                    if (cloud.charging.open.protocols.OCPP.WebSockets.OCPP_CBORFrame.TryAsJSON(bytes, out var message, out _))
                    {
                        received.Enqueue(message);
                        Interlocked.Increment(ref inCBOR);
                    }

                    return Task.CompletedTask;

                };

            }

            public async Task Connect()
                => Connection = await Client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(15));

            /// <summary>
            /// The answer to a request of the given action - a CALLRESULT or a
            /// CALLERROR - or null where none came within ten seconds.
            /// </summary>
            public async Task<JArray?> Ask(String Action, JObject Payload)
            {

                var id = $"tool-{Interlocked.Increment(ref next)}";

                // The way the connection speaks: CBOR in a binary message, or JSON in a text.
                if (Connection.Subprotocol?.EndsWith("cbor") == true)
                    await Connection.SendBinaryMessage(
                              cloud.charging.open.protocols.OCPP.WebSockets.OCPP_CBORFrame.Join(
                                  new JArray(2, id, Action, new JObject()),
                                  CBORJSON.ToCBOR(Payload.ToString(Formatting.None))
                              ).ToByteArray()
                          );

                else
                    await Connection.SendTextMessage(new JArray(2, id, Action, Payload).ToString(Formatting.None));

                var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

                while (DateTimeOffset.UtcNow < until)
                {

                    if (received.FirstOrDefault(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == id) is JArray answer)
                        return answer;

                    await Task.Delay(20);

                }

                return null;

            }

        }

        #endregion

        #region (class) SshCSMS

        /// <summary>
        /// A CSMS of the least kind on the station's uplink, over SSH: answers
        /// a BootNotification with Accepted and every other request with an
        /// empty answer, keeps what it was sent, and may ask the station.
        /// </summary>
        private sealed class SshCSMS : IAsyncDisposable
        {

            private readonly ConcurrentQueue<JArray>  received = new ();
            private          SshMessageConnection?    station;

            public ISshHostKey       HostKey   { get; } = SshHostKey.GenerateEd25519();
            public SshServer         Server    { get; }
            public SshMessageServer  Messages  { get; }

            public UInt16 Port
                => Server.LocalEndPoint.Port.ToUInt16();

            public SshCSMS()
            {

                Messages  = new SshMessageServer(new SshMessageServerOptions {
                                Subsystem     = "ocpp@open.charging.cloud",
                                Subprotocols  = [ "ocpp2.1" ]
                            });

                Messages.OnNewConnection += (timestamp, sender, connection, cancellationToken) => {
                    station = connection;
                    return Task.CompletedTask;
                };

                Messages.OnTextMessageReceived += async (timestamp, sender, connection, text, cancellationToken) => {

                    var message = JArray.Parse(text);

                    received.Enqueue(message);

                    if (message[0].Value<Int32>() != 2)
                        return;

                    var answer = new JArray(3, message[1], message[2].Value<String>() == "BootNotification"
                                                               ? new JObject(
                                                                     new JProperty("status",       "Accepted"),
                                                                     new JProperty("currentTime",  DateTimeOffset.UtcNow.ToString("o")),
                                                                     new JProperty("interval",     300)
                                                                 )
                                                               : new JObject());

                    await connection.SendTextMessage(answer.ToString(Formatting.None), cancellationToken);

                };

                Server    = new SshServer(new SshServerOptions {
                                HostKeys       = [ HostKey ],
                                Authenticator  = new SshUserAuthenticator((request, cancellationToken) => ValueTask.FromResult(true)),
                                Subsystems     = (session, name, cancellationToken) =>
                                                     ValueTask.FromResult(name == Messages.Subsystem ? Messages.Handler : null)
                            });

            }

            public Task Start()
                => Server.StartAsync(new IPSocket(IPv4Address.Localhost, IPPort.Auto), CancellationToken.None).AsTask();

            public JArray[] Requests(String Action)
                => [.. received.Where(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == Action)];

            public JArray[] Answers(String Id)
                => [.. received.Where(message => message[0].Value<Int32>() is 3 or 4 && message[1].Value<String>() == Id)];

            public Task Ask(String Id, String Action, JObject Payload)
                => station!.SendTextMessage(new JArray(2, Id, Action, Payload).ToString(Formatting.None));

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private const String       Subsystem  = "ocpp@open.charging.cloud";
        private const String       Root       = ChargingStation.DefaultAdminUser;

        private readonly ISshHostKey toolKey  = SshHostKey.GenerateEd25519();

        private String             directory  = "";
        private ChargingStation?   station;
        private SshCSMS?           csms;
        private String             holding    = "systemadmin";

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory  = TestStations.TemporaryDirectory("commissioning");
            holding    = "systemadmin";
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

        /// <summary>
        /// A station with its SSH server on, commissioning on or off, and a key
        /// for root - started, with the CSMS on the uplink where one is wanted.
        /// </summary>
        private async Task<ChargingStation> AStation(Boolean Commissioning, Boolean WithAnUplink = false)
        {

            var configuration = TestStations.Offline;

            configuration["ssh"] = new JObject(
                                       new JProperty("enabled",  true),
                                       new JProperty("port",     TestPorts.Free())
                                   );

            if (Commissioning)
                configuration[CommissioningConfiguration.SectionName] = new JObject(new JProperty("enabled", true));

            station = TestStations.New(directory, configuration);

            if (WithAnUplink)
            {

                csms = new SshCSMS();
                await csms.Start();

                Assert.That(station.Connections.TryAddConnection("CSMS over SSH", $"ssh://cs001@127.0.0.1:{csms.Port}", "CSMS", true, null, null,
                                                                 out _, out var error, SshHostKey: SshFingerprint.Sha256(csms.HostKey.PublicKeyBlob)),
                            Is.True, error);

            }

            station = await TestStations.Started(station, directory);

            var added = await station.AuthorizeSSHKey(Root, SshPublicKey.FromHostKey(toolKey).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

            return station;

        }

        private SshClientOptions SSHAsRoot()
            => SSHAs(Root, toolKey);

        private SshClientOptions SSHAs(String Login, ISshHostKey Key)
            => new () {
                   Username       = Login,
                   VerifyHostKey  = blob => station!.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                   Credentials    = [ Key ]
               };

        /// <summary>
        /// An account besides root, in the given role, with the given key.
        /// </summary>
        private async Task MakeAccount(String Account, String Role, ISshHostKey Key)
        {

            await station!.ExtAPI.AddUser(new User(User_Id.Parse(Account), I18NString.Create(Languages.en, Account), SimpleEMailAddress.Parse($"{Account}@localhost")),
                                          SkipDefaultNotifications: true, SkipNewUserEMail: true, SkipNewUserNotifications: true);

            station.ExtAPI.TryGetUser(User_Id.Parse(Account), out var stored);
            station.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role), out var group);

            Assert.That((await station.ExtAPI.AddUserToUserGroup((User) stored!, User2UserGroupEdgeLabel.IsMember, (UserGroup) group!)).IsSuccess, Is.True, $"joining {Role}");

            var added = await station.AuthorizeSSHKey(Account, SshPublicKey.FromHostKey(Key).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

        }

        /// <summary>
        /// A tool connecting as root, or as the given account with its key,
        /// saying what its HELLO is given: the tool, or why it was not let in.
        /// </summary>
        private async Task<(Tool? Tool, String? Why)> Connect(JObject? Hello = null, String? Login = null, ISshHostKey? Key = null, String Subprotocol = "ocpp2.1")
        {

            var client = new SshMessageClient(new SshMessageClientOptions {
                                                  Host             = "127.0.0.1",
                                                  Port             = station!.SSHPort!.Value.ToUInt16(),
                                                  Ssh              = Login is not null ? SSHAs(Login, Key!) : SSHAsRoot(),
                                                  Subsystem        = Subsystem,
                                                  Subprotocols     = [ Subprotocol ],
                                                  HelloProperties  = Hello
                                              });

            var tool = new Tool(client);

            try
            {
                await tool.Connect();
                return (tool, null);
            }
            catch (TimeoutException)
            {
                throw;
            }
            catch (Exception e)
            {
                return (null, e.Message);
            }

        }

        /// <summary>
        /// Whether root's request for the subsystem is turned down.
        /// </summary>
        private async Task<Boolean> Refused()
        {

            await using var client = await SshClient.ConnectAsync("127.0.0.1", station!.SSHPort!.Value.ToUInt16(), SSHAsRoot()).AsTask().WaitAsync(TimeSpan.FromSeconds(10));

            try
            {
                var channel = await client.OpenSubsystemAsync(Subsystem).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                await channel.CloseAsync();
                return false;
            }
            catch (SshWireException)
            {
                return true;
            }

        }

        private static JObject TriggerABootNotification
            => new (new JProperty("requestedMessage", "BootNotification"));

        private static async Task Until(Func<Boolean> Condition, String What)
        {
            var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(15);
            while (!Condition())
            {
                if (DateTimeOffset.UtcNow > until)
                    Assert.Fail($"{What} did not happen within 15 s.");
                await Task.Delay(20);
            }
        }

        private static async Task<Boolean> EndsWithin(SshMessageConnection Connection, TimeSpan Within)
        {

            var until = DateTime.UtcNow + Within;

            while (Connection.IsAlive && DateTime.UtcNow < until)
                await Task.Delay(50);

            return !Connection.IsAlive;

        }

        /// <summary>
        /// Root, in the given role and no other - as the tests of the web
        /// interface's doors make it.
        /// </summary>
        private async Task Become(String Role)
        {

            if (holding == Role)
                return;

            var user  = station!.ExtAPI.TryGetUser(User_Id.Parse(Root), out var stored) && stored is User account
                            ? account
                            : throw new InvalidOperationException("The account this station made is not there.");

            var join  = station.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(Role),    out var to)   && to   is UserGroup joined ? joined : throw new InvalidOperationException($"There is no group '{Role}'.");
            var leave = station.ExtAPI.TryGetUserGroup(UserGroup_Id.Parse(holding), out var from) && from is UserGroup left   ? left   : throw new InvalidOperationException($"There is no group '{holding}'.");

            Assert.That((await station.ExtAPI.AddUserToUserGroup     (user, Role    == "systemadmin" ? User2UserGroupEdgeLabel.IsAdmin : User2UserGroupEdgeLabel.IsMember, join )).IsSuccess, Is.True, $"joining {Role}");
            Assert.That((await station.ExtAPI.RemoveUserFromUserGroup(user, holding == "systemadmin" ? User2UserGroupEdgeLabel.IsAdmin : User2UserGroupEdgeLabel.IsMember, leave)).IsSuccess, Is.True, $"leaving {holding}");

            holding = Role;

        }

        #endregion


        #region ATool_IsAnswered_UnderAName_OfItsOwn()

        /// <summary>
        /// A tool that connects as its account is answered by the OCPP 2.1
        /// node: the station takes its request as one for itself, and knows
        /// the tool by the account's name.
        /// </summary>
        [Test]
        public async Task ATool_IsAnswered_UnderAName_OfItsOwn()
        {

            await AStation(Commissioning: true);

            Assert.That(station!.CommissioningServed, Is.True, "Switched on in the file, and not served.");

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, why);
            Assert.That(tool!.Connection.Subprotocol, Is.EqualTo("ocpp2.1"));

            var answer = await tool.Ask("TriggerMessage", TriggerABootNotification);

            Assert.That(answer, Is.Not.Null, "The station did not answer the tool.");

            Assert.Multiple(() => {

                Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"The station answered with an error: {answer}");
                Assert.That(answer[2]["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer.ToString());

                Assert.That(station.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { Root }),
                            "The tool is not known by its account's name.");

                Assert.That(station.SSHSessions.Any(session => session.Account == Root && session.Label == ChargingStation.CommissioningLabel), Is.True,
                            "The SSH server does not say what the session is for.");

            });

            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region AToolThatSpeaksCBOR_IsAnsweredInCBOR()

        /// <summary>
        /// A tool that offers "ocpp2.1cbor" is handed to the station's OCPP 2.1
        /// node as one that offers "ocpp2.1" is, and speaks to it in CBOR.
        /// </summary>
        [Test]
        public async Task AToolThatSpeaksCBOR_IsAnsweredInCBOR()
        {

            await AStation(Commissioning: true);

            var (tool, why) = await Connect(Subprotocol: "ocpp2.1cbor");

            Assert.That(tool, Is.Not.Null, why);
            Assert.That(tool!.Connection.Subprotocol, Is.EqualTo("ocpp2.1cbor"));

            var answer = await tool.Ask("TriggerMessage", TriggerABootNotification);

            Assert.That(answer, Is.Not.Null, "The station did not answer the tool.");

            Assert.Multiple(() => {

                Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"The station answered with an error: {answer}");
                Assert.That(answer[2]["status"]?.Value<String>(), Is.EqualTo("Accepted"), answer.ToString());

                Assert.That(tool.InCBOR, Is.GreaterThan(0), "The answer did not come as CBOR.");

                Assert.That(station!.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { Root }),
                            "The tool is not known by its account's name.");

            });

            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region AToolOfOCPP16_IsAnsweredByTheNodeOfOCPP16()

        /// <summary>
        /// A tool that speaks OCPP 1.6 is handed to the station's OCPP 1.6 node by its HELLO, on the same
        /// subsystem, and answered in OCPP 1.6 (white paper "OCPP over SSH", decision 13).
        /// </summary>
        [Test]
        public async Task AToolOfOCPP16_IsAnsweredByTheNodeOfOCPP16()
        {

            await AStation(Commissioning: true);

            var (tool, why) = await Connect(Subprotocol: "ocpp1.6");

            Assert.That(tool, Is.Not.Null, why);
            Assert.That(tool!.Connection.Subprotocol, Is.EqualTo("ocpp1.6"));

            // GetConfiguration is OCPP 1.6's: the OCPP 2.1 node has no such request.
            var answer = await tool.Ask("GetConfiguration", new JObject());

            Assert.Multiple(() => {
                Assert.That(answer,                       Is.Not.Null, "The station did not answer the tool.");
                Assert.That(answer![0].Value<Int32>(),    Is.EqualTo(3), $"The station answered with an error: {answer}");
                Assert.That(answer[2]["configurationKey"], Is.Not.Null, answer.ToString());
                Assert.That(station!.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { Root }));
                Assert.That(station.CommissioningJSON()["subprotocols"]!.Values<String>(), Is.EquivalentTo(new[] { "ocpp2.1", "ocpp2.0.1", "ocpp2.1cbor", "ocpp1.6", "ocpp1.6cbor" }));
            });

            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region AToolOfOCPP16ThatSpeaksCBOR_IsAnsweredInCBOR()

        /// <summary>
        /// A tool that offers "ocpp1.6cbor" is handed to the station's OCPP 1.6
        /// node, and speaks to it in CBOR.
        /// </summary>
        [Test]
        public async Task AToolOfOCPP16ThatSpeaksCBOR_IsAnsweredInCBOR()
        {

            await AStation(Commissioning: true);

            var (tool, why) = await Connect(Subprotocol: "ocpp1.6cbor");

            Assert.That(tool, Is.Not.Null, why);
            Assert.That(tool!.Connection.Subprotocol, Is.EqualTo("ocpp1.6cbor"));

            var answer = await tool.Ask("GetConfiguration", []);

            Assert.That(answer, Is.Not.Null, "The station did not answer the tool.");

            Assert.Multiple(() => {
                Assert.That(answer![0].Value<Int32>(), Is.EqualTo(3), $"The station answered with an error: {answer}");
                Assert.That(answer[2]["configurationKey"], Is.Not.Null, answer.ToString());
                Assert.That(tool.InCBOR, Is.GreaterThan(0), "The answer did not come as CBOR.");
            });

            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region TheUplink_KeepsItsCSMS_WhileAToolIsConnected()

        /// <summary>
        /// While a tool is connected, what the station sends the CSMS goes to
        /// the CSMS on the uplink, and so do its answers to it; the tool gets
        /// the answers to its own requests and nothing else.
        /// </summary>
        [Test]
        public async Task TheUplink_KeepsItsCSMS_WhileAToolIsConnected()
        {

            await AStation(Commissioning: true, WithAnUplink: true);

            await Until(() => csms!.Requests("BootNotification").Length > 0, "The station registering on its uplink");

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, why);

            // What the tool asks for goes to the CSMS: the station is told to
            // register again, and does so where it registers.
            var answer = await tool!.Ask("TriggerMessage", TriggerABootNotification);

            Assert.That(answer?[0].Value<Int32>(), Is.EqualTo(3), $"The tool was not answered: {answer}");

            await Until(() => csms!.Requests("BootNotification").Length > 1, "The triggered BootNotification reaching the CSMS on the uplink");

            // And the CSMS on the uplink is answered there, the tool still
            // connected.
            await csms!.Ask("csms-1", "TriggerMessage", TriggerABootNotification);

            await Until(() => csms.Answers("csms-1").Length > 0, "The CSMS on the uplink being answered");

            Assert.Multiple(() => {

                Assert.That(csms.Answers("csms-1").Single()[0].Value<Int32>(), Is.EqualTo(3), csms.Answers("csms-1").Single().ToString());

                Assert.That(tool.Received.Where(message => message[0].Value<Int32>() == 2), Is.Empty,
                            "The station sent the tool requests that are the CSMS's.");

                Assert.That(tool.Received.Where(message => message[1].Value<String>() == "csms-1"), Is.Empty,
                            "The tool got the answer to the CSMS's request.");

            });

            await tool.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region OnlyAnAccountThatMayRunIt_IsLetIn()

        /// <summary>
        /// The installer and the system administrator get the subsystem; the
        /// operator, who may read about it, and a viewer do not.
        /// </summary>
        [Test]
        public async Task OnlyAnAccountThatMayRunIt_IsLetIn()
        {

            await AStation(Commissioning: true);

            Assert.That(await Refused(), Is.False, "The system administrator was refused.");

            await Become("cpo");
            Assert.That(await Refused(), Is.True,  "The operator was let in.");

            await Become("installer");
            Assert.That(await Refused(), Is.False, "The installer was refused.");

            await Become("viewer");
            Assert.That(await Refused(), Is.True,  "A viewer was let in.");

        }

        #endregion

        #region ItIsOff_UntilSwitchedOn_AndOffAgain_EndsTheTool()

        /// <summary>
        /// Off where the file says nothing; switched on and off without a
        /// restart, written to the file; switched off, the tool connected is
        /// let go of.
        /// </summary>
        [Test]
        public async Task ItIsOff_UntilSwitchedOn_AndOffAgain_EndsTheTool()
        {

            await AStation(Commissioning: false);

            Assert.Multiple(async () => {
                Assert.That(station!.CommissioningServed, Is.False, "Served where the file says nothing.");
                Assert.That(station.CommissioningURL,     Is.Null);
                Assert.That(await Refused(),              Is.True, "The subsystem was there, switched off.");
            });

            Assert.That(station!.TryUpdateCommissioningConfiguration(new JObject(new JProperty("enabled", true)), out var error), Is.True, error);

            var (tool, why) = await Connect();

            Assert.That(tool, Is.Not.Null, why);

            var json = station.CommissioningJSON();

            Assert.Multiple(() => {
                Assert.That(json["served"]?.Value<Boolean>(), Is.True);
                Assert.That(json["url"]?.Value<String>(),      Is.EqualTo(station.SSHURL));
                Assert.That(json["station"]?.Value<String>(),  Is.EqualTo("test02"));
                Assert.That(json["permission"]?.Value<String>(), Is.EqualTo("commissioning:run"));
                Assert.That(json["tools"]?.Values<String>(),   Is.EqualTo(new[] { Root }));
                Assert.That(JObject.Parse(File.ReadAllText(station.ConfigFile.Path))["commissioning"]?["enabled"]?.Value<Boolean>(), Is.True,
                            "Switched on, and not written down.");
            });

            Assert.That(station.TryUpdateCommissioningConfiguration(new JObject(new JProperty("enabled", false)), out error), Is.True, error);

            Assert.That(await EndsWithin(tool!.Connection, TimeSpan.FromSeconds(10)), Is.True, "Switched off, and the tool stayed.");
            Assert.That(await Refused(), Is.True, "Switched off, and the subsystem stayed.");

        }

        #endregion

        #region ATool_ConnectsAsItsAccount_AndAsNothingElse()

        /// <summary>
        /// A HELLO that names another node than the account - the CSMS, or
        /// the station itself - is refused; and an account named like either
        /// is no commissioning tool, though it may run commissioning.
        /// </summary>
        [Test]
        public async Task ATool_ConnectsAsItsAccount_AndAsNothingElse()
        {

            await AStation(Commissioning: true);

            var csmsKey = SshHostKey.GenerateEd25519();

            await MakeAccount("csms", "installer", csmsKey);

            var (asTheCSMS,    _) = await Connect(new JObject(new JProperty("networkingNodeId", "CSMS")));
            var (asTheStation, _) = await Connect(new JObject(new JProperty("networkingNodeId", "test02")));
            var (namedSo,      _) = await Connect(Login: "csms", Key: csmsKey);
            var (asItself,   why) = await Connect(new JObject(new JProperty("networkingNodeId", Root)));

            Assert.Multiple(() => {

                Assert.That(asTheCSMS,    Is.Null, "A tool connected as the CSMS.");
                Assert.That(asTheStation, Is.Null, "A tool connected as the station.");
                Assert.That(namedSo,      Is.Null, "An account named like the CSMS connected as a tool.");
                Assert.That(asItself,     Is.Not.Null, why);

                var station = NetworkingNode_Id.Parse("test02");

                Assert.That(ChargingStation.WhyNoCommissioningToolMayBe("csms",          station), Is.Not.Null, "An account named like the CSMS.");
                Assert.That(ChargingStation.WhyNoCommissioningToolMayBe("CentralSystem", station), Is.Not.Null, "An account named like the central system.");
                Assert.That(ChargingStation.WhyNoCommissioningToolMayBe("test02",        station), Is.Not.Null, "An account named like the station.");
                Assert.That(ChargingStation.WhyNoCommissioningToolMayBe("installer",     station), Is.Null);

            });

            await asItself!.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region ANodeBuiltAnew_IsCommissionedAsWell()

        /// <summary>
        /// The OCPP nodes are built anew when what the station is changes: the
        /// tool connected to the old one is let go of, and the next one is
        /// answered by the new one.
        /// </summary>
        [Test]
        public async Task ANodeBuiltAnew_IsCommissionedAsWell()
        {

            await AStation(Commissioning: true);

            var (before, why) = await Connect();

            Assert.That(before, Is.Not.Null, why);

            Assert.That(station!.TryUpdateNameplateConfiguration(new JObject(new JProperty("vendor", "Commissioned")), out var error, out _), Is.True, error);

            Assert.That(await EndsWithin(before!.Connection, TimeSpan.FromSeconds(10)), Is.True, "The tool of the old node stayed connected.");

            var (after, why2) = await Connect();

            Assert.That(after, Is.Not.Null, why2);

            var answer = await after!.Ask("TriggerMessage", TriggerABootNotification);

            Assert.Multiple(() => {

                Assert.That(answer?[0].Value<Int32>(), Is.EqualTo(3), $"The tool was not answered: {answer}");

                // By the new node, and not by the old one, which still answers
                // whoever its handler is handed - stopped, and with the EVSEs
                // and the nameplate the station had before.
                Assert.That(station.CommissioningServed, Is.True, "Commissioning is no longer served after the nodes were built anew.");
                Assert.That(station.CommissioningTools.Select(id => id.ToString()), Is.EqualTo(new[] { Root }),
                            "The tool is connected to the old node, not to the new one.");

            });

            await after.Connection.Close(MessageCloseCode.NormalClosure, "done");

        }

        #endregion

        #region TheSection_SaysOneThing()

        /// <summary>
        /// The section has one switch, and anything else in it is an error.
        /// </summary>
        [Test]
        public void TheSection_SaysOneThing()
        {

            Assert.Multiple(() => {

                Assert.That(CommissioningConfiguration.TryParse(new JObject(new JProperty("enabled", true)),  out var on,  out var error), Is.True, error);
                Assert.That(on!.IsEnabled, Is.True);

                Assert.That(CommissioningConfiguration.TryParse(new JObject(),                                out var off, out error),     Is.True, error);
                Assert.That(off!.IsEnabled, Is.False, "Served where the section says nothing.");

                Assert.That(CommissioningConfiguration.TryParse(new JObject(new JProperty("enabled", "yes")), out _,       out error),     Is.False);
                Assert.That(CommissioningConfiguration.TryParse(new JObject(new JProperty("enable",  true)),  out _,       out error),     Is.False);
                Assert.That(error, Does.Contain("commissioning.enable"));

                Assert.That(StationConfiguration.TryParse(new JObject(new JProperty("commissioning", true)), out _, out error), Is.False);

            });

        }

        #endregion

    }

}
