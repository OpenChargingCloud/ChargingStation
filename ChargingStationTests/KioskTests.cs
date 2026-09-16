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

using cloud.charging.open.ChargingStation.Web;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The display on the front of the station.
    /// </summary>
    /// <remarks>
    /// What is pinned here is what has actually been wrong, each of which was
    /// found by driving a real display and none of which the station said a
    /// word about: an outlet whose session was cut because somebody ticked a
    /// box, a payment code that went away for four seconds out of every thirty,
    /// a card that could not stop its own charge, and a QR code that led to a
    /// payment nothing could act on.
    ///
    /// The display itself - how large the code is drawn, what fills a card, how
    /// many notices fit - is a page and is measured in a browser. What is here
    /// is everything underneath it: what the station says, and what it lets
    /// happen.
    /// </remarks>
    [TestFixture]
    public class KioskTests : AChargingStationTests
    {

        #region Data

        /// <summary>
        /// The secret this station's payment codes are made from.
        /// </summary>
        /// <remarks>
        /// Written down here so that a test can ask whether it ever leaves the
        /// station - see <see cref="TheDisplayNeverCarriesTheSharedSecret"/>.
        /// </remarks>
        public const String  Secret        = "a-shared-secret-nobody-else-has";

        public const String  CardOfKnown   = "04A2112233";
        public const String  CardOfOther   = "0B99887766";

        /// <summary>
        /// Long enough to step across on purpose, short enough that a mistake
        /// in the arithmetic shows up as a whole slot rather than a rounding.
        /// </summary>
        public static readonly TimeSpan  Validity = TimeSpan.FromSeconds(30);

        #endregion

        #region A station with a display worth looking at

        /// <summary>
        /// A clock a test can move.
        /// </summary>
        /// <remarks>
        /// A one-time password is a function of the time, so a test about what
        /// the display shows near the end of a password's life has to be able
        /// to stand near the end of a password's life. Waiting for a real
        /// half-minute would make this a test nobody runs twice.
        /// </remarks>
        public sealed class MovableClock(DateTimeOffset Start) : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = Start;
            public override DateTimeOffset GetUtcNow() => Now;
        }

        /// <remarks>
        /// It starts at the real now rather than at a date somebody picked,
        /// and that is not a detail: the OCPP node inside this station still
        /// reads the system clock of its own (Timestamp.Now), so a fixture that
        /// moved the whole station to last March would find its reservations
        /// already expired and its sign-in already over - and would be testing
        /// that disagreement rather than the display. Starting here and
        /// stepping forward on purpose keeps the two within seconds of each
        /// other, which is all these tests need.
        /// </remarks>
        private readonly MovableClock clock = new (DateTimeOffset.UtcNow);

        protected override TimeProvider? Clock
            => clock;

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
                   )),

                   new JProperty("webPayments", new JObject(
                       new JProperty("enabled",          true),
                       new JProperty("urlTemplate",      "https://pay.example.org/{evseId}/{TOTP}"),
                       new JProperty("validitySeconds",  Validity.TotalSeconds),
                       new JProperty("sharedSecret",     Secret)
                   ))

               );

        #endregion

        #region (private) Helpers

        /// <summary>
        /// Everything the display is told, as it is told it.
        /// </summary>
        private JObject WhatTheDisplayShows()
            => Station.KioskJSON();

        private JObject OutletOnTheDisplay(Byte EVSEId)
            => (WhatTheDisplayShows()["evses"] as JArray)!.
                   OfType<JObject>().
                   First(evse => evse.Value<Byte>("id") == EVSEId);

        /// <summary>
        /// The password out of the payment URL the display is showing.
        /// </summary>
        private String? PasswordOnTheDisplay(Byte EVSEId)
            => (OutletOnTheDisplay(EVSEId)["qrCode"] as JObject)?.
                   Value<String>("url")?.
                   Split('/').Last();

        /// <summary>
        /// Take an outlet out of service, the way the web interface does.
        /// </summary>
        private void TakeOutOfService(Byte EVSEId)
        {

            var evses = new JArray(
                            Station.EVSEs.Select(evse => {
                                var json = evse.ToJSON();
                                if (evse.Id == EVSEId)
                                    json["operative"] = false;
                                return json;
                            })
                        );

            var updated = Station.TryUpdateEVSEConfiguration(
                              new JObject(new JProperty("evses", evses)),
                              _ => true,
                              out _,
                              out var error,
                              out _
                          );

            Assert.That(updated, Is.True, $"Taking EVSE {EVSEId} out of service failed: {error}");

        }

        #endregion


        #region The display says nothing it should not

        /// <summary>
        /// The one thing on this station that must never reach the display.
        /// </summary>
        /// <remarks>
        /// The display has no sign-in on it and the machine it runs on stands
        /// in a car park. The secret the payment codes are made from is what
        /// lets somebody make their own, so the answer carries the codes and
        /// never the thing they are made of. Asked of the whole answer as text,
        /// because the way it would get out is a field nobody thought about.
        /// </remarks>
        [Test]
        public void TheDisplayNeverCarriesTheSharedSecret()
        {

            var answer = WhatTheDisplayShows().ToString();

            Assert.That(answer.Contains(Secret, StringComparison.Ordinal), Is.False,
                        "The shared secret is in the answer the display gets.");

            Assert.That(answer.Contains("sharedSecret", StringComparison.OrdinalIgnoreCase), Is.False,
                        "The answer the display gets has a field called 'sharedSecret'.");

        }

        /// <summary>
        /// The display's own server answers, and answers nothing else.
        /// </summary>
        [Test]
        public async Task TheDisplayNeedsNoSignInAndTheWebInterfaceIsNotOnItsPort()
        {

            using var atTheDisplay = AtTheDisplay();

            var display = await atTheDisplay.GetAsync("/api/kiosk");

            Assert.That(display.IsSuccessStatusCode, Is.True,
                        $"The display asked its own server and got {(Int32) display.StatusCode}.");

            var webInterface = await atTheDisplay.GetAsync("/api/v1/status");

            Assert.That(webInterface.IsSuccessStatusCode, Is.False,
                        "The administrative API answered on the display's port.");

        }

        #endregion

        #region A payment code is always on offer

        /// <summary>
        /// There is never a moment with no way to pay at a free outlet.
        /// </summary>
        /// <remarks>
        /// There used to be: the station would not hand out a password with
        /// less than five seconds left on it and had nothing to hand out
        /// instead, so a thirty-second validity left four seconds out of every
        /// thirty with no code at all - twice a minute, for as long as the
        /// station stood there, with the card jumping its whole layout as the
        /// code came and went.
        /// </remarks>
        [Test]
        public void APaymentCodeIsAlwaysOnOffer()
        {

            var start = clock.Now;

            for (var second = 0; second < 90; second++)
            {

                clock.Now = start.AddSeconds(second);

                Assert.That(PasswordOnTheDisplay(1), Is.Not.Null,
                            $"The display had no payment code {second} s in.");

            }

        }

        /// <summary>
        /// Near the end of a password's life, the next one is shown instead.
        /// </summary>
        /// <remarks>
        /// Which is what makes the rule above safe rather than merely quiet: a
        /// code somebody photographs and then cannot use is worse than no code.
        /// TOTP is verified against three - the one before, the one now and the
        /// one next - so handing out the next one a few seconds early is
        /// something the far end already accepts.
        /// </remarks>
        [Test]
        public void NearTheEndOfASlotTheNextPasswordIsShown()
        {

            // Somewhere in the middle of a slot, wherever the slots happen to
            // fall: asked from the station rather than worked out here.
            var middle    = clock.Now;
            var inTheMiddle = PasswordOnTheDisplay(1);

            var expires   = DateTimeOffset.Parse(
                                (OutletOnTheDisplay(1)["qrCode"] as JObject)!.Value<String>("expiresAt")!
                            );

            // Two seconds before it runs out - inside the five the station
            // refuses to hand out.
            clock.Now     = expires.AddSeconds(-2);
            var atTheEnd  = PasswordOnTheDisplay(1);

            // And a second after, which is the slot that was being handed out
            // early.
            clock.Now     = expires.AddSeconds(1);
            var afterwards = PasswordOnTheDisplay(1);

            Assert.Multiple(() => {

                Assert.That(atTheEnd,   Is.Not.Null);
                Assert.That(afterwards, Is.Not.Null);

                Assert.That(atTheEnd,   Is.Not.EqualTo(inTheMiddle),
                            "The code in the last seconds of a slot is the one that is about to run out.");

                Assert.That(atTheEnd,   Is.EqualTo(afterwards),
                            "The code shown early is not the one that follows.");

            });

            clock.Now = middle;

        }

        #endregion

        #region Out of service does not pull anybody's plug

        /// <summary>
        /// A car already charging keeps charging.
        /// </summary>
        /// <remarks>
        /// It did not: setting operative to false ended the session on the
        /// spot, and the display went from "charging" to "out of service" with
        /// the cable still in the car. OCPP says the same thing in its own
        /// words - a ChangeAvailability during a transaction is answered
        /// Scheduled and takes effect when the transaction ends.
        /// </remarks>
        [Test]
        public void TakingAnOutletOutOfServiceDoesNotStopACarThatIsCharging()
        {

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var error), Is.True, error);

            TakeOutOfService(1);

            var outlet = OutletOnTheDisplay(1);

            Assert.Multiple(() => {

                Assert.That(outlet.Value<String>("status"), Is.EqualTo("occupied"),
                            "The outlet stopped saying it was charging while a car was on it.");

                Assert.That(outlet.Value<Boolean>("closing"), Is.True,
                            "The display was not told the outlet goes out of service when this session ends.");

                Assert.That(outlet["session"]?.Type, Is.Not.EqualTo(JTokenType.Null),
                            "The session was ended.");

            });

        }

        /// <summary>
        /// And the card that started it can still end it.
        /// </summary>
        /// <remarks>
        /// The out-of-service check sat in front of the branch that stops a
        /// session, so the one person who could not end a charge was the person
        /// who had started it.
        /// </remarks>
        [Test]
        public void TheCardThatStartedASessionCanStillStopItOnAClosingOutlet()
        {

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var starting), Is.True, starting);

            TakeOutOfService(1);

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out var result, out var stopping), Is.True,
                        $"The card that started the session could not stop it: {stopping}");

            Assert.That(result!.Value<Boolean>("stopped"), Is.True);

            // And now that the car has gone, the outlet is out of service in
            // fact and not only on paper.
            var outlet = OutletOnTheDisplay(1);

            Assert.Multiple(() => {
                Assert.That(outlet.Value<String>("status"),   Is.EqualTo("inoperative"));
                Assert.That(outlet.Value<Boolean>("closing"), Is.False);
            });

        }

        /// <summary>
        /// Somebody else's card is turned away for the right reason.
        /// </summary>
        [Test]
        public void ADifferentCardIsTurnedAwayFromAnOutletSomebodyIsChargingAt()
        {

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var starting), Is.True, starting);

            TakeOutOfService(1);

            Assert.That(Station.TryPresentToken("reader", 1, CardOfOther, out _, out var refused), Is.False);

            Assert.That(refused, Does.Contain("Another card"),
                        "Somebody who walked up to a busy outlet was told it was out of service.");

        }

        /// <summary>
        /// An outlet on its way out of service takes nothing new.
        /// </summary>
        [Test]
        public void AnOutletOnItsWayOutOfServiceTakesNoNewSession()
        {

            TakeOutOfService(2);

            Assert.That(Station.TryPresentToken("reader", 2, CardOfKnown, out _, out var refused), Is.False);
            Assert.That(refused, Does.Contain("out of service"));

        }

        /// <summary>
        /// And OCPP's display messages still see it as charging.
        /// </summary>
        /// <remarks>
        /// The same mistake one layer down: an outlet closing under a running
        /// session reported Unavailable, so the line a back end had written for
        /// people who are charging was the one they could not see.
        /// </remarks>
        [Test]
        public void AnOutletChargingUnderAScheduledCloseIsStillCharging()
        {

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var error), Is.True, error);

            TakeOutOfService(1);

            var evse = Station.EVSEs.First(candidate => candidate.Id == 1);

            Assert.That(Station.MessageStateOf(evse),
                        Is.EqualTo(OCPPv2_1.MessageState.Charging),
                        "An outlet with a car on it called itself unavailable.");

        }

        #endregion

        #region The other end of the QR code

        /// <summary>
        /// A payment code this station did not issue starts nothing.
        /// </summary>
        [Test]
        public void APasswordThisStationDidNotIssueStartsNothing()
        {

            Assert.That(Station.TryStartWebPayment(1, "not-a-password", out _, out var error), Is.False);
            Assert.That(error, Is.Not.Null);

            Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"),
                        "Something started anyway.");

        }

        /// <summary>
        /// The one on the display does, and the name over it is the operator's.
        /// </summary>
        /// <remarks>
        /// Somebody paying at the screen is buying from whoever runs the
        /// station, which is the one case where a session carries the
        /// operator's name rather than a provider's. Until the route under this
        /// existed there was no way to reach it: every session this station
        /// could start was a card one.
        /// </remarks>
        [Test]
        public void ThePasswordOnTheDisplayStartsACharge()
        {

            var password = PasswordOnTheDisplay(1);

            Assert.That(password, Is.Not.Null, "The display was showing no payment code to pay with.");

            Assert.That(Station.TryStartWebPayment(1, password, out var result, out var error), Is.True, error);

            Assert.That(result!.Value<String>("method"), Is.EqualTo("AdHoc"));

            var session = OutletOnTheDisplay(1)["session"] as JObject;

            Assert.Multiple(() => {

                Assert.That(session,                                          Is.Not.Null);
                Assert.That(session!.Value<String>("method"),                 Is.EqualTo("AdHoc"));
                Assert.That((session["provider"] as JObject)?.Value<String>("name"),
                            Is.EqualTo("Stadtwerke Musterstadt"),
                            "A charge paid for at the screen should carry the name of whoever runs the station.");

            });

        }

        /// <summary>
        /// A payment cannot take an outlet somebody is holding.
        /// </summary>
        /// <remarks>
        /// A hold is against a card and a payment carries none, so letting one
        /// in would hand somebody else's outlet to whoever paid fastest.
        /// </remarks>
        [Test]
        public void APaymentIsTurnedAwayFromAHeldOutlet()
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

            var password = PasswordOnTheDisplay(1);

            Assert.That(Station.TryStartWebPayment(2, password, out _, out var refused), Is.False,
                        "A payment took an outlet that was being held for somebody.");

            Assert.That(refused, Does.Contain("reserved"));

        }

        /// <summary>
        /// Neither route is a door for whoever is walking past.
        /// </summary>
        /// <remarks>
        /// Starting and stopping charges is on the administrative API behind
        /// the sign-in, and is deliberately not on the display's own server.
        /// </remarks>
        [Test]
        public async Task StartingAndStoppingChargesNeedsASignIn()
        {

            using var anonymous = Anonymous();

            var starting = await anonymous.PostAsync(
                                     "/api/v1/sessions/webpayment",
                                     JSONBody(
                                         new JProperty("evse",  1),
                                         new JProperty("totp",  PasswordOnTheDisplay(1))
                                     )
                                 );

            var stopping = await anonymous.PostAsync(
                                     "/api/v1/sessions/stop",
                                     JSONBody(new JProperty("evse", 1))
                                 );

            Assert.Multiple(() => {
                Assert.That(starting.IsSuccessStatusCode, Is.False, "Anybody could start a charge.");
                Assert.That(stopping.IsSuccessStatusCode, Is.False, "Anybody could stop one.");
            });

            using var atTheDisplay = AtTheDisplay();

            var onTheDisplaysPort = await atTheDisplay.PostAsync(
                                              "/api/v1/sessions/webpayment",
                                              JSONBody(new JProperty("evse", 1))
                                          );

            Assert.That(onTheDisplaysPort.IsSuccessStatusCode, Is.False,
                        "The display's own server would start a charge.");

        }

        /// <summary>
        /// A charge paid for at the screen can be ended again.
        /// </summary>
        /// <remarks>
        /// It has to be ended from somewhere: a card session is stopped by the
        /// card that started it, and this one has no card to hold up again.
        /// </remarks>
        [Test]
        public async Task AChargePaidForAtTheScreenCanBeStopped()
        {

            Assert.That(Station.TryStartWebPayment(1, PasswordOnTheDisplay(1), out _, out var error), Is.True, error);

            using var signedIn = await SignedIn();

            var response = await signedIn.PostAsync(
                                     "/api/v1/sessions/stop",
                                     JSONBody(new JProperty("evse", 1))
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"Stopping the charge answered {(Int32) response.StatusCode}.");

            Assert.That(OutletOnTheDisplay(1).Value<String>("status"), Is.EqualTo("available"));

        }

        #endregion

    }

}
