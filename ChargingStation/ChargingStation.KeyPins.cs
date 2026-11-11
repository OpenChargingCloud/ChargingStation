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

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;
using org.GraphDefined.Vanaheimr.Hermod.WebSocket;

using cloud.charging.open.protocols.WWCP.WebSockets;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the white paper "Key Pins and Raw Public Keys for Local Controllers"
    /// asks of the station as a TLS client: a wss:// connection whose server -
    /// typically its Local Controller, which no CA issues a certificate for - is
    /// known by the key pin of its key rather than by a CA (section 5.4); trust
    /// on first use where it is configured, and a key pin learned that way on
    /// probation until a signed pin set confirms it (sections 5.6 and 5.7); and
    /// the security events of section 7.5.
    /// </summary>
    /// <remarks>
    /// What an ssh:// connection does with the host key of its server, done for
    /// the key of a TLS server: the same probation, the same gate, the same
    /// clock. The signed pin set itself arrives over the OCPP Device Model,
    /// whose variables are not built here: <see cref="TryTakeSignedServerKeyPins"/>
    /// is where they hand it over.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        // The trust each key-pinned wss:// connection was dialled with last: what
        // its server showed, and what its WebSocket client asks at every handshake.
        private readonly ConcurrentDictionary<String, ServerKeyPinTrust>  keyPinTrusts  = new ();

        #endregion


        #region (internal) KeyPinTrustOf(ConnectionId)

        /// <summary>
        /// The trust the given connection was dialled with last, where it is
        /// key-pinned: what its server showed, and the validator its WebSocket
        /// client asks.
        /// </summary>
        internal ServerKeyPinTrust? KeyPinTrustOf(String ConnectionId)

            => keyPinTrusts.TryGetValue(ConnectionId, out var trust)
                   ? trust
                   : null;

        #endregion

        #region (private) KeyPinTrustFor(Connection, out NotDialled)

        /// <summary>
        /// The trust a wss:// connection is dialled with, where it is key-pinned;
        /// null where it is checked by its CA, as it always was.
        /// </summary>
        /// <param name="Connection">The connection.</param>
        /// <param name="NotDialled">Why it is not dialled at all: it requires raw public keys, which this station's TLS stack cannot offer yet.</param>
        private ServerKeyPinTrust? KeyPinTrustFor(ConnectionEntry  Connection,
                                                  out String?      NotDialled)
        {

            NotDialled = null;

            if (!Connection.IsKeyPinned)
                return null;

            // .NET's SslStream offers no raw public key, and a certificate is
            // what such a connection refuses: dialled, it would only be turned
            // away, and report its server's certificate as invalid when it is
            // this station that cannot do what was asked.
            if (Connection.RawPublicKeys == RawPublicKeyMode.Require)
            {
                NotDialled = "requires raw public keys, which this station's TLS stack cannot offer yet. Prefer or Off check the same " +
                             "key pins in a certificate";
                return null;
            }

            var trust = new ServerKeyPinTrust(Connection.Copy(),
                                              () => Connections.CopyOf(Connection.Id));

            // Reported once per change of the verdict, at the first attempt as at
            // every one after: the client keeps trying with its back-off, as
            // after any TLS handshake that failed, and would otherwise report
            // the same key again at every attempt.
            trust.OnRefused += (validation, newVerdict) => {
                if (newVerdict)
                    ReportRefused(Connection, trust);
            };

            keyPinTrusts[Connection.Id] = trust;

            return trust;

        }

        #endregion

        #region (private) FollowKeyPins(Connection, Client, Trust, DialledURL, Connected)

        /// <summary>
        /// Follow a key-pinned connection beside what Follow says of it: a key
        /// pin learned on the connection made now and on every one after - the
        /// first, or one learned again after a probation ran out - and a server
        /// not believed when it is tried again.
        /// </summary>
        /// <remarks>
        /// The client keeps its policy: a key that matches none of the pins is
        /// refused at every handshake, and tried again after the back-off, as a
        /// certificate a CA does not vouch for is. Nothing is believed meanwhile,
        /// and a key pinned or learned later - the real server back, a signed
        /// pin set - is taken at the next attempt.
        /// </remarks>
        /// <param name="Connection">The connection, as it is written down.</param>
        /// <param name="Client">The client the node made for it.</param>
        /// <param name="Trust">The trust its server is believed by.</param>
        /// <param name="DialledURL">Where it went when it was dialled.</param>
        /// <param name="Connected">Whether the dial got through: the connection made now is then on probation, where it learned its key pin.</param>
        private void FollowKeyPins(ConnectionEntry    Connection,
                                   WebSocketClient    Client,
                                   ServerKeyPinTrust  Trust,
                                   URL                DialledURL,
                                   Boolean            Connected)
        {

            if (Connected)
                LearnIfFirst(Connection, DialledURL, Trust, Client, (Client as WWCPWebSocketClient)?.WebSocketClientConnection);

            Client.OnWebSocketConnectionAccepted += (timestamp, sender, connection, response, cancellationToken) => {

                if (Client.ReconnectPolicy is not null)
                    LearnIfFirst(Connection, DialledURL, Trust, Client, connection);

                return Task.CompletedTask;

            };

            Client.OnCloseMessageReceived        += (timestamp, sender, connection, frame, eventTrackingId, statusCode, reason, cancellationToken) => {
                onProbation.TryRemove(connection, out _);
                return Task.CompletedTask;
            };

            Trust.OnRefused                      += (validation, newVerdict) => {

                if (Client.ReconnectPolicy is null)
                    return;

                Fail(Connection, ConnectionStatus.Trying,
                     $"'{Connection.Description}' was not believed when it was tried again: {Trust.WhyRefused()}; " +
                      "it is tried again by itself, and said here when it gets through.");

            };

        }

        #endregion

        #region (private static) WhatItBelieves(Trust)

        /// <summary>
        /// What a key-pinned connection believes its server by, as the end of a
        /// sentence.
        /// </summary>
        private static String WhatItBelieves(ServerKeyPinTrust Trust)

            => Trust.Pinned is String pinned
                   ? $"its server must show a key whose key pin is one of those pinned ({pinned}), and nothing else of its certificate is checked"
                   : Trust.Learned is KeyPin learned
                         ? $"its server must show the key whose key pin was learned on its first connection ({learned}), which is on probation"
                         : "no key is pinned, so the key pin of the first key its server shows is learned, on probation until a signed pin set confirms it";

        #endregion

        #region (private) LearnIfFirst(Connection, DialledURL, Trust, Client, Current)

        /// <summary>
        /// Write down the key pin a wss:// connection that trusts on first use
        /// learned, and keep the connection on probation while no signed pin set
        /// confirmed it ("Key Pins and Raw Public Keys", section 5.6).
        /// </summary>
        /// <param name="Current">The WebSocket connection made now.</param>
        private void LearnIfFirst(ConnectionEntry      Connection,
                                  URL                  DialledURL,
                                  ServerKeyPinTrust    Trust,
                                  WebSocketClient      Client,
                                  IMessageConnection?  Current)
        {

            if (Trust.Learned is not KeyPin learned)
                return;

            if (Connections.TryLearnServerKeyPin(Connection.Id, DialledURL, learned, out var notWritten))
            {

                KeyPinSecurityEvent(Connection, "KeyPinLearned", $"Key pin {learned} of endpoint '{Connection.Id}' ({DialledURL}) learned on first use, on probation");

                Log.Warning($"'{Connection.Description}' learned the key pin {learned} of its server's key on its first connection. It is on probation for " +
                            $"{Connection.ServerKeyPinProbationTime.TotalMinutes:0.#} minute(s): until a signed pin set confirms it, this station " +
                             "executes nothing its server asks but such a pin set.",
                            "ocpp", "connections", "keypins", "security");

            }

            else if (notWritten is not null)
                Log.Warning($"'{Connection.Description}' learned the key pin {learned} of its server's key on its first connection, and it could not be " +
                            $"written down: {notWritten}. It is on probation all the same.",
                            "ocpp", "connections", "keypins", "security");

            if (Current is not null)
                onProbation[Current] = new Probation(Connection, null, Trust, Client);

            StartProbationClock(Connection, Trust, learned);

        }

        #endregion

        #region (private) StartProbationClock(Connection, Trust, Learned)

        /// <summary>
        /// Forget the learned key pin where no signed pin set confirmed it in
        /// time, and drop the connection, so that it learns again after its
        /// back-off: a key pin learned is never kept unconfirmed, and a server
        /// that answered first cannot lock the real one out.
        /// </summary>
        private void StartProbationClock(ConnectionEntry    Connection,
                                         ServerKeyPinTrust  Trust,
                                         KeyPin             Learned)
        {

            var learnedAt  = Connections.CopyOf(Connection.Id)?.ServerKeyPinLearnedAt ?? TimeProvider.GetUtcNow();
            var probation  = Connections.CopyOf(Connection.Id)?.ServerKeyPinProbationTime ?? Connection.ServerKeyPinProbationTime;
            var remaining  = learnedAt + probation - TimeProvider.GetUtcNow();
            var clock      = new CancellationTokenSource();

            if (!probationClocks.TryAdd(Connection.Id, clock))
            {
                clock.Dispose();
                return;
            }

            _ = Task.Run(async () => {

                try
                {
                    if (remaining > TimeSpan.Zero)
                        await Task.Delay(remaining, TimeProvider, clock.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    probationClocks.TryRemove(new KeyValuePair<String, CancellationTokenSource>(Connection.Id, clock));
                }

                // Confirmed meanwhile: a signed pin set ended the probation.
                if (!Trust.OnProbation || Trust.Learned != Learned)
                    return;

                Connections.TryForgetLearnedServerKeyPin(Connection.Id, Learned);
                Trust.Forget();

                KeyPinSecurityEvent(Connection, "KeyPinProbationExpired",
                                    $"Key pin {Learned} of endpoint '{Connection.Id}' ({Trust.URL}) not confirmed within {probation.TotalSeconds:0} s; discarded");

                Log.Warning($"'{Connection.Description}': no signed pin set confirmed the key pin {Learned} within " +
                            $"{probation.TotalMinutes:0.#} minute(s), so it is forgotten, and the connection is made anew.",
                            "ocpp", "connections", "keypins", "security");

                foreach (var onIt in onProbation.Where(probation => ReferenceEquals(probation.Value.KeyPinTrust, Trust)).ToArray())
                {

                    onProbation.TryRemove(onIt.Key, out _);

                    // Closed underneath its client, which takes that for a loss
                    // and comes back after its back-off - and learns again.
                    if (onIt.Key is WebSocketClientConnection current)
                    {

                        await current.Close(WebSocketFrame.ClosingStatusCode.PolicyViolation, "The key of this server was not confirmed in time.");

                        Fail(Connection, ConnectionStatus.Lost,
                             $"'{Connection.Description}' was closed: no signed pin set confirmed the key pin of its server in time. " +
                              "It comes back by itself, and learns again.");

                    }

                }

            });

        }

        #endregion

        #region (private) ReportRefused(Connection, Trust)

        /// <summary>
        /// The security event of a key that matches none of the key pins:
        /// InvalidCsmsCertificate, as A00.FR.310 reports a certificate the
        /// station did not accept ("Key Pins and Raw Public Keys", section 7.5).
        /// </summary>
        private void ReportRefused(ConnectionEntry    Connection,
                                   ServerKeyPinTrust  Trust)

            => KeyPinSecurityEvent(Connection, "InvalidCsmsCertificate",
                                   $"Key pin mismatch: {Trust.Shown?.ToHex() ?? "no key"} at endpoint '{Connection.Id}' ({Trust.URL})");

        #endregion

        #region (internal) TryTakeSignedServerKeyPins(Connection, Pins, Version, Signer, out Error)

        /// <summary>
        /// Take the key pins of a signed pin set for a wss:// connection: what
        /// ServerKeyPins and ServerKeyPinsVersion of NetworkConfiguration (OCPP
        /// 2.x) or evqi.serverKeyPins (OCPP 1.6) hand over, once a signer this
        /// station believes was found to have signed them ("Key Pins and Raw
        /// Public Keys", sections 5.7, 7.2 and 7.3).
        /// </summary>
        /// <remarks>
        /// Only with a higher version than the one the connection holds. Its
        /// trust is held to the pins from the next handshake on, and a probation
        /// ends: confirmed, where the pins have the key pin learned; otherwise
        /// with that server refused - the connection is dropped, and comes back
        /// held to the signed pins.
        /// </remarks>
        /// <param name="Connection">The connection, by its identification.</param>
        /// <param name="Pins">The key pins of the pin set, separated by commas.</param>
        /// <param name="Version">The version of the pin set.</param>
        /// <param name="Signer">Who signed it, for the log.</param>
        /// <param name="Error">Why the pins were not taken.</param>
        internal Boolean TryTakeSignedServerKeyPins(String                           Connection,
                                                    String                           Pins,
                                                    UInt64                           Version,
                                                    String                           Signer,
                                                    [NotNullWhen(false)] out String? Error)
        {

            // A key pin learned on first use that could not be written down is
            // held only by its trust. The signed pin set judges that one all the
            // same: otherwise a connection on probation to a server its CSMS does
            // not know would go on, and without the gate.
            var held = KeyPinTrustOf(Connection)?.Learned;

            if (!Connections.TryTakeSignedServerKeyPins(Connection, Pins, Version, out var learned, out var confirmed, out Error))
                return false;

            if (learned is null && held is not null)
            {
                learned    = held;
                confirmed  = Connections.CopyOf(Connection)?.ServerKeyPins.Contains(held) == true;
            }

            ServerKeyPinSetTaken(Connection, learned, confirmed, Version, Signer);

            return true;

        }

        #endregion

        #region (private) ServerKeyPinSetTaken(Connection, Learned, Confirmed, Version, Signer)

        /// <summary>
        /// A signed pin set was taken for a wss:// connection: a probation ends -
        /// confirmed, or with the server it learned refused.
        /// </summary>
        private void ServerKeyPinSetTaken(String   Connection,
                                          KeyPin?  Learned,
                                          Boolean  Confirmed,
                                          UInt64   Version,
                                          String   Signer)
        {

            if (Connections.CopyOf(Connection) is not ConnectionEntry entry)
                return;

            // The pins are what the next handshake is held to; nothing learned
            // is believed beside them.
            if (keyPinTrusts.TryGetValue(Connection, out var trust))
                trust.Forget();

            if (probationClocks.TryRemove(Connection, out var clock))
                clock.Cancel();

            var onIt = onProbation.Where(probation => probation.Value.Connection.Id == Connection).ToArray();

            foreach (var probation in onIt)
                onProbation.TryRemove(probation.Key, out _);

            var pins = ConnectionEntry.AsText(entry.ServerKeyPins);

            if (Learned is not null && Confirmed)
            {

                Log.Notice($"'{entry.Description}': the signed pin set of version {Version}, signed by '{Signer}', confirms the key pin " +
                           $"{Learned} it learned on its first connection; its probation ends.", "ocpp", "connections", "keypins", "security");

                KeyPinSecurityEvent(entry, "KeyPinConfirmed",
                                    $"Key pin {Learned} of endpoint '{entry.Id}' ({entry.URL}) confirmed by the signed pin set of version {Version}");

            }

            else if (Learned is not null)
            {

                Log.Warning($"'{entry.Description}': the signed pin set of version {Version}, signed by '{Signer}', does not have the key pin " +
                            $"{Learned} it learned on its first connection - that server is not the one its CSMS knows. The connection is made anew, " +
                            $"held to the signed pins ({pins}).", "ocpp", "connections", "keypins", "security");

                KeyPinSecurityEvent(entry, "KeyPinRejected",
                                    $"Key pin {Learned} of endpoint '{entry.Id}' ({entry.URL}) is not among the signed pins of version {Version}, which replace it");

                foreach (var probation in onIt)
                    if (probation.Key is WebSocketClientConnection current)
                        _ = current.Close(WebSocketFrame.ClosingStatusCode.PolicyViolation, "The key of this server is not among the signed pins.");

            }

            else
                KeyPinSecurityEvent(entry, "ReconfigurationOfSecurityParameters",
                                    $"Key pins of endpoint '{entry.Id}' ({entry.URL}) set by a signed pin set of version {Version}");

        }

        #endregion

        #region (internal) KeyPinSecurityEvent(Connection, Type, TechInfo)

        /// <summary>
        /// Report a security event of the white paper "Key Pins and Raw Public
        /// Keys" (section 7.5) through the OCPP node of the connection's
        /// version: InvalidCsmsCertificate is InvalidCentralSystemCertificate in
        /// OCPP 1.6, the new ones are the same in both.
        /// </summary>
        internal void KeyPinSecurityEvent(ConnectionEntry  Connection,
                                          String           Type,
                                          String           TechInfo)

            => SshSecurityEvent(Connection,
                                Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6 && Type == "InvalidCsmsCertificate"
                                    ? "InvalidCentralSystemCertificate"
                                    : Type,
                                TechInfo);

        #endregion

    }

}
