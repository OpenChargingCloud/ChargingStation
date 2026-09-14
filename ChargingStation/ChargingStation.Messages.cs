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
using cloud.charging.open.ChargingStation.Kiosk;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What a back end has asked this station to put in front of whoever is
    /// standing at it.
    /// </summary>
    /// <remarks>
    /// OCPP 2.1's display messages, kept where OCPP keeps them - in the 2.1
    /// node, set by <c>SetDisplayMessage</c> and taken off by
    /// <c>ClearDisplayMessage</c>.
    ///
    /// Three things decide whether a message is on the screen, and all three
    /// come from the message rather than from the screen: when it applies
    /// (a start and an end), where it applies (the whole station, or one
    /// outlet), and what the thing it applies to has to be doing (charging,
    /// idle, out of service). A message with none of them set is on the screen
    /// always and everywhere, which is the simple case and not the only one.
    ///
    /// They stack. A station can be holding several at once and a priority says
    /// which matters: <c>AlwaysFront</c> and <c>InFront</c> stay put, and
    /// everything else takes its turn in a cycle. That is the part worth
    /// building before it is needed - a screen that can only ever show one line
    /// has to be taken apart again the first time a second message arrives.
    /// </remarks>
    public partial class ChargingStation
    {

        #region MessageStateOf(EVSE) / StationMessageState()

        /// <summary>
        /// What one outlet is doing, in the words OCPP ties messages to.
        /// </summary>
        /// <remarks>
        /// A held outlet is idle: nothing is drawing from it, and somebody
        /// walking up to it is in exactly the position a message for the idle
        /// state is written for. <c>Faulted</c> is deliberately never returned -
        /// nothing in this station diagnoses a fault, and a state it cannot
        /// tell the truth about is one it should not claim.
        /// </remarks>
        public OCPPv2_1.MessageState MessageStateOf(EVSEConfig EVSE)

            => !EVSE.Operative
                   ? OCPPv2_1.MessageState.Unavailable
                   : sessions.ContainsKey(EVSE.Id)
                         ? OCPPv2_1.MessageState.Charging
                         : OCPPv2_1.MessageState.Idle;

        /// <summary>
        /// What the station as a whole is doing.
        /// </summary>
        /// <remarks>
        /// Charging while anything on it is, unavailable when nothing on it can
        /// be used, and idle in between. A message for the whole housing is
        /// read by somebody who is looking at the housing, so the question it
        /// has to answer is "is anything happening here", not "is this
        /// particular socket busy".
        /// </remarks>
        public OCPPv2_1.MessageState StationMessageState()

            => EVSEs.Any(evse => evse.Operative && sessions.ContainsKey(evse.Id))
                   ? OCPPv2_1.MessageState.Charging
                   : EVSEs.All(evse => !evse.Operative)
                         ? OCPPv2_1.MessageState.Unavailable
                         : OCPPv2_1.MessageState.Idle;

        #endregion

        #region DisplayMessagesJSON(State, EVSEId, Now)

        /// <summary>
        /// The messages to show at one place right now, most important first.
        /// </summary>
        public JArray DisplayMessagesJSON(OCPPv2_1.MessageState  State,
                                          Byte?                  EVSEId,
                                          DateTimeOffset         Now)

            => new (
                   cs02.DisplayMessagesFor(
                            State,
                            EVSEId.HasValue ? OCPPv2_1.EVSE_Id.Parse(EVSEId.Value) : null,
                            Now
                        ).
                        Select(message => new JObject(
                            new JProperty("id",        message.Id.ToString()),
                            new JProperty("priority",  message.Priority.ToString()),
                            new JProperty("text",      TextOf(message))
                        ))
               );

        #endregion

        #region (private static) TextOf(Message)

        /// <summary>
        /// The one line of a message that goes on the screen, in the language
        /// this station is set to.
        /// </summary>
        /// <remarks>
        /// A message may carry its text several times over, once per language,
        /// which is what the plural in <c>MessageContents</c> is for. Which one
        /// belongs on the screen is not a question about whoever is standing in
        /// front of it - nothing here can know that, and nothing guesses from a
        /// browser's Accept-Language, because the browser is a screen on a wall
        /// and its language is whoever set it up. It is a question about where
        /// the station stands, which is configuration: <c>operator.language</c>.
        ///
        /// The fall-back runs from exact, through the language without its
        /// region, to a text that named no language at all, to whatever came
        /// first. A station told nothing takes the first, which is the order
        /// the sender wrote them in and the closest thing to a stated
        /// preference there is.
        /// </remarks>
        private String TextOf(OCPPv2_1.MessageInfo Message)
        {

            var contents = Message.Messages.ToArray();

            if (contents.Length == 0)
                return "";

            var wanted   = Operator.Language?.ToLowerInvariant();
            var primary  = Operator.PrimaryLanguage;

            if (wanted is not null)
            {

                var exact = contents.FirstOrDefault(content => content.Language.HasValue &&
                                                               String.Equals(content.Language.Value.ToString(), wanted, StringComparison.OrdinalIgnoreCase));

                if (exact is not null)
                    return exact.Content;

                var sameLanguage = contents.FirstOrDefault(content => content.Language.HasValue &&
                                                                      String.Equals(content.Language.Value.ToString().Split('-')[0], primary, StringComparison.OrdinalIgnoreCase));

                if (sameLanguage is not null)
                    return sameLanguage.Content;

            }

            return (contents.FirstOrDefault(content => !content.Language.HasValue) ?? contents[0]).Content;

        }

        #endregion


        #region TryShowMessage(JSON, out Result, out Error)

        /// <summary>
        /// Put a message on this station, as a CSMS would.
        /// </summary>
        /// <remarks>
        /// A real <c>MessageInfo</c> is built and handed to the same method the
        /// incoming OCPP handler calls, so a message put here and one sent by a
        /// CSMS are the same message, kept by the same rules and refused for
        /// the same reasons. A way in, not a second implementation - see
        /// ChargingStation.Reservations.cs, which exists for the same reason.
        /// </remarks>
        public Boolean TryShowMessage(JObject                           JSON,
                                      [NotNullWhen(true)]  out JObject? Result,
                                      [NotNullWhen(false)] out String?  Error)
        {

            Result  = null;
            Error   = null;

            #region The text

            var text = JSON.Value<String>("text")?.Trim();

            if (String.IsNullOrEmpty(text))
            {
                Error = "A display message needs its 'text'.";
                return false;
            }

            if (text.Length > MaxMessageLength)
            {
                Error = $"A display message may be at most {MaxMessageLength} characters long.";
                return false;
            }

            #endregion

            #region Which id

            var id = JSON.Value<String>("id")?.Trim();

            var messageId = String.IsNullOrEmpty(id)
                                ? OCPPv2_1.DisplayMessage_Id.NewRandom
                                : OCPPv2_1.DisplayMessage_Id.TryParse(id);

            if (messageId is null)
            {
                Error = "'id' is a number.";
                return false;
            }

            #endregion

            #region How loudly

            var priorityText = JSON.Value<String>("priority")?.Trim();

            var priority     = String.IsNullOrEmpty(priorityText)
                                   ? OCPPv2_1.MessagePriority.NormalCycle
                                   : OCPPv2_1.MessagePriority.TryParse(priorityText);

            if (priority is null)
            {
                Error = $"'priority' is one of {OCPPv2_1.MessagePriority.AlwaysFront}, {OCPPv2_1.MessagePriority.InFront} or {OCPPv2_1.MessagePriority.NormalCycle}.";
                return false;
            }

            #endregion

            #region When it applies

            OCPPv2_1.MessageState? state = null;

            var stateText = JSON.Value<String>("state")?.Trim();

            if (!String.IsNullOrEmpty(stateText))
            {

                state = OCPPv2_1.MessageState.TryParse(stateText);

                if (state is null)
                {
                    Error = $"'state' is one of {String.Join(", ", OCPPv2_1.CS.AChargingStationNode.SupportedMessageStates)}, or absent for always.";
                    return false;
                }

            }

            #endregion

            #region Where it applies

            OCPPv2_1.Component? display = null;

            if (JSON["evse"] is JToken evseToken && evseToken.Type != JTokenType.Null)
            {

                var value = evseToken.Type == JTokenType.Integer ? evseToken.Value<Int64>() : -1;

                if (value < 1 || !EVSEs.Any(evse => evse.Id == value))
                {
                    Error = "'evse' is the number of an EVSE of this station, or null for the whole station.";
                    return false;
                }

                display = new OCPPv2_1.Component(
                              Name:  "Display",
                              EVSE:  new OCPPv2_1.EVSE(OCPPv2_1.EVSE_Id.Parse((UInt16) value))
                          );

            }

            #endregion

            #region For how long

            var now      = TimeProvider.GetUtcNow();
            var minutes  = JSON.Value<Double?>("minutes");

            if (minutes.HasValue && (minutes.Value <= 0 || minutes.Value > MaxMessageTime.TotalMinutes))
            {
                Error = $"'minutes' must be more than 0 and at most {MaxMessageTime.TotalMinutes}, or absent for no end.";
                return false;
            }

            #endregion

            var message = new OCPPv2_1.MessageInfo(
                              Id:              messageId.Value,
                              Priority:        priority.Value,
                              Messages:        new OCPPv2_1.MessageContents(
                                                   new OCPPv2_1.MessageContent(
                                                       Content:  text,
                                                       Format:   OCPPv2_1.MessageFormat.UTF8
                                                   )
                                               ),
                              State:           state,
                              StartTimestamp:  now,
                              EndTimestamp:    minutes.HasValue ? now + TimeSpan.FromMinutes(minutes.Value) : null,
                              Display:         display
                          );

            var status  = cs02.SetDisplayMessage(message, now);

            if (status != OCPPv2_1.DisplayMessageStatus.Accepted)
            {

                Log.Notice($"A display message was refused: {status}.", "ocpp", "message");

                Error = status switch {
                            OCPPv2_1.DisplayMessageStatus.NotSupportedMessageFormat  => "This station shows text, and nothing else.",
                            OCPPv2_1.DisplayMessageStatus.NotSupportedPriority       => "This station does not know that priority.",
                            OCPPv2_1.DisplayMessageStatus.NotSupportedState          => "This station does not know that state.",
                            _                                                        => "This station will not show that message."
                        };

                return false;

            }

            Log.Notice(
                $"Display message {message.Id} ({priority.Value}" +
                (state is null   ? ""  : $", while {state.Value}") +
                (display is null ? ""  : $", at EVSE {display.EVSE!.Id}") +
                $"): \"{text}\"",
                "ocpp", "message"
            );

            Result = new JObject(
                         new JProperty("status",    status.ToString()),
                         new JProperty("id",        message.Id.ToString()),
                         new JProperty("priority",  priority.Value.ToString()),
                         new JProperty("state",     state?.ToString()),
                         new JProperty("evse",      display?.EVSE?.Id.Value),
                         new JProperty("text",      text),
                         new JProperty("until",     message.EndTimestamp?.ToString("o"))
                     );

            return true;

        }

        #endregion

        #region TryClearMessage(JSON, out Result, out Error)

        /// <summary>
        /// Take a message off this station again.
        /// </summary>
        public Boolean TryClearMessage(JObject                           JSON,
                                       [NotNullWhen(true)]  out JObject? Result,
                                       [NotNullWhen(false)] out String?  Error)
        {

            Result  = null;
            Error   = null;

            var text = JSON.Value<String>("id")?.Trim();

            if (String.IsNullOrEmpty(text) ||
                OCPPv2_1.DisplayMessage_Id.TryParse(text) is not OCPPv2_1.DisplayMessage_Id messageId)
            {
                Error = "An 'id' is needed.";
                return false;
            }

            if (!cs02.ClearDisplayMessage(messageId))
            {
                Error = $"This station is not showing a message with the id '{messageId}'.";
                return false;
            }

            Log.Notice($"Display message {messageId} was taken off.", "ocpp", "message");

            Result = new JObject(
                         new JProperty("status",  "Accepted"),
                         new JProperty("id",      messageId.ToString())
                     );

            return true;

        }

        #endregion

        #region MessagesJSON()

        /// <summary>
        /// Every message this station is holding, and where each of them would
        /// be shown, for the web interface.
        /// </summary>
        public JObject MessagesJSON()
        {

            var now = TimeProvider.GetUtcNow();

            return new JObject(

                       new JProperty("messages",    new JArray(cs02.DisplayMessages.Select(message => new JObject(
                                                        new JProperty("id",        message.Id.ToString()),
                                                        new JProperty("priority",  message.Priority.ToString()),
                                                        new JProperty("state",     message.State?.ToString()),
                                                        new JProperty("evse",      message.Display?.EVSE?.Id.Value),
                                                        new JProperty("text",      TextOf(message)),
                                                        new JProperty("from",      message.StartTimestamp?.ToString("o")),
                                                        new JProperty("until",     message.EndTimestamp?.  ToString("o")),
                                                        // Whether it is on the screen at this moment, which is a
                                                        // different question from whether this station holds it.
                                                        new JProperty("showing",   IsShowing(message, now))
                                                    )))),

                       new JProperty("priorities",  new JArray(
                                                        OCPPv2_1.MessagePriority.AlwaysFront.ToString(),
                                                        OCPPv2_1.MessagePriority.InFront.    ToString(),
                                                        OCPPv2_1.MessagePriority.NormalCycle.ToString()
                                                    )),

                       new JProperty("states",      new JArray(OCPPv2_1.CS.AChargingStationNode.SupportedMessageStates.Select(state => state.ToString()))),

                       new JProperty("evses",       new JArray(EVSEs.Select(evse => new JObject(
                                                        new JProperty("id",     evse.Id),
                                                        new JProperty("label",  evse.PhysicalReference),
                                                        new JProperty("state",  MessageStateOf(evse).ToString())
                                                    )))),

                       new JProperty("stationState",  StationMessageState().ToString()),
                       new JProperty("maxLength",     MaxMessageLength),
                       new JProperty("maxMinutes",    MaxMessageTime.TotalMinutes)

                   );

        }

        #endregion

        #region (private) IsShowing(Message, Now)

        /// <summary>
        /// Whether the given message is on the screen at this moment.
        /// </summary>
        private Boolean IsShowing(OCPPv2_1.MessageInfo  Message,
                                  DateTimeOffset        Now)
        {

            if (Message.Display?.EVSE is not null)
            {

                var evse = EVSEs.FirstOrDefault(candidate => candidate.Id == Message.Display.EVSE.Id.Value);

                return evse is not null &&
                       cs02.DisplayMessagesFor(MessageStateOf(evse), Message.Display.EVSE.Id, Now).
                            Any(shown => shown.Id == Message.Id);

            }

            return cs02.DisplayMessagesFor(StationMessageState(), null, Now).
                        Any(shown => shown.Id == Message.Id);

        }

        #endregion

    }

}
