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
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// One calibration certificate this charging station runs under.
    /// </summary>
    /// <remarks>
    /// Whoever commissions a station under a calibration law regime arrives
    /// holding these: the certificate of the meter that was fitted, and the
    /// certificate of whoever signs what that meter reports. They are public
    /// documents - a certificate is a signature over a public key, and there is
    /// nothing in one that has to be kept - so this station keeps them as they
    /// were handed to it, in PEM, and reads the rest out of them rather than
    /// asking anybody to type it in twice.
    ///
    /// That is the whole reason the certificate itself is what is stored and
    /// not a form full of fields: an issuer and a validity somebody typed can
    /// disagree with the certificate they were typed from, and then there is no
    /// way to tell which of the two is the station's actual position.
    /// </remarks>
    public sealed record CalibrationCertificate
    {

        #region Data

        /// <summary>
        /// The most certificates this station will hold. A plausibility limit:
        /// one per meter and a few for the signatures over them is the real
        /// number, and anything beyond this is a loop that went wrong.
        /// </summary>
        public const Int32  MaxCertificates  = 32;

        /// <summary>
        /// The longest an id may be.
        /// </summary>
        public const Int32  MaxIdLength      = 64;

        /// <summary>
        /// The longest a description may be.
        /// </summary>
        public const Int32  MaxDescriptionLength = 200;

        /// <summary>
        /// The most PEM this station will read for one certificate.
        /// </summary>
        public const Int32  MaxPEMLength     = 64 * 1024;

        /// <summary>
        /// How long before a certificate runs out this station starts saying so.
        /// </summary>
        /// <remarks>
        /// Long enough that somebody can do something about it. A calibration
        /// certificate that runs out does not stop a station from charging, it
        /// stops what it charged from being billable - which is noticed a month
        /// later by somebody who was not there.
        /// </remarks>
        public static readonly TimeSpan  ExpiryWarningTime = TimeSpan.FromDays(90);

        #endregion

        #region Properties

        /// <summary>
        /// What this certificate is called here - the name it is changed and
        /// removed by.
        /// </summary>
        public String          Id                  { get; }

        /// <summary>
        /// What it is for, in the words of whoever put it here.
        /// </summary>
        public String?         Description         { get; }

        /// <summary>
        /// The certificate itself, canonically encoded.
        /// </summary>
        /// <remarks>
        /// Written out again from what was parsed rather than kept as it
        /// arrived, so that the same certificate pasted twice - once with
        /// Windows line endings, once with a stray blank line - is the same
        /// text in the file both times.
        /// </remarks>
        public String          PEM                 { get; }

        /// <summary>Who the certificate is about.</summary>
        public String          Subject             { get; }

        /// <summary>Who signed it.</summary>
        public String          Issuer              { get; }

        /// <summary>Its serial number, as the issuer gave it.</summary>
        public String          SerialNumber        { get; }

        /// <summary>Not valid before this.</summary>
        public DateTimeOffset  NotBefore           { get; }

        /// <summary>Not valid after this.</summary>
        public DateTimeOffset  NotAfter            { get; }

        /// <summary>The SHA-256 over the certificate, for comparing one against a paper.</summary>
        public String          ThumbprintSHA256    { get; }

        #endregion

        #region Constructor(s)

        private CalibrationCertificate(String          Id,
                                       String?         Description,
                                       String          PEM,
                                       String          Subject,
                                       String          Issuer,
                                       String          SerialNumber,
                                       DateTimeOffset  NotBefore,
                                       DateTimeOffset  NotAfter,
                                       String          ThumbprintSHA256)
        {

            this.Id                = Id;
            this.Description       = Description;
            this.PEM               = PEM;
            this.Subject           = Subject;
            this.Issuer            = Issuer;
            this.SerialNumber      = SerialNumber;
            this.NotBefore         = NotBefore;
            this.NotAfter          = NotAfter;
            this.ThumbprintSHA256  = ThumbprintSHA256;

        }

        #endregion


        #region IsExpired(Now) / IsNotYetValid(Now) / RunsOutWithin(Now, Within)

        /// <summary>
        /// Whether this certificate has run out.
        /// </summary>
        public Boolean IsExpired(DateTimeOffset Now)
            => Now > NotAfter;

        /// <summary>
        /// Whether this certificate has not started yet.
        /// </summary>
        public Boolean IsNotYetValid(DateTimeOffset Now)
            => Now < NotBefore;

        /// <summary>
        /// Whether this certificate runs out soon enough to be worth a word.
        /// </summary>
        public Boolean RunsOutWithin(DateTimeOffset Now,
                                     TimeSpan       Within)

            => !IsExpired(Now) && Now + Within > NotAfter;

        #endregion

        #region (static) TryParse(JSON, out Certificate, out Error)

        /// <summary>
        /// One certificate as the web interface sends it, or as the file keeps
        /// it: an id, an optional description, and the PEM.
        /// </summary>
        public static Boolean TryParse(JToken                                          JSON,
                                       [NotNullWhen(true)]  out CalibrationCertificate? Certificate,
                                       [NotNullWhen(false)] out String?                 Error)
        {

            Certificate  = null;
            Error        = null;

            if (JSON is not JObject json)
            {
                Error = "A calibration certificate must be a JSON object.";
                return false;
            }

            #region Id

            var id = json.Value<String>("id")?.Trim();

            if (String.IsNullOrEmpty(id))
            {
                Error = "Every calibration certificate needs an 'id'.";
                return false;
            }

            if (id.Length > MaxIdLength)
            {
                Error = $"A calibration certificate id may be at most {MaxIdLength} characters long.";
                return false;
            }

            if (!id.All(character => Char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
            {
                Error = $"The calibration certificate id '{id}' may hold only letters, digits, '-', '_' and '.'.";
                return false;
            }

            #endregion

            #region Description

            var description = json.Value<String>("description")?.Trim();

            if (String.IsNullOrEmpty(description))
                description = null;

            else if (description.Length > MaxDescriptionLength)
            {
                Error = $"The description of '{id}' may be at most {MaxDescriptionLength} characters long.";
                return false;
            }

            #endregion

            #region PEM

            var pem = json.Value<String>("pem")?.Trim();

            if (String.IsNullOrEmpty(pem))
            {
                Error = $"The calibration certificate '{id}' needs its 'pem'.";
                return false;
            }

            if (pem.Length > MaxPEMLength)
            {
                Error = $"The PEM of '{id}' is longer than the {MaxPEMLength} characters this station reads for one certificate.";
                return false;
            }

            X509Certificate2 certificate;

            try
            {
                certificate = X509Certificate2.CreateFromPem(pem);
            }
            catch (Exception e)
            {
                // The message from the platform is worth keeping: it is the
                // difference between "this is not PEM at all" and "this is a
                // private key where a certificate should be".
                Error = $"The PEM of '{id}' is not a certificate this station can read: {e.Message}";
                return false;
            }

            using (certificate)
            {

                Certificate = new CalibrationCertificate(
                                  id,
                                  description,
                                  PemEncoding.WriteString("CERTIFICATE", certificate.RawData),
                                  certificate.Subject,
                                  certificate.Issuer,
                                  certificate.SerialNumber,
                                  new DateTimeOffset(certificate.NotBefore.ToUniversalTime()),
                                  new DateTimeOffset(certificate.NotAfter. ToUniversalTime()),
                                  Convert.ToHexString(SHA256.HashData(certificate.RawData))
                              );

            }

            #endregion

            return true;

        }

        #endregion

        #region (static) TryParseList(JSON, out Certificates, out Error)

        /// <summary>
        /// A whole list of them, with the ids checked for being told apart.
        /// </summary>
        public static Boolean TryParseList(JArray                                                         JSON,
                                           [NotNullWhen(true)]  out IReadOnlyList<CalibrationCertificate>? Certificates,
                                           [NotNullWhen(false)] out String?                                Error)
        {

            Certificates  = null;
            Error         = null;

            var parsed = new List<CalibrationCertificate>();

            foreach (var token in JSON)
            {

                if (!TryParse(token, out var certificate, out Error))
                    return false;

                if (parsed.Any(other => String.Equals(other.Id, certificate.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    Error = $"There are two calibration certificates called '{certificate.Id}'; an id is how one of them is taken off again.";
                    return false;
                }

                parsed.Add(certificate);

            }

            if (parsed.Count > MaxCertificates)
            {
                Error = $"This station holds at most {MaxCertificates} calibration certificates.";
                return false;
            }

            Certificates = parsed;
            return true;

        }

        #endregion


        #region ToJSON()

        /// <summary>
        /// The certificate as the file keeps it: what was given, and nothing
        /// that was read out of it.
        /// </summary>
        /// <remarks>
        /// The subject, the issuer and the validity are deliberately not
        /// written. They are in the PEM, and a copy of them in the file could
        /// only ever start disagreeing with it.
        /// </remarks>
        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",   Id),
                           new JProperty("pem",  PEM)
                       );

            if (Description is not null)
                json.Add("description", Description);

            return json;

        }

        #endregion

        #region ToJSON(Now)

        /// <summary>
        /// The certificate as the web interface reads it: what was given, plus
        /// everything that was read out of it and what it means today.
        /// </summary>
        public JObject ToJSON(DateTimeOffset Now)

            => new (
                   new JProperty("id",                Id),
                   new JProperty("description",       Description),
                   new JProperty("pem",               PEM),
                   new JProperty("subject",           Subject),
                   new JProperty("issuer",            Issuer),
                   new JProperty("serialNumber",      SerialNumber),
                   new JProperty("notBefore",         NotBefore.       ToString("o")),
                   new JProperty("notAfter",          NotAfter.        ToString("o")),
                   new JProperty("thumbprintSHA256",  ThumbprintSHA256),
                   new JProperty("expired",           IsExpired(Now)),
                   new JProperty("notYetValid",       IsNotYetValid(Now)),
                   new JProperty("daysLeft",          (Int32) Math.Floor((NotAfter - Now).TotalDays))
               );

        #endregion

        #region (override) ToString()

        public override String ToString()

            => $"{Id}: {Subject} until {NotAfter:yyyy-MM-dd}";

        #endregion

    }

}
