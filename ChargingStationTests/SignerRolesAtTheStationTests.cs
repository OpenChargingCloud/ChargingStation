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

using System.Net;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP;

using cloud.charging.open.ChargingStation.CommandLine;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The signer roles at the station itself: the page's JSON API and the
    /// console command write through what the OCPP messages write through,
    /// so that a role assignment read at the station is checked as one
    /// received over OCPP is - the first owner being the one exception
    /// (white paper "Signed OCPP Messages and Signer Roles", section 6.6).
    /// </summary>
    [TestFixture]
    public class SignerRolesAtTheStationTests : AChargingStationTests
    {

        #region Data

        private ECCKeyPair owner     = null!;
        private ECCKeyPair support   = null!;
        private ECCKeyPair stranger  = null!;

        private String     files     = "";

        [SetUp]
        public void Keys()
        {

            owner     = ECCKeyPair.GenerateKeys()!;
            support   = ECCKeyPair.GenerateKeys()!;
            stranger  = ECCKeyPair.GenerateKeys()!;

            files     = TestStations.TemporaryDirectory("signer-role-files");

            System.IO.Directory.CreateDirectory(files);

        }

        [TearDown]
        public void RemoveTheFiles()
            => TestStations.Remove(files);

        #endregion

        #region (private) Assignment / Signed / Post / File / SignerRoles

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

        private static async Task<(HttpStatusCode Status, JObject Body)> Send(HttpClient HTTP, HttpMethod Method, String Path, JObject JSON)
        {

            using var request   = new HttpRequestMessage(Method, Path) {
                                      Content = new StringContent(JSON.ToString(Formatting.None), Encoding.UTF8, "application/json")
                                  };

            using var response  = await HTTP.SendAsync(request);
            var       text      = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 ? SignedDataObject.ParseAsReceived(text) : []);

        }

        private static Task<(HttpStatusCode Status, JObject Body)> Post(HttpClient HTTP, String Path, JObject JSON)
            => Send(HTTP, HttpMethod.Post, Path, JSON);

        private String AFile(String Name, JObject JSON)
        {

            var path = Path.Combine(files, Name);

            File.WriteAllText(path, JSON.ToString(Formatting.Indented));

            return path;

        }

        private Task<String[]> SignerRoles(params String[] Arguments)
            => new SignerRolesCommand(new StationCLI(Station)).Execute([ SignerRolesCommand.CommandName, .. Arguments ], CancellationToken.None);

        #endregion


        #region ThePageWritesThroughWhatOCPPWritesThrough()

        /// <summary>
        /// The first owner from its public key, a role assignment the owner
        /// signed, the mode of the policy, a policy table, and the way out:
        /// over the JSON API of the page.
        /// </summary>
        [Test]
        public async Task ThePageWritesThroughWhatOCPPWritesThrough()
        {

            using var http = await SignedIn();

            const String api = "/api/v1/configuration/signatures";

            var atFirst        = await GetJSON(http, api);

            var forTheSupport  = Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, owner);

            var tooEarly       = await Post(http, api + "/roles",            forTheSupport);
            var aPrivateKey    = await Post(http, api + "/roles/firstOwner", new JObject(new JProperty("jwk", new JObject(JWS.PublicKeyJWK(owner.PublicKey).Properties().Append(new JProperty("d", "AAAA"))))));
            var theOwner       = await Post(http, api + "/roles/firstOwner", new JObject(new JProperty("jwk", JWS.PublicKeyJWK(owner.PublicKey)), new JProperty("label", "The operator's key")));
            var aSecondOwner   = await Post(http, api + "/roles/firstOwner", new JObject(new JProperty("jwk", JWS.PublicKeyJWK(stranger.PublicKey))));

            var byAStranger    = await Post(http, api + "/roles",            Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, stranger));
            var added          = await Post(http, api + "/roles",            forTheSupport);
            var again          = await Post(http, api + "/roles",            forTheSupport);
            var noSuchRole     = await Post(http, api + "/roles",            Signed(Assignment("nobody", 1, support), SignedDataObject.SignerRoleType, owner));

            var noMode         = await Send(http, HttpMethod.Put, api + "/settings", new JObject(new JProperty("mode", "Sometimes")));
            var enforced       = await Send(http, HttpMethod.Put, api + "/settings", new JObject(new JProperty("mode", "Enforce"), new JProperty("quorum", 1)));

            var aPolicy        = Signed(new JObject(
                                            new JProperty("version", 1),
                                            new JProperty("entries", new JArray(new MessagePolicyEntry("two for a reset", "Reset", SignerRequirement.AnyOf([ "support" ], 2)).ToJSON()))
                                        ),
                                        SignedDataObject.SignaturePolicyType,
                                        owner);

            var policySet      = await Post(http, api + "/policy",           aPolicy);

            var tooLate        = await Post(http, api + "/roles/remove",     new JObject(new JProperty("role", "support"),     new JProperty("version", 7)));
            var theLastOwner   = await Post(http, api + "/roles/remove",     new JObject(new JProperty("role", "systemadmin"), new JProperty("version", 1)));
            var removed        = await Post(http, api + "/roles/remove",     new JObject(new JProperty("role", "support"),     new JProperty("version", 1)));

            var notConfirmed   = await Post(http, api + "/reset",            []);
            var reset          = await Post(http, api + "/reset",            new JObject(new JProperty("confirm", "reset")));

            JToken? Role(JObject Said, String Name)
                => Said["roles"]?.FirstOrDefault(role => role["role"]?.Value<String>() == Name);

            Assert.Multiple(() => {

                Assert.That(atFirst["hasOwner"]?.Value<Boolean>(),                 Is.False);
                Assert.That(atFirst["settings"]?["mode"]?.Value<String>(),         Is.EqualTo("Off"));
                Assert.That(atFirst["roles"]?.Select(role => role["role"]?.Value<String>()), Does.Contain("systemadmin").And.Contain("support"));

                Assert.That(tooEarly.    Status,   Is.EqualTo(HttpStatusCode.Forbidden),    "A station without an owner took a role assignment from its page!");
                Assert.That(aPrivateKey. Status,   Is.EqualTo(HttpStatusCode.BadRequest),   "A private key was taken!");
                Assert.That(theOwner.    Status,   Is.EqualTo(HttpStatusCode.Created),      theOwner.Body.ToString(Formatting.None));
                Assert.That(Role(theOwner.Body, "systemadmin")?["keys"]?[0]?["kid"]?.Value<String>(),    Is.EqualTo(JWS.Thumbprint(owner.PublicKey)));
                Assert.That(Role(theOwner.Body, "systemadmin")?["keys"]?[0]?["label"]?.Value<String>(),  Is.EqualTo("The operator's key"));
                Assert.That(aSecondOwner.Status,   Is.EqualTo(HttpStatusCode.Forbidden),    "A second first owner was made at the page!");

                // Checked exactly as one received over OCPP.
                Assert.That(byAStranger. Status,   Is.EqualTo(HttpStatusCode.Forbidden),    "An assignment a stranger signed was taken from the page!");
                Assert.That(byAStranger. Body["description"]?.Value<String>() ?? byAStranger.Body.ToString(), Does.Contain("NotAuthorized"));
                Assert.That(added.       Status,   Is.EqualTo(HttpStatusCode.Created),      added.Body.ToString(Formatting.None));
                Assert.That(JToken.DeepEquals(Role(added.Body, "support")?["assignment"], forTheSupport), Is.True, "The assignment is not shown as it was read.");
                Assert.That(again.       Status,   Is.EqualTo(HttpStatusCode.Conflict),     "The same version was taken twice!");
                Assert.That(noSuchRole.  Status,   Is.EqualTo(HttpStatusCode.NotFound));

                Assert.That(noMode.      Status,   Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(enforced.    Status,   Is.EqualTo(HttpStatusCode.OK),           enforced.Body.ToString(Formatting.None));
                Assert.That(enforced.    Body["settings"]?["mode"]?.Value<String>(),        Is.EqualTo("Enforce"));

                Assert.That(policySet.   Status,   Is.EqualTo(HttpStatusCode.Created),      policySet.Body.ToString(Formatting.None));
                Assert.That(policySet.   Body["policyVersion"]?.Value<Int32>(),             Is.EqualTo(1));
                Assert.That(JToken.DeepEquals(policySet.Body["policy"], aPolicy),           Is.True);

                Assert.That(tooLate.     Status,   Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(theLastOwner.Status,   Is.EqualTo(HttpStatusCode.Conflict),     "The last owner was removed at the page: that is what the reset is for.");
                Assert.That(removed.     Status,   Is.EqualTo(HttpStatusCode.OK),           removed.Body.ToString(Formatting.None));
                Assert.That(Role(removed.Body, "support")?["keys"]?.Count(),                Is.Zero);

                Assert.That(notConfirmed.Status,   Is.EqualTo(HttpStatusCode.BadRequest),   "Every key was forgotten without being asked twice!");
                Assert.That(reset.       Status,   Is.EqualTo(HttpStatusCode.OK));
                Assert.That(reset.       Body["hasOwner"]?.Value<Boolean>(),                Is.False);
                Assert.That(reset.       Body["policy"],                                    Is.Null);

                // The policy table was the one of both nodes, and is the one of neither now.
                Assert.That(Station.OCPPv2_1Node.OCPP.SignaturePolicy.PolicyEntries.Select(entry => entry.Id), Has.None.EqualTo("two for a reset"));
                Assert.That(Station.OCPPv1_6Node.OCPP.SignaturePolicy.PolicyEntries.Select(entry => entry.Id), Has.None.EqualTo("two for a reset"));
                Assert.That(Station.OCPPv2_1Node.OCPP.SignaturePolicy.Mode,                 Is.EqualTo(MessagePolicyModes.Enforce));

            });

        }

        #endregion

        #region APolicyTable_IsTheOneOfBothNodes()

        /// <summary>
        /// A policy table the station is given is the one of its OCPP 2.1
        /// node and of its OCPP 1.6 node, whichever was asked; a later one
        /// replaces it in both.
        /// </summary>
        [Test]
        public void APolicyTable_IsTheOneOfBothNodes()
        {

            JObject APolicy(UInt64 Version, params String[] Ids)
                => Signed(new JObject(
                              new JProperty("version", Version),
                              new JProperty("entries", new JArray(Ids.Select(id => new MessagePolicyEntry(id, id, SignerRequirement.AnyOf([ "support" ], 2)).ToJSON())))
                          ),
                          SignedDataObject.SignaturePolicyType,
                          owner);

            IEnumerable<String> Of21() => Station.OCPPv2_1Node.OCPP.SignaturePolicy.PolicyEntries.Select(entry => entry.Id);
            IEnumerable<String> Of16() => Station.OCPPv1_6Node.OCPP.SignaturePolicy.PolicyEntries.Select(entry => entry.Id);

            Assert.That(Station.TryInstallFirstOwner(Assignment("systemadmin", 1, owner), out _, out var notInstalled), Is.True, notInstalled);

            var ofTheStation = Of21().ToArray();

            Assert.Multiple(() => {

                Assert.That(Station.TryImportSignaturePolicy(APolicy(1, "Reset", "UnlockConnector"), out _, out var e1), Is.True, e1);
                Assert.That(Of21(), Does.Contain("Reset").And.Contain("UnlockConnector"));
                Assert.That(Of16(), Does.Contain("Reset").And.Contain("UnlockConnector"), "The policy table is not the one of the OCPP 1.6 node.");

                Assert.That(Station.TryImportSignaturePolicy(APolicy(2, "TriggerMessage"), out _, out var e2), Is.True, e2);
                Assert.That(Of21(), Does.Contain("TriggerMessage").And.Not.Contain("Reset"));
                Assert.That(Of16(), Does.Contain("TriggerMessage").And.Not.Contain("Reset"), "The earlier policy table is still the one of the OCPP 1.6 node.");

                // What the station made of entries itself is still there.
                Assert.That(Of21(), Is.SupersetOf(ofTheStation));

            });

        }

        #endregion

        #region TheConsoleWritesThroughItToo()

        /// <summary>
        /// signerroles: the first owner and a role assignment from files,
        /// the mode, and what there is.
        /// </summary>
        [Test]
        public async Task TheConsoleWritesThroughItToo()
        {

            var atFirst      = await SignerRoles();
            var tooEarly     = await SignerRoles("import", AFile("support.json",  Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, owner)));
            var noFile       = await SignerRoles("owner",  Path.Combine(files, "there-is-none.json"));
            var theOwner     = await SignerRoles("owner",  AFile("owner.jwk",     JWS.PublicKeyJWK(owner.PublicKey)));
            var byAStranger  = await SignerRoles("import", AFile("stranger.json", Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, stranger)));
            var imported     = await SignerRoles("import", AFile("support.json",  Signed(Assignment("support", 1, support), SignedDataObject.SignerRoleType, owner)));
            var noMode       = await SignerRoles("mode",   "Sometimes");
            var enforced     = await SignerRoles("mode",   "Enforce");
            var listed       = await SignerRoles("list");
            var removed      = await SignerRoles("remove", "support", "1");
            var notYet       = await SignerRoles("reset");
            var reset        = await SignerRoles("reset", "reset");

            Assert.Multiple(() => {

                Assert.That(atFirst,                    Has.One.Contains("no owner yet"));
                Assert.That(tooEarly.Single(),          Does.StartWith("NotAuthorized"));
                Assert.That(noFile.Single(),            Does.Contain("cannot be read"));
                Assert.That(theOwner.Single(),          Does.Contain("first owner"));
                Assert.That(byAStranger.Single(),       Does.StartWith("NotAuthorized"));
                Assert.That(imported.Single(),          Does.Contain("'support'").And.Contain("version 1"));
                Assert.That(noMode.Single(),            Does.Contain("Off, Observe or Enforce"));
                Assert.That(enforced.Single(),          Does.Contain("Enforce"));

                Assert.That(listed,                     Has.One.Contains("'Enforce'"));
                Assert.That(listed,                     Has.One.Contains("The owner is the role 'systemadmin'"));
                Assert.That(listed,                     Has.One.StartsWith("support").And.Contains(JWS.Thumbprint(support.PublicKey)));
                Assert.That(listed,                     Has.Some.StartsWith("cpo").And.Contains("no keys"));

                Assert.That(removed.Single(),           Does.Contain("no keys any more"));
                Assert.That(notYet.Single(),            Does.Contain("reset reset"));
                Assert.That(Station.SignerRoles.HasOwner is false, Is.True, reset.Single());

            });

        }

        #endregion

    }

}
