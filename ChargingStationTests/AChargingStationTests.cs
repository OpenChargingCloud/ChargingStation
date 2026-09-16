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
using System.Text;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// One charging station, listening, for the length of one test.
    /// </summary>
    /// <remarks>
    /// A whole station per test rather than one for the fixture, because half
    /// of what is worth testing here changes the station: a test that repoints
    /// the name servers must not decide what the next test reads. One costs a
    /// few hundred milliseconds, which is cheaper than the morning spent on a
    /// test that only fails when it runs second.
    ///
    /// Each one gets a directory of its own for the two files it writes, and
    /// two ports the operating system has just confirmed are free - so a
    /// developer with a station running on 2348 can still run the tests.
    ///
    /// **Nothing here reaches the network.** The configuration written before
    /// the station is built switches the time client off, which is what stops
    /// the clock check from ever being scheduled; the DNS client is only ever
    /// asked what it is configured as, never to resolve anything; and a station
    /// is built with <c>V2GOptions.Off</c> unless it is handed something else,
    /// so nothing goes looking for a powerline modem either.
    /// </remarks>
    public abstract class AChargingStationTests
    {

        #region Properties

        /// <summary>
        /// The station under test, listening, from SetUp until TearDown.
        /// </summary>
        protected ChargingStation  Station      { get; private set; } = default!;

        /// <summary>
        /// The password this station made up for itself at its first start.
        /// </summary>
        protected String           Password     { get; private set; } = default!;

        /// <summary>
        /// The web interface: "http://127.0.0.1:&lt;port&gt;/".
        /// </summary>
        protected String           BaseURL      { get; private set; } = default!;

        /// <summary>
        /// The display, on its own server and its own port.
        /// </summary>
        protected String           KioskURL     { get; private set; } = default!;

        /// <summary>
        /// The directory holding its web login and its configuration, removed
        /// again in TearDown.
        /// </summary>
        protected String           Directory    { get; private set; } = default!;

        #endregion

        #region What this station is made of

        /// <summary>
        /// What its configuration file says before it is built.
        /// </summary>
        /// <remarks>
        /// Overridden by a fixture that needs a station with something on it -
        /// outlets, a card reader, an operator. The time client stays switched
        /// off in all of them, which is what keeps a test run off the network.
        /// </remarks>
        protected virtual JObject Configuration
            => TestStations.Offline;

        /// <summary>
        /// Where it reads the time, or null for the system clock.
        /// </summary>
        /// <remarks>
        /// Overridden by a fixture that has to decide what time it is - a
        /// one-time password is a function of the clock, and a test that waited
        /// for a real half-minute to pass would be a test nobody runs.
        /// </remarks>
        protected virtual TimeProvider? Clock
            => null;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public async Task StartTheStation()
        {

            Directory  = TestStations.TemporaryDirectory("tests");

            Station    = TestStations.New(Directory, Configuration, Clock: Clock);

            // Null would mean the login came from a file, and there was no file.
            Password   = Station.GeneratedPassword
                             ?? throw new InvalidOperationException("The station did not make up a password for its first start!");

            BaseURL    = Station.WebInterfaceURL.ToString();

            KioskURL   = Station.KioskURL?.ToString()
                             ?? throw new InvalidOperationException("The station was built with a display and has no URL for it!");

            await Station.Start();

        }

        [TearDown]
        public async Task StopTheStation()
        {

            if (Station is not null)
                await Station.DisposeAsync();

            TestStations.Remove(Directory);

        }

        #endregion


        #region (protected) Anonymous() / AtTheDisplay()

        /// <summary>
        /// A browser that has not signed in.
        /// </summary>
        protected HttpClient Anonymous()

            => new (new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                   BaseAddress = new Uri(BaseURL)
               };

        /// <summary>
        /// A browser at the display, which is a different server on a different
        /// port and has no sign-in at all.
        /// </summary>
        protected HttpClient AtTheDisplay()

            => new (new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true }) {
                   BaseAddress = new Uri(KioskURL)
               };

        #endregion

        #region (protected) SignedIn()

        /// <summary>
        /// A browser that has signed in with the password this station made up,
        /// carrying the session cookie from here on.
        /// </summary>
        protected async Task<HttpClient> SignedIn()
        {

            var http      = Anonymous();

            var response  = await http.PostAsync(
                                      "/api/v1/auth/login",
                                      JSONBody(
                                          new JProperty("username", Station.Sessions.Username),
                                          new JProperty("password", Password)
                                      )
                                  );

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"Signing in failed with {(Int32) response.StatusCode}, and every assertion below it would say so instead.");

            return http;

        }

        #endregion

        #region (protected static) JSONBody(...)

        /// <summary>
        /// A request body, as the web interface sends one.
        /// </summary>
        protected static StringContent JSONBody(params JProperty[] Properties)

            => new (new JObject(Properties).ToString(),
                    Encoding.UTF8,
                    "application/json");

        #endregion

        #region (protected static) GetJSON(HTTP, Path)

        /// <summary>
        /// One GET, with the answer parsed and the status checked - so that a
        /// test which is about what a resource says does not also have to say
        /// what a 500 looks like.
        /// </summary>
        protected static async Task<JObject> GetJSON(HttpClient  HTTP,
                                                     String      Path)
        {

            var response = await HTTP.GetAsync(Path);

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"GET {Path} answered {(Int32) response.StatusCode}.");

            return JObject.Parse(await response.Content.ReadAsStringAsync());

        }

        #endregion

    }

}
