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

using System.Runtime.CompilerServices;

using Newtonsoft.Json.Linq;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    #region (enum) RegistrationState

    /// <summary>
    /// Where an OCPP node of this station stands with its CSMS.
    /// </summary>
    public enum RegistrationState
    {

        /// <summary>
        /// No BootNotification sent yet: no connection was up.
        /// </summary>
        NotAsked,

        /// <summary>
        /// A BootNotification is on its way.
        /// </summary>
        Asking,

        /// <summary>
        /// The CSMS accepted this station.
        /// </summary>
        Accepted,

        /// <summary>
        /// The CSMS has not decided yet, and may ask this station things meanwhile.
        /// </summary>
        Pending,

        /// <summary>
        /// The CSMS will not have this station, for now.
        /// </summary>
        Rejected,

        /// <summary>
        /// The CSMS did not answer the BootNotification.
        /// </summary>
        Unanswered

    }

    #endregion

    /// <summary>
    /// Registering with the CSMS: the BootNotification each OCPP node sends
    /// once a connection is up, and what this station may say before and after.
    /// </summary>
    /// <remarks>
    /// As OCPP has it. Until a CSMS accepts the BootNotification, a station
    /// tells it nothing of its own: what it has to say waits in the queue,
    /// and it sends no heartbeats. A CSMS that answers Pending may still ask
    /// this station things, and is answered. Pending and Rejected come with
    /// how long to wait before asking again; where that is 0, and where the
    /// CSMS did not answer at all, it is a minute. Accepted comes with how
    /// often to send a heartbeat, which the node then does by itself.
    ///
    /// A node registers once: after the start, when its first connection is
    /// up, and again when a connection comes back only where it was not
    /// accepted yet - one that went unanswered asks at once, one told to wait
    /// waits. A node built anew because the EVSEs or what this station says it
    /// is changed is a new start: it hangs up, dials again and registers
    /// again, with the BootReason LocalReset in OCPP 2.1 rather than PowerUp.
    /// </remarks>
    public partial class ChargingStation
    {

        #region (class) Registration

        /// <summary>
        /// Where one OCPP node stands with its CSMS, and the next time it asks.
        /// </summary>
        private sealed class Registration
        {

            public Registration(String               Version,
                                OCPPv2_1.BootReason  Reason)
            {
                this.Version  = Version;
                this.Reason   = Reason;
            }

            public String                   Version    { get; }
            public OCPPv2_1.BootReason      Reason     { get; }

            public RegistrationState        State      { get; set; } = RegistrationState.NotAsked;
            public DateTimeOffset?          Since      { get; set; }

            /// <summary>
            /// Accepted, how often a heartbeat goes; otherwise how long it waits before asking again.
            /// </summary>
            public TimeSpan?                Interval   { get; set; }
            public DateTimeOffset?          NextTry    { get; set; }
            public String?                  Problem    { get; set; }

            /// <summary>
            /// The connection that was up last, whose line on the connections page says how it stands.
            /// </summary>
            public ConnectionEntry?         Over       { get; set; }

            /// <summary>
            /// Cancelled once the node is let go of: nothing is asked for it any more.
            /// </summary>
            public CancellationTokenSource  Ended      { get; } = new();

            /// <summary>
            /// The BootNotification on its way, if one is: cancelled when another
            /// one takes its place.
            /// </summary>
            public CancellationTokenSource? Asking     { get; set; }

            /// <summary>
            /// How many were asked: only the answer to the last one counts.
            /// </summary>
            public Int32                    Asks       { get; set; }

        }

        #endregion

        #region Data

        /// <summary>
        /// How long to wait before asking again where the CSMS gave no time -
        /// a Pending or a Rejected with an interval of 0 - or did not answer.
        /// </summary>
        public static readonly TimeSpan  DefaultRegistrationRetry   = TimeSpan.FromMinutes(1);

        private readonly ConditionalWeakTable<AWWCPNetworkingNode, Registration>  registrations  = new();

        private Boolean nodesBuiltBefore;

        #endregion


        #region RegistrationJSON()

        /// <summary>
        /// Where each OCPP node stands with its CSMS.
        /// </summary>
        public JObject RegistrationJSON()

            => new (
                   new JProperty("1.6", RegistrationJSON(RegistrationOf(cs01))),
                   new JProperty("2.1", RegistrationJSON(RegistrationOf(cs02)))
               );

        private static JObject RegistrationJSON(Registration Registration)

            => new (
                   new JProperty("state",     Registration.State.ToString()),
                   new JProperty("since",     Registration.Since?.  ToString("o")),
                   new JProperty("interval",  Registration.Interval?.TotalSeconds),
                   new JProperty("nextTry",   Registration.NextTry?.ToString("o")),
                   new JProperty("problem",   Registration.Problem)
               );

        #endregion

        #region (private) NewRegistrations(ChargePoint, ChargingStation)

        /// <summary>
        /// Two nodes just built, neither of them registered: PowerUp for the
        /// first two this station builds, LocalReset for every two after.
        /// </summary>
        private void NewRegistrations(OCPPv1_6.TestChargePointNode         ChargePoint,
                                      OCPPv2_1.CS.TestChargingStationNode  ChargingStation)
        {

            var reason = nodesBuiltBefore
                             ? OCPPv2_1.BootReason.LocalReset
                             : OCPPv2_1.BootReason.PowerUp;

            nodesBuiltBefore = true;

            registrations.AddOrUpdate(ChargePoint,     new Registration("1.6", reason));
            registrations.AddOrUpdate(ChargingStation, new Registration("2.1", reason));

            // Until the CSMS accepts the BootNotification, what is queued waits.
            ChargePoint.    HoldOfflineQueue = true;
            ChargingStation.HoldOfflineQueue = true;

        }

        #endregion

        #region (private) RegistrationOf(Node)

        private Registration RegistrationOf(AWWCPNetworkingNode Node)

            => registrations.TryGetValue(Node, out var registration)
                   ? registration
                   : throw new InvalidOperationException($"The OCPP node '{Node.Id}' was built without a registration.");

        #endregion

        #region (private) EndRegistrations(Nodes)

        /// <summary>
        /// Nothing more is asked for these nodes: they were let go of, or this
        /// station is stopping.
        /// </summary>
        private void EndRegistrations(params AWWCPNetworkingNode[] Nodes)
        {
            foreach (var node in Nodes)
                if (registrations.TryGetValue(node, out var registration))
                    registration.Ended.Cancel();
        }

        #endregion


        #region (private) ConnectionIsUp(Connection)

        /// <summary>
        /// A connection is up: send what waits where the CSMS accepted this
        /// station's node, and register the node where it did not yet.
        /// </summary>
        private void ConnectionIsUp(ConnectionEntry Connection)
        {

            AWWCPNetworkingNode node = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6 ? cs01 : cs02;

            var registration = RegistrationOf(node);

            registration.Over = Connection;

            switch (registration.State)
            {

                case RegistrationState.Accepted:
                    SendWhatWaits(Connection);
                    return;

                // Told to wait: Rejected may not ask before its time is up,
                // and Pending is asked again when it is.
                case RegistrationState.Pending or RegistrationState.Rejected
                    when registration.NextTry > TimeProvider.GetUtcNow():
                    return;

                // Not asked yet, not answered - or asked over a connection that
                // was lost before the answer came, which is not waited for.
                default:
                    _ = RegisterAsync(node, registration, ByTheClock: false);
                    return;

            }

        }

        #endregion

        #region (private) RegisterAsync(Node, Registration, ByTheClock)

        /// <summary>
        /// Send the node's BootNotification, and do what the answer says.
        /// </summary>
        /// <remarks>
        /// Never for a node let go of or one already accepted. A connection
        /// that comes up asks anew, and the answer to what was asked before -
        /// over a connection that is gone - no longer counts; the clock asks
        /// only where nothing is on its way. Nothing here throws: a CSMS that
        /// cannot be asked is asked again later.
        /// </remarks>
        /// <param name="Node">The node to register.</param>
        /// <param name="Registration">Where it stands.</param>
        /// <param name="ByTheClock">Whether the time to ask again is up, rather than a connection having come up.</param>
        private async Task RegisterAsync(AWWCPNetworkingNode  Node,
                                         Registration         Registration,
                                         Boolean              ByTheClock)
        {

            CancellationTokenSource asking;
            Int32                   ask;

            lock (Registration)
            {

                if (Registration.Ended.IsCancellationRequested ||
                    Registration.State == RegistrationState.Accepted ||
                    (ByTheClock && Registration.State == RegistrationState.Asking))
                    return;

                Registration.Asking?.Cancel();

                asking                = CancellationTokenSource.CreateLinkedTokenSource(Registration.Ended.Token);
                ask                   = ++Registration.Asks;

                Registration.Asking   = asking;
                Registration.State    = RegistrationState.Asking;
                Registration.NextTry  = null;

            }

            Boolean StillCounts()
            {
                lock (Registration)
                    return ask == Registration.Asks && !Registration.Ended.IsCancellationRequested;
            }

            try
            {

                var (answered, status, interval, problem) = Node switch {

                    // Built here rather than with the node's own SendBootNotification,
                    // which says a serial number the node made up where this station
                    // has none.
                    OCPPv1_6.TestChargePointNode chargePoint
                        => Of(await chargePoint.OCPP.OUT.BootNotification(
                                        new OCPPv1_6.CP.BootNotificationRequest(
                                            Destination:              SourceRouting.To(NetworkingNode_Id.CentralSystem),
                                            ChargePointVendor:        chargePoint.ChargePointVendor,
                                            ChargePointModel:         chargePoint.ChargePointModel,
                                            ChargePointSerialNumber:  Nameplate.SerialNumber,
                                            FirmwareVersion:          chargePoint.FirmwareVersion,
                                            RequestId:                chargePoint.NextRequestId,
                                            RequestTimestamp:         chargePoint.Now,
                                            RequestTimeout:           chargePoint.OCPP.DefaultRequestTimeout,
                                            NetworkPath:              NetworkPath.From(chargePoint.Id),
                                            CancellationToken:        asking.Token
                                        )
                                    )),

                    OCPPv2_1.CS.TestChargingStationNode chargingStation
                        => Of(await OCPPv2_1.CS.IChargingStationNodeExtensions.SendBootNotification(
                                        chargingStation,
                                        Registration.Reason,
                                        CancellationToken: asking.Token
                                    )),

                    _   => (false, "", TimeSpan.Zero, $"'{Node.Id}' is no OCPP node this station knows how to register.")

                };

                if (!StillCounts())
                    return;

                if (!answered)
                {
                    Unanswered(Node, Registration, problem ?? "no answer");
                    return;
                }

                Registration.Since    = TimeProvider.GetUtcNow();
                Registration.Problem  = null;

                if (status == "Accepted")
                {
                    Accepted(Node, Registration, interval);
                    return;
                }

                var wait = interval > TimeSpan.Zero ? interval : DefaultRegistrationRetry;

                Registration.State     = status == "Pending" ? RegistrationState.Pending : RegistrationState.Rejected;
                Registration.Interval  = wait;

                var what = $"the CSMS has not accepted this station's OCPP {Registration.Version} node yet ({status}); " +
                           $"it asks again in {wait.TotalSeconds:F0} s, and tells the CSMS nothing else until then.";

                if (Registration.Over is ConnectionEntry over)
                    Note(over, $"Connected, but {what}");

                if (Registration.State == RegistrationState.Rejected)
                    Log.Warning($"Registering: {what}", "ocpp", "connections");
                else
                    Log.Info   ($"Registering: {what}", "ocpp", "connections");

                AskAgain(Node, Registration, wait);

            }
            catch (Exception e)
            {
                if (StillCounts())
                    Unanswered(Node, Registration, e.Message);
            }
            finally
            {

                lock (Registration)
                    if (Registration.Asking == asking)
                        Registration.Asking = null;

                asking.Dispose();

            }

        }

        #endregion

        #region (private static) Of(Response)

        // What both versions' answers have to say, the same way.
        private static (Boolean Answered, String Status, TimeSpan Interval, String? Problem) Of(OCPPv1_6.CS.BootNotificationResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.Status.ToString(), Response.HeartbeatInterval, null)
                   : (false, "", TimeSpan.Zero, Response.Result.Description ?? Response.Result.ResultCode.ToString());

        private static (Boolean Answered, String Status, TimeSpan Interval, String? Problem) Of(OCPPv2_1.CSMS.BootNotificationResponse Response)

            => Response.Result.ResultCode == cloud.charging.open.protocols.OCPP.ResultCode.OK
                   ? (true,  Response.Status.ToString(), Response.Interval, null)
                   : (false, "", TimeSpan.Zero, Response.Result.Description ?? Response.Result.ResultCode.ToString());

        #endregion

        #region (private) Accepted(Node, Registration, Interval)

        /// <summary>
        /// The CSMS accepted the node: what waited is let go and sent, and the
        /// node sends its heartbeats by itself from now on.
        /// </summary>
        private void Accepted(AWWCPNetworkingNode  Node,
                              Registration         Registration,
                              TimeSpan             Interval)
        {

            Registration.State     = RegistrationState.Accepted;
            Registration.Interval  = Interval;

            switch (Node)
            {
                case OCPPv1_6.TestChargePointNode         chargePoint:      chargePoint.    HoldOfflineQueue = false; break;
                case OCPPv2_1.CS.TestChargingStationNode  chargingStation:  chargingStation.HoldOfflineQueue = false; break;
            }

            var what = $"the CSMS accepted this station's OCPP {Registration.Version} node, " +
                       $"which sends a heartbeat every {Interval.TotalSeconds:F0} s.";

            Log.Notice($"Registering: {what}", "ocpp", "connections");

            if (Registration.Over is ConnectionEntry over)
            {
                Note(over, $"Connected; {what}");
                SendWhatWaits(over);
            }

        }

        #endregion

        #region (private) Unanswered(Node, Registration, Problem)

        /// <summary>
        /// The BootNotification got no answer: asked again in a minute, or as
        /// soon as a connection comes back.
        /// </summary>
        private void Unanswered(AWWCPNetworkingNode  Node,
                                Registration         Registration,
                                String               Problem)
        {

            Registration.State     = RegistrationState.Unanswered;
            Registration.Interval  = DefaultRegistrationRetry;
            Registration.Problem   = Problem;

            Log.Warning($"Registering: the CSMS did not answer the BootNotification of this station's OCPP {Registration.Version} node " +
                        $"({Problem}); it asks again in {DefaultRegistrationRetry.TotalSeconds:F0} s.",
                        "ocpp", "connections");

            AskAgain(Node, Registration, DefaultRegistrationRetry);

        }

        #endregion

        #region (private) AskAgain(Node, Registration, After)

        private void AskAgain(AWWCPNetworkingNode  Node,
                              Registration         Registration,
                              TimeSpan             After)
        {

            Registration.NextTry = TimeProvider.GetUtcNow() + After;

            _ = Task.Run(async () => {

                    try
                    {
                        await Task.Delay(After, TimeProvider, Registration.Ended.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }

                    await RegisterAsync(Node, Registration, ByTheClock: true);

                });

        }

        #endregion

    }

}
