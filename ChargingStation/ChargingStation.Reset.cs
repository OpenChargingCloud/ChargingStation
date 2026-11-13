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

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using Newtonsoft.Json.Linq;

using cloud.charging.open.ChargingStation.EVSEs;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// A restart of this station: once nothing is charging any more - a Reset
    /// of OCPP 2.1 OnIdle for the whole station, and a Soft one of OCPP 1.6 -
    /// or at once, which ends what is charging - Immediate, and Hard. Asked
    /// over OCPP, on the web page and at the command line.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A restart of this station is its OCPP side built anew: both nodes made
    /// again from what the station is, what they were connected to hung up and
    /// dialled again, and a BootNotification over each - what a change of the
    /// EVSEs does. The page, SSH and the configuration stay as they are.
    /// </para>
    /// <para>
    /// While something charges, the restart waits for it, and no new session
    /// starts: otherwise a busy station would never become idle. A free EVSE
    /// is shown out of service until then. Over OCPP 2.1 that is "Scheduled";
    /// OCPP 1.6 has no such word and says "Accepted" - and where its text ends
    /// running transactions with a soft reset, this station waits for them, as
    /// for OnIdle (white paper "Signed OCPP Messages and Signer Roles", section
    /// 5.3: a soft reset needs "reset:run", the right of the support).
    /// </para>
    /// <para>
    /// A restart at once ends every session first, as a change of the EVSEs
    /// does, and is the same restart afterwards: a Reset of OCPP 2.1 Immediate
    /// for the whole station, and a Hard one of OCPP 1.6. It is the one of the
    /// two that can do damage, and a right of its own: "reset-immediate:run".
    /// </para>
    /// <para>
    /// A Reset of OCPP 2.1 for a single EVSE is left to the node, as before:
    /// it accepts and does nothing.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// Why this station restarts once nothing charges any more, while it
        /// waits for that; null when it does not.
        /// </summary>
        private String?  restartOnceIdle;

        /// <summary>
        /// 1 while the OCPP side is built anew for a restart.
        /// </summary>
        private Int32    restarting;

        /// <summary>
        /// 1 while a restart at once still has the sessions to end: after the
        /// answer to its Reset went out.
        /// </summary>
        private Int32    endSessionsForRestart;

        #endregion

        #region Properties

        /// <summary>
        /// Why this station restarts once nothing charges any more, or null
        /// where no restart waits.
        /// </summary>
        public String? RestartOnceIdle
            => Volatile.Read(ref restartOnceIdle);

        #endregion


        #region (private) AttachReset(ChargePoint, ChargingStation)

        /// <summary>
        /// Answer the Resets both new OCPP nodes are sent, and restart once the
        /// answer is on its way and nothing charges.
        /// </summary>
        private void AttachReset(OCPPv1_6.TestChargePointNode         ChargePoint,
                                 OCPPv2_1.CS.TestChargingStationNode  ChargingStation)
        {

            ChargePoint.    OnResetByOwner = Reset16;
            ChargingStation.OnResetByOwner = Reset21;

            // After the answer, which goes over the connection the restart
            // hangs up: answered first, so that the CSMS hears it.
            ChargePoint.    OCPP.OUT.OnResetResponseSent += (timestamp, sender, connection, request, response, runtime, result, cancellationToken) => {
                RestartAsAskedFor();
                return Task.CompletedTask;
            };

            ChargingStation.OCPP.OUT.OnResetResponseSent += (timestamp, sender, connection, request, response, runtime, result, cancellationToken) => {
                RestartAsAskedFor();
                return Task.CompletedTask;
            };

        }

        #endregion

        #region (private) Reset16(Request, CancellationToken) / Reset21(Request, CancellationToken)

        /// <summary>
        /// A Soft reset of OCPP 1.6 is a restart once idle, a Hard one a
        /// restart at once. Both are "Accepted".
        /// </summary>
        private Task<OCPPv1_6.CP.ResetResponse?> Reset16(OCPPv1_6.CS.ResetRequest  Request,
                                                         CancellationToken          CancellationToken)
        {

            if (Request.ResetType == OCPPv1_6.ResetType.Soft)
                RestartWhenIdle("a Soft reset over OCPP 1.6");

            else if (Request.ResetType == OCPPv1_6.ResetType.Hard)
                RestartWhenIdle("a Hard reset over OCPP 1.6", AtOnce: true);

            else
                return Task.FromResult<OCPPv1_6.CP.ResetResponse?>(null);

            return Task.FromResult<OCPPv1_6.CP.ResetResponse?>(
                       new OCPPv1_6.CP.ResetResponse(
                           Request,
                           OCPPv1_6.ResetStatus.Accepted
                       )
                   );

        }

        /// <summary>
        /// A Reset of OCPP 2.1 OnIdle for the whole station is a restart once
        /// idle: "Accepted" where nothing charges, "Scheduled" where something
        /// does. Immediate, it is a restart at once, and "Accepted". One for a
        /// single EVSE is the node's, as before.
        /// </summary>
        private Task<OCPPv2_1.CS.ResetResponse?> Reset21(OCPPv2_1.CSMS.ResetRequest  Request,
                                                         CancellationToken            CancellationToken)
        {

            if (Request.EVSEId.HasValue)
                return Task.FromResult<OCPPv2_1.CS.ResetResponse?>(null);

            if (Request.ResetType == OCPPv2_1.ResetType.Immediate)
            {

                RestartWhenIdle("a Reset Immediate over OCPP 2.1", AtOnce: true);

                return Task.FromResult<OCPPv2_1.CS.ResetResponse?>(
                           new OCPPv2_1.CS.ResetResponse(
                               Request,
                               OCPPv2_1.ResetStatus.Accepted
                           )
                       );

            }

            if (Request.ResetType != OCPPv2_1.ResetType.OnIdle)
                return Task.FromResult<OCPPv2_1.CS.ResetResponse?>(null);

            RestartWhenIdle("a Reset OnIdle over OCPP 2.1");

            return Task.FromResult<OCPPv2_1.CS.ResetResponse?>(
                       new OCPPv2_1.CS.ResetResponse(
                           Request,
                           sessions.IsEmpty
                               ? OCPPv2_1.ResetStatus.Accepted
                               : OCPPv2_1.ResetStatus.Scheduled
                       )
                   );

        }

        #endregion

        #region Restart(AtOnce, By)

        /// <summary>
        /// Restart this station, as its web page and its command line ask for
        /// it: once nothing charges any more, or at once, which ends what is
        /// charging.
        /// </summary>
        /// <param name="AtOnce">Whether what is charging is ended for it.</param>
        /// <param name="By">Who asked for it, for the log: "'alice' on the web page".</param>
        /// <returns>Whether it restarts "now" or "onceIdle", how many sessions were charging, and how many of them it ended.</returns>
        public JObject Restart(Boolean  AtOnce,
                               String   By)
        {

            var charging = sessions.Count;

            RestartWhenIdle(By, AtOnce);
            RestartAsAskedFor();

            return new JObject(
                       new JProperty("restart",   AtOnce || charging == 0 ? "now" : "onceIdle"),
                       new JProperty("sessions",  charging),
                       new JProperty("ended",     AtOnce ? charging : 0)
                   );

        }

        #endregion

        #region (private) RestartWhenIdle(Why, AtOnce = false)

        /// <summary>
        /// Restart once nothing charges any more, and start no new session until
        /// then. A restart that waits already is the one asked for again - and
        /// asked for at once, it no longer waits.
        /// </summary>
        /// <param name="Why">What asked for it, for the log: "a Soft reset over OCPP 1.6".</param>
        /// <param name="AtOnce">Whether what is charging is ended for it, by <see cref="RestartAsAskedFor"/>.</param>
        private void RestartWhenIdle(String   Why,
                                     Boolean  AtOnce = false)
        {

            if (AtOnce)
                Volatile.Write(ref endSessionsForRestart, 1);

            if (Interlocked.CompareExchange(ref restartOnceIdle, Why, null) is String waiting)
            {
                Log.Notice($"{Capitalized(Why)} asked for the restart that waits already ({waiting}){(AtOnce ? ", and for it at once" : "")}.", "ocpp", "reset");
                return;
            }

            var charging = sessions.Count;

            Log.Notice(
                charging == 0
                    ? $"{Capitalized(Why)}: nothing is charging, so this station restarts its OCPP side now."
                    : AtOnce
                          ? $"{Capitalized(Why)}: this station ends the {charging} session(s) charging now, and restarts its OCPP side."
                          : $"{Capitalized(Why)}: this station restarts its OCPP side once the {charging} session(s) charging now have ended, " +
                             "and starts no new session until then.",
                "ocpp", "reset"
            );

        }

        private static String Capitalized(String Text)

            => Text.Length == 0
                   ? Text
                   : Char.ToUpperInvariant(Text[0]) + Text[1..];

        #endregion

        #region (private) WhyNoSessionStarts()

        /// <summary>
        /// Why no new session may start now - a restart waits for the running
        /// ones to end - or null where one may.
        /// </summary>
        private String? WhyNoSessionStarts()

            => RestartOnceIdle is null
                   ? null
                   : "This station restarts once nothing is charging any more; until then no session starts.";

        #endregion

        #region (private) RestartAsAskedFor()

        /// <summary>
        /// What was asked for: where it was a restart at once, end what is
        /// charging; then restart where nothing charges. After the answer to a
        /// Reset went out, and at once where the page or the command line
        /// asked.
        /// </summary>
        private void RestartAsAskedFor()
        {

            if (Interlocked.Exchange(ref endSessionsForRestart, 0) == 1)
                StopAllSessions("this station restarts at once");

            RestartIfIdle();

        }

        #endregion

        #region (private) RestartIfIdle()

        /// <summary>
        /// Restart where a restart waits and nothing charges any more: after the
        /// answer to the Reset went out, and whenever a session ends.
        /// </summary>
        private void RestartIfIdle()
        {

            if (RestartOnceIdle is null || !sessions.IsEmpty)
                return;

            if (Interlocked.CompareExchange(ref restarting, 1, 0) != 0)
                return;

            // Not on the thread that ended the session or sent the answer: that
            // may hold the lock a rebuild takes, or be the connection it hangs up.
            _ = Task.Run(RestartAsync);

        }

        #endregion

        #region (private) RestartAsync()

        /// <summary>
        /// Build the OCPP side anew, as a change of the EVSEs does, and dial
        /// again what it was connected to.
        /// </summary>
        private async Task RestartAsync()
        {

            try
            {

                OCPPv1_6.   TestChargePointNode      old16;
                OCPPv2_1.CS.TestChargingStationNode  old21;
                String?                              why;

                await reconfigureLock.WaitAsync();

                try
                {

                    why = Volatile.Read(ref restartOnceIdle);

                    // Done already, or a session came in between, whose end
                    // does it.
                    if (why is null || !sessions.IsEmpty)
                        return;

                    // A station stopped meanwhile has nothing to dial again,
                    // and its next start is a restart of its own.
                    if (!started)
                    {
                        Volatile.Write(ref restartOnceIdle, null);
                        return;
                    }

                    (old16, old21) = RebuildTheOCPPNodes(EVSEChange.None);

                    // Sessions may start again: the restart they waited for is
                    // done, and what follows is only dialling.
                    Volatile.Write(ref restartOnceIdle, null);

                }
                catch (Exception e)
                {

                    // As for a change of the EVSEs, a process restart repairs
                    // it; until then the station is not to stay closed.
                    Volatile.Write(ref restartOnceIdle, null);

                    Log.Critical($"This station could not restart its OCPP side: {e.Message} Restart it.", "ocpp", "reset");
                    return;

                }
                finally
                {
                    reconfigureLock.Release();
                }

                Log.Notice($"This station restarted its OCPP side, as {why} asked for.", "ocpp", "reset");

                await DialAgain(old16, old21, $"This charging station restarted, as {why} asked for");

            }
            catch (Exception e)
            {
                Log.Warning($"Dialling again after a restart failed: {e.GetBaseException().Message}", "ocpp", "reset");
            }
            finally
            {
                Volatile.Write(ref restarting, 0);
            }

        }

        #endregion

    }

}
