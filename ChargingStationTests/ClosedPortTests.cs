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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a test that uses a ClosedPort relies on: a connection to it is
    /// refused, nobody else can listen on it, and handed over it is a port like
    /// any other - Hermod's guards, asked of this suite's copy.
    /// </summary>
    /// <remarks>
    /// None of it is what the free port these tests used before gave: a port
    /// nobody listened on a moment ago was anybody's, and another test run on
    /// the same machine took one now and then and answered 401 on it, where a
    /// test counted on nothing answering at all.
    /// </remarks>
    [TestFixture]
    public class ClosedPortTests
    {

        #region AConnectionToItIsRefused()

        /// <summary>
        /// On 127.0.0.1, and on [::1] where there is one.
        /// </summary>
        [Test]
        public async Task AConnectionToItIsRefused()
        {

            using var port = new ClosedPort();

            Assert.That(await Connect(System.Net.IPAddress.Loopback, port), Is.EqualTo(SocketError.ConnectionRefused), "127.0.0.1");

            if (Socket.OSSupportsIPv6)
                Assert.That(await Connect(System.Net.IPAddress.IPv6Loopback, port), Is.EqualTo(SocketError.ConnectionRefused), "[::1]");

        }

        #endregion

        #region NobodyElseCanListenOnIt(Where, ReuseAddress)

        /// <summary>
        /// Another socket asking for the port is told no - wherever it binds, and
        /// whether it asks to share the address or not.
        /// </summary>
        [Test]
        public void NobodyElseCanListenOnIt([Values("127.0.0.1", "0.0.0.0", "[::] in dual mode", "[::]", "[::1]")]  String   Where,
                                            [Values]                                                                  Boolean  ReuseAddress)
        {

            var (family, address, dualMode) = Where switch {
                                                  "127.0.0.1"          => (AddressFamily.InterNetwork,   System.Net.IPAddress.Loopback,     false),
                                                  "0.0.0.0"            => (AddressFamily.InterNetwork,   System.Net.IPAddress.Any,          false),
                                                  "[::] in dual mode"  => (AddressFamily.InterNetworkV6, System.Net.IPAddress.IPv6Any,      true),
                                                  "[::]"               => (AddressFamily.InterNetworkV6, System.Net.IPAddress.IPv6Any,      false),
                                                  _                    => (AddressFamily.InterNetworkV6, System.Net.IPAddress.IPv6Loopback, false)
                                              };

            if (family == AddressFamily.InterNetworkV6 && !Socket.OSSupportsIPv6)
                Assert.Ignore("There is no IPv6 here to bind to.");

            using var port   = new ClosedPort();
            using var other  = new Socket(family, SocketType.Stream, ProtocolType.Tcp);

            if (family == AddressFamily.InterNetworkV6)
                other.DualMode = dualMode;

            if (ReuseAddress)
                other.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            Assert.That(() => {
                            other.Bind(new IPEndPoint(address, port.Number.ToInt32()));
                            other.Listen();
                        },
                        Throws.InstanceOf<SocketException>(),
                        $"Another socket was bound to {Where}:{port} and listened there.");

        }

        #endregion

        #region HandedOverItTakesABackEndAndTakenBackItIsClosedAgain()

        /// <summary>
        /// Handed over, the port takes a back end of the kind these tests start,
        /// and a client reaches it. Taken back once that back end has stopped, a
        /// connection to it is refused again.
        /// </summary>
        [Test]
        public async Task HandedOverItTakesABackEndAndTakenBackItIsClosedAgain()
        {

            using var port  = new ClosedPort();

            port.HandOver();

            var backEnd     = new WebSocketServer(HTTPPort: port.Number, RequireAuthentication: false, AutoStart: true);

            try
            {
                Assert.That(await Connect(System.Net.IPAddress.Loopback, port), Is.EqualTo(SocketError.Success),
                            "The back end did not take the port it was handed.");
            }
            finally
            {
                await backEnd.Shutdown();
            }

            port.TakeBack();

            Assert.That(await Connect(System.Net.IPAddress.Loopback, port), Is.EqualTo(SocketError.ConnectionRefused));

        }

        #endregion


        #region (private static) Connect(Address, Port)

        /// <summary>
        /// How a connection to the port ends: Success where it was accepted, the
        /// socket error where it was not.
        /// </summary>
        private static async Task<SocketError> Connect(System.Net.IPAddress  Address,
                                                       ClosedPort            Port)
        {

            using var client = new TcpClient(Address.AddressFamily);

            try
            {
                await client.ConnectAsync(Address, Port.Number.ToInt32()).WaitAsync(TimeSpan.FromSeconds(10));
                return SocketError.Success;
            }
            catch (SocketException e)
            {
                return e.SocketErrorCode;
            }

        }

        #endregion

    }

}
