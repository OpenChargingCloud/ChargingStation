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

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.SSH;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.OCPP.SSH;
using cloud.charging.open.protocols.OCPP.Node.KeyPins;

using OCPPv2_1 = cloud.charging.open.protocols.OCPPv2_1;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The SSH peers of this station: the hosts of its "ssh://" connections,
    /// each with the one SSH connection the station keeps to it. OCPP is a
    /// channel of that connection, and so is whatever else goes to that host
    /// over SSH and says SSHConnection Shared - the key exchange of a time
    /// server (section 8.3 of the white paper "NTS Key Establishment over
    /// SSH"), name resolution.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The connection is the station's, not OCPP's. Whoever needs it first has
    /// it made: a station that starts cold may have the time over it before a
    /// single OCPP message went over it. The station signs in on it with its
    /// own SSH key, under the login of the connection's entry, and the host is
    /// believed by what that entry holds it to: no second key pair, no pin of
    /// its own, no connection of its own for the others.
    /// </para>
    /// <para>
    /// Nothing but OCPP goes over a connection whose host is held to no pinned
    /// key: one that is believed by a key learned on its first connection and
    /// not confirmed yet, or that would learn its key from whoever asked first.
    /// On probation the station sends nothing but its BootNotification, and
    /// executes nothing but a signed pin set (section 10.4 of the white paper
    /// "OCPP over SSH"); time or names from that host would be time or names
    /// from a server nobody vouched for yet.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        // The peer each SSH client of OCPP is a channel of, for as long as the
        // station holds on to that client.
        private readonly ConcurrentDictionary<WWCPSshClient, SSHPeer> sshPeerOf = new ();

        #endregion

        #region (protected override) SSHPeerClientKey, SSHPeerClientPublicKey, DefaultSSHPeerUser, SSHPeerHostsToBe

        /// <summary>
        /// The key this station signs in with at the SSH peers of its
        /// configuration: its own, the one of the purpose "Station" - the key
        /// it is known by wherever it signs in over SSH, and the one OCPP is a
        /// channel under as well. Made at the first need.
        /// </summary>
        protected override ISshHostKey? SSHPeerClientKey
        {
            get
            {
                try
                {
                    return SshKey.MakeIfNeeded().GetAwaiter().GetResult();
                }
                catch (Exception e)
                {
                    Log.Error($"The SSH key of this station could not be had for its SSH peers: {e.Message}", "ocpp", "connections", "ssh", "security");
                    return null;
                }
            }
        }

        /// <summary>
        /// The public key this station signs in with at its SSH peers, as a
        /// line of an authorized_keys file.
        /// </summary>
        public override String? SSHPeerClientPublicKey

            => SSHPeerClientKey is not null
                   ? SshKey.PublicKeyLine
                   : null;

        /// <summary>
        /// The user name this station signs in under at an SSH peer whose entry
        /// names none: what it calls itself in OCPP.
        /// </summary>
        protected override String DefaultSSHPeerUser

            => cs02.Id.ToString();

        /// <summary>
        /// The hosts of the connections this station dials over SSH: each
        /// becomes an SSH peer when it is first dialled, and is one a name
        /// server may be given to share the connection of before that - the
        /// host of a network connection profile with an ssh:// URL, as section
        /// 7.2 of the white paper "DNS over SSH" has it. A station that starts
        /// cold has its names over that connection before OCPP went over it.
        /// </summary>
        protected override IEnumerable<String> SSHPeerHostsToBe

            => [.. Connections.Connections.Where (entry => entry.IsSSH).
                                           Select(entry => entry.URL.Host.ToString())];

        #endregion

        #region (private) SshPeerFor(Connection, Login, Trust)

        /// <summary>
        /// The SSH peer the host of the given connection becomes: with a
        /// connection of the station's own to it, made with the given login when
        /// the first one needs it. Null where that host is a peer already - of
        /// another connection of this station, with a login and a trust of its
        /// own - or its name is none a peer goes by: that connection is dialled
        /// for OCPP alone, as before.
        /// </summary>
        private SSHPeer? SshPeerFor(ConnectionEntry   Connection,
                                    SshClientOptions  Login,
                                    SshHostKeyTrust   Trust)
        {

            if (!DomainName.TryParse(Connection.URL.Host.ToString(), out var host, out _) || host is null)
                return null;

            if (SSHPeerTo(host) is not null)
                return null;

            var port  = Connection.URL.Port?.ToUInt16() ?? 22;

            var peer  = new SSHPeer(
                            host,
                            port,
                            new SshSharedConnection(
                                Connection.URL.Host.ToString(),
                                port,
                                Login with { DNSClient = Login.DNSClient ?? DNSClient }
                            ),
                            WhyNot:     subsystem => subsystem == OCPPSshServer.OCPPSubsystem
                                                         ? null
                                                         : Trust.Pins.Count == 0
                                                               ? "its host is held to no pinned host key yet, and nothing but OCPP goes over it until a signed pin set says which it is."
                                                               : null,
                            PublicKey:  () => SshKey.PublicKeyLine
                        );

            // Another one became the peer of that host meanwhile.
            if (AddSSHPeer(peer) is SSHPeer before)
            {
                AddSSHPeer(before);
                return null;
            }

            return peer;

        }

        #endregion

        #region (private) DropSshConnectionOf(Client)

        /// <summary>
        /// Close the SSH connection the given client of OCPP is a channel of,
        /// for everything on it. The host stays a peer: whoever needs the
        /// connection next has it made anew, with a handshake of its own - which
        /// is what a host that is believed no longer, or by something else than
        /// before, has to go through again.
        /// </summary>
        private async Task DropSshConnectionOf(WWCPSshClient Client)
        {

            if (!sshPeerOf.TryGetValue(Client, out var peer) || peer.Connection.Current is not SshClient ssh)
                return;

            try
            {
                await ssh.DisposeAsync();
            }
            catch (Exception e)
            {
                Log.Debug($"The SSH connection to {peer.Host.Trimmed} did not close cleanly: {e.Message}", "ocpp", "connections", "ssh");
            }

        }

        #endregion

        #region (private) ReleaseSshPeerOf(Client) / ReleaseSshPeer(Peer)

        /// <summary>
        /// The station is done with the given SSH client of OCPP: the peer it
        /// was a channel of is no peer any more, and its connection is closed -
        /// for everything that shared it.
        /// </summary>
        private async Task ReleaseSshPeerOf(WWCPSshClient Client)
        {

            if (sshPeerOf.TryRemove(Client, out var peer))
                await ReleaseSshPeer(peer);

        }

        /// <summary>
        /// Remove the given SSH peer and close its connection, unless an SSH
        /// client of OCPP the station still holds on to is a channel of it.
        /// </summary>
        private async Task ReleaseSshPeer(SSHPeer Peer)
        {

            if (sshPeerOf.Values.Any(other => ReferenceEquals(other, Peer)))
                return;

            RemoveSSHPeer(Peer);

            try
            {
                await Peer.Connection.DisposeAsync();
            }
            catch (Exception e)
            {
                Log.Debug($"The SSH connection to {Peer.Host.Trimmed} did not close cleanly: {e.Message}", "ocpp", "connections", "ssh");
            }

        }

        #endregion

        #region (private) SSHPeersSignedBy(Request, out Signer, out Why)

        /// <summary>
        /// Whether a signer this station believes signed the given SetVariables
        /// request, which writes its SSH peers: one of the pin set signers on its
        /// Connections page, as for the pins of a connection (white paper "SFTP
        /// for Logfile Uploads and Firmware Downloads", section 8.4).
        /// </summary>
        /// <remarks>
        /// <para>
        /// Within the time window, by this station's clock: a peer has no
        /// version that would stop an old request from coming back.
        /// </para>
        /// <para>
        /// The signature names the station as its destination: by an identity
        /// it has on one of its connections - which is what its CSMS knows it
        /// as - or by the id of its OCPP node. A peer belongs to no connection,
        /// so any of them will do.
        /// </para>
        /// </remarks>
        /// <param name="Request">The request.</param>
        /// <param name="Signer">Who signed it, as the Connections page says.</param>
        /// <param name="Why">Why it is not taken as signed.</param>
        private Boolean SSHPeersSignedBy(OCPPv2_1.CSMS.SetVariablesRequest?                                    Request,
                                         [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]  out String?      Signer,
                                         [System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out String?      Why)
        {

            Signer = null;

            if (Request is null || !Request.Signatures.Any())
            {
                Why = "The SSH peers of this station are written over OCPP only by a SetVariables request a signer of pin sets it believes signed, and this one is not signed.";
                return false;
            }

            // As this station's OCPP 2.1 node serializes it: what its signer signed.
            var json = Request.ToJSON(true,
                                      cs02.OCPP.CustomSetVariablesRequestSerializer,
                                      cs02.OCPP.CustomSetVariableDataSerializer,
                                      cs02.OCPP.CustomComponentSerializer,
                                      cs02.OCPP.CustomEVSESerializer,
                                      cs02.OCPP.CustomVariableSerializer,
                                      cs02.OCPP.CustomSignatureSerializer,
                                      cs02.OCPP.CustomCustomDataSerializer);

            String? why = null;

            foreach (var identity in Connections.Connections.
                                         Select  (NodeIdentityOf).
                                         Where   (identity => identity.HasValue).
                                         Select  (identity => identity!.Value).
                                         Append  (cs02.Id).
                                         Distinct())
            {

                if (PinSetSignatures.SignedBy(Request,
                                              json,
                                              Connections.PinSetSigners,
                                              identity,
                                              OCPPv2_1.Version.WebSocketSubProtocolId,
                                              "OCPP",
                                              WithoutTimeWindow: false,
                                              () => TimeProvider.GetUtcNow(),
                                              out Signer,
                                              out var notBy,
                                              "station"))
                {
                    Why = null;
                    return true;
                }

                why ??= notBy;

            }

            Why = why ?? "None of the request's signatures is a valid one of a signer this station believes.";
            return false;

        }

        #endregion

    }

}
