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

using org.GraphDefined.Vanaheimr.Hermod;

using cloud.charging.open.protocols.WWCP.Node.Connections;
using cloud.charging.open.protocols.WWCP.Node.KeyPins;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Trust on first use by asking over wss:// (mDNS-Concept.md, B.4): the key
    /// pin of the key shown waits for an admin and nothing goes over the
    /// connection; reported as a security event; confirmed it is pinned and
    /// the station registers, turned down the station stays away.
    /// </summary>
    public partial class ConnectingWithKeyPinsTests
    {

        private Boolean StandsAt(String Id, ConnectionStatus Status)
            => station!.ConnectionStates.TryGetValue(Id, out var state) && state.Status == Status;


        #region AKeyPinThatAsks_WaitsForAnAdmin_AndConfirmedTheStationRegisters()

        /// <summary>
        /// "Ask": the key pin of the key shown is written down and reported, no
        /// WebSocket is opened, and the station says that it waits; confirmed by
        /// an admin it is pinned and confirmed, and the station connects again by
        /// itself and registers.
        /// </summary>
        [Test]
        public async Task AKeyPinThatAsks_WaitsForAnAdmin_AndConfirmedTheStationRegisters()
        {

            var id   = Added(null, new ServerKeyPinSettings(null, "Ask", null));
            var pin  = KeyPin.FromKey(lcKey);

            await Start();

            await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));
            await Until(() => Reported("KeyPinAwaitingConfirmation").Length > 0,    "Reporting the key pin that waits");

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPinAwaiting,              Is.EqualTo(pin));
                Assert.That(Entry.ServerKeyPins,                     Is.Empty);
                Assert.That(lc!.Accepted,                            Is.Zero, "A WebSocket was opened to a server nobody confirmed.");
                Assert.That(lc. Requests("BootNotification"),        Is.Empty);
                Assert.That(Reported("KeyPinAwaitingConfirmation")[0].Value<String>("techInfo"), Does.Contain(pin.ToHex()).And.Contain(id));
                Assert.That(SaidOf(id),                              Does.Contain("waits for an admin to confirm the key pin"));
            });

            Assert.That(station!.Connections.TryConfirmKey(id, "alice", pin.ToHex(), out _, out var error), Is.True, error);

            await Until(() => IsConnected(id),                                "Connecting once confirmed", () => SaidOf(id));
            await Until(() => lc!.Requests("BootNotification").Length > 0,   "A BootNotification once confirmed");

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPins,         Is.EqualTo(new[] { pin }));
                Assert.That(Entry.ServerKeyPinState,     Is.EqualTo(KeyPinState.Confirmed));
                Assert.That(Entry.ServerKeyPinAwaiting,  Is.Null);
                Assert.That(Entry.KeyConfirmedBy,        Is.EqualTo("alice"));
            });

        }

        #endregion

        #region AKeyPinTurnedDown_KeepsTheStationAway()

        /// <summary>
        /// Turned down by an admin: forgotten, the connection refused with who
        /// did it, and no WebSocket opened.
        /// </summary>
        [Test]
        public async Task AKeyPinTurnedDown_KeepsTheStationAway()
        {

            var id = Added(null, new ServerKeyPinSettings(null, "Ask", null));

            await Start();

            await Until(() => StandsAt(id, ConnectionStatus.AwaitingConfirmation), "Waiting for an admin", () => SaidOf(id));

            Assert.That(station!.Connections.TryRejectKey(id, "bob", out _, out var error), Is.True, error);

            await Until(() => StandsAt(id, ConnectionStatus.Refused), "Refusing the connection turned down", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(Entry.KeyRejected,   Is.EqualTo(KeyPin.FromKey(lcKey).ToString()));
                Assert.That(SaidOf(id),          Does.Contain("bob turned down the key"));
                Assert.That(lc!.Accepted,        Is.Zero);
            });

        }

        #endregion

    }

}
