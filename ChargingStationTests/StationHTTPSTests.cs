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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.TestKit;
using cloud.charging.open.protocols.WWCP.Node.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The station served safely, as the node's "web" section and its switches
    /// say it: over HTTPS of its own, with the identity the command line put
    /// into its store - its web interface, its JSON API and its sign-in alike -
    /// and behind a trusted proxy, where the sign-in's ration of guesses is
    /// counted per client the proxy names.
    /// </summary>
    /// <remarks>
    /// Each made as ChargingStationCLI makes it: the switches handed on as
    /// NodeArguments.Web, and what goes into the store put in before the
    /// start. A station that does not take them on refuses that step - which
    /// is what the program would return 2 for.
    /// </remarks>
    public class StationHTTPSTests
    {

        #region Data

        private          String                 directory  = "";
        private readonly List<ChargingStation>  stations   = [];

        #endregion

        #region Setup / TearDown

        [SetUp]
        public void Setup()
        {
            directory = TestStations.TemporaryDirectory("https");
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public async Task TearDown()
        {

            foreach (var station in stations)
            {
                try
                {
                    await station.DisposeAsync();
                }
                catch (Exception)
                { }
            }

            stations.Clear();

            TestStations.Remove(directory);

        }

        #endregion


        #region (helpers) AnIdentityFile() / Started(Arguments) / SignIn(...)

        /// <summary>
        /// A TLS server identity for 127.0.0.1 in a PKCS#12 file without a
        /// password, as --import-certificate takes it, and its thumbprint.
        /// </summary>
        private (String File, String Thumbprint) AnIdentityFile()
        {

            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var request   = new CertificateRequest("CN=charging station web interface", key, HashAlgorithmName.SHA256);
            var names     = new SubjectAlternativeNameBuilder();

            names.AddIpAddress(IPAddress.Loopback);
            names.AddDnsName("localhost");

            request.CertificateExtensions.Add(names.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([ new Oid("1.3.6.1.5.5.7.3.1") ], false));

            using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

            var file = Path.Combine(directory, "station-web.p12");

            File.WriteAllBytes(file, certificate.Export(X509ContentType.Pkcs12));

            return (file, CertificateEntry.ThumbprintOf(certificate));

        }

        /// <summary>
        /// A station made as ChargingStationCLI makes it - from the command line
        /// given, what goes into its store put in - and started on fresh ports.
        /// </summary>
        private async Task<ChargingStation> Started(NodeArguments Arguments)
        {

            Assert.That(Arguments.Problem, Is.Null);

            var station = await TestPorts.StartedOnFreshPorts(() => {

                var made     = TestStations.New(Path.Combine(directory, "station" + stations.Count),
                                                TestStations.Offline,
                                                WithDisplay:  false,
                                                Web:          Arguments.Web);

                var refusal  = new StringWriter();

                Assert.That(made.ImportCertificates(Arguments, out _, new StringWriter(), refusal), Is.Null, refusal.ToString());

                return made;

            });

            stations.Add(station);

            return station;

        }

        /// <summary>
        /// A sign-in at the HTTPExt API's form, with the given header fields
        /// written as given and without a cookie jar.
        /// </summary>
        private static async Task<HttpResponseMessage> SignIn(ChargingStation            Station,
                                                              String                     Login,
                                                              String                     Password,
                                                              params (String, String)[]  Headers)
        {

            using var handler  = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            using var client   = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            using var request  = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Station.HTTPPort}/ext/login") {
                                     Content = new FormUrlEncodedContent([
                                                   new KeyValuePair<String, String>("login",     Login),
                                                   new KeyValuePair<String, String>("password",  Password)
                                               ])
                                 };

            foreach (var (name, value) in Headers)
                Assert.That(request.Headers.TryAddWithoutValidation(name, value), Is.True, $"{name}: {value}");

            var response = await client.SendAsync(request);

            await response.Content.LoadIntoBufferAsync();

            return response;

        }

        #endregion


        #region TheStationServesItsWebInterfaceAndAPIOverHTTPS()

        /// <summary>
        /// --https with --import-certificate tlsServerIdentity=&lt;file&gt;, as
        /// ChargingStationCLI passes them on: the station keeps the identity,
        /// shows it, and serves its web interface with HSTS, its sign-in with a
        /// secure cookie and its JSON API - all over HTTPS - and says nothing
        /// about plain HTTP.
        /// </summary>
        [Test]
        public async Task TheStationServesItsWebInterfaceAndAPIOverHTTPS()
        {

            var (file, thumbprint)  = AnIdentityFile();
            var station             = await Started(NodeArguments.Parse([ "--https", "--import-certificate", $"tlsServerIdentity={file}" ]));

            String? shown           = null;

            using var http          = new HttpClient(new HttpClientHandler {
                                                         CookieContainer                            = new CookieContainer(),
                                                         UseCookies                                 = true,
                                                         ServerCertificateCustomValidationCallback  = (message, served, chain, errors) => {
                                                                                                          shown = served is null ? null : CertificateEntry.ThumbprintOf(served);
                                                                                                          return true;
                                                                                                      }
                                                     }) {
                                          BaseAddress  = new Uri(station.WebInterfaceURL.ToString()),
                                          Timeout      = TimeSpan.FromSeconds(30)
                                      };

            using var page          = await http.GetAsync("");
            using var signIn        = await http.PostAsync("ext/login", new FormUrlEncodedContent([
                                                                            new KeyValuePair<String, String>("login",     ChargingStation.DefaultAdminUser),
                                                                            new KeyValuePair<String, String>("password",  station.GeneratedPassword!)
                                                                        ]));
            using var configuration = await http.GetAsync("api/v1/configuration");

            var session             = signIn.Headers.TryGetValues("Set-Cookie", out var cookies)
                                          ? cookies.FirstOrDefault(cookie => cookie.StartsWith($"{station.ExtAPI.SessionCookieName}=", StringComparison.Ordinal))
                                          : null;

            Assert.Multiple(() => {

                Assert.That(ChargingStation.StoredCertificateKinds,                      Does.Contain(CertificateKind.TLSServerIdentity));
                Assert.That(station.Certificates.ByKind(CertificateKind.TLSServerIdentity).Select(entry => entry.Thumbprint),
                                                                                         Is.EqualTo(new[] { thumbprint }));

                Assert.That(station.HTTPS,                                               Is.True);
                Assert.That(station.WebInterfaceURL.ToString(),                          Does.StartWith("https://"));
                Assert.That(shown,                                                       Is.EqualTo(thumbprint));
                Assert.That(station.PlainHTTPWarning,                                    Is.Null);

                Assert.That(page.StatusCode,                                             Is.EqualTo(HttpStatusCode.OK));
                Assert.That(page.Headers.TryGetValues("Strict-Transport-Security", out var hsts) ? hsts.Single() : null,
                                                                                         Is.EqualTo("max-age=31536000"));

                Assert.That(signIn.IsSuccessStatusCode,                                  Is.True, $"{(Int32) signIn.StatusCode}");
                Assert.That(session,                                                     Does.Contain("secure").IgnoreCase);

                Assert.That(configuration.StatusCode,                                    Is.EqualTo(HttpStatusCode.OK), "the JSON API, with the session over HTTPS");

            });

            var said = JObject.Parse(await configuration.Content.ReadAsStringAsync());

            Assert.That(said["http"]?["tls"]?.Value<String>("thumbprint"),               Is.EqualTo(thumbprint));

        }

        #endregion

        #region BehindATrustedProxyTheSignInsRationIsCountedPerClient()

        /// <summary>
        /// Behind nginx, named with --trusted-proxy: a client that keeps guessing
        /// is made to wait once it has had its ten, and another behind the same
        /// proxy signs in at once - with a secure cookie, as the proxy says its
        /// leg was HTTPS. And the station, listening on the loopback only behind
        /// it, says nothing about plain HTTP.
        /// </summary>
        /// <remarks>
        /// Guessed until the client is made to wait, rather than ten times and
        /// then once with the right password. The ration gives a guess back every
        /// six seconds, and a runner that took nine for the ten let that sign-in
        /// through. Each guess names another account, so that the client's
        /// ration is what runs out and not one of an account's.
        /// </remarks>
        [Test]
        public async Task BehindATrustedProxyTheSignInsRationIsCountedPerClient()
        {

            var station  = await Started(NodeArguments.Parse([ "--trusted-proxy", "127.0.0.1", "--trusted-proxy", "::1" ]));

            var guesses  = 0;
            var told     = (HttpStatusCode?) null;
            var wait     = (String?) null;

            while (told != HttpStatusCode.TooManyRequests && guesses < 40)
            {

                guesses++;

                using var guess = await SignIn(station, $"nobody-{guesses}", $"Not-The-Password-{guesses}", ("X-Forwarded-For", "192.0.2.7"), ("X-Forwarded-Proto", "https"));

                told = guess.StatusCode;
                wait = guess.Headers.RetryAfter?.ToString();

            }

            using var other   = await SignIn(station, ChargingStation.DefaultAdminUser, station.GeneratedPassword!, ("X-Forwarded-For", "198.51.100.4"), ("X-Forwarded-Proto", "https"));

            var session       = other.Headers.TryGetValues("Set-Cookie", out var cookies)
                                    ? cookies.FirstOrDefault(cookie => cookie.StartsWith($"{station.ExtAPI.SessionCookieName}=", StringComparison.Ordinal))
                                    : null;

            Assert.Multiple(() => {
                Assert.That(told,                        Is.EqualTo(HttpStatusCode.TooManyRequests), $"the client who guessed was not made to wait in {guesses} guesses");
                Assert.That(guesses,                     Is.GreaterThan(10),                         "a client may guess ten times before it waits");
                Assert.That(wait,                        Is.Not.Null,                                "and is told for how long");
                Assert.That((Int32) other.StatusCode,    Is.InRange(200, 399),                       "another client behind the same proxy signs in");
                Assert.That(session,                     Does.Contain("secure").IgnoreCase,           "its leg was HTTPS, as the proxy says");
                Assert.That(station.PlainHTTPWarning,    Is.Null);
                Assert.That(station.HTTPS,               Is.False);
            });

        }

        #endregion

    }

}
