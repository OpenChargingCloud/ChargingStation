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

using System.Security.Cryptography.X509Certificates;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Dialling the back ends this station was told about.
    /// </summary>
    /// <remarks>
    /// Everything that decides how a connection is made is configuration - the
    /// URL, the credentials, the certificate, whether to come back after a
    /// drop - and this file is the only place that turns it into an actual
    /// call. What is configured lives in <see cref="ConnectionStore"/> and can
    /// be changed from the web interface while the station runs; what has been
    /// dialled lives here.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What each connection did, by its identification.
        /// </summary>
        private readonly Dictionary<String, String> dialled = [];

        #endregion

        #region Properties

        /// <summary>
        /// What happened to each configured connection the last time this
        /// station tried it, by identification.
        /// </summary>
        /// <remarks>
        /// One sentence each, the same one that went into the log. Kept so
        /// that somebody arriving at the station later can be told without
        /// having to find the moment it started in the log.
        /// </remarks>
        public IReadOnlyDictionary<String, String> DialledConnections
        {
            get
            {
                lock (dialled)
                    return new Dictionary<String, String>(dialled);
            }
        }

        #endregion


        #region (private) DialConfiguredConnections(CancellationToken = default)

        /// <summary>
        /// Dial everything this station was told to dial.
        /// </summary>
        /// <remarks>
        /// After the servers are listening, and deliberately so: somebody
        /// watching the Logs page sees each connection come up or fail, rather
        /// than having to reload afterwards to find out how it went. It is the
        /// same reason the V2G link starts where it does.
        ///
        /// Nothing here throws. A station whose back end is unreachable is a
        /// station that still has to open its door, run its display and answer
        /// its web interface - not least because the web interface is where
        /// somebody fixes the address that was wrong.
        ///
        /// The spare management system is dialled only when the main one could
        /// not be reached. That is what makes it a spare rather than a second
        /// one: two live connections to two management systems means two
        /// places that each believe they are in charge of the same station.
        /// </remarks>
        private async Task DialConfiguredConnections(CancellationToken CancellationToken = default)
        {

            var connections = Connections.Connections;

            if (connections.Count == 0)
            {
                Log.Info("No connections are configured; this station dials nowhere.", "ocpp", "connections");
                return;
            }

            lock (dialled)
                dialled.Clear();

            var mainSystemIsUp = false;
            var anyMainSystem  = false;

            // The spares last, and the order matters rather than being tidy:
            // whether a spare is dialled at all depends on how the others went.
            foreach (var connection in connections.OrderBy(one => one.ConnectionType == OCPP.ConnectionType.CSMSBackup ? 1 : 0).
                                                   ThenBy  (one => one.CreatedAt))
            {

                if (connection.ConnectionType == OCPP.ConnectionType.CSMS)
                    anyMainSystem = true;

                if (connection.ConnectionType == OCPP.ConnectionType.CSMSBackup)
                {

                    if (!anyMainSystem)
                        Note(connection, "Dialling the spare management system: none is configured as the main one.");

                    else if (mainSystemIsUp)
                    {
                        Note(connection, "Not dialled: the management system it stands in for answered.");
                        continue;
                    }

                    else
                        Note(connection, "The management system did not answer, so the spare is being dialled.");

                }

                var reached = await Dial(connection, CancellationToken);

                if (connection.ConnectionType == OCPP.ConnectionType.CSMS && reached)
                    mainSystemIsUp = true;

            }

        }

        #endregion

        #region (private) Dial(Connection, CancellationToken)

        /// <summary>
        /// One connection: what it proves itself with, and whether it got
        /// through.
        /// </summary>
        private async Task<Boolean> Dial(ConnectionEntry    Connection,
                                         CancellationToken  CancellationToken)
        {

            var where = $"'{Connection.Description}' ({Connection.URL})";

            try
            {

                #region What it proves itself with

                IHTTPAuthentication?  basic        = null;
                TOTPConfig?           totp         = null;
                X509Certificate2[]?   certificates = null;

                if (Connection.AuthenticationId is not null)
                {

                    var credentials = Connections.Authentications.
                                          FirstOrDefault(entry => entry.Id == Connection.AuthenticationId);

                    if (credentials is null)
                    {
                        Fail(Connection, $"Not dialled: {where} names credentials that are not configured here.");
                        return false;
                    }

                    basic  = credentials.ToBasicAuthentication();
                    totp   = credentials.ToTOTPConfig();

                    if (basic is null && totp is null)
                    {
                        Fail(Connection, $"Not dialled: {where} uses '{credentials.Description}', which has no secret set.");
                        return false;
                    }

                }

                if (Connection.CertificateId is not null)
                {

                    var key = ClientCertificates.Entries.
                                  FirstOrDefault(entry => entry.Id == Connection.CertificateId);

                    if (key?.Certificate is null)
                    {
                        Fail(Connection, $"Not dialled: {where} names a client certificate this station does not have.");
                        return false;
                    }

                    // A certificate whose private key this runtime could not
                    // attach cannot be shown in a handshake. Said here rather
                    // than left to a TLS error that names neither the
                    // certificate nor the reason.
                    if (!key.CanBeHeldUp)
                    {
                        Fail(Connection, $"Not dialled: {where} would show a certificate this runtime cannot present ({key.CannotBeHeldUp}).");
                        return false;
                    }

                    certificates = [ key.Certificate ];

                }

                #endregion

                Log.Info($"Dialling {where} as {ConnectionEntry.AsText(Connection.OCPPVersion)} " +
                         $"{Connection.ConnectionType}.", "ocpp", "connections");

                var response = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6

                                   ? await cs01.ConnectOCPPWebSocketClient(
                                             RemoteURL:           Connection.URL,
                                             HTTPAuthentication:  basic,
                                             ClientCertificates:  certificates,
                                             TOTPConfig:          totp,
                                             DNSClient:           dnsClient,
                                             CancellationToken:   CancellationToken
                                         )

                                   : await cs02.ConnectOCPPWebSocketClient(
                                             RemoteURL:           Connection.URL,
                                             HTTPAuthentication:  basic,
                                             ClientCertificates:  certificates,
                                             TOTPConfig:          totp,
                                             DNSClient:           dnsClient,
                                             CancellationToken:   CancellationToken
                                         );

                // 101 and nothing else: an HTTP answer that is not an upgrade
                // is a web server being polite, not a back end.
                //
                // What it is not is a report of who said so. Measured: when
                // the connection is refused outright the client has no stream
                // to read and answers itself with a bare 400, headers and all
                // absent - indistinguishable at this point from a real 400
                // sent by something that is listening. So the sentence says
                // what happened and not who did it; claiming the far end
                // answered would send somebody looking for a server that was
                // never there.
                if (response.HTTPStatusCode != HTTPStatusCode.SwitchingProtocols)
                {
                    Fail(Connection, $"{where} did not become a WebSocket connection: {response.HTTPStatusCode}. " +
                                      "Nothing answering and an answer that will not upgrade look the same here.");
                    return false;
                }

                KeepComingBack(Connection);

                Note(Connection, $"Connected{(Connection.AutomaticReconnect ? ", and will dial again by itself if it drops" : "")}.");

                return true;

            }
            catch (Exception e)
            {
                // Anything at all: a name that does not resolve, a refused
                // socket, a certificate the other end will not accept. One
                // connection that cannot be made must not take the station
                // down with it.
                Fail(Connection, $"{where} could not be reached: {e.Message}");
                return false;
            }

        }

        #endregion

        #region (private) KeepComingBack(Connection)

        /// <summary>
        /// Whether this connection dials again by itself after a drop.
        /// </summary>
        /// <remarks>
        /// Set after the connection is made rather than before, because the
        /// client that carries the policy is made inside the call: the node
        /// builds it, connects it and keeps it. The policy is about losing a
        /// connection that exists, so the moment after it exists is soon
        /// enough.
        ///
        /// Off means null, which is what the WebSocket client reads as "do not
        /// come back". Setting it to a policy with no attempts would be a
        /// station that reconnects zero times and still counts them.
        /// </remarks>
        private void KeepComingBack(ConnectionEntry Connection)
        {

            var client = (Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6
                              ? cs01.OCPPWebSocketClients
                              : cs02.OCPPWebSocketClients).
                         OfType<WebSocketClient>().
                         LastOrDefault(one => one.RemoteURL == Connection.URL);

            if (client is null)
            {
                Log.Warning($"'{Connection.Description}' connected, but the client that did it could not be found again, " +
                             "so nothing could be said about reconnecting.", "ocpp", "connections");
                return;
            }

            client.ReconnectPolicy = Connection.AutomaticReconnect
                                         ? new WebSocketClientReconnectPolicy()
                                         : null;

        }

        #endregion

        #region (private) Note(Connection, What) / Fail(Connection, What)

        private void Note(ConnectionEntry Connection, String What)
        {

            lock (dialled)
                dialled[Connection.Id] = What;

            Log.Info(What.StartsWith("Connected", StringComparison.Ordinal)
                         ? $"'{Connection.Description}': {What}"
                         : What,
                     "ocpp", "connections");

        }

        private void Fail(ConnectionEntry Connection, String What)
        {

            lock (dialled)
                dialled[Connection.Id] = What;

            // A warning and not an error: the station is doing what it can,
            // and the thing that is wrong is normally somewhere else and
            // normally fixable from the page this message is visible on.
            Log.Warning(What, "ocpp", "connections");

        }

        #endregion

        #region (private) HangUp()

        /// <summary>
        /// Close every connection this station made.
        /// </summary>
        /// <remarks>
        /// Closed rather than dropped, because a close that says so is a close
        /// that does not start a reconnect: the policy deliberately ignores a
        /// shutdown the application asked for. A station restarting would
        /// otherwise spend its last second dialling.
        /// </remarks>
        private async Task HangUp()
        {

            foreach (var client in cs01.OCPPWebSocketClients.OfType<WebSocketClient>().
                                   Concat(cs02.OCPPWebSocketClients.OfType<WebSocketClient>()))
            {
                try
                {
                    client.ReconnectPolicy = null;
                    await client.Close();
                }
                catch (Exception e)
                {
                    Log.Debug($"A connection to {client.RemoteURL} did not close cleanly: {e.Message}", "ocpp", "connections");
                }
            }

            lock (dialled)
                dialled.Clear();

        }

        #endregion

    }

}
