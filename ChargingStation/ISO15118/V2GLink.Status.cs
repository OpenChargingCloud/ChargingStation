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
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.SDP.Server;
using cloud.charging.open.protocols.ISO15118.StateMachines;
using cloud.charging.open.protocols.ISO15118.Transport.BouncyCastle;

#endregion

namespace cloud.charging.open.ChargingStation.ISO15118
{

    /// <summary>
    /// What a V2G link is doing right now, for the EVSEs page: the last time a
    /// vehicle asked SDP where the endpoint is and what it was told, the vehicle
    /// connected now and over what, and how the last session ended.
    /// </summary>
    /// <remarks>
    /// Kept as one immutable value each, replaced as a whole, because they are
    /// written by the sockets and read by the station's look at its EVSEs once a
    /// second - and a reader that saw half of a new connection and half of the
    /// old one would draw a vehicle that was never there.
    /// </remarks>
    public sealed partial class V2GLink
    {

        #region Data

        private volatile SDPExchange?     lastSDP;
        private volatile IPEndPoint?      lastPeer;
        private volatile V2GConnection?   connection;
        private volatile EndedSession?    lastSession;

        #endregion


        #region (private records) SDPExchange / V2GConnection / TLSDetails / EndedSession

        /// <summary>
        /// One question a vehicle asked over SDP, and the answer it got.
        /// </summary>
        private sealed record SDPExchange(DateTimeOffset  At,
                                          IPEndPoint      From,
                                          String          Version,
                                          String          Security,
                                          String          Transport,
                                          Boolean         Accepted,
                                          String?         RejectReason,
                                          String?         AnsweredAddress    = null,
                                          UInt16?         AnsweredPort       = null,
                                          String?         AnsweredSecurity   = null);

        /// <summary>
        /// The vehicle on the endpoint right now.
        /// </summary>
        private sealed record V2GConnection(DateTimeOffset  Since,
                                            IPEndPoint?     From,
                                            TLSDetails?     TLS,
                                            String?         Protocol  = null,
                                            String?         Mode      = null);

        /// <summary>
        /// What the TLS handshake settled on.
        /// </summary>
        private sealed record TLSDetails(String             Protocol,
                                         String?            CipherSuite,
                                         String             Library,
                                         X509Certificate2?  Vehicle);

        /// <summary>
        /// How the last session ended.
        /// </summary>
        private sealed record EndedSession(DateTimeOffset  At,
                                           String          Protocol,
                                           Double          Seconds,
                                           Boolean         Paused);

        #endregion


        #region (private) NoteThePeer(Peer)

        /// <summary>
        /// Who is connecting - asked by the listener before the handshake, and
        /// the one place the address is still to be had: the stream it hands
        /// back, under TLS, keeps it to itself.
        /// </summary>
        private Boolean NoteThePeer(IPEndPoint Peer)
        {
            lastPeer = Peer;
            return true;
        }

        #endregion

        #region (internal) Heard(Request) / Answered(Response)

        internal void Heard(SDP_RequestReceivedEventArgs Request)

            => lastSDP = new SDPExchange(
                             Request.Timestamp,
                             Request.RemoteEndpoint,
                             Request.Request.Version.ToString(),
                             Request.Request.Security.ToString(),
                             Request.Request.TransportProtocol.ToString(),
                             Request.Accepted,
                             Request.RejectReason
                         );

        internal void Answered(SDP_ResponseSentEventArgs Response)
        {

            // The answer to the question last heard, from the same vehicle. A
            // duplicate answered again changes nothing worth saying.
            if (lastSDP is { } heard && heard.From.Equals(Response.RemoteEndpoint))
                lastSDP = heard with {
                              AnsweredAddress   = Response.Response.SeccIPAddress.ToString(),
                              AnsweredPort      = Response.Response.SeccPort,
                              AnsweredSecurity  = Response.Response.Security.ToString()
                          };

        }

        #endregion

        #region (private) Connected(Stream) / Agreed(Protocol, Mode) / Disconnected()

        /// <summary>
        /// A vehicle connected, over what the stream says it did.
        /// </summary>
        private void Connected(Stream Stream)

            => connection = new V2GConnection(
                                clock.GetUtcNow(),
                                lastPeer,
                                TLSOf(Stream)
                            );

        private void Agreed(String     Protocol,
                            PowerMode  Mode)
        {
            if (connection is { } now)
                connection = now with {
                                 Protocol  = Protocol,
                                 Mode      = Mode.ToString().ToUpperInvariant()
                             };
        }

        private void Disconnected()
            => connection = null;

        private void Ended(String   Protocol,
                           Double   Seconds,
                           Boolean  Paused)

            => lastSession = new EndedSession(clock.GetUtcNow(), Protocol, Seconds, Paused);

        #endregion

        #region (static) TLSOf(Stream)

        /// <summary>
        /// What the TLS handshake settled on, or null for plain TCP.
        /// </summary>
        /// <remarks>
        /// The platform's TLS says the protocol and the cipher suite it agreed;
        /// BouncyCastle's, which a platform without TLS 1.3 server support gets,
        /// says only the vehicle's certificate - and its protocol is TLS 1.3,
        /// because that is the one thing this endpoint offers.
        /// </remarks>
        private static TLSDetails? TLSOf(Stream Stream)

            => Stream switch {

                   SslStream ssl
                       => new TLSDetails(ssl.SslProtocol.ToString(),
                                         ssl.NegotiatedCipherSuite.ToString(),
                                         "platform",
                                         ssl.RemoteCertificate is null ? null : X509CertificateLoader.LoadCertificate(ssl.RemoteCertificate.GetRawCertData())),

                   BcTlsStream bc
                       => new TLSDetails("Tls13",
                                         null,
                                         "BouncyCastle",
                                         bc.PeerLeafCertificate is null ? null : X509CertificateLoader.LoadCertificate(bc.PeerLeafCertificate)),

                   _   => null

               };

        #endregion


        #region StatusJSON()

        /// <summary>
        /// What this link is doing right now, for the EVSEs page.
        /// </summary>
        /// <remarks>
        /// Nothing in it changes by itself while nothing happens - no "seconds
        /// ago", no counters that tick - because the station says the EVSEs again
        /// whenever what it says differs, and a number that moves every second
        /// would have it say them every second for nothing.
        /// </remarks>
        public JObject StatusJSON()
        {

            var sdp  = lastSDP;
            var now  = connection;
            var last = lastSession;

            return new JObject(

                       new JProperty("interface",    Interface?.Name),
                       new JProperty("endpoint",     V2GEndpoint?.ToString()),
                       new JProperty("tls",          UsesTLS),
                       new JProperty("certificate",  CertificateJSON(Options.ServerCertificate)),
                       new JProperty("sdp",          SDPRunning),
                       new JProperty("slac",         SLACRunning),
                       new JProperty("evseId",       Options.EVSEId),
                       new JProperty("mode",         Options.Mode.ToString().ToUpperInvariant()),

                       new JProperty("lastSDP",      sdp is null
                                                         ? null
                                                         : new JObject(
                                                               new JProperty("at",            sdp.At.ToString("o")),
                                                               new JProperty("from",          sdp.From.ToString()),
                                                               new JProperty("version",       sdp.Version),
                                                               new JProperty("security",      sdp.Security),
                                                               new JProperty("transport",     sdp.Transport),
                                                               new JProperty("accepted",      sdp.Accepted),
                                                               new JProperty("rejectReason",  sdp.RejectReason),
                                                               new JProperty("answer",        sdp.AnsweredAddress is null
                                                                                                  ? null
                                                                                                  : new JObject(
                                                                                                        new JProperty("address",   sdp.AnsweredAddress),
                                                                                                        new JProperty("port",      sdp.AnsweredPort),
                                                                                                        new JProperty("security",  sdp.AnsweredSecurity)
                                                                                                    ))
                                                           )),

                       new JProperty("connection",   now is null
                                                         ? null
                                                         : new JObject(
                                                               new JProperty("since",     now.Since.ToString("o")),
                                                               new JProperty("from",      now.From?.ToString()),
                                                               new JProperty("protocol",  now.Protocol),
                                                               new JProperty("mode",      now.Mode),
                                                               new JProperty("tls",       now.TLS is null
                                                                                              ? null
                                                                                              : new JObject(
                                                                                                    new JProperty("protocol",     now.TLS.Protocol),
                                                                                                    new JProperty("cipherSuite",  now.TLS.CipherSuite),
                                                                                                    new JProperty("library",      now.TLS.Library),
                                                                                                    new JProperty("vehicle",      CertificateJSON(now.TLS.Vehicle))
                                                                                                ))
                                                           )),

                       new JProperty("lastSession",  last is null
                                                         ? null
                                                         : new JObject(
                                                               new JProperty("endedAt",   last.At.ToString("o")),
                                                               new JProperty("protocol",  last.Protocol),
                                                               new JProperty("seconds",   last.Seconds),
                                                               new JProperty("paused",    last.Paused)
                                                           ))

                   );

        }

        #endregion

        #region (static) CertificateJSON(Certificate)

        /// <summary>
        /// A certificate as somebody checks one: whose it is, who says so, for
        /// how long, and its fingerprint.
        /// </summary>
        public static JObject? CertificateJSON(X509Certificate2? Certificate)

            => Certificate is null
                   ? null
                   : new JObject(
                         new JProperty("subject",    Certificate.Subject),
                         new JProperty("issuer",     Certificate.Issuer),
                         new JProperty("notBefore",  new DateTimeOffset(Certificate.NotBefore.ToUniversalTime()).ToString("o")),
                         new JProperty("notAfter",   new DateTimeOffset(Certificate.NotAfter. ToUniversalTime()).ToString("o")),
                         new JProperty("sha256",     Certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256))
                     );

        #endregion

    }

}
