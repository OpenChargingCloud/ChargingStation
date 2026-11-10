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
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the white paper "OCPP over SSH" asks of the station beyond dialling: its SSH client in the
    /// Device Model (SSHClientController, section 9.2), pin sets a signer it believes signed (section
    /// 10.4), one key per purpose and their rotation (sections 10.1 and 10.3), and the security events
    /// (section 9.4).
    /// </summary>
    public partial class ChargingStation
    {

        #region Data

        // The trust each connection over SSH was dialled with last: what its
        // server showed, and where a signed pin set puts its pins at once.
        private readonly ConcurrentDictionary<String, SshHostKeyTrust>  sshTrusts  = new ();

        private SshClientKey? sftpKey;

        #endregion

        #region Properties

        /// <summary>
        /// The key this station signs in with for SFTP: a key of its own, as every purpose has one
        /// (decision 9).
        /// </summary>
        public SshClientKey SftpKey

            => sftpKey ??= new SshClientKey(System.IO.Path.Combine(Connections.Path, SshClientKey.DirectoryName),
                                            $"chargingstation-sftp@{Environment.MachineName}",
                                            "id_ed25519_sftp");

        #endregion


        #region (private) KeyOf(Purpose)

        /// <summary>
        /// The station's key of the given purpose; null for NTS, whose key is the node's.
        /// </summary>
        private SshClientKey? KeyOf(OCPPv2_1.SSHKeyPurpose Purpose)

            => Purpose switch {
                   OCPPv2_1.SSHKeyPurpose.OCPP  => SshKey,
                   OCPPv2_1.SSHKeyPurpose.SFTP  => SftpKey,
                   _                            => null
               };

        #endregion

        #region (internal) FollowSSHClientComponents(Node)

        /// <summary>
        /// Give the OCPP 2.1 node an SSHClientController for the station's keys and one for every
        /// connection over SSH there is now, and none for one there is not.
        /// </summary>
        internal void FollowSSHClientComponents(OCPPv2_1.CS.TestChargingStationNode? Node)
        {

            if (Node is null)
                return;

            var source       = new SSHClientSource(this);
            var connections  = source.Connections.ToArray();

            foreach (var shown in Node.GetComponentConfigs(nameof(OCPPv2_1.SSHClientController)).OfType<OCPPv2_1.SSHClientController>().ToArray())
                if (shown.Connection is not null && !connections.Contains(shown.Connection, StringComparer.Ordinal))
                    Node.RemoveComponent(shown.Name, shown.Instance);

            if (!Node.GetComponentConfigs(nameof(OCPPv2_1.SSHClientController)).OfType<OCPPv2_1.SSHClientController>().Any(shown => shown.Connection is null))
                Node.AddComponent(new OCPPv2_1.SSHClientController(source));

            foreach (var connection in connections)
                if (!Node.GetComponentConfigs(nameof(OCPPv2_1.SSHClientController)).OfType<OCPPv2_1.SSHClientController>().Any(shown => shown.Connection == connection))
                    Node.AddComponent(new OCPPv2_1.SSHClientController(source, connection));

        }

        #endregion

        #region (internal) AttachSSHClient(Node)

        /// <summary>
        /// What the OCPP 2.1 node answers of the SSH client: the controller, SSHKeySigned, and the
        /// CustomTrigger "SignSSHKey".
        /// </summary>
        internal void AttachSSHClient(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            FollowSSHClientComponents(Node);

            Node.OnCustomTrigger          = CustomTrigger;
            Node.OCPP.IN.OnSSHKeySigned  += SSHKeySigned;

        }

        #endregion


        #region (private) SignedByPinSetSigner(Request, out Signer, out Why)

        /// <summary>
        /// Whether a signer this station believes signed the given SetVariables request: one of the pin
        /// set signers on its Connections page (white paper "OCPP over SSH", section 10.4; "Key Pins and
        /// Raw Public Keys", section 5.7, way 1).
        /// </summary>
        private Boolean SignedByPinSetSigner(OCPPv2_1.CSMS.SetVariablesRequest?   Request,
                                             [NotNullWhen(true)]  out String?     Signer,
                                             [NotNullWhen(false)] out String?     Why)
        {

            Signer  = null;
            Why     = null;

            var signers = Connections.PinSetSigners;

            if (Request is null)
            {
                Why = "The request is not known, so who signed it is not either.";
                return false;
            }

            if (signers.Count == 0)
            {
                Why = "This station believes no signer of pin sets yet: one is written down on its Connections page, from commissioning.";
                return false;
            }

            if (!Request.Signatures.Any())
            {
                Why = "A pin set for a connection over SSH is taken only from a SetVariables request signed by a signer this station believes, and this one is not signed.";
                return false;
            }

            var json = Request.ToJSON(true,
                                      cs02.OCPP.CustomSetVariablesRequestSerializer,
                                      cs02.OCPP.CustomSetVariableDataSerializer,
                                      cs02.OCPP.CustomComponentSerializer,
                                      cs02.OCPP.CustomEVSESerializer,
                                      cs02.OCPP.CustomVariableSerializer,
                                      cs02.OCPP.CustomSignatureSerializer,
                                      cs02.OCPP.CustomCustomDataSerializer);

            foreach (var signer in signers)
            {

                var policy = new SignaturePolicy(VerificationRules: [ new VerificationRule(Request.Context,
                                                                                           0,
                                                                                           VerificationRuleActions.VerifyAny,
                                                                                           new KeyPair(signer.Bytes)) ]);

                if (policy.VerifyRequestMessage(Request, json, out _))
                {
                    Signer = signer.Description;
                    return true;
                }

            }

            Why = "None of the request's signatures is a valid one of a signer this station believes.";
            return false;

        }

        #endregion

        #region (private) PinSetTaken(Connection, Learned, Confirmed, Version, Signer)

        /// <summary>
        /// A signed pin set was taken for a connection: its trust is held to the pins at once, and a
        /// probation ends - confirmed, or with the server it learned refused.
        /// </summary>
        private void PinSetTaken(String   Connection,
                                 String?  Learned,
                                 Boolean  Confirmed,
                                 UInt64   Version,
                                 String   Signer)
        {

            var entry = Connections.Connections.FirstOrDefault(entry => entry.Id == Connection);

            if (entry is null)
                return;

            if (sshTrusts.TryGetValue(Connection, out var trust))
                trust.TakePins(entry.SshHostKeys);

            if (probationClocks.TryRemove(Connection, out var clock))
                clock.Cancel();

            var onIt = onProbation.Where(probation => probation.Value.Connection.Id == Connection).ToArray();

            foreach (var probation in onIt)
                onProbation.TryRemove(probation.Key, out _);

            if (Learned is not null && Confirmed)
            {
                Log.Notice($"'{entry.Description}': the signed pin set of version {Version}, signed by '{Signer}', confirms the host key " +
                           $"{Learned} it learned on its first connection; its probation ends.", "ocpp", "connections", "ssh", "security");
                SshSecurityEvent(entry, "ReconfigurationOfSecurityParameters", $"Host key {Learned} of '{entry.Id}' confirmed by a signed pin set of version {Version}");
            }

            else if (Learned is not null)
            {

                Log.Warning($"'{entry.Description}': the signed pin set of version {Version}, signed by '{Signer}', does not have the host key " +
                            $"{Learned} it learned on its first connection - that server is not the one its CSMS knows. The connection is made anew, " +
                            $"held to the signed pins ({entry.SshHostKey}).", "ocpp", "connections", "ssh", "security");

                SshSecurityEvent(entry, "InvalidCsmsHostKey", $"Learned host key {Learned} of '{entry.Id}' is not among the signed pins of version {Version}");

                foreach (var probation in onIt)
                    if (probation.Key is SshMessageConnection current)
                        _ = current.Close(MessageCloseCode.PolicyViolation, "The host key of this server is not among the signed pins.");

            }

            else
                SshSecurityEvent(entry, "ReconfigurationOfSecurityParameters", $"Host keys of '{entry.Id}' set by a signed pin set of version {Version}");

        }

        #endregion

        #region (internal) SshSecurityEvent(Connection, Type, TechInfo)

        /// <summary>
        /// Report a security event of the white paper "OCPP over SSH" (section 9.4) through the OCPP node
        /// of the connection's version - or of both, where it is the station's own.
        /// </summary>
        internal void SshSecurityEvent(ConnectionEntry?  Connection,
                                       String            Type,
                                       String            TechInfo)
        {

            try
            {

                if (Connection is null || Connection.OCPPVersion == OCPP.OCPPVersion.OCPP2_1)
                    _ = cs02.QueueSecurityEventAsync(OCPPv2_1.SecurityEventType.Parse(Type), TechInfo.Length > 255 ? TechInfo[..255] : TechInfo);

                if (Connection is null || Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6)
                    _ = cs01.QueueSecurityEventAsync(OCPPv1_6.SecurityEvent.Parse(Type), TechInfo.Length > 255 ? TechInfo[..255] : TechInfo);

            }
            catch (Exception e)
            {
                Log.Warning($"The security event '{Type}' could not be queued: {e.Message}", "ocpp", "ssh", "security");
            }

        }

        #endregion


        #region (private) CustomTrigger(Request)

        /// <summary>
        /// TriggerMessage of CustomTrigger "SignSSHKey" - "SignSSHKey:SFTP" for the key of SFTP: a new key
        /// is made, and shown to the CSMS in SignSSHKey (section 10.3). The NTS key is the node's, and is
        /// not rotated this way.
        /// </summary>
        private Task<OCPPv2_1.CS.TriggerMessageResponse?> CustomTrigger(OCPPv2_1.CSMS.TriggerMessageRequest Request)
        {

            var trigger = Request.CustomTrigger ?? "";

            if (!trigger.StartsWith("SignSSHKey", StringComparison.Ordinal))
                return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(null);

            var purposeText = trigger.Length > "SignSSHKey".Length && trigger["SignSSHKey".Length] == ':'
                                  ? trigger["SignSSHKey:".Length..]
                                  : trigger == "SignSSHKey" ? "OCPP" : "";

            if (!OCPPv2_1.SSHKeyPurposeExtensions.TryParse(purposeText, out var purpose) || KeyOf(purpose) is null)
                return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                           new OCPPv2_1.CS.TriggerMessageResponse(Request, OCPPv2_1.TriggerMessageStatus.Rejected,
                                                                  new OCPPv2_1.StatusInfo("NotSupported", $"'{trigger}' names no key this station rotates: OCPP or SFTP."))
                       );

            _ = Task.Run(() => RotateSSHKey(purpose, "its CSMS asked for it"));

            return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                       new OCPPv2_1.CS.TriggerMessageResponse(Request, OCPPv2_1.TriggerMessageStatus.Accepted)
                   );

        }

        #endregion

        #region (internal) RotateSSHKey(Purpose, Why)

        /// <summary>
        /// Make the next key of the given purpose - where there is none yet - and show it to the CSMS in
        /// SignSSHKey. The station keeps signing in with the current key until SSHKeySigned confirms the
        /// next one.
        /// </summary>
        internal async Task<OCPPv2_1.CSMS.SignSSHKeyResponse?> RotateSSHKey(OCPPv2_1.SSHKeyPurpose  Purpose,
                                                                           String                  Why)
        {

            if (KeyOf(Purpose) is not SshClientKey key)
                return null;

            try
            {

                await key.MakeIfNeeded();

                var next      = await key.MakePending();
                var response  = await cs02.OCPP.OUT.SignSSHKey(new OCPPv2_1.CS.SignSSHKeyRequest(SourceRouting.To(NetworkingNode_Id.CSMS),
                                                                                                    Purpose,
                                                                                                    next));

                Log.Notice($"The SSH key of {OCPPv2_1.SSHKeyPurposeExtensions.AsText(Purpose)} is rotated, as {Why}: the next one was shown to the CSMS in SignSSHKey, " +
                           $"which answered {OCPPv2_1.GenericStatusExtensions.AsText(response.Status)}{(response.StatusInfo is not null ? $" ({response.StatusInfo.ReasonCode})" : "")}.",
                           "ocpp", "ssh", "security");

                return response;

            }
            catch (Exception e)
            {
                Log.Warning($"The SSH key of {OCPPv2_1.SSHKeyPurposeExtensions.AsText(Purpose)} could not be rotated: {e.Message}", "ocpp", "ssh", "security");
                return null;
            }

        }

        #endregion

        #region (private) RotateIfOld()

        /// <summary>
        /// Rotate every key older than its maximum age, where none is pending yet.
        /// </summary>
        private async Task RotateIfOld()
        {
            foreach (var purpose in new[] { OCPPv2_1.SSHKeyPurpose.OCPP, OCPPv2_1.SSHKeyPurpose.SFTP })
            {
                if (KeyOf(purpose) is SshClientKey key                                       &&
                    key.MaxAgeDays > 0                                                       &&
                    key.MadeAt is DateTimeOffset madeAt                                      &&
                    madeAt + TimeSpan.FromDays(key.MaxAgeDays) < TimeProvider.GetUtcNow()   &&
                    key.PendingPublicKeyLine is null)
                {
                    await RotateSSHKey(purpose, $"it is older than {key.MaxAgeDays} day(s)");
                }
            }
        }

        #endregion

        #region (private) SSHKeySigned(...)

        /// <summary>
        /// The CSMS confirms that a key is enrolled at every server the station dials for its purpose: the
        /// pending one becomes the key, and the old one's private half goes; the current one keeps the
        /// certificate that came with it.
        /// </summary>
        private async Task<OCPPv2_1.CS.SSHKeySignedResponse> SSHKeySigned(DateTimeOffset                        Timestamp,
                                                                          IEventSender                          Sender,
                                                                          IMessageConnection                    Connection,
                                                                          OCPPv2_1.CSMS.SSHKeySignedRequest     Request,
                                                                          CancellationToken                     CancellationToken)
        {

            if (KeyOf(Request.Purpose) is not SshClientKey key)
                return new OCPPv2_1.CS.SSHKeySignedResponse(Request, OCPPv2_1.GenericStatus.Rejected,
                                                            new OCPPv2_1.StatusInfo("NotSupported", $"This station has no key of {OCPPv2_1.SSHKeyPurposeExtensions.AsText(Request.Purpose)} of its own."));

            if (key.PendingPublicKeyLine is String pending && SameKey(pending, Request.PublicKey))
            {

                await key.Promote(Request.Certificate, CancellationToken);

                SshSecurityEvent(null, "ReconfigurationOfSecurityParameters", $"SSH key of {OCPPv2_1.SSHKeyPurposeExtensions.AsText(Request.Purpose)} rotated: {key.Fingerprint}");

                return new OCPPv2_1.CS.SSHKeySignedResponse(Request, OCPPv2_1.GenericStatus.Accepted);

            }

            if (key.PublicKeyLine is String current && SameKey(current, Request.PublicKey))
            {
                key.KeepCertificate(Request.Certificate);
                return new OCPPv2_1.CS.SSHKeySignedResponse(Request, OCPPv2_1.GenericStatus.Accepted);
            }

            return new OCPPv2_1.CS.SSHKeySignedResponse(Request, OCPPv2_1.GenericStatus.Rejected,
                                                        new OCPPv2_1.StatusInfo("UnknownKey", $"It is neither the pending nor the current key of {OCPPv2_1.SSHKeyPurposeExtensions.AsText(Request.Purpose)}."));

        }

        /// <summary>
        /// Whether two lines of authorized_keys name the same key: its type and its base64, whatever the
        /// comment.
        /// </summary>
        private static Boolean SameKey(String Line1, String Line2)

            => String.Join(' ', Line1.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2)) ==
               String.Join(' ', Line2.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2));

        #endregion


        #region (private class) SSHClientSource

        /// <summary>
        /// What SSHClientController reads and writes of this station.
        /// </summary>
        private sealed class SSHClientSource(ChargingStation Station) : OCPPv2_1.ISSHClientControllerSource,
                                                                        OCPPv2_1.ISetVariablesSource
        {

            public IEnumerable<String> Connections

                => [.. Station.Connections.Connections.Where(entry => entry.IsSSH).Select(entry => entry.Id)];

            public IEnumerable<String> ConnectionVariables          => [ "HostKeyPins", "HostKeyPinsVersion", "TrustOnFirstUse", "ProbationTime", "OnPinMismatch", "HostKeyFingerprint", "LearnedHostKey" ];
            public IEnumerable<String> WritableConnectionVariables  => [ "HostKeyPins", "HostKeyPinsVersion", "TrustOnFirstUse", "ProbationTime", "OnPinMismatch" ];
            public IEnumerable<String> Purposes                     => [ "OCPP", "NTS", "SFTP" ];
            public IEnumerable<String> KeyVariables                 => [ "ClientPublicKey", "ClientPendingPublicKey", "ClientCertificate", "ClientKeyMaxAge", "ClientKeyAlgorithms" ];
            public IEnumerable<String> WritableKeyVariables         => [ "ClientKeyMaxAge" ];

            public String? ConnectionValue(String Connection, String Variable)
            {

                if (Station.Connections.Connections.FirstOrDefault(entry => entry.Id == Connection) is not ConnectionEntry entry)
                    return null;

                return Variable switch {
                           "HostKeyPins"         => entry.SshHostKey ?? "",
                           "HostKeyPinsVersion"  => entry.SshHostKeysVersion.ToString(),
                           "TrustOnFirstUse"     => entry.SshTrustOnFirstUse.ToString(),
                           "ProbationTime"       => ((Int64) entry.SshProbationTime.TotalSeconds).ToString(),
                           "OnPinMismatch"       => entry.SshOnPinMismatch.ToString(),
                           "HostKeyFingerprint"  => Station.sshTrusts.TryGetValue(Connection, out var trust) ? trust.Shown ?? "" : "",
                           "LearnedHostKey"      => entry.SshLearnedHostKey ?? "",
                           _                     => null
                       };

            }

            public String? KeyValue(String Variable, String? Purpose)
            {

                if (Variable == "ClientKeyAlgorithms")
                    return "ssh-ed25519";

                if (!OCPPv2_1.SSHKeyPurposeExtensions.TryParse(Purpose ?? "", out var purpose))
                    return null;

                // The NTS key is the node's: shown, and neither rotated nor aged here.
                if (Station.KeyOf(purpose) is not SshClientKey key)
                    return Variable == "ClientPublicKey" ? Station.NTSKEClientPublicKey ?? "" : Variable == "ClientKeyMaxAge" ? "0" : "";

                return Variable switch {
                           "ClientPublicKey"         => key.PublicKeyLine ?? Made(key),
                           "ClientPendingPublicKey"  => key.PendingPublicKeyLine ?? "",
                           "ClientCertificate"       => key.Certificate ?? "",
                           "ClientKeyMaxAge"         => key.MaxAgeDays.ToString(),
                           _                         => null
                       };

            }

            /// <summary>
            /// A key is made at the first read of its public half, as the paper says.
            /// </summary>
            private static String Made(SshClientKey Key)
            {
                try
                {
                    Key.MakeIfNeeded().GetAwaiter().GetResult();
                    return Key.PublicKeyLine ?? "";
                }
                catch
                {
                    return "";
                }
            }

            public async Task<IReadOnlyList<OCPPv2_1.SetVariableResult>> SetVariablesAsync(IReadOnlyList<OCPPv2_1.SetVariableEntry>  Entries,
                                                                                           OCPPv2_1.DataConsistencyModel             DataConsistencyModel,
                                                                                           CancellationToken                         CancellationToken)
            {

                var results = new OCPPv2_1.SetVariableResult[Entries.Count];

                OCPPv2_1.SetVariableResult Result(Int32 Index, OCPPv2_1.SetVariableStatus Status, String? ReasonCode = null, String? AdditionalInfo = null)

                    => results[Index] = new (
                                            AttributeStatus:      Status,
                                            Component:            Entries[Index].Data.Component,
                                            Variable:             Entries[Index].Data.Variable,
                                            AttributeType:        Entries[Index].Variable.Attributes?.Type ?? OCPPv2_1.AttributeTypes.Actual,
                                            AttributeStatusInfo:  ReasonCode is not null ? new OCPPv2_1.StatusInfo(ReasonCode, AdditionalInfo) : null
                                        );

                foreach (var group in Enumerable.Range(0, Entries.Count).GroupBy(index => (Entries[index].Component as OCPPv2_1.SSHClientController)?.Connection))
                {

                    #region The station's keys: how old one may get

                    if (group.Key is null)
                    {
                        foreach (var index in group)
                        {

                            var entry = Entries[index];

                            if (entry.Variable.Name != "ClientKeyMaxAge")
                                Result(index, OCPPv2_1.SetVariableStatus.Rejected, "ReadOnly", $"SSHClientController.{entry.Variable.Name} is read-only!");

                            else if (!OCPPv2_1.SSHKeyPurposeExtensions.TryParse(entry.Data.Variable.Instance ?? "", out var purpose) ||
                                     Station.KeyOf(purpose) is not SshClientKey key)
                                Result(index, OCPPv2_1.SetVariableStatus.Rejected, "NotSupported", "Only the keys of OCPP and SFTP are this station's own; the NTS key is the node's.");

                            else if (!UInt32.TryParse(entry.Data.AttributeValue, out var days))
                                Result(index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", $"'{entry.Data.AttributeValue}' is no number of days.");

                            else
                            {
                                key.MaxAgeDays = days;
                                Result(index, OCPPv2_1.SetVariableStatus.Accepted);
                            }

                        }
                        continue;
                    }

                    #endregion

                    #region A connection: only from a pin set a signer this station believes signed

                    var connection = group.Key;

                    if (!Station.SignedByPinSetSigner(Entries[group.First()].Request, out var signer, out var why))
                    {
                        foreach (var index in group)
                            Result(index, OCPPv2_1.SetVariableStatus.Rejected, "Unsigned", why);
                        continue;
                    }

                    var values = new Dictionary<String, (Int32 Index, String Value)>(StringComparer.OrdinalIgnoreCase);

                    foreach (var index in group)
                    {
                        if (!OCPPv2_1.SSHClientController.TryCheck(connection, Entries[index].Variable.Name, Entries[index].Data.AttributeValue, out var refusal))
                            results[index] = new (OCPPv2_1.SetVariableStatus.Rejected, Entries[index].Data.Component, Entries[index].Data.Variable,
                                                  Entries[index].Variable.Attributes?.Type ?? OCPPv2_1.AttributeTypes.Actual, refusal);
                        else
                            values[Entries[index].Variable.Name] = (index, Entries[index].Data.AttributeValue);
                    }

                    var hasPins     = values.TryGetValue("HostKeyPins",        out var pins);
                    var hasVersion  = values.TryGetValue("HostKeyPinsVersion", out var version);

                    if (hasPins != hasVersion)
                    {
                        foreach (var one in new[] { pins, version }.Where(one => one.Value is not null))
                            Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue",
                                   "A pin set is HostKeyPins and HostKeyPinsVersion in one request: the version is what keeps an older one from coming back.");
                    }

                    else if (hasPins)
                    {

                        if (!UInt64.TryParse(version.Value, out var versionNumber))
                        {
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", $"'{version.Value}' is no version.");
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", "The pin set has no version.");
                        }

                        else if (!Station.Connections.TryTakeSignedHostKeys(connection, pins.Value, versionNumber, out var learned, out var confirmed, out var refused))
                        {
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", refused);
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", refused);
                        }

                        else
                        {
                            Station.PinSetTaken(connection, learned, confirmed, versionNumber, signer);
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Accepted);
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Accepted);
                        }

                    }

                    var trustEntries = new[] { "TrustOnFirstUse", "ProbationTime", "OnPinMismatch" }.
                                           Where (name => values.ContainsKey(name)).
                                           Select(name => (Name: name, values[name].Index, values[name].Value)).
                                           ToArray();

                    if (trustEntries.Length > 0)
                    {

                        var settings = new SshTrustSettings(trustEntries.FirstOrDefault(one => one.Name == "TrustOnFirstUse").Value,
                                                            trustEntries.FirstOrDefault(one => one.Name == "OnPinMismatch").Value,
                                                            trustEntries.FirstOrDefault(one => one.Name == "ProbationTime").Value is String seconds && Int64.TryParse(seconds, out var s) ? s : null);

                        var changed  = Station.Connections.TryChangeSshTrust(connection, settings, out var notChanged);

                        foreach (var one in trustEntries)
                            if (changed)
                                Result(one.Index, OCPPv2_1.SetVariableStatus.Accepted);
                            else
                                Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", notChanged);

                        if (changed)
                            Station.SshSecurityEvent(Station.Connections.Connections.FirstOrDefault(entry => entry.Id == connection),
                                                     "ReconfigurationOfSecurityParameters", $"How '{connection}' believes its server, set by a signed pin set");

                    }

                    #endregion

                }

                await Task.CompletedTask;

                return results;

            }

        }

        #endregion

    }

}
