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

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// How somebody with no card and no contract starts charging here: a URL
    /// on the display, as a QR code, that changes every few seconds.
    /// </summary>
    /// <remarks>
    /// The URL carries a time-based one-time password, so that a photograph of
    /// yesterday's screen is worth nothing and a link forwarded to somebody at
    /// home does not start a charge at this station. That is what the shared
    /// secret is for: it is held by this station and by whoever answers the
    /// URL, and by nobody else.
    ///
    /// **This section is read from the file and never written back over HTTP,
    /// and there is no page for it.** The shared secret is the one piece of
    /// configuration in this station that is worth stealing, and the display it
    /// ends up on hangs in public. So it stays in the file: changing it is
    /// editing the file and restarting, which is a fair price for the secret
    /// never being on the wire and never being in a browser. Everything else
    /// this station can be told still changes while it runs.
    /// </remarks>
    /// <param name="Enabled">Whether a QR code is shown at all.</param>
    /// <param name="URLTemplate">The URL, with {totp} where the one-time password goes.</param>
    /// <param name="ValidityTime">How long one password is good for.</param>
    /// <param name="TOTPLength">How many characters the password has.</param>
    /// <param name="SharedSecret">What this station and the far end both know.</param>
    public sealed record WebPaymentsConfiguration(Boolean?   Enabled        = null,
                                                  URL?       URLTemplate    = null,
                                                  TimeSpan?  ValidityTime   = null,
                                                  Byte?      TOTPLength     = null,
                                                  String?    SharedSecret   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName = "webPayments";

        #endregion

        #region Properties

        /// <summary>
        /// Whether this section says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => Enabled is null && URLTemplate is null && ValidityTime is null &&
               TOTPLength is null && SharedSecret is null;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The web payments section of the configuration file.
        /// </summary>
        public static Boolean TryParse(JObject                                            JSON,
                                       [NotNullWhen(true)]  out WebPaymentsConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?                   Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled", $"{SectionName}.enabled", out var enabled, out Error))
                return false;

            #region URLTemplate

            URL? urlTemplate = null;

            if (JSON["urlTemplate"] is JToken templateToken && templateToken.Type != JTokenType.Null)
            {

                var text = templateToken.Value<String>()?.Trim();

                if (String.IsNullOrEmpty(text) || !URL.TryParse(text, out var url))
                {
                    Error = $"'{SectionName}.urlTemplate' must be a URL.";
                    return false;
                }

                urlTemplate = url;

            }

            #endregion

            if (!ConfigurationReader.TryReadSeconds(JSON, "validitySeconds", $"{SectionName}.validitySeconds",
                                                    OCPPv2_1.WebPaymentsCtrlr.MinimumValidityTime.TotalSeconds,
                                                    OCPPv2_1.WebPaymentsCtrlr.MaximumValidityTime.TotalSeconds,
                                                    out var validityTime, out Error))
            {
                return false;
            }

            #region TOTPLength

            Byte? totpLength = null;

            if (JSON["totpLength"] is JToken lengthToken && lengthToken.Type != JTokenType.Null)
            {

                if (lengthToken.Type != JTokenType.Integer ||
                    lengthToken.Value<Int64>() < OCPPv2_1.WebPaymentsCtrlr.MinimumLength ||
                    lengthToken.Value<Int64>() > OCPPv2_1.WebPaymentsCtrlr.MaximumLength)
                {
                    Error = $"'{SectionName}.totpLength' must be between {OCPPv2_1.WebPaymentsCtrlr.MinimumLength} and {OCPPv2_1.WebPaymentsCtrlr.MaximumLength}.";
                    return false;
                }

                totpLength = (Byte) lengthToken.Value<Int64>();

            }

            #endregion

            #region SharedSecret

            String? sharedSecret = null;

            if (JSON["sharedSecret"] is JToken secretToken && secretToken.Type != JTokenType.Null)
            {

                var text = secretToken.Value<String>()?.Trim();

                if (String.IsNullOrEmpty(text) ||
                    text.Length < OCPPv2_1.WebPaymentsCtrlr.MinimumSharedSecretLength ||
                    text.Length > OCPPv2_1.WebPaymentsCtrlr.MaximumSharedSecretLength)
                {
                    Error = $"'{SectionName}.sharedSecret' must be between {OCPPv2_1.WebPaymentsCtrlr.MinimumSharedSecretLength} " +
                            $"and {OCPPv2_1.WebPaymentsCtrlr.MaximumSharedSecretLength} characters long.";
                    return false;
                }

                sharedSecret = text;

            }

            #endregion

            Configuration = new WebPaymentsConfiguration(enabled, urlTemplate, validityTime, totpLength, sharedSecret);
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file.
        /// </summary>
        /// <remarks>
        /// Including the shared secret, because this is the file - it is the
        /// one place the secret belongs. Nothing in the HTTP API calls this.
        /// </remarks>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.HasValue)        json.Add("enabled",          Enabled.Value);
            if (URLTemplate.HasValue)    json.Add("urlTemplate",      URLTemplate.Value.ToString());
            if (ValidityTime.HasValue)   json.Add("validitySeconds",  Math.Round(ValidityTime.Value.TotalSeconds, 3));
            if (TOTPLength.HasValue)     json.Add("totpLength",       TOTPLength.Value);
            if (SharedSecret is not null) json.Add("sharedSecret",    SharedSecret);

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Enabled == true
                   ? $"web payments at {URLTemplate?.ToString() ?? "no URL"}"
                   : "web payments switched off";

        #endregion

    }

}
