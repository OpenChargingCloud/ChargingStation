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
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The display's own server: built, and moved to another port while the
    /// station runs.
    /// </summary>
    /// <remarks>
    /// Moving it is not stopping it on one port and starting it on another.
    /// The new port is bound first, so that a port something else has is a
    /// refusal that leaves the display where it was; and the old one keeps
    /// answering for a while, saying where the display went, because a screen
    /// in a car park is pointed at it and would otherwise show a dead page
    /// until somebody walked out to it.
    /// </remarks>
    public partial class ChargingStation
    {

        #region (private) BuildTheDisplay(Address, Port, SayWhatIsMissing)

        /// <summary>
        /// The display's server and API on the given address and port, built
        /// and not started.
        /// </summary>
        /// <param name="Address">Where it listens.</param>
        /// <param name="Port">On which port.</param>
        /// <param name="SayWhatIsMissing">Whether to log that there is no display page to serve - once, at the start, and not again at every move.</param>
        private (HTTPServer Server, KioskHTTPAPI API) BuildTheDisplay(IIPAddress  Address,
                                                                      IPPort      Port,
                                                                      Boolean     SayWhatIsMissing)
        {

            var server = new HTTPServer(
                             IPAddress:       Address,
                             TCPPort:         Port,
                             HTTPServerName:  $"OpenChargingCloud ChargingStation Display v{Version}",
                             DNSClient:       this.DNSClient
                         );

            var api    = new KioskHTTPAPI(
                             HTTPServer:  server,
                             Station:     this,
                             Log:         this.Log
                         );

            if (this.Frontend.TryGet(KioskHTTPAPI.IndexFile, out _))
                server.AddHTTPAPI().
                       MapSinglePageApplication(
                           this.Frontend,
                           new SinglePageAppOptions {
                               // The same bundle as the web interface, entered
                               // at its other door. The assets are shared; the
                               // page is not.
                               IndexFile       = KioskHTTPAPI.IndexFile,
                               IndexTransform  = html => html.Replace("{{ServerVersion}}", $"v{Version}", StringComparison.Ordinal)
                           }
                       );

            else if (SayWhatIsMissing)
                this.Log.Error(
                    $"No display page to serve ({this.Frontend.Description} has no '{KioskHTTPAPI.IndexFile}'): " +
                    "the display API answers, the screen gets nothing.",
                    "kiosk"
                );

            server.OnHTTPRequest += (_, request, _) => {
                this.Log.Debug($"{request.HTTPMethod} {request.Path} from {request.RemoteSocket}", "kiosk", "http");
                return Task.CompletedTask;
            };

            return (server, api);

        }

        #endregion

        #region (private) OpenTheDisplayAt(Port)

        /// <summary>
        /// The display's server on another port, built - and, on a station that
        /// is running, already listening there - or why it cannot be.
        /// </summary>
        /// <remarks>
        /// Bound before anything else is changed, so that a refusal leaves the
        /// display where it was and the file as it was.
        /// </remarks>
        private async Task<(HTTPServer? Server, KioskHTTPAPI? API, String? Error)> OpenTheDisplayAt(IPPort Port)
        {

            if (kioskAddress is null)
                return (null, null, "This station was started without a display (--no-kiosk): there is no display to move.");

            if (Port == HTTPPort)
                return (null, null, $"Port {Port} is the web interface's. The point of the display being its own server is that it is somewhere else.");

            if (LocalAppPort.HasValue && Port == LocalAppPort.Value)
                return (null, null, $"Port {Port} is the local app server's.");

            var (server, api) = BuildTheDisplay(kioskAddress, Port, SayWhatIsMissing: false);

            if (!started)
                return (server, api, null);

            try
            {
                await server.Start();
                return (server, api, null);
            }
            catch (SocketException problem)
            {

                try
                {
                    await server.Stop();
                }
                catch
                { }

                return (null, null, $"The display cannot move to port {Port}: {problem.Message} It stays on {KioskPort}.");

            }

        }

        #endregion

        #region (private) HandOverTheDisplay(Server, API, Port)

        /// <summary>
        /// Make the display opened by OpenTheDisplayAt the display, and keep
        /// the old one answering for <see cref="DisplayHandover"/> - saying
        /// where the display went - before it is let go.
        /// </summary>
        private void HandOverTheDisplay(HTTPServer    Server,
                                        KioskHTTPAPI  API,
                                        IPPort        Port)
        {

            var oldServer  = kioskServer;
            var oldAPI     = KioskAPI;
            var oldURL     = KioskURL;

            kioskServer    = Server;
            KioskAPI       = API;
            KioskPort      = Port;
            KioskURL       = URL.Parse($"http://{kioskAddress}:{Port}/");

            // Before the start nothing was listening, and nothing is pointed
            // at the port it would have had.
            if (!started || oldServer is null)
                return;

            if (oldAPI is not null)
                oldAPI.MovedTo = Port;

            lock (handedOver)
                handedOver.Add(oldServer);

            var handover = DisplayHandover;

            _ = Task.Run(async () => {

                    await Task.Delay(handover);

                    if (Untake(oldServer))
                    {
                        try
                        {
                            await oldServer.Stop();
                        }
                        catch (Exception e)
                        {
                            Log.Warning($"The port the display moved away from could not be let go: {e.Message}", "kiosk");
                        }
                    }

                });

            Log.Notice(
                $"The display moved to {KioskURL}. {oldURL} tells the screens still on it where it went for {handover.TotalSeconds:0.#} s, and is then let go.",
                "kiosk", "config"
            );

        }

        #endregion

        #region (private) Untake(Server) / TakeTheHandedOver()

        /// <summary>
        /// Take one server off the handed-over list: true for whoever took it,
        /// who is then the one to stop it.
        /// </summary>
        private Boolean Untake(HTTPServer Server)
        {
            lock (handedOver)
                return handedOver.Remove(Server);
        }

        /// <summary>
        /// Take every server off the handed-over list, to stop them.
        /// </summary>
        private HTTPServer[] TakeTheHandedOver()
        {
            lock (handedOver)
            {
                var all = handedOver.ToArray();
                handedOver.Clear();
                return all;
            }
        }

        #endregion

    }

}
