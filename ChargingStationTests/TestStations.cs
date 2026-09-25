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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.DNS;

using cloud.charging.open.ChargingStation.Configuration;

using cloud.charging.open.protocols.WWCP.Node.Configuration;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Building charging stations to test against.
    /// </summary>
    internal static class TestStations
    {

        #region New(Directory, Configuration = null, WithDisplay = true)

        /// <summary>
        /// A charging station, built and not started.
        /// </summary>
        /// <remarks>
        /// Both ports are asked of the operating system rather than left at
        /// 2348 and 2349, so that these tests neither fight with each other nor
        /// with a station somebody has running while they work.
        ///
        /// Not started, and that is often the point: it is <c>Start()</c> that
        /// puts a timer on the network to check the clock, so a station that
        /// was only built is also one that will not quietly go and ask a time
        /// server in the middle of a test run.
        /// </remarks>
        /// <param name="Directory">Where its accounts and its configuration go; created when it does not exist.</param>
        /// <param name="Configuration">What its configuration file says, or null for a station nobody has configured.</param>
        /// <param name="WithDisplay">Whether it also listens for the display, as a station does unless told otherwise.</param>
        /// <param name="Clock">Where it reads the time, for a test that needs to decide what time it is.</param>
        /// <param name="HTTPPort">A port of its own rather than a free one, for a test about two stations wanting the same.</param>
        /// <param name="KioskPort">The same for the display.</param>
        /// <param name="LocalAppPort">A port for the local app server, which a station has none of unless it is given one - as here, only for a test about it.</param>
        /// <param name="LogToConsole">Whether its log reaches the console, for a test about who gets to write there. Off otherwise, because a test run's console is for the test run.</param>
        /// <param name="LogPath">A directory for its log files, for a test about those. None otherwise.</param>
        public static ChargingStation New(String         Directory,
                                          JObject?       Configuration   = null,
                                          Boolean        WithDisplay     = true,
                                          TimeProvider?  Clock           = null,
                                          IPPort?        HTTPPort        = null,
                                          IPPort?        KioskPort       = null,
                                          IPPort?        LocalAppPort    = null,
                                          Boolean        LogToConsole    = false,
                                          String?        LogPath         = null)
        {

            System.IO.Directory.CreateDirectory(Directory);

            var configFile = Path.Combine(Directory, "configuration.json");

            if (Configuration is not null)
                File.WriteAllText(configFile, Configuration.ToString());

            return new ChargingStation(
                       DNSClient:        Resolver(),
                       HTTPPort:         HTTPPort  ?? IPPort.Parse(FreePort()),
                       KioskPort:        WithDisplay ? (KioskPort ?? IPPort.Parse(FreePort())) : null,
                       NoKiosk:          !WithDisplay,
                       LocalAppPort:     LocalAppPort,
                       AccountsPath:     Path.Combine(Directory, ChargingStation.DefaultAccountsPath),
                       ConfigFile:       new WWCPConfigFile(configFile),
                       LogToConsole:     LogToConsole,
                       LogPath:          LogPath,
                       BridgeDebugLog:   false,
                       TimeProvider:     Clock
                   );

        }

        #endregion

        #region (private) Resolver()

        /// <summary>
        /// A name resolver with its servers written down, rather than one that
        /// goes looking for them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The parameterless <c>DNSClient</c> enumerates every network
        /// interface on the machine - twice, once for the IPv4 servers and once
        /// for the IPv6 ones - and a station builds one in its constructor. A
        /// station per test therefore asks the operating system for its adapter
        /// table some four hundred times in a full run.
        /// </para>
        /// <para>
        /// That call is not reliably quick. It was caught hanging inside
        /// <c>SetUp</c> on a machine carrying three virtual switches, and the
        /// consequences go well beyond a slow test: NUnit waits for a test that
        /// will not finish, so the test host outlives the run still holding the
        /// assemblies it loaded, the next build cannot copy over them and fails
        /// with MSB3027, and a run started with <c>--no-build</c> after that
        /// quietly tests the previous build. Test counts of 174, 194 and 109
        /// were all the same suite, truncated in different places.
        /// </para>
        /// <para>
        /// Two fixed servers instead, which is what these tests wanted anyway:
        /// they read what this client is configured as and never ask it to
        /// resolve anything, and "whatever this machine happened to have" is
        /// not something to assert against. The addresses are documentation
        /// ones from RFC 5737 and RFC 3849, so a query that escapes by mistake
        /// has nowhere to arrive.
        /// </para>
        /// </remarks>
        private static DNSClient Resolver()

            => new ([
                   new DNSServerConfig(IPv4Address.Parse("192.0.2.53"),  IPPort.DNS),
                   new DNSServerConfig(IPv6Address.Parse("2001:db8::53"), IPPort.DNS)
               ]);

        #endregion

        #region Offline

        /// <summary>
        /// A configuration with the time client switched off.
        /// </summary>
        /// <remarks>
        /// Written before a station is built, because that is when it is read,
        /// and switched off there rather than afterwards because it is
        /// <c>StartCheckingTheClock</c> inside <c>Start()</c> that would
        /// otherwise schedule the first check. A test run has no business
        /// asking a public time server anything.
        ///
        /// The wire below the charging cable needs no switching off: a station
        /// is built with <c>V2GOptions.Off</c> unless it is handed something
        /// else, and <c>V2GLink.TryStart</c> then returns before it touches an
        /// interface.
        /// </remarks>
        public static JObject Offline

            => new (
                   new JProperty("nts", new JObject(
                       new JProperty("enabled", false)
                   ))
               );

        #endregion

        #region FreePort()

        /// <summary>
        /// A TCP port nobody was listening on a moment ago.
        /// </summary>
        /// <remarks>
        /// There is a gap between letting the port go and binding it again, and
        /// nothing here can close it; what it buys is that the gap is
        /// milliseconds wide instead of the whole test run.
        /// </remarks>
        public static UInt16 FreePort()
        {

            var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);

            listener.Start();

            try
            {
                return (UInt16) ((IPEndPoint) listener.LocalEndpoint).Port;
            }
            finally
            {
                listener.Stop();
            }

        }

        #endregion

        #region TemporaryDirectory(Purpose)

        /// <summary>
        /// A directory of its own for one test, so that no two of them read
        /// each other's accounts or configuration.
        /// </summary>
        public static String TemporaryDirectory(String Purpose)

            => Path.Combine(
                   Path.GetTempPath(),
                   $"cs-{Purpose}-{Guid.NewGuid().ToString("N")[..12]}"
               );

        #endregion

        #region Remove(Directory)

        /// <summary>
        /// Take a test's directory away again.
        /// </summary>
        /// <remarks>
        /// A directory that survives a failed run is untidy and nothing more,
        /// so this never throws: failing a teardown over it would hide the
        /// failure that actually matters.
        /// </remarks>
        public static void Remove(String? Directory)
        {

            try
            {
                if (Directory is not null && System.IO.Directory.Exists(Directory))
                    System.IO.Directory.Delete(Directory, true);
            }
            catch (IOException)
            { }
            catch (UnauthorizedAccessException)
            { }

        }

        #endregion

    }

}
