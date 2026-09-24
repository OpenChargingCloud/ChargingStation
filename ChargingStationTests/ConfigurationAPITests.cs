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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What this local station says it is, and what happens when somebody
    /// tells it to be something else.
    /// </summary>
    public class ConfigurationAPITests : AChargingStationTests
    {

        #region TheStatusResourceSaysWhatIsRunning()

        [Test]
        public async Task TheStatusResourceSaysWhatIsRunning()
        {

            using var http = await SignedIn();

            var status = await GetJSON(http, "/api/v1/status");

            Assert.Multiple(() => {
                Assert.That(status.Value<String>("service"),  Is.EqualTo("ChargingStation"));
                Assert.That(status.Value<String>("version"),  Is.EqualTo(Station.Version));
                Assert.That(status.Value<String>("hermod"),   Is.Not.Null.And.Not.Empty);
                Assert.That(status.Value<Int32> ("sessions"), Is.EqualTo(1));
                Assert.That(status["log"]?.Value<Int32>("capacity"), Is.GreaterThan(0));
            });

        }

        #endregion

        #region TheClockIsServedToWhoeverIsSignedIn()

        /// <summary>
        /// GET /api/v1/clock: what time it is here and what that is worth - to
        /// anybody signed in, as the status is, and to nobody else.
        /// </summary>
        /// <remarks>
        /// The display has had this JSON all along, inside its own answer on its
        /// own port. A station started without a display served it nowhere, and
        /// a page of the administration could not ask it at all.
        /// </remarks>
        [Test]
        public async Task TheClockIsServedToWhoeverIsSignedIn()
        {

            using var anonymous  = Anonymous();
            var       refused    = await anonymous.GetAsync("/api/v1/clock");

            using var http       = await SignedIn();
            var       clock      = await GetJSON(http, "/api/v1/clock");

            Assert.Multiple(() => {
                Assert.That(refused.StatusCode,                      Is.EqualTo(HttpStatusCode.Unauthorized));
                Assert.That(clock.Value<String>("source"),           Is.EqualTo("system"));
                Assert.That(clock["legal"]?.Type,                    Is.EqualTo(JTokenType.Boolean));
                Assert.That(clock["nts"]?.Value<Boolean>("enabled"), Is.EqualTo(Station.NTSEnabled));
            });

        }

        #endregion

        #region TheConfigurationNamesEverySection()

        /// <summary>
        /// The Configuration page renders whatever the station sends rather
        /// than a list of its own, so a section going missing is not a broken
        /// page - it is a page that quietly stops mentioning something.
        /// </summary>
        [Test]
        public async Task TheConfigurationNamesEverySection()
        {

            using var http = await SignedIn();

            var configuration = await GetJSON(http, "/api/v1/configuration");

            Assert.Multiple(() => {
                Assert.That(configuration["station"],    Is.Not.Null);
                Assert.That(configuration["http"],       Is.Not.Null);
                Assert.That(configuration["web"],        Is.Not.Null);
                Assert.That(configuration["log"],        Is.Not.Null);
                Assert.That(configuration["time"],       Is.Not.Null);
                Assert.That(configuration["v2g"],        Is.Not.Null);
                Assert.That(configuration["ocpp"],       Is.TypeOf<JArray>());
                Assert.That(configuration["assemblies"], Is.TypeOf<JArray>());
            });

        }

        #endregion

        #region TheConfigurationNeverCarriesThePassword()

        /// <summary>
        /// The accounts appear in the configuration as a directory, a route to
        /// sign in at and two counts, and never with anything about a password
        /// - neither the password nor its hash.
        /// </summary>
        [Test]
        public async Task TheConfigurationNeverCarriesThePassword()
        {

            using var http = await SignedIn();

            var configuration = (await GetJSON(http, "/api/v1/configuration")).ToString();

            Assert.Multiple(() => {
                Assert.That(configuration.Contains(Password, StringComparison.Ordinal),   Is.False,
                            "The configuration carries the password in the clear.");
                Assert.That(configuration.Contains("$pbkdf2", StringComparison.Ordinal),  Is.False,
                            "The configuration carries the password hash, which is worth a dictionary attack.");
            });

        }

        #endregion

        #region TheOCPPSectionDescribesBothNodes()

        /// <summary>
        /// A station speaks through two nodes, and both are reported: OCPP 1.6
        /// has connectors and no EVSEs, OCPP 2.1 has EVSEs. What a station is
        /// made of is spelled differently in each, and the page shows both
        /// rather than picking one.
        /// </summary>
        [Test]
        public async Task TheOCPPSectionDescribesBothNodes()
        {

            using var http = await SignedIn();

            var ocpp = (await GetJSON(http, "/api/v1/configuration"))["ocpp"] as JArray ?? [];

            var v16  = ocpp.FirstOrDefault(node => node.Value<String>("version") == "1.6");
            var v21  = ocpp.FirstOrDefault(node => node.Value<String>("version") == "2.1");

            Assert.Multiple(() => {

                Assert.That(ocpp.Count, Is.EqualTo(2));

                Assert.That(v16,                          Is.Not.Null, "The OCPP 1.6 node is not reported.");
                Assert.That(v16?.Value<String>("role"),   Is.EqualTo("Charge Point"));
                Assert.That(v16?["connectors"],           Is.TypeOf<JArray>());

                Assert.That(v21,                          Is.Not.Null, "The OCPP 2.1 node is not reported.");
                Assert.That(v21?.Value<String>("role"),   Is.EqualTo("Charging Station"));
                Assert.That(v21?["evses"],                Is.TypeOf<JArray>());

            });

        }

        #endregion

        #region TheOCPPNodesAgreeWithTheEVSEsTheStationHas()

        /// <summary>
        /// The nodes are built from the EVSEs and rebuilt whenever those
        /// change, so that what the station says it is made of and what a back
        /// end is told about it are never two different things.
        /// </summary>
        [Test]
        public async Task TheOCPPNodesAgreeWithTheEVSEsTheStationHas()
        {

            using var http = await SignedIn();

            var ocpp  = (await GetJSON(http, "/api/v1/configuration"))["ocpp"] as JArray ?? [];
            var v21   = ocpp.FirstOrDefault(node => node.Value<String>("version") == "2.1");
            var evses = (v21?["evses"] as JArray)?.Count ?? 0;

            Assert.Multiple(() => {

                // Said first, so that this cannot pass by both sides being
                // empty: a station has at least one outlet or it is not one.
                Assert.That(Station.EVSEs.Count, Is.GreaterThan(0));

                // One connector of OCPP 1.6 is one cable, so an EVSE with two
                // of them becomes two - which is why only the 2.1 side counts
                // EVSEs.
                Assert.That(evses, Is.EqualTo(Station.EVSEs.Count),
                            "The OCPP 2.1 node reports a different number of EVSEs than the station has.");

            });

        }

        #endregion

        #region TheDNSConfigurationIsReadable()

        [Test]
        public async Task TheDNSConfigurationIsReadable()
        {

            using var http = await SignedIn();

            var dns = await GetJSON(http, "/api/v1/configuration/dns");

            Assert.Multiple(() => {
                Assert.That(dns.Value<Boolean>("enabled"),   Is.True);
                Assert.That(dns["servers"],                  Is.TypeOf<JArray>());
                Assert.That(dns["settings"],                 Is.Not.Null);
                Assert.That(dns["limits"]?.Value<Int32>("maxServers"), Is.GreaterThan(0));
                Assert.That(dns.Value<String>("file"),       Is.EqualTo(Station.ConfigFile.Path));
            });

        }

        #endregion

        #region ChangingTheDNSSettingsReachesTheClientAndTheFile()

        /// <summary>
        /// Every change takes effect at once and is written down, in that order
        /// of importance: a change that was applied but not written down
        /// disappears at the next start without anybody noticing.
        /// </summary>
        [Test]
        public async Task ChangingTheDNSSettingsReachesTheClientAndTheFile()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     "/api/v1/configuration/dns",
                                     JSONBody(
                                         new JProperty("useCache",    false),
                                         new JProperty("maxRetries",  4),
                                         new JProperty("servers",     new JArray(
                                             new JObject(
                                                 new JProperty("address",    "9.9.9.9"),
                                                 new JProperty("port",       853),
                                                 new JProperty("transport",  "TLS")
                                             )
                                         ))
                                     )
                                 );

            var answered = JObject.Parse(await response.Content.ReadAsStringAsync());
            var onDisk   = JObject.Parse(File.ReadAllText(Station.ConfigFile.Path));

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode, Is.True);

                // What the station answers is the whole configuration as it
                // now stands, so the page shows what was taken rather than what
                // was sent.
                Assert.That(answered["settings"]?.Value<Boolean>("useCache"),   Is.False);
                Assert.That(answered["settings"]?.Value<Int32>  ("maxRetries"), Is.EqualTo(4));
                Assert.That((answered["servers"] as JArray)?.Count,             Is.EqualTo(1));

                // In the client everything below this station was handed,
                // and not in a copy of it.
                Assert.That(Station.DNSClient.UseCache,    Is.False);
                Assert.That(Station.DNSClient.MaxRetries,  Is.EqualTo(4));

                // And in the file, for the next start.
                Assert.That(onDisk["dns"]?.Value<Int32>("maxRetries"), Is.EqualTo(4));

            });

        }

        #endregion

        #region ANameServerInTheFormTheLogUsesIsRefusedAndNothingIsWritten()

        /// <summary>
        /// "udp://…:53", as the log and the banner write a name server, typed
        /// into the DNS page: refused with the sentence that says why, and
        /// neither applied nor written down. It was an internal server error,
        /// out of an exception in the address parser.
        /// </summary>
        [Test]
        public async Task ANameServerInTheFormTheLogUsesIsRefusedAndNothingIsWritten()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync(
                                     "/api/v1/configuration/dns",
                                     JSONBody(
                                         new JProperty("servers", new JArray("udp://213.133.98.98:53"))
                                     )
                                 );

            var answered = await response.Content.ReadAsStringAsync();
            var onDisk   = File.Exists(Station.ConfigFile.Path)
                               ? File.ReadAllText(Station.ConfigFile.Path)
                               : "";

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest),  answered);
                Assert.That(answered,             Does.Contain("'dns.servers'").And.Contain("udp://213.133.98.98:53"));
                Assert.That(onDisk,               Does.Not.Contain("213.133.98.98"));
                Assert.That(Station.DNSClient.DNSServers.Select(server => server.ToString()),
                            Has.None.Contains("213.133.98.98"));
            });

        }

        #endregion

        #region WhatAChangeDoesNotMentionIsLeftAlone()

        /// <summary>
        /// A page that only offers the checkboxes must be able to send only the
        /// checkboxes without taking the name servers with it.
        /// </summary>
        [Test]
        public async Task WhatAChangeDoesNotMentionIsLeftAlone()
        {

            using var http = await SignedIn();

            var before = await GetJSON(http, "/api/v1/configuration/dns");
            var servers = (before["servers"] as JArray)?.Count ?? 0;

            await http.PutAsync("/api/v1/configuration/dns",
                                JSONBody(new JProperty("dnssecOK", true)));

            var after = await GetJSON(http, "/api/v1/configuration/dns");

            Assert.Multiple(() => {
                Assert.That(after["settings"]?.Value<Boolean>("dnssecOK"), Is.True);
                Assert.That((after["servers"] as JArray)?.Count,           Is.EqualTo(servers),
                            "A change that said nothing about the name servers took them away.");
            });

        }

        #endregion

        #region EmptyingTheNameServersIsRefused()

        /// <summary>
        /// Switching name resolution off is a thing somebody means; a list of
        /// no name servers is a thing nobody means.
        /// </summary>
        [Test]
        public async Task EmptyingTheNameServersIsRefused()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync("/api/v1/configuration/dns",
                                               JSONBody(new JProperty("servers", new JArray())));

            var error = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,           Is.EqualTo(HttpStatusCode.BadRequest));
                Assert.That(error.Value<String>("error"),  Does.Contain("Switch name resolution off"));
                Assert.That(Station.DNSClient.DNSServers.Any(), Is.True,
                            "The refused request emptied the list anyway.");
            });

        }

        #endregion

        #region SwitchingNameResolutionOffTakesTheServersAway()

        /// <summary>
        /// Off means off for everything that was handed this client, not only
        /// for the parts of the station that would have remembered to check
        /// a flag first.
        /// </summary>
        [Test]
        public async Task SwitchingNameResolutionOffTakesTheServersAway()
        {

            using var http = await SignedIn();

            await http.PutAsync("/api/v1/configuration/dns",
                                JSONBody(new JProperty("enabled", false)));

            Assert.Multiple(() => {
                Assert.That(Station.DNSEnabled,                 Is.False);
                Assert.That(Station.DNSClient.DNSServers.Any(),  Is.False,
                            "Name resolution is off, but the client still holds servers to ask.");
            });

        }

        #endregion

        #region AFieldOfTheWrongKindIsRefused()

        /// <summary>
        /// Present but of the wrong kind is an error and never a silent null:
        /// somebody wrote it and deserves to be told, rather than watching the
        /// setting quietly not happen.
        /// </summary>
        [Test]
        public async Task AFieldOfTheWrongKindIsRefused()
        {

            using var http = await SignedIn();

            var response = await http.PutAsync("/api/v1/configuration/nts",
                                               JSONBody(new JProperty("ntpPort", "onetwothree")));

            var error = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,          Is.EqualTo(HttpStatusCode.BadRequest));
                // Named by its path in the document, because "must be a port
                // number" over a form with three ports on it has said nothing.
                Assert.That(error.Value<String>("error"), Does.Contain("nts.ntpPort"));
            });

        }

        #endregion


        #region TheNTSConfigurationIsReadable()

        [Test]
        public async Task TheNTSConfigurationIsReadable()
        {

            using var http = await SignedIn();

            var nts = await GetJSON(http, "/api/v1/configuration/nts");

            Assert.Multiple(() => {
                // Switched off by the fixture, so that no test reaches the
                // network - the server it would ask is still named.
                Assert.That(nts.Value<Boolean>("enabled"),                Is.False);
                Assert.That(nts["server"]?.Value<String>("hostname"),     Is.Not.Null.And.Not.Empty);
                Assert.That(nts["cookies"],                               Is.Not.Null);
                Assert.That(nts["keyExchange"],                           Is.Not.Null);
                Assert.That(nts.Value<String>("file"),                    Is.EqualTo(Station.ConfigFile.Path));

                // What the page draws its "Time servers" card from. A station
                // nobody has configured asks the PTB's four, so the card has
                // four to draw rather than nothing.
                Assert.That(nts["timeSources"],                           Is.Not.Null.And.Count.EqualTo(4));
                Assert.That(nts["timeSources"]?[0]?.Value<String>("hostname"),
                                                                          Is.EqualTo(Station.NTSClient.Hostname.ToString()));
                Assert.That(nts["group"]?.Value<String>("name"),          Is.EqualTo("legal"));
                Assert.That(nts["group"]?.Value<Byte>  ("minServers"),    Is.EqualTo(2));
            });

        }

        #endregion

        #region PointingTheStationAtAnotherTimeServerReplacesTheClient()

        /// <summary>
        /// An NTS client is bound to its host at construction, and the cookies
        /// and keys it holds belong to that host and to no other - so being
        /// pointed elsewhere builds a new one rather than reconfiguring this
        /// one.
        /// </summary>
        [Test]
        public async Task PointingTheStationAtAnotherTimeServerReplacesTheClient()
        {

            using var http = await SignedIn();

            var before = Station.NTSClient;

            var response = await http.PutAsync(
                                     "/api/v1/configuration/nts",
                                     JSONBody(
                                         new JProperty("hostname",   "ptbtime2.ptb.de"),
                                         new JProperty("ntsKEPort",  4460),
                                         new JProperty("ntpPort",    123)
                                     )
                                 );

            var onDisk = JObject.Parse(File.ReadAllText(Station.ConfigFile.Path));

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode,              Is.True);
                Assert.That(Station.NTSClient.Hostname.ToString(),  Does.StartWith("ptbtime2.ptb.de"));

                Assert.That(Station.NTSClient,                      Is.Not.SameAs(before),
                            "The host changed and the client did not, so it still holds the cookies of the old one.");

                // Written as the domain name it was parsed into, which is the
                // absolute form with the root label on the end - so this is
                // what a name looks like on its way back out of the file, and
                // not a stray character.
                Assert.That(onDisk["nts"]?.Value<String>("hostname"),
                            Is.EqualTo(Station.NTSClient.Hostname.ToString()));

            });

        }

        #endregion

    }

}
