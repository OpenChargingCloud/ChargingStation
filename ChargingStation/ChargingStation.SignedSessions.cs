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
using System.Globalization;
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using cloud.charging.open.chargy.Formats.OCMF;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.Meters;

using I18NDictionary       = cloud.charging.open.chargy.I18NDictionary;
using VerificationResult   = cloud.charging.open.chargy.VerificationResult;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Charging sessions signed by the meter of their EVSE: begun at the meter
    /// with who charges, where and on which tariff, ended into one OCMF
    /// document, and that document checked and kept.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what tells the meter of an EVSE from the meter of a grid
    /// connection point. A meter at a grid connection point counts on and on;
    /// the meter of an EVSE measures charging sessions, and under the German
    /// calibration law it is the meter that signs what a session cost in
    /// energy, together with who charged and on which tariff. There is no
    /// standard for how a station tells it so. This station speaks the session
    /// model of OpenChargingCloud's Modbus/TLS meter - see
    /// <see cref="MeterSessionModel"/> - to a meter that has it, and reads a
    /// meter that has not as it always did.
    /// </para>
    /// <para>
    /// Nothing waits for the meter. A car is charged whether the meter could be
    /// told or not; what could not be signed is said, kept as unsigned, and
    /// shown on the Meters page, because a session that silently has no
    /// document is the one somebody finds out about from a bill.
    /// </para>
    /// <para>
    /// A session the meter is still measuring when it should begin another -
    /// the station was restarted in the middle of one, or could not end it - is
    /// ended first, and its document kept with the session it belongs to where
    /// the station still knows it.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How often ending a session is tried before it is given up as
        /// unsigned, and how long apart.
        /// </summary>
        internal Int32     SessionStopAttempts  { get; set; } = 5;
        internal TimeSpan  SessionStopRetry     { get; set; } = TimeSpan.FromSeconds(5);

        private readonly ConcurrentDictionary<Byte, String>         signing        = [];
        private readonly ConcurrentDictionary<Byte, SemaphoreSlim>  signingGates   = [];
        private readonly ConcurrentDictionary<Task, Byte>           signingTasks   = [];
        private readonly CancellationTokenSource                    signingStops   = new ();

        #endregion

        #region Properties

        /// <summary>
        /// The charging sessions signed by the meters of this station's EVSEs,
        /// or to be.
        /// </summary>
        public SignedSessionStore  SignedSessions  { get; }

        #endregion


        #region (private) SessionBegan(EVSEId)

        /// <summary>
        /// A session began at an EVSE: tell its meter, where it signs sessions.
        /// </summary>
        private void SessionBegan(Byte EVSEId)
        {

            if (!sessions.TryGetValue(EVSEId, out var session) ||
                MeterOf(EVSEId) is not EVSEMeter meter      ||
                metersFile.MeterOf(EVSEId) is not MeterConfiguration configuration)
                return;

            var (type, identification) = IdentificationOf(session);

            var signed = new SignedSession(
                             Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
                             EVSEId,
                             session.StartedAt,
                             SignedSessionState.Starting,
                             type,
                             identification,
                             configuration.EVSEId,
                             configuration.TariffText
                         );

            signing[EVSEId] = signed.Id;

            Keep(signed);

            Signing(EVSEId, () => Begin(meter, signed));

        }

        #endregion

        #region (private) SessionEnded(EVSEId)

        /// <summary>
        /// The session at an EVSE ended: have its meter end it too, and sign it.
        /// </summary>
        private void SessionEnded(Byte EVSEId)
        {

            if (!signing.TryRemove(EVSEId, out var id) ||
                SignedSessions.Of(id) is not SignedSession signed)
                return;

            var stopped = TimeProvider.GetUtcNow();

            Signing(EVSEId, () => End(EVSEId, signed.Id, stopped));

        }

        #endregion

        #region (private) IdentificationOf(Session)

        /// <summary>
        /// Who charges, as OCMF says it: a card's UID for a card, at the reader
        /// or held up in an app; the contract for Plug and Charge; nobody for a
        /// payment at the screen, which identifies a payment and not a person.
        /// </summary>
        private static (String? Type, String? Identification) IdentificationOf(ChargingSession Session)

            => Session.Method switch {
                   AuthorizationMethod.RFID      when Session.TokenUID is not null  => ("ISO14443", Session.TokenUID),
                   AuthorizationMethod.LocalApp  when Session.TokenUID is not null  => ("ISO14443", Session.TokenUID),
                   AuthorizationMethod.PnC       when Session.TokenUID is not null  => ("EMAID",    Session.TokenUID),
                   AuthorizationMethod.Remote    when Session.TokenUID is not null  => ("CENTRAL",  Session.TokenUID),
                   _                                                                => (null,       null)
               };

        #endregion


        #region (private) Begin(Meter, Signed)

        private async Task Begin(EVSEMeter      Meter,
                                 SignedSession  Signed)
        {

            var answer = await Meter.Command(MeterSessionModel.Start, ParametersOf(Signed), signingStops.Token);

            // A session the meter still measures: one the station lost, or could
            // not end. Ended first, and begun again.
            if (!answer.Done && answer.Reason == SessionCommandAnswer.AlreadyRunning)
            {

                Log.Notice($"{Meter.About}: the meter was still measuring a session when '{Signed.Id}' began; that one is ended first.", "meter", "sessions");

                var orphan = await Meter.Command(MeterSessionModel.Stop, [], signingStops.Token);

                if (orphan.Done)
                    KeepTheDocument(Meter.EVSE, orphan, TimeProvider.GetUtcNow());

                answer = await Meter.Command(MeterSessionModel.Start, ParametersOf(Signed), signingStops.Token);

            }

            if (answer.Done)
            {
                Keep(Signed with { State = SignedSessionState.Running });
                return;
            }

            Log.Warning($"{Meter.About}: the session '{Signed.Id}' is not signed by its meter: {answer.Error}", "meter", "sessions");

            Keep(Signed with { State = SignedSessionState.Unsigned, Error = answer.Error });

        }

        /// <summary>
        /// What a session is begun with, point by point. Every point is written,
        /// the empty ones too: what is not written is not said.
        /// </summary>
        private static IEnumerable<(UInt16, UInt16, String?)> ParametersOf(SignedSession Signed)
            => [
                   (MeterSessionModel.SessionId,           MeterSessionModel.SessionIdLength,           Signed.Id),
                   (MeterSessionModel.IdentificationType,  MeterSessionModel.IdentificationTypeLength,  Signed.IdentificationType),
                   (MeterSessionModel.Identification,      MeterSessionModel.IdentificationLength,      Signed.Identification),
                   (MeterSessionModel.ChargePointIdType,   MeterSessionModel.ChargePointIdTypeLength,   Signed.EVSEId is null ? null : "EVSEID"),
                   (MeterSessionModel.ChargePointId,       MeterSessionModel.ChargePointIdLength,       Signed.EVSEId),
                   (MeterSessionModel.TariffText,          MeterSessionModel.TariffTextLength,          Signed.TariffText)
               ];

        #endregion

        #region (private) End(EVSEId, Id, StoppedAt)

        private async Task End(Byte            EVSEId,
                               String          Id,
                               DateTimeOffset  StoppedAt)
        {

            if (SignedSessions.Of(Id) is not SignedSession signed)
                return;

            // Never begun at the meter: there is nothing to end there.
            if (signed.State == SignedSessionState.Unsigned)
            {
                Keep(signed with { StoppedAt = StoppedAt });
                return;
            }

            Keep(signed with { State = SignedSessionState.Stopping, StoppedAt = StoppedAt });

            String? error = null;

            for (var attempt = 1; attempt <= SessionStopAttempts && !signingStops.IsCancellationRequested; attempt++)
            {

                // The meter it is at now: a meter read anew since is still the
                // meter of this EVSE.
                if (MeterOf(EVSEId) is not EVSEMeter meter)
                {
                    error = $"EVSE {EVSEId} has no meter read any more.";
                    break;
                }

                var answer = await meter.Command(
                                       MeterSessionModel.Stop,
                                       [ (MeterSessionModel.SessionId, MeterSessionModel.SessionIdLength, Id) ],
                                       signingStops.Token
                                   );

                if (answer.Done)
                {
                    KeepTheDocument(EVSEId, answer, StoppedAt);
                    return;
                }

                error = answer.Error;

                // What the meter refused, it will refuse again.
                if (answer.Reason != 0)
                    break;

                Log.Warning($"{meter.About}: ending the session '{Id}' at the meter failed ({attempt} of {SessionStopAttempts}): {error}", "meter", "sessions");

                await Task.Delay(SessionStopRetry, TimeProvider, signingStops.Token);

            }

            Log.Warning($"EVSE {EVSEId}: the session '{Id}' is not signed by its meter: {error}", "meter", "sessions");

            if (SignedSessions.Of(Id) is SignedSession now)
                Keep(now with { State = SignedSessionState.Unsigned, Error = error });

        }

        #endregion

        #region (private) KeepTheDocument(EVSEId, Answer, StoppedAt)

        /// <summary>
        /// A document the meter signed: checked by ChargyCore against the key it
        /// came with, and kept with the session it belongs to - or as a session
        /// of its own, where the station no longer knew it.
        /// </summary>
        private void KeepTheDocument(Byte                  EVSEId,
                                     SessionCommandAnswer  Answer,
                                     DateTimeOffset        StoppedAt)
        {

            if (Answer.Document is not String document)
                return;

            var (verification, payload) = Verify(document, Answer.PublicKey);

            var readings   = payload?["RD"] as JArray;
            var startValue = ReadingOf(readings?.FirstOrDefault(reading => reading["TX"]?.Value<String>() == "B"));
            var stopValue  = ReadingOf(readings?.LastOrDefault (reading => reading["TX"]?.Value<String>() == "E"));

            var signed     = (Answer.SessionId is String id && id.Length > 0 ? SignedSessions.Of(id) : null)
                                 ?? new SignedSession(
                                        String.IsNullOrEmpty(Answer.SessionId) ? Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)) : Answer.SessionId,
                                        EVSEId,
                                        MomentOf(readings?.FirstOrDefault(reading => reading["TX"]?.Value<String>() == "B")) ?? StoppedAt,
                                        SignedSessionState.Stopping,
                                        payload?["IT"]?.Value<String>(),
                                        payload?["ID"]?.Value<String>(),
                                        payload?["CI"]?.Value<String>(),
                                        payload?["TT"]?.Value<String>(),
                                        Error: "The station did not know this session any more when its meter ended it."
                                    );

            var kept = signed with {
                           State         = SignedSessionState.Signed,
                           StoppedAt     = signed.StoppedAt ?? StoppedAt,
                           Document      = document,
                           PublicKey     = Answer.PublicKey,
                           Verification  = verification,
                           StartValue    = startValue,
                           StopValue     = stopValue
                       };

            Keep(kept);

            if (verification == nameof(VerificationResult.ValidSignature))
                Log.Notice($"EVSE {EVSEId}: the session '{kept.Id}' is signed by its meter: {startValue} kWh to {stopValue} kWh, {stopValue - startValue} kWh in all.",
                           "meter", "sessions");
            else
                Log.Warning($"EVSE {EVSEId}: the meter signed the session '{kept.Id}', and ChargyCore says of the document: {verification}.",
                            "meter", "sessions");

        }

        /// <summary>
        /// When a reading of an OCMF document was taken: "2026-10-20T10:00:00,000+0000 S".
        /// </summary>
        private static DateTimeOffset? MomentOf(JToken? Reading)
        {

            var text = Reading?["TM"]?.Value<String>();

            if (text is null || text.Length < 28)
                return null;

            // An offset without a colon, which no format string of .NET reads.
            if (!DateTime.TryParseExact(text[..23], "yyyy-MM-dd'T'HH:mm:ss','fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local) ||
                text[23] is not ('+' or '-')                                                                                                     ||
                !Int32.TryParse(text[24..26], out var hours)                                                                                     ||
                !Int32.TryParse(text[26..28], out var minutes))
                return null;

            return new DateTimeOffset(local, (text[23] == '-' ? -1 : 1) * new TimeSpan(hours, minutes, 0));

        }

        private static Decimal? ReadingOf(JToken? Reading)

            => Reading?["RV"]?.Type is JTokenType.Float or JTokenType.Integer
                   ? Reading["RV"]!.Value<Decimal>()
                   : null;

        #endregion

        #region (private static) Verify(Document, PublicKey)

        /// <summary>
        /// What ChargyCore makes of a document and the key the meter handed out
        /// with it - and the payload, where it could read one.
        /// </summary>
        private static (String Verification, JObject? Payload) Verify(String   Document,
                                                                      String?  PublicKey)
        {

            try
            {

                var scanned = new OCMFDocumentScanner().Scan([ Document ]);

                if (scanned.Documents.Count == 0)
                    return (nameof(VerificationResult.UnknownSignatureFormat), null);

                var payload = scanned.Documents[0].Payload;

                if (String.IsNullOrWhiteSpace(PublicKey))
                    return (nameof(VerificationResult.PublicKeyNotFound), payload);

                return (new OCMFSignatureValidator(I18NDictionary.Default()).
                            Validate(scanned.Documents[0], PublicKey, "hex").
                            ToString(),
                        payload);

            }
            catch (Exception e)
            {
                return ($"Unreadable: {e.Message}", null);
            }

        }

        #endregion


        #region (private) Signing(EVSEId, Work)

        /// <summary>
        /// Do what the meter of an EVSE is told, one thing after the other for
        /// that EVSE and beside everything else - and nothing after the station
        /// began to stop.
        /// </summary>
        private void Signing(Byte        EVSEId,
                             Func<Task>  Work)
        {

            if (signingStops.IsCancellationRequested)
                return;

            var gate = signingGates.GetOrAdd(EVSEId, _ => new SemaphoreSlim(1, 1));

            var task = Task.Run(async () => {

                await gate.WaitAsync();

                try
                {
                    await Work();
                }
                catch (OperationCanceledException) when (signingStops.IsCancellationRequested)
                { }
                catch (Exception e)
                {
                    Log.Warning($"EVSE {EVSEId}: telling its meter about a session failed: {e.Message}", "meter", "sessions");
                }
                finally
                {
                    gate.Release();
                }

            });

            signingTasks[task] = EVSEId;

            _ = task.ContinueWith(done => signingTasks.TryRemove(done, out _), TaskScheduler.Default);

        }

        #endregion

        #region (private) Keep(Signed)

        private void Keep(SignedSession Signed)
        {

            try
            {
                SignedSessions.Keep(Signed);
            }
            catch (Exception e)
            {
                Log.Warning($"EVSE {Signed.EVSE}: the signed session '{Signed.Id}' could not be written down: {e.Message}", "meter", "sessions");
            }

            ReportMeterChanges();

        }

        #endregion

        #region (protected) StopSigning()

        /// <summary>
        /// Let what the meters are being told finish, as far as it does in a few
        /// seconds, and tell them nothing more - what a station does when it
        /// stops.
        /// </summary>
        /// <remarks>
        /// A session the meter is still measuring then is ended the next time a
        /// session begins at that EVSE, and its document kept with it.
        /// </remarks>
        protected async Task StopSigning()
        {

            signingStops.Cancel();

            try
            {
                await Task.WhenAll(signingTasks.Keys.ToArray()).WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception)
            { }

        }

        #endregion


        #region SignedSessionsJSON(EVSE = null)

        /// <summary>
        /// The recent signed sessions, the newest first - of one EVSE, or of
        /// all.
        /// </summary>
        public JObject SignedSessionsJSON(Byte? EVSE = null)

            => new (
                   new JProperty("timestamp",  TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                   new JProperty("sessions",   new JArray(SignedSessions.All.
                                                              Where (session => EVSE is null || session.EVSE == EVSE).
                                                              Select(session => session.ToJSON())))
               );

        #endregion

        #region (private) SignedSessionSummary(EVSE)

        /// <summary>
        /// Where the latest session of an EVSE stands with its meter - in the
        /// meters' JSON, so that the event of a meter says when it changed.
        /// </summary>
        private JObject? SignedSessionSummary(Byte EVSE)

            => SignedSessions.All.FirstOrDefault(session => session.EVSE == EVSE) is SignedSession latest
                   ? new JObject(
                         new JProperty("id",            latest.Id),
                         new JProperty("state",         latest.State.ToString().ToLowerInvariant()),
                         new JProperty("verification",  latest.Verification)
                     )
                   : null;

        #endregion

    }

}
