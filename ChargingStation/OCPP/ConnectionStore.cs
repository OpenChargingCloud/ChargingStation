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

using cloud.charging.open.ChargingStation.Logging;
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

        #region (private) Read(FileName) / Save()

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
        /// Both files, written out. Called under the lock.
        /// </summary>
        /// <remarks>
        /// Whole files rather than one record at a time: there are a handful
        /// of these, and a rewrite that either happened or did not is easier
        /// to reason about than a directory half-way through being changed.
        /// </remarks>
        private void Save()
        {

            Directory.CreateDirectory(Path);

            // The credentials hold the secrets, so they are written the way
            // the private keys next door are - and the mode goes on at
            // creation, not afterwards.
            OwnerOnlyFile.Write(
                System.IO.Path.Combine(Path, AuthenticationsFileName),
                new JArray(authentications.Values.
                               OrderBy(entry => entry.CreatedAt).
                               Select (entry => entry.ToJSON(IncludeSecrets: true))).
                    ToString(Formatting.Indented)
            );

            File.WriteAllText(
                System.IO.Path.Combine(Path, ConnectionsFileName),
                new JArray(connections.Values.
                               OrderBy(entry => entry.CreatedAt).
                               Select (entry => entry.ToJSON())).
                    ToString(Formatting.Indented)
            );

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


        #region TryAddAuthentication   (Description, Kind, Login, Secret, ..., out Id, out Error)

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
        {

            Id = null;

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

                Save();

                OnNotice?.Invoke(LogLevel.Info, $"Credentials written down: {entry}.");

                Id = entry.Id;
                return true;

            }

        }

        #endregion

        #region TryUpdateAuthentication(Id, Description, Kind, Login, Secret, ..., out Error)

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
        {

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

                entry.Description  = Description!.Trim();
                entry.Login        = Login!.Trim();
                entry.Kind         = kind;

                Apply(entry, Secret, ValiditySeconds, Length, Alphabet, HashAlgorithm, TLSChannelBinding);

                Save();

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

                Entry.TLSChannelBinding  = TLSChannelBinding ?? true;

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

        #region TryRemoveAuthentication(Id, out Error)

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
        {

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

                Save();

                OnNotice?.Invoke(LogLevel.Info, $"Credentials removed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion


        #region TryAddConnection   (Description, URL, ConnectionType, ..., out Id, out Error)

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
                                        String?                          OCPPVersion   = null)
        {

            Id = null;

            if (!ConnectionEntry.Validate(Description, URL, ConnectionType, OCPPVersion,
                                          out var url, out var type, out var version, out Error))
                return false;

            lock (updateLock)
            {

                if (!CheckReferences(AuthenticationId, CertificateId, out var authenticationId, out var certificateId, out Error))
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
                                CertificateId       = certificateId
                            };

                connections[entry.Id] = entry;

                Save();

                OnNotice?.Invoke(LogLevel.Info, $"Connection written down: {entry}.");

                Id = entry.Id;
                return true;

            }

        }

        #endregion

        #region TryUpdateConnection(Id, Description, URL, ConnectionType, ..., out Error)

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
                                           String?                          OCPPVersion   = null)
        {

            if (!ConnectionEntry.Validate(Description, URL, ConnectionType, OCPPVersion,
                                          out var url, out var type, out var version, out Error))
                return false;

            lock (updateLock)
            {

                if (Id is null || !connections.TryGetValue(Id, out var entry))
                {
                    Error = "There is no connection here under that identification.";
                    return false;
                }

                if (!CheckReferences(AuthenticationId, CertificateId, out var authenticationId, out var certificateId, out Error))
                    return false;

                entry.Description         = Description!.Trim();
                entry.URL                 = url;
                entry.ConnectionType      = type;
                entry.OCPPVersion         = version;
                entry.AutoConnect  = AutoConnect ?? false;
                entry.AuthenticationId    = authenticationId;
                entry.CertificateId       = certificateId;

                Save();

                OnNotice?.Invoke(LogLevel.Info, $"Connection changed: {entry}.");

                Error = null;
                return true;

            }

        }

        #endregion

        #region TryRemoveConnection(Id, out Error)

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
        {

            lock (updateLock)
            {

                if (Id is null || !connections.TryGetValue(Id, out var entry))
                {
                    Error = "There is no connection here under that identification.";
                    return false;
                }

                connections.Remove(Id);

                Save();

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

                    if (credentials.Kind == AuthenticationKind.Basic && !Entry.IsSecure)
                        Entry.Warnings.Add("HTTP Basic over a connection without TLS sends the password in the clear to anybody on the way.");

                    if (credentials.Kind == AuthenticationKind.TOTP && credentials.TLSChannelBinding && !Entry.IsSecure)
                        Entry.Warnings.Add($"'{credentials.Description}' binds its password to a TLS session, and this connection has none. " +
                                            "Either dial wss:// or turn the binding off - and know that an unbound password can be replayed.");

                }

            }

            if (Entry.CertificateId is not null)
            {

                if (KnownCertificateIds is not null && !KnownCertificateIds().Contains(Entry.CertificateId))
                    Entry.Warnings.Add("The client certificate this connection names is not on this station any more.");

                if (!Entry.IsSecure)
                    Entry.Warnings.Add("A client certificate is shown during a TLS handshake, and this connection makes none.");

            }

            if (Entry.AuthenticationId is null && Entry.CertificateId is null)
                Entry.Warnings.Add("This connection proves nothing about who is dialling. A back end that asks will refuse it.");

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
