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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The event stream every browser hangs on, as a proxy in front of the
    /// station sees it.
    /// </summary>
    /// <remarks>
    /// Against a station that is started, because what is tested is what goes
    /// over the wire: a header, and what the stream says while nothing happens.
    /// </remarks>
    [TestFixture]
    public class EventStreamTests : AChargingStationTests
    {

        #region TheStreamAsksAProxyNotToBufferIt()

        /// <summary>
        /// "X-Accel-Buffering: no" on the event stream.
        /// </summary>
        /// <remarks>
        /// nginx buffers what it passes on unless it is told otherwise, and a
        /// buffered event stream reaches the browser as nothing at all - not
        /// even its header - until a buffer is full or nginx gives up on it
        /// after 60 silent seconds. The Logs page says "reconnecting ..." all
        /// the while, and never asks for its snapshot, which it does when the
        /// stream opens.
        /// </remarks>
        [Test]
        public async Task TheStreamAsksAProxyNotToBufferIt()
        {

            using var http      = await SignedIn();
            using var response  = await http.GetAsync("/api/v1/events", HttpCompletionOption.ResponseHeadersRead);

            Assert.Multiple(() => {
                Assert.That(response.StatusCode,                                                 Is.EqualTo(HttpStatusCode.OK));
                Assert.That(response.Content.Headers.ContentType?.MediaType,                     Is.EqualTo("text/event-stream"));
                Assert.That(response.Headers.TryGetValues("X-Accel-Buffering", out var values),  Is.True, "the header is there");
                Assert.That(values,                                                              Is.EqualTo(new[] { "no" }));
            });

        }

        #endregion

        #region ASilentStreamSaysSoAndThenCarriesOn()

        /// <summary>
        /// A comment whenever the stream has been silent for the heartbeat, and
        /// the next entry after it as if nothing had happened.
        /// </summary>
        /// <remarks>
        /// nginx gives up on an upstream that has sent nothing for 60 seconds,
        /// and a station nobody is using says nothing for longer than that. The
        /// second half is the one that could go wrong: the stream waits for the
        /// next entry across the heartbeat instead of asking for it again, and
        /// an entry that arrived during one must neither be lost nor come twice.
        /// </remarks>
        [Test]
        public async Task ASilentStreamSaysSoAndThenCarriesOn()
        {

            Station.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            using var http    = await SignedIn();
            using var stream  = await EventStream.Open(http);

            var heartbeat     = await stream.ReadUntil(": keep-alive");

            // Each step is judged as soon as it is taken. A read that timed out
            // has closed the connection under the reader, and the next read
            // would fail with an ObjectDisposedException that says nothing about
            // why - which is how a stream without a heartbeat failed here.
            Assert.That(heartbeat,  Is.True,  "a comment came while nothing was logged");

            var marker        = "A line for the event stream " + Guid.NewGuid().ToString("N")[..8];
            Station.Log.Info(marker, "test");

            var entry         = await stream.ReadUntil(marker);

            Assert.That(entry,      Is.True,  "the entry logged after the heartbeat arrived");

            // And the one after it, to be sure the stream is still waiting for
            // entries and not only for the heartbeat.
            var second        = marker + " (second)";
            Station.Log.Info(second, "test");

            var secondEntry   = await stream.ReadUntil(second);

            // The first one in quotes, which is how its message is written - so
            // that the second one, which begins with it, is not counted too.
            var delivered     = stream.Received.Split($"\"{marker}\"").Length - 1;

            Assert.Multiple(() => {
                Assert.That(secondEntry,  Is.True,         "and so did the one after it");
                Assert.That(delivered,    Is.EqualTo(1),   "once");
            });

        }

        #endregion

        #region AStreamEndsWithTheSessionThatOpenedIt()

        /// <summary>
        /// Signed out, a stream opened with that session ends - and a line
        /// logged after the sign-out does not come down it first.
        /// </summary>
        /// <remarks>
        /// Measured on a local controller before this was so: signed out, the
        /// Logs page went on saying "live" and showing every line it wrote for
        /// as long as it was watched. A stream is a request that is answered
        /// for hours, and it was asked about its session once, when it opened.
        /// </remarks>
        [Test]
        public async Task AStreamEndsWithTheSessionThatOpenedIt()
        {

            Station.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            using var http    = await SignedIn();
            using var stream  = await EventStream.OpenAndSettle(Station, http);

            Assert.That((await http.PostAsync("/api/v1/auth/logout", null)).StatusCode,
                        Is.EqualTo(HttpStatusCode.NoContent));

            var afterwards    = "Logged after the sign-out " + Guid.NewGuid().ToString("N")[..8];
            Station.Log.Info(afterwards, "test");

            var ended         = await stream.EndsWithin(TimeSpan.FromSeconds(5));

            Assert.Multiple(() => {
                Assert.That(ended,                     Is.True,        "the stream went on after its session had ended");
                Assert.That(stream.Received.Contains(afterwards),  Is.False,  "a line logged after the sign-out was sent to the session that had signed out");
            });

        }

        #endregion

        #region AQuietStreamEndsWithItsSessionToo()

        /// <summary>
        /// And a stream nothing is logged into ends at its next heartbeat, not
        /// whenever the next line happens to be written - however the session
        /// ended. Here all of an account's sessions are taken back at once,
        /// the way a new password takes them, which logs nothing at all.
        /// </summary>
        [Test]
        public async Task AQuietStreamEndsWithItsSessionToo()
        {

            Station.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            using var http    = await SignedIn();
            using var stream  = await EventStream.OpenAndSettle(Station, http);

            var session       = Station.ExtAPI.Sessions.Single();

            Assert.That(Station.ExtAPI.Sessions.RemoveAllForUser(session.UserId), Is.EqualTo(1));

            Assert.That(await stream.EndsWithin(TimeSpan.FromSeconds(3)), Is.True,
                        "a stream nothing was logged into went on after its session had ended");

        }

        #endregion

        #region AStreamOfAnotherSessionGoesOn()

        /// <summary>
        /// Only the stream of the session that ended ends: a second browser,
        /// signed in on its own, goes on being sent the log.
        /// </summary>
        [Test]
        public async Task AStreamOfAnotherSessionGoesOn()
        {

            using var mine    = await SignedIn();
            using var theirs  = await SignedIn();
            using var ending  = await EventStream.OpenAndSettle(Station, mine);
            using var going   = await EventStream.OpenAndSettle(Station, theirs);

            Assert.That((await mine.PostAsync("/api/v1/auth/logout", null)).StatusCode,
                        Is.EqualTo(HttpStatusCode.NoContent));

            var afterwards    = "Logged after one of two signed out " + Guid.NewGuid().ToString("N")[..8];
            Station.Log.Info(afterwards, "test");

            var arrived       = await going. ReadUntil  (afterwards);
            var ended         = await ending.EndsWithin (TimeSpan.FromSeconds(5));

            Assert.Multiple(() => {
                Assert.That(arrived,                   Is.True,        "the stream of the session still signed in stopped too");
                Assert.That(ended,                     Is.True,        "the stream of the session that signed out went on");
                Assert.That(ending.Received.Contains(afterwards),  Is.False,  "a line logged after the sign-out was sent to the session that had signed out");
            });

        }

        #endregion

        #region (helper) WithAPIKey(NotAfter = null)

        /// <summary>
        /// A client that opens this station with an API key of the account
        /// its first start made up, and nothing else: no session, no password.
        /// </summary>
        private async Task<(HttpClient HTTP, APIKey Key)> WithAPIKey(DateTimeOffset? NotAfter = null)
        {

            var key   = new APIKey(APIKey_Id.Parse("event-stream-" + Guid.NewGuid().ToString("N")),
                                   User_Id.Parse("root"),
                                   NotAfter: NotAfter);

            await Station.ExtAPI.AddAPIKey(key);

            Assert.That(Station.ExtAPI.TryGetAPIKey(key.Id, out _), Is.True,
                        "The API key was not added, so a test of taking it back would pass for the wrong reason.");

            var http  = Anonymous();

            http.DefaultRequestHeaders.Add("API-Key", key.Id.ToString());

            return (http, key);

        }

        #endregion

        #region AStreamOpenedWithAnAPIKeyEndsWithTheKey()

        /// <summary>
        /// A stream opened with an API key ends when the key is taken back -
        /// and a line logged afterwards does not come down it first.
        /// </summary>
        /// <remarks>
        /// Such a stream has no session that could end, and was held to its
        /// account alone: a key that was revoked went on being sent the log for
        /// as long as the account it belonged to was there.
        /// </remarks>
        [Test]
        public async Task AStreamOpenedWithAnAPIKeyEndsWithTheKey()
        {

            Station.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            var (http, key)   = await WithAPIKey();

            using var client  = http;
            using var stream  = await EventStream.OpenAndSettle(Station, client);

            await Station.ExtAPI.RemoveAPIKey(key);

            Assert.That(Station.ExtAPI.TryGetAPIKey(key.Id, out _), Is.False, "the API key is gone");

            var afterwards    = "Logged after the key was taken back " + Guid.NewGuid().ToString("N")[..8];
            Station.Log.Info(afterwards, "test");

            var ended         = await stream.EndsWithin(TimeSpan.FromSeconds(5));

            Assert.Multiple(() => {
                Assert.That(ended,                     Is.True,        "the stream went on after its API key had been taken back");
                Assert.That(stream.Received.Contains(afterwards),  Is.False,  "a line logged after the key was taken back was sent over it");
            });

        }

        #endregion

        #region AStreamEndsWhenItsAPIKeyRunsOut()

        /// <summary>
        /// And one whose key runs out ends at the next heartbeat after, with
        /// nothing logged and nobody taking anything back.
        /// </summary>
        [Test]
        public async Task AStreamEndsWhenItsAPIKeyRunsOut()
        {

            Station.API.EventStreamHeartbeat = TimeSpan.FromMilliseconds(300);

            // Long enough for the stream to settle before the key runs out.
            var runsOut       = DateTimeOffset.UtcNow.AddSeconds(5);
            var (http, _)     = await WithAPIKey(runsOut);

            using var client  = http;
            using var stream  = await EventStream.OpenAndSettle(Station, client);

            var ended         = await stream.EndsWithin(runsOut - DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3));
            var endedAt       = DateTimeOffset.UtcNow;

            Assert.Multiple(() => {
                Assert.That(ended,    Is.True,                                         "the stream went on after its API key had run out");
                Assert.That(endedAt,  Is.GreaterThanOrEqualTo(runsOut.AddMilliseconds(-100)),  "the stream ended before its API key ran out, so something else ended it");
            });

        }

        #endregion

    }

}
