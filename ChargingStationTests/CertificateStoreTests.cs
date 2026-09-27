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

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;


#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The certificate store of this station's node, over the wire: what it
    /// keeps, what a root is for, and who may change any of it.
    /// </summary>
    /// <remarks>
    /// The store is WWCP_Node's, the same one a vehicle keeps, and its own tests
    /// are in WWCP_Node; what is asked here is what this station makes of it -
    /// the kinds it keeps and the ones it does not, and that the roots it
    /// believes are the administrators' to change. The three about usages are
    /// the vehicle's own, asked of the station.
    /// </remarks>
    public class CertificateStoreTests : AChargingStationTests
    {

        #region (helpers) RootPem(Name) / AsTheAdministrator() / Send(HTTP, Method, Path, JSON)

        /// <summary>
        /// A self-signed certificate, as the text of a PEM file base64-encoded -
        /// which is what an upload from the browser turns into.
        /// </summary>
        private static String RootPem(String Name)
        {

            using var key  = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request    = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

            return Convert.ToBase64String(Encoding.ASCII.GetBytes(root.ExportCertificatePem()));

        }

        /// <summary>
        /// The one account this station made, by its password: the
        /// administrators', until a test takes it out of their group.
        /// </summary>
        private HttpClient AsTheAdministrator()
        {

            var http = Anonymous();

            http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ChargingStation.DefaultAdminUser}:{Password}"))
                );

            return http;

        }

        private static async Task<(HttpStatusCode Status, JObject JSON)> Send(HttpClient  HTTP,
                                                                             HttpMethod  Method,
                                                                             String      Path,
                                                                             JObject?    JSON = null)
        {

            using var request   = new HttpRequestMessage(Method, Path);

            if (JSON is not null)
                request.Content = new StringContent(JSON.ToString(), Encoding.UTF8, "application/json");

            using var response  = await HTTP.SendAsync(request);
            var text            = await response.Content.ReadAsStringAsync();

            return (response.StatusCode, text.Length > 0 ? JObject.Parse(text) : new JObject());

        }

        #endregion


        #region TheStoreKeepsTheRootsAndTLSAndNothingOnlyAVehicleHolds()

        /// <summary>
        /// TLS's four kinds and the three roots Plug &amp; Charge checks a
        /// vehicle against - grouped as what the station believes, presents and
        /// recognises - and none of what only a vehicle holds.
        /// </summary>
        [Test]
        public async Task TheStoreKeepsTheRootsAndTLSAndNothingOnlyAVehicleHolds()
        {

            using var http          = AsTheAdministrator();

            var (status, store)     = await Send(http, HttpMethod.Get, "api/v1/certificates");
            var (refused, said)     = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "contract"),
                                                     new JProperty("content",  RootPem("Somebody's Contract"))
                                                 ));

            Assert.Multiple(() => {

                Assert.That(status,                                           Is.EqualTo(HttpStatusCode.OK), store.ToString());
                Assert.That(((JObject) store["kinds"]!).Properties().Select(kind => kind.Name),
                            Is.EqualTo(new[] { "v2gRoot", "moRoot", "oemRoot", "tlsRoot", "clientRoot", "tlsServer", "tlsIdentity" }));
                Assert.That(store["trustAnchors"]!.Values<String>(),          Is.EqualTo(new[] { "v2gRoot", "moRoot", "oemRoot", "tlsRoot", "clientRoot" }));
                Assert.That(store["credentials"]!.Values<String>(),           Is.EqualTo(new[] { "tlsIdentity" }));
                Assert.That(store["recognised"]!.Values<String>(),            Is.EqualTo(new[] { "tlsServer" }),
                            "a server certificate is recognised, neither believed nor presented");
                Assert.That(Path.GetFullPath(store["directory"]!.Value<String>()!).StartsWith(Path.GetFullPath(Directory), StringComparison.OrdinalIgnoreCase),
                            Is.True,
                            "the store lives beside the configuration file of this station");

                Assert.That(refused,                                          Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(said.ToString(),                                  Does.Contain("v2gRoot").And.Not.Contain("contract,"));
                Assert.That(Station.Certificates.Entries,                     Is.Empty, "a kind this station does not keep was kept");

            });

        }

        #endregion

        #region ARootIsUploadedForTheUsesItIsFor()

        [Test]
        public async Task ARootIsUploadedForTheUsesItIsFor()
        {

            using var http          = AsTheAdministrator();

            var (created, entry)    = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Our Clocks' Root")),
                                                     new JProperty("usages",   new JArray("nts"))
                                                 ));

            var (_, store)          = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {

                Assert.That(created,                                                                 Is.EqualTo(HttpStatusCode.Created), entry.ToString());
                Assert.That(entry["usages"]!.Values<String>(),                                       Is.EqualTo(new[] { "nts" }));

                Assert.That(store["usages"]!.Values<String>(),                                       Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer");
                Assert.That(store["kinds"]!["tlsRoot"]!["hasUsages"]!.Value<Boolean>(),              Is.True);
                Assert.That(store["kinds"]!["v2gRoot"]!["hasUsages"]!.Value<Boolean>(),              Is.False);
                Assert.That(store["kinds"]!["tlsRoot"]!["usages"]?.Values<String>(),                 Is.EqualTo(new[] { "dns", "nts" }), "what a page may offer a root");
                Assert.That(store["kinds"]!["tlsServer"]!["usages"]?.Values<String>(),               Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(store["kinds"]!["tlsIdentity"]!["hasUsages"]!.Value<Boolean>(),          Is.False,
                            "a station names no listener an identity could be told of, so a page offers it nothing - not the services a root vouches for");
                Assert.That(store["kinds"]!["tlsIdentity"]!["usages"]?.Children().Any(),             Is.False);
                Assert.That(store["certificates"]!["tlsRoot"]![0]!["usages"]!.Values<String>(),      Is.EqualTo(new[] { "nts" }));
                Assert.That(store["certificates"]!["v2gRoot"]!.Children().Any(),                     Is.False);

            });

        }

        #endregion

        #region WhatARootIsForIsChangedAndTakenBackToEveryUse()

        [Test]
        public async Task WhatARootIsForIsChangedAndTakenBackToEveryUse()
        {

            using var http          = AsTheAdministrator();

            var (_, entry)          = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Our Resolvers' Root")),
                                                     new JProperty("usages",   new JArray("dns"))
                                                 ));

            var path                = $"api/v1/certificates/{entry["id"]}";

            var (both,  forBoth)    = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("usages", new JArray("nts", "dns"))));
            var (label, relabel)    = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("label",  "Our Root")));
            var (every, forAll)     = await Send(http, HttpMethod.Patch, path, new JObject(new JProperty("usages", JValue.CreateNull())));

            Assert.Multiple(() => {
                Assert.That(both,                                      Is.EqualTo(HttpStatusCode.OK), forBoth.ToString());
                Assert.That(forBoth["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }));
                Assert.That(label,                                     Is.EqualTo(HttpStatusCode.OK), relabel.ToString());
                Assert.That(relabel["usages"]!.Values<String>(),       Is.EqualTo(new[] { "dns", "nts" }), "a PATCH without them leaves them alone");
                Assert.That(every,                                     Is.EqualTo(HttpStatusCode.OK), forAll.ToString());
                Assert.That(forAll["usages"]!.Type,                    Is.EqualTo(JTokenType.Null),    "null is every use again");
                Assert.That(Station.Log.Recent(200, Tag: "security").Any(line => line.Message.Contains("is now for every use")),
                            Is.True,
                            "a change of what a root vouches for is a matter of security, and said as one");
            });

        }

        #endregion

        #region WhatIsNotAUsageIsRefusedWhereItIsTyped()

        [Test]
        public async Task WhatIsNotAUsageIsRefusedWhereItIsTyped()
        {

            using var http              = AsTheAdministrator();

            var (unknown,  said)        = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "tlsRoot"),
                                                         new JProperty("content",  RootPem("Some Root")),
                                                         new JProperty("usages",   new JArray("ntp"))
                                                     ));

            var (onV2G,    v2gSaid)     = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "v2gRoot"),
                                                         new JProperty("content",  RootPem("A V2G Root")),
                                                         new JProperty("usages",   new JArray("nts"))
                                                     ));

            var (notAList, listSaid)    = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "tlsRoot"),
                                                         new JProperty("content",  RootPem("Another Root")),
                                                         new JProperty("usages",   "dns")
                                                     ));

            // Refused before the file is read, so a root's file does for an
            // identity here: what is wrong is what it was to be told.
            var (identity, idSaid)      = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                         new JProperty("kind",     "tlsIdentity"),
                                                         new JProperty("content",  RootPem("Not An Identity")),
                                                         new JProperty("usages",   new JArray("dns"))
                                                     ));

            var (_, store)              = await Send(http, HttpMethod.Get, "api/v1/certificates");

            Assert.Multiple(() => {
                Assert.That(unknown,                       Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(said.ToString(),               Does.Contain("'ntp' is not a usage this charging station knows").And.Contain("dns, nts"));
                Assert.That(onV2G,                         Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(v2gSaid.ToString(),            Does.Contain("only a TLS root and a server certificate"));
                Assert.That(notAList,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(listSaid.ToString(),           Does.Contain("has to be a list of usages"));
                Assert.That(identity,                      Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(idSaid.ToString(),             Does.Contain("names none"), "an identity is told listeners, and a station has none");
                Assert.That(store["certificates"]!.Values().SelectMany(kind => kind.Children()).Any(),
                            Is.False,
                            "nothing refused was half-imported");
            });

        }

        #endregion

        #region OnlyTheAdministratorsChangeWhatThisStationBelieves()

        /// <summary>
        /// An installer - who may raise a power limit and put calibration
        /// certificates on - may read the store and change nothing in it: a root
        /// is a decision about whom this station believes, and that is the
        /// administrators' alone, as on the vehicle.
        /// </summary>
        [Test]
        public async Task OnlyTheAdministratorsChangeWhatThisStationBelieves()
        {

            using var http          = AsTheAdministrator();

            var (_, kept)           = await Send(http, HttpMethod.Post, "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Kept Root"))
                                                 ));

            // The same account from here on, as an installer: which groups it is
            // in is asked on every request, so this takes effect at the next one.
            await Become("installer", "systemadmin");

            var path                = $"api/v1/certificates/{kept["id"]}";

            var (read,     _)       = await Send(http, HttpMethod.Get,    "api/v1/certificates");
            var (upload,   said)    = await Send(http, HttpMethod.Post,   "api/v1/certificates", new JObject(
                                                     new JProperty("kind",     "tlsRoot"),
                                                     new JProperty("content",  RootPem("Smuggled Root"))
                                                 ));
            var (patch,    _)       = await Send(http, HttpMethod.Patch,  path, new JObject(new JProperty("active", false)));
            var (delete,   _)       = await Send(http, HttpMethod.Delete, path);
            var (reload,   _)       = await Send(http, HttpMethod.Post,   "api/v1/certificates/reload");

            Assert.Multiple(() => {
                Assert.That(read,                                      Is.EqualTo(HttpStatusCode.OK), "reading the store is reading the configuration");
                Assert.That(upload,                                    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(said.ToString(),                           Does.Contain("systemadmin"));
                Assert.That(patch,                                     Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(delete,                                    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(reload,                                    Is.EqualTo(HttpStatusCode.Forbidden));
                Assert.That(Station.Certificates.Entries.Select(entry => entry.Label), Is.EqualTo(new[] { "Kept Root" }),
                            "the store was changed by somebody who may not change it");
                Assert.That(Station.Certificates.Entries.Single().IsActive, Is.True);
            });

        }

        #endregion

    }

}
