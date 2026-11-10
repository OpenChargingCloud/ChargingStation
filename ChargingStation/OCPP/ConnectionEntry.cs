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

using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// What is at the other end of a connection.
    /// </summary>
    /// <remarks>
    /// A label, and deliberately no more than one. Nothing in this station
    /// reads it to decide anything: every connection is dialled on its own
    /// merits. Whether a spare should wait for its main one to fail is a real
    /// question with a real answer, and it is not answered here - a guess at
    /// it now is something that would have to be unpicked when somebody
    /// decides properly.
    /// </remarks>
    public enum ConnectionType
    {

        /// <summary>
        /// The charging station management system this station reports to.
        /// </summary>
        CSMS,

        /// <summary>
        /// A second management system, for when the first cannot be reached.
        /// </summary>
        CSMSBackup,

        /// <summary>
        /// A local controller, normally in the same installation.
        /// </summary>
        LocalController

    }


    /// <summary>
    /// Which OCPP this station speaks on a connection.
    /// </summary>
    /// <remarks>
    /// Written down rather than worked out, because there is nothing to work
    /// it out from. This station is two nodes - an OCPP 1.6 charge point and
    /// an OCPP 2.1 charging station - and which of them dials is not something
    /// a URL says. The version is negotiated in the WebSocket sub-protocol
    /// during the handshake, and by then the node has already been chosen.
    /// </remarks>
    public enum OCPPVersion
    {

        /// <summary>
        /// OCPP 1.6, still what most of the installed base speaks.
        /// </summary>
        OCPP1_6,

        /// <summary>
        /// OCPP 2.1.
        /// </summary>
        OCPP2_1

    }


    /// <summary>
    /// One place this charging station dials, and what it proves itself with
    /// when it gets there.
    /// </summary>
    /// <remarks>
    /// The credentials are referred to rather than repeated. A station that
    /// reaches its management system twice - once normally and once through a
    /// spare address - proves itself the same way both times, and a password
    /// written down twice is a password that gets changed once.
    ///
    /// At most one way of proving itself, because that is one decision and a
    /// connection that carried two would leave somebody guessing which one
    /// was actually used. None at all is allowed and is what a test back end
    /// on the bench wants; the page says plainly what that means.
    /// </remarks>
    public sealed class ConnectionEntry
    {

        #region Data

        /// <summary>
        /// The longest a description may be written.
        /// </summary>
        public const Int32 MaxDescriptionLength  = 120;

        /// <summary>
        /// The longest a URL may be written.
        /// </summary>
        public const Int32 MaxURLLength          = 500;

        /// <summary>
        /// The schemes a charging station may be told to dial.
        /// </summary>
        /// <remarks>
        /// OCPP is a WebSocket protocol, so ws and wss are what this is for.
        /// http and https are here because a URL is frequently written down
        /// that way and means the same endpoint - refusing it would be
        /// pedantry, and taking it silently would hide which one is meant, so
        /// it is taken and said out loud on the page.
        ///
        /// And ssh: the same OCPP over the SSH subsystem
        /// "ocpp@open.charging.cloud", signed in with this station's own key -
        /// "ssh://cs001@csms.example.org:2222".
        /// </remarks>
        public static readonly URIScheme[] AllowedSchemes = [
                                                                URIScheme.ws,
                                                                URIScheme.wss,
                                                                URIScheme.http,
                                                                URIScheme.https,
                                                                URIScheme.ssh
                                                            ];

        /// <summary>
        /// The longest a host key may be written: a public-key line of an RSA
        /// key of 4096 bits, and room to spare.
        /// </summary>
        public const Int32 MaxHostKeyLength      = 1000;

        /// <summary>
        /// The most host keys one connection pins: the current ones of its
        /// server and room for a change, as HostKeyPins of the white paper
        /// "OCPP over SSH" allows.
        /// </summary>
        public const Int32 MaxHostKeys           = 16;

        /// <summary>
        /// How long a host key learned on first use waits for a signed pin set
        /// that confirms it, where the connection does not say: 10 minutes.
        /// </summary>
        public static readonly TimeSpan DefaultSshProbationTime  = TimeSpan.FromMinutes(10);

        /// <summary>
        /// The shortest probation a connection may give a learned host key. A
        /// short one is the stricter one - an unconfirmed key is held for less
        /// time - so it is limited only by what a CSMS can answer in.
        /// </summary>
        public static readonly TimeSpan MinSshProbationTime      = TimeSpan.FromSeconds(1);

        #endregion

        #region Properties

        /// <summary>
        /// What this connection is called here.
        /// </summary>
        public String          Id                   { get; }

        /// <summary>
        /// What somebody wrote down that it is, e.g. "CSMS, main".
        /// </summary>
        public String          Description          { get; internal set; }

        /// <summary>
        /// Where it goes.
        /// </summary>
        public URL             URL                  { get; internal set; }

        /// <summary>
        /// What is at the other end.
        /// </summary>
        public ConnectionType  ConnectionType       { get; internal set; }

        /// <summary>
        /// Which OCPP this station speaks here, and so which of its two nodes
        /// does the dialling.
        /// </summary>
        public OCPPVersion     OCPPVersion          { get; internal set; }

        /// <summary>
        /// Whether this station connects here by itself - at start, and again
        /// after the connection drops.
        /// </summary>
        /// <remarks>
        /// This is what decides whether a connection is used at all. Off, it
        /// stays written down and nothing happens: an address kept ready for a
        /// day somebody needs it, which can still be tried by hand from the
        /// page. On, the station connects when it starts and keeps coming
        /// back.
        ///
        /// Off by default, which is the quieter of the two wrong answers. A
        /// station that was never told to connect is noticed by whoever
        /// configured it; a station that connects somewhere nobody meant it to
        /// is noticed by whatever is at the other end, later.
        /// </remarks>
        public Boolean         AutoConnect          { get; internal set; }

        /// <summary>
        /// The credentials this connection proves itself with, by their
        /// identification - or null.
        /// </summary>
        public String?         AuthenticationId     { get; internal set; }

        /// <summary>
        /// The client certificate this connection proves itself with, by its
        /// identification - or null.
        /// </summary>
        public String?         CertificateId        { get; internal set; }

        /// <summary>
        /// The host keys the server of an ssh:// connection may show, separated
        /// by commas: SHA-256 fingerprints ("SHA256:...") or public-key lines,
        /// at most <see cref="MaxHostKeys"/> - or null for none.
        /// </summary>
        /// <remarks>
        /// A set, so that a server that is about to change its host key can be
        /// pinned with both for the time of the change. Without one, the
        /// connection is dialled only where it trusts on first use (see
        /// <see cref="SshTrustOnFirstUse"/>). Written as HostKeyPins of the white
        /// paper "OCPP over SSH" is.
        /// </remarks>
        public String?         SshHostKey           { get; internal set; }

        /// <summary>
        /// The host keys pinned, one by one; none where nothing is.
        /// </summary>
        public IReadOnlyList<String>  SshHostKeys

            => SplitHostKeys(SshHostKey);

        /// <summary>
        /// Whether an ssh:// connection that pins no host key learns the one
        /// its server shows at the first handshake - on probation until a signed
        /// pin set confirms it. Never, unless it says so.
        /// </summary>
        public SshTrustOnFirstUse  SshTrustOnFirstUse   { get; internal set; }

        /// <summary>
        /// What a host key that matches none of the pins leads to: refused,
        /// unless the connection says to record or accept it.
        /// </summary>
        public SshOnPinMismatch    SshOnPinMismatch     { get; internal set; }

        /// <summary>
        /// How long a host key learned on first use waits for a signed pin set
        /// that confirms it.
        /// </summary>
        public TimeSpan        SshProbationTime     { get; internal set; } = DefaultSshProbationTime;

        /// <summary>
        /// The version of the signed pin set the host keys came from; 0 where
        /// none did. A signed pin set is taken only with a higher one, so that
        /// an older one cannot be brought back.
        /// </summary>
        public UInt64          SshHostKeysVersion   { get; internal set; }

        /// <summary>
        /// The SHA-256 fingerprint of the host key learned on first use, while
        /// it is on probation - or null.
        /// </summary>
        public String?         SshLearnedHostKey    { get; internal set; }

        /// <summary>
        /// When <see cref="SshLearnedHostKey"/> was learned.
        /// </summary>
        public DateTimeOffset? SshLearnedAt         { get; internal set; }

        /// <summary>
        /// When this was written down.
        /// </summary>
        public DateTimeOffset  CreatedAt            { get; }

        /// <summary>
        /// Whether the connection is made over TLS, which decides what a TLS
        /// client certificate and a channel-bound password can mean here.
        /// </summary>
        public Boolean         IsSecure

            => URL.Scheme == URIScheme.wss ||
               URL.Scheme == URIScheme.https;

        /// <summary>
        /// Whether the connection is made over SSH rather than as an HTTP
        /// WebSocket - encrypted, and the server known by its host key rather
        /// than by a TLS certificate.
        /// </summary>
        public Boolean         IsSSH

            => URL.Scheme == URIScheme.ssh;

        /// <summary>
        /// What is worth saying about this connection that is not an error:
        /// something configured that cannot do what it looks like it does.
        /// </summary>
        /// <remarks>
        /// Worked out rather than remembered, because most of it depends on
        /// something other than this record - whether the credentials it
        /// names still exist, and whether the URL it was given can carry them.
        /// </remarks>
        public List<String>    Warnings             { get; } = [];

        #endregion

        #region Constructor(s)

        /// <summary>
        /// One connection.
        /// </summary>
        public ConnectionEntry(String          Id,
                               String          Description,
                               URL             URL,
                               ConnectionType  ConnectionType,
                               DateTimeOffset  CreatedAt)
        {

            this.Id              = Id;
            this.Description     = Description;
            this.URL             = URL;
            this.ConnectionType  = ConnectionType;
            this.CreatedAt       = CreatedAt;

        }

        #endregion


        #region (static) Named(Text)

        /// <summary>
        /// The text with the spaces taken off, or null when there was nothing
        /// but spaces.
        /// </summary>
        /// <remarks>
        /// "No credentials" and "credentials whose identification is the empty
        /// string" are the same thing arriving from a form, and only one of
        /// them is worth storing.
        /// </remarks>
        internal static String? Named(String? Text)
        {
            var text = Text?.Trim();
            return String.IsNullOrEmpty(text) ? null : text;
        }

        #endregion

        #region (static) TryParseVersion(Text, out Version)

        /// <summary>
        /// Which OCPP, written the way the API writes it.
        /// </summary>
        public static Boolean TryParseVersion(String? Text, out OCPPVersion Version)
        {

            switch (Text?.Trim().ToLowerInvariant().Replace(".", "").Replace("_", ""))
            {

                case "ocpp16":
                case "16":
                    Version = OCPPVersion.OCPP1_6;
                    return true;

                case "ocpp21":
                case "21":
                    Version = OCPPVersion.OCPP2_1;
                    return true;

                default:
                    Version = OCPPVersion.OCPP2_1;
                    return false;

            }

        }

        /// <summary>
        /// How a version is written in the API and in the file.
        /// </summary>
        public static String AsText(OCPPVersion Version)

            => Version == OCPPVersion.OCPP1_6
                   ? "OCPP1.6"
                   : "OCPP2.1";

        #endregion

        #region (static) TryParseType(Text, out Type)

        /// <summary>
        /// What is at the other end, written the way the API writes it.
        /// </summary>
        public static Boolean TryParseType(String? Text, out ConnectionType Type)
        {

            switch (Text?.Trim().ToLowerInvariant())
            {

                case "csms":
                    Type = ConnectionType.CSMS;
                    return true;

                case "csmsbackup":
                    Type = ConnectionType.CSMSBackup;
                    return true;

                case "localcontroller":
                    Type = ConnectionType.LocalController;
                    return true;

                default:
                    Type = ConnectionType.CSMS;
                    return false;

            }

        }

        #endregion

        #region (static) Validate(Description, URLText, TypeText, out URL, out Type, out Error)

        /// <summary>
        /// Everything that can be said to be wrong with a connection before it
        /// is written down, in the words somebody typing it should read.
        /// </summary>
        /// <remarks>
        /// One place rather than two, so that adding one and changing one
        /// cannot come to disagree about what is allowed.
        ///
        /// Whether the credentials it names exist is not asked here: this
        /// record does not know what else is configured, and the store that
        /// does asks that question.
        /// </remarks>
        public static Boolean Validate(String?                          Description,
                                       String?                          URLText,
                                       String?                          TypeText,
                                       String?                          VersionText,
                                       out URL                          URL,
                                       out ConnectionType               Type,
                                       out OCPPVersion                  Version,
                                       [NotNullWhen(false)] out String? Error)
        {

            URL      = default;
            Type     = ConnectionType.CSMS;
            Version  = OCPPVersion.OCPP2_1;
            Error    = null;

            var description = (Description ?? "").Trim();
            var urlText     = (URLText     ?? "").Trim();

            if (description.Length == 0)
            {
                Error = "Say what this connection is - that is what tells it from the others.";
                return false;
            }

            if (description.Length > MaxDescriptionLength)
            {
                Error = $"A description may be at most {MaxDescriptionLength} characters.";
                return false;
            }

            if (urlText.Length == 0)
            {
                Error = "A connection needs somewhere to go.";
                return false;
            }

            if (urlText.Length > MaxURLLength)
            {
                Error = $"A URL may be at most {MaxURLLength} characters.";
                return false;
            }

            // Insisted on before parsing, because Hermod's parser puts
            // "https://" in front of anything that has no scheme. Everywhere
            // else that is a convenience; here the scheme is the whole
            // question of whether there is TLS at all, and everything this
            // station says about a connection - whether a client certificate
            // can be shown, whether a password goes out in the clear - is read
            // off it. A guessed scheme would have the station quietly claiming
            // to be secure because somebody left four characters out.
            if (!urlText.Contains("://", StringComparison.Ordinal))
            {
                Error = "The URL says no scheme - write ws://, wss:// or ssh:// in front of it. " +
                        "Which one it is decides whether this connection is encrypted, so it is not guessed at here.";
                return false;
            }

            if (!org.GraphDefined.Vanaheimr.Hermod.HTTP.URL.TryParse(urlText, out var url))
            {
                Error = $"'{urlText}' cannot be read as a URL.";
                return false;
            }

            if (url.Scheme is null)
            {
                Error = "The URL says no scheme - write ws://, wss:// or ssh:// in front of it.";
                return false;
            }

            if (!AllowedSchemes.Contains(url.Scheme))
            {
                Error = $"'{url.Scheme}' is not something this station dials. OCPP goes over ws, wss or ssh.";
                return false;
            }

            if (!TryParseType(TypeText, out var type))
            {
                Error = $"'{TypeText}' is not a kind of connection this station knows.";
                return false;
            }

            // Left out, it is the newer one. A station configured by somebody
            // who did not think about it should not quietly be speaking the
            // older protocol.
            if (!String.IsNullOrWhiteSpace(VersionText) && !TryParseVersion(VersionText, out Version))
            {
                Error = $"'{VersionText}' is not an OCPP version this station speaks. It speaks OCPP1.6 and OCPP2.1.";
                return false;
            }

            URL   = url;
            Type  = type;

            return true;

        }

        #endregion

        #region (static) SplitHostKeys(Text)

        /// <summary>
        /// The host keys of a set as it is written: separated by commas or line
        /// breaks, trimmed, the empty ones left out.
        /// </summary>
        public static IReadOnlyList<String> SplitHostKeys(String? Text)

            => (Text ?? "").Split([ ',', '\n', '\r' ], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        #endregion

        #region (static) ValidateHostKey(URL, Text, out HostKey, out Error, MD5Kept = false)

        /// <summary>
        /// The host keys a connection pins, as they are written down - or the
        /// one sentence that says what is wrong with them.
        /// </summary>
        /// <remarks>
        /// Read as the SSH client reads them, so that what is taken here is what
        /// is checked when the station dials: SHA-256 fingerprints, or
        /// public-key lines, at most <see cref="MaxHostKeys"/> of them. An MD5
        /// fingerprint is refused with the way to its SHA-256 one (white paper
        /// "OCPP over SSH", decision 3); one already in the file is kept, so
        /// that the connection is not lost, and it is not dialled until it is
        /// replaced (see <see cref="PinsMD5"/>). Only for ssh://: the server of
        /// a WebSocket is known by its TLS certificate, and a host key written
        /// down beside it would look like a check nothing makes.
        /// </remarks>
        /// <param name="URL">Where the connection goes.</param>
        /// <param name="Text">The host keys as written, separated by commas; nothing for none.</param>
        /// <param name="HostKey">The host keys, trimmed and separated by ", "; null for none.</param>
        /// <param name="Error">What is wrong with them.</param>
        /// <param name="MD5Kept">Whether an MD5 fingerprint is kept rather than refused: as read from the file.</param>
        public static Boolean ValidateHostKey(URL                              URL,
                                              String?                          Text,
                                              out String?                      HostKey,
                                              [NotNullWhen(false)] out String? Error,
                                              Boolean                          MD5Kept   = false)
        {

            var keys  = SplitHostKeys(Text);

            HostKey   = null;
            Error     = null;

            if (keys.Count == 0)
                return true;

            if (URL.Scheme != URIScheme.ssh)
            {
                Error    = "A host key is what an ssh:// connection checks its server by. This one goes over a WebSocket, " +
                           "whose server is known by its TLS certificate.";
                return false;
            }

            if (keys.Count > MaxHostKeys)
            {
                Error    = $"A connection pins at most {MaxHostKeys} host keys, and these are {keys.Count}.";
                return false;
            }

            foreach (var key in keys)
            {

                if (key.Length > MaxHostKeyLength)
                {
                    Error    = $"A host key may be written in at most {MaxHostKeyLength} characters.";
                    return false;
                }

                if (!MD5Kept && key.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase))
                {
                    Error    = $"'{key}' is an MD5 fingerprint, and MD5 fingerprints are no longer taken: write its SHA-256 one, " +
                               "which 'ssh-keygen -l -f' prints for the server's public host key, or the public-key line itself.";
                    return false;
                }

                try
                {
                    HostKeyPolicy.Pin(key);
                }
                catch (ArgumentException)
                {
                    Error    = $"'{key}' is neither a fingerprint (SHA256:...) nor a public-key line (ssh-ed25519 AAAA...).";
                    return false;
                }

            }

            HostKey = String.Join(", ", keys.Distinct(StringComparer.Ordinal));

            return true;

        }

        /// <summary>
        /// Whether one of the host keys pinned is an MD5 fingerprint, which is
        /// no longer taken: one kept from the file, which is not dialled with.
        /// </summary>
        public Boolean PinsMD5

            => SshHostKeys.Any(key => key.StartsWith("MD5:", StringComparison.OrdinalIgnoreCase));

        #endregion

        #region (static) TryParseTrustOnFirstUse(Text, out Value) / TryParseOnPinMismatch(Text, out Value)

        /// <summary>
        /// "None" or "HostKey", in any case; nothing for "None".
        /// </summary>
        public static Boolean TryParseTrustOnFirstUse(String? Text, out SshTrustOnFirstUse Value)
        {

            Value = SshTrustOnFirstUse.None;

            if (String.IsNullOrWhiteSpace(Text))
                return true;

            return Enum.TryParse(Text.Trim(), ignoreCase: true, out Value) && Enum.IsDefined(Value);

        }

        /// <summary>
        /// "Refuse", "Record" or "Accept", in any case; nothing for "Refuse".
        /// </summary>
        public static Boolean TryParseOnPinMismatch(String? Text, out SshOnPinMismatch Value)
        {

            Value = SshOnPinMismatch.Refuse;

            if (String.IsNullOrWhiteSpace(Text))
                return true;

            return Enum.TryParse(Text.Trim(), ignoreCase: true, out Value) && Enum.IsDefined(Value);

        }

        #endregion

        #region (static) ValidateSshTrust(TrustText, MismatchText, ProbationSeconds, out Trust, out Mismatch, out Probation, out Error)

        /// <summary>
        /// How an ssh:// connection believes its server where nothing or
        /// something else is pinned, as it is written - or the one sentence that
        /// says what is wrong with it.
        /// </summary>
        public static Boolean ValidateSshTrust(String?                          TrustText,
                                               String?                          MismatchText,
                                               Int64?                           ProbationSeconds,
                                               out SshTrustOnFirstUse           Trust,
                                               out SshOnPinMismatch             Mismatch,
                                               out TimeSpan                     Probation,
                                               [NotNullWhen(false)] out String? Error)
        {

            Mismatch   = SshOnPinMismatch.Refuse;
            Probation  = DefaultSshProbationTime;
            Error      = null;

            if (!TryParseTrustOnFirstUse(TrustText, out Trust))
            {
                Error = $"'{TrustText}' says nothing of trust on first use: it is 'None' or 'HostKey'.";
                return false;
            }

            if (!TryParseOnPinMismatch(MismatchText, out Mismatch))
            {
                Error = $"'{MismatchText}' says nothing of a host key that matches no pin: it is 'Refuse', 'Record' or 'Accept'.";
                return false;
            }

            if (ProbationSeconds is Int64 seconds)
            {

                if (seconds < MinSshProbationTime.TotalSeconds || seconds > TimeSpan.FromDays(7).TotalSeconds)
                {
                    Error = $"A probation of {seconds} s is not one: it lasts from {MinSshProbationTime.TotalSeconds:0} s to 7 days.";
                    return false;
                }

                Probation = TimeSpan.FromSeconds(seconds);

            }

            return true;

        }

        #endregion

        #region (static) TryParse(JSON, out Entry, out Error)

        /// <summary>
        /// One connection as it stands in the file, or the one sentence that
        /// says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                JSON,
                                       [NotNullWhen(true)]  out ConnectionEntry?  Entry,
                                       [NotNullWhen(false)] out String?           Error)
        {

            Entry = null;

            var id = JSON.Value<String>("id")?.Trim();

            if (String.IsNullOrEmpty(id))
            {
                Error = "A connection without an identification.";
                return false;
            }

            if (!Validate(JSON.Value<String>("description"),
                          JSON.Value<String>("url"),
                          JSON.Value<String>("connectionType"),
                          JSON.Value<String>("ocppVersion"),
                          out var url,
                          out var type,
                          out var version,
                          out Error))
            {
                return false;
            }

            if (!ValidateHostKey(url, JSON.Value<String>("sshHostKey"), out var hostKey, out Error, MD5Kept: true))
                return false;

            if (!ValidateSshTrust(JSON.Value<String>("sshTrustOnFirstUse"),
                                  JSON.Value<String>("sshOnPinMismatch"),
                                  JSON.Value<Int64?>("sshProbationTime"),
                                  out var trustOnFirstUse,
                                  out var onPinMismatch,
                                  out var probationTime,
                                  out Error))
                return false;

            var learnedAt = JSON.Value<String>("sshLearnedAt") is String learnedAtText &&
                            DateTimeOffset.TryParse(learnedAtText, out var learnedAtValue)
                                ? learnedAtValue
                                : (DateTimeOffset?) null;

            Entry = new ConnectionEntry(
                        id,
                        (JSON.Value<String>("description") ?? "").Trim(),
                        url,
                        type,
                        AuthenticationEntry.Written(JSON.Value<String>("createdAt"))
                    ) {
                        OCPPVersion         = version,
                        AutoConnect  = JSON.Value<Boolean?>("autoConnect") ?? false,
                        AuthenticationId    = Named(JSON.Value<String>("authenticationId")),
                        CertificateId       = Named(JSON.Value<String>("certificateId")),
                        SshHostKey          = hostKey,
                        SshTrustOnFirstUse  = trustOnFirstUse,
                        SshOnPinMismatch    = onPinMismatch,
                        SshProbationTime    = probationTime,
                        SshHostKeysVersion  = JSON.Value<UInt64?>("sshHostKeysVersion") ?? 0,
                        SshLearnedHostKey   = hostKey is null ? Named(JSON.Value<String>("sshLearnedHostKey")) : null,
                        SshLearnedAt        = hostKey is null ? learnedAt : null
                    };

            Error = null;
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// What this connection looks like from outside.
        /// </summary>
        /// <remarks>
        /// The same shape on disk and in the web interface, because there is
        /// nothing secret here: a connection names its credentials, it does
        /// not hold them.
        /// </remarks>
        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",                  Id),
                           new JProperty("description",         Description),
                           new JProperty("url",                 URL.ToString()),
                           new JProperty("connectionType",      ConnectionType.ToString()),
                           new JProperty("ocppVersion",         AsText(OCPPVersion)),
                           new JProperty("autoConnect",  AutoConnect),
                           new JProperty("secure",              IsSecure),
                           new JProperty("transport",           IsSSH ? "ssh" : "websocket"),
                           new JProperty("createdAt",           CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                       );

            if (AuthenticationId is not null)  json.Add(new JProperty("authenticationId",  AuthenticationId));
            if (CertificateId    is not null)  json.Add(new JProperty("certificateId",     CertificateId));
            if (SshHostKey       is not null)  json.Add(new JProperty("sshHostKey",        SshHostKey));

            if (IsSSH)
            {

                json.Add(new JProperty("sshTrustOnFirstUse",  SshTrustOnFirstUse.ToString()));
                json.Add(new JProperty("sshOnPinMismatch",    SshOnPinMismatch.  ToString()));
                json.Add(new JProperty("sshProbationTime",    (Int64) SshProbationTime.TotalSeconds));

                if (SshHostKeysVersion > 0)
                    json.Add(new JProperty("sshHostKeysVersion",  SshHostKeysVersion));

                if (SshLearnedHostKey is not null)
                {
                    json.Add(new JProperty("sshLearnedHostKey",  SshLearnedHostKey));
                    json.Add(new JProperty("sshLearnedAt",       SshLearnedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));
                }

            }

            if (Warnings.Count > 0)
                json.Add(new JProperty("warnings", new JArray(Warnings)));

            return json;

        }

        #endregion

        #region (internal) Copy()

        /// <summary>
        /// This connection as it is now, in an entry of its own: what the store
        /// puts back where a change of it could not be written. Its warnings
        /// are not copied; the store works them out whenever it is asked.
        /// </summary>
        internal ConnectionEntry Copy()

            => new (Id, Description, URL, ConnectionType, CreatedAt) {
                   OCPPVersion       = OCPPVersion,
                   AutoConnect       = AutoConnect,
                   AuthenticationId  = AuthenticationId,
                   CertificateId       = CertificateId,
                   SshHostKey          = SshHostKey,
                   SshTrustOnFirstUse  = SshTrustOnFirstUse,
                   SshOnPinMismatch    = SshOnPinMismatch,
                   SshProbationTime    = SshProbationTime,
                   SshHostKeysVersion  = SshHostKeysVersion,
                   SshLearnedHostKey   = SshLearnedHostKey,
                   SshLearnedAt        = SshLearnedAt
               };

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Description} ({ConnectionType} over {AsText(OCPPVersion)}, {URL})";

        #endregion

    }

}
