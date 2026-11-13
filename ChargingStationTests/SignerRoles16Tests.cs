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
using cloud.charging.open.protocols.OCPPv1_6;
using cloud.charging.open.protocols.OCPPv1_6.CS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a signer of a request a Central System sends may have done to the
    /// station over OCPP 1.6: what the role of its key may do on the page, as
    /// over OCPP 2.1 (white paper "Signed OCPP Messages and Signer Roles",
    /// section 5.3, in the words of OCPP 1.6).
    /// </summary>
    /// <remarks>
    /// Asked over a connection to a Central System, as it asks, with the policy
    /// enforced: a request the policy refuses never reaches its handler.
    /// </remarks>
    [TestFixture]
    public class SignerRoles16Tests
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
            directory  = TestStations.TemporaryDirectory("signers16");
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
        /// The station started, connected to this test's Central System over
        /// OCPP 1.6, and registered with it.
        /// </summary>
        private async Task Connected()
        {

            Assert.That(station!.Connections.TryAddConnection("A Central System of OCPP 1.6", $"ws://127.0.0.1:{csms!.Port}/cs001", "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: "1.6"),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.DialledConnections[id!].StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"the station did not get connected: {started.DialledConnections[id!]}");

            Assert.That(await Within(() => started.RegistrationJSON()["1.6"]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        "the station did not register");

        }

        #endregion

        #region (private) Enforced(Roles)

        /// <summary>
        /// The policy enforced, and a key for each of the given roles, which the
        /// station's OCPP 1.6 node says are the keys of that role.
        /// </summary>
        private Dictionary<String, ECCKeyPair> Enforced(params String[] Roles)
        {

            var keys = Roles.ToDictionary(role => role, role => ECCKeyPair.GenerateKeys()!);

            foreach (var (role, key) in keys)
                station!.OCPPv1_6Node.UserRoles.Add(new UserRole(Id: UserRole_Id.Parse(role), KeyPairs: [ key ]));

            station!.OCPPv1_6Node.OCPP.SignaturePolicy.Mode = MessagePolicyModes.Enforce;
            station!.OCPPv1_6Node.OCPP.SignaturePolicy.OnPolicyViolation += violations.Enqueue;

            return keys;

        }

        #endregion

        #region (private) Ask(Action, Make, Signer)

        /// <summary>
        /// Send the station a request as the Central System does, signed by the
        /// given key, and wait for its answer: a CALLRESULT or a CALLERROR.
        /// </summary>
        private async Task<JArray> Ask<T>(String                                Action,
                                          Func<SourceRouting, Request_Id, T>    Make,
                                          ECCKeyPair?                           Signer)

            where T : IRequest

        {

            var id       = Guid.NewGuid().ToString();
            var request  = Make(SourceRouting.To(station!.OCPPv1_6Node.Id), Request_Id.Parse(id));

            JObject toJSON()
                => request switch {
                       ResetRequest                reset      => reset.     ToJSON(),
                       UnlockConnectorRequest      unlock     => unlock.    ToJSON(),
                       TriggerMessageRequest       trigger    => trigger.   ToJSON(),
                       ChangeAvailabilityRequest   available  => available. ToJSON(),
                       UpdateFirmwareRequest       firmware   => firmware.  ToJSON(),
                       ChangeConfigurationRequest  change     => change.    ToJSON(),
                       GetConfigurationRequest     get        => get.       ToJSON(),
                       ClearCacheRequest           clear      => clear.     ToJSON(),
                       _                                      => throw new ArgumentException($"A request of this test that it cannot write: {request.GetType().Name}")
                   };

            if (Signer is not null)
                Assert.That(new SignaturePolicy().BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv1_6.Version.WebSocketSubProtocolId).
                                SignRequestMessage(request, toJSON(), out var notSigned,
                                                   Signer.ToSignInfo1("Central System", I18NString.Create(Action), DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            var before = violations.Count;

            await csms!.Ask(new JArray(2, id, Action, toJSON()).ToString(Formatting.None));

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

        #region (private) Reset / Unlock / Trigger / TakeOutOfService / UpdateFirmware / ChangeKey / GetKeys

        private Task<JArray> Reset(ResetType Type, ECCKeyPair? Signer)

            => Ask("Reset",
                   (destination, id) => new ResetRequest(destination, Type, RequestId: id),
                   Signer);

        private Task<JArray> Unlock(ECCKeyPair? Signer)

            => Ask("UnlockConnector",
                   (destination, id) => new UnlockConnectorRequest(destination, Connector_Id.Parse(1), RequestId: id),
                   Signer);

        private Task<JArray> Trigger(MessageTrigger Message, ECCKeyPair? Signer)

            => Ask("TriggerMessage",
                   (destination, id) => new TriggerMessageRequest(destination, Message, RequestId: id),
                   Signer);

        private Task<JArray> TakeOutOfService(ECCKeyPair? Signer)

            => Ask("ChangeAvailability",
                   (destination, id) => new ChangeAvailabilityRequest(destination, Connector_Id.Parse(1), Availabilities.Inoperative, RequestId: id),
                   Signer);

        private Task<JArray> UpdateFirmware(ECCKeyPair? Signer)

            => Ask("UpdateFirmware",
                   (destination, id) => new UpdateFirmwareRequest(destination, URL.Parse("https://firmware.example.org/station.bin"), Timestamp.Now, RequestId: id),
                   Signer);

        private Task<JArray> ChangeKey(String Key, String Value, ECCKeyPair? Signer)

            => Ask("ChangeConfiguration",
                   (destination, id) => new ChangeConfigurationRequest(destination, Key, Value, RequestId: id),
                   Signer);

        private Task<JArray> GetKeys(ECCKeyPair? Signer, params String[] Keys)

            => Ask("GetConfiguration",
                   (destination, id) => new GetConfigurationRequest(destination, Keys, RequestId: id),
                   Signer);

        #endregion

        #region (private) Refused(Answer) / (private static) Shown(Answer, Key) / (private static) Within(Condition)

        /// <summary>
        /// Whether the policy refused a request: it said so, and the station
        /// did not answer that it did it.
        /// </summary>
        /// <remarks>
        /// OCPP 1.6 has no word for a refusal in every answer: an unlock that
        /// was refused is "UnlockFailed", and the answer to UpdateFirmware has
        /// nothing to say at all.
        /// </remarks>
        private Boolean Refused(JArray Answer)
        {

            var said = Answer[0].Value<Int32>() == 4 || Answer[2]?["status"]?.Value<String>() is not ("Accepted" or "Unlocked");

            if (refused.ContainsKey(Answer[1].Value<String>()!))
            {
                Assert.That(said, Is.True, $"refused, and answered {Answer.ToString(Formatting.None)}");
                return true;
            }

            return false;

        }

        /// <summary>
        /// Whether the answer to a GetConfiguration shows the given key, rather
        /// than calling it unknown.
        /// </summary>
        private static Boolean Shown(JArray Answer, String Key)
        {

            var shown    = (Answer[2]?["configurationKey"] as JArray ?? []).Any(entry => entry["key"]?.Value<String>() == Key);
            var unknown  = (Answer[2]?["unknownKey"]       as JArray ?? []).Any(entry => entry.Value<String>()           == Key);

            Assert.That(shown ^ unknown, Is.True, $"'{Key}' is to be either shown or unknown: {Answer.ToString(Formatting.None)}");

            return shown;

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


        #region TheSupportRestartsAnIdleStationAndNothingMore()

        /// <summary>
        /// A request the support signed: a soft reset - the restart once idle -,
        /// a cable let go of and the status reported again are handled; a hard
        /// reset - the restart at once -, a connector taken out of service and
        /// a BootNotification asked for are refused, as the support may not do
        /// them on the page either.
        /// </summary>
        [Test]
        public async Task TheSupportRestartsAnIdleStationAndNothingMore()
        {

            await Connected();

            var support = Enforced("support")["support"];

            var unlocked  = await Unlock          (                                    support);
            var status    = await Trigger         (MessageTrigger.StatusNotification,  support);
            var hard      = await Reset           (ResetType.Hard,                     support);
            var inop      = await TakeOutOfService(                                    support);
            var boot      = await Trigger         (MessageTrigger.BootNotification,    support);

            // Last: it is a restart, and what the test set on the nodes goes with them.
            var soft      = await Reset           (ResetType.Soft,                     support);

            Assert.Multiple(() => {
                Assert.That(Refused(soft),      Is.False, "a soft reset");
                Assert.That(Refused(unlocked),  Is.False, "a cable let go of");
                Assert.That(Refused(status),    Is.False, "the status reported again");
                Assert.That(Refused(hard),      Is.True,  "a hard reset");
                Assert.That(Refused(inop),      Is.True,  "a connector taken out of service");
                Assert.That(Refused(boot),      Is.True,  "a BootNotification asked for");
            });

        }

        #endregion

        #region TheFirmwareOfAMetrologicallyRelevantStationIsTheInstallers()

        /// <summary>
        /// The operator resets hard and updates the firmware - until the station
        /// runs under a calibration certificate: then a firmware update needs
        /// "calibration:edit" too, which the installer has and the operator has
        /// not. Over OCPP 1.6 as over OCPP 2.1, that the station is relevant is
        /// the station's to say.
        /// </summary>
        [Test]
        public async Task TheFirmwareOfAMetrologicallyRelevantStationIsTheInstallers()
        {

            await Connected();

            var keys = Enforced("cpo", "installer");

            Assert.That(station!.IsMetrologicallyRelevant, Is.False);

            var hard      = await Reset         (ResetType.Hard,  keys["cpo"]);
            var firmware  = await UpdateFirmware(                 keys["cpo"]);

            Assert.Multiple(() => {
                Assert.That(Refused(hard),      Is.False, "the operator resets hard");
                Assert.That(Refused(firmware),  Is.False, "the operator updates the firmware");
            });

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

        }

        #endregion

        #region AKeyIsShownToWhoMayReadIt()

        /// <summary>
        /// GetConfiguration and ChangeConfiguration are decided key by key: a
        /// key of the DNS client is "dns", which the operator may read and
        /// change, as on the page; the support, who may see what is going on
        /// but not how the station is set up, is told it is unknown, and may
        /// not change it.
        /// </summary>
        [Test]
        public async Task AKeyIsShownToWhoMayReadIt()
        {

            const String key = "dns.maxRetries";

            await Connected();

            var keys = Enforced("cpo", "support");

            var toTheOperator  = await GetKeys(keys["cpo"],      key);
            var toTheSupport   = await GetKeys(keys["support"],  key);

            Assert.Multiple(() => {
                Assert.That(Shown(toTheOperator, key), Is.True,  "the operator is shown a key of the DNS client");
                Assert.That(Shown(toTheSupport,  key), Is.False, "the support is shown a key of the DNS client");
            });

            // Written back as it is, so that only who may write it is asked.
            var value          = (toTheOperator[2]!["configurationKey"] as JArray)!.First(entry => entry["key"]?.Value<String>() == key)["value"]?.Value<String>() ?? "";

            var bySupport      = await ChangeKey(key, value, keys["support"]);
            var byTheOperator  = await ChangeKey(key, value, keys["cpo"]);

            Assert.Multiple(() => {
                Assert.That(bySupport    [2]?["status"]?.Value<String>(), Is.EqualTo("Rejected"), "the support changed a key of the DNS client");
                Assert.That(byTheOperator[2]?["status"]?.Value<String>(), Is.EqualTo("Accepted"), "the operator changed a key of the DNS client");
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

            Assert.That(Refused(await Reset(ResetType.Soft, support)), Is.False, "a restart once idle");

            Assert.That(await Within(() => csms.Requests("BootNotification").Length > boots &&
                                           csms.Connections == 1 &&
                                           station!.RegistrationJSON()["1.6"]?["state"]?.Value<String>() == "Accepted"),
                        Is.True, "the station did not restart");

            var atOnce    = await Reset (ResetType.Hard, support);
            var unlocked  = await Unlock(support);

            Assert.Multiple(() => {
                Assert.That(atOnce  [0].Value<Int32>(), Is.EqualTo(4), $"a restart at once by the support, after a restart: {atOnce.ToString(Formatting.None)}");
                Assert.That(unlocked[0].Value<Int32>(), Is.EqualTo(3), $"a cable let go of by the support, after a restart: {unlocked.ToString(Formatting.None)}");
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

            Assert.That(station!.OCPPv1_6Node.OCPP.SignaturePolicy.Mode, Is.EqualTo(MessagePolicyModes.Off));

            Assert.That(Refused(await Reset(ResetType.Hard, null)), Is.False);

        }

        #endregion

    }

}
