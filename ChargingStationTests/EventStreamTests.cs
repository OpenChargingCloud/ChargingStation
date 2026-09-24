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

    }

}
