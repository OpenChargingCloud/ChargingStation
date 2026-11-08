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

using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.protocols.WWCP.Node;
using cloud.charging.open.protocols.WWCP.Node.Logging;

#endregion

namespace cloud.charging.open.ChargingStation.OCPP
{

    /// <summary>
    /// The key this charging station signs in with when it dials an ssh://
    /// connection.
    /// </summary>
    /// <remarks>
    /// Its own, and not the host key of its SSH server: that one says which
    /// machine this is to whoever dials it, this one says who is dialling. One
    /// key for both would make every back end that let this station in a back
    /// end that could pass for it.
    ///
    /// Made the first time it is needed - when an ssh:// connection is written
    /// down, tested or dialled - and kept from then on, in a directory only
    /// its owner may read. A station that never dials over SSH has none.
    ///
    /// What the page shows is its public half, the line that goes into the
    /// back end's authorized_keys. The private half never leaves the file.
    /// </remarks>
    public sealed class SshClientKey
    {

        #region Data

        /// <summary>
        /// The directory beside the connections, when nobody says otherwise.
        /// </summary>
        public const String  DirectoryName  = "ssh";

        /// <summary>
        /// The file the private key is kept in; its public half beside it, in
        /// the same name with ".pub".
        /// </summary>
        public const String  FileName       = "id_ed25519";

        private readonly SemaphoreSlim  making  = new (1, 1);
        private          ISshHostKey?   key;

        #endregion

        #region Properties

        /// <summary>
        /// Where the private key is kept.
        /// </summary>
        public String   Path          { get; }

        /// <summary>
        /// What the key's public half says about whose it is.
        /// </summary>
        public String   Comment       { get; }

        /// <summary>
        /// The key, where it was made; null until the first ssh:// connection
        /// needed it.
        /// </summary>
        public ISshHostKey?  Key

            => Volatile.Read(ref key);

        /// <summary>
        /// The public half, as a line of authorized_keys; null where there is
        /// no key yet.
        /// </summary>
        public String?  PublicKeyLine

            => Key is ISshHostKey made
                   ? SshPublicKey.FromHostKey(made, Comment).ToAuthorizedKeyLine()
                   : null;

        /// <summary>
        /// The public half's SHA-256 fingerprint, as OpenSSH writes it; null
        /// where there is no key yet.
        /// </summary>
        public String?  Fingerprint

            => Key is ISshHostKey made
                   ? SshFingerprint.Sha256(made.PublicKeyBlob)
                   : null;

        #endregion

        #region Events

        /// <summary>
        /// Something happened here that belongs in the log of the station: a
        /// key made, a directory that could not be restricted.
        /// </summary>
        public event Action<LogLevel, String>? OnNotice;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The key in the given directory, where there is one.
        /// </summary>
        /// <param name="Directory">The directory the key is kept in; it need not exist yet.</param>
        /// <param name="Comment">What the public half says about whose it is.</param>
        /// <exception cref="InvalidOperationException">There is a key, and it cannot be read.</exception>
        public SshClientKey(String  Directory,
                            String  Comment)
        {

            this.Path     = System.IO.Path.Combine(System.IO.Path.GetFullPath(Directory), FileName);
            this.Comment  = Comment;

            if (File.Exists(Path))
                key = Read(Path);

        }

        #endregion


        #region MakeIfNeeded(CancellationToken = default)

        /// <summary>
        /// The key, made now where there was none.
        /// </summary>
        public async Task<ISshHostKey> MakeIfNeeded(CancellationToken CancellationToken = default)
        {

            if (Key is ISshHostKey made)
                return made;

            await making.WaitAsync(CancellationToken);

            try
            {

                if (Key is ISshHostKey madeMeanwhile)
                    return madeMeanwhile;

                // Its directory is its owner's alone: whoever can read the key
                // can sign in as this station wherever it is let in.
                OwnerOnly.Directory(System.IO.Path.GetDirectoryName(Path)!,
                                    problem => OnNotice?.Invoke(LogLevel.Warning, problem));

                // One somebody put there since this was made, rather than a
                // new identity over it.
                if (File.Exists(Path))
                {
                    var found = Read(Path);
                    Volatile.Write(ref key, found);
                    return found;
                }

                var newKey = SshHostKey.GenerateEd25519();

                await SshKeyGenerator.WriteKeyPairAsync(newKey, Path, Comment, CancellationToken);

                Volatile.Write(ref key, newKey);

                OnNotice?.Invoke(LogLevel.Notice,
                                 $"A key was made for this station to sign in with over SSH: {newKey.AlgorithmNames[0]} " +
                                 $"{SshFingerprint.Sha256(newKey.PublicKeyBlob)}, kept in '{Path}'. " +
                                 "Its public half goes into the authorized_keys of every back end this station dials over SSH.");

                return newKey;

            }
            finally
            {
                making.Release();
            }

        }

        #endregion

        #region ToJSON()

        /// <summary>
        /// What the page shows: the public half and where the key is kept, or
        /// null where there is none.
        /// </summary>
        public JObject? ToJSON()
        {

            if (Key is null)
                return null;

            return new JObject(
                       new JProperty("publicKey",    PublicKeyLine),
                       new JProperty("fingerprint",  Fingerprint),
                       new JProperty("path",         Path)
                   );

        }

        #endregion


        #region (private static) Read(Path)

        /// <summary>
        /// A key that is there and cannot be read stops the station, as a
        /// configuration file that cannot be read does: making a new one would
        /// be a new identity, which every back end that knew the old one turns
        /// away.
        /// </summary>
        private static ISshHostKey Read(String Path)
        {
            try
            {
                return SshKeyGenerator.LoadPrivateKey(File.ReadAllText(Path)).Key;
            }
            catch (Exception problem)
            {
                throw new InvalidOperationException(
                          $"The key this station signs in with over SSH, in '{Path}', could not be read: {problem.Message} " +
                          "Repair it, or remove it for a new one - whose public half every back end then has to be given again.",
                          problem
                      );
            }
        }

        #endregion

    }

}
