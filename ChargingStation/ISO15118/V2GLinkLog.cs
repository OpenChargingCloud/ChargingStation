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

using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.ChargingStation.ISO15118
{

    /// <summary>
    /// The station's log, as one V2G link writes into it: every line said with
    /// the EVSE it is about in front.
    /// </summary>
    /// <remarks>
    /// A station with one link said "SDP is answering" and everybody knew
    /// where; with a link below every EVSE the same line is about one cable of
    /// several, and a log that does not say which is a log somebody has to
    /// guess at while a car waits.
    /// </remarks>
    /// <param name="Into">The station's log.</param>
    /// <param name="About">What every line is about, e.g. "EVSE A"; nothing in front where null.</param>
    internal sealed class V2GLinkLog(EventLog  Into,
                                     String?   About)
    {

        private String Said(String Message)
            => About is null ? Message : $"{About}: {Message}";

        public LogEntry Log      (LogLevel Level, String Message, params String[] Tags)                 => Into.Log(Level, Said(Message), Tags);
        public LogEntry Log      (LogLevel Level, String Message, JObject? Data, params String[] Tags)  => Into.Log(Level, Said(Message), Data, Tags);

        public LogEntry Debug    (String Message, params String[] Tags)  => Into.Debug   (Said(Message), Tags);
        public LogEntry Info     (String Message, params String[] Tags)  => Into.Info    (Said(Message), Tags);
        public LogEntry Notice   (String Message, params String[] Tags)  => Into.Notice  (Said(Message), Tags);
        public LogEntry Warning  (String Message, params String[] Tags)  => Into.Warning (Said(Message), Tags);
        public LogEntry Error    (String Message, params String[] Tags)  => Into.Error   (Said(Message), Tags);
        public LogEntry Critical (String Message, params String[] Tags)  => Into.Critical(Said(Message), Tags);

        public LogEntry Exception(Exception Exception, String Message, params String[] Tags)  => Into.Exception(Exception, Said(Message), Tags);

    }

}
