/*
 * Copyright (c) 2014-2026 GraphDefined GmbH <achim.friedland@graphdefined.com>
 * This file is part of ChargingStation <https://github.com/OpenChargingCloud/ChargingStation>
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

#region Usings

using System.Collections.Concurrent;
using System.Text.RegularExpressions;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.ChargingStation.CommandLine;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A connection test asks its back end something in OCPP once it is
    /// connected - a Heartbeat - and says what was sent, what came back and
    /// how long that took: a handshake says that something took the
    /// connection, and only an answer that OCPP is spoken on it. And the
    /// command line runs such a test for a connection that is written down.
    /// </summary>
    [TestFixture]
    public class ConnectionTestRequestTests
    {

        #region (enum) Answers

        private enum Answers
        {
            TheTime,
            AnError,
            SomethingElse,
            Nothing
        }

        #endregion

        #region (class) BackEnd

        /// <summary>
        /// A back end that takes every WebSocket and answers what it is asked
        /// the way a test tells it to.
        /// </summary>
        private sealed class BackEnd : IAsyncDisposable
        {

            public WebSocketServer          Server    { get; }
            public IPPort                   Port      { get; }
            public ConcurrentQueue<JArray>  Received  { get; } = new ();

            public BackEnd(Answers Answers)
            {

                Port    = IPPort.Parse(TestPorts.Free());

                Server  = new WebSocketServer(HTTPPort:               Port,
                                              RequireAuthentication:  false,
                                              AutoStart:              true);

                Server.OnTextMessageReceived += async (timestamp, server, connection, frame, eventTrackingId, text, cancellationToken) => {

                    var message = JArray.Parse(text);
                    Received.Enqueue(message);

                    JArray? answer = Answers switch {
                                         Answers.TheTime        => new JArray(3, message[1], new JObject(
                                                                       new JProperty("currentTime", DateTimeOffset.UtcNow.AddMinutes(2).ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                                                                   )),
                                         Answers.AnError        => new JArray(4, message[1], "SecurityError", "No BootNotification of this station was accepted", new JObject()),
                                         Answers.SomethingElse  => new JArray(3, "not-what-was-asked", new JObject()),
                                         _                      => null
                                     };

                    if (answer is not null)
                        await Server.SendTextMessage(connection, answer.ToString(Formatting.None));

                };

            }

            public IEnumerable<JArray> Heartbeats
                => Received.Where(message => message[0].Value<Int32>() == 2 && message[2].Value<String>() == "Heartbeat");

            public ValueTask DisposeAsync()
                => Server.DisposeAsync();

        }

        #endregion

        #region Data

        private String            directory  = "";
        private ChargingStation?  station;
        private BackEnd?          backEnd;
        private TimeSpan          answerWithin;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory     = TestStations.TemporaryDirectory("connection-test-request");
            station       = TestStations.New(directory, TestStations.Offline);
            answerWithin  = ChargingStation.TestAnswerWithin;
        }

        [TearDown]
        public async Task TakeThemAwayAgain()
        {

            ChargingStation.TestAnswerWithin = answerWithin;

            if (station is not null)
                await station.DisposeAsync();

            if (backEnd is not null)
                await backEnd.DisposeAsync();

            station  = null;
            backEnd  = null;

            TestStations.Remove(directory);

        }

        #endregion

        #region (private) Helpers

        private String Address
            => $"ws://127.0.0.1:{backEnd!.Port}/cs001";

        private void ABackEndThatAnswers(Answers Answers)
            => backEnd = new BackEnd(Answers);

        private Task<JObject> Test(String? OCPPRequest = null)
            => station!.TestConnection("The CSMS", Address, "CSMS", OCPPRequest: OCPPRequest);

        private String Written()
        {

            Assert.That(station!.Connections.TryAddConnection("The CSMS", Address, "CSMS", false, null, null,
                                                              out var id, out var error),
                        Is.True, error);

            return id!;

        }

        private Task<String[]> TestConnection(params String[] Arguments)
            => new TestConnectionCommand(new StationCLI(station!)).Execute([ TestConnectionCommand.CommandName, .. Arguments ], CancellationToken.None);

        private static String Steps(JObject Result)
            => String.Join("\n", Result["steps"]!.Select(step => $"{step.Value<String>("level")}: {step.Value<String>("text")}"));

        #endregion


        #region AHeartbeatIsAskedAndItsAnswerSaidWithHowLongItTook()

        /// <summary>
        /// The request as it was sent, the answer as it came, the time in
        /// between - and how far the back end's clock is from the station's.
        /// </summary>
        [Test]
        public async Task AHeartbeatIsAskedAndItsAnswerSaidWithHowLongItTook()
        {

            ABackEndThatAnswers(Answers.TheTime);

            var result  = await Test();
            var ocpp    = result["ocpp"] as JObject;

            Assert.That(result.Value<Boolean>("ok"), Is.True, Steps(result));
            Assert.That(ocpp,                        Is.Not.Null, "The answer says nothing of what was asked in OCPP.");

            Assert.Multiple(() => {

                Assert.That(backEnd!.Heartbeats.Count(),                    Is.EqualTo(1),            "what the back end was asked");
                Assert.That(backEnd.Heartbeats.First().ToString(Formatting.None),
                            Is.EqualTo(ocpp!["sent"]!.ToString(Formatting.None)),                     "what the answer says was sent is what was sent");

                Assert.That(ocpp.Value<String>("request"),                  Is.EqualTo("Heartbeat"));
                Assert.That(ocpp.Value<Boolean>("answered"),                Is.True);
                Assert.That(ocpp.Value<Boolean>("error"),                   Is.False);
                Assert.That(ocpp["answer"]![0]!.Value<Int32>(),             Is.EqualTo(3));
                Assert.That(ocpp["answer"]![1]!.Value<String>(),            Is.EqualTo(ocpp["sent"]![1]!.Value<String>()));
                Assert.That(ocpp.Value<Int64>("roundTrip_ms"),              Is.InRange(0, 5000));
                Assert.That(ocpp.Value<Int64>("sentAt_ms"),                 Is.InRange(0, result.Value<Int64>("runtime_ms")));

                Assert.That(Steps(result),  Does.Contain("info: Asking in OCPP, to see whether it is spoken here: [2,\"test-"));
                Assert.That(Steps(result),  Does.Match(@"notice: Answered after \d+ ms: \[3,""test-"));
                Assert.That(Steps(result),  Does.Match(@"Its clock says \d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z, 1[12]\d{4} ms ahead of this station's\."),
                            "The back end's clock is two minutes ahead.");
                Assert.That(Steps(result),  Does.Not.Contain("Received: [3,"),  "The answer is said twice.");
                Assert.That(Steps(result),  Does.Contain("notice: Closed. The connection can be made."));

            });

        }

        #endregion

        #region AnErrorIsAnAnswer()

        /// <summary>
        /// A back end that answers a CALLERROR has read a request and answered
        /// it in OCPP: the test gets through, and says what it was told.
        /// </summary>
        [Test]
        public async Task AnErrorIsAnAnswer()
        {

            ABackEndThatAnswers(Answers.AnError);

            var result  = await Test();
            var ocpp    = result["ocpp"] as JObject;

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),        Is.True,  Steps(result));
                Assert.That(ocpp?.Value<Boolean>("answered"),   Is.True);
                Assert.That(ocpp?.Value<Boolean>("error"),      Is.True);
                Assert.That(Steps(result),                      Does.Match(@"warning: Answered after \d+ ms, with an error: \[4,""test-[0-9a-f]+"",""SecurityError"""));
                Assert.That(Steps(result),                      Does.Contain("OCPP is spoken here."));
            });

        }

        #endregion

        #region ABackEndThatDoesNotAnswerFailsTheTest(Answers)

        /// <summary>
        /// Something that takes the WebSocket and answers nothing - or
        /// something that was not asked - speaks no OCPP on it: the handshake
        /// got through, and the test does not.
        /// </summary>
        [TestCase(Answers.Nothing)]
        [TestCase(Answers.SomethingElse)]
        public async Task ABackEndThatDoesNotAnswerFailsTheTest(Object Answers)
        {

            ABackEndThatAnswers((Answers) Answers);

            ChargingStation.TestAnswerWithin = TimeSpan.FromMilliseconds(600);

            var result  = await Test();
            var ocpp    = result["ocpp"] as JObject;

            Assert.Multiple(() => {

                Assert.That(result.Value<Boolean>("ok"),        Is.False,  Steps(result));
                Assert.That(backEnd!.Heartbeats.Count(),        Is.EqualTo(1));
                Assert.That(ocpp?.Value<Boolean>("answered"),   Is.False);
                Assert.That(ocpp?["answer"],                    Is.Null);
                Assert.That(ocpp?["roundTrip_ms"],              Is.Null);

                Assert.That(Steps(result),  Does.Contain("notice: Connected"),  "The handshake got through, and is said to have.");
                Assert.That(Steps(result),  Does.Contain("error: No answer to the Heartbeat within 0.6 second(s)."));
                Assert.That(Steps(result),  Does.Contain("error: Closed. The connection can be made, and what the station asked on it was not answered."));
                Assert.That(Steps(result),  Does.Not.Contain("notice: Closed. The connection can be made."));

                if ((Answers) Answers == ConnectionTestRequestTests.Answers.SomethingElse)
                    Assert.That(Steps(result),  Does.Contain("info: Received: [3,\"not-what-was-asked\""),  "What came instead is said.");

            });

        }

        #endregion

        #region ToldToAskNothingItAsksNothing()

        /// <summary>
        /// "none" is the test as it was before it asked anything: everything
        /// up to the handshake.
        /// </summary>
        [Test]
        public async Task ToldToAskNothingItAsksNothing()
        {

            ABackEndThatAnswers(Answers.Nothing);

            var result = await Test("none");

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),  Is.True,  Steps(result));
                Assert.That(result["ocpp"],               Is.Null);
                Assert.That(backEnd!.Received,            Is.Empty);
                Assert.That(Steps(result),                Does.Not.Contain("Asking in OCPP"));
            });

        }

        #endregion

        #region WhatIsNoRequestOfATestIsRefusedInWords()

        [Test]
        public async Task WhatIsNoRequestOfATestIsRefusedInWords()
        {

            ABackEndThatAnswers(Answers.TheTime);

            var result = await Test("bootNotification");

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),  Is.False);
                Assert.That(Steps(result),                Does.Contain("error: 'bootNotification' is nothing a connection test asks in OCPP: it is 'heartbeat', which it is where nothing is said, or 'none'."));
                Assert.That(backEnd!.Received,            Is.Empty,  "It was dialled all the same.");
            });

        }

        #endregion

        #region AConnectionThatDidNotComeThatFarSaysNothingOfOCPP()

        [Test]
        public async Task AConnectionThatDidNotComeThatFarSaysNothingOfOCPP()
        {

            var result = await station!.TestConnection("Nobody", $"ws://127.0.0.1:{TestPorts.Free()}/cs001", "CSMS");

            Assert.Multiple(() => {
                Assert.That(result.Value<Boolean>("ok"),  Is.False);
                Assert.That(result["ocpp"],               Is.Null,  Steps(result));
            });

        }

        #endregion


        #region TheCommandLineTestsAConnectionThatIsWrittenDown()

        /// <summary>
        /// testconnection, by the description of a connection and by its
        /// identification: every step on a line with its milliseconds, and
        /// what came of it at the end.
        /// </summary>
        [Test]
        public async Task TheCommandLineTestsAConnectionThatIsWrittenDown()
        {

            ABackEndThatAnswers(Answers.TheTime);

            var id      = Written();

            var byName  = await TestConnection("The", "CSMS");
            var byId    = await TestConnection(id);

            Assert.Multiple(() => {

                foreach (var lines in new[] { byName, byId })
                {

                    var said = String.Join("\n", lines);

                    Assert.That(lines.Length,                              Is.GreaterThan(5),  said);
                    Assert.That(lines[..^1],                               Has.All.Match(@"^ *\d+ ms  (info   |notice |warning|error  )  \S"),  said);
                    Assert.That(lines.Any(line => Regex.IsMatch(line, @"^ *\d+ ms  notice   Answered after \d+ ms: \[3,")),  Is.True,  said);
                    Assert.That(lines[^1],                                 Does.Match(@"^OK after \d+ ms; the Heartbeat was answered within \d+ ms\.$"),  said);

                }

                Assert.That(backEnd!.Heartbeats.Count(),             Is.EqualTo(2));
                Assert.That(station!.Connections.Connections.Count,  Is.EqualTo(1),  "A test wrote something down.");

            });

        }

        #endregion

        #region TheCommandLineSaysThatATestFailed_AndAsksNothingWhereToldSo()

        [Test]
        public async Task TheCommandLineSaysThatATestFailed_AndAsksNothingWhereToldSo()
        {

            ABackEndThatAnswers(Answers.Nothing);

            ChargingStation.TestAnswerWithin = TimeSpan.FromMilliseconds(600);

            Written();

            var asked    = await TestConnection("The", "CSMS");
            var notAsked = await TestConnection("The", "CSMS", "none");

            Assert.Multiple(() => {

                Assert.That(asked[^1],                           Does.Match(@"^FAILED after \d+ ms\.$"),  String.Join("\n", asked));
                Assert.That(String.Join("\n", asked),            Does.Contain("No answer to the Heartbeat"));

                Assert.That(notAsked[^1],                        Does.Match(@"^OK after \d+ ms\.$"),      String.Join("\n", notAsked));
                Assert.That(String.Join("\n", notAsked),         Does.Not.Contain("Asking in OCPP"));

                Assert.That(backEnd!.Heartbeats.Count(),         Is.EqualTo(1));

            });

        }

        #endregion

        #region TheCommandLineSaysWhichConnectionsThereAre()

        /// <summary>
        /// Without a name, and with one that is none: how the command is
        /// used, and what is written down - and nothing is dialled.
        /// </summary>
        [Test]
        public async Task TheCommandLineSaysWhichConnectionsThereAre()
        {

            ABackEndThatAnswers(Answers.TheTime);

            var none     = await TestConnection();
            var id       = Written();
            var listed   = await TestConnection();
            var unknown  = await TestConnection("Somebody", "else");

            Assert.Multiple(() => {

                Assert.That(none,     Is.EqualTo(new[] {
                                          "Usage: testconnection <id or description of a connection> [heartbeat|none]",
                                          "No connection is written down."
                                      }));

                Assert.That(listed,   Is.EqualTo(new[] {
                                          "Usage: testconnection <id or description of a connection> [heartbeat|none]",
                                          $"  {id}  The CSMS  {Address}"
                                      }));

                Assert.That(unknown,  Is.EqualTo(new[] {
                                          "No connection is written down as 'Somebody else'.",
                                          $"  {id}  The CSMS  {Address}"
                                      }));

                Assert.That(backEnd!.Received,  Is.Empty);

            });

        }

        #endregion

    }

}
