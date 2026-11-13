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

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Web;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.Kiosk;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// What this station tells its back ends of itself without being asked,
    /// and again when it is: how each EVSE stands - a StatusNotification - and
    /// that it is there - a BootNotification, a Heartbeat.
    /// </summary>
    /// <remarks>
    /// <para>
    /// How an EVSE stands is told to every back end that accepted this
    /// station: all of them when it is accepted and when its connection comes
    /// back, and each one that changed afterwards, within a second. It is what
    /// the EVSEs page shows, in the words of OCPP: free is "Available", held
    /// "Reserved", out of service "Unavailable", and one somebody charges at
    /// "Occupied" - "Charging" over OCPP 1.6, which tells the steps of a
    /// session apart and of which this station knows that one.
    /// </para>
    /// <para>
    /// Told again when asked: by a TriggerMessage of either version, on the
    /// web page and at the command line - to every back end this station is
    /// connected to, whether it accepted the station already or not yet, as
    /// OCPP has it for a TriggerMessage. Having the state reported again is
    /// "state:run", the right of the operator's telephone support; a
    /// BootNotification and a Heartbeat are "node:run", the operator's (white
    /// paper "Signed OCPP Messages and Signer Roles", section 5.3).
    /// </para>
    /// <para>
    /// A BootNotification asked for is this station registering again, with
    /// the reason "Triggered": until the back end accepted it once more, it is
    /// told nothing else - as after every BootNotification.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What is typed and sent to have a BootNotification sent again.
        /// </summary>
        public const String  SendsBootNotification    = "BootNotification";

        /// <summary>
        /// What is typed and sent to have a Heartbeat sent.
        /// </summary>
        public const String  SendsHeartbeat           = "Heartbeat";

        /// <summary>
        /// What is typed and sent to have the state of the EVSEs reported again.
        /// </summary>
        public const String  SendsStatusNotification  = "StatusNotification";

        /// <summary>
        /// All three.
        /// </summary>
        public static readonly IReadOnlyList<String>  SentAgain = [ SendsBootNotification, SendsHeartbeat, SendsStatusNotification ];

        /// <summary>
        /// How each EVSE stood when this station last looked.
        /// </summary>
        private readonly Dictionary<Byte, EVSEStatus>  statusTold = [];
        private readonly Lock                          statusLock = new();

        #endregion


        #region (static) PermissionToSend(Message)

        /// <summary>
        /// What somebody needs to have the given message sent again, or null
        /// where it is none of the three.
        /// </summary>
        public static Permission? PermissionToSend(String Message)

            => NameOfSent(Message) switch {
                   SendsStatusNotification                  => Permission.Run(NodeResources.State),
                   SendsBootNotification or SendsHeartbeat  => Permission.Run(NodeResources.Node),
                   _                                        => null
               };

        /// <summary>
        /// The message as it is written, however it was typed, or null.
        /// </summary>
        public static String? NameOfSent(String? Message)

            => SentAgain.FirstOrDefault(sent => sent.Equals(Message?.Trim(), StringComparison.OrdinalIgnoreCase));

        #endregion

        #region (private) AttachReports(ChargePoint, ChargingStation)

        /// <summary>
        /// Answer the TriggerMessages both new OCPP nodes are sent that are
        /// this station's to answer, and send what was asked for once the
        /// answer is on its way.
        /// </summary>
        private void AttachReports(OCPPv1_6.TestChargePointNode         ChargePoint,
                                   OCPPv2_1.CS.TestChargingStationNode  ChargingStation)
        {

            // The node of OCPP 1.6 answers no TriggerMessage by itself.
            ChargePoint.OCPP.IN.OnTriggerMessage += (timestamp, sender, connection, request, cancellationToken) => {

                var message  = NameOfSent(request.RequestedMessage.ToString());

                var status   = message is null
                                   ? OCPPv1_6.TriggerMessageStatus.NotImplemented
                                   : message == SendsStatusNotification &&
                                     request.ConnectorId is OCPPv1_6.Connector_Id connector &&
                                     connector.Value != 0 &&
                                     !EVSEs.Any(evse => evse.Id == connector.Value)
                                         ? OCPPv1_6.TriggerMessageStatus.Rejected
                                         : OCPPv1_6.TriggerMessageStatus.Accepted;

                return Task.FromResult(new OCPPv1_6.CP.TriggerMessageResponse(request, status));

            };

            ChargingStation.OnTriggerMessageByOwner = (request, cancellationToken) => {

                var message = NameOfSent(request.RequestedMessage.ToString());

                // A certificate asked for, a CustomTrigger and everything
                // else: the node's, and the station's other answers.
                if (message is null)
                    return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(null);

                return Task.FromResult<OCPPv2_1.CS.TriggerMessageResponse?>(
                           new OCPPv2_1.CS.TriggerMessageResponse(
                               request,
                               message == SendsStatusNotification &&
                               request.EVSE is OCPPv2_1.EVSE evse &&
                               !EVSEs.Any(known => known.Id == evse.Id.Value)
                                   ? OCPPv2_1.TriggerMessageStatus.Rejected
                                   : OCPPv2_1.TriggerMessageStatus.Accepted
                           )
                       );

            };

            // After the answer, as OCPP has it: the answer first, then what it
            // was asked for.
            ChargePoint.    OCPP.OUT.OnTriggerMessageResponseSent += (timestamp, sender, connection, request, response, runtime, result, cancellationToken) => {

                if (response.Status == OCPPv1_6.TriggerMessageStatus.Accepted &&
                    NameOfSent(request.RequestedMessage.ToString()) is String message)
                {
                    SendAgain(message,
                              request.ConnectorId is OCPPv1_6.Connector_Id connector && connector.Value is > 0 and <= Byte.MaxValue
                                  ? (Byte) connector.Value
                                  : null,
                              "a TriggerMessage over OCPP 1.6");
                }

                return Task.CompletedTask;

            };

            ChargingStation.OCPP.OUT.OnTriggerMessageResponseSent += (timestamp, sender, connection, request, response, runtime, result, cancellationToken) => {

                if (response.Status == OCPPv2_1.TriggerMessageStatus.Accepted &&
                    NameOfSent(request.RequestedMessage.ToString()) is String message)
                {
                    SendAgain(message,
                              request.EVSE is OCPPv2_1.EVSE evse && evse.Id.Value is > 0 and <= Byte.MaxValue
                                  ? (Byte) evse.Id.Value
                                  : null,
                              "a TriggerMessage over OCPP 2.1");
                }

                return Task.CompletedTask;

            };

        }

        #endregion


        #region TrySendAgain(Message, By, out Result, out Error)

        /// <summary>
        /// Have this station send a message again that it sends by itself, as
        /// its web page and its command line ask for it.
        /// </summary>
        /// <param name="Message">"BootNotification", "Heartbeat" or "StatusNotification", however typed.</param>
        /// <param name="By">Who asked for it, for the log: "'alice' on the web page".</param>
        /// <param name="Result">Which message goes to how many back ends.</param>
        /// <param name="Error">Why none is sent.</param>
        public Boolean TrySendAgain(String                             Message,
                                    String                             By,
                                    [NotNullWhen(true)]  out JObject?  Result,
                                    [NotNullWhen(false)] out String?   Error)
        {

            Result = null;

            if (NameOfSent(Message) is not String message)
            {
                Error = $"'{Message}' is none of the messages this station sends again: {String.Join(", ", SentAgain)}.";
                return false;
            }

            var backEnds = SendAgain(message, null, By);

            if (backEnds == 0)
            {
                Error = "No back end is connected to this station, so nobody is told.";
                return false;
            }

            Result = new JObject(
                         new JProperty("message",   message),
                         new JProperty("backEnds",  backEnds)
                     );

            Error = null;
            return true;

        }

        #endregion

        #region (private) SendAgain(Message, EVSEId, Why)

        /// <summary>
        /// Send one of the three again, and say to how many back ends it goes.
        /// </summary>
        /// <param name="Message">One of <see cref="SentAgain"/>.</param>
        /// <param name="EVSEId">The one EVSE a StatusNotification is asked for, or null for all.</param>
        /// <param name="Why">What asked for it, for the log.</param>
        private Int32 SendAgain(String  Message,
                                Byte?   EVSEId,
                                String  Why)
        {

            var backEnds = 0;

            foreach (var node in new AWWCPNetworkingNode[] { cs01, cs02 })
            {

                if (!registrations.TryGetValue(node, out var ofNode))
                    continue;

                foreach (var registration in ofNode.ByConnection.Values)
                {

                    if (registration.Ended.IsCancellationRequested)
                        continue;

                    switch (Message)
                    {

                        // Asked again whatever the back end said before: its
                        // answer is where this station stands with it.
                        case SendsBootNotification:
                            _ = RegisterAsync(node, registration, ByTheClock: false, Triggered: true);
                            backEnds++;
                            break;

                        // Whether the back end accepted this station or not
                        // yet: what is asked for is sent, as OCPP has it for
                        // a TriggerMessage while a registration is pending.
                        case SendsHeartbeat:
                            Tell(registration, () => Heartbeat(node, registration));
                            backEnds++;
                            break;

                        case SendsStatusNotification:
                            TellHowTheEVSEsStand(node, registration, EVSEId);
                            backEnds++;
                            break;

                    }

                }

            }

            Log.Notice(
                backEnds == 0
                    ? $"{Capitalized(Why)} asked for a {Message}, and there is no back end to send it to."
                    : $"{Capitalized(Why)} asked for a {Message}: it goes to {backEnds} back end(s).",
                "ocpp", "reports"
            );

            return backEnds;

        }

        #endregion


        #region (private) RememberHowTheEVSEsStand()

        /// <summary>
        /// Take how the EVSEs stand now as told: this station starts, and
        /// tells each back end all of it once that one accepts it.
        /// </summary>
        private void RememberHowTheEVSEsStand()
        {

            var now = EVSEsNow();

            lock (statusLock)
            {

                statusTold.Clear();

                foreach (var evse in now)
                    statusTold[evse.EVSE.Id] = evse.Status;

            }

        }

        #endregion

        #region ReportStatusChanges()

        /// <summary>
        /// Look at how the EVSEs stand, and tell every back end that accepted
        /// this station of each one that is not as it was the last time - what
        /// the station does once a second while it runs.
        /// </summary>
        /// <returns>How many EVSEs changed.</returns>
        public Int32 ReportStatusChanges()
        {

            var changed = new List<EVSENow>();

            try
            {

                var now = EVSEsNow();

                lock (statusLock)
                {

                    foreach (var evse in now)
                    {
                        if (!statusTold.TryGetValue(evse.EVSE.Id, out var told) || told != evse.Status)
                        {
                            statusTold[evse.EVSE.Id] = evse.Status;
                            changed.Add(evse);
                        }
                    }

                    foreach (var gone in statusTold.Keys.Where(id => !now.Any(evse => evse.EVSE.Id == id)).ToArray())
                        statusTold.Remove(gone);

                }

                if (changed.Count == 0)
                    return 0;

                var at = TimeProvider.GetUtcNow();

                foreach (var node in new AWWCPNetworkingNode[] { cs01, cs02 })
                {

                    if (!registrations.TryGetValue(node, out var ofNode))
                        continue;

                    foreach (var registration in ofNode.ByConnection.Values)
                        if (registration.State == RegistrationState.Accepted && !registration.Ended.IsCancellationRequested)
                            foreach (var evse in changed)
                                TellStatus(node, registration, evse.EVSE, evse.Status, at);

                }

            }
            catch (Exception e)
            {
                Log.Warning($"Telling the back ends how the EVSEs stand failed: {e.Message}", "ocpp", "reports");
            }

            return changed.Count;

        }

        #endregion

        #region (private) TellHowTheEVSEsStand(Node, Registration, EVSEId = null)

        /// <summary>
        /// Tell one back end how every EVSE stands, or the one asked for: it
        /// accepted this station, its connection came back, or it asked.
        /// </summary>
        private void TellHowTheEVSEsStand(AWWCPNetworkingNode  Node,
                                          Registration         Registration,
                                          Byte?                EVSEId = null)
        {

            try
            {

                var at = TimeProvider.GetUtcNow();

                foreach (var evse in EVSEsNow())
                    if (EVSEId is null || EVSEId == evse.EVSE.Id)
                        TellStatus(Node, Registration, evse.EVSE, evse.Status, at);

            }
            catch (Exception e)
            {
                Log.Warning($"Telling {BackEndOf(Registration)} how the EVSEs stand failed: {e.Message}", "ocpp", "reports");
            }

        }

        #endregion

        #region (private) TellStatus(Node, Registration, EVSE, Status, At)

        /// <summary>
        /// Tell one back end how one EVSE stands: a StatusNotification for the
        /// connector of OCPP 1.6 the EVSE is, and one for each connector of
        /// the EVSE over OCPP 2.1.
        /// </summary>
        private void TellStatus(AWWCPNetworkingNode  Node,
                                Registration         Registration,
                                EVSEConfig           EVSE,
                                EVSEStatus           Status,
                                DateTimeOffset       At)

            => Tell(Registration, async () => {

                   var destination = SourceRouting.To(Registration.Destination);

                   switch (Node)
                   {

                       case OCPPv1_6.TestChargePointNode chargePoint:
                           await OCPPv1_6.CP.IChargePointNodeExtensions.SendStatusNotification(
                                     chargePoint,
                                     OCPPv1_6.Connector_Id.Parse(EVSE.Id),
                                     Status switch {
                                         EVSEStatus.Available  => OCPPv1_6.ChargePointStatus.Available,
                                         EVSEStatus.Reserved   => OCPPv1_6.ChargePointStatus.Reserved,
                                         EVSEStatus.Occupied   => OCPPv1_6.ChargePointStatus.Charging,
                                         _                     => OCPPv1_6.ChargePointStatus.Unavailable
                                     },
                                     OCPPv1_6.ChargePointErrorCodes.NoError,
                                     StatusTimestamp:    At,
                                     Destination:        destination,
                                     CancellationToken:  Registration.Ended.Token
                                 );
                           break;

                       case OCPPv2_1.CS.TestChargingStationNode chargingStation:
                           for (var connector = 1; connector <= Math.Max(1, EVSE.Connectors.Count); connector++)
                               await OCPPv2_1.CS.IChargingStationNodeExtensions.SendStatusNotification(
                                         chargingStation,
                                         OCPPv2_1.EVSE_Id.     Parse(EVSE.Id),
                                         OCPPv2_1.Connector_Id.Parse((Byte) connector),
                                         At,
                                         Status switch {
                                             EVSEStatus.Available  => OCPPv2_1.ConnectorStatus.Available,
                                             EVSEStatus.Reserved   => OCPPv2_1.ConnectorStatus.Reserved,
                                             EVSEStatus.Occupied   => OCPPv2_1.ConnectorStatus.Occupied,
                                             _                     => OCPPv2_1.ConnectorStatus.Unavailable
                                         },
                                         Destination:        destination,
                                         CancellationToken:  Registration.Ended.Token
                                     );
                           break;

                   }

               });

        #endregion

        #region (private) Heartbeat(Node, Registration)

        private static Task Heartbeat(AWWCPNetworkingNode  Node,
                                      Registration         Registration)

            => Node switch {

                   OCPPv1_6.TestChargePointNode chargePoint
                       => OCPPv1_6.CP.IChargePointNodeExtensions.SendHeartbeat(
                              chargePoint,
                              Destination:        SourceRouting.To(Registration.Destination),
                              CancellationToken:  Registration.Ended.Token
                          ),

                   OCPPv2_1.CS.TestChargingStationNode chargingStation
                       => OCPPv2_1.CS.IChargingStationNodeExtensions.SendHeartbeat(
                              chargingStation,
                              Destination:        SourceRouting.To(Registration.Destination),
                              CancellationToken:  Registration.Ended.Token
                          ),

                   _   => Task.CompletedTask

               };

        #endregion

        #region (private) Tell(Registration, Send)

        /// <summary>
        /// Send something to one back end after what was to be sent to it
        /// before: an EVSE that became free and occupied again is told in that
        /// order. Nothing here throws - what cannot be sent is not, and how the
        /// EVSEs stand is told again when the connection is back.
        /// </summary>
        private void Tell(Registration  Registration,
                          Func<Task>    Send)
        {

            lock (Registration)
            {

                Registration.Telling = Registration.Telling.ContinueWith(
                                           async _ => {

                                               if (Registration.Ended.IsCancellationRequested)
                                                   return;

                                               try
                                               {
                                                   await Send();
                                               }
                                               catch (Exception e) when (e is not OperationCanceledException)
                                               {
                                                   Log.Debug($"{BackEndOf(Registration)} could not be told: {e.Message}", "ocpp", "reports");
                                               }
                                               catch (OperationCanceledException)
                                               { }

                                           },
                                           CancellationToken.None,
                                           TaskContinuationOptions.None,
                                           TaskScheduler.Default
                                       ).Unwrap();

            }

        }

        #endregion

    }

}
