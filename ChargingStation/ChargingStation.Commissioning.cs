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

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.WWCP.SSH;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// Commissioning over SSH: a tool speaking OCPP 2.1 to this station on
    /// the station's own SSH server, as a CSMS would over the uplink.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The subsystem "ocpp@open.charging.cloud", served by the OCPP 2.1 node
    /// to the accounts that may run <see cref="StationAccess.Commissioning"/>
    /// - the installer and the system administrator, unless the file says
    /// otherwise. The node's SSH server has let the account in by its key;
    /// what it may do here is what a CSMS may.
    /// </para>
    /// <para>
    /// The tool is a networking node of its own, named by its login: it
    /// does not connect as the CSMS. A route is one per destination, and the
    /// newest connection to a destination takes it: a tool connecting as the
    /// CSMS would take the route to the CSMS away from the uplink, and with
    /// it every request this station sends and every answer it owes the real
    /// CSMS, and leave it pointing at a closed connection when it went - until
    /// the uplink happened to reconnect. Under a name of its own, the tool
    /// gets the answers to its requests, and the uplink keeps everything
    /// else.
    /// </para>
    /// <para>
    /// OCPP 1.6 is not served. A subsystem has one handler, and the message
    /// server reads the HELLO that says which version a client speaks itself
    /// - so one name could not lead to either node. OCPP 2.1 and 2.0.1 are.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// What the node's SSH sessions call it: who, the SSH page, the audit.
        /// </summary>
        public const String  CommissioningLabel  = "OCPP commissioning";

        /// <summary>
        /// The commissioning server of the OCPP 2.1 node there is now: built
        /// with every node, so that a node rebuilt for new EVSEs has one too.
        /// </summary>
        private OCPPSshServer?  commissioningServer;

        /// <summary>
        /// Whether the subsystem is on the node's SSH server now.
        /// </summary>
        private Boolean         commissioningServed;

        #endregion

        #region Properties

        /// <summary>
        /// What the configuration file says about commissioning over SSH.
        /// </summary>
        public CommissioningConfiguration  Commissioning          { get; private set; } = new ();

        /// <summary>
        /// Whether commissioning over SSH is served now.
        /// </summary>
        public Boolean                     CommissioningServed
            => commissioningServed;

        /// <summary>
        /// What an account needs to be let in to it.
        /// </summary>
        public static Permission           CommissioningPermission
            => Permission.Run(StationAccess.Commissioning);

        /// <summary>
        /// Where a commissioning tool connects - the node's SSH server, as its
        /// own account - or null where it cannot: not served, or no SSH server.
        /// </summary>
        public String?                     CommissioningURL
            => commissioningServed ? SSHURL : null;

        /// <summary>
        /// The commissioning tools connected now, by the names they connected
        /// under.
        /// </summary>
        public IEnumerable<NetworkingNode_Id> CommissioningTools
            => commissioningServer?.ConnectedNetworkingNodeIds ?? [];

        #endregion


        #region (private) AttachCommissioning(Node)

        /// <summary>
        /// The commissioning server of the given OCPP 2.1 node, which the node
        /// stops when it is stopped: attached, and served only once the
        /// subsystem is on the node's SSH server.
        /// </summary>
        /// <remarks>
        /// <para>
        /// In standard networking - no destination on the wire - a request
        /// that comes in over a connection somebody else made is sent on to the
        /// CSMS: what a local controller does with the requests of the stations
        /// below it. This station has nothing below it, and a request a tool
        /// sends it is for itself; so the node takes the CSMS's name as one of
        /// its own, as a CSMS node does. Its own requests to the CSMS go out
        /// and are not received, and a CSMS on the uplink addresses this
        /// station by its own name - the name changes what is taken in from a
        /// tool and nothing else.
        /// </para>
        /// <para>
        /// The HELLO is held to the login: a tool connects as its account and
        /// as nothing else, and neither as the CSMS nor as this station.
        /// </para>
        /// </remarks>
        /// <param name="Node">A new OCPP 2.1 node of this station.</param>
        private OCPPSshServer AttachCommissioning(OCPPv2_1.CS.TestChargingStationNode Node)
        {

            var server = Node.AttachOCPPSshSubsystem();

            server.MessageServer.ValidateHello = (messageServer, context, hello, subprotocol, cancellationToken) =>
                                                     ValueTask.FromResult(
                                                         WhyNoCommissioningToolMayBe(context.Session.Username, Node.Id) is String why
                                                             ? SshHelloVerdict.Refuse(WWCPSshServer.RefusalNotPermitted, why)
                                                             : server.Validate(context.Session.Username, hello)
                                                     );

            Node.OCPP.IN.AnycastIds.Add(NetworkingNode_Id.CSMS);

            WireCommissioningLogging(server);

            return server;

        }

        #endregion

        #region (internal static) WhyNoCommissioningToolMayBe(Login, StationId)

        /// <summary>
        /// Why a tool may not connect under the given login, or null where it
        /// may: not as the CSMS, which would take the uplink's route, and not
        /// as this station, nor as no node at all.
        /// </summary>
        /// <param name="Login">The SSH login - the name of an account.</param>
        /// <param name="StationId">The name of the node it is connecting to.</param>
        internal static String? WhyNoCommissioningToolMayBe(String             Login,
                                                            NetworkingNode_Id  StationId)
        {

            if (!NetworkingNode_Id.TryParse(Login, out var tool))
                return null;    // refused by the server's own check, in its own words

            if (tool == NetworkingNode_Id.CSMS || tool == NetworkingNode_Id.CentralSystem)
                return $"'{Login}' is what this station calls its CSMS - a commissioning tool connects under a name of its own.";

            if (tool == NetworkingNode_Id.Broadcast || tool == NetworkingNode_Id.Zero)
                return $"'{Login}' names no single networking node.";

            if (tool == StationId)
                return $"'{Login}' is this station's own name.";

            return null;

        }

        #endregion

        #region (private) WireCommissioningLogging(Server)

        /// <summary>
        /// Who connected for commissioning and went away, in the event log.
        /// Who signed in, or was refused at the door, the node's SSH server
        /// says itself.
        /// </summary>
        private void WireCommissioningLogging(OCPPSshServer Server)
        {

            Server.OnNetworkingNodeConnectionAccepted += (timestamp, transport, connection, networkingNodeId, networkingMode, eventTrackingId, cancellationToken) => {

                Log.Notice(
                    $"'{networkingNodeId?.ToString() ?? connection.RemoteSocket.ToString()}' is connected for commissioning over SSH from {connection.RemoteSocket}" +
                    $" ({connection.Subprotocol ?? "no subprotocol"}).",
                    "ocpp", "ssh", "commissioning"
                );

                return Task.CompletedTask;

            };

            Server.OnNetworkingNodeConnectionClosed += (timestamp, transport, connection, networkingNodeId, close, cancellationToken) => {

                Log.Notice(
                    $"'{networkingNodeId?.ToString() ?? connection.RemoteSocket.ToString()}' went away from commissioning over SSH" +
                    $" ({(close.ByPeer ? "it closed" : "closed here")}{(close.Code.HasValue ? $", {close.Code}" : "")}{(close.Reason.IsNullOrEmpty() ? "" : $": {close.Reason}")}).",
                    "ocpp", "ssh", "commissioning"
                );

                return Task.CompletedTask;

            };

        }

        #endregion


        #region (private) ServeCommissioning() / StopServingCommissioning(Why)

        /// <summary>
        /// Serve commissioning over SSH from now on: the subsystem on the
        /// node's SSH server, for the accounts that may run it.
        /// </summary>
        /// <remarks>
        /// Said out loud where it cannot work: switched on with the SSH server
        /// off is a door painted on a wall.
        /// </remarks>
        private void ServeCommissioning()
        {

            if (commissioningServer is null || commissioningServed)
                return;

            commissioningServed = true;

            try
            {
                AddSSHSubsystem(new SSHSubsystem(OCPPSshServer.OCPPSubsystem, CommissioningLabel, commissioningServer.Handler, CommissioningPermission));
            }
            catch (Exception e)
            {

                commissioningServed = false;

                Log.Critical($"Commissioning over SSH could not be served: {e.Message}", "ocpp", "ssh", "commissioning");
                return;

            }

            if (!SSHEnabled)
                Log.Warning(
                    "Commissioning over SSH is switched on, but the SSH server of this charging station is off - so no tool can reach it. " +
                    "Switch SSH on under Configuration.",
                    "ocpp", "ssh", "commissioning"
                );

            else
                Log.Notice(
                    $"A commissioning tool may speak OCPP 2.1 to this station over SSH: {SSHURL} as an account that may {CommissioningPermission}, " +
                    $"subsystem {OCPPSshServer.OCPPSubsystem}, host key {SSHHostKey ?? "not known yet"}.",
                    "ocpp", "ssh", "commissioning"
                );

        }

        /// <summary>
        /// Serve commissioning over SSH no longer: the subsystem goes, and
        /// every session of it ends.
        /// </summary>
        /// <param name="Why">Why, for the log.</param>
        private void StopServingCommissioning(String Why)
        {

            if (!commissioningServed)
                return;

            commissioningServed = false;

            RemoveSSHSubsystem(OCPPSshServer.OCPPSubsystem);

            Log.Info($"Commissioning over SSH is no longer served: {Why}.", "ocpp", "ssh", "commissioning");

        }

        #endregion

        #region (private) ServeCommissioningByTheNewNode()

        /// <summary>
        /// After the OCPP nodes were rebuilt: the subsystem handed to the new
        /// OCPP 2.1 node, where it is served. The sessions of the old one end
        /// with it.
        /// </summary>
        private void ServeCommissioningByTheNewNode()
        {

            if (!commissioningServed || commissioningServer is null)
                return;

            RemoveSSHSubsystem(OCPPSshServer.OCPPSubsystem);

            try
            {
                AddSSHSubsystem(new SSHSubsystem(OCPPSshServer.OCPPSubsystem, CommissioningLabel, commissioningServer.Handler, CommissioningPermission));
            }
            catch (Exception e)
            {

                commissioningServed = false;

                Log.Critical($"Commissioning over SSH could not be served by the rebuilt OCPP node: {e.Message}", "ocpp", "ssh", "commissioning");

            }

        }

        #endregion


        #region CommissioningJSON() / TryUpdateCommissioningConfiguration(JSON, out Error [, out NotSaved])

        /// <summary>
        /// Commissioning over SSH, as the web interface reads it: what the
        /// file says, whether it is served, where a tool connects and who is
        /// connected.
        /// </summary>
        public JObject CommissioningJSON()

            => new (
                   new JProperty("enabled",       Commissioning.IsEnabled),
                   new JProperty("served",        commissioningServed),
                   new JProperty("sshServer",     SSHEnabled),
                   new JProperty("url",           SSHEnabled ? SSHURL : null),
                   new JProperty("hostKey",       SSHHostKey),
                   new JProperty("subsystem",     OCPPSshServer.OCPPSubsystem),
                   new JProperty("subprotocols",  new JArray(commissioningServer?.MessageServer.Options.Subprotocols ?? [])),
                   new JProperty("station",       cs02.Id.ToString()),
                   new JProperty("permission",    CommissioningPermission.ToString()),
                   new JProperty("tools",         new JArray(CommissioningTools.Select(tool => tool.ToString()).Order())),
                   new JProperty("file",          ConfigFile.Path)
               );

        /// <summary>
        /// Switch commissioning over SSH on or off.
        /// </summary>
        public Boolean TryUpdateCommissioningConfiguration(JObject                           JSON,
                                                           [NotNullWhen(false)] out String?  Error)

            => TryUpdateCommissioningConfiguration(JSON, out Error, out _);

        /// <summary>
        /// Switch commissioning over SSH on or off: written to the file, then
        /// served or not from that moment, without a restart. Switched off,
        /// every tool connected is let go of.
        /// </summary>
        /// <param name="JSON">The new section.</param>
        /// <param name="Error">What is wrong with it, or what stood in the way.</param>
        /// <param name="NotSaved">True where the configuration file could not be read or written: nothing about the change was wrong, and nothing was changed.</param>
        public Boolean TryUpdateCommissioningConfiguration(JObject                           JSON,
                                                           [NotNullWhen(false)] out String?  Error,
                                                           out Boolean                       NotSaved)
        {

            NotSaved = false;

            if (!CommissioningConfiguration.TryParse(JSON, out var wanted, out Error))
                return false;

            reconfigureLock.Wait();

            try
            {

                if (wanted == Commissioning)
                    return true;

                if (!ConfigFile.TryReplaceSection(CommissioningConfiguration.SectionName, wanted.ToJSON(), out Error))
                {
                    NotSaved = true;
                    return false;
                }

                Commissioning = wanted;

                if (wanted.IsEnabled)
                    ServeCommissioning();
                else
                    StopServingCommissioning("switched off");

                return true;

            }
            finally
            {
                reconfigureLock.Release();
            }

        }

        #endregion

    }

}
