/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of Open Charging Station <https://github.com/OpenChargingCloud/ChargingStation>
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

using System.Diagnostics;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;
using cloud.charging.open.protocols.OCPPv2_1.NetworkingNode;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Which keys sign as which role of the station is written by signed
    /// role assignments - at the station, and over OCPP - and kept beside its
    /// configuration, with the policy table and the mode of the policy
    /// (white paper "Signed OCPP Messages and Signer Roles", chapter 6 and
    /// phase 4).
    /// </summary>
    [TestFixture]
    public class SignerRoleProvisioningTests
    {

        #region Data

        private String                        directory  = "";
        private ChargingStation?              station;
        private TalkingToTheCSMSTests.CSMS?   csms;

        private ECCKeyPair owner     = null!;
        private ECCKeyPair support   = null!;
        private ECCKeyPair stranger  = null!;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {

            directory  = TestStations.TemporaryDirectory("signer-roles");
            station    = TestStations.New(directory, TestStations.Offline);
            csms       = new TalkingToTheCSMSTests.CSMS();

            owner      = ECCKeyPair.GenerateKeys()!;
            support    = ECCKeyPair.GenerateKeys()!;
            stranger   = ECCKeyPair.GenerateKeys()!;

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

        #region (private) Assignment / Signed / Connected / Ask / Within

        private static JObject Assignment(String Role, UInt64 Version, params ECCKeyPair[] Keys)

            => SignerRoleAssignment.Create(
                   Role,
                   Version,
                   Keys.Select(key => (key.PublicKey, (String?) "a key", (DateTimeOffset?) null, (DateTimeOffset?) null))
               );

        private static JObject Signed(JObject Object, String Type, params ECCKeyPair[] By)
        {

            foreach (var key in By)
                SignedDataObject.Sign(Object, Type, key);

            return Object;

        }

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

        /// <summary>
        /// Send the station a request as the CSMS does, signed by the given key,
        /// and wait for its answer: a CALLRESULT or a CALLERROR.
        /// </summary>
        private async Task<JArray> Ask<T>(String                              Action,
                                          Func<SourceRouting, Request_Id, T>  Make,
                                          Func<T, JObject>                    ToJSON,
                                          ECCKeyPair?                         Signer)
            where T : IRequest
        {

            var id       = Guid.NewGuid().ToString();
            var request  = Make(SourceRouting.To(station!.OCPPv2_1Node.Id), Request_Id.Parse(id));

            if (Signer is not null)
                Assert.That(new SignaturePolicy().BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv2_1.Version.WebSocketSubProtocolId).
                                SignRequestMessage(request, ToJSON(request), out var notSigned,
                                                   Signer.ToSignInfo1("CSMS", I18NString.Create(Action), DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            await csms!.Ask(new JArray(2, id, Action, ToJSON(request)).ToString(Formatting.None));

            var answer = await csms.Sent(message => message[0].Value<Int32>() is 3 or 4 &&
                                                    message[1].Value<String>() == id);

            Assert.That(answer, Is.Not.Null, $"the station did not answer '{Action}'");

            return answer!;

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

        private Task<JArray> AddSignerRole(JObject SignerRole, ECCKeyPair? Signer)
            => Ask("AddSignerRole",
                   (destination, id) => new AddSignerRoleRequest(destination, SignerRole, RequestId: id),
                   request => request.ToJSON(),
                   Signer);

        // What the support may do, and what leaves the station as it is: a
        // restart would hang up on the CSMS that asks.
        private Task<JArray> Unlock(ECCKeyPair? Signer)
            => Ask("UnlockConnector",
                   (destination, id) => new UnlockConnectorRequest(destination, EVSE_Id.Parse(1), Connector_Id.Parse(1), RequestId: id),
                   request => request.ToJSON(),
                   Signer);

        private Task<JArray> SetTheMode(String Mode, ECCKeyPair? Signer)
            => Ask("SetVariables",
                   (destination, id) => new SetVariablesRequest(destination,
                                                                [ new SetVariableData(new Component("MessageSignatureController"), new Variable("Mode"), Mode) ],
                                                                RequestId: id),
                   request => request.ToJSON(),
                   Signer);

        private Task<JArray> GetVariables(ECCKeyPair? Signer, params (String Component, String? Instance, String Variable)[] Variables)
            => Ask("GetVariables",
                   (destination, id) => new GetVariablesRequest(destination,
                                                                Variables.Select(variable => new GetVariableData(new Component(variable.Component, variable.Instance), new Variable(variable.Variable))),
                                                                RequestId: id),
                   request => request.ToJSON(),
                   Signer);

        #endregion


        #region WhatTheStationIsGiven_IsThereAfterItIsStartedAgain()

        /// <summary>
        /// The first owner is made at the station. A role assignment the
        /// owner signed gives a role its keys, the mode of the policy is set -
        /// and all of it is kept beside the configuration file: a station
        /// made anew from the same directory knows its owner, its keys and
        /// its mode, and takes no version again.
        /// </summary>
        [Test]
        public async Task WhatTheStationIsGiven_IsThereAfterItIsStartedAgain()
        {

            var forTheSupport = Signed(Assignment("support", 3, support), SignedDataObject.SignerRoleType, owner);

            Assert.Multiple(() => {

                Assert.That(station!.SignerRoles.HasOwner, Is.False);

                Assert.That(station.TryImportSignerRole (forTheSupport, out var r1, out _), Is.False, "A station without an owner took a role assignment!");
                Assert.That(r1, Is.EqualTo(SignerRoleReasons.NotAuthorized));

                Assert.That(station.TryInstallFirstOwner(Assignment("support",     1, stranger), out var r2, out _), Is.False, "The support became the owner of the station!");
                Assert.That(r2, Is.EqualTo(SignerRoleReasons.NotAuthorized));

                Assert.That(station.TryInstallFirstOwner(Assignment("systemadmin", 1, owner),    out _,      out var e3), Is.True, e3);
                Assert.That(station.TryImportSignerRole (forTheSupport,                          out _,      out var e4), Is.True, e4);

                Assert.That(station.TryImportSignerRole (Signed(Assignment("no-such-role", 1, stranger), SignedDataObject.SignerRoleType, owner), out var r5, out _), Is.False, "A role the station does not have got keys!");
                Assert.That(r5, Is.EqualTo(SignerRoleReasons.UnknownRole));

                Assert.That(station.TryUpdateSignatureSettings(new JObject(new JProperty("mode", "Sometimes")),                                  out _, out _), Is.False, "A mode there is not was set!");
                Assert.That(station.TryUpdateSignatureSettings(new JObject(new JProperty("mode", "Enforce"), new JProperty("maxClockSkew", 60)), out var e6, out _), Is.True, e6);

                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.Mode,                  Is.EqualTo(MessagePolicyModes.Enforce), "The mode is not the one of the OCPP 2.1 node.");
                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.Mode,                  Is.EqualTo(MessagePolicyModes.Enforce), "The mode is not the one of the OCPP 1.6 node.");
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.MaxClockSkew,          Is.EqualTo(TimeSpan.FromSeconds(60)));

            });

            var file = Path.Combine(directory, ChargingStation.SignaturesDirectoryName, ChargingStation.SignerRolesFileName);

            Assert.That(File.Exists(file), Is.True, $"Nothing was written to '{file}'.");

            await station!.DisposeAsync();
            station = TestStations.New(directory, TestStations.Offline);

            Assert.Multiple(() => {

                Assert.That(station.SignerRoles.HasOwner,                                    Is.True,                               "The station forgot its owner.");
                Assert.That(station.SignerRoles.RolesOf(owner.  PublicKeyBytes),             Is.EqualTo(new[] { "systemadmin" }));
                Assert.That(station.SignerRoles.RolesOf(support.PublicKeyBytes),             Is.EqualTo(new[] { "support" }));
                Assert.That(station.SignerRoles.VersionOf("support"),                        Is.EqualTo(3));
                Assert.That(station.SignatureSettings.Mode,                                  Is.EqualTo(MessagePolicyModes.Enforce));
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.Mode,                  Is.EqualTo(MessagePolicyModes.Enforce), "The station forgot that its policy is enforced.");
                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.Mode,                  Is.EqualTo(MessagePolicyModes.Enforce));
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.MaxClockSkew,          Is.EqualTo(TimeSpan.FromSeconds(60)));

                // What the roles may do is what the station says of them, for both nodes.
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.PermissionsOfRole!("support"), Does.Contain("reset:run"));
                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.PermissionsOfRole!("support"), Does.Contain("reset:run"));

                Assert.That(station.TryImportSignerRole(Signed(Assignment("support", 3, stranger), SignedDataObject.SignerRoleType, owner), out var r1, out _), Is.False, "A version was taken again after the restart!");
                Assert.That(r1, Is.EqualTo(SignerRoleReasons.VersionNotNewer));

                var said = station.SignaturesJSON();

                Assert.That(said["hasOwner"]?.Value<Boolean>(),                              Is.True);
                Assert.That(said["settings"]?["mode"]?.Value<String>(),                      Is.EqualTo("Enforce"));
                Assert.That(said["roles"]!.Select(role => role["role"]!.Value<String>()),    Does.Contain("systemadmin").And.Contain("support").And.Contain("cpo"));
                Assert.That(JToken.DeepEquals(said["roles"]!.First(role => role["role"]!.Value<String>() == "support")["assignment"], forTheSupport),
                                                                                             Is.True,                               "The assignment is not shown as it was received.");

            });

            // The way out for a station whose owners lost their keys.
            station.ResetSignerRoles();

            Assert.Multiple(() => {
                Assert.That(station.SignerRoles.HasOwner,                           Is.False);
                Assert.That(station.SignerRoles.RolesOf(support.PublicKeyBytes),    Is.Empty);
            });

        }

        #endregion

        #region ARoleGetsItsKeysOverOCPP_AndTheCSMSIsToldOfIt()

        /// <summary>
        /// Asked as a CSMS asks: an AddSignerRole the owner signed gives the
        /// support its key, which lets go of a stuck cable from then on;
        /// the CSMS is told with a security event "RoleTableChanged"; the
        /// mode of the policy and the keys of a role are read from the
        /// device model by whoever may read the signatures, and the mode is
        /// written by the owner alone.
        /// </summary>
        [Test]
        public async Task ARoleGetsItsKeysOverOCPP_AndTheCSMSIsToldOfIt()
        {

            Assert.That(station!.TryInstallFirstOwner(Assignment("systemadmin", 1, owner), out _, out var notInstalled), Is.True, notInstalled);
            await Connected();

            Assert.That(station.TryUpdateSignatureSettings(new JObject(new JProperty("mode", "Enforce")), out var notSet, out _), Is.True, notSet);

            var forTheSupport  = Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, owner);

            var before         = await Unlock(support);
            var unsigned       = await AddSignerRole(forTheSupport, null);
            var byAStranger    = await AddSignerRole(forTheSupport, stranger);
            var added          = await AddSignerRole(forTheSupport, owner);
            var afterwards     = await Unlock(support);

            var told           = await csms!.Sent(message => message[0].Value<Int32>() == 2 &&
                                                             message[2].Value<String>() == "SecurityEventNotification" &&
                                                             message[3]?["type"]?.Value<String>() == "RoleTableChanged" &&
                                                            (message[3]?["techInfo"]?.Value<String>() ?? "").Contains("'support' got its keys"));

            var readByTheOwner    = await GetVariables(owner,
                                                       ("MessageSignatureController", null,      "Mode"),
                                                       ("MessageSignatureController", null,      "PolicyVersion"),
                                                       ("SignerRoleController",       "support", "KeyIds"),
                                                       ("SignerRoleController",       "support", "Version"),
                                                       ("SignerRoleController",       "support", "Permissions"));

            var readByTheSupport  = await GetVariables(support,
                                                       ("MessageSignatureController", null,      "Mode"));

            var setByTheSupport   = await SetTheMode("Observe", support);
            var modeThen          = station.SignatureSettings.Mode;
            var setByTheOwner     = await SetTheMode("Observe", owner);

            String? Value(JArray Answer, Int32 Index)
                => Answer[2]?["getVariableResult"]?[Index]?["attributeValue"]?.Value<String>();

            Assert.Multiple(() => {

                Assert.That(before.     First().Value<Int32>(),                 Is.EqualTo(4),   "A key without a role let go of a cable!");

                Assert.That(unsigned.   First().Value<Int32>(),                 Is.EqualTo(4),   "An AddSignerRole nobody signed was handled!");
                Assert.That(unsigned[2].Value<String>(),                        Is.EqualTo("SecurityError"));
                Assert.That(unsigned[3].Value<String>(),                        Does.Contain("signatures:edit"));
                Assert.That(byAStranger.First().Value<Int32>(),                 Is.EqualTo(4),   "An AddSignerRole a stranger signed was handled!");

                Assert.That(added.      First().Value<Int32>(),                 Is.EqualTo(3),   added.ToString(Formatting.None));
                Assert.That(added[2]?["status"]?.Value<String>(),               Is.EqualTo("Accepted"), added.ToString(Formatting.None));
                Assert.That(station.SignerRoles.RolesOf(support.PublicKeyBytes), Is.EqualTo(new[] { "support" }));

                Assert.That(afterwards. First().Value<Int32>(),                 Is.EqualTo(3),   $"The support did not let go of a cable: {afterwards.ToString(Formatting.None)}");

                Assert.That(told,                                               Is.Not.Null,     "The CSMS was not told that a role got its keys.");

                Assert.That(readByTheOwner.First().Value<Int32>(),              Is.EqualTo(3),   readByTheOwner.ToString(Formatting.None));
                Assert.That(Value(readByTheOwner, 0),                           Is.EqualTo("Enforce"));
                Assert.That(Value(readByTheOwner, 1),                           Is.EqualTo("0"));
                Assert.That(Value(readByTheOwner, 2),                           Is.EqualTo(JWS.Thumbprint(support.PublicKey)));
                Assert.That(Value(readByTheOwner, 3),                           Is.EqualTo("1"));
                Assert.That(Value(readByTheOwner, 4),                           Does.Contain("unlock:run"));

                // Who may let go of a cable may not read how the policy is set, nor set it.
                Assert.That(readByTheSupport[2]?["getVariableResult"]?[0]?["attributeStatus"]?.Value<String>(),   Is.EqualTo("Rejected"), readByTheSupport.ToString(Formatting.None));
                Assert.That(setByTheSupport [2]?["setVariableResult"]?[0]?["attributeStatus"]?.Value<String>(),   Is.EqualTo("Rejected"), setByTheSupport. ToString(Formatting.None));
                Assert.That(modeThen,                                           Is.EqualTo(MessagePolicyModes.Enforce));

                Assert.That(setByTheOwner   [2]?["setVariableResult"]?[0]?["attributeStatus"]?.Value<String>(),   Is.EqualTo("Accepted"), setByTheOwner.   ToString(Formatting.None));
                Assert.That(station.SignatureSettings.Mode,                     Is.EqualTo(MessagePolicyModes.Observe));
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.Mode,     Is.EqualTo(MessagePolicyModes.Observe));
                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.Mode,     Is.EqualTo(MessagePolicyModes.Observe));

            });

        }

        #endregion

        #region TheKeysOfOCPP16_SayWhatTheVariablesSay()

        /// <summary>
        /// A charge point of OCPP 1.6 has configuration keys for what the
        /// device model of OCPP 2.1 has variables for: "signatures.mode" is
        /// read and written, the keys of a role are read alone.
        /// </summary>
        [Test]
        public async Task TheKeysOfOCPP16_SayWhatTheVariablesSay()
        {

            Assert.That(station!.TryInstallFirstOwner(Assignment("systemadmin", 1, owner), out _, out var notInstalled), Is.True, notInstalled);
            Assert.That(station.TryImportSignerRole(Signed(Assignment("support", 2, support), SignedDataObject.SignerRoleType, owner), out _, out var notImported), Is.True, notImported);

            var keys = station.OCPPv1_6Node.ConfigurationKeySources.Single(source => source.Keys.Contains("signatures.mode"));

            var changed     = await keys.ChangeAsync("signatures.mode",                  "Enforce",   "a test");
            var noMode      = await keys.ChangeAsync("signatures.mode",                  "Sometimes", "a test");
            var readOnly    = await keys.ChangeAsync("signatures.policyVersion",         "7",         "a test");
            var ofARole     = await keys.ChangeAsync("signatures.roles.support.keyIds",  "",          "a test");
            var notMine     = await keys.ChangeAsync("dns.enabled",                      "true",      "a test");

            Assert.Multiple(() => {

                Assert.That(keys.Keys, Does.Contain("signatures.maxClockSkew").And.Contain("signatures.roles.support.version").And.Contain("signatures.roles.cpo.keyIds"));

                Assert.That(changed,    Is.EqualTo(protocols.OCPPv1_6.ConfigurationStatus.Accepted));
                Assert.That(noMode,     Is.EqualTo(protocols.OCPPv1_6.ConfigurationStatus.Rejected));
                Assert.That(readOnly,   Is.EqualTo(protocols.OCPPv1_6.ConfigurationStatus.Rejected));
                Assert.That(ofARole,    Is.EqualTo(protocols.OCPPv1_6.ConfigurationStatus.Rejected), "The keys of a role were written by a configuration key!");
                Assert.That(notMine,    Is.Null);

                Assert.That(keys.Get("signatures.mode")?.Value,                        Is.EqualTo("Enforce"));
                Assert.That(keys.Get("SIGNATURES.MODE")?.Value,                        Is.EqualTo("Enforce"));
                Assert.That(keys.Get("signatures.roles.support.keyIds")?.Value,        Is.EqualTo(JWS.Thumbprint(support.PublicKey)));
                Assert.That(keys.Get("signatures.roles.support.version")?.Value,       Is.EqualTo("2"));
                Assert.That(keys.Get("signatures.roles.cpo.keyIds")?.Value,            Is.Empty);
                Assert.That(keys.Get("signatures.roles.nobody.keyIds"),                Is.Null);

                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.Mode,            Is.EqualTo(MessagePolicyModes.Enforce));
                Assert.That(station.OCPPv2_1Node.OCPP.SignaturePolicy.Mode,            Is.EqualTo(MessagePolicyModes.Enforce));

                // "signatures.*" is "signatures" to the policy, as the controllers are.
                Assert.That(station.OCPPv1_6Node.OCPP.SignaturePolicy.ResourceOfComponent(protocols.OCPPv1_6.CP.ChargePointActionPermissions.ComponentOfKey("signatures.mode")),
                            Is.EqualTo("signatures"));

            });

        }

        #endregion

    }

}
