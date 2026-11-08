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

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The ssh:// connections this charging station is told about: what it
    /// takes, what it refuses, and the host key it pins.
    /// </summary>
    /// <remarks>
    /// The host key is measured hardest, because it is the one thing here
    /// that is written by the station rather than by somebody: the first
    /// connection of one that pinned none writes the key it was shown, and
    /// nothing a person does on the page afterwards may undo that by
    /// accident - a save of the description from a page drawn before it.
    /// </remarks>
    [TestFixture]
    public class SshConnectionStoreTests
    {

        #region Data

        private String           directory  = "";
        private ConnectionStore  store      = default!;

        /// <summary>
        /// The host key of a server that is never started.
        /// </summary>
        private static readonly ISshHostKey  hostKey      = SshHostKey.GenerateEd25519();

        private static String Fingerprint
            => SshFingerprint.Sha256(hostKey.PublicKeyBlob);

        private const String  AnSshURL = "ssh://cs001@csms.example.org:2222";

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStore()
        {
            directory  = TestStations.TemporaryDirectory("ssh-connections");
            store      = new ConnectionStore(Path.Combine(directory, "conn"));
        }

        [TearDown]
        public void TakeItAwayAgain()
        {
            TestStations.Remove(directory);
        }

        #endregion


        #region AnSSHURLIsTakenAndSaysSo()

        /// <summary>
        /// An ssh:// URL is a connection like any other, and says that it goes
        /// over SSH - with a host key pinned or without one.
        /// </summary>
        [Test]
        public void AnSSHURLIsTakenAndSaysSo()
        {

            Assert.That(store.TryAddConnection("Over SSH, pinned", AnSshURL, "CSMS", true, null, null,
                                               out var pinned, out var error, SshHostKey: $"  {Fingerprint} "),
                        Is.True, error);

            Assert.That(store.TryAddConnection("Over SSH, not pinned", AnSshURL, "CSMS", true, null, null,
                                               out var notPinned, out error),
                        Is.True, error);

            var withPin     = store.Connections.Single(entry => entry.Id == pinned);
            var withoutPin  = store.Connections.Single(entry => entry.Id == notPinned);

            Assert.Multiple(() => {

                Assert.That(withPin.IsSSH,                                  Is.True);
                Assert.That(withPin.SshHostKey,                             Is.EqualTo(Fingerprint), "The pin was not taken as written, trimmed.");
                Assert.That(withPin.ToJSON().Value<String>("transport"),    Is.EqualTo("ssh"));
                Assert.That(withPin.Warnings,                               Is.Empty,
                            "An ssh:// connection proves itself with this station's key, and a pinned one has nothing to warn of.");

                Assert.That(withoutPin.SshHostKey,                          Is.Null);
                Assert.That(withoutPin.Warnings,                            Has.Some.Contains("No host key is pinned"),
                            "Trust on first use was not said out loud.");

            });

            // And read back as it was written.
            var again = new ConnectionStore(store.Path);

            Assert.That(again.Connections.Single(entry => entry.Id == pinned).SshHostKey, Is.EqualTo(Fingerprint));

        }

        #endregion

        #region WhatCannotBeAHostKeyIsRefused()

        /// <summary>
        /// A pin that is neither a fingerprint nor a public-key line is
        /// refused, and so is one beside a WebSocket, whose server is known by
        /// its certificate.
        /// </summary>
        [Test]
        public void WhatCannotBeAHostKeyIsRefused()
        {

            Assert.Multiple(() => {

                Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, null, null,
                                                   out _, out var notAKey, SshHostKey: "the one from last week"),
                            Is.False);
                Assert.That(notAKey, Does.Contain("neither a fingerprint"));

                Assert.That(store.TryAddConnection("Over a WebSocket", "wss://csms.example.org/cs001", "CSMS", true, null, null,
                                                   out _, out var notSSH, SshHostKey: Fingerprint),
                            Is.False);
                Assert.That(notSSH, Does.Contain("TLS certificate"));

                // A public-key line is a pin as well.
                Assert.That(store.TryAddConnection("Over SSH, by its key", AnSshURL, "CSMS", true, null, null,
                                                   out _, out var error,
                                                   SshHostKey: SshPublicKey.FromHostKey(hostKey).ToAuthorizedKeyLine()),
                            Is.True, error);

            });

            Assert.That(store.Connections, Has.Count.EqualTo(1));

        }

        #endregion

        #region ACertificateOrATOTPPasswordIsRefusedOverSSH()

        /// <summary>
        /// Over SSH there is no TLS handshake to show a certificate in, and no
        /// HTTP to answer a TOTP password in. A Basic login is taken: its
        /// password is what an SSH server asks for.
        /// </summary>
        [Test]
        public void ACertificateOrATOTPPasswordIsRefusedOverSSH()
        {

            Assert.That(store.TryAddAuthentication("A TOTP login", "totp", "cs001", "shared-secret-not-the-real-one", out var totp, out var error),
                        Is.True, error);

            Assert.That(store.TryAddAuthentication("A Basic login", "basic", "cs001", "not-the-real-one", out var basic, out error),
                        Is.True, error);

            Assert.Multiple(() => {

                Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, totp, null, out _, out var refusedTOTP),
                            Is.False);
                Assert.That(refusedTOTP, Does.Contain("TOTP"));

                Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, null, "some-certificate", out _, out var refusedCertificate),
                            Is.False);
                Assert.That(refusedCertificate, Does.Contain("TLS handshake"));

                Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, basic, null, out var withPassword, out error),
                            Is.True, error);

                Assert.That(store.Connections.Single(entry => entry.Id == withPassword).Warnings,
                            Has.None.Contains("in the clear"),
                            "A password over SSH is not sent in the clear.");

            });

        }

        #endregion

        #region AFirstHostKeyIsWrittenDownOnlyWhereNothingChanged()

        /// <summary>
        /// The host key the first connection was shown is written down - where
        /// the connection pins none, and still goes where it went.
        /// </summary>
        [Test]
        public void AFirstHostKeyIsWrittenDownOnlyWhereNothingChanged()
        {

            Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, null, null, out var id, out var error),
                        Is.True, error);

            var dialled = URL.Parse(AnSshURL);

            Assert.Multiple(() => {

                Assert.That(store.TryPinHostKey(id!, URL.Parse("ssh://cs001@elsewhere.example.org"), Fingerprint, out _), Is.False,
                            "A key shown by where the connection went before was written down for where it goes now.");

                Assert.That(store.TryPinHostKey(id!, dialled, Fingerprint, out error), Is.True, error);

                Assert.That(store.TryPinHostKey(id!, dialled, "SHA256:another", out _), Is.False,
                            "A pinned key was overwritten.");

            });

            Assert.That(new ConnectionStore(store.Path).Connections.Single().SshHostKey, Is.EqualTo(Fingerprint),
                        "The key written down did not reach the file.");

        }

        #endregion

        #region ASaveAsShownKeepsTheKeyWrittenDownMeanwhile()

        /// <summary>
        /// A page drawn before the first connection shows no host key. Saved
        /// with the field as it was, the key written down since stays; typed
        /// into, the field is what is taken - emptied, too.
        /// </summary>
        [Test]
        public void ASaveAsShownKeepsTheKeyWrittenDownMeanwhile()
        {

            Assert.That(store.TryAddConnection("Over SSH", AnSshURL, "CSMS", true, null, null, out var id, out var error),
                        Is.True, error);

            Assert.That(store.TryPinHostKey(id!, URL.Parse(AnSshURL), Fingerprint, out error), Is.True, error);

            Assert.That(store.TryUpdateConnection(id, "Over SSH, renamed", AnSshURL, "CSMS", true, null, null, out error,
                                                  SshHostKey: "", SshHostKeyAsShown: ""),
                        Is.True, error);

            Assert.That(store.Connections.Single().SshHostKey, Is.EqualTo(Fingerprint),
                        "A save of the description took away the host key written down meanwhile.");

            // Left out altogether, as by somebody using the API without it.
            Assert.That(store.TryUpdateConnection(id, "Over SSH, renamed again", AnSshURL, "CSMS", true, null, null, out error),
                        Is.True, error);

            Assert.That(store.Connections.Single().SshHostKey, Is.EqualTo(Fingerprint));

            // Emptied on purpose: the next first connection pins it again.
            Assert.That(store.TryUpdateConnection(id, "Over SSH", AnSshURL, "CSMS", true, null, null, out error,
                                                  SshHostKey: "", SshHostKeyAsShown: Fingerprint),
                        Is.True, error);

            Assert.That(store.Connections.Single().SshHostKey, Is.Null);

            // And a connection that is no longer ssh:// keeps none.
            Assert.That(store.TryPinHostKey(id!, URL.Parse(AnSshURL), Fingerprint, out error), Is.True, error);

            Assert.That(store.TryUpdateConnection(id, "Over a WebSocket now", "wss://csms.example.org/cs001", "CSMS", true, null, null, out error),
                        Is.True, error);

            Assert.That(store.Connections.Single().SshHostKey, Is.Null,
                        "A host key stayed beside a WebSocket, where nothing checks it.");

        }

        #endregion

    }

}
