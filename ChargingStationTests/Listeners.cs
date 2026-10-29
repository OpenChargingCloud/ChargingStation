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

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Whether anything on this machine listens on a TCP port.
    /// </summary>
    /// <remarks>
    /// Tried with a connection over the IPv4 and the IPv6 loopback rather than
    /// asked of the operating system's table of listeners: that table is not
    /// always whole. On Windows it has been seen to list eight listeners, of
    /// 127.0.0.1 only and none of IPv6, while every port it left out took
    /// connections - and a port nobody found was taken as one closed.
    /// On Windows, a closed port on the loopback is refused only after some
    /// two seconds of retries, so ask of several ports at once. Only a refusal
    /// says that nothing is there: what is neither taken nor refused within
    /// ten seconds counts as listening.
    /// </remarks>
    internal static class Listeners
    {

        #region Listening(Port)

        /// <summary>
        /// Whether anything takes a connection on the given port.
        /// </summary>
        /// <param name="Port">A TCP port.</param>
        public static async Task<Boolean> Listening(UInt16 Port)

            => (await Task.WhenAll(TakesAConnection(IPAddress.Loopback,     Port),
                                   TakesAConnection(IPAddress.IPv6Loopback, Port))).
                   Any(taken => taken);

        #endregion

        #region (private static) TakesAConnection(Address, Port)

        private static async Task<Boolean> TakesAConnection(IPAddress  Address,
                                                            UInt16     Port)
        {

            using var client   = new TcpClient(Address.AddressFamily);
            using var timeout  = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            try
            {
                await client.ConnectAsync(Address, Port, timeout.Token);
                return true;
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused
                                                              or SocketError.AddressNotAvailable
                                                              or SocketError.AddressFamilyNotSupported)
            {
                // Refused, or a loopback this machine does not have.
                return false;
            }
            catch (Exception)
            {
                return true;
            }

        }

        #endregion

    }

}
