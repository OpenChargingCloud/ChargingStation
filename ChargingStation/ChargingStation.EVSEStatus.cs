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

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// How each EVSE of this station stands right now, as the EVSEs page of the
    /// web interface shows it, and the news that it changed.
    /// </summary>
    /// <remarks>
    /// What an EVSE is doing changes in more places than one: a card held to a
    /// reader, a local app, a payment, a CSMS reserving it over OCPP, a
    /// reservation running out by itself, the configuration saved. Rather than
    /// each of them remembering to say so - and the one that forgets leaving a
    /// page that is quietly wrong - the station looks once a second at what
    /// they add up to, and says so only when that is different from the last
    /// time. A second is what the page can be behind at worst, and looking costs
    /// no more than drawing the display does.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// How often the station looks at whether an EVSE changed.
        /// </summary>
        public static readonly TimeSpan  EVSEWatchInterval = TimeSpan.FromSeconds(1);

        private          ITimer?  evseWatch;
        private          String?  lastEVSEs;
        private readonly Lock     evseWatchLock = new();

        #endregion

        #region Events

        /// <summary>
        /// The EVSEs of this station, as <see cref="EVSEsStatusJSON"/> says them
        /// without its timestamp, whenever what it says changed.
        /// </summary>
        public event Action<JObject>?  EVSEsChanged;

        #endregion


        #region (private) StatusOf(EVSE, Session, Reservation)

        /// <summary>
        /// Whether an EVSE is free, charging, held or out of service.
        /// </summary>
        /// <remarks>
        /// A car that is charging is charging, whatever the configuration now
        /// says about the outlet it is on: an outlet taken out of service under
        /// a running session goes out of service when that session ends, not
        /// while somebody is standing at it. Out of service beats held, because
        /// a hold on an outlet nobody can use is let go of at the same moment.
        /// </remarks>
        private static EVSEStatus StatusOf(EVSEConfig        EVSE,
                                           ChargingSession?  Session,
                                           JObject?          Reservation)

            => Session is not null
                   ? EVSEStatus.Occupied
                   : !EVSE.Operative
                         ? EVSEStatus.Inoperative
                         : Reservation is not null
                               ? EVSEStatus.Reserved
                               : EVSEStatus.Available;

        #endregion

        #region EVSEsStatusJSON()

        /// <summary>
        /// How each EVSE of this station stands right now, and what time it is
        /// here.
        /// </summary>
        /// <remarks>
        /// The time, because the page says how long a session has been running,
        /// and the clock it would otherwise ask is the browser's.
        /// </remarks>
        public JObject EVSEsStatusJSON()
        {

            var now  = TimeProvider.GetUtcNow();
            var json = EVSEsJSON(now);

            json.AddFirst(new JProperty("timestamp", now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ")));

            return json;

        }

        #endregion

        #region (private) EVSEsJSON(Now)

        private JObject EVSEsJSON(DateTimeOffset Now)

            => new (
                   new JProperty("evses", new JArray(EVSEs.Select(evse => EVSEStatusJSON(evse, Now))))
               );

        #endregion

        #region (private) EVSEStatusJSON(EVSE, Now)

        /// <summary>
        /// One EVSE, as the EVSEs page shows it.
        /// </summary>
        /// <remarks>
        /// No card's UID, although the page is for those who run the station:
        /// what this says also goes out on the event stream, which every signed-in
        /// user follows, and a UID is somebody's - the log says it where it is
        /// needed. Nothing simulated either: the display shows a power figure
        /// that no meter measured where no meter is read, and on a page about
        /// the EVSE itself that would read as a measurement. What a meter that
        /// is read measures is in <see cref="MetersStatusJSON"/>.
        /// </remarks>
        private JObject EVSEStatusJSON(EVSEConfig      EVSE,
                                       DateTimeOffset  Now)
        {

            sessions.TryGetValue(EVSE.Id, out var session);

            var reservation = ReservationJSON(EVSE.Id, Now);

            return new JObject(

                       new JProperty("id",                 EVSE.Id),
                       new JProperty("label",              EVSE.PhysicalReference ?? EVSE.Id.ToString()),
                       new JProperty("physicalReference",  EVSE.PhysicalReference),
                       new JProperty("status",             StatusOf(EVSE, session, reservation).ToString().ToLowerInvariant()),
                       new JProperty("operative",          EVSE.Operative),
                       new JProperty("closing",            session is not null && !EVSE.Operative),
                       new JProperty("maxPower_kW",        EVSE.MaxPower_kW),

                       new JProperty("connectors",         new JArray(EVSE.Connectors.Select(connector => new JObject(
                                                               new JProperty("id",           connector.Id),
                                                               new JProperty("type",         connector.Type),
                                                               new JProperty("maxPower_kW",  connector.MaxPower_kW)
                                                           )))),

                       new JProperty("session",            session is null
                                                               ? null
                                                               : new JObject(
                                                                     new JProperty("method",     session.Method.ToString()),
                                                                     new JProperty("startedAt",  session.StartedAt.ToString("o")),
                                                                     new JProperty("provider",   ProviderJSON(session))
                                                                 )),

                       new JProperty("reservation",        reservation),

                       new JProperty("v2g",                V2GStatusJSON(EVSE.Id)),

                       // Whether it is read, and not what it measures: that is
                       // news every second, and goes out as "meters" - see
                       // ChargingStation.Meters.cs - so that this says only
                       // what changes when the EVSE does.
                       new JProperty("meter",              EVSE.MeterType is null && EVSE.MeterSerialNumber is null && metersFile.MeterOf(EVSE.Id) is null
                                                               ? null
                                                               : new JObject(
                                                                     new JProperty("type",          EVSE.MeterType),
                                                                     new JProperty("serialNumber",  EVSE.MeterSerialNumber),
                                                                     new JProperty("read",          metersFile.MeterOf(EVSE.Id) is not null)
                                                                 ))

                   );

        }

        #endregion

        #region (private) V2GStatusJSON(EVSE)

        /// <summary>
        /// What is going on below the cable of one EVSE: null where it is to
        /// have no V2G link, whether the one it is to have is up, and what that
        /// one is doing - SDP, the vehicle connected and over what.
        /// </summary>
        private JObject? V2GStatusJSON(Byte EVSE)
        {

            if (!V2GOptionsOf(EVSE).Enabled)
                return null;

            var json = V2GLinkOf(EVSE)?.StatusJSON() ?? new JObject();

            json.AddFirst(new JProperty("up", V2GLinkOf(EVSE) is not null));

            return json;

        }

        #endregion

        #region ReportEVSEChanges()

        /// <summary>
        /// Look at the EVSEs, and tell <see cref="EVSEsChanged"/> when they are
        /// not as they were the last time - what the station does once a second
        /// while it runs.
        /// </summary>
        /// <returns>Whether they changed.</returns>
        public Boolean ReportEVSEChanges()
        {

            JObject json;

            lock (evseWatchLock)
            {

                json = EVSEsJSON(TimeProvider.GetUtcNow());

                var said = json.ToString(Formatting.None);

                if (said == lastEVSEs)
                    return false;

                lastEVSEs = said;

            }

            try
            {
                EVSEsChanged?.Invoke(json);
            }
            catch (Exception e)
            {
                Log.Warning($"Telling who follows the EVSEs that they changed failed: {e.Message}", "evses");
            }

            return true;

        }

        #endregion

        #region (private) StartWatchingTheEVSEs() / StopWatchingTheEVSEs()

        private void StartWatchingTheEVSEs()
        {

            lock (evseWatchLock)
                lastEVSEs = EVSEsJSON(TimeProvider.GetUtcNow()).ToString(Formatting.None);

            // The meters on the same beat: a second is what their page can be
            // behind at worst, as it is for the EVSEs.
            evseWatch = TimeProvider.CreateTimer(_ => { ReportEVSEChanges(); ReportMeterChanges(); },
                                                 null,
                                                 EVSEWatchInterval,
                                                 EVSEWatchInterval);

        }

        private void StopWatchingTheEVSEs()
        {
            evseWatch?.Dispose();
            evseWatch = null;
        }

        #endregion

    }

}
