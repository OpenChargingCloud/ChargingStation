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

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a browser gets before it has signed in to anything: the
    /// single-page-application stub, the bundle it references, and the line
    /// between a page URL and a file that is not there.
    /// </summary>
    /// <remarks>
    /// The bundle under test is the one embedded in the assembly, which means
    /// these tests also say whether the build embedded it - a station whose
    /// frontend did not make it into the assembly answers every one of them
    /// with the JSON API and nothing else.
    /// </remarks>
    public class WebInterfaceTests : AChargingStationTests
    {

        #region ServesTheApplicationStub()

        [Test]
        public async Task ServesTheApplicationStub()
        {

            using var http     = Anonymous();

            var response       = await http.GetAsync("/");
            var html           = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,                            Is.True);
                Assert.That(response.Content.Headers.ContentType?.MediaType,         Is.EqualTo("text/html"));
                Assert.That(html.Contains("<div id=\"app\">", StringComparison.Ordinal), Is.True,
                            "The stub is what the bundle renders into; without that element there is nothing to render into.");
            });

        }

        #endregion

        #region TheStubCarriesTheVersionOfTheStation()

        /// <summary>
        /// The server replaces {{ServerVersion}} as it serves the stub, and the
        /// bundle reads it out of a meta tag rather than from an inline script -
        /// which is what lets the Content-Security-Policy stay strict.
        /// </summary>
        [Test]
        public async Task TheStubCarriesTheVersionOfTheStation()
        {

            using var http = Anonymous();

            var html = await http.GetStringAsync("/");

            Assert.Multiple(() => {
                Assert.That(html.Contains($"content=\"v{Station.Version}\"", StringComparison.Ordinal), Is.True,
                            "The stub does not carry the version of the station that served it.");
                Assert.That(html.Contains("{{ServerVersion}}", StringComparison.Ordinal), Is.False,
                            "The placeholder was served as it stands, so nothing replaced it.");
            });

        }

        #endregion

        #region TheStubReferencesABundleThatIsServed()

        [Test]
        public async Task TheStubReferencesABundleThatIsServed()
        {

            using var http = Anonymous();

            var html   = await http.GetStringAsync("/");

            var start  = html.IndexOf("/assets/main.", StringComparison.Ordinal);

            Assert.That(start, Is.GreaterThan(-1),
                        "The stub references no bundle at all.");

            var script = html[start..html.IndexOf('"', start)];

            var bundle = await http.GetAsync(script);

            Assert.Multiple(() => {
                Assert.That(bundle.IsSuccessStatusCode,             Is.True, $"'{script}' is referenced and not served.");
                Assert.That(bundle.Content.Headers.ContentLength,   Is.GreaterThan(0));
            });

        }

        #endregion

        #region FaviconIcoIsPointedAtTheSVG()

        /// <summary>
        /// Browsers ask for /favicon.ico whatever the page says, and a bundle
        /// built by webpack carries an SVG.
        /// </summary>
        [Test]
        public async Task FaviconIcoIsPointedAtTheSVG()
        {

            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http    = new HttpClient(handler) { BaseAddress = new Uri(BaseURL) };

            var response = await http.GetAsync("/favicon.ico");

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,                   Is.EqualTo(HttpStatusCode.TemporaryRedirect));
                Assert.That(response.Headers.Location?.ToString(),  Is.EqualTo("/favicon.svg"));
            });

        }

        #endregion

        #region ADeepPageURLGetsTheStub()

        /// <summary>
        /// A reload on /configuration/nts and a bookmark to it are the same
        /// request, and both have to arrive at the application rather than at a
        /// 404 - the router in the browser decides what that URL means.
        /// </summary>
        [Test]
        public async Task ADeepPageURLGetsTheStub()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/configuration/nts");
            var html     = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode, Is.True);
                Assert.That(html.Contains("<div id=\"app\">", StringComparison.Ordinal), Is.True);
            });

        }

        #endregion

        #region AMissingAssetIsARealNotFound()

        /// <summary>
        /// The other side of the line above, and the reason it is drawn at all:
        /// a mistyped script tag has to be a 404 rather than the stub with
        /// status 200, or the browser is handed HTML to execute and a
        /// deployment that forgot half its bundle looks healthy.
        /// </summary>
        [Test]
        public async Task AMissingAssetIsARealNotFound()
        {

            using var http = Anonymous();

            var response = await http.GetAsync("/assets/nothing-was-built-here.js");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));

        }

        #endregion

        #region TheDisplayServesItsOwnPage()

        /// <summary>
        /// The display is a second single-page application out of the same
        /// bundle, entered at its other door: the assets are shared, the page
        /// is not.
        /// </summary>
        [Test]
        public async Task TheDisplayServesItsOwnPage()
        {

            using var display = AtTheDisplay();

            var response = await display.GetAsync("/");
            var html     = await response.Content.ReadAsStringAsync();

            Assert.Multiple(() => {
                Assert.That(response.IsSuccessStatusCode,                     Is.True);
                Assert.That(response.Content.Headers.ContentType?.MediaType,  Is.EqualTo("text/html"));
                Assert.That(html.Contains($"content=\"v{Station.Version}\"", StringComparison.Ordinal), Is.True,
                            "The display page does not carry the version of the station that served it.");
            });

        }

        #endregion

        #region TheDisplayIsNotTheWebInterface()

        /// <summary>
        /// Two pages out of one bundle, and the wrong one on either port would
        /// be a screen in a car park showing the sign-in - or an administrator
        /// looking at the outlets.
        /// </summary>
        [Test]
        public async Task TheDisplayIsNotTheWebInterface()
        {

            using var display = AtTheDisplay();
            using var http    = Anonymous();

            var onTheDisplay      = await display.GetStringAsync("/");
            var onTheWebInterface = await http.   GetStringAsync("/");

            Assert.That(onTheDisplay, Is.Not.EqualTo(onTheWebInterface),
                        "Both ports serve the same page, so one of them is serving the wrong one.");

        }

        #endregion

    }

}
