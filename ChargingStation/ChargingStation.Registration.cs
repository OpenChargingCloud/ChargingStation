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
using System.Runtime.CompilerServices;

using Newtonsoft.Json.Linq;

using OCPPv1_6 = cloud.charging.open.protocols.OCPPv1_6;
using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.WWCP.NetworkingNode;

using cloud.charging.open.protocols.OCPP.Node.Connections;

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
    /// Each connection registers on its own: after the start, when it is first
    /// up, and again when it comes back only where it was not accepted yet -
    /// one that went unanswered asks at once, one told to wait waits. A node
    /// with two connections of its version - a CSMS and its successor, during a
    /// migration - is registered with both, and each back end that accepted it
    /// gets its heartbeat at the interval it gave. What waits in the queue
    /// waits for the primary, the back end in charge, alone (see IsPrimary).
    ///
    /// A node built anew because the EVSEs or what this station says it is
    /// changed is a new start: it hangs up, dials again and registers again,
    /// with the BootReason LocalReset in OCPP 2.1 rather than PowerUp.
    /// </remarks>
    public partial class ChargingStation
    {

        #region (class) Registrations

        /// <summary>
        /// The registrations of one OCPP node: one per connection.
        /// </summary>
        private sealed class Registrations(String               Version,
                                           OCPPv2_1.BootReason  Reason)
        {

            public String                                            Version         { get; } = Version;
            public OCPPv2_1.BootReason                               Reason          { get; } = Reason;

            /// <summary>
            /// Cancelled once the node is let go of: nothing is asked for it any more.
            /// </summary>
            public CancellationTokenSource                           Ended           { get; } = new();

            /// <summary>
            /// By the identification of the connection.
            /// </summary>
            public ConcurrentDictionary<String, Registration>        ByConnection    { get; } = new();

        }

        #endregion

        #region (class) Registration

        /// <summary>
        /// Where one connection of an OCPP node stands with its back end, and the
        /// next time it asks.
        /// </summary>
        private sealed class Registration
        {

            public Registration(Registrations    Node,
                                ConnectionEntry  Connection,
                                Boolean          Primary)
            {
                this.Version     = Node.Version;
                this.Reason      = Node.Reason;
                this.Ended       = Node.Ended;
                this.Connection  = Connection;
                this.Primary     = Primary;
            }

            public String                   Version      { get; }
            public OCPPv2_1.BootReason      Reason       { get; }

            /// <summary>
            /// The connection, whose line on the connections page says how it stands.
            /// </summary>
            public ConnectionEntry          Connection   { get; }

            /// <summary>
            /// Whether its back end is the one in charge, whose acceptance lets go of what waits in the queue.
            /// </summary>
            public Boolean                  Primary      { get; }

            /// <summary>
            /// Where its BootNotification and its heartbeats go: the connection's own name.
            /// </summary>
            public NetworkingNode_Id        Destination
                => NextHopOf(Connection);

            public RegistrationState        State        { get; set; } = RegistrationState.NotAsked;
            public DateTimeOffset?          Since        { get; set; }

            /// <summary>
            /// Accepted, how often a heartbeat goes; otherwise how long it waits before asking again.
            /// </summary>
            public TimeSpan?                Interval     { get; set; }
            public DateTimeOffset?          NextTry      { get; set; }
            public String?                  Problem      { get; set; }

            /// <summary>
            /// The node's: cancelled once it is let go of.
            /// </summary>
            public CancellationTokenSource  Ended        { get; }

            /// <summary>
            /// The BootNotification on its way, if one is: cancelled when another
            /// one takes its place.
            /// </summary>
            public CancellationTokenSource? Asking       { get; set; }

            /// <summary>
            /// How many were asked: only the answer to the last one counts.
            /// </summary>
            public Int32                    Asks         { get; set; }

            /// <summary>
            /// The heartbeat this station sends a back end other than the
            /// primary, which the node does not: cancelled when it changes.
            /// </summary>
            public CancellationTokenSource? Beating      { get; set; }

        }

        #endregion

        #region Data

        /// <summary>
        /// How long to wait before asking again where the CSMS gave no time -
        /// a Pending or a Rejected with an interval of 0 - or did not answer.
        /// </summary>
        public static readonly TimeSpan  DefaultRegistrationRetry   = TimeSpan.FromMinutes(1);

        /// <summary>
        /// The shortest heartbeat interval this station keeps to, whatever a
        /// back end asks for: the same as its OCPP nodes'.
        /// </summary>
        public static readonly TimeSpan  ShortestHeartbeatInterval  = TimeSpan.FromSeconds(5);

        private readonly ConditionalWeakTable<AWWCPNetworkingNode, Registrations>  registrations  = new();

        private Boolean nodesBuiltBefore;

        #endregion


        #region RegistrationJSON()

        /// <summary>
        /// Where each OCPP node stands with its CSMS: the primary's registration.
        /// </summary>
        public JObject RegistrationJSON()

            => new (
                   new JProperty("1.6", RegistrationJSON(PrimaryRegistrationOf(cs01))),
                   new JProperty("2.1", RegistrationJSON(PrimaryRegistrationOf(cs02)))
               );

        private static JObject RegistrationJSON(Registration? Registration)

            => new (
                   new JProperty("state",     (Registration?.State ?? RegistrationState.NotAsked).ToString()),
                   new JProperty("since",     Registration?.Since?.  ToString("o")),
                   new JProperty("interval",  Registration?.Interval?.TotalSeconds),
                   new JProperty("nextTry",   Registration?.NextTry?.ToString("o")),
                   new JProperty("problem",   Registration?.Problem)
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

            registrations.AddOrUpdate(ChargePoint,     new Registrations("1.6", reason));
            registrations.AddOrUpdate(ChargingStation, new Registrations("2.1", reason));

            // Until the CSMS accepts the BootNotification, what is queued waits.
            ChargePoint.    HoldOfflineQueue = true;
            ChargingStation.HoldOfflineQueue = true;

        }

        #endregion

        #region (private) RegistrationsOf(Node) / RegistrationOf(Node, Connection) / PrimaryRegistrationOf(Node)

        private Registrations RegistrationsOf(AWWCPNetworkingNode Node)

            => registrations.TryGetValue(Node, out var nodeRegistrations)
                   ? nodeRegistrations
                   : throw new InvalidOperationException($"The OCPP node '{Node.Id}' was built without a registration.");

        /// <summary>
        /// The registration of the given connection with the given node: made
        /// when the connection is first up.
        /// </summary>
        private Registration RegistrationOf(AWWCPNetworkingNode  Node,
                                            ConnectionEntry      Connection)
        {

            var nodeRegistrations = RegistrationsOf(Node);

            return nodeRegistrations.ByConnection.GetOrAdd(
                       Connection.Id,
                       _ => new Registration(nodeRegistrations, Connection, IsPrimary(Connection))
                   );

        }

        private Registration? PrimaryRegistrationOf(AWWCPNetworkingNode Node)

            => RegistrationsOf(Node).ByConnection.Values.FirstOrDefault(registration => registration.Primary);

        #endregion

        #region (private) EndRegistrations(Nodes)

        /// <summary>
        /// Nothing more is asked for these nodes: they were let go of, or this
        /// station is stopping.
        /// </summary>
        private void EndRegistrations(params AWWCPNetworkingNode[] Nodes)
        {
            foreach (var node in Nodes)
                if (registrations.TryGetValue(node, out var nodeRegistrations))
                    nodeRegistrations.Ended.Cancel();
        }

        #endregion


        #region (private) ConnectionIsUp(Connection)

        /// <summary>
        /// A connection is up: send what waits where it leads to the primary and
        /// that one accepted this station's node, and register where its back end
        /// did not accept it yet.
        /// </summary>
        private void ConnectionIsUp(ConnectionEntry Connection)
        {

            // A key older than its maximum age is rotated once a back end can be told.
            _ = RotateIfOld();
            _ = RenewStationCertificateIfDue();

            AWWCPNetworkingNode node = Connection.OCPPVersion == OCPP.OCPPVersion.OCPP1_6 ? cs01 : cs02;

            var registration = RegistrationOf(node, Connection);

            switch (registration.State)
            {

                case RegistrationState.Accepted:
                    if (registration.Primary)
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
                                            Destination:              SourceRouting.To(Registration.Destination),
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
                                        Destination:        SourceRouting.To(Registration.Destination),
                                        CancellationToken:  asking.Token
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

                StopTheHeartbeat(Registration);

                var what = $"{BackEndOf(Registration)} has not accepted this station's OCPP {Registration.Version} node yet ({status}); " +
                           $"it asks again in {wait.TotalSeconds:F0} s, and tells it nothing else until then.";

                Note(Registration.Connection, $"Connected, but {what}");

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
        /// The back end accepted the node. The primary: what waited is let go
        /// and sent, and the node sends its heartbeats by itself from now on.
        /// Any other: this station sends it heartbeats at the interval it gave.
        /// </summary>
        private void Accepted(AWWCPNetworkingNode  Node,
                              Registration         Registration,
                              TimeSpan             Interval)
        {

            Registration.State     = RegistrationState.Accepted;
            Registration.Interval  = Interval;

            var every = Interval >= ShortestHeartbeatInterval ? Interval : ShortestHeartbeatInterval;

            var what  = $"{BackEndOf(Registration)} accepted this station's OCPP {Registration.Version} node, " +
                        $"which sends it a heartbeat every {every.TotalSeconds:F0} s.";

            Log.Notice($"Registering: {what}", "ocpp", "connections");

            Note(Registration.Connection, $"Connected; {what}");

            if (Registration.Primary)
            {

                switch (Node)
                {
                    case OCPPv1_6.TestChargePointNode         chargePoint:      chargePoint.    HoldOfflineQueue = false; break;
                    case OCPPv2_1.CS.TestChargingStationNode  chargingStation:  chargingStation.HoldOfflineQueue = false; break;
                }

                SendWhatWaits(Registration.Connection);

            }

            else
                StartTheHeartbeat(Node, Registration, every);

        }

        #endregion

        #region (private) StartTheHeartbeat(Node, Registration, Every) / StopTheHeartbeat(Registration)

        /// <summary>
        /// Send heartbeats to a back end other than the primary, which the node
        /// does not: its own heartbeat goes to the primary alone.
        /// </summary>
        /// <remarks>
        /// One that cannot be sent - the connection is down for the moment - is
        /// not sent, and the next one is tried at its time.
        /// </remarks>
        private void StartTheHeartbeat(AWWCPNetworkingNode  Node,
                                       Registration         Registration,
                                       TimeSpan             Every)
        {

            CancellationTokenSource beating;

            lock (Registration)
            {
                Registration.Beating?.Cancel();
                beating = Registration.Beating = CancellationTokenSource.CreateLinkedTokenSource(Registration.Ended.Token);
            }

            _ = Task.Run(async () => {

                    while (!beating.IsCancellationRequested)
                    {

                        try
                        {
                            await Task.Delay(Every, TimeProvider, beating.Token);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        try
                        {

                            var destination = SourceRouting.To(Registration.Destination);

                            switch (Node)
                            {

                                case OCPPv1_6.TestChargePointNode chargePoint:
                                    await OCPPv1_6.CP.IChargePointNodeExtensions.SendHeartbeat(
                                              chargePoint,
                                              Destination:        destination,
                                              CancellationToken:  beating.Token
                                          );
                                    break;

                                case OCPPv2_1.CS.TestChargingStationNode chargingStation:
                                    await OCPPv2_1.CS.IChargingStationNodeExtensions.SendHeartbeat(
                                              chargingStation,
                                              Destination:        destination,
                                              CancellationToken:  beating.Token
                                          );
                                    break;

                            }

                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            Log.Debug($"A heartbeat to {BackEndOf(Registration)} could not be sent: {e.Message}", "ocpp", "connections");
                        }

                    }

                });

        }

        private static void StopTheHeartbeat(Registration Registration)
        {
            lock (Registration)
            {
                Registration.Beating?.Cancel();
                Registration.Beating = null;
            }
        }

        #endregion

        #region (private static) BackEndOf(Registration)

        /// <summary>
        /// Who a registration is with, for whoever reads the log.
        /// </summary>
        private static String BackEndOf(Registration Registration)

            => Registration.Primary
                   ? "the CSMS"
                   : $"the back end of '{Registration.Connection.Description}'";

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

            StopTheHeartbeat(Registration);

            Log.Warning($"Registering: {BackEndOf(Registration)} did not answer the BootNotification of this station's OCPP {Registration.Version} node " +
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
