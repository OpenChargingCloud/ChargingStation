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
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Server;

using cloud.charging.open.ChargingStation.OCPP;

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

            public ISshHostKey       HostKey        { get; } = SshHostKey.GenerateEd25519();
            public SshServer         Server         { get; }
            public SshMessageServer  Messages       { get; }

            /// <summary>
            /// Whether it lets in whoever shows a key.
            /// </summary>
            public Boolean           LetsIn         { get; set; } = true;

            public UInt16            Port
                => Server.LocalEndPoint.Port.ToUInt16();

            public String            Fingerprint
                => SshFingerprint.Sha256(HostKey.PublicKeyBlob);

            public SshCSMS()
            {

                Messages  = new SshMessageServer(new SshMessageServerOptions {
                                Subsystem     = "ocpp@open.charging.cloud",
                                Subprotocols  = [ "ocpp2.1", "ocpp2.0.1", "ocpp1.6" ]
                            });

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
                                                     ValueTask.FromResult(name == Messages.Subsystem ? Messages.Handler : null)
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

        #endregion


        #region AStationRegistersOverSSHWithItsOwnKeyAndPinsTheHostKey()

        /// <summary>
        /// Dialled over SSH, a station signs in as the URL says with a key of
        /// its own, registers with its CSMS, and writes down the host key it
        /// was shown, where none was pinned.
        /// </summary>
        [Test]
        public async Task AStationRegistersOverSSHWithItsOwnKeyAndPinsTheHostKey()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS over SSH", SshURL(), "CSMS", true, null, null,
                                                              out var id, out var error),
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

                Assert.That(station.Connections.Connections.Single().SshHostKey, Is.EqualTo(csms.Fingerprint),
                            "The host key shown on the first connection was not written down.");

                Assert.That(File.Exists(station.SshKey.Path), Is.True, "The key was not kept.");

            });

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
                                                              out var id, out var error),
                        Is.True, error);

            await Start();

            await Until(() => station.ConnectionStates.TryGetValue(id!, out var state) && state.Status == ConnectionStatus.Refused,
                        "Being turned away", () => SaidOf(id!));

            Assert.That(SaidOf(id!), Does.Contain("did not let this station in").And.Contain("not tried again"));

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
