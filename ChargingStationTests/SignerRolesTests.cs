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
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a signer of a request a CSMS sends may have done to the station:
    /// what the role of its key may do on the page (white paper "Signed OCPP
    /// Messages and Signer Roles", section 5.3).
    /// </summary>
    /// <remarks>
    /// Asked over a connection to a CSMS, as a CSMS asks, with the policy
    /// enforced: a request the policy refuses never reaches its handler.
    /// </remarks>
    [TestFixture]
    public class SignerRolesTests
    {

        #region Data

        private String                        directory  = "";
        private ChargingStation?              station;
        private TalkingToTheCSMSTests.CSMS?   csms;

        /// <summary>
        /// What the policy refused or recorded, as it said so.
        /// </summary>
        private readonly ConcurrentQueue<PolicyViolation>  violations  = new();

        /// <summary>
        /// The message ids of the requests the policy refused.
        /// </summary>
        private readonly ConcurrentDictionary<String, Byte>  refused     = new();

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("signers");
            station    = TestStations.New(directory, TestStations.Offline);
            csms       = new TalkingToTheCSMSTests.CSMS();
        }

        [TearDown]
        public async Task TakeItAwayAgain()
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


        #region (private) Connected()

        /// <summary>
        /// The station started, connected to this test's CSMS over OCPP 2.1,
        /// and registered with it.
        /// </summary>
        private async Task Connected()
        {

            Assert.That(station!.Connections.TryAddConnection("A CSMS of OCPP 2.1", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: "2.1"),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.DialledConnections[id!].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected: {started.DialledConnections[id!]}");

            Assert.That(await Within(() => started.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        "the station did not register");

        }

        #endregion

        #region (private) Enforced(Roles)

        /// <summary>
        /// The policy enforced, and a key for each of the given roles, which the
        /// station's OCPP node says are the keys of that role.
        /// </summary>
        private Dictionary<String, ECCKeyPair> Enforced(params String[] Roles)
        {

            var keys = Roles.ToDictionary(role => role, role => ECCKeyPair.GenerateKeys()!);

            foreach (var (role, key) in keys)
                station!.OCPPv2_1Node.UserRoles.Add(new UserRole(Id: UserRole_Id.Parse(role), KeyPairs: [ key ]));

            station!.OCPPv2_1Node.OCPP.SignaturePolicy.Mode = MessagePolicyModes.Enforce;
            station!.OCPPv2_1Node.OCPP.SignaturePolicy.OnPolicyViolation += violations.Enqueue;

            return keys;

        }

        #endregion

        #region (private) Ask(Action, Make, ToJSON, Signer)

        /// <summary>
        /// Send the station a request as the CSMS does, signed by the given key,
        /// and wait for its answer: a CALLRESULT or a CALLERROR.
        /// </summary>
        private async Task<JArray> Ask<T>(String                                Action,
                                          Func<SourceRouting, Request_Id, T>    Make,
                                          Func<T, Boolean, JObject>             ToJSON,
                                          ECCKeyPair?                           Signer)

            where T : IRequest

        {

            var id       = Guid.NewGuid().ToString();
            var request  = Make(SourceRouting.To(station!.OCPPv2_1Node.Id), Request_Id.Parse(id));

            if (Signer is not null)
                Assert.That(new SignaturePolicy().BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv2_1.Version.WebSocketSubProtocolId).
                                SignRequestMessage(request, ToJSON(request, true), out var notSigned,
                                                   Signer.ToSignInfo1("CSMS", I18NString.Create(Action), DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            var before = violations.Count;

            await csms!.Ask(new JArray(2, id, Action, ToJSON(request, false)).ToString(Formatting.None));

            var answer = await csms.Sent(message => message[0].Value<Int32>() is 3 or 4 &&
                                                    message[1].Value<String>() == id);

            Assert.That(answer, Is.Not.Null, $"the station did not answer '{Action}'");

            // Refused by the policy, rather than by a handler that saw it: the
            // policy said so, and nothing was accepted.
            if (violations.Count > before)
                refused[id] = 0;

            return answer!;

        }

        #endregion

        #region (private) Reset / Unlock / Trigger / ChangeAvailability / UpdateFirmware

        private Task<JArray> Reset(ResetType Type, ECCKeyPair? Signer)

            => Ask("Reset",
                   (destination, id) => new ResetRequest(destination, Type, RequestId: id),
                   (request, context) => request.ToJSON(context),
                   Signer);

        private Task<JArray> Unlock(ECCKeyPair? Signer)

            => Ask("UnlockConnector",
                   (destination, id) => new UnlockConnectorRequest(destination, EVSE_Id.Parse(1), Connector_Id.Parse(1), RequestId: id),
                   (request, context) => request.ToJSON(context),
                   Signer);

        private Task<JArray> Trigger(MessageTrigger Message, ECCKeyPair? Signer)

            => Ask("TriggerMessage",
                   (destination, id) => new TriggerMessageRequest(destination, Message, RequestId: id),
                   (request, context) => request.ToJSON(context),
                   Signer);

        private Task<JArray> TakeOutOfService(ECCKeyPair? Signer)

            => Ask("ChangeAvailability",
                   (destination, id) => new ChangeAvailabilityRequest(destination, OperationalStatus.Inoperative, RequestId: id),
                   (request, context) => request.ToJSON(context),
                   Signer);

        private Task<JArray> UpdateFirmware(ECCKeyPair? Signer)

            => Ask("UpdateFirmware",
                   (destination, id) => new UpdateFirmwareRequest(destination,
                                                                  new Firmware(URL.Parse("https://firmware.example.org/station.bin"), DateTimeOffset.UtcNow),
                                                                  1,
                                                                  RequestId: id),
                   (request, context) => request.ToJSON(context),
                   Signer);

        #endregion

        #region (private) Refused(Answer) / (private static) Within(Condition)

        /// <summary>
        /// Whether the policy refused a request: it said so, and the station
        /// answered that it did nothing - "Rejected", as a request whose
        /// signature does not hold is answered - rather than handling it.
        /// </summary>
        private Boolean Refused(JArray Answer)
        {

            var said = Answer[0].Value<Int32>() == 4 || Answer[2]?["status"]?.Value<String>() == "Rejected";

            if (refused.ContainsKey(Answer[1].Value<String>()!))
            {
                Assert.That(said, Is.True, $"refused, and answered {Answer.ToString(Formatting.None)}");
                return true;
            }

            return false;

        }

        private static async Task<Boolean> Within(Func<Boolean> Condition)
        {

            var watch = Stopwatch.StartNew();

            while (!Condition())
            {

                if (watch.Elapsed > TalkingToTheCSMSTests.Patience)
                    return false;

                await Task.Delay(20);

            }

            return true;

        }

        #endregion


        #region TheSupportIsTheNodesAndLetsGoOfACable()

        /// <summary>
        /// The station's support may do what the node's may - see what is going
        /// on, have it reported again, restart once idle - and let go of a stuck
        /// cable; the operator may restart at once, ask for any message, update
        /// the firmware and let go of a cable too, and the installer inherits it.
        /// </summary>
        [Test]
        public void TheSupportIsTheNodesAndLetsGoOfACable()
        {

            String[] PermissionsOf(String Role)
                => [.. station!.Access.RoleNamed(Role)!.Permissions.Select(permission => permission.ToString())];

            Assert.Multiple(() => {

                Assert.That(PermissionsOf("support"),    Is.EquivalentTo(new[] { "state:read", "state:run", "reset:run", "unlock:run" }));

                Assert.That(PermissionsOf("cpo"),        Is.SupersetOf  (new[] { "node:run", "reset:run", "reset-immediate:run",
                                                                                 "state:run", "firmware:edit", "unlock:run", "monitoring:edit", "authorization:edit" }));

                Assert.That(PermissionsOf("installer"),  Is.SupersetOf  (PermissionsOf("cpo")));

            });

        }

        #endregion

        #region TheSupportRestartsAnIdleStationAndNothingMore()

        /// <summary>
        /// A request the support signed: a restart once idle, a cable let go of
        /// and the EVSE status reported again are handled; a restart at once,
        /// an EVSE taken out of service and any other message asked for are
        /// refused - as the support may not do them on the page either.
        /// </summary>
        [Test]
        public async Task TheSupportRestartsAnIdleStationAndNothingMore()
        {

            await Connected();

            var support = Enforced("support")["support"];

            var unlocked  = await Unlock          (                                    support);
            var status    = await Trigger         (MessageTrigger.StatusNotification,  support);
            var atOnce    = await Reset           (ResetType.Immediate,                support);
            var inop      = await TakeOutOfService(                                    support);
            var boot      = await Trigger         (MessageTrigger.BootNotification,    support);

            // Last: it is a restart, and what the test set on the nodes goes with them.
            var onceIdle  = await Reset           (ResetType.OnIdle,                   support);

            Assert.Multiple(() => {
                Assert.That(Refused(onceIdle),  Is.False, "a restart once idle");
                Assert.That(Refused(unlocked),  Is.False, "a cable let go of");
                Assert.That(Refused(status),    Is.False, "the EVSE status reported again");
                Assert.That(Refused(atOnce),    Is.True,  "a restart at once");
                Assert.That(Refused(inop),      Is.True,  "an EVSE taken out of service");
                Assert.That(Refused(boot),      Is.True,  "another message asked for");
            });

        }

        #endregion

        #region TheFirmwareOfAMetrologicallyRelevantStationIsTheInstallers()

        /// <summary>
        /// The operator restarts at once and updates the firmware - until the
        /// station runs under a calibration certificate: then a firmware update
        /// needs "calibration:edit" too, which the installer has and the
        /// operator has not. That the station is relevant is the station's to
        /// say, and not the request's.
        /// </summary>
        [Test]
        public async Task TheFirmwareOfAMetrologicallyRelevantStationIsTheInstallers()
        {

            await Connected();

            var keys = Enforced("cpo", "installer");

            Assert.That(station!.IsMetrologicallyRelevant, Is.False);

            var firmware  = await UpdateFirmware(keys["cpo"]);

            Assert.That(Refused(firmware), Is.False, "the operator updates the firmware");

            using var key          = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request            = new CertificateRequest("CN=A Calibration Of This Station", key, HashAlgorithmName.SHA256);
            using var certificate  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            Assert.That(station.TryUpdateCalibrationConfiguration(new JObject(
                                                                      new JProperty("certificates", new JArray(
                                                                          new JObject(
                                                                              new JProperty("id",   "calibration-1"),
                                                                              new JProperty("pem",  certificate.ExportCertificatePem())
                                                                          )
                                                                      ))
                                                                  ),
                                                                  out var error,
                                                                  out _),
                        Is.True, error);

            Assert.That(station.IsMetrologicallyRelevant, Is.True);

            var byTheOperator   = await UpdateFirmware(keys["cpo"]);
            var byTheInstaller  = await UpdateFirmware(keys["installer"]);

            Assert.Multiple(() => {
                Assert.That(Refused(byTheOperator),   Is.True,  "the operator updates the firmware of a relevant station");
                Assert.That(Refused(byTheInstaller),  Is.False, "the installer updates the firmware of a relevant station");
            });

            // Asked last: the station restarts at once, and hangs up.
            Assert.That(Refused(await Reset(ResetType.Immediate, keys["cpo"])), Is.False, "the operator restarts at once");

        }

        #endregion

        #region APinSetIsLeftToItsSigners()

        /// <summary>
        /// A pin set is signed by a key of the CSMS that holds no role: the
        /// policy leaves it to the pin set signers of its connection, which the
        /// station holds it to itself, rather than refusing what no role
        /// signed. Any other entry such a key signed is refused.
        /// </summary>
        [Test]
        public async Task APinSetIsLeftToItsSigners()
        {

            await Connected();

            Enforced();

            var signer   = ECCKeyPair.GenerateKeys()!;
            var request  = new SetVariablesRequest(
                               SourceRouting.To(station!.OCPPv2_1Node.Id),
                               [
                                   new SetVariableData(new Component("SSHClientController", "csms"), new Variable("HostKeyPins"),         "AAAA"),
                                   new SetVariableData(new Component("DNSClientController"),         new Variable("NameServerAddresses"), "192.0.2.53")
                               ],
                               RequestId: Request_Id.Parse(Guid.NewGuid().ToString())
                           );

            Assert.That(new SignaturePolicy().BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv2_1.Version.WebSocketSubProtocolId).
                            SignRequestMessage(request, request.ToJSON(true), out var notSigned,
                                               signer.ToSignInfo1("CSMS", I18NString.Create("pin set"), DateTimeOffset.UtcNow)),
                        Is.True, notSigned);

            // Verified as the node verifies what it receives, so that the
            // signature is one the policy counts.
            var policy = station.OCPPv2_1Node.OCPP.SignaturePolicy;

            Assert.That(policy.VerifyRequestMessage(request, request.ToJSON(true), out var notVerified), Is.True, notVerified);

            Assert.Multiple(() => {
                Assert.That(policy.DecideVariable(request, "SSHClientController", "HostKeyPins",         "csms", null, "edit", out var pinSet),  Is.True,  pinSet);
                Assert.That(policy.DecideVariable(request, "DNSClientController",  "NameServerAddresses", null,   null, "edit", out var dns),     Is.False, "a name server set by no role");
            });

        }

        #endregion

        #region TheMonitoringIsTheOperators()

        /// <summary>
        /// The monitoring of the station as a whole is a right of its own,
        /// "monitoring": the operator sets its level, and the support, who
        /// sets nothing up, does not.
        /// </summary>
        [Test]
        public async Task TheMonitoringIsTheOperators()
        {

            await Connected();

            var keys = Enforced("cpo", "support");

            Task<JArray> SetLevel(ECCKeyPair Signer)

                => Ask("SetMonitoringLevel",
                       (destination, id) => new SetMonitoringLevelRequest(destination, Severities.Danger, RequestId: id),
                       (request, context) => request.ToJSON(context),
                       Signer);

            var byTheOperator  = await SetLevel(keys["cpo"]);
            var byTheSupport   = await SetLevel(keys["support"]);

            Assert.Multiple(() => {
                Assert.That(Refused(byTheOperator),  Is.False, "the operator sets the level of the monitoring");
                Assert.That(Refused(byTheSupport),   Is.True,  "the support sets the level of the monitoring");
            });

        }

        #endregion

        #region TheAuthorizationIsTheOperators()

        /// <summary>
        /// The local authorization list and the authorization cache are a
        /// right of their own, "authorization": the operator clears the cache,
        /// and the support, who sets nothing up, does not.
        /// </summary>
        [Test]
        public async Task TheAuthorizationIsTheOperators()
        {

            await Connected();

            var keys = Enforced("cpo", "support");

            Task<JArray> Clear(ECCKeyPair Signer)

                => Ask("ClearCache",
                       (destination, id) => new ClearCacheRequest(destination, RequestId: id),
                       (request, context) => request.ToJSON(context),
                       Signer);

            var byTheOperator  = await Clear(keys["cpo"]);
            var byTheSupport   = await Clear(keys["support"]);

            Assert.Multiple(() => {
                Assert.That(Refused(byTheOperator),  Is.False, "the operator clears the authorization cache");
                Assert.That(Refused(byTheSupport),   Is.True,  "the support clears the authorization cache");
            });

        }

        #endregion

        #region ARestartKeepsWhoMayDoWhat()

        /// <summary>
        /// A restart builds the OCPP nodes anew, and the new ones are held to
        /// what the old ones were: the policy is still enforced, and a key
        /// still has the role it had. Otherwise the support's restart would
        /// leave a station that takes everything from everybody.
        /// </summary>
        [Test]
        public async Task ARestartKeepsWhoMayDoWhat()
        {

            await Connected();

            var support  = Enforced("support")["support"];
            var boots    = csms!.Requests("BootNotification").Length;

            Assert.That(Refused(await Reset(ResetType.OnIdle, support)), Is.False, "a restart once idle");

            Assert.That(await Within(() => csms.Requests("BootNotification").Length > boots &&
                                           csms.Connections == 1 &&
                                           station!.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"),
                        Is.True, "the station did not restart");

            var atOnce    = await Reset (ResetType.Immediate, support);
            var unlocked  = await Unlock(support);

            Assert.Multiple(() => {
                Assert.That(atOnce  [0].Value<Int32>(), Is.EqualTo(4), $"a restart at once by the support, after a restart: {atOnce.ToString(Formatting.None)}");
                Assert.That(unlocked[0].Value<Int32>(), Is.EqualTo(3), $"a cable let go of by the support, after a restart: {unlocked.ToString(Formatting.None)}");
            });

        }

        #endregion

        #region TheSSHPeersAreTheConnections_AndAnybodysWhileThePolicyIsOff()

        /// <summary>
        /// The SSH peers of the station - who a host is believed by, and where
        /// its files may go - are a component like every other to the policy.
        /// While it is off a request nobody signed writes them, as a CSMS that
        /// signs nothing has to. Enforced, they are the connections': the
        /// operator, who may edit those, writes a peer and what it may be used
        /// for, and neither the support nor a request nobody signed does.
        /// </summary>
        [Test]
        public async Task TheSSHPeersAreTheConnections_AndAnybodysWhileThePolicyIsOff()
        {

            await Connected();

            Task<JArray> Peer(String Name, ECCKeyPair? Signer, params (String Variable, String Value)[] Further)

                => Ask("SetVariables",
                       (destination, id) => new SetVariablesRequest(
                                                destination,
                                                [
                                                    new SetVariableData(new Component("SSHPeerController"),        new Variable("Peers"),       String.Join(",", station!.ConfiguredSSHPeers.Select(peer => peer.Id).Append(Name).Distinct())),
                                                    new SetVariableData(new Component("SSHPeerController", Name),  new Variable("Address"),     $"{Name}.example.org"),
                                                    new SetVariableData(new Component("SSHPeerController", Name),  new Variable("HostKeyPins"), "SHA256:" + new String('A', 43)),
                                                    .. Further.Select(further => new SetVariableData(new Component("SSHPeerController", Name), new Variable(further.Variable), further.Value))
                                                ],
                                                DataConsistencyModel.ACID,
                                                RequestId: id
                                            ),
                       (request, context) => request.ToJSON(context),
                       Signer);

            static IEnumerable<String?> Statuses(JArray Answer)
                => Answer[2]?["setVariableResult"]?.Select(result => result["attributeStatus"]?.Value<String>()) ?? [];

            // The policy decides SetVariables entry by entry: what it refuses is
            // an entry that is "Rejected" as "NotAuthorized", not a request that fails.
            static IEnumerable<String?> Reasons(JArray Answer)
                => Answer[2]?["setVariableResult"]?.Select(result => result["attributeStatusInfo"]?["reasonCode"]?.Value<String>()) ?? [];

            // Off, as it is unless somebody sets another: nobody signed, and it is written.
            Assert.That(station!.OCPPv2_1Node.OCPP.SignaturePolicy.Mode, Is.EqualTo(MessagePolicyModes.Off));

            var whileOff = await Peer("time", null);

            Assert.Multiple(() => {
                Assert.That(Statuses(whileOff),                                   Is.Not.Empty.And.All.EqualTo("Accepted"), "the policy is off, and a peer nobody signed was not written: " + whileOff.ToString(Formatting.None));
                Assert.That(station.ConfiguredSSHPeers.Select(peer => peer.Id),   Is.EqualTo(new[] { "time" }));
            });

            var keys = Enforced("cpo", "support");

            var byNobody       = await Peer("names", null);
            var byTheSupport   = await Peer("names", keys["support"]);

            Assert.Multiple(() => {
                Assert.That(Statuses(byNobody),                                   Is.Not.Empty.And.All.EqualTo("Rejected"),       "enforced, a peer nobody signed was written: " + byNobody.ToString(Formatting.None));
                Assert.That(Reasons (byNobody),                                   Is.All.EqualTo("NotAuthorized"),                byNobody.ToString(Formatting.None));
                Assert.That(Statuses(byTheSupport),                               Is.Not.Empty.And.All.EqualTo("Rejected"),       "the support writes a peer: " + byTheSupport.ToString(Formatting.None));
                Assert.That(Reasons (byTheSupport),                               Is.All.EqualTo("NotAuthorized"),                byTheSupport.ToString(Formatting.None));
                Assert.That(byNobody.    ToString(Formatting.None),               Does.Contain("connections:edit"),               "the peers are not held to the right of the connections");
                Assert.That(byTheSupport.ToString(Formatting.None),               Does.Contain("connections:edit"),               "the peers are not held to the right of the connections");
                Assert.That(station.ConfiguredSSHPeers.Select(peer => peer.Id),   Is.EqualTo(new[] { "time" }), "a refused request made a peer");
            });

            var byTheOperator  = await Peer("files", keys["cpo"], ("FileTransfers", "Upload"), ("UploadPaths", "/upload/{id}/"));

            Assert.Multiple(() => {
                Assert.That(Statuses(byTheOperator),                              Is.Not.Empty.And.All.EqualTo("Accepted"), "the operator, who may edit the connections, writes no peer: " + byTheOperator.ToString(Formatting.None));
                Assert.That(station.ConfiguredSSHPeers.Select(peer => peer.Id),   Is.EqualTo(new[] { "time", "files" }));
                Assert.That(station.FileServers.Select(server => server.Host),    Is.EqualTo(new[] { "files.example.org" }));
            });

        }

        #endregion

        #region WhileThePolicyIsOffNothingIsAsked()

        /// <summary>
        /// The mode of the policy is "Off" unless somebody sets another: then a
        /// request nobody signed is handled as before.
        /// </summary>
        [Test]
        public async Task WhileThePolicyIsOffNothingIsAsked()
        {

            await Connected();

            Assert.That(station!.OCPPv2_1Node.OCPP.SignaturePolicy.Mode, Is.EqualTo(MessagePolicyModes.Off));

            Assert.That(Refused(await Reset(ResetType.Immediate, null)), Is.False);

        }

        #endregion

    }

}
