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

using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

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
        /// What became of each connection, by its identification.
        /// </summary>
        private readonly Dictionary<String, ConnectionState> dialled = [];

        /// <summary>
        /// How long a connection test stays connected before closing again.
        /// </summary>
        /// <remarks>
        /// Long enough for a back end that greets its stations to get a word
        /// in, short enough that somebody pressing the button is still looking
        /// at the screen when it finishes. It is a test of whether the
        /// connection can be made, not a conversation.
        /// </remarks>
        public static readonly TimeSpan TestHoldsFor = TimeSpan.FromSeconds(2);

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
                    return dialled.ToDictionary(entry => entry.Key, entry => entry.Value.Said);
            }
        }

        /// <summary>
        /// Where each connection this station dialled stands, by
        /// identification.
        /// </summary>
        /// <remarks>
        /// The sentence of DialledConnections, and around it what the
        /// Connections page says beside each connection: whether it is
        /// connected, since when, what was dialled - a connection changed
        /// after the start is dialled as it was until the next one - and,
        /// while it is tried again, when the next attempt is.
        /// </remarks>
        public IReadOnlyDictionary<String, ConnectionState> ConnectionStates
        {
            get
            {
                lock (dialled)
                    return new Dictionary<String, ConnectionState>(dialled);
            }
        }

        /// <summary>
        /// How many WebSocket clients the two OCPP nodes are holding.
        /// </summary>
        /// <remarks>
        /// One connection made by this station is one client here. It is the
        /// only way from outside to see that a connection test left nothing
        /// behind, which is the thing about a test that would otherwise go
        /// unnoticed until a back end complained about duplicate stations.
        /// </remarks>
        public Int32 OCPPWebSocketClientCount

            => cs01.OCPPWebSocketClients.Count() +
               cs02.OCPPWebSocketClients.Count();

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
        /// Only the ones set to connect by themselves. The rest stay written
        /// down and are not touched - an address kept ready for the day
        /// somebody needs it, which can still be tried by hand from the page.
        ///
        /// Each on its own, in the order it was written down, and none of them
        /// affected by how another went. What is at the other end is a label
        /// and nothing reads it here: whether a spare waits for its main one to
        /// fail is a decision nobody has made yet, and a guess at it now would
        /// have to be unpicked before the real answer could go in.
        /// </remarks>
        private async Task DialConfiguredConnections(CancellationToken CancellationToken = default)
        {

            var connections  = Connections.Connections;
            var byItself     = connections.Where(one => one.AutoConnect).ToArray();

            lock (dialled)
                dialled.Clear();

            if (byItself.Length == 0)
            {
                Log.Info(connections.Count == 0
                             ? "No connections are configured; this station connects nowhere."
                             : $"None of the {connections.Count} configured connection(s) is set to connect by itself.",
                         "ocpp", "connections");
                return;
            }

            foreach (var connection in byItself.OrderBy(one => one.CreatedAt))
                await Dial(connection, CancellationToken);

        }

        #endregion

        #region (private) WhatItProvesItselfWith(Connection, out Basic, out TOTP, out Certificates, out Says, out Error)

        /// <summary>
        /// The credentials or the certificate a connection names, looked up
        /// and turned into what an HTTP client takes.
        /// </summary>
        /// <remarks>
        /// One place, because connecting and testing a connection have to
        /// agree about what it would use - a test that proves itself
        /// differently from the real thing is a test of something else.
        ///
        /// <paramref name="Says"/> is for whoever is reading: a sentence
        /// naming what will be shown, without any part of the secret in it.
        /// </remarks>
        private Boolean WhatItProvesItselfWith(ConnectionEntry             Connection,
                                               out IHTTPAuthentication?    Basic,
                                               out TOTPConfig?             TOTP,
                                               out X509Certificate2[]?     Certificates,
                                               out String                  Says,
                                               out String?                 Error)
        {

            Basic         = null;
            TOTP          = null;
            Certificates  = null;
            Says          = "nothing - it identifies itself to whoever is at the other end not at all";
            Error         = null;

            if (Connection.AuthenticationId is not null)
            {

                var credentials = Connections.Authentications.
                                      FirstOrDefault(entry => entry.Id == Connection.AuthenticationId);

                if (credentials is null)
                {
                    Error = "it names credentials that are not configured here";
                    return false;
                }

                Basic  = credentials.ToBasicAuthentication();
                TOTP   = credentials.ToTOTPConfig();

                if (Basic is null && TOTP is null)
                {
                    Error = $"it uses '{credentials.Description}', which has no secret set";
                    return false;
                }

                Says = TOTP is not null
                           ? $"HTTP TOTP as '{credentials.Login}' ('{credentials.Description}'), " +
                             $"{(credentials.TLSChannelBinding ? "bound to the TLS session" : "unbound")}"
                           : $"HTTP Basic as '{credentials.Login}' ('{credentials.Description}')";

            }

            if (Connection.CertificateId is not null)
            {

                var key = ClientCertificates.Entries.
                              FirstOrDefault(entry => entry.Id == Connection.CertificateId);

                if (key?.Certificate is null)
                {
                    Error = "it names a client certificate this station does not have";
                    return false;
                }

                // A certificate whose private key this runtime could not
                // attach cannot be shown in a handshake. Said here rather than
                // left to a TLS error that names neither the certificate nor
                // the reason.
                if (!key.CanBeHeldUp)
                {
                    Error = $"it would show a certificate this runtime cannot present ({key.CannotBeHeldUp})";
                    return false;
                }

                Certificates  = [ key.Certificate ];
                Says          = $"a TLS client certificate, '{key.Subject}' ({key.Algorithm})";

            }

            return true;

        }

        #endregion

        #region (private static) SubProtocolsOf(Version)

        /// <summary>
        /// What this station offers in the WebSocket handshake.
        /// </summary>
        /// <remarks>
        /// 2.1 offers 2.0.1 as well, which is what the OCPP node does and what
        /// most back ends of that generation actually answer with.
        /// </remarks>
        private static String[] SubProtocolsOf(OCPP.OCPPVersion Version)

            => Version == OCPP.OCPPVersion.OCPP1_6
                   ? [ OCPPv1_6.Version.WebSocketSubProtocolId ]
                   : [ OCPPv2_1.Version.WebSocketSubProtocolId, "ocpp2.0.1" ];

        #endregion

        #region (private) Dial(Connection, CancellationToken)

        /// <summary>
        /// One connection: what it proves itself with, and whether it got
        /// through.
        /// </summary>
        private async Task Dial(ConnectionEntry    Connection,
                                CancellationToken  CancellationToken)
        {

            var where = $"'{Connection.Description}' ({Connection.URL})";

            try
            {

                if (!WhatItProvesItselfWith(Connection, out var basic, out var totp, out var certificates, out _, out var wrong))
                {
                    Fail(Connection, ConnectionStatus.NotDialled, $"Not dialled: {where} {wrong}.");
                    return;
                }

                Log.Info($"Dialling {where} as {ConnectionEntry.AsText(Connection.OCPPVersion)} " +
                         $"{Connection.ConnectionType}.", "ocpp", "connections");

                var response = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6

                                   ? await cs01.ConnectOCPPWebSocketClient(
                                             RemoteURL:           Connection.URL,
                                             HTTPAuthentication:  basic,
                                             ClientCertificates:  certificates,
                                             TOTPConfig:          totp,
                                             DNSClient:           DNSClient,
                                             CancellationToken:   CancellationToken
                                         )

                                   : await cs02.ConnectOCPPWebSocketClient(
                                             RemoteURL:           Connection.URL,
                                             HTTPAuthentication:  basic,
                                             ClientCertificates:  certificates,
                                             TOTPConfig:          totp,
                                             DNSClient:           DNSClient,
                                             CancellationToken:   CancellationToken
                                         );

                // 101 and nothing else: an HTTP answer that is not an upgrade
                // is a web server being polite, not a back end.
                //
                // Nor is every answer one the far end gave. An attempt that
                // ended before it could ask anything - nothing listening, a
                // name that does not resolve, a TLS handshake that failed - is
                // answered by the client itself, with a 400 that looks like a
                // real one. What gives it away is that it answers no request:
                // there was none. So a station whose back end is not there says
                // it could not be reached, rather than sending somebody looking
                // for a server that answered 400 and never existed.
                var client     = ClientOf(Connection);
                var unreached  = response.HTTPRequest is null;

                if (response.HTTPStatusCode != HTTPStatusCode.SwitchingProtocols)
                {

                    // Whether it goes on, asked of the client rather than read
                    // from the answer: a first attempt that found nothing, or a
                    // back end not ready yet, is tried again by itself; an
                    // answer that means no - a wrong address, a wrong password
                    // - is an answer, and is not.
                    var keepsTrying = client?.KeepsTrying == true;

                    Fail(Connection,
                         keepsTrying ? ConnectionStatus.Trying
                                     : unreached ? ConnectionStatus.Failed
                                                 : ConnectionStatus.Refused,
                         (unreached
                              ? $"{where} could not be reached: the attempt ended before anything could be asked of it"
                              : $"{where} did not become a WebSocket connection: {response.HTTPStatusCode}") +
                         (keepsTrying
                              ? "; it is tried again by itself, and said here when it gets through."
                              : unreached
                                    ? "; it is not tried again."
                                    : "; that answer is final, and it is not tried again."));

                    if (client is not null && keepsTrying)
                        Follow(Connection, client, Connected: false);

                    return;

                }

                if (client is not null)
                    Follow(Connection, client, Connected: true);

                else
                    Log.Warning($"'{Connection.Description}' connected, but the client that did it could not be found again, " +
                                 "so nothing could be said about what becomes of it.", "ocpp", "connections");

                Note(Connection, "Connected, and will come back by itself if it drops.");

            }
            catch (Exception e)
            {
                // Anything at all: a name that does not resolve, a refused
                // socket, a certificate the other end will not accept. One
                // connection that cannot be made must not take the station
                // down with it.
                Fail(Connection, ConnectionStatus.Failed, $"{where} could not be reached: {e.Message}");
            }

        }

        #endregion

        #region (private) ClientOf(Connection)

        /// <summary>
        /// The client the node made for this connection when it was dialled.
        /// </summary>
        /// <remarks>
        /// Made inside the call and kept by the node, whether it got through or
        /// not; the last one to the connection's address is the one this dial
        /// made.
        /// </remarks>
        private WebSocketClient? ClientOf(ConnectionEntry Connection)

            => (Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6
                    ? cs01.OCPPWebSocketClients
                    : cs02.OCPPWebSocketClients).
               OfType<WebSocketClient>().
               LastOrDefault(one => one.RemoteURL == Connection.URL);

        #endregion

        #region (private) Follow(Connection, Client, Connected)

        /// <summary>
        /// Say what becomes of a dialled connection from here on.
        /// </summary>
        /// <remarks>
        /// The client comes back by itself - after a drop, and until it gets
        /// through at all: the node gave it its policy before its first
        /// attempt, see BuildOCPPNodes. What becomes of it is said where what
        /// happened when it was dialled is said, in the log and in
        /// DialledConnections, which otherwise went on saying "Connected" of a
        /// connection gone for an hour, and "did not become a WebSocket
        /// connection" of one made long since.
        ///
        /// Not while this station hangs up: HangUp takes the policy away first,
        /// and a close this station asked for is not a loss. Every event below
        /// asks for the policy before it says anything, so that one arriving
        /// late from a client being closed does not bring back a connection
        /// HangUp has just forgotten.
        /// </remarks>
        /// <param name="Connection">The connection, as it is written down.</param>
        /// <param name="Client">The client the node made for it.</param>
        /// <param name="Connected">Whether the dial got through, so that the next connection the client opens is "again".</param>
        private void Follow(ConnectionEntry  Connection,
                            WebSocketClient  Client,
                            Boolean          Connected)
        {

            var connectedBefore = Connected;

            Client.OnCloseMessageReceived += (timestamp, sender, connection, frame, eventTrackingId, statusCode, reason, cancellationToken) => {

                if (Client.ReconnectPolicy is not null)
                    Fail(Connection, ConnectionStatus.Lost,
                         $"'{Connection.Description}' was lost ({(UInt16) statusCode} {statusCode}" +
                         $"{(String.IsNullOrWhiteSpace(reason) ? "" : $": {reason}")}); it comes back by itself.");

                return Task.CompletedTask;

            };

            // A connection that went without a word - a back end that crashed,
            // a socket reset underneath, pings no longer answered - arrives
            // above as well: the client reports it with a close frame of its
            // own making, 1006.

            // Before every attempt the client makes, and so what the page says
            // of when the next one is.
            Client.OnReconnecting += (timestamp, sender, attempt, delay, cancellationToken) => {

                if (Client.ReconnectPolicy is null)
                    return Task.CompletedTask;

                Log.Debug($"'{Connection.Description}': trying again in {delay.TotalSeconds:F1} s (attempt {attempt}).", "ocpp", "connections");

                NextAttempt(Connection, attempt, delay);

                return Task.CompletedTask;

            };

            // After every attempt, whatever came of it. One that ended the
            // client's trying is the end of "it comes back by itself": a back
            // end that came back as something that will not have this station
            // - a 401 after its passwords were changed, a 404 after the station
            // was removed from it. Without this the station went on promising a
            // connection that nothing was trying to make any more.
            Client.ResponseLogDelegate += (timestamp, sender, request, response) => {

                if (Client.ReconnectPolicy is not null && !Client.KeepsTrying)
                    Fail(Connection, ConnectionStatus.Refused,
                         response.HTTPStatusCode != HTTPStatusCode.SwitchingProtocols
                             ? $"'{Connection.Description}' answered {response.HTTPStatusCode} when it was tried again; " +
                                "that answer is final, and it is not tried again."
                             : $"'{Connection.Description}' ended the connection in a way that is not tried again " +
                               $"({Client.ClientCloseMessage ?? "nothing more was said"}).");

                return Task.CompletedTask;

            };

            // Sent for every connection the client opens from here on: the first
            // one a connection that failed at the start gets, or the next one
            // after a loss.
            Client.OnWebSocketConnectionAccepted += (timestamp, sender, connection, response, cancellationToken) => {

                if (Client.ReconnectPolicy is null)
                    return Task.CompletedTask;

                Note(Connection, connectedBefore
                                     ? "Connected again, and will come back by itself if it drops."
                                     : "Connected, and will come back by itself if it drops.");

                connectedBefore = true;

                return Task.CompletedTask;

            };

        }

        #endregion

        #region (private) Note(Connection, What) / Fail(Connection, Status, What)

        /// <summary>
        /// Say that the connection is connected.
        /// </summary>
        private void Note(ConnectionEntry Connection, String What)
        {

            Record(Connection, ConnectionStatus.Connected, What);

            Log.Info($"'{Connection.Description}': {What}", "ocpp", "connections");

        }

        /// <summary>
        /// Say that the connection is not, and how it stands.
        /// </summary>
        private void Fail(ConnectionEntry Connection, ConnectionStatus Status, String What)
        {

            Record(Connection, Status, What);

            // A warning and not an error: the station is doing what it can,
            // and the thing that is wrong is normally somewhere else and
            // normally fixable from the page this message is visible on.
            Log.Warning(What, "ocpp", "connections");

        }

        private void Record(ConnectionEntry Connection, ConnectionStatus Status, String What)
        {
            lock (dialled)
                dialled[Connection.Id] = new ConnectionState(
                                             Status,
                                             TimeProvider.GetUtcNow(),
                                             What,
                                             Connection.Description,
                                             Connection.URL,
                                             Connection.OCPPVersion
                                         );
        }

        #endregion

        #region (private) NextAttempt(Connection, Attempt, Delay)

        /// <summary>
        /// When the next attempt is, beside how the connection stands - which
        /// does not change with it: a connection lost ten minutes ago and
        /// tried for the fifth time is still lost since ten minutes ago.
        /// </summary>
        private void NextAttempt(ConnectionEntry Connection, UInt32 Attempt, TimeSpan Delay)
        {
            lock (dialled)
                if (dialled.TryGetValue(Connection.Id, out var state))
                    dialled[Connection.Id] = state with {
                                                 Attempt        = Attempt,
                                                 NextAttemptAt  = TimeProvider.GetUtcNow() + Delay
                                             };
        }

        #endregion

        #region TestConnection(Id, CancellationToken = default)

        /// <summary>
        /// Make this one connection, once, write down everything that happened,
        /// and close it again.
        /// </summary>
        /// <remarks>
        /// For the button beside a connection on the page, and for the reason
        /// that button exists: a connection that is only written down is never
        /// tried, so the first time anybody finds out whether the address, the
        /// certificate and the password are right is the day it is switched on
        /// - which is normally the day it has to work.
        ///
        /// It is its own client and not one of the OCPP nodes'. A test that
        /// left a client behind in the node would be a test that changed the
        /// station, and running it twice would leave two. What it measures is
        /// everything up to and including the WebSocket handshake - the name,
        /// the route, the TLS, the credentials, the sub-protocol - which is
        /// where connections actually fail.
        ///
        /// It closes politely rather than dropping the socket: a back end that
        /// is told the connection is going away logs a test, and one that is
        /// left hanging logs a fault.
        ///
        /// <b>It tests what it is handed, not what is stored.</b> The fields
        /// arrive from the form somebody is looking at, which is the only
        /// answer that is never surprising: the button beside a connection
        /// being written down for the first time has nothing stored to test,
        /// and the button beside one being edited would otherwise test the
        /// version on disk while somebody watches the version on screen. What
        /// is stored is not touched either way - a test writes nothing.
        /// </remarks>
        /// <param name="Description">What it is; only for the log and the answer.</param>
        /// <param name="URL">Where it goes.</param>
        /// <param name="ConnectionType">What is at the other end; a label.</param>
        /// <param name="OCPPVersion">Which OCPP, and so which sub-protocols are offered.</param>
        /// <param name="AutoConnect">Whether this is one the station would connect to by itself, which the answer mentions but does not act on.</param>
        /// <param name="AuthenticationId">The credentials to prove itself with, by identification.</param>
        /// <param name="CertificateId">The client certificate to prove itself with, by identification.</param>
        public async Task<JObject> TestConnection(String?            Description,
                                                  String?            URL,
                                                  String?            ConnectionType,
                                                  String?            OCPPVersion           = null,
                                                  Boolean            AutoConnect           = false,
                                                  String?            AuthenticationId      = null,
                                                  String?            CertificateId         = null,
                                                  CancellationToken  CancellationToken     = default)
        {

            var clock  = Stopwatch.StartNew();
            var steps  = new JArray();

            void Step(String Level, String Text)
                => steps.Add(new JObject(
                       new JProperty("at_ms",  clock.ElapsedMilliseconds),
                       new JProperty("level",  Level),
                       new JProperty("text",   Text)
                   ));

            JObject Done(ConnectionEntry? Connection, Boolean OK)
            {

                clock.Stop();

                return new JObject(
                           new JProperty("description",  Connection?.Description ?? (Description ?? "").Trim()),
                           new JProperty("url",          Connection?.URL.ToString() ?? (URL ?? "").Trim()),
                           new JProperty("ok",           OK),
                           new JProperty("runtime_ms",   clock.ElapsedMilliseconds),
                           new JProperty("steps",        steps)
                       );

            }

            #region Is it even a connection

            // The same rules as writing one down, and the same sentences: a
            // test of a half-filled form should say what is missing rather
            // than fail at a socket with something less useful.
            if (!ConnectionEntry.Validate(Description, URL, ConnectionType, OCPPVersion,
                                          out var url, out var type, out var version, out var notYet))
            {
                Step("error", notYet);
                return Done(null, false);
            }

            // A throwaway, so that everything below reads one shape whether it
            // came from the store or from a form. Nothing writes it anywhere.
            var connection = new ConnectionEntry(
                                 "",
                                 Description!.Trim(),
                                 url,
                                 type,
                                 TimeProvider.GetUtcNow()
                             ) {
                                 OCPPVersion       = version,
                                 AutoConnect       = AutoConnect,
                                 AuthenticationId  = ConnectionEntry.Named(AuthenticationId),
                                 CertificateId     = ConnectionEntry.Named(CertificateId)
                             };

            #endregion

            Log.Info($"Testing the connection to '{connection.Description}' ({connection.URL}) ...", "ocpp", "connections", "test");

            #region What is going to be tried

            Step("info", $"{ConnectionEntry.AsText(connection.OCPPVersion)} to {connection.URL}, " +
                         $"written down as {connection.ConnectionType}.");

            Step(connection.IsSecure ? "info" : "warning",
                 connection.IsSecure
                     ? "The URL is a TLS one, so the certificate of whatever answers will be checked against this station's trust store."
                     : "The URL is not a TLS one. Nothing on this connection is encrypted or authenticated at the transport layer.");

            if (!WhatItProvesItselfWith(connection, out var basic, out var totp, out var certificates, out var proves, out var wrong))
            {
                Step("error", $"Not tried: {wrong}.");
                Log.Warning($"The connection test for '{connection.Description}' was not run: {wrong}.", "ocpp", "connections", "test");
                return Done(connection, false);
            }

            Step("info", $"It will prove itself with {proves}.");

            if (!connection.AutoConnect)
                Step("info", "This is not one the station connects to by itself. A test that gets through does not " +
                             "change that - it only says that it could.");

            #endregion

            #region Does the name resolve

            var host = connection.URL.Host.ToString();

            if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _))
            {
                try
                {

                    var lookedUp  = await DNSClient.Query(
                                              DNSServiceName.Parse(host),
                                              [ DNSResourceRecordTypes.A, DNSResourceRecordTypes.AAAA ],
                                              CancellationToken: CancellationToken
                                          );

                    var addresses = lookedUp.Answers.Take(8).Select(record => record.RText ?? record.ToString()).ToArray();

                    Step(addresses.Length > 0 ? "info" : "warning",
                         addresses.Length > 0
                             ? $"'{host}' resolves to {String.Join(", ", addresses)}."
                             : $"'{host}' resolved to nothing ({lookedUp.ResponseCode}). The connection below will not get far.");

                }
                catch (Exception e)
                {
                    // Not fatal to the test: the socket layer has its own
                    // resolver and may well succeed where this lookup did not.
                    Step("warning", $"'{host}' could not be looked up here: {e.Message}. Trying to connect anyway.");
                }
            }

            #endregion

            #region Can the socket even be opened

            // Asked separately, and it is the difference between the two
            // answers somebody actually needs: "nothing is there" and "something
            // is there and it said no". The WebSocket client cannot tell them
            // apart - when its connection is refused it has no stream to read
            // and answers itself with a 400 that carries a Date, a
            // Content-Type and a Content-Length, indistinguishable from a real
            // one. Measured, against a port nothing was listening on. So the
            // reachable-or-not question is settled here, before anything can
            // invent an answer to it.
            var port = connection.URL.Port?.ToUInt16() ?? (connection.IsSecure ? (UInt16) 443 : (UInt16) 80);

            try
            {

                using var probe = new TcpClient();

                await probe.ConnectAsync(host.Trim('[', ']'), port, CancellationToken);

                Step("info", $"A TCP connection to {host}:{port} was accepted.");

            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {

                // The name of the socket error and never the operating
                // system's own words for it: those are translated, and a
                // German station and an English one would say different things
                // about the same fault.
                var why = e is SocketException socket
                              ? socket.SocketErrorCode.ToString()
                              : e.GetType().Name;

                Step("error", $"Nothing accepted a TCP connection on {host}:{port} ({why}). " +
                              "There is nothing to speak WebSocket to, so the rest was not tried.");

                Log.Warning($"The connection test for '{connection.Description}' found nothing at {host}:{port}: {why}.",
                            "ocpp", "connections", "test");

                return Done(connection, false);

            }

            #endregion

            WebSocketClient? client = null;

            try
            {

                #region Open it

                client = new WebSocketClient(
                             connection.URL,
                             HTTPAuthentication:     basic,
                             SecWebSocketProtocols:  SubProtocolsOf(connection.OCPPVersion),
                             ClientCertificates:     certificates,
                             TOTPConfig:             totp,
                             DisableWebSocketPings:  true,
                             DisableLogging:         true
                         );

                client.OnTextMessageReceived   += (timestamp, sender, conn, frame, tracking, message, token) => {
                    Step("info", $"Received: {(message.Length > 400 ? message[..400] + " ..." : message)}");
                    return Task.CompletedTask;
                };

                client.OnBinaryMessageReceived += (timestamp, sender, conn, frame, tracking, message, token) => {
                    Step("info", $"Received {message.Length} byte(s) of binary.");
                    return Task.CompletedTask;
                };

                client.OnCloseMessageReceived  += (timestamp, sender, conn, frame, tracking, status, reason, token) => {
                    Step("info", $"The other end closed the connection: {status}{(reason is not null ? $" ({reason})" : "")}.");
                    return Task.CompletedTask;
                };

                Step("info", $"Connecting, offering {String.Join(", ", SubProtocolsOf(connection.OCPPVersion))} ...");

                var (_, response) = await client.Connect(
                                              MaxNumberOfRetries:  0,
                                              CancellationToken:   CancellationToken
                                          );

                #endregion

                #region What came back

                if (response.HTTPStatusCode != HTTPStatusCode.SwitchingProtocols)
                {

                    Step("error", $"It did not become a WebSocket connection: {response.HTTPStatusCode}.");

                    // The socket was accepted a moment ago, so whatever is
                    // there either refused the upgrade or dropped the
                    // connection before answering. The headers below are worth
                    // reading only in the first case, and the client writes
                    // the same shape in both - so they are offered as what the
                    // client has rather than as what the far end said.
                    Step("info", $"What the client has of the answer: " +
                                 String.Join("; ", response.Take(8).Select(header => $"{header.Key}: {header.Value}")) +
                                 ". The socket was accepted, so something is there - it either would not upgrade, " +
                                 "or went away before saying anything.");

                    Log.Warning($"The connection test for '{connection.Description}' did not get through: {response.HTTPStatusCode}.",
                                "ocpp", "connections", "test");

                    return Done(connection, false);

                }

                // Read out of the headers rather than off a typed property:
                // on a response this is a single header that may simply not be
                // there, which is what a server that ignores sub-protocols
                // sends - and is worth saying, because an OCPP back end that
                // does that is a back end that has not agreed to speak OCPP.
                var agreedOn = response.FirstOrDefault(header => header.Key.Equals("Sec-WebSocket-Protocol",
                                                                                   StringComparison.OrdinalIgnoreCase)).
                                        Value?.ToString();

                Step("notice", agreedOn is not null && agreedOn.Length > 0
                                   ? $"Connected. The handshake was accepted for {agreedOn}."
                                   : "Connected, but the handshake named no sub-protocol. Whatever is there took the " +
                                     "connection without agreeing to speak OCPP.");

                #endregion

                #region Hold it briefly, then let go

                Step("info", $"Staying connected for {TestHoldsFor.TotalSeconds:0.#} second(s), to see whether anything is said.");

                await Task.Delay(TestHoldsFor, CancellationToken);

                Step("info", "Closing.");

                await client.Close(
                          WebSocketFrame.ClosingStatusCode.NormalClosure,
                          "Connection test finished",
                          CancellationToken: CancellationToken
                      );

                Step("notice", "Closed. The connection can be made.");

                #endregion

                Log.Notice($"The connection test for '{connection.Description}' got through.", "ocpp", "connections", "test");

                return Done(connection, true);

            }
            catch (Exception e)
            {

                Step("error", $"{e.GetType().Name}: {e.Message}");

                Log.Warning($"The connection test for '{connection.Description}' failed: {e.Message}", "ocpp", "connections", "test");

                return Done(connection, false);

            }
            finally
            {
                // Whatever happened, nothing of this test is left connected.
                if (client is not null)
                {
                    try
                    {
                        client.ReconnectPolicy = null;
                        await client.Close();
                    }
                    catch
                    { }
                }
            }

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
