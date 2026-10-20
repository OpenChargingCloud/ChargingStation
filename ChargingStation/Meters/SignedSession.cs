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

using System.Globalization;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

#endregion

namespace cloud.charging.open.ChargingStation.Meters
{

    /// <summary>
    /// Where a charging session stands with the meter that signs it.
    /// </summary>
    public enum SignedSessionState
    {

        /// <summary>The meter is being asked to begin it.</summary>
        Starting,

        /// <summary>The meter measures it.</summary>
        Running,

        /// <summary>The meter is being asked to end it.</summary>
        Stopping,

        /// <summary>It ended in a document the meter signed.</summary>
        Signed,

        /// <summary>It ended in no document: the meter could not be asked, or said no - see the error.</summary>
        Unsigned

    }


    /// <summary>
    /// A charging session as the meter of its EVSE signs it: what it was begun
    /// with, and the OCMF document it ended in.
    /// </summary>
    /// <remarks>
    /// Kept apart from the session on the display, which knows nothing of
    /// meters and is gone when it ends. This is what is left of it: the
    /// document, the key it can be checked with, and what ChargyCore made of
    /// the two when the station read them.
    /// </remarks>
    /// <param name="Id">What the station calls it, and tells the meter to call it.</param>
    /// <param name="EVSE">The EVSE it was at.</param>
    /// <param name="StartedAt">When it began.</param>
    /// <param name="State">Where it stands with the meter.</param>
    /// <param name="IdentificationType">How whoever charged was identified, OCMF "IT".</param>
    /// <param name="Identification">Whom they were identified as, OCMF "ID".</param>
    /// <param name="EVSEId">The EVSE as OCMF "CI" says it.</param>
    /// <param name="TariffText">The tariff, OCMF "TT".</param>
    /// <param name="StoppedAt">When it ended.</param>
    /// <param name="Error">Why it is not signed, or not yet.</param>
    /// <param name="Document">The OCMF document.</param>
    /// <param name="PublicKey">The key it is signed with, as hex.</param>
    /// <param name="Verification">What ChargyCore said of the document and the key.</param>
    /// <param name="StartValue">The reading it began at, in kWh, as the document says.</param>
    /// <param name="StopValue">The reading it ended at, in kWh, as the document says.</param>
    public sealed record SignedSession(String              Id,
                                       Byte                EVSE,
                                       DateTimeOffset      StartedAt,
                                       SignedSessionState  State,
                                       String?             IdentificationType  = null,
                                       String?             Identification      = null,
                                       String?             EVSEId              = null,
                                       String?             TariffText          = null,
                                       DateTimeOffset?     StoppedAt           = null,
                                       String?             Error               = null,
                                       String?             Document            = null,
                                       String?             PublicKey           = null,
                                       String?             Verification        = null,
                                       Decimal?            StartValue          = null,
                                       Decimal?            StopValue           = null)
    {

        #region Properties

        /// <summary>
        /// Whether it is over with the meter: signed, or never to be.
        /// </summary>
        public Boolean IsOver
            => State is SignedSessionState.Signed or SignedSessionState.Unsigned;

        #endregion


        #region ToJSON() / TryParse(JSON)

        public JObject ToJSON()
        {

            var json = new JObject(
                           new JProperty("id",         Id),
                           new JProperty("evse",       EVSE),
                           new JProperty("startedAt",  StartedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)),
                           new JProperty("state",      State.ToString().ToLowerInvariant())
                       );

            void Add(String Name, Object? Value)
            {
                if (Value is not null)
                    json[Name] = JToken.FromObject(Value);
            }

            Add("identificationType",  IdentificationType);
            Add("identification",      Identification);
            Add("evseId",              EVSEId);
            Add("tariffText",          TariffText);
            Add("stoppedAt",           StoppedAt?.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            Add("error",               Error);
            Add("startValue",          StartValue);
            Add("stopValue",           StopValue);
            Add("energy_kWh",          StartValue is Decimal from && StopValue is Decimal to ? to - from : null);
            Add("verification",        Verification);
            Add("ocmf",                Document);
            Add("publicKey",           PublicKey);

            return json;

        }

        /// <summary>
        /// One read back from disk, or null where it says nothing usable.
        /// </summary>
        public static SignedSession? TryParse(JObject JSON)
        {

            var id    = JSON["id"]?.   Value<String>();
            var evse  = JSON["evse"]?. Value<Int32>();
            var state = JSON["state"]?.Value<String>();

            if (id is null || evse is not (>= 1 and <= 255) ||
                !Enum.TryParse<SignedSessionState>(state, true, out var parsedState) ||
                !TryMoment(JSON["startedAt"], out var startedAt))
                return null;

            return new SignedSession(
                       id,
                       (Byte) evse,
                       startedAt,
                       parsedState,
                       Text(JSON, "identificationType"),
                       Text(JSON, "identification"),
                       Text(JSON, "evseId"),
                       Text(JSON, "tariffText"),
                       TryMoment(JSON["stoppedAt"], out var stoppedAt) ? stoppedAt : null,
                       Text(JSON, "error"),
                       Text(JSON, "ocmf"),
                       Text(JSON, "publicKey"),
                       Text(JSON, "verification"),
                       JSON["startValue"]?.Type is JTokenType.Float or JTokenType.Integer ? JSON["startValue"]!.Value<Decimal>() : null,
                       JSON["stopValue"]?. Type is JTokenType.Float or JTokenType.Integer ? JSON["stopValue"]!. Value<Decimal>() : null
                   );

        }

        private static String? Text(JObject JSON, String Name)

            => JSON[Name]?.Type == JTokenType.String
                   ? JSON[Name]!.Value<String>()
                   : null;

        private static Boolean TryMoment(JToken? Token, out DateTimeOffset Moment)
        {

            Moment = default;

            // Newtonsoft reads an ISO timestamp into a DateTime of its own.
            if (Token?.Type == JTokenType.Date)
            {
                Moment = new DateTimeOffset(DateTime.SpecifyKind(Token.Value<DateTime>(), DateTimeKind.Utc));
                return true;
            }

            return Token?.Type == JTokenType.String &&
                   DateTimeOffset.TryParse(Token.Value<String>(), CultureInfo.InvariantCulture,
                                           DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out Moment);

        }

        #endregion

    }


    /// <summary>
    /// The signed sessions of this station: one file each, beside the
    /// configuration file, and the recent ones in memory.
    /// </summary>
    /// <remarks>
    /// A file each, written beside and moved into place, so that a document is
    /// never half written and one that could not be written takes no other with
    /// it. Nothing here is ever deleted: a signed document is what a charge can
    /// be checked against, and how long that has to stay possible is not for a
    /// station to decide.
    /// </remarks>
    public sealed class SignedSessionStore
    {

        #region Data

        /// <summary>
        /// The directory beside the configuration file.
        /// </summary>
        public const String  DefaultDirectoryName  = "signed-sessions";

        /// <summary>
        /// How many are kept in memory, and handed to a page.
        /// </summary>
        public const Int32   Recent                = 200;

        private readonly Lock                 sync     = new ();
        private readonly List<SignedSession>  sessions = [];

        #endregion

        #region Properties

        /// <summary>The directory they are kept in.</summary>
        public String  Path  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Open the signed sessions kept in a directory.
        /// </summary>
        /// <param name="Path">The directory.</param>
        /// <param name="Unreadable">Said of a file that could not be read.</param>
        public SignedSessionStore(String           Path,
                                  Action<String>?  Unreadable = null)
        {

            this.Path = System.IO.Path.GetFullPath(Path);

            if (!Directory.Exists(this.Path))
                return;

            foreach (var file in Directory.EnumerateFiles(this.Path, "*.json").Order().TakeLast(Recent))
            {
                try
                {
                    if (SignedSession.TryParse(JObject.Parse(File.ReadAllText(file))) is SignedSession session)
                        sessions.Add(session);
                    else
                        Unreadable?.Invoke($"'{file}' is no signed session.");
                }
                catch (Exception e)
                {
                    Unreadable?.Invoke($"'{file}' could not be read: {e.Message}");
                }
            }

        }

        #endregion


        #region All / Of(Id) / Open(EVSE)

        /// <summary>
        /// The recent ones, the one that began last first.
        /// </summary>
        /// <remarks>
        /// By when they began, and not by when they were kept: a session the
        /// meter still measured, kept when the next began, is older than that
        /// one.
        /// </remarks>
        public IReadOnlyList<SignedSession> All
        {
            get
            {
                lock (sync)
                    return sessions.AsEnumerable().Reverse().OrderByDescending(session => session.StartedAt).ToArray();
            }
        }

        /// <summary>
        /// The one of that id, where it is a recent one.
        /// </summary>
        public SignedSession? Of(String Id)
        {
            lock (sync)
                return sessions.LastOrDefault(session => session.Id == Id);
        }

        /// <summary>
        /// The ones not over with the meter: begun, running, or ending.
        /// </summary>
        public IReadOnlyList<SignedSession> Open
        {
            get
            {
                lock (sync)
                    return sessions.Where(session => !session.IsOver).ToArray();
            }
        }

        #endregion

        #region Keep(Session)

        /// <summary>
        /// Keep a session as it stands now, in place of how it stood before.
        /// </summary>
        /// <exception cref="IOException">When it could not be written down.</exception>
        public void Keep(SignedSession Session)
        {

            lock (sync)
            {

                var index = sessions.FindIndex(session => session.Id == Session.Id);

                if (index >= 0)
                    sessions[index] = Session;
                else
                {
                    sessions.Add(Session);
                    if (sessions.Count > Recent)
                        sessions.RemoveAt(0);
                }

                Directory.CreateDirectory(Path);

                var file      = System.IO.Path.Combine(Path, $"{Session.StartedAt.UtcDateTime:yyyyMMdd-HHmmss}-{Session.Id}.json");
                var temporary = file + ".new";

                File.WriteAllText(temporary, Session.ToJSON().ToString(Formatting.Indented));
                File.Move(temporary, file, overwrite: true);

            }

        }

        #endregion

    }

}
