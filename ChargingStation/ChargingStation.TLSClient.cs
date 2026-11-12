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

using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.Messaging;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What the white paper "Key Pins and TLS 1.3 Raw Public Keys for Local Controllers" asks of the
    /// station's Device Model (work package K4): its TLS client in TLSClientController, an instance for
    /// every wss:// connection and one for its own key (sections 7.1 and 7.2), and pin sets taken from a
    /// SetVariables request a signer it believes signed (section 5.7).
    /// </summary>
    /// <remarks>
    /// What ChargingStation.SSHClient.cs does for the SSH client, done for the TLS client: the same
    /// signers, the same signature check - without a time window for a pin set, whose version keeps an
    /// older one from coming back - and the store and the probation of K2 behind it
    /// (ChargingStation.KeyPins.cs).
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        private readonly Object tlsClientComponentsLock = new ();

        /// <summary>
        /// How long what waits for the response to a SetVariables request waits at most: a response that
        /// could not be sent at all says nothing of it.
        /// </summary>
        private static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);

        // The SetVariables requests being answered, until their response is out:
        // what a signed pin set or a change of trust ends - a connection dropped,
        // or dialled again - waits for it, since it is the connection the answer
        // goes over.
        private readonly ConditionalWeakTable<OCPPv2_1.CSMS.SetVariablesRequest, TaskCompletionSource>  answering    = new ();

        // The connections whose server is not believed any more and which are
        // about to be closed, with why: the gate refuses everything over them
        // meanwhile, as it did while they were on probation.
        private readonly ConditionalWeakTable<IMessageConnection, String>                                notBelieved  = new ();

        #endregion


        #region (internal) FollowTLSClientComponents(Node)

        /// <summary>
        /// Give the OCPP 2.1 node a TLSClientController for the station's own key and one for every
        /// connection over TLS there is now, and none for one there is not.
        /// </summary>
        /// <remarks>
        /// Told by the store after every change it wrote, before the station dials what changed, so that a
        /// connection dialled again has its instance by then. A failure is said in the log and kept here:
        /// the dialling told after this must not miss the change for it.
        /// </remarks>
        internal void FollowTLSClientComponents(OCPPv2_1.CS.TestChargingStationNode? Node)
        {

            if (Node is null)
                return;

            try
            {

                lock (tlsClientComponentsLock)
                {

                    var source       = new TLSClientSource(this);
                    var connections  = source.Connections.ToArray();
                    var shown        = Node.GetComponentConfigs(nameof(OCPPv2_1.TLSClientController)).OfType<OCPPv2_1.TLSClientController>().ToArray();

                    foreach (var instance in shown)
                        if (instance.Connection is not null && !connections.Contains(instance.Connection, StringComparer.Ordinal))
                            Node.RemoveComponent(instance.Name, instance.Instance);

                    if (!shown.Any(instance => instance.Connection is null))
                        Node.AddComponent(new OCPPv2_1.TLSClientController(source));

                    foreach (var connection in connections)
                        if (!shown.Any(instance => instance.Connection == connection))
                            Node.AddComponent(new OCPPv2_1.TLSClientController(source, connection));

                }

            }
            catch (Exception e)
            {
                Log.Warning($"The TLSClientController instances could not follow the connections: {e.Message}", "ocpp", "connections", "keypins");
            }

        }

        #endregion

        #region (internal) AttachTLSClient(Node)

        /// <summary>
        /// What the OCPP 2.1 node answers of the TLS client: TLSClientController, and when the answer to
        /// a SetVariables request is out.
        /// </summary>
        internal void AttachTLSClient(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            FollowTLSClientComponents(Node);

            Node.OCPP.OUT.OnSetVariablesResponseSent += (timestamp, sender, connection, request, response, runtime, sentMessageResult, cancellationToken) => {

                if (answering.TryGetValue(request, out var answered))
                    answered.TrySetResult();

                return Task.CompletedTask;

            };

        }

        #endregion

        #region (private) Answered(Request)

        /// <summary>
        /// Done once the response to the given SetVariables request is out - sent, or found not to be
        /// sendable - and after <see cref="AnswerWait"/> at most; at once where there is no request.
        /// </summary>
        /// <remarks>
        /// What drops the connection a pin set or a change of trust came over waits for this: dropped
        /// before, the connection takes the answer with it, and the CSMS never learns that its change was
        /// taken.
        /// </remarks>
        private Task Answered(OCPPv2_1.CSMS.SetVariablesRequest? Request)
        {

            if (Request is null)
                return Task.CompletedTask;

            var answered = answering.GetValue(Request, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

            return Task.WhenAny(answered.Task, Task.Delay(AnswerWait));

        }

        #endregion

        #region (private) ClientKeyPins()

        /// <summary>
        /// The key pins of the TLS client keys this station presents: those of the client certificates its
        /// wss:// connections name, the oldest connection's first, each once.
        /// </summary>
        /// <remarks>
        /// A station of this kind has no one key of its own: each connection names the certificate it
        /// shows (certificateId, which the paper calls clientKeyId, section 7.1). Normally that is one
        /// certificate for all of them; during a renewal, or with back ends of several operators, it is
        /// more, and a Local Controller must know the one each connection to it shows. So every one is
        /// said, comma separated as ServerKeyPins are, the oldest connection's - the one in charge, where it
        /// dials by itself - first. A connection that names no certificate, or one whose certificate has
        /// not come back yet, shows no key, and adds none.
        /// </remarks>
        private IEnumerable<KeyPin> ClientKeyPins()
        {

            var certificates = ClientCertificates.Entries.ToDictionary(entry => entry.Id);
            var pins         = new List<KeyPin>();

            foreach (var connection in Connections.Connections.Where(entry => entry.IsSecure && entry.CertificateId is not null).OrderBy(entry => entry.CreatedAt))
            {

                if (!certificates.TryGetValue(connection.CertificateId!, out var certificate) || certificate.Certificate is null)
                    continue;

                try
                {
                    var pin = KeyPin.FromCertificate(certificate.Certificate);
                    if (!pins.Contains(pin))
                        pins.Add(pin);
                }
                catch
                {
                    // A key this runtime cannot read is no key it presents either.
                }

            }

            return pins;

        }

        #endregion

        #region (private) KeyPinTrustChanged(Connection, Forgotten, KeyPinnedChanged, Signer, AfterAnswer)

        /// <summary>
        /// A signed SetVariables changed how a wss:// connection believes its server beyond its pins. Where
        /// that forgot a key pin learned on probation - trust on first use switched off - the probation ends
        /// with it: nothing believes that key any more. Where the connection is key-pinned now and was not,
        /// or the other way round, it is dialled again, once the answer is out.
        /// </summary>
        /// <remarks>
        /// A connection made with a key that is no longer believed is refused everything until it is hung
        /// up - the gate let nothing but a pin set through while it was on probation, and would let
        /// everything through once it is not. It is hung up by being dialled again: as one that is no longer
        /// key-pinned, it is checked by its CA.
        /// </remarks>
        /// <param name="Connection">The connection.</param>
        /// <param name="Forgotten">The key pin learned on probation that went with trust on first use; null where none did.</param>
        /// <param name="KeyPinnedChanged">Whether it has to be dialled again.</param>
        /// <param name="Signer">Who signed the change, for the log.</param>
        /// <param name="AfterAnswer">Done once the answer to the change is out.</param>
        private void KeyPinTrustChanged(String   Connection,
                                        KeyPin?  Forgotten,
                                        Boolean  KeyPinnedChanged,
                                        String   Signer,
                                        Task     AfterAnswer)
        {

            if (Connections.CopyOf(Connection) is not ConnectionEntry entry)
                return;

            if (Forgotten is not null)
            {

                if (keyPinTrusts.TryGetValue(Connection, out var trust))
                    trust.Forget();

                if (probationClocks.TryRemove(Connection, out var clock))
                    clock.Cancel();

                foreach (var probation in onProbation.Where(probation => probation.Value.Connection.Id == Connection).ToArray())
                {
                    notBelieved.AddOrUpdate(probation.Key, $"This station no longer believes its server by the key pin {Forgotten} it learned on its first " +
                                                            "connection: trust on first use was switched off, and the connection is made anew.");
                    onProbation.TryRemove(probation.Key, out _);
                }

                Log.Warning($"'{entry.Description}': a signed SetVariables, signed by '{Signer}', switched trust on first use off, so the key pin " +
                            $"{Forgotten} it learned on its first connection is forgotten and the connection is made anew, checked by its CA.",
                            "ocpp", "connections", "keypins", "security");

            }

            KeyPinSecurityEvent(entry, "ReconfigurationOfSecurityParameters",
                                $"How endpoint '{entry.Id}' ({entry.URL}) believes its server, set by a signed SetVariables" +
                                (Forgotten is not null ? $"; learned key pin {Forgotten} forgotten" : ""));

            if (KeyPinnedChanged)
                _ = Task.Run(async () => {
                        await AfterAnswer;
                        Connections.DialAgain(Connection);
                    });

        }

        #endregion


        #region (private class) TLSClientSource

        /// <summary>
        /// What TLSClientController reads and writes of this station.
        /// </summary>
        private sealed class TLSClientSource(ChargingStation Station) : OCPPv2_1.ITLSClientControllerSource,
                                                                        OCPPv2_1.ISetVariablesSource
        {

            /// <summary>
            /// The settings of how a connection believes its server beyond its pins, as the paper names them.
            /// </summary>
            private static readonly String[] trustVariables = [ "RawPublicKeys", "TrustOnFirstUse", "ProbationTime" ];

            public IEnumerable<String> Connections

                => [.. Station.Connections.Connections.Where(entry => entry.IsSecure).Select(entry => entry.Id)];

            public IEnumerable<String> ConnectionVariables          => [ "ServerKeyPins", "ServerKeyPinsVersion", "ServerKeyPinState", "RawPublicKeys", "TrustOnFirstUse",
                                                                         "ProbationTime", "LearnedKeyPin", "KeyPinShown", "URL" ];
            public IEnumerable<String> WritableConnectionVariables  => [ "ServerKeyPins", "ServerKeyPinsVersion", "RawPublicKeys", "TrustOnFirstUse", "ProbationTime" ];
            public IEnumerable<String> KeyVariables                 => [ "ClientKeyPin" ];

            public String? ConnectionValue(String Connection, String Variable)
            {

                if (Station.Connections.CopyOf(Connection) is not ConnectionEntry entry || !entry.IsSecure)
                    return null;

                var trust    = Station.KeyPinTrustOf(Connection);

                // One learned while the file could not be written is held by its
                // trust alone, and on probation all the same.
                var learned  = entry.ServerKeyPinLearned ?? trust?.Learned;

                return Variable switch {
                           "ServerKeyPins"         => String.Join(",", entry.ServerKeyPins.Select(pin => pin.ToHex())),
                           "ServerKeyPinsVersion"  => entry.ServerKeyPinsVersion.ToString(),
                           "ServerKeyPinState"     => (entry.ServerKeyPins.Count == 0 && learned is not null ? KeyPinState.Learned : entry.ServerKeyPinState).ToString(),
                           "RawPublicKeys"         => entry.RawPublicKeys.ToString(),
                           "TrustOnFirstUse"       => entry.TrustOnFirstUse.ToString(),
                           "ProbationTime"         => ((Int64) entry.ServerKeyPinProbationTime.TotalSeconds).ToString(),
                           "LearnedKeyPin"         => entry.ServerKeyPins.Count == 0 ? learned?.ToHex() ?? "" : "",
                           "KeyPinShown"           => trust?.Shown?.ToHex() ?? "",
                           "URL"                   => WithoutPassword(entry.URL.ToString()),
                           _                       => null
                       };

            }

            public String? KeyValue(String Variable)

                => Variable == "ClientKeyPin"
                       ? String.Join(",", Station.ClientKeyPins().Select(pin => pin.ToHex()))
                       : null;

            /// <summary>
            /// A URL as a CSMS may read it: without a password somebody wrote into it.
            /// </summary>
            private static String WithoutPassword(String URL)

                => Regex.Replace(URL, "^([A-Za-z][A-Za-z0-9+.-]*://[^/@:]*):[^/@]*@", "$1@");

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

                foreach (var group in Enumerable.Range(0, Entries.Count).GroupBy(index => (Entries[index].Component as OCPPv2_1.TLSClientController)?.Connection))
                {

                    #region The station's own key: read-only

                    // The Device Model refuses these before they get here, as the
                    // instance has no source to write them: said again, should one
                    // come all the same.
                    if (group.Key is null)
                    {
                        foreach (var index in group)
                            Result(index, OCPPv2_1.SetVariableStatus.Rejected, "ReadOnly", $"TLSClientController.{Entries[index].Variable.Name} is read-only!");
                        continue;
                    }

                    #endregion

                    #region A connection: only from a pin set a signer this station believes signed

                    var connection = group.Key;

                    if (Station.Connections.CopyOf(connection) is not ConnectionEntry entry || !entry.IsSecure)
                    {
                        foreach (var index in group)
                            Result(index, OCPPv2_1.SetVariableStatus.UnknownComponent, "NotFound", $"This station has no wss:// connection '{connection}' any more.");
                        continue;
                    }

                    var request = Entries[group.First()].Request;

                    if (!Station.SignedByPinSetSigner(request, entry, WithoutTimeWindow: true, out var signer, out var why))
                    {
                        foreach (var index in group)
                            Result(index, OCPPv2_1.SetVariableStatus.Rejected, "Unsigned", why);
                        continue;
                    }

                    var values = new Dictionary<String, (Int32 Index, String Value)>(StringComparer.OrdinalIgnoreCase);

                    foreach (var index in group)
                    {
                        if (!OCPPv2_1.TLSClientController.TryCheck(connection, Entries[index].Variable.Name, Entries[index].Data.AttributeValue, out var refusal))
                            results[index] = new (OCPPv2_1.SetVariableStatus.Rejected, Entries[index].Data.Component, Entries[index].Data.Variable,
                                                  Entries[index].Variable.Attributes?.Type ?? OCPPv2_1.AttributeTypes.Actual, refusal);
                        else
                            values[Entries[index].Variable.Name] = (index, Entries[index].Data.AttributeValue);
                    }

                    #endregion

                    #region The pin set: ServerKeyPins and ServerKeyPinsVersion, together

                    var hasPins     = values.TryGetValue("ServerKeyPins",        out var pins);
                    var hasVersion  = values.TryGetValue("ServerKeyPinsVersion", out var version);

                    if (hasPins != hasVersion)
                    {
                        foreach (var one in new[] { pins, version }.Where(one => one.Value is not null))
                            Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue",
                                   "A pin set is ServerKeyPins and ServerKeyPinsVersion in one request: the version is what keeps an older one from coming back.");
                    }

                    else if (hasPins)
                    {

                        if (!UInt64.TryParse(version.Value, out var versionNumber))
                        {
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", $"'{version.Value}' is no version.");
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", "The pin set has no version.");
                        }

                        else if (!Station.TryTakeSignedServerKeyPins(connection, pins.Value, versionNumber, signer, out var refused, Station.Answered(request)))
                        {
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", refused);
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", refused);
                        }

                        else
                        {
                            Result(pins.Index,    OCPPv2_1.SetVariableStatus.Accepted);
                            Result(version.Index, OCPPv2_1.SetVariableStatus.Accepted);
                        }

                    }

                    #endregion

                    #region How it believes its server beyond its pins: within the time window

                    var trustEntries = trustVariables.
                                           Where (name => values.ContainsKey(name)).
                                           Select(name => (Name: name, values[name].Index, values[name].Value)).
                                           ToArray();

                    if (trustEntries.Length > 0)
                    {

                        // No version keeps an older one of these from coming back,
                        // so the signature's time does, by this station's clock: a
                        // station without a set clock takes them with a pin set's
                        // help only - from the page, or once its clock is set.
                        if (!Station.SignedByPinSetSigner(request, entry, WithoutTimeWindow: false, out _, out var notNow))
                        {
                            foreach (var one in trustEntries)
                                Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "OutOfTime", notNow);
                            continue;
                        }

                        // .NET's SslStream offers no raw public key: a connection that
                        // requires them is not dialled, and a CSMS that asked for it
                        // from afar would have cut the station off from this server.
                        var require = trustEntries.Where(one => one.Name == "RawPublicKeys" &&
                                                                String.Equals(one.Value?.Trim(), nameof(RawPublicKeyMode.Require), StringComparison.OrdinalIgnoreCase)).
                                                   ToArray();

                        foreach (var one in require)
                            Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "NotSupported",
                                   "This station's TLS stack cannot offer raw public keys yet, and a connection that requires them would not be dialled: " +
                                   "Prefer checks the same key pins in a certificate.");

                        var taken = trustEntries.Except(require).ToArray();

                        if (taken.Length == 0)
                            continue;

                        var settings = new ServerKeyPinSettings(taken.FirstOrDefault(one => one.Name == "RawPublicKeys").  Value,
                                                                taken.FirstOrDefault(one => one.Name == "TrustOnFirstUse").Value,
                                                                taken.FirstOrDefault(one => one.Name == "ProbationTime").  Value is String seconds && Int64.TryParse(seconds, out var s) ? s : null);

                        var changed  = Station.Connections.TryChangeKeyPinTrust(connection, settings, out var forgotten, out var keyPinnedChanged, out var notChanged);

                        foreach (var one in taken)
                            if (changed)
                                Result(one.Index, OCPPv2_1.SetVariableStatus.Accepted);
                            else
                                Result(one.Index, OCPPv2_1.SetVariableStatus.Rejected, "InvalidValue", notChanged);

                        if (changed)
                            Station.KeyPinTrustChanged(connection, forgotten, keyPinnedChanged, signer, Station.Answered(request));

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
