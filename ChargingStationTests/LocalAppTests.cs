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
using System.Net.WebSockets;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The local app server: an app on a phone in the station's own network
    /// starting and stopping a charge with a card's UID, over HTTP and over
    /// the WebSocket beside it.
    /// </summary>
    /// <remarks>
    /// Nothing here is real but the station: the cards are UIDs typed into a
    /// test, the app is an HttpClient and a ClientWebSocket, and the charge is
    /// the station's own simulated one - an outlet the display shows as
    /// occupied, and the OCPP node told that it is busy. What is shown is that
    /// a start and a stop sent to this server are processed, the same way
    /// through either door, and that nothing else is on it.
    ///
    /// Two outlets, so that an app has to say which one it means; a fake
    /// reader for the whole station, so that a card held against it can be
    /// compared with the app holding up the same card; and an operator with
    /// one provider, so that a card of that provider has a name on the
    /// display.
    /// </remarks>
    [TestFixture]
    public class LocalAppTests : AChargingStationTests
    {

        #region Data

        /// <summary>
        /// A card of the provider this station knows.
        /// </summary>
        private const String CardOfKnown    = "04A2B3C4D5E6F7";

        /// <summary>
        /// A card nobody here has heard of.
        /// </summary>
        private const String CardOfUnknown  = "11223344";

        #endregion

        #region What this station is made of

        protected override Boolean WithLocalApp
            => true;

        protected override JObject Configuration

            => new (

                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   )),

                   new JProperty("evses", new JArray(
                       new JObject(
                           new JProperty("id",                  1),
                           new JProperty("connectors",          new JArray("sType2")),
                           new JProperty("maxPower_kW",         22.0),
                           new JProperty("operative",           true),
                           new JProperty("physicalReference",   "A")
                       ),
                       new JObject(
                           new JProperty("id",                  2),
                           new JProperty("connectors",          new JArray("cCCS2")),
                           new JProperty("maxPower_kW",         150.0),
                           new JProperty("operative",           true),
                           new JProperty("physicalReference",   "B")
                       )
                   )),

                   new JProperty("rfid", new JArray(
                       new JObject(
                           new JProperty("id",       "reader"),
                           new JProperty("kind",     "GraphDefined.FakeRFID"),
                           new JProperty("evse",     null),
                           new JProperty("enabled",  true)
                       )
                   )),

                   new JProperty("operator", new JObject(
                       new JProperty("name",      "Stadtwerke Musterstadt"),
                       new JProperty("language",  "de"),
                       new JProperty("emps",      new JArray(
                           new JObject(
                               new JProperty("id",             "emp-one"),
                               new JProperty("name",           "Elektro Mobil GmbH"),
                               new JProperty("tokenPrefixes",  new JArray("04A2"))
                           )
                       ))
                   ))

               );

        #endregion


        #region Over HTTP

        /// <summary>
        /// A card's UID starts a charge, and the answer carries the handle to
        /// stop it with.
        /// </summary>
        [Test]
        public async Task AnAppStartsAChargeWithACardsUID()
        {

            using var app = AtTheLocalApp();

            var (status, answer) = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", "04 a2 b3 c4 d5 e6 f7")));

            Assert.Multiple(() => {

                Assert.That(status,                             Is.EqualTo(HttpStatusCode.OK), answer.ToString());
                Assert.That(answer.Value<Boolean>("started"),   Is.True);
                Assert.That(answer.Value<Int32>  ("evse"),      Is.EqualTo(1));

                // The UID as a reader would have read it, whatever it was typed as.
                Assert.That(answer.Value<String> ("uid"),       Is.EqualTo(CardOfKnown));

                // A hundred and twenty-eight bits, as hex.
                Assert.That(answer.Value<String> ("sessionId"), Does.Match("^[0-9a-f]{32}$"));

                var outlet = OutletOnTheDisplay(1);

                Assert.That(outlet.Value<String>("status"),                        Is.EqualTo("occupied"));
                Assert.That(outlet["session"]?.Value<String>("method"),            Is.EqualTo("LocalApp"));
                Assert.That(outlet["session"]?["provider"]?.Value<String>("name"), Is.EqualTo("Elektro Mobil GmbH"));

                Assert.That(OutletOnTheDisplay(2).Value<String>("status"),         Is.EqualTo("available"),
                            "The app asked for EVSE 1 and something happened at EVSE 2.");

            });

        }

        /// <summary>
        /// The handle stops it, and the outlet is free again.
        /// </summary>
        [Test]
        public async Task ItsHandleStopsIt()
        {

            using var app = AtTheLocalApp();

            var sessionId = await Started(app, 1, CardOfKnown);

            var (status, answer) = await Stop(app, sessionId);

            Assert.Multiple(() => {
                Assert.That(status,                             Is.EqualTo(HttpStatusCode.OK), answer.ToString());
                Assert.That(answer.Value<Boolean>("stopped"),   Is.True);
                Assert.That(answer.Value<Int32>  ("evse"),      Is.EqualTo(1));
                Assert.That(answer.Value<String> ("sessionId"), Is.EqualTo(sessionId));
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"));
            });

        }

        /// <summary>
        /// A handle that stops nothing is not found - whether it never was one,
        /// or its session has ended.
        /// </summary>
        [Test]
        public async Task AHandleThatStopsNothingIsNotFound()
        {

            using var app = AtTheLocalApp();

            var (never, _) = await Stop(app, "0123456789abcdef0123456789abcdef");

            var sessionId  = await Started(app, 1, CardOfKnown);

            await Stop(app, sessionId);

            var (again, _) = await Stop(app, sessionId);

            Assert.Multiple(() => {
                Assert.That(never, Is.EqualTo(HttpStatusCode.NotFound));
                Assert.That(again, Is.EqualTo(HttpStatusCode.NotFound));
            });

        }

        /// <summary>
        /// An outlet somebody is charging at is refused, not taken over - and
        /// not stopped either, which is what the same card at a reader would
        /// have done.
        /// </summary>
        [Test]
        public async Task AnOccupiedOutletIsNotTakenOver()
        {

            using var app = AtTheLocalApp();

            var first = await Started(app, 1, CardOfKnown);

            var (other, _) = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", CardOfUnknown)));
            var (same,  _) = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", CardOfKnown)));

            Assert.Multiple(() => {
                Assert.That(other, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(same,  Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("occupied"));
            });

            var (stopped, _) = await Stop(app, first);

            Assert.That(stopped, Is.EqualTo(HttpStatusCode.OK),
                        "The first session was no longer the one charging there.");

        }

        /// <summary>
        /// What cannot be read as a start is refused as one, and starts
        /// nothing.
        /// </summary>
        [Test]
        public async Task WhatIsNoStartIsRefused()
        {

            using var app = AtTheLocalApp();

            var noUID     = await Start(app, new JObject(new JProperty("evse", 1)));
            var noCard    = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", "not a card")));
            var noNumber  = await Start(app, new JObject(new JProperty("evse", "one"), new JProperty("uid", CardOfKnown)));
            var noOutlet  = await Start(app, new JObject(new JProperty("evse", 9), new JProperty("uid", CardOfKnown)));
            var noWhich   = await Start(app, new JObject(new JProperty("uid", CardOfKnown)));
            var noJSON    = await app.PostAsync("localStart", new StringContent("uid=04A2B3C4", Encoding.UTF8, "application/json"));

            Assert.Multiple(() => {

                Assert.That(noUID.   Status, Is.EqualTo(HttpStatusCode.BadRequest), noUID.   Answer.ToString());
                Assert.That(noCard.  Status, Is.EqualTo(HttpStatusCode.BadRequest), noCard.  Answer.ToString());
                Assert.That(noNumber.Status, Is.EqualTo(HttpStatusCode.BadRequest), noNumber.Answer.ToString());
                Assert.That(noOutlet.Status, Is.EqualTo(HttpStatusCode.BadRequest), noOutlet.Answer.ToString());
                Assert.That(noWhich. Status, Is.EqualTo(HttpStatusCode.BadRequest), noWhich. Answer.ToString());
                Assert.That(noJSON.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));

                // Two outlets, and nothing standing beside one to say which.
                Assert.That(noWhich.Answer.Value<String>("error"), Does.Contain("evse"));
                Assert.That(noOutlet.Answer.Value<String>("error"), Does.Contain("no EVSE 9"));

                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"));
                Assert.That(OutletOnTheDisplay(2).Value<String>("status"), Is.EqualTo("available"));

            });

        }

        /// <summary>
        /// A one-time password or a certificate is refused rather than taken
        /// along unchecked: this station checks neither yet, and an app that
        /// sends one believes it is being checked.
        /// </summary>
        [Test]
        public async Task AOneTimePasswordOrACertificateIsNotTakenAsChecked()
        {

            using var app = AtTheLocalApp();

            var withTOTP         = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", CardOfKnown), new JProperty("totp", "123456")));
            var withCertificate  = await Start(app, new JObject(new JProperty("evse", 1), new JProperty("uid", CardOfKnown), new JProperty("certificate", "MIIB...")));

            Assert.Multiple(() => {
                Assert.That(withTOTP.       Status, Is.EqualTo(HttpStatusCode.NotImplemented), withTOTP.       Answer.ToString());
                Assert.That(withCertificate.Status, Is.EqualTo(HttpStatusCode.NotImplemented), withCertificate.Answer.ToString());
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"),
                            "A start that carried something unchecked went ahead anyway.");
            });

        }

        /// <summary>
        /// An outlet held for somebody lets in the app holding up their card,
        /// and no other - and the hold has then done its job.
        /// </summary>
        [Test]
        public async Task AHeldOutletLetsInOnlyTheCardItIsHeldFor()
        {

            Assert.That(
                Station.TryReserveNow(
                    new JObject(
                        new JProperty("evse",     2),
                        new JProperty("idToken",  CardOfKnown),
                        new JProperty("minutes",  15)
                    ),
                    out _,
                    out var holding
                ),
                Is.True,
                holding
            );

            using var app = AtTheLocalApp();

            var (somebodyElse, refusal) = await Start(app, new JObject(new JProperty("evse", 2), new JProperty("uid", CardOfUnknown)));
            var (theirs,       _)       = await Start(app, new JObject(new JProperty("evse", 2), new JProperty("uid", CardOfKnown)));

            Assert.Multiple(() => {
                Assert.That(somebodyElse, Is.EqualTo(HttpStatusCode.Conflict));
                Assert.That(refusal.Value<String>("error"), Does.Contain("reserved"));
                Assert.That(theirs,       Is.EqualTo(HttpStatusCode.OK));
                Assert.That(OutletOnTheDisplay(2)["reservation"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null),
                            "The hold was still standing after the card it was held for had started.");
            });

        }

        /// <summary>
        /// The app holds up a card, so the card itself at a reader stops what
        /// the app started with it - and the handle then stops nothing.
        /// </summary>
        [Test]
        public async Task TheCardAtAReaderStopsWhatTheAppStartedWithIt()
        {

            using var app = AtTheLocalApp();

            var sessionId = await Started(app, 1, CardOfKnown);

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out var result, out var error), Is.True, error);

            Assert.That(result!.Value<Boolean>("stopped"), Is.True);

            var (status, _) = await Stop(app, sessionId);

            Assert.That(status, Is.EqualTo(HttpStatusCode.NotFound));

        }

        /// <summary>
        /// The handle is in no log line: not when the session starts, not when
        /// it ends, and not when the operator stops it - which writes the whole
        /// session into the log.
        /// </summary>
        /// <remarks>
        /// Whoever holds the handle can end the session, and the log is read
        /// on the Logs page and kept in files.
        /// </remarks>
        [Test]
        public async Task TheHandleIsInNoLogLine()
        {

            using var app = AtTheLocalApp();

            var first   = await Started(app, 1, CardOfKnown);
            var second  = await Started(app, 2, CardOfUnknown);

            await Stop(app, first);

            Assert.That(Station.TryStopSession(2, out _, out var error), Is.True, error);

            var said = Station.Log.Recent(500).Select(entry => entry.Message).ToArray();

            Assert.Multiple(() => {
                Assert.That(said, Has.Some.Contains("The local app started a session at EVSE 1"),
                            "The start was not logged at all, so this test would pass for the wrong reason.");
                Assert.That(said, Has.None.Contains(first));
                Assert.That(said, Has.None.Contains(second));
            });

        }

        #endregion

        #region Over the WebSocket

        /// <summary>
        /// The same start and the same stop, as messages, each answered with
        /// what the POST would have answered and the status it would have had.
        /// </summary>
        [Test]
        public async Task TheWebSocketStartsAndStopsTheSameWay()
        {

            using var webSocket = await OpenTheWebSocket();

            var started = await Ask(webSocket, new JObject(
                                                   new JProperty("id",      1),
                                                   new JProperty("action",  "start"),
                                                   new JProperty("evse",    1),
                                                   new JProperty("uid",     CardOfKnown)
                                               ));

            Assert.Multiple(() => {
                Assert.That(started.Value<Int32>  ("id"),       Is.EqualTo(1));
                Assert.That(started.Value<String> ("action"),   Is.EqualTo("start"));
                Assert.That(started.Value<Int32>  ("status"),   Is.EqualTo(200), started.ToString());
                Assert.That(started.Value<Boolean>("started"),  Is.True);
                Assert.That(started.Value<String> ("sessionId"), Does.Match("^[0-9a-f]{32}$"));
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("occupied"));
            });

            var stopped = await Ask(webSocket, new JObject(
                                                   new JProperty("id",         "two"),
                                                   new JProperty("action",     "stop"),
                                                   new JProperty("sessionId",  started.Value<String>("sessionId"))
                                               ));

            Assert.Multiple(() => {
                Assert.That(stopped.Value<String> ("id"),       Is.EqualTo("two"));
                Assert.That(stopped.Value<String> ("action"),   Is.EqualTo("stop"));
                Assert.That(stopped.Value<Int32>  ("status"),   Is.EqualTo(200), stopped.ToString());
                Assert.That(stopped.Value<Boolean>("stopped"),  Is.True);
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"));
            });

        }

        /// <summary>
        /// What one door started, the other stops: they are two ways into the
        /// same sessions, not two sets of them.
        /// </summary>
        [Test]
        public async Task WhatOneDoorStartedTheOtherStops()
        {

            using var app        = AtTheLocalApp();
            using var webSocket  = await OpenTheWebSocket();

            var overHTTP       = await Started(app, 1, CardOfKnown);

            var stoppedOverWS  = await Ask(webSocket, new JObject(
                                                          new JProperty("action",     "stop"),
                                                          new JProperty("sessionId",  overHTTP)
                                                      ));

            var startedOverWS  = await Ask(webSocket, new JObject(
                                                          new JProperty("action",  "start"),
                                                          new JProperty("evse",    2),
                                                          new JProperty("uid",     CardOfUnknown)
                                                      ));

            var (stoppedOverHTTP, _) = await Stop(app, startedOverWS.Value<String>("sessionId")!);

            Assert.Multiple(() => {
                Assert.That(stoppedOverWS.Value<Int32>("status"), Is.EqualTo(200), stoppedOverWS.ToString());
                Assert.That(startedOverWS.Value<Int32>("status"), Is.EqualTo(200), startedOverWS.ToString());
                Assert.That(stoppedOverHTTP,                      Is.EqualTo(HttpStatusCode.OK));
            });

        }

        /// <summary>
        /// A refusal on the WebSocket carries the status the POST would have
        /// had - and the connection stays open for the next message, even after
        /// one that was not JSON at all.
        /// </summary>
        [Test]
        public async Task TheWebSocketRefusesWithTheStatusOfThePOST()
        {

            using var webSocket = await OpenTheWebSocket();

            var notJSON     = await Ask(webSocket, "{ this is not json");
            var noAction    = await Ask(webSocket, new JObject(new JProperty("id", 7), new JProperty("evse", 1), new JProperty("uid", CardOfKnown)));
            var notYet      = await Ask(webSocket, new JObject(new JProperty("action", "start"), new JProperty("evse", 1), new JProperty("uid", CardOfKnown), new JProperty("totp", "123456")));
            var started     = await Ask(webSocket, new JObject(new JProperty("action", "start"), new JProperty("evse", 1), new JProperty("uid", CardOfKnown)));
            var busy        = await Ask(webSocket, new JObject(new JProperty("action", "start"), new JProperty("evse", 1), new JProperty("uid", CardOfUnknown)));
            var nobody      = await Ask(webSocket, new JObject(new JProperty("action", "stop"),  new JProperty("sessionId", "0123456789abcdef0123456789abcdef")));

            Assert.Multiple(() => {
                Assert.That(notJSON. Value<Int32>("status"), Is.EqualTo(400), notJSON. ToString());
                Assert.That(noAction.Value<Int32>("status"), Is.EqualTo(400), noAction.ToString());
                Assert.That(noAction.Value<Int32>("id"),     Is.EqualTo(7),   "The answer did not say which message it answers.");
                Assert.That(notYet.  Value<Int32>("status"), Is.EqualTo(501), notYet.  ToString());
                Assert.That(started. Value<Int32>("status"), Is.EqualTo(200), started. ToString());
                Assert.That(busy.    Value<Int32>("status"), Is.EqualTo(409), busy.    ToString());
                Assert.That(nobody.  Value<Int32>("status"), Is.EqualTo(404), nobody.  ToString());
            });

        }

        /// <summary>
        /// A plain GET of the WebSocket's path is told to ask for an upgrade.
        /// </summary>
        [Test]
        public async Task APlainRequestForTheWebSocketIsToldToUpgrade()
        {

            using var app = AtTheLocalApp();

            var response = await app.GetAsync("localApp");

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.UpgradeRequired));

        }

        #endregion

        #region Nothing else on it, and it on nothing else

        /// <summary>
        /// The local app server serves none of the administration and none of
        /// the display: an app on an open network reaches a start and a stop,
        /// and nothing that says what this station is.
        /// </summary>
        [Test]
        public async Task TheLocalAppServerServesNothingButTheApp()
        {

            using var app = AtTheLocalApp();

            foreach (var path in new[] { "", "api/v1/status", "api/v1/configuration", "api/v1/logs", "api/kiosk", "ext/login" })
            {

                var response = await app.GetAsync(path);

                Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                            $"GET /{path} answered {(Int32) response.StatusCode} on the local app server.");

            }

            var signIn = await app.PostAsync("ext/login", new FormUrlEncodedContent([
                                                              new KeyValuePair<String, String>("login",    ChargingStation.DefaultAdminUser),
                                                              new KeyValuePair<String, String>("password", Password)
                                                          ]));

            Assert.That(signIn.StatusCode, Is.EqualTo(HttpStatusCode.NotFound),
                        "Somebody on the app's network could try passwords here.");

        }

        /// <summary>
        /// And neither the web interface nor the display starts a charge for
        /// an app: the door for it is this one.
        /// </summary>
        [Test]
        public async Task NeitherTheWebInterfaceNorTheDisplayTakesAStart()
        {

            var start = new JObject(new JProperty("evse", 1), new JProperty("uid", CardOfKnown)).ToString(Formatting.None);

            using var web      = Anonymous();
            using var display  = AtTheDisplay();

            var atTheWebInterface  = await web.    PostAsync("localStart", new StringContent(start, Encoding.UTF8, "application/json"));
            var atTheDisplay       = await display.PostAsync("localStart", new StringContent(start, Encoding.UTF8, "application/json"));

            Assert.Multiple(() => {
                Assert.That(atTheWebInterface.IsSuccessStatusCode, Is.False, "The web interface took a start meant for the local app server.");
                Assert.That(atTheDisplay.     IsSuccessStatusCode, Is.False, "The display took a start meant for the local app server.");
                Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"));
            });

        }

        #endregion


        #region (private) Helpers

        /// <summary>
        /// POST /localStart with the given body, and what came back.
        /// </summary>
        private static async Task<(HttpStatusCode Status, JObject Answer)> Start(HttpClient  App,
                                                                                 JObject     Request)
        {

            var response = await App.PostAsync("localStart", new StringContent(Request.ToString(Formatting.None), Encoding.UTF8, "application/json"));

            return (response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));

        }

        /// <summary>
        /// A start that has to succeed, and the handle it answered with.
        /// </summary>
        private static async Task<String> Started(HttpClient  App,
                                                  Byte        EVSEId,
                                                  String      UID)
        {

            var (status, answer) = await Start(App, new JObject(new JProperty("evse", EVSEId), new JProperty("uid", UID)));

            Assert.That(status, Is.EqualTo(HttpStatusCode.OK), answer.ToString());

            return answer.Value<String>("sessionId")!;

        }

        /// <summary>
        /// POST /localStop/{SessionId}, and what came back.
        /// </summary>
        private static async Task<(HttpStatusCode Status, JObject Answer)> Stop(HttpClient  App,
                                                                                String      SessionId)
        {

            var response = await App.PostAsync($"localStop/{SessionId}", null);

            return (response.StatusCode, JObject.Parse(await response.Content.ReadAsStringAsync()));

        }

        /// <summary>
        /// The WebSocket beside the two POSTs, open.
        /// </summary>
        private async Task<ClientWebSocket> OpenTheWebSocket()
        {

            var webSocket = new ClientWebSocket();

            await webSocket.ConnectAsync(new Uri($"ws://{new Uri(LocalAppURL).Authority}/localApp"), CancellationToken.None);

            return webSocket;

        }

        private static Task<JObject> Ask(ClientWebSocket WebSocket, JObject Message)
            => Ask(WebSocket, Message.ToString(Formatting.None));

        /// <summary>
        /// One message sent, and the one message that answers it.
        /// </summary>
        private static async Task<JObject> Ask(ClientWebSocket WebSocket, String Message)
        {

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            await WebSocket.SendAsync(Encoding.UTF8.GetBytes(Message), WebSocketMessageType.Text, true, timeout.Token);

            var buffer   = new Byte[8192];
            var received = new MemoryStream();

            WebSocketReceiveResult result;

            do
            {
                result = await WebSocket.ReceiveAsync(buffer, timeout.Token);
                received.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            Assert.That(result.MessageType, Is.EqualTo(WebSocketMessageType.Text),
                        $"The station answered with a {result.MessageType} frame ({result.CloseStatus} {result.CloseStatusDescription}).");

            return JObject.Parse(Encoding.UTF8.GetString(received.ToArray()));

        }

        /// <summary>
        /// One outlet, as the display shows it.
        /// </summary>
        private JObject OutletOnTheDisplay(Byte EVSEId)
            => (Station.KioskJSON()["evses"] as JArray)!.
                   OfType<JObject>().
                   First(evse => evse.Value<Byte>("id") == EVSEId);

        #endregion

    }

}
