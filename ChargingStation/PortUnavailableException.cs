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

using System.Net.Sockets;

using org.GraphDefined.Vanaheimr.Hermod;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Which of the two things this station listens for a port was to be.
    /// </summary>
    /// <remarks>
    /// Carried rather than written into the sentence, because what to do about
    /// a port that cannot be had depends on which one it was, and the switches
    /// that say so belong to whoever started the station rather than to the
    /// station.
    /// </remarks>
    public enum StationPort
    {

        /// <summary>The web interface, its JSON API and its event stream.</summary>
        WebInterface,

        /// <summary>The screen on the front of the station.</summary>
        Display

    }


    /// <summary>
    /// A port this station has to have, and cannot get.
    /// </summary>
    /// <remarks>
    /// Until this existed, the whole of what somebody starting a second copy
    /// of the station got was thirteen frames of stack trace under the
    /// operating system's own words for it - on a German Windows, "Normaler-
    /// weise darf jede Socketadresse (Protokoll, Netzwerkadresse oder
    /// Anschluss) nur jeweils einmal verwendet werden", under eleven lines of
    /// English log. The port was named nowhere in it, and neither was which of
    /// the two servers had wanted it.
    ///
    /// The reason is carried as the socket error's name rather than as the
    /// operating system's message: the name is the same everywhere, and a
    /// station that stands in Germany should not answer in German to somebody
    /// reading the rest of its output in English.
    /// </remarks>
    public sealed class PortUnavailableException : Exception
    {

        #region Properties

        /// <summary>The port that could not be had.</summary>
        public IPPort       Port     { get; }

        /// <summary>What it was to listen for.</summary>
        public StationPort  Whose    { get; }

        /// <summary>Why not, as the socket layer names it.</summary>
        public SocketError  Because  { get; }

        #endregion

        #region Constructor

        /// <summary>
        /// A port that could not be had.
        /// </summary>
        /// <param name="Port">The port.</param>
        /// <param name="Whose">What it was to listen for.</param>
        /// <param name="Problem">What the socket layer said.</param>
        public PortUnavailableException(IPPort           Port,
                                        StationPort      Whose,
                                        SocketException  Problem)

            : base($"{NameOf(Whose)} could not be given port {Port}: {Why(Problem.SocketErrorCode)}",
                   Problem)

        {

            this.Port     = Port;
            this.Whose    = Whose;
            this.Because  = Problem.SocketErrorCode;

        }

        #endregion


        #region (static) NameOf(Whose)

        /// <summary>
        /// What a station calls it, which is not what the code calls it.
        /// </summary>
        public static String NameOf(StationPort Whose)

            => Whose == StationPort.Display
                   ? "The display"
                   : "The web interface";

        #endregion

        #region (static) Why(Problem)

        /// <summary>
        /// What happened, in a clause rather than in a number.
        /// </summary>
        private static String Why(SocketError Problem)

            => Problem switch {

                   SocketError.AddressAlreadyInUse
                       => "something else is already listening on it",

                   // Windows keeps whole ranges to itself for Hyper-V and for
                   // WSL, and a port inside one of them is refused rather than
                   // taken - which looks nothing like a port in use and is the
                   // harder of the two to work out from a stack trace.
                   SocketError.AccessDenied
                       => "the operating system would not let this station have it",

                   SocketError.AddressNotAvailable
                       => "the address it was to listen on is not one this machine has",

                   _   => $"the socket layer answered {Problem}"

               };

        #endregion

    }

}
