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

using Newtonsoft.Json.Linq;

using cloud.charging.open.ChargingStation.Configuration;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What time this station thinks it is, and what that is worth.
    /// </summary>
    /// <remarks>
    /// Two different questions, and a charging station has to keep them apart.
    /// The time it shows is its own system clock. Whether that clock is any
    /// good is a separate matter, answered by asking a server that knows - and
    /// **this station does not set its clock from the answer**. It measures the
    /// difference and reports it.
    ///
    /// That is deliberate. Stepping the clock of a machine that is metering
    /// energy and writing signed records is not something a background task
    /// does by surprise; a jump backwards would put two readings out of order
    /// and there would be nothing in the record to say why. Measuring is the
    /// part that can be done safely and is the part that tells somebody whether
    /// there is a problem.
    ///
    /// **"Legal time" is never guessed.** Nothing here can tell from a hostname
    /// whether a server disseminates a country's legal time - that is a fact
    /// about an institution, not about DNS. So the operator says so, in
    /// <c>nts.legalTimeAuthority</c>, and this station then only repeats that
    /// claim while it can stand behind it: the claim exists, the clock was
    /// actually checked against that server, the check is recent, and the
    /// difference it found was small. Any one of those missing and the display
    /// says the time is unverified instead - which is a true statement about a
    /// station that has not checked, and the whole point of not printing the
    /// word "legal" over it.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Properties

        /// <summary>
        /// How often this station checks its clock.
        /// </summary>
        public TimeSpan        TimeCheckEvery
            => ntsSettings?.CheckEvery ?? NTSConfiguration.DefaultCheckEvery;

        /// <summary>
        /// Who the operator says stands behind the time of the configured
        /// server, or null when nobody has said.
        /// </summary>
        public String?         LegalTimeAuthority
            => ntsSettings?.LegalTimeAuthority;

        /// <summary>
        /// How far this station's clock may be off and still count.
        /// </summary>
        public TimeSpan        LegalTimeTolerance
            => ntsSettings?.LegalTimeTolerance ?? NTSConfiguration.DefaultLegalTolerance;

        /// <summary>
        /// How old the last check may be and still count.
        /// </summary>
        public TimeSpan        LegalTimeMaxAge
            => ntsSettings?.LegalTimeMaxAge ?? NTSConfiguration.DefaultLegalMaxAge;

        #endregion


        #region (private) StartCheckingTheClock()

        /// <summary>
        /// Begin checking this station's clock against its time server.
        /// </summary>
        /// <remarks>
        /// Through the station's own <see cref="TimeProvider"/> rather than a
        /// bare timer, so that a test which moves the clock moves this too.
        ///
        /// The first check is one minute in rather than at once: everything
        /// else is still coming up, the network may not be there yet, and a
        /// display that says "unverified" for a minute after a start is telling
        /// the truth.
        /// </remarks>
        private void StartCheckingTheClock()
        {

            timeCheckTimer?.Dispose();
            timeCheckTimer = null;

            if (!NTSEnabled)
            {
                Log.Info("The clock of this station is not being checked: NTS is switched off.", "nts", "clock");
                return;
            }

            timeCheckTimer = TimeProvider.CreateTimer(
                                 _ => _ = CheckTheClockAsync(),
                                 null,
                                 TimeSpan.FromMinutes(1),
                                 TimeCheckEvery
                             );

            // Named rather than counted, because this is written once at a
            // start and somebody reading it is checking that the file took
            // effect. "4 time servers" would not tell them which four.
            // Trimmed, because this is a sentence somebody reads. The root
            // dot belongs on a name going back into a file - see how the
            // configuration is written - and not in the middle of a line of
            // prose, where it reads as a typing mistake.
            var asking = timeSources.Bands().SelectMany(band => band).Select(source => source.Hostname.Trimmed).ToArray();

            Log.Info(
                $"The clock of this station will be checked against {String.Join(", ", asking)} every {TimeCheckEvery.TotalMinutes:F0} minute(s)" +
                (asking.Length > 1 ? $", at least {timeSources.MinServers} of which must answer" : "") +
                (LegalTimeAuthority is not null ? $", which the operator says is {LegalTimeAuthority}." : "."),
                "nts", "clock"
            );

        }

        #endregion

        #region (private) CheckTheClockAsync()

        /// <summary>
        /// One check, quietly.
        /// </summary>
        /// <remarks>
        /// The same exchange the "Sync now" button does, so there is one way of
        /// asking and one place that records the answer. A failed check is a
        /// warning and not an exception: a time server that cannot be reached
        /// for ten minutes is a thing that happens, and the display already
        /// says the time is unverified when the last check has gone stale.
        /// </remarks>
        private async Task CheckTheClockAsync()
        {

            try
            {

                var result = await SyncTimeAsync();

                if (result.Value<Boolean>("ok") != true)
                    Log.Warning(
                        $"The clock of this station could not be checked: {result.Value<String>("error") ?? "no answer"}.",
                        "nts", "clock"
                    );

            }
            catch (Exception e)
            {
                Log.Warning($"The clock of this station could not be checked: {e.Message}", "nts", "clock");
            }

        }

        #endregion

        #region ClockJSON()

        /// <summary>
        /// What time it is here, and what that is worth, for the display.
        /// </summary>
        /// <remarks>
        /// Everything a screen needs to say something true, and nothing it
        /// would have to interpret: the time, whether it has been checked,
        /// against whom, how long ago, by how much it was out, and whether all
        /// of that adds up to legal time. The one word the display must not
        /// invent is "legal", so it is decided here and sent as a fact.
        /// </remarks>
        public JObject ClockJSON()
        {

            var now       = TimeProvider.GetUtcNow();
            var asking    = timeSources.Bands().SelectMany(band => band).ToArray();
            var checkedAt = lastTimeCheck;
            var offset    = lastTimeCheckOffset;
            var age       = checkedAt.HasValue ? now - checkedAt.Value : (TimeSpan?) null;

            var fresh     = age.HasValue && age.Value <= LegalTimeMaxAge;
            var close     = offset.HasValue && offset.Value.Duration() <= LegalTimeTolerance;

            // Every one of these has to hold. Each is a different way of not
            // knowing, and none of them is repaired by the others.
            var legal     = LegalTimeAuthority is not null &&
                            NTSEnabled                     &&
                            fresh                          &&
                            close;

            return new JObject(

                       new JProperty("now",             now.ToString("o")),

                       // Said out loud: the number above is this station's own
                       // clock, and the check below did not set it.
                       new JProperty("source",          "system"),

                       new JProperty("nts",             new JObject(
                           new JProperty("enabled",       NTSEnabled),
                           new JProperty("server",        NTSEnabled && asking.Length == 1
                                                              ? asking[0].Hostname.ToString()
                                                              : null),
                           new JProperty("servers",       new JArray(asking.Select(source => source.Hostname.ToString()))),
                           new JProperty("lastServer",    lastTimeCheckServer),
                           new JProperty("asked",         lastTimeCheckAsked),
                           new JProperty("answered",      lastTimeCheckAnswered),
                           new JProperty("checkedAt",     checkedAt?.ToString("o")),
                           new JProperty("ageSeconds",    age.HasValue ? Math.Round(age.Value.TotalSeconds, 1) : null),
                           new JProperty("offset_ms",     offset.HasValue ? Math.Round(offset.Value.TotalMilliseconds, 1) : null),
                           new JProperty("everySeconds",  Math.Round(TimeCheckEvery.TotalSeconds, 0))
                       )),

                       new JProperty("legal",           legal),
                       new JProperty("authority",       LegalTimeAuthority),

                       // Why not, when not - so that a screen can say something
                       // more useful than "no" and somebody looking at it knows
                       // what to go and fix.
                       new JProperty("why",             legal
                                                            ? null
                                                            : LegalTimeAuthority is null ? "notClaimed"
                                                            : !NTSEnabled                ? "ntsOff"
                                                            : !checkedAt.HasValue        ? "neverChecked"
                                                            : !fresh                     ? "stale"
                                                            : !close                     ? "offBy"
                                                            : "unknown"),

                       new JProperty("toleranceSeconds",  Math.Round(LegalTimeTolerance.TotalSeconds, 3)),
                       new JProperty("maxAgeSeconds",     Math.Round(LegalTimeMaxAge.   TotalSeconds, 0))

                   );

        }

        #endregion

    }

}
