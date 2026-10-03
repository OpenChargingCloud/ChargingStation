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

using System.Collections.Concurrent;
using System.Net.NetworkInformation;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.Ethernet;

using cloud.charging.open.protocols.ISO15118.SLAC;
using cloud.charging.open.protocols.ISO15118.SLAC.Messages;
using cloud.charging.open.protocols.ISO15118.SLAC.Transport;

using cloud.charging.open.protocols.WWCP.Node.Logging;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether a V2G link that is ended lets go of everything it holds.
    /// </summary>
    /// <remarks>
    /// The station lets go of a link that failed to end and does not ask
    /// again, so a part the link left open stays open for as long as the
    /// process runs. The parts of a real link need a charging station's
    /// network interfaces, except two: the V2G endpoint listens on every
    /// interface, and SLAC listens on whatever medium it is given. So the
    /// medium is the part that fails here, and the endpoint, which is ended
    /// after it, is what has to be closed all the same.
    /// </remarks>
    public class V2GLinkEndingTests
    {

        #region APartThatFailsToEndDoesNotKeepTheOthersOpen()

        [Test]
        public async Task APartThatFailsToEndDoesNotKeepTheOthersOpen()
        {

            var log       = new EventLog();
            var warnings  = new ConcurrentQueue<String>();

            log.OnLogged += entry => {
                if (entry.Level == LogLevel.Warning)
                    warnings.Enqueue(entry.Message);
            };

            var link      = await V2GLink.TryStart(new V2GOptions {
                                                       Enabled        = true,
                                                       V2GPort        = 0,
                                                       SDP            = false,
                                                       SlacTransport  = SlacTransportKind.None
                                                   },
                                                   log,
                                                   SlacTransport: new ASlacMediumThatWillNotClose());

            Assert.That(link?.V2GEndpoint, Is.Not.Null, "The V2G endpoint did not open, so there is nothing to see closed.");
            Assert.That(link!.SLACRunning, Is.True,     "SLAC did not start on the medium it was given.");

            var port      = (UInt16) link.V2GEndpoint!.Port;

            Assert.That(Listening(port),   Is.True,     $"The V2G endpoint is not listening on port {port}.");

            Assert.That(async () => await link.DisposeAsync(),
                        Throws.InstanceOf<IOException>().With.Message.EqualTo(ASlacMediumThatWillNotClose.Why),
                        "What failed did not reach whoever ended the link.");

            Assert.Multiple(() => {

                Assert.That(Listening(port),  Is.False,
                            $"The V2G endpoint is still listening on port {port} once the link was ended.");

                Assert.That(warnings,         Has.Some.Matches<String>(warning => warning is not null &&
                                                                                  warning.Contains("the SLAC medium") &&
                                                                                  warning.Contains(ASlacMediumThatWillNotClose.Why)),
                            "The log does not say which part failed. Its warnings: " + String.Join(" | ", warnings));

            });

        }

        #endregion


        #region (private static) Listening(Port)

        private static Boolean Listening(UInt16 Port)

            => IPGlobalProperties.GetIPGlobalProperties().
                   GetActiveTcpListeners().
                   Any(listener => listener.Port == Port);

        #endregion

        #region (private class) ASlacMediumThatWillNotClose

        /// <summary>
        /// A medium nothing arrives on, which fails when it is closed.
        /// </summary>
        private sealed class ASlacMediumThatWillNotClose : ISlacTransport
        {

            /// <summary>
            /// What closing it says.
            /// </summary>
            public const String Why = "The SLAC medium would not close.";

            public MACAddress LocalMac { get; } = MACAddress.Parse("02:00:00:00:00:01");

            public event EventHandler<DecodedFrame>? FrameReceived { add { } remove { } }

            public Task SendAsync(MACAddress Destination, ISlacMessage Message, CancellationToken CancellationToken = default)
                => Task.CompletedTask;

            public Task SendRawAsync(Byte[] Frame, CancellationToken CancellationToken = default)
                => Task.CompletedTask;

            public Task StartAsync(CancellationToken CancellationToken = default)
                => Task.CompletedTask;

            public ValueTask DisposeAsync()
                => ValueTask.FromException(new IOException(Why));

        }

        #endregion

    }

}
