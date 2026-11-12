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

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The certificate this station shows its CSMS, asked for and brought over
    /// OCPP 2.1 (use cases A02 and A03): a key made here, its signing request
    /// in SignCertificate, the certificate in CertificateSigned - and the roots
    /// a CSMS installs for its own servers with InstallCertificate.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The key never leaves the station. A new one is made for every request,
    /// with the subject and the kind of the key it replaces, and waits beside
    /// that until its certificate comes. The connection keeps showing the
    /// certificate it has: nothing is switched before there is something to
    /// switch to.
    /// </para>
    /// <para>
    /// CertificateSigned is answered first and acted on a moment later, so
    /// that the answer still goes out over the connection it came in on. Then
    /// every connection that showed the old certificate is dialled again with
    /// the new one. Where that does not get through within
    /// <see cref="ANewCertificateGetsThroughWithin"/>, the connection goes
    /// back to the certificate it had: a certificate its CSMS does not let in
    /// must not be the end of a station that had one that worked. The old key
    /// is kept either way, and is removed on the client keys page.
    /// </para>
    /// <para>
    /// Asked for when the CSMS says so - TriggerMessage of
    /// SignChargingStationCertificate - and by the station itself, once per
    /// day at most, when it connects and the certificate it shows runs out
    /// within <see cref="RenewsDaysBefore"/> days.
    /// </para>
    /// <para>
    /// OCPP 2.1 only: the OCPP 1.6 node takes the roots, and asks for no
    /// certificate here. A V2G certificate is refused - this station has no
    /// key of its own a vehicle would be shown.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region (record) PendingStationCertificate

        /// <summary>
        /// A key whose certificate was asked for, and what it is to replace.
        /// </summary>
        /// <param name="KeyId">The new key.</param>
        /// <param name="Replaces">The key whose certificate the connections show now, or null where they show none.</param>
        /// <param name="Connections">The connections that are switched to it.</param>
        /// <param name="RequestId">The id of the SignCertificate request.</param>
        /// <param name="AskedAt">When it was asked for.</param>
        private sealed record PendingStationCertificate(String                 KeyId,
                                                        String?                Replaces,
                                                        IReadOnlyList<String>  Connections,
                                                        Int32                  RequestId,
                                                        DateTimeOffset         AskedAt);

        #endregion

        #region Data

        /// <summary>
        /// How many days before the certificate it shows runs out a station asks
        /// for the next one by itself.
        /// </summary>
        public const Int32 RenewsDaysBefore = 30;

        /// <summary>
        /// How often at most a station asks by itself for the certificate that
        /// replaces the one of a key: a CSMS that says no, or brings one it
        /// does not let in, is not asked at every connection.
        /// </summary>
        public static readonly TimeSpan RenewsAtMostEvery = TimeSpan.FromDays(1);

        private readonly ConcurrentDictionary<String, PendingStationCertificate>  pendingStationCertificates  = new (StringComparer.Ordinal);

        // When the station last asked by itself for the certificate that is
        // to replace the one of a key: once a day, however often it connects.
        private readonly ConcurrentDictionary<String, DateTimeOffset>             renewalAskedAt              = new (StringComparer.Ordinal);

        private          CSMSRootCertificates?                                    csmsRootCertificates;

        private          Int32                                                    signCertificateRequestIds;

        #endregion

        #region Properties

        /// <summary>
        /// How long after CertificateSigned was answered the connections are
        /// dialled again with the new certificate: long enough for the answer
        /// to be on its way.
        /// </summary>
        internal TimeSpan SwitchesToANewCertificateAfter       { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// How long a connection has to get through with a new certificate
        /// before it goes back to the one it had.
        /// </summary>
        internal TimeSpan ANewCertificateGetsThroughWithin     { get; set; } = TimeSpan.FromSeconds(60);

        /// <summary>
        /// The roots for its back ends a CSMS installs, lists and deletes: TLS
        /// roots of this station's certificate store, for "ocpp".
        /// </summary>
        internal CSMSRootCertificates CSMSRootCertificates
            => csmsRootCertificates ??= new CSMSRootCertificates(this);

        /// <summary>
        /// The keys whose certificate was asked for and has not come yet.
        /// </summary>
        public IReadOnlyCollection<String> KeysWaitingForTheirCertificate
            => [.. pendingStationCertificates.Keys];

        #endregion


        #region (internal) AttachStationCertificate(Node)

        /// <summary>
        /// What the OCPP 2.1 node answers of the station's certificate:
        /// CertificateSigned, the trigger of SignChargingStationCertificate,
        /// and the roots of its CSMS.
        /// </summary>
        internal void AttachStationCertificate(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            Node.OnCertificateSignedByOwner  = CertificateSigned;
            Node.OnSignCertificateTrigger    = SignCertificateTrigger;

            Node.CertificateSources.Add(CSMSRootCertificates);

        }

        #endregion


        #region (private) ConnectionsShowingACertificate(ConnectionId = null)

        /// <summary>
        /// The wss:// connections as OCPP 2.1 a certificate would be shown on:
        /// the given one, or all of them - those that show one already first.
        /// </summary>
        private ConnectionEntry[] ConnectionsShowingACertificate(String? ConnectionId = null)

            => [.. Connections.Connections.
                               Where          (connection => connection.OCPPVersion == OCPPVersion.OCPP2_1 &&
                                                             connection.IsSecure                           &&
                                                             !connection.IsSSH                             &&
                                                             (ConnectionId is null || connection.Id == ConnectionId)).
                               OrderByDescending(connection => connection.CertificateId is not null).
                               ThenByDescending (connection => ConnectionStates.TryGetValue(connection.Id, out var state) &&
                                                               state.Status == ConnectionStatus.Connected)];

        #endregion

        #region (private static) IdentityOf(Connection)

        /// <summary>
        /// Who the station says it is on a connection: the last segment of the
        /// address it dials, which is the name its CSMS knows it by.
        /// </summary>
        private static String? IdentityOf(ConnectionEntry Connection)
        {

            var text   = Connection.URL.ToString();
            var query  = text.IndexOfAny([ '?', '#' ]);

            if (query >= 0)
                text = text[..query];

            var identity = Uri.UnescapeDataString(text.TrimEnd('/')[(text.TrimEnd('/').LastIndexOf('/') + 1)..]);

            // "wss://csms.example.org" has no segment: its last one is the host.
            return identity.Length > 0 && !text.TrimEnd('/').EndsWith("//" + identity, StringComparison.Ordinal)
                       ? identity
                       : null;

        }

        #endregion


        #region AskForAStationCertificate(Why, ConnectionId = null)

        /// <summary>
        /// Make a new key and ask the CSMS for its certificate in
        /// SignCertificate. The connections go on showing what they show until
        /// CertificateSigned brings it.
        /// </summary>
        /// <param name="Why">Why it is asked for, for the log.</param>
        /// <param name="ConnectionId">The connection it is for; every wss:// connection as OCPP 2.1 that shows the same certificate where none is given.</param>
        /// <returns>Whether the CSMS accepted the request, and what there is to say about it.</returns>
        public async Task<(Boolean Accepted, String Said)> AskForAStationCertificate(String   Why,
                                                                                     String?  ConnectionId   = null)
        {

            var candidates = ConnectionsShowingACertificate(ConnectionId);

            if (candidates.Length == 0)
                return (false, ConnectionId is null
                                   ? "This station dials no wss:// connection as OCPP 2.1 a certificate could be shown on."
                                   : $"'{ConnectionId}' is no wss:// connection as OCPP 2.1 of this station.");

            var first       = candidates[0];
            var replaces    = first.CertificateId;
            var before      = replaces is not null
                                  ? ClientCertificates.Entries.FirstOrDefault(entry => entry.Id == replaces)
                                  : null;

            // Every connection that shows the same certificate gets the new
            // one: it is the station's, not the connection's.
            var connections = candidates.Where (connection => connection.CertificateId == replaces &&
                                                              (replaces is not null || connection.Id == first.Id)).
                                         Select(connection => connection.Id).
                                         ToArray();

            var subject     = before?.Subject ?? IdentityOf(first) ?? cs02.Id.ToString();

            String? keyId, csr, error;

            if (!ClientCertificates.TryCreateKey(subject, before?.Algorithm, out keyId, out csr, out error) &&
                !(before is not null && ClientCertificates.TryCreateKey(subject, null, out keyId, out csr, out error)))
            {
                Log.Warning($"No certificate could be asked for, as {Why}: no key could be made for '{subject}': {error}", "ocpp", "certificates", "security");
                return (false, $"No key could be made for '{subject}': {error}");
            }

            // One at a time for what it replaces: a key that waited for a
            // certificate that never came is of no use to anybody.
            foreach (var waiting in pendingStationCertificates.Values.Where(waiting => waiting.Replaces == replaces).ToArray())
                if (pendingStationCertificates.TryRemove(waiting.KeyId, out _))
                    ClientCertificates.TryRemove(waiting.KeyId, out _);

            var pending = new PendingStationCertificate(keyId!,
                                                        replaces,
                                                        connections,
                                                        Interlocked.Increment(ref signCertificateRequestIds),
                                                        TimeProvider.GetUtcNow());

            pendingStationCertificates[pending.KeyId] = pending;

            try
            {

                var response = await cs02.OCPP.OUT.SignCertificate(
                                         new OCPPv2_1.CS.SignCertificateRequest(
                                             SourceRouting.To(NetworkingNode_Id.CSMS),
                                             pending.RequestId,
                                             csr!,
                                             OCPPv2_1.CertificateSigningUse.ChargingStationCertificate
                                         )
                                     );

                if (response.Status != OCPPv2_1.GenericStatus.Accepted)
                {

                    var said = $"The CSMS answered {OCPPv2_1.GenericStatusExtensions.AsText(response.Status)}" +
                               (response.StatusInfo is not null ? $" ({response.StatusInfo.ReasonCode}{(response.StatusInfo.AdditionalInfo is not null ? ": " + response.StatusInfo.AdditionalInfo : "")})" : "") +
                               " to SignCertificate.";

                    GiveUpOn(pending);

                    Log.Warning($"A certificate for '{subject}' was asked for, as {Why}, and not promised: {said} The key made for it is gone again.",
                                "ocpp", "certificates", "security");

                    return (false, said);

                }

                Log.Notice($"A certificate for '{subject}' was asked for in SignCertificate, as {Why}: the CSMS accepted the request for the new key {pending.KeyId}" +
                           (replaces is not null ? $", which replaces {replaces}" : "") +
                           $". {(connections.Length == 1 ? "The connection goes" : $"{connections.Length} connections go")} on as before until CertificateSigned brings it.",
                           "ocpp", "certificates", "security");

                return (true, $"The CSMS accepted the request for the new key {pending.KeyId}.");

            }
            catch (Exception e)
            {

                GiveUpOn(pending);

                Log.Warning($"A certificate for '{subject}' could not be asked for, as {Why}: {e.Message}", "ocpp", "certificates", "security");

                return (false, e.Message);

            }

        }

        #endregion

        #region (private) GiveUpOn(Pending)

        /// <summary>
        /// A key nobody will bring a certificate for is not kept.
        /// </summary>
        private void GiveUpOn(PendingStationCertificate Pending)
        {
            if (pendingStationCertificates.TryRemove(Pending.KeyId, out _))
                ClientCertificates.TryRemove(Pending.KeyId, out _);
        }

        #endregion

        #region (private) RenewStationCertificateIfDue()

        /// <summary>
        /// Ask for the next certificate where the one a wss:// connection as
        /// OCPP 2.1 shows runs out within <see cref="RenewsDaysBefore"/> days,
        /// and none is waited for yet.
        /// </summary>
        private async Task RenewStationCertificateIfDue()
        {

            try
            {

                var now = TimeProvider.GetUtcNow();

                foreach (var keyId in ConnectionsShowingACertificate().
                                          Select  (connection => connection.CertificateId).
                                          Distinct().
                                          ToArray ())
                {

                    if (keyId is null                                                                              ||
                        pendingStationCertificates.Values.Any(pending => pending.Replaces == keyId)                 ||
                        ClientCertificates.Entries.FirstOrDefault(entry => entry.Id == keyId) is not { } showing    ||
                        showing.NotAfter is not DateTimeOffset notAfter                                             ||
                        notAfter - now > TimeSpan.FromDays(RenewsDaysBefore)                                        ||
                        (renewalAskedAt.TryGetValue(keyId, out var askedAt) && now - askedAt < RenewsAtMostEvery))
                    {
                        continue;
                    }

                    renewalAskedAt[keyId] = now;

                    var connection = ConnectionsShowingACertificate().First(connection => connection.CertificateId == keyId);

                    await AskForAStationCertificate(notAfter <= now
                                                        ? $"the certificate it shows ran out on {notAfter:yyyy-MM-dd}"
                                                        : $"the certificate it shows runs out on {notAfter:yyyy-MM-dd}, within {RenewsDaysBefore} days",
                                                    connection.Id);

                }

            }
            catch (Exception e)
            {
                Log.Warning($"Whether the certificate this station shows is to be renewed could not be looked at: {e.Message}", "ocpp", "certificates");
            }

        }

        #endregion


        #region (private) SignCertificateTrigger(Request)

        /// <summary>
        /// TriggerMessage of SignChargingStationCertificate: a new key is made,
        /// and shown to the CSMS in SignCertificate. A V2G certificate is the
        /// node's to answer, which has none.
        /// </summary>
        private Task<OCPPv2_1.CS.TriggerMessageResponse?> SignCertificateTrigger(OCPPv2_1.CSMS.TriggerMessageRequest Request)
        {

            if (Request.RequestedMessage != OCPPv2_1.MessageTrigger.SignChargingStationCertificate)
                return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                           new OCPPv2_1.CS.TriggerMessageResponse(Request, OCPPv2_1.TriggerMessageStatus.Rejected,
                                                                  new OCPPv2_1.StatusInfo("NotSupported", "This station has no key of its own a vehicle is shown: it asks for a ChargingStationCertificate only."))
                       );

            if (ConnectionsShowingACertificate().Length == 0)
                return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                           new OCPPv2_1.CS.TriggerMessageResponse(Request, OCPPv2_1.TriggerMessageStatus.Rejected,
                                                                  new OCPPv2_1.StatusInfo("NoTLS", "This station dials no wss:// connection as OCPP 2.1 a certificate could be shown on."))
                       );

            _ = Task.Run(() => AskForAStationCertificate("its CSMS asked for it"));

            return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                       new OCPPv2_1.CS.TriggerMessageResponse(Request, OCPPv2_1.TriggerMessageStatus.Accepted)
                   );

        }

        #endregion

        #region (private) CertificateSigned(Request, CancellationToken)

        /// <summary>
        /// The CSMS brings a certificate: taken where it is one for a key of
        /// this station, and - where that key waited for it - the connections
        /// are switched to it a moment after the answer went out.
        /// </summary>
        private Task<OCPPv2_1.CS.CertificateSignedResponse?> CertificateSigned(OCPPv2_1.CSMS.CertificateSignedRequest  Request,
                                                                               CancellationToken                       CancellationToken)
        {

            OCPPv2_1.CS.CertificateSignedResponse Rejected(String ReasonCode, String Why)
            {

                Log.Warning($"A CertificateSigned was rejected ({ReasonCode}): {Why}", "ocpp", "certificates", "security");

                StationCertificateSecurityEvent("InvalidChargingStationCertificate", Why);

                return new OCPPv2_1.CS.CertificateSignedResponse(Request,
                                                                 OCPPv2_1.CertificateSignedStatus.Rejected,
                                                                 new OCPPv2_1.StatusInfo(ReasonCode, Why.Length > 1024 ? Why[..1024] : Why));

            }

            if (Request.CertificateType is OCPPv2_1.CertificateSigningUse type &&
                type != OCPPv2_1.CertificateSigningUse.ChargingStationCertificate)
            {
                return Task.FromResult<OCPPv2_1.CS.CertificateSignedResponse?>(
                           Rejected("NotSupported", $"This station keeps no key for a certificate of type {type}: it asked for none.")
                       );
            }

            var pems = new List<String>();

            foreach (var certificate in Request.CertificateChain)
            {

                if (certificate.Parsed is not { } parsed)
                    return Task.FromResult<OCPPv2_1.CS.CertificateSignedResponse?>(
                               Rejected("InvalidCertificate", "The certificate chain holds something that is no PEM encoded X.509 certificate.")
                           );

                pems.Add(parsed.ExportCertificatePem());

            }

            if (!ClientCertificates.TryAddCertificate(String.Join("\n", pems), out var keyId, out var warnings, out var error, out var notSaved))
                return Task.FromResult<OCPPv2_1.CS.CertificateSignedResponse?>(
                           Rejected(notSaved ? "InternalError" : "InvalidCertificate", error)
                       );

            if (pendingStationCertificates.TryRemove(keyId, out var pending))
            {

                Log.Notice($"CertificateSigned brought the certificate of the new key {keyId}" +
                           (warnings.Count > 0 ? $" ({String.Join(" ", warnings)})" : "") +
                           $". {(pending.Connections.Count == 1 ? "Its connection is" : $"Its {pending.Connections.Count} connections are")} dialled again with it in {SwitchesToANewCertificateAfter.TotalSeconds:0.#} second(s).",
                           "ocpp", "certificates", "security");

                _ = Task.Run(() => SwitchTo(pending), CancellationToken.None);

            }

            else
                Log.Notice($"CertificateSigned brought a certificate for the key {keyId}, which did not wait for one" +
                           (warnings.Count > 0 ? $" ({String.Join(" ", warnings)})" : "") +
                            ": it is shown wherever a connection names that key, from its next handshake on.",
                           "ocpp", "certificates", "security");

            return Task.FromResult<OCPPv2_1.CS.CertificateSignedResponse?>(
                       new OCPPv2_1.CS.CertificateSignedResponse(Request, OCPPv2_1.CertificateSignedStatus.Accepted)
                   );

        }

        #endregion

        #region (private) SwitchTo(Pending)

        /// <summary>
        /// Dial the connections again with the new certificate - and go back to
        /// the one they had where that does not get through.
        /// </summary>
        private async Task SwitchTo(PendingStationCertificate Pending)
        {

            try
            {

                await Task.Delay(SwitchesToANewCertificateAfter);

                var switchedAt  = TimeProvider.GetUtcNow();
                var switched    = new List<String>();

                foreach (var id in Pending.Connections)
                {

                    // Not one somebody changed meanwhile: that hand knew better.
                    if (Connections.CopyOf(id) is not ConnectionEntry now || now.CertificateId != Pending.Replaces)
                        continue;

                    if (Connections.TrySetCertificate(id, Pending.KeyId, out var error))
                        switched.Add(id);

                    else
                        Log.Warning($"'{now.Description}' could not be switched to the new certificate of the key {Pending.KeyId}: {error}", "ocpp", "certificates", "security");

                }

                if (switched.Count == 0)
                    return;

                StationCertificateSecurityEvent("ReconfigurationOfSecurityParameters",
                                                $"ChargingStationCertificate of the key {Pending.KeyId} is shown" +
                                                (Pending.Replaces is not null ? $" instead of {Pending.Replaces}" : ""));

                Boolean GotThrough(String Id)
                    => ConnectionStates.TryGetValue(Id, out var state)   &&
                       state.Status == ConnectionStatus.Connected       &&
                       state.Since  >= switchedAt;

                var until = DateTimeOffset.UtcNow + ANewCertificateGetsThroughWithin;

                while (DateTimeOffset.UtcNow < until && !switched.All(GotThrough))
                    await Task.Delay(100);

                var stuck = switched.Where(id => !GotThrough(id) &&
                                                 Connections.CopyOf(id) is ConnectionEntry now &&
                                                 now.CertificateId == Pending.KeyId).
                                     ToArray();

                if (stuck.Length == 0)
                {

                    Log.Notice($"The new certificate of the key {Pending.KeyId} got through" +
                               (Pending.Replaces is not null ? $". The key {Pending.Replaces} it replaces is kept, and is removed on the client keys page." : "."),
                               "ocpp", "certificates", "security");

                    return;

                }

                // Without one to go back to, the new one is all there is.
                if (Pending.Replaces is null)
                {
                    Log.Warning($"The new certificate of the key {Pending.KeyId} did not get through within {ANewCertificateGetsThroughWithin.TotalSeconds:0.#} second(s). " +
                                 "There is none to go back to, so it stays.", "ocpp", "certificates", "security");
                    return;
                }

                foreach (var id in stuck)
                    if (!Connections.TrySetCertificate(id, Pending.Replaces, out var error))
                        Log.Warning($"Connection '{id}' could not be switched back to the certificate of the key {Pending.Replaces}: {error}", "ocpp", "certificates", "security");

                Log.Warning($"The new certificate of the key {Pending.KeyId} did not get through within {ANewCertificateGetsThroughWithin.TotalSeconds:0.#} second(s): " +
                            $"{(stuck.Length == 1 ? "the connection is" : $"{stuck.Length} connections are")} dialled with the certificate of the key {Pending.Replaces} again. " +
                             "The new one is kept, and is chosen on the Connections page once its CSMS lets it in.",
                            "ocpp", "certificates", "security");

                StationCertificateSecurityEvent("InvalidChargingStationCertificate",
                                                $"ChargingStationCertificate of the key {Pending.KeyId} did not get through; back to {Pending.Replaces}");

            }
            catch (Exception e)
            {
                Log.Warning($"Switching to the new certificate of the key {Pending.KeyId} failed: {e.Message}", "ocpp", "certificates", "security");
            }

        }

        #endregion

        #region (private) StationCertificateSecurityEvent(Type, TechInfo)

        /// <summary>
        /// Report a security event about the station's certificate through the
        /// OCPP 2.1 node, which is the one it is brought over.
        /// </summary>
        private void StationCertificateSecurityEvent(String  Type,
                                                     String  TechInfo)
        {

            try
            {
                _ = cs02.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo.Length > 255 ? TechInfo[..255] : TechInfo);
            }
            catch (Exception e)
            {
                Log.Warning($"The security event '{Type}' could not be queued: {e.Message}", "ocpp", "certificates", "security");
            }

        }

        #endregion

    }

}
