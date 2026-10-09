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
using System.Net.Sockets;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

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
        /// It still starts at the real now rather than at a date somebody
        /// picked, but the reason has changed and the old one should not be
        /// left standing here. It used to be that the OCPP node inside this
        /// station kept a clock of its own - the global Timestamp.Now - so a
        /// fixture that moved the station to last March found its reservations
        /// already expired and measured that disagreement rather than the
        /// display. The node is on the station's clock now.
        ///
        /// What is left is not the node. Measured: with this fixture moved a
        /// year forward, 18 of these 19 pass. Moved a year back, two answer
        /// 401 - because the sign-in cookie is then stamped with an Expires in
        /// the past, and the HttpClient these tests sign in with measures that
        /// against the real system clock and drops the cookie before it is
        /// ever sent. A test's own HTTP client cannot be time-travelled from
        /// in here.
        ///
        /// So the real now stays, for a reason that now lives in the test
        /// rather than in the station.
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

        #region The time on the display

        /// <summary>
        /// The display is told the station's clock and what it is worth, as the
        /// web interface is.
        /// </summary>
        /// <remarks>
        /// A station that shows a time somebody may later be billed against
        /// says in the same breath whether that time has been checked, against
        /// whom, and how long ago - so the display is told the whole of what
        /// api/v1/clock says, not a time of its own. The clock of these tests
        /// stands still, so the two answers are of the same moment and have to
        /// be equal down to the last field.
        /// </remarks>
        [Test]
        public async Task TheDisplayIsToldTheClockTheWebInterfaceIsTold()
        {

            using var atTheDisplay  = AtTheDisplay();
            using var http          = await SignedIn();

            var shown  = JObject.Parse(await atTheDisplay.GetStringAsync("/api/kiosk"))["clock"];
            var clock  = JObject.Parse(await http.GetStringAsync("api/v1/clock"));

            Assert.That(shown, Is.Not.Null, "The display is not told the time at all.");

            Assert.That(JToken.DeepEquals(shown, clock), Is.True,
                        $"The display is told {shown}, where the web interface is told {clock}.");

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
        /// When the display's code runs out, as the station says it.
        /// </summary>
        private DateTimeOffset ExpiryOnTheDisplay(Byte EVSEId)
            => DateTimeOffset.Parse(
                   (OutletOnTheDisplay(EVSEId)["qrCode"] as JObject)!.Value<String>("expiresAt")!
               );

        /// <summary>
        /// Ten seconds into a slot, wherever the slots happen to fall: asked
        /// from the station rather than worked out here.
        /// </summary>
        private DateTimeOffset IntoASlot()
        {
            clock.Now = ExpiryOnTheDisplay(1).AddSeconds(10);
            return clock.Now;
        }

        /// <summary>
        /// The display shows the password of the slot it is in, to its last second.
        /// </summary>
        /// <remarks>
        /// It used to show the next one for the last five seconds of a slot, so
        /// that nobody would be handed a code with two seconds left. But a
        /// password is taken for a whole slot after its own as well - see below -
        /// so a code read in the last second still has that slot to be paid
        /// with, and the five seconds bought nothing but a second rule.
        /// </remarks>
        [Test]
        public void TheDisplayShowsThePasswordOfTheSlotItIsIn()
        {

            var start        = clock.Now;
            IntoASlot();

            var inTheMiddle  = PasswordOnTheDisplay(1);
            var expires      = ExpiryOnTheDisplay(1);

            clock.Now        = expires.AddSeconds(-2);
            var atTheEnd     = PasswordOnTheDisplay(1);
            var expiresThen  = ExpiryOnTheDisplay(1);

            clock.Now        = expires.AddSeconds(1);
            var afterwards   = PasswordOnTheDisplay(1);

            Assert.Multiple(() => {

                Assert.That(inTheMiddle, Is.Not.Null);
                Assert.That(afterwards,  Is.Not.Null);

                Assert.That(atTheEnd,    Is.EqualTo(inTheMiddle),
                            "Two seconds before the end of a slot the display showed another slot's password.");

                Assert.That(expiresThen, Is.EqualTo(expires),
                            "Two seconds before the end of a slot the display said its code runs out at another time.");

                Assert.That(afterwards,  Is.Not.EqualTo(inTheMiddle),
                            "The next slot still showed the password before it.");

            });

            clock.Now = start;

        }

        /// <summary>
        /// The password before the one on the display and the one after it are
        /// taken, and none further away.
        /// </summary>
        /// <remarks>
        /// What the display can rely on: a code read in the last second of its
        /// slot is still paid with for the whole slot after it, and a phone or
        /// a payment service whose clock runs a little ahead is not turned away.
        /// Two slots off is a photograph of an earlier screen.
        /// </remarks>
        [Test]
        public void ThePasswordBeforeAndTheOneAfterAreTakenAndNoneFurther()
        {

            var start     = IntoASlot();
            var shown     = PasswordOnTheDisplay(1)!;

            clock.Now     = start.AddSeconds(2 * Validity.TotalSeconds);
            var twoAhead  = Station.TryStartWebPayment(1, shown, out _, out _);

            clock.Now     = start.AddSeconds(Validity.TotalSeconds + 15);
            var oneAhead  = Station.TryStartWebPayment(1, shown, out _, out var errorOneAhead);

            clock.Now     = start.AddSeconds(Validity.TotalSeconds);
            var later     = PasswordOnTheDisplay(2)!;

            clock.Now     = start.AddSeconds(-Validity.TotalSeconds);
            var twoBack   = Station.TryStartWebPayment(2, later, out _, out _);

            clock.Now     = start;
            var oneBack   = Station.TryStartWebPayment(2, later, out _, out var errorOneBack);

            Assert.Multiple(() => {

                Assert.That(oneAhead,  Is.True,  $"The password of the slot before was turned away: {errorOneAhead}");
                Assert.That(oneBack,   Is.True,  $"The password of the slot after was turned away: {errorOneBack}");
                Assert.That(twoAhead,  Is.False, "A password two slots old was taken.");
                Assert.That(twoBack,   Is.False, "A password two slots ahead was taken.");

            });

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

        #region The quiet hours

        /// <summary>
        /// One local moment, whatever zone this test is being run in.
        /// </summary>
        private static DateTimeOffset LocallyAt(Int32 Hour, Int32 Minute)
        {

            var when = new DateTime(2026, 3, 1, Hour, Minute, 0, DateTimeKind.Unspecified);

            return new DateTimeOffset(when, TimeZoneInfo.Local.GetUtcOffset(when));

        }

        /// <summary>
        /// Ten at night until six is one window, not two.
        /// </summary>
        /// <remarks>
        /// The ordinary case and the one an interval written the obvious way
        /// gets wrong: "after ten and before six" is true of no moment at all.
        /// </remarks>
        [Test]
        public void QuietHoursThatCrossMidnightAreOneWindow()
        {

            var night = new DisplayConfiguration(new TimeOnly(22, 0), new TimeOnly(6, 0), 0.3);

            Assert.Multiple(() => {

                Assert.That(night.IsAQuietHour(LocallyAt(23, 30)), Is.True,  "half past eleven at night is not a quiet hour");
                Assert.That(night.IsAQuietHour(LocallyAt( 3,  0)), Is.True,  "three in the morning is not a quiet hour");
                Assert.That(night.IsAQuietHour(LocallyAt(22,  0)), Is.True,  "the hours do not start when they say they do");

                Assert.That(night.IsAQuietHour(LocallyAt( 6,  0)), Is.False, "the hours do not end when they say they do");
                Assert.That(night.IsAQuietHour(LocallyAt(12,  0)), Is.False, "midday is a quiet hour");
                Assert.That(night.IsAQuietHour(LocallyAt(21, 59)), Is.False, "the hours start a minute early");

            });

        }

        /// <summary>
        /// And a window inside one day is still a window.
        /// </summary>
        [Test]
        public void QuietHoursInsideOneDayAreTheSameWindow()
        {

            var siesta = new DisplayConfiguration(new TimeOnly(13, 0), new TimeOnly(15, 0), 0.3);

            Assert.Multiple(() => {
                Assert.That(siesta.IsAQuietHour(LocallyAt(14, 0)), Is.True);
                Assert.That(siesta.IsAQuietHour(LocallyAt( 3, 0)), Is.False);
                Assert.That(siesta.IsAQuietHour(LocallyAt(23, 0)), Is.False);
            });

        }

        /// <summary>
        /// A station nobody has told keeps its screen up.
        /// </summary>
        /// <remarks>
        /// Which hours are quiet is a fact about the site. A screen that went
        /// dark on its own would be read as a fault, so not being told is not
        /// an invitation to guess - and neither is being told only half of it.
        /// </remarks>
        [Test]
        public void AStationNobodyHasToldDoesNotDim()
        {

            Assert.Multiple(() => {

                Assert.That(new DisplayConfiguration().IsAQuietHour(LocallyAt(3, 0)), Is.False);

                Assert.That(new DisplayConfiguration(new TimeOnly(22, 0), null, 0.3).IsAQuietHour(LocallyAt(23, 0)),
                            Is.False,
                            "half a window dimmed the screen");

                // "From ten until ten" is far more likely to be a mistake than
                // a request for a screen that is dim for ever.
                Assert.That(new DisplayConfiguration(new TimeOnly(22, 0), new TimeOnly(22, 0), 0.3).IsAQuietHour(LocallyAt(3, 0)),
                            Is.False,
                            "a window with no width dimmed the screen for ever");

            });

            // And the display is told so.
            Assert.That(WhatTheDisplayShows()["dim"]?.Type, Is.EqualTo(JTokenType.Null),
                        "a station with no quiet hours asked its display to dim.");

        }

        /// <summary>
        /// The hours can be changed while the station stands there.
        /// </summary>
        /// <remarks>
        /// It takes effect at the display's next poll, two seconds away: the
        /// answer is worked out from the configuration whenever it is asked
        /// for, so there is nothing to restart and nothing to tell.
        /// </remarks>
        [Test]
        public async Task TheHoursCanBeChangedWithoutRestartingAnything()
        {

            using var signedIn = await SignedIn();

            // A window around whatever moment this test is running at.
            var now   = DateTime.Now;
            var from  = now.AddMinutes(-30).ToString("HH\\:mm");
            var until = now.AddMinutes( 30).ToString("HH\\:mm");

            var response = await signedIn.PutAsync(
                                     "/api/v1/configuration/display",
                                     JSONBody(
                                         new JProperty("dimFrom",   from),
                                         new JProperty("dimUntil",  until),
                                         new JProperty("dimTo",     0.2)
                                     )
                                 );

            Assert.That(response.IsSuccessStatusCode, Is.True,
                        $"Setting the display's hours answered {(Int32) response.StatusCode}.");

            Assert.That(WhatTheDisplayShows().Value<Double?>("dim"), Is.EqualTo(0.2),
                        "The display was not told about hours that had just been set.");

            // And taking them away again is an empty section.
            var off = await signedIn.PutAsync("/api/v1/configuration/display", JSONBody());

            Assert.That(off.IsSuccessStatusCode, Is.True);

            Assert.That(WhatTheDisplayShows()["dim"]?.Type, Is.EqualTo(JTokenType.Null),
                        "The display was still being asked to dim after the hours were taken away.");

        }

        /// <summary>
        /// Nobody may darken the screen from outside, and nobody may do it
        /// without signing in.
        /// </summary>
        [Test]
        public async Task ChangingTheHoursNeedsASignIn()
        {

            using var anonymous = Anonymous();

            var refused = await anonymous.PutAsync(
                                    "/api/v1/configuration/display",
                                    JSONBody(new JProperty("dimFrom",  "22:00"),
                                             new JProperty("dimUntil", "06:00"))
                                );

            Assert.That(refused.IsSuccessStatusCode, Is.False, "Anybody could dim the display.");

            using var atTheDisplay = AtTheDisplay();

            var onItsOwnPort = await atTheDisplay.PutAsync(
                                         "/api/v1/configuration/display",
                                         JSONBody(new JProperty("dimFrom",  "22:00"),
                                                  new JProperty("dimUntil", "06:00"))
                                     );

            Assert.That(onItsOwnPort.IsSuccessStatusCode, Is.False,
                        "The display's own server would change the station's configuration.");

        }

        #endregion

        #region Keeping the picture moving

        /// <summary>
        /// The display's section of the configuration file, as it is on disk.
        /// </summary>
        private JObject? DisplaySectionInTheFile()
            => JObject.Parse(File.ReadAllText(Path.Combine(Directory, "configuration.json")))[DisplayConfiguration.SectionName] as JObject;

        /// <summary>
        /// The picture stands still unless somebody asks it to walk.
        /// </summary>
        /// <remarks>
        /// It used to walk on every station, a step every three quarters of a
        /// minute, and a step is a jump of the whole screen that somebody in
        /// front of it sees. Against burn-in on a panel that shows the same
        /// thing for months it is worth it; on a panel that does not, it is a
        /// screen that twitches. So it is a setting, and off unless set.
        /// </remarks>
        [Test]
        public async Task ThePictureStandsStillUnlessToldToWalk()
        {

            var before = WhatTheDisplayShows()["keepMoving"];

            using var signedIn = await SignedIn();

            var response = await signedIn.PutAsync(
                                     "/api/v1/configuration/display",
                                     JSONBody(new JProperty("keepMoving", true))
                                 );

            var answer = JObject.Parse(await response.Content.ReadAsStringAsync());

            Assert.Multiple(() => {

                Assert.That(before?.Type,                                     Is.EqualTo(JTokenType.Boolean), "The display is not told whether to walk.");
                Assert.That(before?.Value<Boolean>(),                         Is.False, "A station nobody told keeps its picture walking.");

                Assert.That(response.IsSuccessStatusCode,                     Is.True, answer.ToString());
                Assert.That(answer.Value<Boolean?>("keepMoving"),             Is.True);
                Assert.That(WhatTheDisplayShows().Value<Boolean?>("keepMoving"), Is.True, "The display was not told at its next poll.");
                Assert.That(DisplaySectionInTheFile()?.Value<Boolean?>("keepMoving"), Is.True, "It was not written down.");

            });

        }

        /// <summary>
        /// A word where a yes or no belongs is refused, and nothing is changed.
        /// </summary>
        [Test]
        public void KeepMovingIsAYesOrANo()
        {

            Assert.Multiple(() => {

                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("keepMoving", "yes")), out _, out var error), Is.False);
                Assert.That(error, Does.Contain("keepMoving"));

                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("keepMoving", false)), out var off, out _), Is.True);
                Assert.That(off!.KeepsMoving, Is.False);

                Assert.That(new DisplayConfiguration().KeepsMoving, Is.False, "the default walks");

            });

        }

        #endregion

        #region The display's port

        /// <summary>
        /// A port nobody has, on the loopback address the tests' displays use.
        /// </summary>
        private static UInt16 APortNobodyHas()
            => TestPorts.Free();

        /// <summary>
        /// What a display at the given port answers, or null where nothing does.
        /// </summary>
        private static async Task<JObject?> TheDisplayAt(UInt16 Port)
        {
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Port}/"), Timeout = TimeSpan.FromSeconds(5) };
                var response   = await http.GetAsync("api/kiosk");
                return response.IsSuccessStatusCode ? JObject.Parse(await response.Content.ReadAsStringAsync()) : null;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private async Task<HttpResponseMessage> PutDisplay(HttpClient SignedIn, params JProperty[] Properties)
            => await SignedIn.PutAsync("/api/v1/configuration/display", JSONBody(Properties));

        /// <summary>
        /// A port set on the page moves the display at once, and the screen
        /// still on the old one is told where it went.
        /// </summary>
        /// <remarks>
        /// The old port is held a little longer rather than dropped: a screen
        /// in the car park is pointed at it, and dropped, it would show a dead
        /// page until somebody walked out to it. Held, it answers what it always
        /// answered and where the display is now, and the page goes there.
        /// </remarks>
        [Test]
        public async Task APortSetOnThePageMovesTheDisplayAtOnce()
        {

            var oldPort   = Station.KioskPort!.Value.ToUInt16();
            var newPort   = APortNobodyHas();

            using var signedIn = await SignedIn();

            var response  = await PutDisplay(signedIn, new JProperty("port", newPort));
            var answer    = JObject.Parse(await response.Content.ReadAsStringAsync());

            var atTheNew  = await TheDisplayAt(newPort);
            var atTheOld  = await TheDisplayAt(oldPort);

            Assert.Multiple(() => {

                Assert.That(response.IsSuccessStatusCode,             Is.True, answer.ToString());
                Assert.That(answer.Value<UInt16?>("port"),            Is.EqualTo(newPort), "what is in the file");
                Assert.That(answer.Value<UInt16?>("portInUse"),       Is.EqualTo(newPort), "where the display is");
                Assert.That(Station.KioskPort?.ToUInt16(),            Is.EqualTo(newPort));
                Assert.That(Station.KioskURL?.ToString(),             Does.Contain($":{newPort}/"));

                Assert.That(atTheNew,                                 Is.Not.Null, "Nothing answered at the new port.");
                Assert.That(atTheNew?["movedTo"]?.Type ?? JTokenType.Null, Is.EqualTo(JTokenType.Null), "The new port says the display moved on from it.");

                Assert.That(atTheOld,                                 Is.Not.Null, "The old port was dropped with a screen still pointed at it.");
                Assert.That(atTheOld?.Value<UInt16?>("movedTo"),      Is.EqualTo(newPort), "The old port does not say where the display went.");

                Assert.That(DisplaySectionInTheFile()?.Value<UInt16?>("port"), Is.EqualTo(newPort), "It was not written down.");

            });

        }

        /// <summary>
        /// After the handover the old port is let go.
        /// </summary>
        [Test]
        public async Task AfterTheHandoverTheOldPortIsLetGo()
        {

            Station.DisplayHandover = TimeSpan.FromMilliseconds(200);

            var oldPort = Station.KioskPort!.Value.ToUInt16();

            using var signedIn = await SignedIn();

            var response = await PutDisplay(signedIn, new JProperty("port", APortNobodyHas()));

            Assert.That(response.IsSuccessStatusCode, Is.True, await response.Content.ReadAsStringAsync());

            JObject? atTheOld = new ();

            for (var i = 0; i < 50 && atTheOld is not null; i++)
            {
                await Task.Delay(100);
                atTheOld = await TheDisplayAt(oldPort);
            }

            Assert.That(atTheOld, Is.Null, "The old port was still answering five seconds after a handover of 200 ms.");

        }

        /// <summary>
        /// A port something else has is refused, and the display stays where it is.
        /// </summary>
        /// <remarks>
        /// Bound first and only then written down, so that a refusal leaves
        /// nothing behind: not a display gone from where it was, and not a file
        /// that would fail the next start on a port that cannot be had.
        /// </remarks>
        [Test]
        public async Task APortSomethingElseHasIsRefusedAndTheDisplayStays()
        {

            var oldPort   = Station.KioskPort!.Value.ToUInt16();

            using var squatter = new TcpListener(System.Net.IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
            squatter.Start();
            var taken     = (UInt16) ((IPEndPoint) squatter.LocalEndpoint).Port;

            using var signedIn = await SignedIn();

            var response  = await PutDisplay(signedIn, new JProperty("port", taken));
            var said      = await response.Content.ReadAsStringAsync();
            var atTheOld  = await TheDisplayAt(oldPort);

            Assert.Multiple(() => {

                Assert.That(response.StatusCode,              Is.EqualTo(HttpStatusCode.BadRequest), said);
                Assert.That(said,                             Does.Contain(taken.ToString()));
                Assert.That(Station.KioskPort?.ToUInt16(),    Is.EqualTo(oldPort), "The display moved anyway.");
                Assert.That(atTheOld,                         Is.Not.Null, "The display is gone from where it was.");
                Assert.That(DisplaySectionInTheFile()?["port"], Is.Null, "A port that cannot be had was written down.");

            });

        }

        /// <summary>
        /// The web interface's port is not the display's to take.
        /// </summary>
        [Test]
        public async Task TheWebInterfacesPortIsRefused()
        {

            using var signedIn = await SignedIn();

            var response = await PutDisplay(signedIn, new JProperty("port", Station.HTTPPort.ToUInt16()));

            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest), await response.Content.ReadAsStringAsync());

        }

        /// <summary>
        /// At a start, the port in the file is the display's - unless the
        /// command line gives one, which wins for that start.
        /// </summary>
        /// <remarks>
        /// A switch given at a start is the more deliberate statement, and it is
        /// the way back up when the port in the file is one something else took.
        /// </remarks>
        [Test]
        public async Task AtAStartTheCommandLineWinsOverTheFile()
        {

            var fromTheFile  = APortNobodyHas();
            var fromTheLine  = APortNobodyHas();

            var directory    = TestStations.TemporaryDirectory("display-port");

            try
            {

                System.IO.Directory.CreateDirectory(directory);

                var configuration = new JObject(
                                        new JProperty("nts",     new JObject(new JProperty("enabled", false))),
                                        new JProperty("display", new JObject(new JProperty("port", fromTheFile)))
                                    );

                File.WriteAllText(Path.Combine(directory, "configuration.json"), configuration.ToString());

                await using var byTheFile = new ChargingStation(
                                                DNSClient:       TestStations.Resolver(),
                                                HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                                AccountsPath:    Path.Combine(directory, ChargingStation.DefaultAccountsPath),
                                                ConfigFile:      new WWCPConfigFile(Path.Combine(directory, "configuration.json")),
                                                LogToConsole:    false,
                                                BridgeDebugLog:  false
                                            );

                await using var byTheLine = new ChargingStation(
                                                DNSClient:       TestStations.Resolver(),
                                                HTTPPort:        IPPort.Parse(TestPorts.Free()),
                                                KioskPort:       IPPort.Parse(fromTheLine),
                                                AccountsPath:    Path.Combine(directory, ChargingStation.DefaultAccountsPath),
                                                ConfigFile:      new WWCPConfigFile(Path.Combine(directory, "configuration.json")),
                                                LogToConsole:    false,
                                                BridgeDebugLog:  false
                                            );

                var said = byTheLine.DisplayConfigurationJSON();

                Assert.Multiple(() => {

                    Assert.That(byTheFile.KioskPort?.ToUInt16(),          Is.EqualTo(fromTheFile), "The port in the file was passed over.");
                    Assert.That(byTheLine.KioskPort?.ToUInt16(),          Is.EqualTo(fromTheLine), "The command line lost to the file.");
                    Assert.That(said.Value<UInt16?>("portGivenAtStart"),  Is.EqualTo(fromTheLine), "The page is not told what the command line said.");
                    Assert.That(said.Value<UInt16?>("port"),              Is.EqualTo(fromTheFile));

                });

            }
            finally
            {
                TestStations.Remove(directory);
            }

        }

        /// <summary>
        /// A port that is no port is refused.
        /// </summary>
        [Test]
        public void APortIsANumberFromOneUp()
        {

            Assert.Multiple(() => {
                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("port", 0)),       out _, out _), Is.False);
                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("port", 70000)),   out _, out _), Is.False);
                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("port", "2349")),  out _, out _), Is.False);
                Assert.That(DisplayConfiguration.TryParse(new JObject(new JProperty("port", 2350)),    out var c, out _), Is.True);
                Assert.That(c!.Port?.ToUInt16(), Is.EqualTo(2350));
            });

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
