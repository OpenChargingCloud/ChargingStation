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

using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// Whether to believe the host key the server of an ssh:// connection
    /// shows: the one pinned, or - where none is - the first one shown, and no
    /// other after it.
    /// </summary>
    /// <remarks>
    /// One per dialled connection, and handed to its client for every attempt
    /// it makes: the first connection, and every one after a drop. What it was
    /// shown is kept, so that the station can say which key it was - on the
    /// page, after a test, and when it writes down the first one.
    /// </remarks>
    public sealed class SshHostKeyTrust
    {

        #region Data

        private readonly HostKeyPolicy?  policy;
        private readonly Object          lockObject  = new ();
        private          Byte[]?         firstShown;
        private          Byte[]?         lastShown;
        private          Boolean         refused;

        #endregion

        #region Properties

        /// <summary>
        /// The host key pinned - a fingerprint or a public-key line - or null,
        /// for the first one shown.
        /// </summary>
        public String?  Pinned        { get; }

        /// <summary>
        /// The server's host name, as the connection names it.
        /// </summary>
        public String   Host          { get; }

        /// <summary>
        /// The server's port.
        /// </summary>
        public IPPort   Port          { get; }

        /// <summary>
        /// The SHA-256 fingerprint of the host key shown last; null before any.
        /// </summary>
        public String?  Shown
        {
            get
            {
                lock (lockObject)
                    return lastShown is null ? null : SshFingerprint.Sha256(lastShown);
            }
        }

        /// <summary>
        /// The kind of the host key shown last - "ssh-ed25519" - or null before
        /// any.
        /// </summary>
        public String?  ShownKind
        {
            get
            {
                lock (lockObject)
                    return lastShown is null ? null : new SshPublicKey(lastShown).Algorithm;
            }
        }

        /// <summary>
        /// Whether the host key shown last was turned away.
        /// </summary>
        public Boolean  Refused
        {
            get
            {
                lock (lockObject)
                    return refused;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// A host key was turned away: not the one pinned, or not the one shown
        /// first.
        /// </summary>
        public event Action<String>? OnRefused;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The trust of one connection.
        /// </summary>
        /// <param name="Pinned">The host key pinned, as the connection was checked to have it; null for the first one shown.</param>
        /// <param name="Host">The server's host name.</param>
        /// <param name="Port">The server's port.</param>
        public SshHostKeyTrust(String?  Pinned,
                               String   Host,
                               IPPort   Port)
        {

            this.Pinned  = Pinned;
            this.Host    = Host;
            this.Port    = Port;
            this.policy  = Pinned is not null
                               ? HostKeyPolicy.Pin(Pinned)
                               : null;

        }

        #endregion


        #region Verify(HostKeyBlob)

        /// <summary>
        /// Whether to believe the given host key: what the SSH client asks
        /// before it signs in.
        /// </summary>
        public Boolean Verify(Byte[] HostKeyBlob)
        {

            Boolean believed;

            lock (lockObject)
            {

                lastShown   = HostKeyBlob;

                if (policy is not null)
                    believed = policy.Verify(Host, Port, HostKeyBlob);

                else if (firstShown is null)
                {
                    firstShown  = HostKeyBlob;
                    believed    = true;
                }

                else
                    believed = firstShown.AsSpan().SequenceEqual(HostKeyBlob);

                refused = !believed;

            }

            if (!believed)
                OnRefused?.Invoke(WhyRefused());

            return believed;

        }

        #endregion

        #region WhyRefused()

        /// <summary>
        /// The sentence that says which key was shown and which one was
        /// expected.
        /// </summary>
        public String WhyRefused()
        {

            lock (lockObject)
            {

                var shown = lastShown is null
                                ? "no host key"
                                : $"the host key {new SshPublicKey(lastShown).Algorithm} {SshFingerprint.Sha256(lastShown)}";

                return Pinned is not null
                           ? $"its server showed {shown}, which is not the one pinned ({Pinned})"
                           : $"its server showed {shown}, which is not the one it showed first " +
                             $"({(firstShown is null ? "none" : SshFingerprint.Sha256(firstShown))})";

            }

        }

        #endregion

    }

}
