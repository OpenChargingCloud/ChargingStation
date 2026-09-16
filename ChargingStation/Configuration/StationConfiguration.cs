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

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.RFID;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// Everything this charging station can be told in writing: one document
    /// with one section per thing that can be configured.
    /// </summary>
    /// <remarks>
    /// One file rather than one per subject, because these settings are read
    /// together, changed together and backed up together - and because the
    /// question "what is this station configured as" should have one answer
    /// that fits on a screen instead of a directory to go through.
    ///
    /// Every section is optional and so is every field inside it. A section
    /// that is absent is not a section set to nothing: it means the file has no
    /// opinion, and whatever the station was handed at construction stands. A
    /// station handed nothing either falls back to the system default. So the
    /// order is: system default, then what the constructor was given, then what
    /// this file says - each one only where it actually speaks.
    /// </remarks>
    /// <param name="DNS">How this station resolves names.</param>
    /// <param name="NTS">Where this station reads the time.</param>
    /// <param name="Power">What this station may draw from the grid.</param>
    /// <param name="EVSEs">What this station is made of.</param>
    /// <param name="Calibration">The calibration certificates it runs under.</param>
    /// <param name="RFID">The card readers it has, and where they sit.</param>
    /// <param name="Operator">Whose station this is, and whose cards it recognises.</param>
    /// <param name="WebPayments">How somebody with no card and no contract pays here.</param>
    /// <param name="Display">The quiet hours the screen on the front keeps.</param>
    public sealed record StationConfiguration(DNSConfiguration?                        DNS           = null,
                                              NTSConfiguration?                        NTS           = null,
                                              PowerConfiguration?                      Power         = null,
                                              IReadOnlyList<EVSEConfig>?               EVSEs         = null,
                                              IReadOnlyList<CalibrationCertificate>?   Calibration   = null,
                                              IReadOnlyList<RFIDReaderConfig>?         RFID          = null,
                                              OperatorConfiguration?                   Operator      = null,
                                              WebPaymentsConfiguration?                WebPayments   = null,
                                              DisplayConfiguration?                    Display       = null)
    {

        #region Data

        /// <summary>
        /// The name of the section holding the EVSEs.
        /// </summary>
        public const String  EVSEsSectionName        = "evses";

        /// <summary>
        /// The name of the section holding the calibration certificates.
        /// </summary>
        public const String  CalibrationSectionName  = "calibration";

        /// <summary>
        /// The name of the section holding the RFID readers.
        /// </summary>
        public const String  RFIDSectionName         = "rfid";

        #endregion

        #region Properties

        /// <summary>
        /// Whether this document says anything at all.
        /// </summary>
        public Boolean IsEmpty
            => DNS is null && NTS is null && Power is null && EVSEs is null &&
               Calibration is null && RFID is null && Operator is null && WebPayments is null;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The whole document, or the one sentence that says what is wrong with it.
        /// </summary>
        /// <remarks>
        /// A section of the wrong kind is an error rather than a section
        /// skipped: <c>"dns": null</c> is a file that has nothing to say about
        /// DNS, but <c>"dns": "google"</c> is a file whose author believed they
        /// had configured something.
        ///
        /// Sections this station does not know are passed over without a word.
        /// A file written by a newer station should still start an older one,
        /// and the file keeps them - see
        /// <see cref="StationConfigFile.TryReplaceSection"/>.
        /// </remarks>
        public static Boolean TryParse(JObject                                       JSON,
                                       [NotNullWhen(true)]  out StationConfiguration? Configuration,
                                       [NotNullWhen(false)] out String?               Error)
        {

            Configuration  = null;
            Error          = null;

            #region DNS

            DNSConfiguration? dns = null;

            if (JSON[DNSConfiguration.SectionName] is JToken dnsToken && dnsToken.Type != JTokenType.Null)
            {

                if (dnsToken is not JObject dnsJSON)
                {
                    Error = $"'{DNSConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!DNSConfiguration.TryParse(dnsJSON, out dns, out Error))
                    return false;

            }

            #endregion

            #region NTS

            NTSConfiguration? nts = null;

            if (JSON[NTSConfiguration.SectionName] is JToken ntsToken && ntsToken.Type != JTokenType.Null)
            {

                if (ntsToken is not JObject ntsJSON)
                {
                    Error = $"'{NTSConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!NTSConfiguration.TryParse(ntsJSON, out nts, out Error))
                    return false;

            }

            #endregion

            #region Display

            DisplayConfiguration? display = null;

            if (JSON[DisplayConfiguration.SectionName] is JToken displayToken && displayToken.Type != JTokenType.Null)
            {

                if (displayToken is not JObject displayJSON)
                {
                    Error = $"'{DisplayConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!DisplayConfiguration.TryParse(displayJSON, out display, out Error))
                    return false;

            }

            #endregion

            #region Power

            PowerConfiguration? power = null;

            if (JSON[PowerConfiguration.SectionName] is JToken powerToken && powerToken.Type != JTokenType.Null)
            {

                if (powerToken is not JObject powerJSON)
                {
                    Error = $"'{PowerConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!PowerConfiguration.TryParse(powerJSON, out power, out Error))
                    return false;

            }

            #endregion

            #region EVSEs

            IReadOnlyList<EVSEConfig>? evses = null;

            if (JSON[EVSEsSectionName] is JToken evsesToken && evsesToken.Type != JTokenType.Null)
            {

                if (evsesToken is not JArray evsesJSON)
                {
                    Error = $"'{EVSEsSectionName}' must be an array of EVSEs.";
                    return false;
                }

                if (!EVSEConfig.TryParseList(evsesJSON, out evses, out Error))
                    return false;

            }

            #endregion

            #region Calibration

            IReadOnlyList<CalibrationCertificate>? calibration = null;

            if (JSON[CalibrationSectionName] is JToken calibrationToken && calibrationToken.Type != JTokenType.Null)
            {

                if (calibrationToken is not JArray calibrationJSON)
                {
                    Error = $"'{CalibrationSectionName}' must be an array of calibration certificates.";
                    return false;
                }

                if (!CalibrationCertificate.TryParseList(calibrationJSON, out calibration, out Error))
                    return false;

            }

            #endregion

            #region RFID

            IReadOnlyList<RFIDReaderConfig>? rfid = null;

            if (JSON[RFIDSectionName] is JToken rfidToken && rfidToken.Type != JTokenType.Null)
            {

                if (rfidToken is not JArray rfidJSON)
                {
                    Error = $"'{RFIDSectionName}' must be an array of RFID readers.";
                    return false;
                }

                if (!RFIDReaderConfig.TryParseList(rfidJSON, out rfid, out Error))
                    return false;

            }

            #endregion

            #region Operator

            OperatorConfiguration? stationOperator = null;

            if (JSON[OperatorConfiguration.SectionName] is JToken operatorToken && operatorToken.Type != JTokenType.Null)
            {

                if (operatorToken is not JObject operatorJSON)
                {
                    Error = $"'{OperatorConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!OperatorConfiguration.TryParse(operatorJSON, out stationOperator, out Error))
                    return false;

            }

            #endregion

            #region WebPayments

            WebPaymentsConfiguration? webPayments = null;

            if (JSON[WebPaymentsConfiguration.SectionName] is JToken webPaymentsToken && webPaymentsToken.Type != JTokenType.Null)
            {

                if (webPaymentsToken is not JObject webPaymentsJSON)
                {
                    Error = $"'{WebPaymentsConfiguration.SectionName}' must be a JSON object.";
                    return false;
                }

                if (!WebPaymentsConfiguration.TryParse(webPaymentsJSON, out webPayments, out Error))
                    return false;

            }

            #endregion

            Configuration = new StationConfiguration(dns, nts, power, evses, calibration, rfid, stationOperator, webPayments, display);
            return true;

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// The document as it is written to the file.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (DNS is not null)
                json.Add(DNSConfiguration.SectionName,  DNS.ToJSON());

            if (NTS is not null)
                json.Add(NTSConfiguration.SectionName,    NTS.ToJSON());

            if (Power is not null)
                json.Add(PowerConfiguration.SectionName,  Power.ToJSON());

            if (Display is not null)
                json.Add(DisplayConfiguration.SectionName, Display.ToJSON());

            if (EVSEs is not null)
                json.Add(EVSEsSectionName,                new JArray(EVSEs.OrderBy(evse => evse.Id).
                                                                           Select (evse => evse.ToJSON())));

            if (Calibration is not null)
                json.Add(CalibrationSectionName,          new JArray(Calibration.Select(certificate => certificate.ToJSON())));

            if (RFID is not null)
                json.Add(RFIDSectionName,                 new JArray(RFID.Select(reader => reader.ToJSON())));

            if (Operator is not null)
                json.Add(OperatorConfiguration.SectionName,     Operator.ToJSON());

            if (WebPayments is not null)
                json.Add(WebPaymentsConfiguration.SectionName,  WebPayments.ToJSON());

            return json;

        }

        #endregion

        #region (override) ToString()

        public override String ToString()

            => IsEmpty
                   ? "nothing configured"
                   : String.Join(", ",
                         new[] {
                             DNS         is not null ? "DNS"                                    : null,
                             NTS         is not null ? "NTS"                                    : null,
                             Power       is not null ? Power.ToString()                         : null,
                             EVSEs       is not null ? $"{EVSEs.Count} EVSE(s)"                 : null,
                             Calibration is not null ? $"{Calibration.Count} certificate(s)"    : null,
                             RFID        is not null ? $"{RFID.Count} RFID reader(s)"           : null,
                             Operator    is not null ? Operator.ToString()                      : null,
                             WebPayments is not null ? WebPayments.ToString()                   : null
                         }.Where(section => section is not null));

        #endregion

    }

}
