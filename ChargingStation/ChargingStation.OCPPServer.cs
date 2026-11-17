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

using System.Net.Security;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.TCP;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.OCPP.WebSockets;
using cloud.charging.open.protocols.OCPP.Node.Connections;

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The OCPP server of this station: a WebSocket server a CSMS or a local
    /// controller dials, where the station cannot dial them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// OCPP as it always is - this station is the charging station, what
    /// dials in is its back end - and only who opens the WebSocket is the
    /// other way round. Who may come in is written down on the Connections
    /// page, as a connection the other end makes: under the name its URL ends
    /// with, with the password or the one-time password its credentials
    /// say. Nothing else gets in, and what is in is one of this station's
    /// connections from then on: the oldest of them that is switched on is
    /// the one in charge, whoever opened it. See
    /// OCPPDiallingNode.Accepting.cs.
    /// </para>
    /// <para>
    /// One server for each of the two nodes, on a port of its own: OCPP 2.1
    /// and 2.0.1 on one, OCPP 1.6 on the other. With the certificate of the
    /// web interface where that speaks HTTPS, and without TLS where it does
    /// not - which the log and the page say, since a password then crosses
    /// the network in the clear.
    /// </para>
    /// <para>
    /// A charging station forwards nothing. A request that comes in over a
    /// connection somebody else made is for this station, and one addressed
    /// to anything else is dropped, or refused where it wants an answer -
    /// what a local controller does with the requests of the stations below
    /// it is not this station's to do.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What the log calls it.
        /// </summary>
        public const String  OCPPServerLabel  = "OCPP server";

        /// <summary>
        /// How many sign-ins that were wrong one address is given within a
        /// minute before the OCPP server tells it to wait.
        /// </summary>
        public const UInt32  MaxFailedOCPPSignInsPerMinute  = 10;

        private OCPPWebSocketServer?  ocppServer21;
        private OCPPWebSocketServer?  ocppServer16;

        /// <summary>
        /// Why a version that has a port is not served, by version.
        /// </summary>
        private readonly Dictionary<OCPPVersion, String>  ocppServerProblems  = [];

        /// <summary>
        /// When each address signed in wrong at the OCPP server within the
        /// last minute.
        /// </summary>
        private readonly ConcurrentDictionary<String, Queue<DateTimeOffset>>  failedOCPPSignIns  = new ();

        #endregion

        #region Properties

        /// <summary>
        /// What the configuration file says about the OCPP server.
        /// </summary>
        public OCPPServerConfiguration  OCPPServer    { get; private set; } = new ();

        /// <summary>
        /// Whether the OCPP server speaks TLS: where the web interface does,
        /// with its certificate.
        /// </summary>
        public Boolean                  OCPPServerTLS
            => WebCertificate is not null;

        /// <summary>
        /// Why the OCPP server takes no WebSockets over HTTP/3 although the
        /// file says it shall, where that is known before it is tried.
        /// </summary>
        public const String             NoHTTP3WithoutTLS  = "HTTP/3 is QUIC, which is TLS always: without HTTPS this station has no certificate to serve it with.";

        #endregion


        #region (private) ServeOCPP() / StopServingOCPP(Why)

        /// <summary>
        /// Start the OCPP servers the file names a port for, each on the node
        /// of its version, and let in what is written down.
        /// </summary>
        /// <remarks>
        /// A port that is taken is said and does not stop the station: it
        /// dials what it dials all the same.
        /// </remarks>
        private void ServeOCPP()
        {

            lock (ocppServerProblems)
                ocppServerProblems.Clear();

            if (!OCPPServer.IsEnabled)
                return;

            if (OCPPServer.Port is IPPort port && ocppServer21 is null)
                ocppServer21 = StartedOCPPServer(OCPPVersion.OCPP2_1, port);

            if (OCPPServer.Port16 is IPPort port16 && ocppServer16 is null)
                ocppServer16 = StartedOCPPServer(OCPPVersion.OCPP1_6, port16);

            if ((ocppServer21 is not null || ocppServer16 is not null) && !OCPPServerTLS)
                Log.Warning(
                    $"The {OCPPServerLabel} of this charging station speaks no TLS, as its web interface speaks none: a password that " +
                    "signs in to it crosses the network in the clear. Switch HTTPS on under Configuration.",
                    "ocpp", "server"
                );

        }

        /// <summary>
        /// One server, attached to the node of its version, listening and
        /// letting in what is written down - or null, and why in the log.
        /// </summary>
        private OCPPWebSocketServer? StartedOCPPServer(OCPPVersion  Version,
                                                       IPPort       Port)
        {

            OCPPWebSocketServer? server = null;

            try
            {

                var certificate = WebCertificate;

                // Who comes in is decided at the door of this station, see
                // LetInOrNot: a client certificate and a password are two ways
                // in, and the check built into the server knows a certificate
                // let in by its key pin as nobody's. Over TLS a client
                // certificate is asked for, and need not be shown: who signs
                // in with a password shows none.
                RemoteTLSClientCertificateValidationHandler<ITCPServer>? clientCertificates
                    = certificate is not null
                          ? (sender, shown, chain, tcpServer, policyErrors) => JudgeOCPPClientCertificate(Version, shown, chain, policyErrors)
                          : null;

                server = Version == OCPPVersion.OCPP1_6

                             ? cs01.AttachWebSocketServer(
                                   HTTPServiceName:             $"{Kind.Product} OCPP 1.6",
                                   IPAddress:                   ListenAddress,
                                   TCPPort:                     Port,
                                   RequireAuthentication:       false,
                                   ServerCertificateSelector:   certificate is not null
                                                                    ? (tcpServer, tcpClient) => certificate.ChainFor(tcpServer, tcpClient).Certificate
                                                                    : null,
                                   ClientCertificateValidator:  clientCertificates,
                                   ClientCertificateRequired:   false,
                                   AutoStart:                   false
                               )

                             : cs02.AttachWebSocketServer(
                                   HTTPServiceName:             $"{Kind.Product} OCPP 2.1",
                                   IPAddress:                   ListenAddress,
                                   TCPPort:                     Port,
                                   RequireAuthentication:       false,
                                   ServerCertificateSelector:   certificate is not null
                                                                    ? (tcpServer, tcpClient) => certificate.ChainFor(tcpServer, tcpClient).Certificate
                                                                    : null,
                                   ClientCertificateValidator:  clientCertificates,
                                   ClientCertificateRequired:   false,
                                   AutoStart:                   false
                               );

                var served = server;

                // A certificate let in by its key pin names nobody: whose key
                // it is, is asked at the door, by the name it comes under.
                served.AcceptedByKeyPin               = shown => IsInboundKeyPinned(Version, shown);

                served.OnValidateWebSocketConnection += (timestamp, endpoint, connection, eventTrackingId, cancellationToken)
                                                            => LetInOrNot(Version, served, connection);

                // A TCP port and nothing else, unless the file asks for it:
                // HTTP/3 opens a UDP port of the same number, which nobody is
                // given who did not ask. And never without a certificate.
                server.AcceptHTTP3 = OCPPServer.TakesHTTP3 && certificate is not null;

                // A password is guessed at from wherever this port can be
                server.Start();

                ServeInbound(Version, server);

                Log.Notice(
                    $"A CSMS or a local controller may dial this charging station: {ConnectionEntry.AsText(Version)} on " +
                    $"{(OCPPServerTLS ? "wss" : "ws")}://{ListenAddress}:{Port}/<its name>, as a connection the other end makes that is written down " +
                    "under Connections" +
                    (server.AcceptHTTP3 ? $" - over HTTP/3 as well, on UDP port {Port}." : "."),
                    "ocpp", "server"
                );

                if (OCPPServer.TakesHTTP3 && !server.AcceptHTTP3)
                    Log.Warning(
                        $"The {OCPPServerLabel} for {ConnectionEntry.AsText(Version)} takes no WebSockets over HTTP/3, although the configuration " +
                        $"says it shall: {NoHTTP3WithoutTLS}",
                        "ocpp", "server"
                    );

                return server;

            }
            catch (Exception e)
            {

                var why = e.GetBaseException().Message;

                lock (ocppServerProblems)
                    ocppServerProblems[Version] = why;

                Log.Critical($"The {OCPPServerLabel} for {ConnectionEntry.AsText(Version)} could not be started on port {Port}: {why}", "ocpp", "server");

                if (server is not null)
                    _ = StoppedOCPPServer(server);

                return null;

            }

        }

        /// <summary>
        /// Serve OCPP no longer: whoever dialled in is let go of, and the
        /// ports are free again.
        /// </summary>
        /// <param name="Why">Why, for the log and for the other end.</param>
        private async Task StopServingOCPP(String Why)
        {

            var (server21, server16) = (ocppServer21, ocppServer16);

            ocppServer21  = null;
            ocppServer16  = null;

            if (server21 is null && server16 is null)
                return;

            StopServingInbound(OCPPVersion.OCPP2_1, Why);
            StopServingInbound(OCPPVersion.OCPP1_6, Why);

            if (server21 is not null)
                await StoppedOCPPServer(server21);

            if (server16 is not null)
                await StoppedOCPPServer(server16);

            Log.Info($"The {OCPPServerLabel} is stopped: {Why}.", "ocpp", "server");

        }

        private async Task StoppedOCPPServer(OCPPWebSocketServer Server)
        {
            try
            {
                await Server.DisposeAsync();
            }
            catch (Exception e)
            {
                Log.Warning($"Stopping the {OCPPServerLabel} failed: {e.GetBaseException().Message}", "ocpp", "server");
            }
        }

        #endregion

        #region (private) JudgeOCPPClientCertificate(Version, Certificate, Chain, PolicyErrors)

        /// <summary>
        /// The client certificate shown at the TLS handshake of the OCPP
        /// server of the given version: passed where a connection the other
        /// end makes signs in with it - by its key pin, or by its CA under the
        /// name it is issued to - and refused otherwise. No certificate is
        /// fine here: who comes without one signs in with what it sends, or
        /// not at all, at the door.
        /// </summary>
        /// <remarks>
        /// This station has nobody else to let in over its OCPP server: a
        /// certificate no connection claims is nobody's, and refused rather
        /// than passed on to a door that would read a name from it.
        /// </remarks>
        private TLSValidationResult JudgeOCPPClientCertificate(OCPPVersion        Version,
                                                               X509Certificate2?  Certificate,
                                                               X509Chain?         Chain,
                                                               SslPolicyErrors    PolicyErrors)
        {

            if (Certificate is null)
                return TLSValidationResult.Success();

            if (InboundCertificateCheck(Version, Certificate, Chain, PolicyErrors) is TLSValidationResult judged)
                return judged;

            Log.Warning(
                $"The client certificate of '{Certificate.Subject}' was refused at the {OCPPServerLabel} for {ConnectionEntry.AsText(Version)}: " +
                "no connection the other end makes signs in with it, neither by its key pin nor by the name it is issued to.",
                "ocpp", "server", "tls"
            );

            return TLSValidationResult.Failed("No connection of this charging station signs in with this client certificate.");

        }

        #endregion

        #region (private) LetInOrNot(Version, Server, Connection)

        /// <summary>
        /// The door of the OCPP server: who asks for a WebSocket is let in as
        /// the connection the other end makes whose name it comes under - by
        /// its client certificate or by its credentials, as that connection
        /// says - or not at all.
        /// </summary>
        /// <remarks>
        /// One place for every way in, so that a certificate opens nothing a
        /// connection named credentials for, and the other way round. An
        /// address that signed in wrong too often is told to wait, whatever
        /// it comes with now - but for a client certificate, which is no
        /// guess. A refusal says nothing of why to the other end, and all of
        /// it to the log.
        /// </remarks>
        private Task<HTTPResponse?> LetInOrNot(OCPPVersion                Version,
                                               OCPPWebSocketServer        Server,
                                               WebSocketServerConnection  Connection)
        {

            // Hermod sets the request before it asks: without one the answer
            // is still no, and null would be yes.
            if (Connection.HTTPRequest is not HTTPRequest request)
                throw new InvalidOperationException($"A connection reached the door of the {OCPPServerLabel} without an HTTP request; refusing it.");

            Task<HTTPResponse?> Answer(HTTPStatusCode Status)
                => Task.FromResult<HTTPResponse?>(
                       new HTTPResponse.Builder(request) {
                           HTTPStatusCode  = Status,
                           Server          = Server.HTTPServiceName,
                           Date            = Timestamp.Now,
                           Connection      = org.GraphDefined.Vanaheimr.Hermod.HTTP.ConnectionType.Close
                       }.AsImmutable
                   );

            var from             = Connection.RemoteSocket.IPAddress.ToString();
            var withCertificate  = Connection.ClientCertificate is not null;

            if (!withCertificate && HasGuessedTooOftenAtOCPP(from))
            {

                Log.Warning($"{Connection.RemoteSocket} is told to wait at the {OCPPServerLabel}: it signed in wrong {MaxFailedOCPPSignInsPerMinute} times within a minute.",
                            "ocpp", "server", "auth");

                return Answer(HTTPStatusCode.TooManyRequests);

            }

            var signIn = SignInInbound(Version, Connection);

            if (signIn is { LetIn: true })
            {

                Log.Debug($"'{signIn.Name}' signed in at the {OCPPServerLabel} from {Connection.RemoteSocket} with {signIn.Why}.",
                          "ocpp", "server", "auth");

                return Task.FromResult<HTTPResponse?>(null);

            }

            // A guess, where it came with something: counted against its address.
            if (request is { Authorization: not null } or { TOTP: not null })
                CountFailedOCPPSignIn(from);

            Log.Warning(
                $"{Connection.RemoteSocket} was turned away at the {OCPPServerLabel} for {ConnectionEntry.AsText(Version)}: " +
                (signIn?.Why ?? $"'{request.Path}' ends with no name a connection the other end makes is written down under") + ".",
                "ocpp", "server", "auth"
            );

            return Answer(HTTPStatusCode.Unauthorized);

        }

        private Boolean HasGuessedTooOftenAtOCPP(String Address)
        {

            if (!failedOCPPSignIns.TryGetValue(Address, out var guesses))
                return false;

            var since = Timestamp.Now - TimeSpan.FromMinutes(1);

            lock (guesses)
            {

                while (guesses.Count > 0 && guesses.Peek() < since)
                    guesses.Dequeue();

                return guesses.Count >= MaxFailedOCPPSignInsPerMinute;

            }

        }

        private void CountFailedOCPPSignIn(String Address)
        {

            var now    = Timestamp.Now;
            var since  = now - TimeSpan.FromMinutes(1);

            // Kept small: what is older than a minute says nothing any more.
            if (failedOCPPSignIns.Count > 4096)
            {
                foreach (var (address, old) in failedOCPPSignIns.ToArray())
                {
                    lock (old)
                    {
                        if (old.Count == 0 || old.Last() < since)
                            failedOCPPSignIns.TryRemove(KeyValuePair.Create(address, old));
                    }
                }
            }

            var guesses = failedOCPPSignIns.GetOrAdd(Address, _ => new Queue<DateTimeOffset>());

            lock (guesses)
            {

                while (guesses.Count > 0 && guesses.Peek() < since)
                    guesses.Dequeue();

                // No more kept than are counted.
                if (guesses.Count < MaxFailedOCPPSignInsPerMinute)
                    guesses.Enqueue(now);

            }

        }

        #endregion

        #region (private) ServeOCPPByTheNewNodes()

        /// <summary>
        /// After the OCPP nodes were rebuilt: the servers of the old ones are
        /// stopped, and new ones started on the new nodes. Whoever dialled in
        /// dials again, and is told again that this station started.
        /// </summary>
        private void ServeOCPPByTheNewNodes()
        {

            if (ocppServer21 is null && ocppServer16 is null)
                return;

            StopServingOCPP("the OCPP nodes of this station were rebuilt").GetAwaiter().GetResult();

            ServeOCPP();

        }

        #endregion


        #region OCPPServerJSON() / TryUpdateOCPPServerConfiguration(JSON, out Error, out NotSaved)

        /// <summary>
        /// The OCPP server, as the web interface reads it: what the file says,
        /// what is served, where the other end dials and who is in.
        /// </summary>
        public JObject OCPPServerJSON()
        {

            JObject Of(OCPPVersion Version, IPPort? Port, OCPPWebSocketServer? Server)
            {

                String? problem;

                lock (ocppServerProblems)
                    ocppServerProblems.TryGetValue(Version, out problem);

                var json = new JObject(
                               new JProperty("ocppVersion",  ConnectionEntry.AsText(Version)),
                               new JProperty("port",         Port?.ToUInt16()),
                               new JProperty("served",       Server is not null),
                               new JProperty("url",          Port.HasValue ? $"{(OCPPServerTLS ? "wss" : "ws")}://{ListenAddress}:{Port}/" : null),
                               new JProperty("connected",    new JArray((Server?.ConnectedNetworkingNodeIds ?? []).Select(name => name.ToString()).Order())),
                               new JProperty("http3Port",    Server?.HTTP3Port?.ToUInt16())
                           );

                if (problem is not null)
                    json.Add(new JProperty("problem", problem));

                // Asked for and not there: without a certificate, or the UDP
                // port was not to be had.
                if (Server is not null && OCPPServer.TakesHTTP3 && Server.HTTP3Port is null &&
                    (OCPPServerTLS ? Server.WhyNoHTTP3 : NoHTTP3WithoutTLS) is String whyNoHTTP3)
                {
                    json.Add(new JProperty("http3Problem", whyNoHTTP3));
                }

                return json;

            }

            return new JObject(
                       new JProperty("enabled",   OCPPServer.IsEnabled),
                       new JProperty("tls",       OCPPServerTLS),
                       new JProperty("http3",     OCPPServer.TakesHTTP3),
                       new JProperty("servers",   new JArray(
                                                      Of(OCPPVersion.OCPP2_1, OCPPServer.Port,   ocppServer21),
                                                      Of(OCPPVersion.OCPP1_6, OCPPServer.Port16, ocppServer16)
                                                  )),
                       new JProperty("inbound",   new JArray(Connections.Connections.
                                                                 Where (connection => connection.Inbound).
                                                                 Select(connection => new JObject(
                                                                                          new JProperty("id",           connection.Id),
                                                                                          new JProperty("description",  connection.Description),
                                                                                          new JProperty("name",         connection.InboundName),
                                                                                          new JProperty("ocppVersion",  ConnectionEntry.AsText(connection.OCPPVersion)),
                                                                                          new JProperty("letIn",        connection.AutoConnect)
                                                                                      )))),
                       new JProperty("file",      ConfigFile.Path)
                   );

        }

        /// <summary>
        /// Switch the OCPP server on or off, or give it other ports: written
        /// to the file, then served or not from that moment, without a
        /// restart. Whoever dialled in is let go of where its server goes.
        /// </summary>
        /// <param name="JSON">The new section.</param>
        /// <param name="Error">What is wrong with it, or what stood in the way.</param>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateOCPPServerConfiguration(JObject                           JSON,
                                                        [NotNullWhen(false)] out String?  Error,
                                                        out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!OCPPServerConfiguration.TryParse(JSON, out var wanted, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (wanted == OCPPServer)
                    return true;

                if (!ConfigFile.TryReplaceSection(OCPPServerConfiguration.SectionName, wanted.ToJSON(), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                OCPPServer = wanted;

                StopServingOCPP(wanted.IsEnabled ? "its configuration was changed" : "switched off").GetAwaiter().GetResult();

                ServeOCPP();

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

    }

}
