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

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// What is at the other end of a connection.
    /// </summary>
    /// <remarks>
    /// Not merely a label. A station treats the three differently: it speaks
    /// to one charging station management system at a time and falls back to
    /// the spare only when the first is unreachable, while a local controller
    /// sits in the same cabinet and is a different conversation entirely.
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
        /// </remarks>
        public static readonly URIScheme[] AllowedSchemes = [
                                                                URIScheme.ws,
                                                                URIScheme.wss,
                                                                URIScheme.http,
                                                                URIScheme.https
                                                            ];

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
        /// Whether this station dials again by itself after the connection
        /// drops.
        /// </summary>
        /// <remarks>
        /// Off by default, which is the quieter of the two wrong answers: a
        /// station that does not come back is noticed, and a station that
        /// reconnects in a loop against a back end that keeps refusing it is
        /// noticed by the back end.
        /// </remarks>
        public Boolean         AutomaticReconnect   { get; internal set; }

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
                Error = "The URL says no scheme - write ws:// or wss:// in front of it. " +
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
                Error = "The URL says no scheme - write ws:// or wss:// in front of it.";
                return false;
            }

            if (!AllowedSchemes.Contains(url.Scheme))
            {
                Error = $"'{url.Scheme}' is not something this station dials. OCPP goes over ws or wss.";
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

            Entry = new ConnectionEntry(
                        id,
                        (JSON.Value<String>("description") ?? "").Trim(),
                        url,
                        type,
                        AuthenticationEntry.Written(JSON.Value<String>("createdAt"))
                    ) {
                        OCPPVersion         = version,
                        AutomaticReconnect  = JSON.Value<Boolean?>("automaticReconnect") ?? false,
                        AuthenticationId    = Named(JSON.Value<String>("authenticationId")),
                        CertificateId       = Named(JSON.Value<String>("certificateId"))
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
                           new JProperty("automaticReconnect",  AutomaticReconnect),
                           new JProperty("secure",              IsSecure),
                           new JProperty("createdAt",           CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                       );

            if (AuthenticationId is not null)  json.Add(new JProperty("authenticationId",  AuthenticationId));
            if (CertificateId    is not null)  json.Add(new JProperty("certificateId",     CertificateId));

            if (Warnings.Count > 0)
                json.Add(new JProperty("warnings", new JArray(Warnings)));

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Description} ({ConnectionType} over {AsText(OCPPVersion)}, {URL})";

        #endregion

    }

}
