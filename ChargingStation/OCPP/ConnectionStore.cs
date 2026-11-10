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

using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using cloud.charging.open.protocols.WWCP.Node.Logging;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// The places this charging station dials, and the credentials it proves
    /// itself with when it gets there.
    /// </summary>
    /// <remarks>
    /// Two lists in one place, because the thing that has to hold is the
    /// reference between them: a connection names its credentials rather than
    /// repeating them, so that a password written down once is a password
    /// changed once. Splitting them into two stores would put that reference
    /// across a seam where nothing checks it, and the first thing anybody
    /// would do is delete a login that three connections were using.
    ///
    /// Two files rather than one, and for a reason that is not tidiness: the
    /// credentials hold secrets and are written so that only their owner may
    /// read them, and the connections hold none and are ordinary. A single
    /// file would have to be treated as secret throughout, which makes it
    /// harder to hand somebody the half that is safe to hand over.
    ///
    /// <b>The secrets never come back out.</b> A password or a shared secret
    /// goes in and is written to disk; what the web interface is told is
    /// whether one is set. Changing a record with the secret field left empty
    /// keeps the secret that is already there, which is what lets somebody fix
    /// a typo in a description without being shown - or having to retype - the
    /// password.
    /// </remarks>
    public sealed class ConnectionStore
    {

        #region Data

        /// <summary>
        /// The directory beside the configuration file, when nobody says
        /// otherwise.
        /// </summary>
        public const String  DefaultDirectoryName    = "ocpp-connections";

        /// <summary>
        /// The file the credentials live in - owner-readable only.
        /// </summary>
        public const String  AuthenticationsFileName = "authentications.json";

        /// <summary>
        /// The file the connections live in.
        /// </summary>
        public const String  ConnectionsFileName     = "connections.json";

        private readonly Object                                   updateLock       = new ();
        private readonly Dictionary<String, AuthenticationEntry>  authentications  = [];
        private readonly Dictionary<String, ConnectionEntry>      connections      = [];

        #endregion

        #region Properties

        /// <summary>
        /// The directory the two files live in.
        /// </summary>
        public String        Path                  { get; }

        /// <summary>
        /// The clock that stamps a record when it is written down.
        /// </summary>
        public TimeProvider  TimeProvider          { get; }

        /// <summary>
        /// Which client certificates exist, for the connections that name one.
        /// </summary>
        /// <remarks>
        /// A function rather than a list, because the answer changes while
        /// this store is alive - somebody may remove a certificate on the page
        /// next door - and because this store has no business holding on to
        /// the certificate store itself. Left unset, a connection naming a
        /// certificate is taken at its word; the station sets it.
        /// </remarks>
        public Func<IEnumerable<String>>? KnownCertificateIds { get; set; }

        /// <summary>
        /// Every set of credentials, newest first.
        /// </summary>
        public IReadOnlyList<AuthenticationEntry> Authentications
        {
            get
            {
                lock (updateLock)
                    return [.. authentications.Values.OrderByDescending(entry => entry.CreatedAt)];
            }
        }

        /// <summary>
        /// Every connection, newest first.
        /// </summary>
        public IReadOnlyList<ConnectionEntry> Connections
        {
            get
            {
                lock (updateLock)
                {
                    CheckEverything();
                    return [.. connections.Values.OrderByDescending(entry => entry.CreatedAt)];
                }
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Something happened here that belongs in the log of the station: a
        /// connection written down, a login removed, a file that could not be
        /// read.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The connections and credentials in the given directory; it need not
        /// exist yet.
        /// </summary>
        public ConnectionStore(String?        Path           = null,
                               TimeProvider?  TimeProvider   = null)
        {

            this.Path          = System.IO.Path.GetFullPath(Path ?? DefaultDirectoryName);
            this.TimeProvider  = TimeProvider ?? System.TimeProvider.System;

            Reload();

        }

        #endregion


        #region Reload()

        /// <summary>
        /// Read both files again, from nothing.
        /// </summary>
        /// <remarks>
        /// A record that cannot be read is skipped with a line in the log
        /// rather than thrown: one connection written down wrongly must not
        /// stop a station that has another which works, and the one that works
        /// is frequently the one keeping it reachable.
        /// </remarks>
        public void Reload()
        {

            lock (updateLock)
            {

                authentications.Clear();
                connections.    Clear();

                foreach (var json in Read(AuthenticationsFileName))
                {
                    if (AuthenticationEntry.TryParse(json, out var entry, out var problem))
                        authentications[entry.Id] = entry;
                    else
                        OnNotice?.Invoke(LogLevel.Warning, $"A set of credentials could not be read: {problem}");
                }

                foreach (var json in Read(ConnectionsFileName))
                {
                    if (ConnectionEntry.TryParse(json, out var entry, out var problem))
                        connections[entry.Id] = entry;
                    else
                        OnNotice?.Invoke(LogLevel.Warning, $"A connection could not be read: {problem}");
                }

            }

        }

        #endregion

        #region (private) Read(FileName) / TrySaveAuthentications(out Error) / TrySaveConnections(out Error)

        /// <summary>
        /// Called under the lock.
        /// </summary>
        private IEnumerable<JObject> Read(String FileName)
        {

            var file = System.IO.Path.Combine(Path, FileName);

            if (!File.Exists(file))
                return [];

            try
            {
                return JArray.Parse(File.ReadAllText(file)).OfType<JObject>().ToArray();
            }
            catch (Exception e)
            {
                OnNotice?.Invoke(LogLevel.Error, $"'{FileName}' could not be read and is being ignored: {e.Message}");
                return [];
            }

        }

        /// <summary>
        /// The credentials, written out. Called under the lock, once a change
        /// of them is made, and before it is said to be.
        /// </summary>
        /// <remarks>
        /// They hold the secrets, so they are written the way the private keys
        /// next door are - and the mode goes on at creation, not afterwards.
        /// </remarks>
        private Boolean TrySaveAuthentications([NotNullWhen(false)] out String? Error)

            => TrySave(AuthenticationsFileName,
                       new JArray(authentications.Values.
                                      OrderBy(entry => entry.CreatedAt).
                                      Select (entry => entry.ToJSON(IncludeSecrets: true))),
                       true,
                       out Error);

        /// <summary>
        /// The connections, written out. Called under the lock, once a change
        /// of them is made, and before it is said to be.
        /// </summary>
        private Boolean TrySaveConnections([NotNullWhen(false)] out String? Error)

            => TrySave(ConnectionsFileName,
                       new JArray(connections.Values.
                                      OrderBy(entry => entry.CreatedAt).
                                      Select (entry => entry.ToJSON())),
                       false,
                       out Error);

        /// <summary>
        /// One of the two files, written whole: to a file of its own first, and
        /// moved over the old one, so that it is the old one or the new one and
        /// never half of each.
        /// </summary>
        /// <remarks>
        /// Whole files rather than one record at a time: there are a handful
        /// of these, and a rewrite that either happened or did not is easier
        /// to reason about than a directory half-way through being changed.
        /// Only the file that was changed is written. Where it cannot be, the
        /// caller puts the change back: both files were written in place, and
        /// one that could not be threw out of a change already made - the page
        /// was answered 500 by the web server, and the station went on with
        /// what it had not kept until it was started again.
        /// </remarks>
        /// <param name="FileName">Which of the two.</param>
        /// <param name="Entries">What it is to hold.</param>
        /// <param name="OwnerOnly">Whether only its owner may read it.</param>
        /// <param name="Error">Why it was not written.</param>
        private Boolean TrySave(String                            FileName,
                                JArray                            Entries,
                                Boolean                           OwnerOnly,
                                [NotNullWhen(false)] out String?  Error)
        {

            var file       = System.IO.Path.Combine(Path, FileName);
            var temporary  = file + ".tmp";

            try
            {

                Directory.CreateDirectory(Path);

                // Made anew, so that the mode goes on at its creation: one left
                // over from before would keep the mode it was made with.
                File.Delete(temporary);

                if (OwnerOnly)
                    OwnerOnlyFile.Write(temporary, Entries.ToString(Formatting.Indented));

                else
                    File.WriteAllText(temporary, Entries.ToString(Formatting.Indented));

                File.Move(temporary, file, overwrite: true);

                Error = null;
                return true;

            }
            catch (Exception e)
            {

                // What holds the secrets is not left lying about beside them.
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch
                { }

                Error = $"'{file}' could not be written: {e.Message}";
                return false;

            }

        }

        #endregion

        #region (private static) NewId()

        /// <summary>
        /// A fresh identification, which is only ever compared and never read
        /// out loud.
        /// </summary>
        private static String NewId()
            => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

        #endregion


        #region TryAddAuthentication   (Description, Kind, Login, Secret, ..., out Id, out Error [, out NotSaved])

        /// <summary>
        /// Write down one set of credentials.
        /// </summary>
        public Boolean TryAddAuthentication(String?                          Description,
                                            String?                          Kind,
                                            String?                          Login,
                                            String?                          Secret,
                                            [NotNullWhen(true)]  out String? Id,
                                            [NotNullWhen(false)] out String? Error,
                                            Double?                          ValiditySeconds     = null,
                                            UInt32?                          Length              = null,
                                            String?                          Alphabet            = null,
                                            String?                          HashAlgorithm       = null,
                                            Boolean?                         TLSChannelBinding   = null)

            => TryAddAuthentication(Description, Kind, Login, Secret, out Id, out Error, out _,
                                    ValiditySeconds, Length, Alphabet, HashAlgorithm, TLSChannelBinding);

        /// <summary>
        /// Write down one set of credentials - and say whether a refusal was
        /// the file's rather than theirs.
        /// </summary>
        /// <param name="NotSaved">True where the file of the credentials could not be written: nothing about them was wrong, and they were not taken.</param>
        public Boolean TryAddAuthentication(String?                          Description,
                                            String?                          Kind,
                                            String?                          Login,
                                            String?                          Secret,
                                            [NotNullWhen(true)]  out String? Id,
                                            [NotNullWhen(false)] out String? Error,
                                            out Boolean                      NotSaved,
                                            Double?                          ValiditySeconds     = null,
                                            UInt32?                          Length              = null,
                                            String?                          Alphabet            = null,
                                            String?                          HashAlgorithm       = null,
                                            Boolean?                         TLSChannelBinding   = null)
        {

            Id        = null;
            NotSaved  = false;

            if (!TryParseKind(Kind, out var kind, out Error))
                return false;

            if (!AuthenticationEntry.Validate(Description, kind, Login, Secret, Alphabet, Length, out Error))
                return false;

            if (String.IsNullOrWhiteSpace(Secret))
            {
                Error = kind == AuthenticationKind.Basic
                            ? "A password is needed. It can be changed later without being shown again, but it has to be set once."
                            : "A shared secret is needed. It can be changed later without being shown again, but it has to be set once.";
                return false;
            }

            lock (updateLock)
            {

                var entry = new AuthenticationEntry(
                                NewId(),
                                Description!.Trim(),
                                kind,
                                Login!.Trim(),
                                TimeProvider.GetUtcNow()
                            );

                Apply(entry, Secret, ValiditySeconds, Length, Alphabet, HashAlgorithm, TLSChannelBinding);

                authentications[entry.Id] = entry;

                if (!TrySaveAuthentications(out Error))
                {
                    authentications.Remove(entry.Id);
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Credentials written down: {entry}.");

                Id = entry.Id;
                return true;

            }

        }

        #endregion

        #region TryUpdateAuthentication(Id, Description, Kind, Login, Secret, ..., out Error [, out NotSaved])

        /// <summary>
        /// Change one set of credentials.
        /// </summary>
        /// <remarks>
        /// An empty secret keeps whatever is already there. That is the whole
        /// reason this is not a delete and an add: somebody fixing a
        /// description should not have to know the password, and asking them
        /// for it again is how a password ends up written on a sticky note.
        /// </remarks>
        public Boolean TryUpdateAuthentication(String?                          Id,
                                               String?                          Description,
                                               String?                          Kind,
                                               String?                          Login,
                                               String?                          Secret,
                                               [NotNullWhen(false)] out String? Error,
                                               Double?                          ValiditySeconds     = null,
                                               UInt32?                          Length              = null,
                                               String?                          Alphabet            = null,
                                               String?                          HashAlgorithm       = null,
                                               Boolean?                         TLSChannelBinding   = null)

            => TryUpdateAuthentication(Id, Description, Kind, Login, Secret, out Error, out _,
                                       ValiditySeconds, Length, Alphabet, HashAlgorithm, TLSChannelBinding);

        /// <summary>
        /// Change one set of credentials - and say whether a refusal was the
        /// file's rather than the change's.
        /// </summary>
        /// <param name="NotSaved">True where the file of the credentials could not be written: nothing about the change was wrong, and they stay as they were.</param>
        public Boolean TryUpdateAuthentication(String?                          Id,
                                               String?                          Description,
                                               String?                          Kind,
                                               String?                          Login,
                                               String?                          Secret,
                                               [NotNullWhen(false)] out String? Error,
                                               out Boolean                      NotSaved,
                                               Double?                          ValiditySeconds     = null,
                                               UInt32?                          Length              = null,
                                               String?                          Alphabet            = null,
                                               String?                          HashAlgorithm       = null,
                                               Boolean?                         TLSChannelBinding   = null)
        {

            NotSaved = false;

            if (!TryParseKind(Kind, out var kind, out Error))
                return false;

            if (!AuthenticationEntry.Validate(Description, kind, Login, Secret, Alphabet, Length, out Error))
                return false;

            lock (updateLock)
            {

                if (Id is null || !authentications.TryGetValue(Id, out var entry))
                {
                    Error = "There are no credentials here under that identification.";
                    return false;
                }

                // Changing what kind it is means the secret that is there is
                // the wrong kind of secret, so one has to be given.
                if (entry.Kind != kind && String.IsNullOrWhiteSpace(Secret))
                {
                    Error = kind == AuthenticationKind.Basic
                                ? "Changing these to HTTP Basic needs a password: a shared secret is not one."
                                : "Changing these to HTTP TOTP needs a shared secret: a password is not one.";
                    return false;
                }

                var was = entry.Copy();

                entry.Description  = Description!.Trim();
                entry.Login        = Login!.Trim();
                entry.Kind         = kind;

                Apply(entry, Secret, ValiditySeconds, Length, Alphabet, HashAlgorithm, TLSChannelBinding);

                if (!TrySaveAuthentications(out Error))
                {
                    authentications[Id] = was;
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Credentials changed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion

        #region (private) Apply(Entry, Secret, ...) / TryParseKind(Text, out Kind, out Error)

        /// <summary>
        /// The half of a change that is the same whether it is new or not.
        /// Called under the lock.
        /// </summary>
        private static void Apply(AuthenticationEntry  Entry,
                                  String?              Secret,
                                  Double?              ValiditySeconds,
                                  UInt32?              Length,
                                  String?              Alphabet,
                                  String?              HashAlgorithm,
                                  Boolean?             TLSChannelBinding)
        {

            var secret = Secret?.Trim();

            if (Entry.Kind == AuthenticationKind.Basic)
            {

                if (!String.IsNullOrEmpty(secret))
                    Entry.Password = secret;

                // Whatever was here as TOTP is not a password, and keeping it
                // would leave a secret on disk that nothing can use.
                Entry.SharedSecret       = null;
                Entry.Validity           = null;
                Entry.Length             = null;
                Entry.Alphabet           = null;
                Entry.HashAlgorithm      = null;

            }

            else
            {

                if (!String.IsNullOrEmpty(secret))
                    Entry.SharedSecret = secret;

                Entry.Password           = null;

                Entry.Validity           = ValiditySeconds is Double seconds && seconds > 0
                                               ? TimeSpan.FromSeconds(seconds)
                                               : null;

                Entry.Length             = Length;

                Entry.Alphabet           = String.IsNullOrWhiteSpace(Alphabet) ? null : Alphabet.Trim();

                Entry.HashAlgorithm      = HashAlgorithm?.Trim().ToUpperInvariant() switch {
                                               "SHA384"  => TOTPHashAlgorithm.SHA384,
                                               "SHA512"  => TOTPHashAlgorithm.SHA512,
                                               "SHA256"  => TOTPHashAlgorithm.SHA256,
                                               _         => null
                                           };

                // Left out, it is what it was - unbound for credentials written
                // down anew, see AuthenticationEntry.TLSChannelBinding.
                Entry.TLSChannelBinding  = TLSChannelBinding ?? Entry.TLSChannelBinding;

            }

        }

        private static Boolean TryParseKind(String?                          Text,
                                            out AuthenticationKind           Kind,
                                            [NotNullWhen(false)] out String? Error)
        {

            switch (Text?.Trim().ToLowerInvariant())
            {

                case "basic":
                    Kind   = AuthenticationKind.Basic;
                    Error  = null;
                    return true;

                case "totp":
                    Kind   = AuthenticationKind.TOTP;
                    Error  = null;
                    return true;

                default:
                    Kind   = AuthenticationKind.Basic;
                    Error  = $"'{Text}' is not a kind of authentication this station knows.";
                    return false;

            }

        }

        #endregion

        #region TryRemoveAuthentication(Id, out Error [, out NotSaved])

        /// <summary>
        /// Take one set of credentials away, for good.
        /// </summary>
        /// <remarks>
        /// Refused while a connection is using it, and the refusal names the
        /// connection. The alternative - removing it and leaving the
        /// connection pointing at nothing - turns one deliberate act into a
        /// station that cannot dial home and gives nobody a reason why.
        /// </remarks>
        public Boolean TryRemoveAuthentication(String?                          Id,
                                               [NotNullWhen(false)] out String? Error)

            => TryRemoveAuthentication(Id, out Error, out _);

        /// <summary>
        /// Take one set of credentials away - and say whether a refusal was
        /// the file's rather than the request's.
        /// </summary>
        /// <param name="NotSaved">True where the file of the credentials could not be written: nothing about the request was wrong, and they stay.</param>
        public Boolean TryRemoveAuthentication(String?                          Id,
                                               [NotNullWhen(false)] out String? Error,
                                               out Boolean                      NotSaved)
        {

            NotSaved = false;

            lock (updateLock)
            {

                if (Id is null || !authentications.TryGetValue(Id, out var entry))
                {
                    Error = "There are no credentials here under that identification.";
                    return false;
                }

                var used = connections.Values.
                               Where(connection => connection.AuthenticationId == Id).
                               ToArray();

                if (used.Length > 0)
                {
                    Error = $"These credentials are in use by {String.Join(", ", used.Select(one => $"'{one.Description}'"))}. " +
                             "Point those somewhere else first.";
                    return false;
                }

                authentications.Remove(Id);

                if (!TrySaveAuthentications(out Error))
                {
                    authentications[Id] = entry;
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Credentials removed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion


        #region TryAddConnection   (Description, URL, ConnectionType, ..., out Id, out Error [, out NotSaved])

        /// <summary>
        /// Write down one connection.
        /// </summary>
        public Boolean TryAddConnection(String?                          Description,
                                        String?                          URL,
                                        String?                          ConnectionType,
                                        Boolean?                         AutoConnect,
                                        String?                          AuthenticationId,
                                        String?                          CertificateId,
                                        [NotNullWhen(true)]  out String? Id,
                                        [NotNullWhen(false)] out String? Error,
                                        String?                          OCPPVersion   = null,
                                        String?                          SshHostKey    = null,
                                        SshTrustSettings?                SshTrust      = null)

            => TryAddConnection(Description, URL, ConnectionType, AutoConnect, AuthenticationId, CertificateId,
                                out Id, out Error, out _, OCPPVersion, SshHostKey, SshTrust);

        /// <summary>
        /// Write down one connection - and say whether a refusal was the
        /// file's rather than the connection's.
        /// </summary>
        /// <param name="NotSaved">True where the file of the connections could not be written: nothing about this one was wrong, and it was not taken.</param>
        /// <param name="SshHostKey">The host keys the server of an ssh:// connection may show, separated by commas; none where it is not dialled or learns one on first use.</param>
        /// <param name="SshTrust">How an ssh:// connection believes its server beyond its pins; none for no trust on first use, mismatches refused.</param>
        public Boolean TryAddConnection(String?                          Description,
                                        String?                          URL,
                                        String?                          ConnectionType,
                                        Boolean?                         AutoConnect,
                                        String?                          AuthenticationId,
                                        String?                          CertificateId,
                                        [NotNullWhen(true)]  out String? Id,
                                        [NotNullWhen(false)] out String? Error,
                                        out Boolean                      NotSaved,
                                        String?                          OCPPVersion   = null,
                                        String?                          SshHostKey    = null,
                                        SshTrustSettings?                SshTrust      = null)
        {

            Id        = null;
            NotSaved  = false;

            if (!ConnectionEntry.Validate(Description, URL, ConnectionType, OCPPVersion,
                                          out var url, out var type, out var version, out Error))
                return false;

            if (!ConnectionEntry.ValidateHostKey(url, SshHostKey, out var hostKey, out Error))
                return false;

            if (!ConnectionEntry.ValidateSshTrust(SshTrust?.TrustOnFirstUse, SshTrust?.OnPinMismatch, SshTrust?.ProbationSeconds,
                                                  out var trustOnFirstUse, out var onPinMismatch, out var probationTime, out Error))
                return false;

            lock (updateLock)
            {

                if (!CheckReferences(AuthenticationId, CertificateId, url.Scheme == URIScheme.ssh,
                                     out var authenticationId, out var certificateId, out Error))
                    return false;

                var entry = new ConnectionEntry(
                                NewId(),
                                Description!.Trim(),
                                url,
                                type,
                                TimeProvider.GetUtcNow()
                            ) {
                                OCPPVersion         = version,
                                AutoConnect  = AutoConnect ?? false,
                                AuthenticationId    = authenticationId,
                                CertificateId       = certificateId,
                                SshHostKey          = hostKey,
                                SshTrustOnFirstUse  = url.Scheme == URIScheme.ssh ? trustOnFirstUse : SshTrustOnFirstUse.None,
                                SshOnPinMismatch    = url.Scheme == URIScheme.ssh ? onPinMismatch   : SshOnPinMismatch.Refuse,
                                SshProbationTime    = probationTime
                            };

                connections[entry.Id] = entry;

                if (!TrySaveConnections(out Error))
                {
                    connections.Remove(entry.Id);
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Connection written down: {entry}.");

                Id = entry.Id;
                return true;

            }

        }

        #endregion

        #region TryUpdateConnection(Id, Description, URL, ConnectionType, ..., out Error [, out NotSaved])

        /// <summary>
        /// Change one connection.
        /// </summary>
        public Boolean TryUpdateConnection(String?                          Id,
                                           String?                          Description,
                                           String?                          URL,
                                           String?                          ConnectionType,
                                           Boolean?                         AutoConnect,
                                           String?                          AuthenticationId,
                                           String?                          CertificateId,
                                           [NotNullWhen(false)] out String? Error,
                                           String?                          OCPPVersion         = null,
                                           String?                          SshHostKey          = null,
                                           String?                          SshHostKeyAsShown   = null,
                                           SshTrustSettings?                SshTrust            = null)

            => TryUpdateConnection(Id, Description, URL, ConnectionType, AutoConnect, AuthenticationId, CertificateId,
                                   out Error, out _, OCPPVersion, SshHostKey, SshHostKeyAsShown, SshTrust);

        /// <summary>
        /// Change one connection - and say whether a refusal was the file's
        /// rather than the change's.
        /// </summary>
        /// <param name="NotSaved">True where the file of the connections could not be written: nothing about the change was wrong, and the connection stays as it was.</param>
        /// <param name="SshHostKey">The host keys the server of an ssh:// connection may show, separated by commas: "" for none, and nothing to keep those there are.</param>
        /// <param name="SshHostKeyAsShown">The host keys the page showed when it was drawn. Where <paramref name="SshHostKey"/> is still that, those there are now stay: a signed pin set taken meanwhile is not undone by a change of something else.</param>
        /// <param name="SshTrust">How an ssh:// connection believes its server beyond its pins; nothing to keep what it says.</param>
        public Boolean TryUpdateConnection(String?                          Id,
                                           String?                          Description,
                                           String?                          URL,
                                           String?                          ConnectionType,
                                           Boolean?                         AutoConnect,
                                           String?                          AuthenticationId,
                                           String?                          CertificateId,
                                           [NotNullWhen(false)] out String? Error,
                                           out Boolean                      NotSaved,
                                           String?                          OCPPVersion         = null,
                                           String?                          SshHostKey          = null,
                                           String?                          SshHostKeyAsShown   = null,
                                           SshTrustSettings?                SshTrust            = null)
        {

            NotSaved = false;

            if (!ConnectionEntry.Validate(Description, URL, ConnectionType, OCPPVersion,
                                          out var url, out var type, out var version, out Error))
                return false;

            // Whether the host key is to stay as it is: left out, or sent back
            // as the page showed it.
            var keepsHostKey = SshHostKey is null ||
                               (SshHostKeyAsShown is not null &&
                                ConnectionEntry.Named(SshHostKey) == ConnectionEntry.Named(SshHostKeyAsShown));

            String? hostKey = null;

            if (!keepsHostKey && !ConnectionEntry.ValidateHostKey(url, SshHostKey, out hostKey, out Error))
                return false;

            var trustOnFirstUse  = SshTrustOnFirstUse.None;
            var onPinMismatch    = SshOnPinMismatch.Refuse;
            var probationTime    = ConnectionEntry.DefaultSshProbationTime;

            if (SshTrust is not null &&
                !ConnectionEntry.ValidateSshTrust(SshTrust.TrustOnFirstUse, SshTrust.OnPinMismatch, SshTrust.ProbationSeconds,
                                                  out trustOnFirstUse, out onPinMismatch, out probationTime, out Error))
                return false;

            lock (updateLock)
            {

                if (Id is null || !connections.TryGetValue(Id, out var entry))
                {
                    Error = "There is no connection here under that identification.";
                    return false;
                }

                if (!CheckReferences(AuthenticationId, CertificateId, url.Scheme == URIScheme.ssh,
                                     out var authenticationId, out var certificateId, out Error))
                    return false;

                // A host key kept goes with a connection that is no longer
                // ssh://: there is nothing left that would check it.
                if (keepsHostKey)
                    hostKey = url.Scheme == URIScheme.ssh ? entry.SshHostKey : null;

                var was = entry.Copy();

                entry.Description         = Description!.Trim();
                entry.URL                 = url;
                entry.ConnectionType      = type;
                entry.OCPPVersion         = version;
                entry.AutoConnect  = AutoConnect ?? false;
                entry.AuthenticationId    = authenticationId;
                entry.CertificateId       = certificateId;

                // A key learned on probation goes with the pins somebody wrote,
                // and with a server elsewhere; so does the version of the
                // signed pin set the pins came from.
                if (!keepsHostKey || was.URL != url)
                {
                    entry.SshLearnedHostKey   = null;
                    entry.SshLearnedAt        = null;
                }

                if (!keepsHostKey)
                    entry.SshHostKeysVersion  = 0;

                entry.SshHostKey          = hostKey;

                if (url.Scheme != URIScheme.ssh)
                {
                    entry.SshTrustOnFirstUse  = SshTrustOnFirstUse.None;
                    entry.SshOnPinMismatch    = SshOnPinMismatch.Refuse;
                    entry.SshLearnedHostKey   = null;
                    entry.SshLearnedAt        = null;
                }
                else if (SshTrust is not null)
                {
                    entry.SshTrustOnFirstUse  = trustOnFirstUse;
                    entry.SshOnPinMismatch    = onPinMismatch;
                    entry.SshProbationTime    = probationTime;
                }

                if (!TrySaveConnections(out Error))
                {
                    connections[Id] = was;
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Connection changed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion

        #region TryLearnHostKey(Id, URL, HostKey, out Error)

        /// <summary>
        /// Write down the host key the first connection of an ssh:// connection
        /// that trusts on first use was shown: on probation, until a signed pin
        /// set confirms it (white paper "OCPP over SSH", section 10.4).
        /// </summary>
        /// <remarks>
        /// Only where nothing changed meanwhile: the connection is still there,
        /// still goes where it went when it was dialled, still trusts on first
        /// use, and pins and learned no host key yet. Written down rather than
        /// only remembered, so that a restart does not learn again from whoever
        /// answers first - and so that the probation runs on.
        /// </remarks>
        /// <param name="Id">The connection.</param>
        /// <param name="URL">Where it went when it was dialled.</param>
        /// <param name="HostKey">The host key it was shown, as a fingerprint.</param>
        /// <param name="Error">Why it was not written down - nothing where it was not to be.</param>
        /// <returns>Whether it was written down.</returns>
        public Boolean TryLearnHostKey(String       Id,
                                       URL          URL,
                                       String       HostKey,
                                       out String?  Error)
        {

            Error = null;

            lock (updateLock)
            {

                if (!connections.TryGetValue(Id, out var entry)                  ||
                    entry.URL != URL                                             ||
                    entry.URL.Scheme != URIScheme.ssh                            ||
                    entry.SshTrustOnFirstUse != SshTrustOnFirstUse.HostKey       ||
                    entry.SshHostKey is not null                                 ||
                    entry.SshLearnedHostKey is not null)
                {
                    return false;
                }

                entry.SshLearnedHostKey  = HostKey;
                entry.SshLearnedAt       = TimeProvider.GetUtcNow();

                if (!TrySaveConnections(out Error))
                {
                    entry.SshLearnedHostKey  = null;
                    entry.SshLearnedAt       = null;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice,
                                 $"'{entry.Description}' pins no host key and trusts on first use, so the one its server showed on the first " +
                                 $"connection was learned: {HostKey}. It is on probation for {entry.SshProbationTime.TotalMinutes:0.#} minute(s), " +
                                  "until a signed pin set confirms it; until then nothing but such a pin set is executed.");

                return true;

            }

        }

        #endregion

        #region TryForgetLearnedHostKey(Id, HostKey)

        /// <summary>
        /// Forget the host key learned on first use - after a probation that
        /// ran out without a signed pin set - so that the next connection
        /// learns again.
        /// </summary>
        /// <param name="Id">The connection.</param>
        /// <param name="HostKey">The host key learned, so that one learned again meanwhile is not forgotten.</param>
        /// <returns>Whether it was forgotten.</returns>
        public Boolean TryForgetLearnedHostKey(String  Id,
                                               String  HostKey)
        {

            lock (updateLock)
            {

                if (!connections.TryGetValue(Id, out var entry) || entry.SshLearnedHostKey != HostKey)
                    return false;

                var learnedAt = entry.SshLearnedAt;

                entry.SshLearnedHostKey  = null;
                entry.SshLearnedAt       = null;

                if (!TrySaveConnections(out var error))
                {
                    entry.SshLearnedHostKey  = HostKey;
                    entry.SshLearnedAt       = learnedAt;
                    OnNotice?.Invoke(LogLevel.Warning, $"'{entry.Description}' could not forget the host key it learned: {error}.");
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Warning,
                                 $"'{entry.Description}' forgot the host key {HostKey} it learned on its first connection: no signed pin set " +
                                  "confirmed it in time. The next connection learns again.");

                return true;

            }

        }

        #endregion

        #region TryTakeSignedHostKeys(Id, HostKeys, Version, out Confirmed, out Error)

        /// <summary>
        /// Take the host keys of a signed pin set as the connection's pins: only
        /// with a higher version than the one it holds, so that an older pin
        /// set cannot be brought back (white paper "OCPP over SSH", section
        /// 10.4; "Key Pins and Raw Public Keys", section 5.7).
        /// </summary>
        /// <param name="Id">The connection.</param>
        /// <param name="HostKeys">The host keys of the pin set, separated by commas.</param>
        /// <param name="Version">The version of the pin set.</param>
        /// <param name="Learned">The host key that was learned on probation and is among them, or the one that is not; null where none was learned.</param>
        /// <param name="Confirmed">Whether the host key learned on probation is among them.</param>
        /// <param name="Error">Why they were not taken.</param>
        public Boolean TryTakeSignedHostKeys(String                           Id,
                                             String                           HostKeys,
                                             UInt64                           Version,
                                             out String?                      Learned,
                                             out Boolean                      Confirmed,
                                             [NotNullWhen(false)] out String? Error)
        {

            Learned    = null;
            Confirmed  = false;

            lock (updateLock)
            {

                if (!connections.TryGetValue(Id, out var entry))
                {
                    Error = $"There is no connection '{Id}' here.";
                    return false;
                }

                if (!ConnectionEntry.ValidateHostKey(entry.URL, HostKeys, out var hostKey, out Error))
                    return false;

                if (hostKey is null)
                {
                    Error = "A signed pin set without a host key would leave the connection with nothing to believe its server by.";
                    return false;
                }

                if (Version <= entry.SshHostKeysVersion)
                {
                    Error = $"The pin set has version {Version}, and the connection holds version {entry.SshHostKeysVersion}: " +
                             "only a higher one is taken, so that an older pin set cannot be brought back.";
                    return false;
                }

                var was     = entry.Copy();

                var learned = entry.SshLearnedHostKey;

                Learned     = learned;
                Confirmed   = learned is not null &&
                              ConnectionEntry.SplitHostKeys(hostKey).Any(pin => String.Equals(pin, learned, StringComparison.Ordinal));

                entry.SshHostKey          = hostKey;
                entry.SshHostKeysVersion  = Version;
                entry.SshLearnedHostKey   = null;
                entry.SshLearnedAt        = null;

                if (!TrySaveConnections(out Error))
                {
                    connections[Id] = was;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Notice,
                                 $"'{entry.Description}' took the host keys of the signed pin set of version {Version}: {hostKey}" +
                                 (Learned is null
                                      ? "."
                                      : Confirmed
                                            ? $"; they confirm the one learned on its first connection ({Learned}), and its probation ends."
                                            : $"; the one learned on its first connection ({Learned}) is not among them, so that server is not believed any more."));

                Error = null;
                return true;

            }

        }

        #endregion

        #region TryRemoveConnection(Id, out Error [, out NotSaved])

        /// <summary>
        /// Take one connection away, for good.
        /// </summary>
        /// <remarks>
        /// Nothing refers to a connection, so nothing stands in the way. The
        /// credentials it used stay: they are frequently the same ones the
        /// replacement will use.
        /// </remarks>
        public Boolean TryRemoveConnection(String?                          Id,
                                           [NotNullWhen(false)] out String? Error)

            => TryRemoveConnection(Id, out Error, out _);

        /// <summary>
        /// Take one connection away - and say whether a refusal was the file's
        /// rather than the request's.
        /// </summary>
        /// <param name="NotSaved">True where the file of the connections could not be written: nothing about the request was wrong, and the connection stays.</param>
        public Boolean TryRemoveConnection(String?                          Id,
                                           [NotNullWhen(false)] out String? Error,
                                           out Boolean                      NotSaved)
        {

            NotSaved = false;

            lock (updateLock)
            {

                if (Id is null || !connections.TryGetValue(Id, out var entry))
                {
                    Error = "There is no connection here under that identification.";
                    return false;
                }

                connections.Remove(Id);

                if (!TrySaveConnections(out Error))
                {
                    connections[Id] = entry;
                    NotSaved = true;
                    return false;
                }

                OnNotice?.Invoke(LogLevel.Info, $"Connection removed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion

        #region (private) CheckReferences(...) / CheckEverything() / CheckEntry(Entry)

        /// <summary>
        /// What a connection says it proves itself with, checked against what
        /// is actually here. Called under the lock.
        /// </summary>
        private Boolean CheckReferences(String?                          AuthenticationId,
                                        String?                          CertificateId,
                                        Boolean                          OverSSH,
                                        out String?                      Authentication,
                                        out String?                      Certificate,
                                        [NotNullWhen(false)] out String? Error)
        {

            Authentication  = ConnectionEntry.Named(AuthenticationId);
            Certificate     = ConnectionEntry.Named(CertificateId);
            Error           = null;

            if (Authentication is not null && Certificate is not null)
            {
                Error = "A connection proves itself one way, not two. Choose credentials or a client certificate.";
                return false;
            }

            if (Authentication is not null && !authentications.ContainsKey(Authentication))
            {
                Error = "Those credentials are not configured here.";
                return false;
            }

            // Over SSH this station signs in with its own key, and a password
            // on top where the server asks for one. A TLS client certificate
            // has no handshake to be shown in, and a TOTP password is answered
            // in an HTTP header there is none of.
            if (OverSSH && Certificate is not null)
            {
                Error = "A client certificate is shown in a TLS handshake, and an ssh:// connection makes none: " +
                        "it signs in with this station's SSH key.";
                return false;
            }

            if (OverSSH && Authentication is not null && authentications[Authentication].Kind == AuthenticationKind.TOTP)
            {
                Error = "An ssh:// connection signs in with this station's SSH key, and a password on top where wanted. " +
                        "A TOTP password is answered over HTTP, which it does not speak.";
                return false;
            }

            if (Certificate is not null &&
                KnownCertificateIds is not null &&
                !KnownCertificateIds().Contains(Certificate))
            {
                Error = "That client certificate is not on this station.";
                return false;
            }

            return true;

        }

        /// <summary>
        /// Called under the lock.
        /// </summary>
        private void CheckEverything()
        {
            foreach (var entry in connections.Values)
                CheckEntry(entry);
        }

        /// <summary>
        /// What is worth saying about one connection, worked out again.
        /// </summary>
        /// <remarks>
        /// Rebuilt rather than remembered, because all of it depends on
        /// something other than the connection: which credentials still exist,
        /// whether they have a secret yet, which certificates are still here.
        /// A warning cached at the moment of writing would go on saying
        /// something that stopped being true on the page next door.
        /// </remarks>
        private void CheckEntry(ConnectionEntry Entry)
        {

            Entry.Warnings.Clear();

            if (Entry.AuthenticationId is not null)
            {

                if (!authentications.TryGetValue(Entry.AuthenticationId, out var credentials))
                    Entry.Warnings.Add("The credentials this connection names are not configured here any more.");

                else
                {

                    if (!credentials.HasSecret)
                        Entry.Warnings.Add($"'{credentials.Description}' has no secret set, so this connection cannot prove itself.");

                    if (credentials.Kind == AuthenticationKind.Basic && !Entry.IsSecure && !Entry.IsSSH)
                        Entry.Warnings.Add("HTTP Basic over a connection without TLS sends the password in the clear to anybody on the way.");

                    if (credentials.Kind == AuthenticationKind.TOTP && Entry.IsSSH)
                        Entry.Warnings.Add($"'{credentials.Description}' is a TOTP password, which is answered over HTTP. " +
                                            "An ssh:// connection does not speak it, so it is not dialled.");

                    if (credentials.Kind == AuthenticationKind.Basic && Entry.IsSSH &&
                        Entry.URL.Login is String login && login != credentials.Login)
                        Entry.Warnings.Add($"The URL signs in as '{login}' and '{credentials.Description}' as '{credentials.Login}'. " +
                                            "It is one login, so it is not dialled until the two agree.");

                    if (credentials.Kind == AuthenticationKind.TOTP && credentials.TLSChannelBinding && !Entry.IsSSH)
                        Entry.Warnings.Add($"'{credentials.Description}' binds its password to the TLS session, and this station's TLS exports " +
                                            "no keying material to bind one with, so it is not dialled. Switch the binding off for it to have " +
                                            "the password sent unbound - and know that an unbound password can be replayed within its validity.");

                }

            }

            if (Entry.CertificateId is not null)
            {

                if (KnownCertificateIds is not null && !KnownCertificateIds().Contains(Entry.CertificateId))
                    Entry.Warnings.Add("The client certificate this connection names is not on this station any more.");

                if (!Entry.IsSecure)
                    Entry.Warnings.Add("A client certificate is shown during a TLS handshake, and this connection makes none.");

            }

            // Over SSH this station always shows its own key.
            if (Entry.AuthenticationId is null && Entry.CertificateId is null && !Entry.IsSSH)
                Entry.Warnings.Add("This connection proves nothing about who is dialling. A back end that asks will refuse it.");

            if (Entry.IsSSH)
            {

                if (Entry.PinsMD5)
                    Entry.Warnings.Add("An MD5 fingerprint is pinned, and MD5 fingerprints are no longer taken: this connection is not dialled " +
                                       "until it is replaced by the SHA-256 one - the connection test shows it.");

                if (Entry.SshHostKey is null && Entry.SshLearnedHostKey is not null)
                    Entry.Warnings.Add($"The host key {Entry.SshLearnedHostKey} was learned on the first connection and is on probation: " +
                                        "until a signed pin set confirms it, nothing but such a pin set is executed.");

                else if (Entry.SshHostKey is null && Entry.SshTrustOnFirstUse == SshTrustOnFirstUse.HostKey)
                    Entry.Warnings.Add("No host key is pinned, so the first server that answers is believed, on probation for " +
                                       $"{Entry.SshProbationTime.TotalMinutes:0.#} minute(s) until a signed pin set confirms its key. " +
                                        "Pin it now to decide yourself - the connection test shows the key.");

                else if (Entry.SshHostKey is null)
                    Entry.Warnings.Add("No host key is pinned and trust on first use is off, so this connection is not dialled: " +
                                       "pin its server's host key - the connection test shows it - or let it learn the first one, on probation.");

                if (Entry.SshOnPinMismatch != SshOnPinMismatch.Refuse)
                    Entry.Warnings.Add($"A host key that matches no pin is believed all the same ({Entry.SshOnPinMismatch}): whoever answers on the way " +
                                        "is taken for the server. That is for test benches, and for the move to a server whose key is not pinned yet.");

            }

        }

        #endregion


        #region ToJSON()

        /// <summary>
        /// Everything this store holds, as the web interface sees it - which
        /// is everything except the secrets.
        /// </summary>
        public JObject ToJSON()
        {

            lock (updateLock)
            {

                CheckEverything();

                return new JObject(

                           new JProperty("directory",              Path),

                           new JProperty("authentications",        new JArray(
                               authentications.Values.
                                   OrderByDescending(entry => entry.CreatedAt).
                                   Select(entry => entry.ToJSON()))),

                           new JProperty("connections",            new JArray(
                               connections.Values.
                                   OrderByDescending(entry => entry.CreatedAt).
                                   Select(entry => entry.ToJSON()))),

                           new JProperty("connectionTypes",        new JArray("CSMS", "CSMSBackup", "LocalController")),
                           new JProperty("ocppVersions",           new JArray("OCPP2.1", "OCPP1.6")),

                           new JProperty("maxDescriptionLength",   AuthenticationEntry.MaxDescriptionLength),
                           new JProperty("minSharedSecretLength",  AuthenticationEntry.MinSharedSecretLength),

                           // Said out loud rather than left to be discovered:
                           // somebody looking for where the password went
                           // should find the reason instead.
                           new JProperty("secretsAreReadable",     false),

                           new JProperty("totpDefaults",           new JObject(
                               new JProperty("validitySeconds",    AuthenticationEntry.DefaultValidity.TotalSeconds),
                               new JProperty("length",             AuthenticationEntry.DefaultLength),
                               new JProperty("alphabet",           AuthenticationEntry.DefaultAlphabet),
                               new JProperty("hashAlgorithm",      "SHA256")))

                       );

            }

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{connections.Count} connection(s), {authentications.Count} set(s) of credentials in '{Path}'";

        #endregion

    }

}
