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
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using OCPPCommon = cloud.charging.open.protocols.OCPP;
using OCPPv1_6   = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1   = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.SSH;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

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

        /// <summary>
        /// One DialAgain at a time - see there.
        /// </summary>
        private readonly SemaphoreSlim dialAgainLock = new (1, 1);

        /// <summary>
        /// The SSH clients this station closed, or is closing: what they say
        /// from then on is not a loss, nor anything to come back from.
        /// </summary>
        /// <remarks>
        /// What a WebSocket client's reconnect policy taken away is for those:
        /// an SSH client keeps its policy, and is told to stop by being closed.
        /// </remarks>
        private readonly HashSet<WWCPSshClient> lettingGo = [];

        /// <summary>
        /// How long connecting over SSH may take - the TCP connection, the
        /// version exchange and the key exchange - before an attempt fails.
        /// </summary>
        public static readonly TimeSpan SshConnectTimeout  = TimeSpan.FromSeconds(15);

        /// <summary>
        /// How often a connection over SSH asks its server whether it is still
        /// there, so that a server gone without a word is noticed - and come
        /// back to - rather than waited for.
        /// </summary>
        public static readonly TimeSpan SshKeepAliveEvery  = TimeSpan.FromSeconds(30);

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

                if (Connection.IsSSH)
                {
                    await DialOverSSH(Connection, where, CancellationToken);
                    return;
                }

                if (!WhatItProvesItselfWith(Connection, out var basic, out var totp, out var certificates, out _, out var wrong))
                {
                    Fail(Connection, ConnectionStatus.NotDialled, $"Not dialled: {where} {wrong}.");
                    return;
                }

                Log.Info($"Dialling {where} as {ConnectionEntry.AsText(Connection.OCPPVersion)} " +
                         $"{Connection.ConnectionType}.", "ocpp", "connections");

                // To the CSMS, whatever its type says: the type is a label. A
                // node sends what it has to say - a security event, an event
                // of a monitor - to its CSMS by name, "CentralSystem" in 1.6
                // and "CSMS" in 2.1, and answers a request by the name of the
                // connection it came over. A connection without that name was
                // neither: the CSMS's requests went unanswered, and every
                // event waited in the queue for a CSMS the node did not know
                // how to reach.
                var response = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6

                                   ? await cs01.ConnectOCPPWebSocketClient(
                                             RemoteURL:                Connection.URL,
                                             NextHopNetworkingNodeId:  NetworkingNode_Id.CentralSystem,
                                             HTTPAuthentication:       basic,
                                             ClientCertificates:       certificates,
                                             TOTPConfig:               totp,
                                             DNSClient:                DNSClient,
                                             CancellationToken:        CancellationToken
                                         )

                                   : await cs02.ConnectOCPPWebSocketClient(
                                             RemoteURL:                Connection.URL,
                                             NextHopNetworkingNodeId:  NetworkingNode_Id.CSMS,
                                             HTTPAuthentication:       basic,
                                             ClientCertificates:       certificates,
                                             TOTPConfig:               totp,
                                             DNSClient:                DNSClient,
                                             CancellationToken:        CancellationToken
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

                ConnectionIsUp(Connection);

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

        #region (private) NodeOf(Version)

        /// <summary>
        /// The OCPP node that speaks the given version.
        /// </summary>
        private OCPPCommon.NetworkingNode.AOCPPNetworkingNode NodeOf(OCPP.OCPPVersion Version)

            => Version == OCPP.OCPPVersion.OCPP1_6
                   ? cs01
                   : cs02;

        #endregion

        #region (private) SshLoginOf(Connection, Key, out Login, out Trust, out Says, out Error)

        /// <summary>
        /// Who an ssh:// connection signs in as, with what, and which host key
        /// it believes.
        /// </summary>
        /// <remarks>
        /// One place, for the same reason as WhatItProvesItselfWith: dialling
        /// and testing a connection have to agree about what it shows.
        ///
        /// Always this station's own key. A password on top where the
        /// connection names credentials - HTTP Basic ones, whose login and
        /// password are what an SSH server asks for - and only sent where the
        /// server asks for one, after it proved itself with its host key. Who
        /// signs in is the URL's login, or the credentials', or else the name
        /// of the OCPP node that dials.
        /// </remarks>
        private Boolean SshLoginOf(ConnectionEntry                            Connection,
                                   ISshHostKey                                Key,
                                   [NotNullWhen(true)]  out SshClientOptions? Login,
                                   [NotNullWhen(true)]  out SshHostKeyTrust?  Trust,
                                   out String                                 Says,
                                   [NotNullWhen(false)] out String?           Error)
        {

            Login  = null;
            Trust  = null;
            Says   = "";
            Error  = null;

            if (Connection.CertificateId is not null)
            {
                Error = "it names a TLS client certificate, and an ssh:// connection makes no TLS handshake to show one in";
                return false;
            }

            String? password       = null;
            String? passwordLogin  = null;
            String? passwordOf     = null;

            if (Connection.AuthenticationId is not null)
            {

                var credentials = Connections.Authentications.
                                      FirstOrDefault(entry => entry.Id == Connection.AuthenticationId);

                if (credentials is null)
                {
                    Error = "it names credentials that are not configured here";
                    return false;
                }

                if (credentials.Kind == AuthenticationKind.TOTP)
                {
                    Error = $"it uses '{credentials.Description}', a TOTP password, which is answered over HTTP and not over SSH";
                    return false;
                }

                if (String.IsNullOrEmpty(credentials.Password))
                {
                    Error = $"it uses '{credentials.Description}', which has no secret set";
                    return false;
                }

                password       = credentials.Password;
                passwordLogin  = credentials.Login;
                passwordOf     = credentials.Description;

            }

            var urlLogin = Connection.URL.Login;

            if (urlLogin is not null && passwordLogin is not null && urlLogin != passwordLogin)
            {
                Error = $"the URL signs in as '{urlLogin}' and '{passwordOf}' as '{passwordLogin}', and it is one login";
                return false;
            }

            var username  = urlLogin ?? passwordLogin ?? NodeOf(Connection.OCPPVersion).Id.ToString();

            Trust  = new SshHostKeyTrust(
                         Connection.SshHostKey,
                         Connection.URL.Host.ToString(),
                         Connection.URL.Port ?? IPPort.Parse(22)
                     );

            // The station's DNS client resolves the server's name, as it
            // resolves a WebSocket's: never the operating system's resolver.
            Login  = new SshClientOptions {
                         Username           = username,
                         DNSClient          = DNSClient,
                         VerifyHostKey      = Trust.Verify,
                         Credentials        = [ Key ],
                         Password           = password,
                         ConnectTimeout     = SshConnectTimeout,
                         KeepAliveInterval  = SshKeepAliveEvery
                     };

            Says   = $"this station's SSH key ({SshFingerprint.Sha256(Key.PublicKeyBlob)}) as '{username}'" +
                     (passwordOf is not null
                          ? $", and the password of '{passwordOf}' where the server asks for one"
                          : "");

            return true;

        }

        #endregion

        #region (private) DialOverSSH(Connection, Where, CancellationToken)

        /// <summary>
        /// One ssh:// connection: who it signs in as, and whether it got
        /// through.
        /// </summary>
        /// <remarks>
        /// What is final and what is tried again, as over a WebSocket: a server
        /// not there yet is tried again by itself, and one that answered no is
        /// not - a host key that is not the one believed, a login it did not
        /// take, the OCPP subsystem refused. Trying again would not change the
        /// answer, only fill the log with it.
        /// </remarks>
        private async Task DialOverSSH(ConnectionEntry    Connection,
                                       String             Where,
                                       CancellationToken  CancellationToken)
        {

            var node  = NodeOf(Connection.OCPPVersion);
            var key   = await SshKey.MakeIfNeeded(CancellationToken);

            if (!SshLoginOf(Connection, key, out var login, out var trust, out var says, out var wrong))
            {
                Fail(Connection, ConnectionStatus.NotDialled, $"Not dialled: {Where} {wrong}.");
                return;
            }

            // Where it went when it was dialled: a first host key is written
            // down for this one, and not for wherever it was changed to since.
            var dialledURL = Connection.URL;

            Log.Info($"Dialling {Where} as {ConnectionEntry.AsText(Connection.OCPPVersion)} {Connection.ConnectionType} over SSH, " +
                     $"signing in with {says}; " +
                     (trust.Pinned is not null
                          ? $"its server must show the host key {trust.Pinned}."
                          : "no host key is pinned, so the first one its server shows is believed, and written down."),
                     "ocpp", "connections");

            // To the CSMS, as over a WebSocket - see Dial.
            var result  = await node.ConnectOCPPClient(
                                    RemoteURL:                Connection.URL,
                                    NextHopNetworkingNodeId:  Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6
                                                                  ? NetworkingNode_Id.CentralSystem
                                                                  : NetworkingNode_Id.CSMS,
                                    Ssh:                      login,
                                    DNSClient:                DNSClient,
                                    CancellationToken:        CancellationToken
                                );

            var client  = result.Client as WWCPSshClient;

            if (!result.IsConnected)
            {

                var final        = trust.Refused || IsFinal(result.Error);
                var keepsTrying  = !final && client is not null && node.ReconnectPolicy is not null;

                if (final && client is not null)
                    await LetGo(client, "Not tried again.");

                Fail(Connection,
                     keepsTrying ? ConnectionStatus.Trying
                                 : final ? ConnectionStatus.Refused
                                         : ConnectionStatus.Failed,
                     WhyNotOverSSH(Where, trust, result.Error) +
                     (keepsTrying
                          ? "; it is tried again by itself, and said here when it gets through."
                          : final
                                ? "; that answer is final, and it is not tried again."
                                : "; it is not tried again."));

                if (keepsTrying)
                    FollowOverSSH(Connection, dialledURL, client!, trust, Connected: false);

                return;

            }

            PinIfFirst(Connection, dialledURL, trust);

            if (client is not null)
                FollowOverSSH(Connection, dialledURL, client, trust, Connected: true);

            else
                Log.Warning($"'{Connection.Description}' connected, but the client that did it could not be found again, " +
                             "so nothing could be said about what becomes of it.", "ocpp", "connections");

            Note(Connection, "Connected, and will come back by itself if it drops.");

            ConnectionIsUp(Connection);

        }

        #endregion

        #region (private static) IsFinal(Error) / WhyNotOverSSH(Where, Trust, Error) / ReasonOf(Error)

        /// <summary>
        /// Whether an answer of the server is one that trying again would not
        /// change: a login it did not take, or the OCPP subsystem refused.
        /// </summary>
        private static Boolean IsFinal(Exception? Error)

            => Error?.GetBaseException() is SshAuthenticationException or SshMessageRefusedException ||
               Error is SshAuthenticationException or SshMessageRefusedException;

        /// <summary>
        /// The sentence that says why an ssh:// connection did not get through.
        /// </summary>
        private static String WhyNotOverSSH(String Where, SshHostKeyTrust Trust, Exception? Error)
        {

            if (Trust.Refused)
                return $"{Where} was not believed: {Trust.WhyRefused()}";

            var error = Error is SshAuthenticationException or SshMessageRefusedException
                            ? Error
                            : Error?.GetBaseException();

            return error switch {
                       SshAuthenticationException  => $"{Where} did not let this station in: {error.Message} " +
                                                       "Its authorized_keys needs this station's SSH key, which the Connections page shows",
                       SshMessageRefusedException  => $"{Where} let this station in, and refused it the OCPP subsystem: {error.Message}",
                       null                        => $"{Where} could not be reached over SSH",
                       _                           => $"{Where} could not be reached over SSH: {ReasonOf(error)}"
                   };

        }

        /// <summary>
        /// What went wrong, in words that are the same on every station: the
        /// name of a socket error rather than the operating system's own words
        /// for it, which are translated.
        /// </summary>
        private static String ReasonOf(Exception Error)

            => Error is SocketException socket
                   ? socket.SocketErrorCode.ToString()
                   : Error.Message;

        #endregion

        #region (private) FollowOverSSH(Connection, DialledURL, Client, Trust, Connected)

        /// <summary>
        /// Say what becomes of a connection dialled over SSH from here on - as
        /// Follow does for one over a WebSocket.
        /// </summary>
        /// <remarks>
        /// The client comes back by itself, after a drop and until it gets
        /// through at all. What it is told by the server that is final - a host
        /// key not believed, a login not taken - ends that: it is closed, and
        /// said so - before the first connection as after a drop, since the
        /// client says of every attempt of its own that failed. Nothing of it
        /// is said once this station let go of it.
        /// </remarks>
        private void FollowOverSSH(ConnectionEntry  Connection,
                                   URL              DialledURL,
                                   WWCPSshClient    Client,
                                   SshHostKeyTrust  Trust,
                                   Boolean          Connected)
        {

            var connectedBefore = Connected;

            Client.OnConnected        += (timestamp, transport, connection, networkingNodeId, networkingMode, eventTrackingId, cancellationToken) => {

                if (IsLetGo(Client))
                    return Task.CompletedTask;

                PinIfFirst(Connection, DialledURL, Trust);

                Note(Connection, connectedBefore
                                     ? "Connected again, and will come back by itself if it drops."
                                     : "Connected, and will come back by itself if it drops.");

                connectedBefore = true;

                ConnectionIsUp(Connection);

                return Task.CompletedTask;

            };

            Client.OnConnectionClosed += (timestamp, transport, connection, networkingNodeId, close, cancellationToken) => {

                if (!IsLetGo(Client))
                    Fail(Connection, ConnectionStatus.Lost,
                         $"'{Connection.Description}' was lost ({close.Code?.ToString() ?? "without a word"}" +
                         $"{(String.IsNullOrWhiteSpace(close.Reason) ? "" : $": {close.Reason}")}); it comes back by itself.");

                return Task.CompletedTask;

            };

            // Asked before every attempt, so this is where a server that shows
            // another host key than before is noticed, whichever attempt it is.
            Trust.OnRefused           += why => {

                if (IsLetGo(Client))
                    return;

                Fail(Connection, ConnectionStatus.Refused,
                     $"'{Connection.Description}' was not believed when it was tried again: {why}; " +
                      "that answer is final, and it is not tried again.");

                _ = Task.Run(() => LetGo(Client, "Not believed."));

            };

            // After every attempt the client made by itself that failed: before
            // the first connection, and to come back after a drop.
            Client.OnConnectFailed    += (timestamp, client, error, attempt, nextAttemptIn, cancellationToken) => {

                if (IsLetGo(Client) || Trust.Refused)
                    return Task.CompletedTask;

                if (IsFinal(error))
                {

                    Fail(Connection, ConnectionStatus.Refused,
                         WhyNotOverSSH($"'{Connection.Description}'", Trust, error) +
                         " when it was tried again; that answer is final, and it is not tried again.");

                    _ = Task.Run(() => LetGo(Client, "Not tried again."));

                }

                else if (nextAttemptIn is TimeSpan delay)
                {
                    Log.Debug($"'{Connection.Description}': trying again in {delay.TotalSeconds:F1} s (attempt {attempt + 1}).", "ocpp", "connections");
                    NextAttempt(Connection, attempt + 1, delay);
                }

                else
                    Fail(Connection, ConnectionStatus.Failed,
                         $"{WhyNotOverSSH($"'{Connection.Description}'", Trust, error)}; it was tried {attempt} time(s), and is not tried again.");

                return Task.CompletedTask;

            };

        }

        #endregion

        #region (private) PinIfFirst(Connection, DialledURL, Trust)

        /// <summary>
        /// Write down the host key the first connection of one that pinned none
        /// was shown.
        /// </summary>
        private void PinIfFirst(ConnectionEntry  Connection,
                                URL              DialledURL,
                                SshHostKeyTrust  Trust)
        {

            if (Trust.Pinned is not null || Trust.Shown is not String shown)
                return;

            if (!Connections.TryPinHostKey(Connection.Id, DialledURL, shown, out var notWritten) && notWritten is not null)
                Log.Warning($"'{Connection.Description}' was shown the host key {shown} on its first connection, and it could not be " +
                            $"written down: {notWritten}. It is the only one believed until this station starts again.",
                            "ocpp", "connections", "ssh", "security");

        }

        #endregion

        #region (private) LetGo(Client, Reason) / IsLetGo(Client)

        /// <summary>
        /// Close an SSH client this station is done with, and say nothing more
        /// of what it says from here on.
        /// </summary>
        private async Task LetGo(WWCPSshClient Client, String Reason)
        {

            lock (lettingGo)
                lettingGo.Add(Client);

            try
            {
                await Client.Close(MessageCloseCode.NormalClosure, Reason);
            }
            catch (Exception e)
            {
                Log.Debug($"A connection to {Client.TransportDescription} did not close cleanly: {e.Message}", "ocpp", "connections");
            }

        }

        /// <summary>
        /// Whether this station let go of the given SSH client.
        /// </summary>
        private Boolean IsLetGo(WWCPSshClient Client)
        {
            lock (lettingGo)
                return lettingGo.Contains(Client);
        }

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

                ConnectionIsUp(Connection);

                return Task.CompletedTask;

            };

        }

        #endregion

        #region (private) SendWhatWaits(Connection)

        /// <summary>
        /// Send what waits in the queue of the connection's version of OCPP,
        /// now that the CSMS can be reached and has accepted this station: what
        /// was said before is not left until the next thing there is to say.
        /// </summary>
        /// <remarks>
        /// Not waited for. A CSMS slow to answer holds up the queue and not the
        /// connection, and what it does not take stays in the queue.
        /// </remarks>
        private void SendWhatWaits(ConnectionEntry Connection)
        {

            var sending = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6
                              ? cs01.FlushOfflineQueueAsync()
                              : cs02.FlushOfflineQueueAsync();

            _ = sending.ContinueWith(task => Log.Warning($"Sending what waited for '{Connection.Description}' failed: {task.Exception?.GetBaseException().Message}", "ocpp", "connections"),
                                     TaskContinuationOptions.OnlyOnFaulted);

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
        /// <param name="SshHostKey">The host key the server of an ssh:// connection must show; none to be told which one it shows.</param>
        public async Task<JObject> TestConnection(String?            Description,
                                                  String?            URL,
                                                  String?            ConnectionType,
                                                  String?            OCPPVersion           = null,
                                                  Boolean            AutoConnect           = false,
                                                  String?            AuthenticationId      = null,
                                                  String?            CertificateId         = null,
                                                  CancellationToken  CancellationToken     = default,
                                                  String?            SshHostKey            = null)
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
                                          out var url, out var type, out var version, out var notYet) ||
                !ConnectionEntry.ValidateHostKey(url, SshHostKey, out var hostKey, out notYet))
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
                                 CertificateId     = ConnectionEntry.Named(CertificateId),
                                 SshHostKey        = hostKey
                             };

            #endregion

            Log.Info($"Testing the connection to '{connection.Description}' ({connection.URL}) ...", "ocpp", "connections", "test");

            #region What is going to be tried

            Step("info", $"{ConnectionEntry.AsText(connection.OCPPVersion)} to {connection.URL}, " +
                         $"written down as {connection.ConnectionType}.");

            IHTTPAuthentication?  basic         = null;
            TOTPConfig?           totp          = null;
            X509Certificate2[]?   certificates  = null;
            SshClientOptions?     sshLogin      = null;
            SshHostKeyTrust?      sshTrust      = null;

            if (connection.IsSSH)
            {

                Step("info", $"The URL is an ssh:// one: OCPP goes over the SSH subsystem {OCPPCommon.SSH.OCPPSshServer.OCPPSubsystem}, " +
                              "encrypted, and its server is known by its host key.");

                // Made here where there is none yet: a test of an ssh://
                // connection is a test of signing in with it.
                var key = await SshKey.MakeIfNeeded(CancellationToken);

                if (!SshLoginOf(connection, key, out sshLogin, out sshTrust, out var signsIn, out var notSSH))
                {
                    Step("error", $"Not tried: {notSSH}.");
                    Log.Warning($"The connection test for '{connection.Description}' was not run: {notSSH}.", "ocpp", "connections", "test");
                    return Done(connection, false);
                }

                Step("info", $"It will sign in with {signsIn}. The server lets it in where its authorized_keys has this line: " +
                             $"{SshKey.PublicKeyLine}");

                Step(sshTrust.Pinned is not null ? "info" : "warning",
                     sshTrust.Pinned is not null
                         ? $"Its server must show the host key {sshTrust.Pinned}."
                         : "No host key is pinned. Whatever key the server shows is said below and believed for this test; " +
                           "a test writes nothing down, so the first connection this station makes pins it.");

            }
            else
            {

                Step(connection.IsSecure ? "info" : "warning",
                     connection.IsSecure
                         ? "The URL is a TLS one, so the certificate of whatever answers will be checked against this station's trust store."
                         : "The URL is not a TLS one. Nothing on this connection is encrypted or authenticated at the transport layer.");

                if (!WhatItProvesItselfWith(connection, out basic, out totp, out certificates, out var proves, out var wrong))
                {
                    Step("error", $"Not tried: {wrong}.");
                    Log.Warning($"The connection test for '{connection.Description}' was not run: {wrong}.", "ocpp", "connections", "test");
                    return Done(connection, false);
                }

                Step("info", $"It will prove itself with {proves}.");

            }

            if (!connection.AutoConnect)
                Step("info", "This is not one the station connects to by itself. A test that gets through does not " +
                             "change that - it only says that it could.");

            #endregion

            #region Does the name resolve

            var host = connection.URL.Host.ToString();

            // With this station's DNS client, as everything the station dials
            // is: what it finds is what the probe below connects to. A name it
            // finds nothing for ends the test here - the connection itself
            // would not get further, and no other resolver is asked. The
            // socket used to be handed the name, and the operating system
            // answered for a station whose name servers did not.
            System.Net.IPAddress[] addresses;

            try
            {

                addresses = [.. await NameResolution.ResolveAsync(host, DNSClient, CancellationToken)];

                if (!System.Net.IPAddress.TryParse(host.Trim('[', ']'), out _))
                    Step("info", $"'{host}' resolves to {String.Join(", ", addresses.Take(8).Select(address => address.ToString()))}.");

            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {

                Step("error", $"'{host}' could not be looked up with this station's name servers: {e.Message} " +
                              "Nothing else is asked, so the rest was not tried.");

                Log.Warning($"The connection test for '{connection.Description}' could not look up '{host}': {e.Message}",
                            "ocpp", "connections", "test");

                return Done(connection, false);

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
            var port = connection.URL.Port?.ToUInt16() ?? (connection.IsSSH    ? (UInt16)  22
                                                         : connection.IsSecure ? (UInt16) 443
                                                                               : (UInt16)  80);

            try
            {

                using var probe = new TcpClient();

                await probe.ConnectAsync(addresses, port, CancellationToken);

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
                              $"There is nothing to speak {(connection.IsSSH ? "SSH" : "WebSocket")} to, so the rest was not tried.");

                Log.Warning($"The connection test for '{connection.Description}' found nothing at {host}:{port}: {why}.",
                            "ocpp", "connections", "test");

                return Done(connection, false);

            }

            #endregion

            if (connection.IsSSH)
                return await TestOverSSH(connection, sshLogin!, sshTrust!, Step, Done, CancellationToken);

            WebSocketClient? client = null;

            try
            {

                #region Open it

                client = new WebSocketClient(
                             connection.URL,
                             DNSClient:              DNSClient,
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

        #region (private) TestOverSSH(Connection, Login, Trust, Step, Done, CancellationToken)

        /// <summary>
        /// The rest of a connection test over SSH, once a TCP connection could
        /// be made: sign in, ask for the OCPP subsystem, hold it briefly, and
        /// let go.
        /// </summary>
        /// <remarks>
        /// Its own client, as over a WebSocket, and not one of the OCPP nodes':
        /// nothing it is told reaches them, and nothing of it is left behind.
        /// </remarks>
        private async Task<JObject> TestOverSSH(ConnectionEntry                      Connection,
                                                SshClientOptions                     Login,
                                                SshHostKeyTrust                      Trust,
                                                Action<String, String>               Step,
                                                Func<ConnectionEntry?, Boolean, JObject>  Done,
                                                CancellationToken                    CancellationToken)
        {

            var subprotocols  = SubProtocolsOf(Connection.OCPPVersion);

            var client        = new WWCPSshClient(
                                    NodeOf(Connection.OCPPVersion),
                                    WWCPSshClient.OptionsFrom(Connection.URL, Login, OCPPCommon.SSH.OCPPSshServer.OCPPSubsystem, subprotocols),
                                    ConnectAs: WWCPSshClient.ConnectAsFrom(Connection.URL)
                                );

            client.MessageClient.OnTextMessageReceived   += (timestamp, sender, connection, text, cancellationToken) => {
                Step("info", $"Received: {(text.Length > 400 ? text[..400] + " ..." : text)}");
                return Task.CompletedTask;
            };

            client.MessageClient.OnBinaryMessageReceived += (timestamp, sender, connection, bytes, cancellationToken) => {
                Step("info", $"Received {bytes.Length} byte(s) of binary.");
                return Task.CompletedTask;
            };

            client.OnConnectionClosed                    += (timestamp, transport, connection, networkingNodeId, close, cancellationToken) => {
                if (close.ByPeer)
                    Step("info", $"The other end closed the connection: {close.Code?.ToString() ?? "without a word"}" +
                                 $"{(String.IsNullOrWhiteSpace(close.Reason) ? "" : $" ({close.Reason})")}.");
                return Task.CompletedTask;
            };

            String ShownKey()
                => $"{Trust.ShownKind} {Trust.Shown}";

            try
            {

                Step("info", $"Connecting as '{Login.Username}', asking for {OCPPCommon.SSH.OCPPSshServer.OCPPSubsystem} " +
                             $"and offering {String.Join(", ", subprotocols)} ...");

                var connection = await client.Connect(CancellationToken);

                Step("info", Trust.Pinned is not null
                                 ? $"The server showed the host key {ShownKey()}, the one pinned."
                                 : $"The server showed the host key {ShownKey()}. None is pinned, so it was believed. Pin it to decide yourself; " +
                                    "otherwise the first connection this station makes writes it down.");

                Step("notice", connection.Subprotocol is String agreedOn && agreedOn.Length > 0
                                   ? $"Connected. The server let '{Login.Username}' in and took {agreedOn}."
                                   : $"Connected, and the server let '{Login.Username}' in, but it named no OCPP version. Whatever is " +
                                      "there took the connection without agreeing to speak OCPP.");

                Step("info", $"Staying connected for {TestHoldsFor.TotalSeconds:0.#} second(s), to see whether anything is said.");

                await Task.Delay(TestHoldsFor, CancellationToken);

                Step("info", "Closing.");

                await client.Close(MessageCloseCode.NormalClosure, "Connection test finished");

                Step("notice", "Closed. The connection can be made.");

                Log.Notice($"The connection test for '{Connection.Description}' got through.", "ocpp", "connections", "test");

                return Done(Connection, true);

            }
            catch (Exception e)
            {

                if (Trust.Refused)
                    Step("error", $"Not believed: {Trust.WhyRefused()}. Nothing was signed in.");

                else
                {

                    if (Trust.Shown is not null)
                        Step("info", $"The server showed the host key {ShownKey()}{(Trust.Pinned is not null ? ", the one pinned" : "")}.");

                    var error = e is SshAuthenticationException or SshMessageRefusedException ? e : e.GetBaseException();

                    Step("error", error switch {
                                      SshAuthenticationException  => $"The server did not let '{Login.Username}' in: {error.Message} " +
                                                                      $"Its authorized_keys needs this station's key: {SshKey.PublicKeyLine}",
                                      SshMessageRefusedException  => $"The server let '{Login.Username}' in, and refused the OCPP subsystem: {error.Message}",
                                      _                           => $"{error.GetType().Name}: {ReasonOf(error)}"
                                  });

                }

                Log.Warning($"The connection test for '{Connection.Description}' failed: " +
                            $"{(Trust.Refused ? Trust.WhyRefused() : ReasonOf(e.GetBaseException()))}", "ocpp", "connections", "test");

                return Done(Connection, false);

            }
            finally
            {
                // Whatever happened, nothing of this test is left connected.
                try
                {
                    await client.DisposeAsync();
                }
                catch
                { }
            }

        }

        #endregion

        #region (private) DialAgain(Old16, Old21, Changed)

        /// <summary>
        /// After a change of the EVSEs, or of what this station says it is:
        /// hang up what the OCPP nodes it replaced were connected to, stop
        /// them, and dial again through the new ones what this station dials
        /// at its start.
        /// </summary>
        /// <remarks>
        /// A connection belongs to the node that made it, and nothing moves it
        /// to another. Left where it was, the old node went on answering the
        /// CSMS with the EVSEs as they had been, and nothing this station said
        /// afterwards reached the CSMS: it went through the new nodes, which
        /// had no connection. So it is hung up and dialled again, and the CSMS
        /// sees the connection close and come back - and the new nodes'
        /// BootNotification, which says what this station is now.
        ///
        /// Dialled again only where this station is started and the old nodes
        /// had dialled something. One at a time, so that a second change does
        /// not hang up what the first is still dialling, but after it: the
        /// first one's dialling goes through the nodes the second one replaces.
        /// </remarks>
        /// <param name="Old16">The OCPP 1.6 node replaced.</param>
        /// <param name="Old21">The OCPP 2.1 node replaced.</param>
        /// <param name="Changed">What changed, as the start of a sentence: "The EVSEs of this charging station".</param>
        private async Task DialAgain(OCPPv1_6.   TestChargePointNode      Old16,
                                     OCPPv2_1.CS.TestChargingStationNode  Old21,
                                     String                               Changed)
        {

            // Nothing more is asked of a CSMS for nodes let go of.
            EndRegistrations(Old16, Old21);

            await dialAgainLock.WaitAsync();

            try
            {

                var clients     = Old16.OCPPWebSocketClients.OfType<WebSocketClient>().
                                      Concat(Old21.OCPPWebSocketClients.OfType<WebSocketClient>()).
                                      ToArray();

                var sshClients  = Old16.WWCPSshClients.
                                      Concat(Old21.WWCPSshClients).
                                      ToArray();

                // Before the nodes stop, which closes them too - as a loss.
                foreach (var sshClient in sshClients)
                    await LetGo(sshClient, $"{Changed} changed; it connects again.");

                // As HangUp does: without its policy first, so that the close
                // is neither a loss nor something to come back from.
                foreach (var client in clients)
                {
                    try
                    {
                        client.ReconnectPolicy = null;
                        await client.Close(WebSocketFrame.ClosingStatusCode.NormalClosure,
                                           $"{Changed} changed; it connects again.");
                    }
                    catch (Exception e)
                    {
                        Log.Debug($"A connection to {client.RemoteURL} did not close cleanly: {e.Message}", "ocpp", "connections");
                    }
                }

                foreach (var node in new AWWCPNetworkingNode[] { Old16, Old21 })
                {
                    try
                    {
                        await node.Stop(Message: $"{Changed} changed.");
                    }
                    catch (Exception e)
                    {
                        Log.Warning($"Stopping the OCPP node '{node.Id}' that was replaced failed: {e.Message}", "ocpp");
                    }
                }

                if (started && clients.Length + sshClients.Length > 0)
                {

                    Log.Notice($"{Changed} changed, so what the OCPP nodes it replaced were connected to was hung up " +
                               $"({clients.Length + sshClients.Length} connection(s)); it is dialled again.", "ocpp", "connections");

                    await DialConfiguredConnections();

                }

            }
            finally
            {
                dialAgainLock.Release();
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

            // Nothing more is asked of a CSMS, nor waited for.
            EndRegistrations(cs01, cs02);

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

            foreach (var sshClient in cs01.WWCPSshClients.Concat(cs02.WWCPSshClients))
                await LetGo(sshClient, "This station hangs up.");

            lock (dialled)
                dialled.Clear();

        }

        #endregion

    }

}
