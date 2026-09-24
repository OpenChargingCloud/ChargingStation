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

using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a station makes of an "nts" section: the section put into effect
    /// on top of the group of time servers it already has.
    /// </summary>
    /// <remarks>
    /// Beside the tests of the section on its own, because the rule that
    /// matters here - what a section does not mention is left as it is - can
    /// only be seen against something that is already there.
    ///
    /// The stations are built and never started. The constructor is what
    /// applies the file, and it is Start() that would put a timer on the
    /// network to ask the servers.
    /// </remarks>
    [TestFixture]
    public class NTSGroupInEffectTests
    {

        #region Data

        private String directory = default!;

        private String ConfigurationPath
            => Path.Combine(directory, "configuration.json");

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeADirectory()
        {
            directory = TestStations.TemporaryDirectory("nts-in-effect");
        }

        [TearDown]
        public void RemoveTheDirectory()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region (helper) Station(Configuration = null)

        /// <summary>
        /// A station as it stands after reading this configuration file, or
        /// after reading none.
        /// </summary>
        private ChargingStation Station(String? Configuration = null)

            => TestStations.New(directory,
                                Configuration is not null
                                    ? JObject.Parse(Configuration)
                                    : null);

        #endregion


        #region AQuorumOnItsOwnHoldsTheServersInEffectToIt()

        /// <summary>
        /// "minServers" without a list is about the servers the station has.
        /// </summary>
        /// <remarks>
        /// It used to count only beside a list or a hostname: the file was read,
        /// the start reported NTS configuration, and the default four went on
        /// being held to two.
        /// </remarks>
        [Test]
        public async Task AQuorumOnItsOwnHoldsTheServersInEffectToIt()
        {

            await using var station = Station("""{ "nts": { "minServers": 3 } }""");

            Assert.Multiple(() => {
                Assert.That(station.TimeSources.Sources.Count(),  Is.EqualTo(4),  "the servers were not mentioned, so they are the default four");
                Assert.That(station.TimeSources.MinServers,       Is.EqualTo(3),  "the quorum was read and changed nothing");
            });

        }

        #endregion

        #region ADeviationOnItsOwnAppliesToTheServersInEffect()

        [Test]
        public async Task ADeviationOnItsOwnAppliesToTheServersInEffect()
        {

            await using var station = Station("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.Multiple(() => {
                Assert.That(station.TimeSources.Sources.Count(),  Is.EqualTo(4));
                Assert.That(station.TimeSources.MinServers,       Is.EqualTo(2));
                Assert.That(station.TimeSources.MaxDeviation,     Is.EqualTo(TimeSpan.FromSeconds(0.5)),  "the deviation was read and changed nothing");
            });

        }

        #endregion

        #region AQuorumTheServersInEffectCannotReachStopsTheStart()

        /// <summary>
        /// Five of four is refused while the file is read, as five of a list of
        /// four always was.
        /// </summary>
        [Test]
        public void AQuorumTheServersInEffectCannotReachStopsTheStart()
        {

            var problem = Assert.Throws<InvalidOperationException>(() => Station("""{ "nts": { "minServers": 5 } }"""));

            Assert.That(problem?.Message,  Does.Contain("minServers").And.Contain(ConfigurationPath));

        }

        #endregion

        #region AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()

        /// <summary>
        /// And the same from the page: refused, and the file left as it was, so
        /// that the next start does not stop over what was refused.
        /// </summary>
        [Test]
        public async Task AQuorumOnItsOwnIsRefusedBeforeItIsWrittenDown()
        {

            await using var station = Station();

            Assert.Multiple(() => {

                Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 5 }"""), out var error),  Is.False);
                Assert.That(error,                                                                                   Does.Contain("minServers"));

                Assert.That(!File.Exists(ConfigurationPath) || !File.ReadAllText(ConfigurationPath).Contains("minServers"),
                            Is.True,
                            "a refused quorum was written down all the same");

                Assert.That(station.TimeSources.MinServers,  Is.EqualTo(2));

            });

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "minServers": 3 }"""), out var unexpected),  Is.True,  unexpected);
            Assert.That(station.TimeSources.MinServers,                                                                  Is.EqualTo(3));

        }

        #endregion

        #region AListAfterALoneHostnameIsHeldToTheQuorumAgain()

        /// <summary>
        /// The quorum a group of one has to settle for is not carried over to
        /// the four that come after it.
        /// </summary>
        /// <remarks>
        /// Read from the file at the next start, the same section holds the four
        /// to two. A running station that held them to one would be a different
        /// station from the one that file describes.
        /// </remarks>
        [Test]
        public async Task AListAfterALoneHostnameIsHeldToTheQuorumAgain()
        {

            await using var station = Station();

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "hostname": "ptbtime1.ptb.de" }"""), out var error),  Is.True,  error);
            Assert.That(station.TimeSources.MinServers,                                                                          Is.EqualTo(1),  "one server cannot be held to two");

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""
                            {
                                "servers": [ "ptbtime1.ptb.de", "ptbtime2.ptb.de",
                                             "ptbtime3.ptb.de", "ptbtime4.ptb.de" ]
                            }
                            """), out error),  Is.True,  error);

            Assert.That(station.TimeSources.MinServers,  Is.EqualTo(2),  "the group of one's quorum was carried over to four");

        }

        #endregion

        #region ASaveOfPartOfTheSectionLeavesTheRestInEffect()

        /// <summary>
        /// The switch on the page sends "enabled" and nothing else, and the
        /// rest of what the file said stays in effect.
        /// </summary>
        /// <remarks>
        /// It used to be replaced by what was sent: how often to check and who
        /// stands behind the time went back to their defaults, and came back
        /// only when the next start read the file again - so that switching NTS
        /// off and on again from the page took the station's claim to legal
        /// time away until then.
        /// </remarks>
        [Test]
        public async Task ASaveOfPartOfTheSectionLeavesTheRestInEffect()
        {

            await using var station = Station("""{ "nts": { "checkEverySeconds": 600, "legalTimeAuthority": "PTB" } }""");

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "enabled": true }"""), out var error),  Is.True,  error);

            Assert.Multiple(() => {
                Assert.That(station.TimeCheckEvery,      Is.EqualTo(TimeSpan.FromSeconds(600)),  "the interval went back to its default");
                Assert.That(station.LegalTimeAuthority,  Is.EqualTo("PTB"),                      "the authority was forgotten");
            });

        }

        #endregion

        #region AListShorterThanTheQuorumIsRefusedAndNotWritten()

        /// <summary>
        /// A server deleted or switched off below the quorum the file holds is
        /// refused, and the file is left as it was.
        /// </summary>
        /// <remarks>
        /// Each half was fine on its own - the quorum in the file, the list that
        /// was sent - and merged they made a section the next start refuses. A
        /// save that is accepted and then stops the station is the one thing
        /// worse than a save that is refused.
        /// </remarks>
        [Test]
        public async Task AListShorterThanTheQuorumIsRefusedAndNotWritten()
        {

            await using var station = Station("""
                                          { "nts": { "servers": [ "a.example", "b.example", "c.example" ], "minServers": 3 } }
                                          """);

            var before = File.ReadAllText(ConfigurationPath);

            Assert.Multiple(() => {

                Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var deleted),
                            Is.False,
                            "a server was deleted below the quorum");

                Assert.That(deleted,  Does.Contain("minServers"));

                Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""
                                { "servers": [ "a.example", "b.example", { "hostname": "c.example", "enabled": false } ] }
                                """), out _),
                            Is.False,
                            "a server was switched off below the quorum");

                Assert.That(File.ReadAllText(ConfigurationPath),         Is.EqualTo(before),  "a refused save was written down");
                Assert.That(station.TimeSources.Sources.Count(),          Is.EqualTo(3));

            });

        }

        #endregion

        #region EveryServerIsListedWithItsPorts()

        /// <summary>
        /// The list the page edits and sends back whole has every server in it,
        /// the switched-off ones included, with the ports each is asked on.
        /// </summary>
        /// <remarks>
        /// It used to list the bands, which have only the servers switched on:
        /// a page sending back what it was shown would have deleted every
        /// server that was switched off.
        /// </remarks>
        [Test]
        public async Task EveryServerIsListedWithItsPorts()
        {

            await using var station = Station("""
                                          { "nts": { "servers": [ "a.example",
                                                                  { "hostname": "b.example", "ntsKEPort": 4461, "enabled": false } ] } }
                                          """);

            var listed = station.NTSConfigurationJSON()["timeSources"] as JArray;

            Assert.Multiple(() => {
                Assert.That(listed,                                      Has.Count.EqualTo(2),  "the switched-off server is missing");
                Assert.That(listed?[1]?.Value<String>("hostname"),       Is.EqualTo("b.example."));
                Assert.That(listed?[1]?.Value<Boolean>("enabled"),       Is.False);
                Assert.That(listed?[1]?.Value<Int32>("ntsKEPort"),       Is.EqualTo(4461));
                Assert.That(listed?[0]?.Value<Int32>("ntpPort"),         Is.EqualTo(123));
            });

        }

        #endregion

        #region ARunningStationPutsANewIntervalIntoItsClockCheckAtOnce()

        /// <summary>
        /// How often the clock is checked, and whether it is, are put into the
        /// check of a running station at once - not at its next start.
        /// </summary>
        /// <remarks>
        /// The check runs on a timer set at the start, and a save used to change
        /// only the setting: the page said "in effect" about an interval the
        /// timer did not have until the next start. Seen here in the line the
        /// check writes whenever it is set. Started, and the first check is a
        /// minute in, so a test that is over long before that asks no time
        /// server anything.
        /// </remarks>
        [Test]
        public async Task ARunningStationPutsANewIntervalIntoItsClockCheckAtOnce()
        {

            await using var station = Station();

            await station.Start();

            var before = station.Log.LastId;

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "checkEverySeconds": 600 }"""), out var error),  Is.True,  error);
            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "enabled": false }"""),          out error),      Is.True,  error);

            var said = station.Log.Recent(50, before, "clock").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(said,  Has.Some.Contains("will be checked against").And.Contains("every 10 minute(s)"),  String.Join(" | ", said));
                Assert.That(said,  Has.Some.Contains("is not being checked: NTS is switched off"),                   String.Join(" | ", said));
            });

        }

        #endregion

        #region AServerOfTheGroupIsTestedOnItsOwnPorts()

        /// <summary>
        /// The detailed test asks a server of the group on the ports that server
        /// is configured with, and not on those of the single client.
        /// </summary>
        /// <remarks>
        /// It used to take the single client's ports for every server, so a
        /// server with a port of its own was asked on the usual one and reported
        /// as not answering. Name resolution is switched off here, so that
        /// nothing goes out: the first step names the ports before anything is
        /// asked, and the rest fails at once.
        /// </remarks>
        [Test]
        public async Task AServerOfTheGroupIsTestedOnItsOwnPorts()
        {

            await using var station = Station("""
                                          { "dns": { "enabled": false },
                                            "nts": { "servers": [ "a.example",
                                                                  { "hostname": "b.example", "ntsKEPort": 4461, "ntpPort": 1234 } ],
                                                     "timeoutSeconds": 1 } }
                                          """);

            var result = await station.TestTimeServerAsync("b.example");

            Assert.That(result["steps"]?[0]?.Value<String>("text"),
                        Does.Contain("key exchange on port 4461").And.Contain("time on port 1234"));

        }

        #endregion

        #region TheTestWritesTheNameAsItIsRead()

        /// <summary>
        /// The detailed test names the server the way everything else this
        /// station prints does: without the root's dot.
        /// </summary>
        /// <remarks>
        /// "Asking ptbtime2.ptb.de.: key exchange on port 4460" - the name is
        /// exact with the dot, and in the middle of a sentence it reads like a
        /// typing mistake. The steps and the log lines use the name as it is
        /// read; the name the result carries as data is left as it is. Name
        /// resolution is switched off, so that nothing goes out.
        /// </remarks>
        [Test]
        public async Task TheTestWritesTheNameAsItIsRead()
        {

            await using var station = Station("""
                                          { "dns": { "enabled": false },
                                            "nts": { "servers": [ "a.example", "b.example" ], "timeoutSeconds": 1 } }
                                          """);

            var before  = station.Log.LastId;
            var result  = await station.TestTimeServerAsync("b.example");
            var said    = station.Log.Recent(50, before, "test").Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(result["steps"]?[0]?.Value<String>("text"),  Does.StartWith("Asking b.example:"));
                Assert.That(said,                                          Has.Some.EqualTo("NTS test: asking b.example ..."),  String.Join(" | ", said));
            });

        }

        #endregion

        #region TheQuorumWantedAndTheQuorumHeldAreBothShown()

        /// <summary>
        /// A lone hostname holds the group to one, and the page shows both that
        /// and the two that is wanted, which the next list is held to.
        /// </summary>
        [Test]
        public async Task TheQuorumWantedAndTheQuorumHeldAreBothShown()
        {

            await using var station = Station("""{ "nts": { "hostname": "a.example" } }""");

            var shown = station.NTSConfigurationJSON();

            Assert.Multiple(() => {
                Assert.That(shown["settings"]?.Value<Int32>("minServers"),  Is.EqualTo(2),  "the quorum wanted");
                Assert.That(shown["group"]?.   Value<Int32>("minServers"),  Is.EqualTo(1),  "the quorum one server can be held to");
            });

        }

        #endregion

        #region AChangeToOneServerIsWrittenDown()

        /// <summary>
        /// A server given another priority or a port of its own is a change to
        /// the group, and the log book hears of it.
        /// </summary>
        /// <remarks>
        /// The log compared the names of the servers switched on, in the order
        /// they are asked. A priority that put a server into a band of its own
        /// at the end, and a port of its own, left those just as they were -
        /// and so the group changed without a line saying so.
        /// </remarks>
        [Test]
        public async Task AChangeToOneServerIsWrittenDown()
        {

            await using var station = Station("""{ "nts": { "servers": [ "a.example", "b.example" ] } }""");

            var before = station.Log.LastId;

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""
                            { "servers": [ "a.example", { "hostname": "b.example", "priority": 5, "ntsKEPort": 4461 } ] }
                            """), out var error),
                        Is.True,
                        error);

            var said = station.Log.Recent(50, before, null).Select(entry => entry.Message).ToArray();

            Assert.That(said,  Has.Some.Contains("time servers = a.example, b.example (priority 5, NTS-KE port 4461)"),
                        String.Join(" | ", said));

        }

        #endregion

        #region ADeviationStaysWhenTheServersChange()

        /// <summary>
        /// What a section does not mention is left as it is - the deviation
        /// too, when the servers are replaced.
        /// </summary>
        [Test]
        public async Task ADeviationStaysWhenTheServersChange()
        {

            await using var station = Station("""{ "nts": { "maxDeviationSeconds": 0.5 } }""");

            Assert.That(station.TryUpdateNTSConfiguration(JObject.Parse("""{ "servers": [ "a.example", "b.example" ] }"""), out var error),  Is.True,  error);

            Assert.That(station.TimeSources.MaxDeviation,  Is.EqualTo(TimeSpan.FromSeconds(0.5)));

        }

        #endregion

        #region TheOverviewNamesTheGroupAndNotTheTestClient()

        /// <summary>
        /// The Configuration page's time card names the servers the clock is
        /// set by - all of them, the one switched off as well - and not the
        /// host of the single client the detailed test starts from.
        /// </summary>
        /// <remarks>
        /// It led with "NTS: ptbtime1.ptb.de." above the servers switched on:
        /// one server, which was the test's, above the group that was asked.
        /// </remarks>
        [Test]
        public async Task TheOverviewNamesTheGroupAndNotTheTestClient()
        {

            await using var station = Station("""
                                          { "nts": { "servers": [ "a.example",
                                                                  { "hostname": "b.example", "priority": 5 },
                                                                  { "hostname": "c.example", "enabled": false } ] } }
                                          """);

            var time = station.ConfigurationJSON()["time"] as JObject;

            Assert.Multiple(() => {

                Assert.That(time?.Value<String>("timeServers"),  Is.EqualTo("a.example, b.example (priority 5), c.example (switched off)"));
                Assert.That(time?.Value<Boolean>("ntsEnabled"),  Is.True);
                Assert.That(time?.ContainsKey("nts"),            Is.False,  "the test client's host is named again");

                // There and empty while nothing has been synchronised, so that
                // the card says "-" rather than leaving the line out.
                Assert.That(time?["lastSync"]?.      Type,       Is.EqualTo(JTokenType.Null));
                Assert.That(time?["lastSyncResult"]?.Type,       Is.EqualTo(JTokenType.Null));

            });

        }

        #endregion

        #region TheClockIsCheckedAgainstTheGroupAndNotTheTestClient()

        /// <summary>
        /// Against whom the clock is checked, as its JSON says it: the group,
        /// its servers switched on in the order they are asked, and how many of
        /// them have to answer.
        /// </summary>
        /// <remarks>
        /// The servers used to come with their root dots and without the group
        /// or its quorum; the log line at the start, which names the same
        /// servers, has always left the dots out. Both come from one place now.
        /// "server" stays for a group of one, because that is what the display
        /// shows beside the time.
        /// </remarks>
        [Test]
        public async Task TheClockIsCheckedAgainstTheGroupAndNotTheTestClient()
        {

            await using var station = Station("""
                                          { "nts": { "servers": [ { "hostname": "b.example", "priority": 5 },
                                                                  "a.example",
                                                                  { "hostname": "c.example", "enabled": false } ] } }
                                          """);

            var nts = (JObject) station.ClockJSON()["nts"]!;

            Assert.Multiple(() => {

                Assert.That(nts.Value<String>("group"),        Is.EqualTo("legal"));

                Assert.That(nts["servers"]!.Values<String>(),  Is.EqualTo(new[] { "a.example", "b.example" }),
                            "switched on, in the order their bands are asked, and without the root's dot");

                Assert.That(nts.Value<Int32>("minServers"),    Is.EqualTo(2));

                Assert.That(nts["server"]?.Type,               Is.EqualTo(JTokenType.Null),  "one server named for a group of two");

            });

        }

        #endregion

        #region AClockThatIsNotCheckedNamesNobody()

        /// <summary>
        /// Switched off, the clock is checked against nobody, and says so -
        /// rather than naming servers that are not asked.
        /// </summary>
        [Test]
        public async Task AClockThatIsNotCheckedNamesNobody()
        {

            await using var station = Station("""{ "nts": { "enabled": false } }""");

            var nts = (JObject) station.ClockJSON()["nts"]!;

            Assert.Multiple(() => {
                Assert.That(nts.Value<Boolean>("enabled"),  Is.False);
                Assert.That(nts["group"]?.     Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["servers"]?.   Type,        Is.EqualTo(JTokenType.Null));
                Assert.That(nts["minServers"]?.Type,        Is.EqualTo(JTokenType.Null));
            });

        }

        #endregion

    }

}
