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

using System.Diagnostics;
using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using cloud.charging.open.protocols.WWCP.Node.Connections;

using CSMS = cloud.charging.open.ChargingStation.Tests.TalkingToTheCSMSTests.CSMS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What is written down about the connections is what is dialled, from the
    /// moment it is written - work package P7 of EVQI Transport.
    /// </summary>
    /// <remarks>
    /// A connection written down, removed, switched on or off, sent elsewhere
    /// or given other credentials used to wait for the next start, and the
    /// page said so. Now the station takes it at once: what dials by itself is
    /// dialled, what does not any more is hung up, and what is dialled
    /// differently is hung up and dialled again. A description written anew
    /// changes nothing that is up.
    /// </remarks>
    [TestFixture]
    public class LiveChangesTests
    {

        #region Data

        private static TimeSpan   Patience
            => TalkingToTheCSMSTests.Patience;

        private String            directory  = "";
        private ChargingStation?  station;
        private CSMS?             first;
        private CSMS?             second;

        #endregion

        #region SetUp / TearDown

        [SetUp]
        public void MakeAStation()
        {
            directory  = TestStations.TemporaryDirectory("live-changes");
            station    = TestStations.New(directory, TestStations.Offline);
            first      = new CSMS();
        }

        [TearDown]
        public async Task TakeItAwayAgain()
        {

            if (station is not null)
                await station.DisposeAsync();

            if (first is not null)
                await first.DisposeAsync();

            if (second is not null)
                await second.DisposeAsync();

            station  = null;
            first    = null;
            second   = null;

            TestStations.Remove(directory);

        }

        #endregion


        #region AConnectionWrittenDownIsDialledAtOnce()

        /// <summary>
        /// A connection written down while the station runs, set to dial by
        /// itself, is dialled - and the one that was up stays up.
        /// </summary>
        [Test]
        public async Task AConnectionWrittenDownIsDialledAtOnce()
        {

            var firstId  = await ConnectedToTheFirst();

            second = new CSMS();

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("Another back end"), URL.Parse($"ws://127.0.0.1:{second.Port}/cs001"), "LocalController",
                                                              true, null, null, out var secondId, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(await Within(() => second.Connections == 1 && Said(secondId!).StartsWith("Connected", StringComparison.Ordinal)), Is.True,
                        $"what was written down was not dialled: '{Said(secondId!)}'");

            Assert.That(first!.Accepted, Is.EqualTo(1), "the connection that was up was dialled again");
            Assert.That(Said(firstId),   Does.StartWith("Connected"));

        }

        #endregion

        #region AConnectionRemovedIsHungUp()

        /// <summary>
        /// A connection removed while it is up is hung up, and nothing is said of
        /// it any more.
        /// </summary>
        [Test]
        public async Task AConnectionRemovedIsHungUp()
        {

            var firstId = await ConnectedToTheFirst();

            Assert.That(station!.Connections.TryRemoveConnection(firstId, out var error), Is.True, error);

            Assert.That(await Within(() => first!.Connections == 0), Is.True, "the connection removed was not hung up");
            Assert.That(await Within(() => !station.DialledConnections.ContainsKey(firstId)), Is.True,
                        "a connection removed is still said to be dialled");

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(first!.Accepted, Is.EqualTo(1), "the connection removed came back");

        }

        #endregion

        #region AnAddressChangedIsDialledThere()

        /// <summary>
        /// A connection sent elsewhere is hung up and dialled where it is sent,
        /// registers there, and the CSMS it left is not dialled again.
        /// </summary>
        [Test]
        public async Task AnAddressChangedIsDialledThere()
        {

            var firstId = await ConnectedToTheFirst();

            second = new CSMS();

            Assert.That(station!.Connections.TryUpdateConnection(firstId, I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{second.Port}/cs001"), "CSMS",
                                                                 true, null, null, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(await Within(() => second.Connections == 1 && first!.Connections == 0), Is.True,
                        $"the connection was not moved: '{Said(firstId)}'");

            Assert.That(await Within(() => station.ConnectionStates.TryGetValue(firstId, out var state) &&
                                           state.Status == ConnectionStatus.Connected &&
                                           state.URL.ToString().Contains($":{second.Port}/", StringComparison.Ordinal)),
                        Is.True, "what is said of it is not where it was sent");

            Assert.That(await Within(() => second.Requests("BootNotification").Length > 0 &&
                                           station.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"),
                        Is.True, "the station did not register where the connection was sent");

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(first!.Accepted, Is.EqualTo(1), "the CSMS it left was dialled again");

        }

        #endregion

        #region ADescriptionChangedChangesNothing()

        /// <summary>
        /// A connection given another description stays as it is.
        /// </summary>
        [Test]
        public async Task ADescriptionChangedChangesNothing()
        {

            var firstId = await ConnectedToTheFirst();

            Assert.That(station!.Connections.TryUpdateConnection(firstId, I18NString.Create("The CSMS, renamed"), URL.Parse($"ws://127.0.0.1:{first!.Port}/cs001"), "CSMS",
                                                                 true, null, null, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(first.Accepted,    Is.EqualTo(1), "a description written anew dialled the connection again");
            Assert.That(first.Connections, Is.EqualTo(1));
            Assert.That(Said(firstId),     Does.StartWith("Connected"));

        }

        #endregion

        #region SwitchedOffItIsHungUpAndSwitchedOnDialled()

        /// <summary>
        /// A connection that no longer dials by itself is hung up, and dialled
        /// again once it does.
        /// </summary>
        [Test]
        public async Task SwitchedOffItIsHungUpAndSwitchedOnDialled()
        {

            var firstId  = await ConnectedToTheFirst();
            var url      = URL.Parse($"ws://127.0.0.1:{first!.Port}/cs001");

            Assert.That(station!.Connections.TryUpdateConnection(firstId, I18NString.Create("The CSMS"), url, "CSMS", false, null, null, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(await Within(() => first.Connections == 0 && !station.DialledConnections.ContainsKey(firstId)), Is.True,
                        "a connection switched off was not hung up");

            Assert.That(station.Connections.TryUpdateConnection(firstId, I18NString.Create("The CSMS"), url, "CSMS", true, null, null, out error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            Assert.That(await Within(() => first.Accepted == 2 && first.Connections == 1 && Said(firstId).StartsWith("Connected", StringComparison.Ordinal)),
                        Is.True, $"a connection switched on again was not dialled: '{Said(firstId)}'");

        }

        #endregion

        #region CredentialsChangedAreDialledWith()

        /// <summary>
        /// Credentials given another secret are what every connection naming
        /// them is dialled with again - and credentials named by none change
        /// nothing.
        /// </summary>
        [Test]
        public async Task CredentialsChangedAreDialledWith()
        {

            Assert.That(station!.Connections.TryAddPasswordAuthentication("cs001", "an old secret of sixteen", I18NString.Create("The CSMS's password"),
                                                                  out var login, out var error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddPasswordAuthentication("cs002", "a secret named by no one", I18NString.Create("Nobody's password"),
                                                                 out var unused, out error),
                        Is.True, error);

            Assert.That(station.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{first!.Port}/cs001"), "CSMS", true, login, null,
                                                             out var id, out error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => Said(id!).StartsWith("Connected", StringComparison.Ordinal)), Is.True, Said(id!));

            Assert.That(station.Connections.TryUpdateBasicAuthentication(unused, "cs002", "another secret no one names", I18NString.Create("Nobody's password"),
                                                                    out error),
                        Is.True, error);

            await Task.Delay(TimeSpan.FromSeconds(2));

            Assert.That(first.Accepted, Is.EqualTo(1), "credentials named by no connection dialled one again");

            Assert.That(station.Connections.TryUpdateBasicAuthentication(login, "cs001", "a new secret of sixteen", I18NString.Create("The CSMS's password"),
                                                                    out error),
                        Is.True, error);

            Assert.That(await Within(() => first.Accepted == 2 && first.Connections == 1 && Said(id!).StartsWith("Connected", StringComparison.Ordinal)),
                        Is.True, $"the connection was not dialled again with what changed: '{Said(id!)}'");

        }

        #endregion

        #region TheDialRevisionCountsWhatIsDialledDifferently()

        /// <summary>
        /// What the store tells the station a change is: one in how a connection
        /// is dialled, not one in what it is called or a host key learned.
        /// </summary>
        [Test]
        public void TheDialRevisionCountsWhatIsDialledDifferently()
        {

            var store = station!.Connections;

            Assert.That(store.TryAddPasswordAuthentication("cs001", "a secret of sixteen", I18NString.Create("A password"), out var login, out var error), Is.True, error);
            Assert.That(store.TryAddConnection(I18NString.Create("A"), URL.Parse("ws://a.example.org/cs001"), "CSMS", true, login, null, out var id, out error), Is.True, error);

            Assert.That(store.DialRevisionOf(id!), Is.EqualTo(0));

            Assert.That(store.TryUpdateConnection(id, I18NString.Create("A, renamed"), URL.Parse("ws://a.example.org/cs001"), "CSMS", true, login, null, out error), Is.True, error);
            Assert.That(store.DialRevisionOf(id!), Is.EqualTo(0), "a description is no change of how it is dialled");

            Assert.That(store.TryUpdateConnection(id, I18NString.Create("A, renamed"), URL.Parse("ws://b.example.org/cs001"), "CSMS", true, login, null, out error), Is.True, error);
            Assert.That(store.DialRevisionOf(id!), Is.EqualTo(1), "an address is");

            Assert.That(store.TryUpdateConnection(id, I18NString.Create("A, renamed"), URL.Parse("ws://b.example.org/cs001"), "CSMS", false, login, null, out error), Is.True, error);
            Assert.That(store.DialRevisionOf(id!), Is.EqualTo(2), "whether it dials by itself is");

            Assert.That(store.TryUpdateBasicAuthentication(login, "cs001", "another secret of sixteen", I18NString.Create("A password"), out error), Is.True, error);
            Assert.That(store.DialRevisionOf(id!), Is.EqualTo(3), "what it proves itself with is");

            Assert.That(store.DialRevisionOf("nothing-under-this-id"), Is.EqualTo(0));

        }

        #endregion


        #region (private) ConnectedToTheFirst()

        /// <summary>
        /// The station started with one connection to the first CSMS, over OCPP
        /// 2.1, and registered with it.
        /// </summary>
        private async Task<String> ConnectedToTheFirst()
        {

            Assert.That(station!.Connections.TryAddConnection(I18NString.Create("The CSMS"), URL.Parse($"ws://127.0.0.1:{first!.Port}/cs001"), "CSMS", true, null, null,
                                                              out var id, out var error, OCPPVersion: "OCPP2.1"),
                        Is.True, error);

            var started = station = await TestStations.Started(station, directory);

            Assert.That(await Within(() => started.RegistrationJSON()["2.1"]?["state"]?.Value<String>() == "Accepted"), Is.True,
                        $"the station did not register with the first CSMS: '{Said(id!)}'");

            return id!;

        }

        #endregion

        #region (private) Said(Id)

        private String Said(String Id)

            => station!.DialledConnections.TryGetValue(Id, out var said)
                   ? said
                   : "(nothing)";

        #endregion

        #region (private static) Within(Condition)

        private static async Task<Boolean> Within(Func<Boolean> Condition)
        {

            var watch = Stopwatch.StartNew();

            while (!Condition())
            {

                if (watch.Elapsed > Patience)
                    return false;

                await Task.Delay(20);

            }

            return true;

        }

        #endregion

    }

}
