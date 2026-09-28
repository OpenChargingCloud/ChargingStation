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
