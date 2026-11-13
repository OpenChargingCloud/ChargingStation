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

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// A key this station believes a pin set of: a signed SetVariables of
    /// SSHClientController is taken where one of these signed it (white paper
    /// "OCPP over SSH", section 10.4; "Key Pins and Raw Public Keys", section
    /// 5.7, way 1). The CSMS's signing anchor, from commissioning.
    /// </summary>
    /// <param name="PublicKey">The public key, as the signatures of OCPP carry it in their keyId: the point of an elliptic curve, in base64.</param>
    /// <param name="Description">Whose it is.</param>
    /// <param name="CreatedAt">When it was written down.</param>
    public sealed record PinSetSigner(String          PublicKey,
                                      String          Description,
                                      DateTimeOffset  CreatedAt)
    {

        /// <summary>
        /// The public key's bytes.
        /// </summary>
        public Byte[] Bytes
            => Convert.FromBase64String(PublicKey);

        /// <summary>
        /// The public key as it was written - base64 or hexadecimal - in base64,
        /// or the one sentence that says why it is no key: a point of P-256,
        /// P-384 or P-521, compressed or not.
        /// </summary>
        public static Boolean TryNormalize(String?                           Text,
                                           [NotNullWhen(true)]  out String?  PublicKey,
                                           [NotNullWhen(false)] out String?  Error)
        {

            PublicKey  = null;
            Error      = null;

            var text   = (Text ?? "").Trim();
            Byte[]? bytes = null;

            if (text.Length == 0)
            {
                Error = "A signer needs its public key.";
                return false;
            }

            if (text.Length % 2 == 0 && text.All(Uri.IsHexDigit))
                bytes = Convert.FromHexString(text);

            else
            {
                try
                {
                    bytes = Convert.FromBase64String(text);
                }
                catch (FormatException)
                { }
            }

            if (bytes is null || bytes.Length is not (33 or 65 or 49 or 97 or 67 or 133) || bytes[0] is not (2 or 3 or 4))
            {
                Error = $"'{text}' is no public key of P-256, P-384 or P-521, in base64 or hexadecimal, as the keyId of an OCPP signature carries it.";
                return false;
            }

            PublicKey = Convert.ToBase64String(bytes);
            return true;

        }

        /// <summary>
        /// How it is written down, and how the page sees it.
        /// </summary>
        public JObject ToJSON()

            => new (
                   new JProperty("publicKey",    PublicKey),
                   new JProperty("description",  Description),
                   new JProperty("createdAt",    CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
               );

        /// <summary>
        /// One as it was written down, or none.
        /// </summary>
        public static PinSetSigner? TryParse(JObject JSON)

            => TryNormalize(JSON.Value<String>("publicKey"), out var publicKey, out _)
                   ? new PinSetSigner(publicKey,
                                      JSON.Value<String>("description") ?? "",
                                      DateTimeOffset.TryParse(JSON.Value<String>("createdAt"), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var createdAt) ? createdAt : DateTimeOffset.UnixEpoch)
                   : null;

    }

}
