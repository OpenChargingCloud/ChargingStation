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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What this station does with the connections it was told about, when it
    /// starts.
    /// </summary>
    /// <remarks>
    /// Nothing here needs a back end, and that is the point: what is being
    /// measured is the deciding, not the talking. Which connection is tried,
    /// which is skipped and why, what happens to the station when none of them
    /// answer - all of it is settled before a single OCPP message is sent, and
    /// all of it is what goes wrong in the field.
    ///
    /// The addresses are ports on the loopback that nothing listens on, so a
    /// connection is refused at once rather than timing out. A test that waited
    /// out a connect timeout is a test nobody runs.
    ///
    /// A refused connection does not throw: the WebSocket client has no stream
    /// to read and answers itself with a bare 400 - to no request, because it
    /// never got as far as making one. That is what tells it apart from a 400
    /// somebody sent, so what is asserted is that the back end could not be
    /// reached, rather than only that the connection did not become a
    /// WebSocket.
    /// </remarks>
    [TestFixture]
    public class DiallingTests
    {

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("dialling");

            // With the time client off, as every other station of the suite
            // has it: these are started, and a station nobody configured would
            // otherwise have its first clock check a minute later.
            station    = TestStations.New(directory, TestStations.Offline);
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            station = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private static) NowhereInParticular()

        /// <summary>
        /// A loopback address nothing is listening on.
        /// </summary>
        /// <remarks>
        /// A port that was free a moment ago and that nothing was then told to
        /// listen on. "A moment ago" is the honest phrase and it is the same
        /// bargain every free-port helper makes; what matters here is only
        /// that the connection is refused quickly rather than that it is
        /// refused for a particular reason.
        /// </remarks>
        private static String NowhereInParticular()

            => $"ws://127.0.0.1:{TestStations.FreePort()}/cs001";

        #endregion


        #region AStationComesUpAlthoughNothingAnswers()

        /// <summary>
        /// Every back end unreachable, and the station is still running.
        /// </summary>
        /// <remarks>
        /// The one that matters most. A station whose management system is
        /// down still has to open its door, run its display and answer its web
        /// interface - not least because the web interface is where somebody
        /// corrects the address that was wrong.
        /// </remarks>
        [Test]
        public async Task AStationComesUpAlthoughNothingAnswers()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS", NowhereInParticular(), "CSMS",
                                                              true, null, null, out var id, out var error),
                        Is.True, error);

            Assert.That(async () => await station.Start(), Throws.Nothing,
                        "A back end that does not answer stopped the station from starting.");

            Assert.Multiple(() => {

                Assert.That(station.WebInterfaceURL.ToString(), Is.Not.Empty,
                            "The station is up but has no web interface.");

                Assert.That(station.DialledConnections[id!], Does.Contain("could not be reached"),
                            "Nothing was recorded about the back end that did not answer.");

            });

        }

        #endregion

        #region OneThatWasNotToldToConnectIsLeftAlone()

        /// <summary>
        /// Written down and not connected, which is the whole of what the
        /// switch is for.
        /// </summary>
        /// <remarks>
        /// The two states a configured connection can be in, and the switch is
        /// the only thing that tells them apart: an address kept ready and an
        /// address in use. The one that is kept ready is not touched at all -
        /// not tried once, not recorded as having failed, nothing - because a
        /// station that quietly connects somewhere nobody meant it to is
        /// noticed by whatever is at the other end, later and by somebody
        /// else.
        /// </remarks>
        [Test]
        public async Task OneThatWasNotToldToConnectIsLeftAlone()
        {

            Assert.That(station!.Connections.TryAddConnection("Kept ready", NowhereInParticular(), "CSMSBackup",
                                                              false, null, null, out var kept, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("In use", NowhereInParticular(), "CSMS",
                                                             true, null, null, out var used, out error),
                        Is.True, error);

            await station.Start();

            Assert.Multiple(() => {

                Assert.That(station.DialledConnections.ContainsKey(kept!), Is.False,
                            "A connection that was not told to connect was tried anyway.");

                Assert.That(station.DialledConnections[used!], Does.Contain("could not be reached"),
                            "The one that was told to connect was not tried.");

            });

        }

        #endregion

        #region EveryConnectionIsDialledWhateverItIsCalled()

        /// <summary>
        /// Three connections, three kinds, and all three are tried - including
        /// the two that come after one which answered.
        /// </summary>
        /// <remarks>
        /// This is a guard rather than a feature. What a connection is called
        /// is a label, and nothing in this station reads it to decide anything:
        /// a spare does not wait for its main one to fail, a local controller
        /// does not take precedence, and one connection succeeding changes
        /// nothing for the next. Those are real questions with real answers and
        /// none of them has been answered yet - so what this test protects is
        /// the absence, which is the kind of thing that grows back.
        ///
        /// A plain WebSocket server stands in for the one that answers. It
        /// speaks no OCPP and does not need to: what is measured is which
        /// connections were attempted.
        /// </remarks>
        [Test]
        public async Task EveryConnectionIsDialledWhateverItIsCalled()
        {

            var listening = new WebSocketServer(
                                HTTPPort:   IPPort.Parse(TestStations.FreePort()),
                                AutoStart:  true
                            );

            try
            {

                Assert.That(station!.Connections.TryAddConnection(
                                "Answers",
                                $"ws://127.0.0.1:{listening.IPPort}/cs001",
                                "CSMS",
                                true, null, null, out var answering, out var error),
                            Is.True, error);

                Assert.That(station.Connections.TryAddConnection("Called a spare", NowhereInParticular(), "CSMSBackup",
                                                                 true, null, null, out var spare, out error),
                            Is.True, error);

                Assert.That(station.Connections.TryAddConnection("A local controller", NowhereInParticular(), "LocalController",
                                                                 true, null, null, out var controller, out error),
                            Is.True, error);

                await station.Start();

                Assert.Multiple(() => {

                    Assert.That(station.DialledConnections[answering!],  Does.Contain("Connected"),
                                "The one that answered was not recorded as connected.");

                    Assert.That(station.DialledConnections[spare!],      Does.Contain("could not be reached"),
                                "A connection was skipped because of what it is called.");

                    Assert.That(station.DialledConnections[controller!], Does.Contain("could not be reached"),
                                "A connection was skipped because another one answered.");

                });

            }
            finally
            {
                await listening.Shutdown();
            }

        }

        #endregion


        #region CredentialsWithoutASecretStopTheCallBeforeItIsMade()

        /// <summary>
        /// A connection that cannot prove itself is not dialled at all.
        /// </summary>
        /// <remarks>
        /// Stopped here rather than at the far end, because the far end's
        /// answer to a missing password is an HTTP 401 that says nothing about
        /// which of this station's four logins was empty.
        ///
        /// The record is reached through the file, which is the only way to
        /// arrive at credentials without a secret: the store refuses to write
        /// one down that way. That it can still be read back is deliberate - a
        /// file edited by hand is a real thing, and the station has to cope
        /// with what it finds rather than refuse to start.
        /// </remarks>
        [Test]
        public async Task CredentialsWithoutASecretStopTheCallBeforeItIsMade()
        {

            Assert.That(station!.Connections.TryAddAuthentication("CSMS login", "basic", "cs001", "a-password",
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("CSMS", NowhereInParticular(), "CSMS",
                                                            true, login, null, out var id, out error),
                        Is.True, error);

            // The secret taken back out from underneath, the way a hand-edited
            // file would arrive.
            var file = Path.Combine(station.Connections.Path, ConnectionStore.AuthenticationsFileName);

            File.WriteAllText(file, File.ReadAllText(file).Replace("\"password\": \"a-password\"", "\"password\": \"\""));

            station.Connections.Reload();

            await station.Start();

            Assert.That(station.DialledConnections[id!], Does.Contain("no secret set"),
                        "A connection with nothing to prove itself with was dialled anyway.");

        }

        #endregion

        #region ACertificateThisRuntimeCannotShowStopsTheCall()

        /// <summary>
        /// A certificate that cannot be presented is said so here, not at the
        /// TLS handshake.
        /// </summary>
        /// <remarks>
        /// .NET has no key object for an Ed448 or an SLH-DSA key, so a
        /// certificate of one is kept by this station and cannot be held up.
        /// The handshake's version of this is an error naming neither the
        /// certificate nor the reason.
        /// </remarks>
        [Test]
        public async Task ACertificateThisRuntimeCannotShowStopsTheCall()
        {

            Assert.That(station!.ClientCertificates.TryCreateKey("cs001.example.org", "ed448",
                                                                 out var key, out _, out var error),
                        Is.True, error);

            station.Connections.KnownCertificateIds = () => station.ClientCertificates.Entries.Select(entry => entry.Id);

            Assert.That(station.Connections.TryAddConnection("CSMS", NowhereInParticular(), "CSMS",
                                                            true, null, key, out var id, out error),
                        Is.True, error);

            await station.Start();

            Assert.That(station.DialledConnections[id!], Does.Contain("Not dialled"),
                        "A connection was dialled although it has no certificate it could show.");

        }

        #endregion

        #region EitherOCPPVersionCanBeAskedFor(Version)

        /// <summary>
        /// Both of this station's nodes can be the one that dials.
        /// </summary>
        /// <remarks>
        /// Which one is asked for cannot be read off a URL - the version is
        /// negotiated in the WebSocket sub-protocol, long after the node has
        /// been chosen - so it is written down, and this is what says both
        /// values reach a node that tries.
        /// </remarks>
        [Test]
        [TestCase("OCPP1.6")]
        [TestCase("OCPP2.1")]
        public async Task EitherOCPPVersionCanBeAskedFor(String Version)
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS", NowhereInParticular(), "CSMS",
                                                              true, null, null, out var id, out var error,
                                                              OCPPVersion: Version),
                        Is.True, error);

            Assert.That(station.Connections.Connections.Single().OCPPVersion,
                        Is.EqualTo(Version == "OCPP1.6" ? OCPPVersion.OCPP1_6 : OCPPVersion.OCPP2_1),
                        "The version asked for was not the version written down.");

            await station.Start();

            Assert.That(station.DialledConnections[id!], Does.Contain("could not be reached"),
                        $"An {Version} connection never reached a node that would try it.");

        }

        #endregion

        #region AnUnknownOCPPVersionIsRefusedWithTheOnesItSpeaks()

        /// <summary>
        /// What this station speaks, said in the refusal.
        /// </summary>
        [Test]
        public void AnUnknownOCPPVersionIsRefusedWithTheOnesItSpeaks()
        {

            Assert.That(station!.Connections.TryAddConnection("CSMS", "wss://csms.example.org/cs001", "CSMS",
                                                              false, null, null, out _, out var refused,
                                                              OCPPVersion: "OCPP3.0"),
                        Is.False,
                        "A version this station does not speak was written down.");

            Assert.That(refused, Does.Contain("OCPP1.6").And.Contain("OCPP2.1"));

        }

        #endregion

        #region (private) TestOf(Description)

        /// <summary>
        /// Test a connection that is written down here, by handing its fields
        /// over the way the page does.
        /// </summary>
        /// <remarks>
        /// The station tests what it is given rather than what it has stored,
        /// because the button exists beside a connection being written down
        /// for the first time as well. So even a stored one is tested by
        /// reading it out and handing it over - which is exactly what the page
        /// does with the form.
        /// </remarks>
        private Task<JObject> TestOf(String Description)
        {

            var entry = station!.Connections.Connections.Single(one => one.Description == Description);

            return station.TestConnection(entry.Description,
                                          entry.URL.ToString(),
                                          entry.ConnectionType.ToString(),
                                          ConnectionEntry.AsText(entry.OCPPVersion),
                                          entry.AutoConnect,
                                          entry.AuthenticationId,
                                          entry.CertificateId);

        }

        #endregion

        #region ATestSaysWhatHappenedAndLeavesNothingBehind()

        /// <summary>
        /// The button beside a connection: one attempt, everything that
        /// happened written down, and nothing still connected afterwards.
        /// </summary>
        /// <remarks>
        /// The last part is the one worth measuring. A test that left its
        /// client behind in the OCPP node would be a test that changed the
        /// station, and pressing the button twice would leave two - which is
        /// exactly the sort of thing nobody notices until a back end starts
        /// complaining about duplicate stations.
        /// </remarks>
        [Test]
        public async Task ATestSaysWhatHappenedAndLeavesNothingBehind()
        {

            Assert.That(station!.Connections.TryAddConnection("Kept ready", NowhereInParticular(), "CSMSBackup",
                                                              false, null, null, out _, out var error),
                        Is.True, error);

            await station.Start();

            var result = await TestOf("Kept ready");

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"), Is.False,
                            "A connection to nowhere was reported as working.");

                Assert.That(result["steps"]?.Count(), Is.GreaterThan(2),
                            "A test that says almost nothing is a test nobody can act on.");

                Assert.That(result["steps"]!.Values<JObject>().Any(step => (step!.Value<String>("text") ?? "").Contains("Nothing accepted a TCP connection")),
                            Is.True,
                            "A test against a port nothing is listening on did not say so plainly.");

                Assert.That(station.OCPPWebSocketClientCount, Is.Zero,
                            "The test left a WebSocket client behind in one of the OCPP nodes.");

            });

        }

        #endregion

        #region ATestThatGetsThroughStaysConnectedAndThenLetsGo()

        /// <summary>
        /// Something that answers: the test connects, holds on briefly, and
        /// closes again.
        /// </summary>
        /// <remarks>
        /// The holding is the part that costs time and the part that is worth
        /// having - a back end that greets its stations needs a moment to do
        /// it, and a test that hung up at the handshake would never see the
        /// greeting. So the elapsed time is asserted: a test that came back
        /// instantly did not wait, whatever else it did.
        /// </remarks>
        [Test]
        public async Task ATestThatGetsThroughStaysConnectedAndThenLetsGo()
        {

            var listening = new WebSocketServer(
                                HTTPPort:   IPPort.Parse(TestStations.FreePort()),
                                AutoStart:  true
                            );

            try
            {

                Assert.That(station!.Connections.TryAddConnection(
                                "Answers",
                                $"ws://127.0.0.1:{listening.IPPort}/cs001",
                                "CSMS",
                                false, null, null, out _, out var error),
                            Is.True, error);

                await station.Start();

                var result = await TestOf("Answers");

                Assert.Multiple(() => {

                    Assert.That(result.Value<Boolean>("ok"), Is.True,
                                $"A connection that answers was reported as failing: {result["steps"]}");

                    Assert.That(result.Value<Int64>("runtime_ms"),
                                Is.GreaterThanOrEqualTo((Int64) ChargingStation.TestHoldsFor.TotalMilliseconds),
                                "The test did not stay connected at all.");

                    Assert.That(station.OCPPWebSocketClientCount, Is.Zero,
                                "The test left a WebSocket client behind in one of the OCPP nodes.");

                });

            }
            finally
            {
                await listening.Shutdown();
            }

        }

        #endregion

        #region AHalfFilledFormIsRefusedInTheSameWordsAsWritingItDown()

        /// <summary>
        /// The button beside a form somebody is still typing into.
        /// </summary>
        /// <remarks>
        /// One place decides what a connection has to have, so pressing Test
        /// with the URL still empty gets the sentence that writing it down
        /// would have given, rather than a socket failing with something less
        /// useful.
        /// </remarks>
        [Test]
        public async Task AHalfFilledFormIsRefusedInTheSameWordsAsWritingItDown()
        {

            await station!.Start();

            var result = await station.TestConnection("Half filled in", "", "CSMS");

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"), Is.False);
                Assert.That(result["steps"]?.First?.Value<String>("text"), Does.Contain("somewhere to go"));
            });

        }

        #endregion

        #region ATestDoesNotRunWhatCannotWork()

        /// <summary>
        /// Credentials with no secret stop the test before a socket is opened,
        /// the same as they stop a real connection.
        /// </summary>
        /// <remarks>
        /// One place decides what a connection proves itself with, so the test
        /// and the real thing cannot come to disagree - a test that proved
        /// itself differently from the connection it is testing would be a
        /// test of something else.
        /// </remarks>
        [Test]
        public async Task ATestDoesNotRunWhatCannotWork()
        {

            Assert.That(station!.Connections.TryAddAuthentication("CSMS login", "basic", "cs001", "a-password",
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection("CSMS", NowhereInParticular(), "CSMS",
                                                            false, login, null, out _, out error),
                        Is.True, error);

            var file = Path.Combine(station.Connections.Path, ConnectionStore.AuthenticationsFileName);

            File.WriteAllText(file, File.ReadAllText(file).Replace("\"password\": \"a-password\"", "\"password\": \"\""));

            station.Connections.Reload();

            await station.Start();

            var result = await TestOf("CSMS");

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"), Is.False);

                Assert.That(result["steps"]!.Values<JObject>().Any(step => (step!.Value<String>("text") ?? "").Contains("no secret set")),
                            Is.True,
                            "The test did not say why it would not even try.");

            });

        }

        #endregion

        #region SomethingNeverWrittenDownCanBeTested()

        /// <summary>
        /// The button in the form where a connection is being written down for
        /// the first time.
        /// </summary>
        /// <remarks>
        /// Which is the case the button is most worth having for: a connection
        /// that has never been saved has never been tried, and finding out
        /// that the address is wrong belongs before pressing save rather than
        /// on the day it has to work.
        ///
        /// Nothing is stored by testing, and this checks that too - a test
        /// that quietly wrote down what it tested would turn a look into a
        /// commitment.
        /// </remarks>
        [Test]
        public async Task SomethingNeverWrittenDownCanBeTested()
        {

            await station!.Start();

            var result = await station.TestConnection("Not saved anywhere",
                                                      NowhereInParticular(),
                                                      "CSMS",
                                                      "OCPP1.6");

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"), Is.False,
                            "A connection to nowhere was reported as working.");

                Assert.That(result["steps"]!.Values<JObject>().Any(step => (step!.Value<String>("text") ?? "").Contains("OCPP1.6")),
                            Is.True,
                            "The version handed in was not the one the test worked from.");

                Assert.That(station.Connections.Connections, Is.Empty,
                            "Testing a connection wrote it down.");

                Assert.That(station.OCPPWebSocketClientCount, Is.Zero,
                            "The test left a WebSocket client behind in one of the OCPP nodes.");

            });

        }

        #endregion

        #region NothingConfiguredIsNotAnError()

        /// <summary>
        /// A station that was told to dial nowhere starts and says so.
        /// </summary>
        [Test]
        public async Task NothingConfiguredIsNotAnError()
        {

            Assert.That(async () => await station!.Start(), Throws.Nothing);

            Assert.That(station!.DialledConnections, Is.Empty,
                        "A station with no connections configured recorded one anyway.");

        }

        #endregion

    }

}
