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

using NUnit.Framework;

using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod;
using org.GraphDefined.Vanaheimr.Hermod.PKI;

using cloud.charging.open.protocols.WWCP;
using cloud.charging.open.protocols.WWCP.NetworkingNode;
using cloud.charging.open.protocols.OCPPv2_1;
using cloud.charging.open.protocols.OCPPv2_1.CSMS;

using cloud.charging.open.ChargingStation.OCPP;

using cloud.charging.open.protocols.OCPP.Node.KeyPins;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// Signed pin sets over OCPP 2.1 ("Key Pins and Raw Public Keys", work package
    /// K4, phase B1): the station shows its wss:// connections as instances of
    /// TLSClientController, and takes a pin set - ServerKeyPins with a higher
    /// ServerKeyPinsVersion - from a SetVariables request a signer it believes
    /// signed, whenever it was signed, for the station as its CSMS knows it on
    /// that connection; on probation it lets the CSMS read TLSClientController
    /// and write that connection's instance, and nothing else.
    /// </summary>
    /// <remarks>
    /// The test's Local Controller signs as a CSMS would and sends the request
    /// over the connection the station made to it, as a Local Controller relays
    /// it.
    /// </remarks>
    public partial class DiallingWithKeyPinsTests
    {

        #region (private) Helpers of pin sets

        /// <summary>
        /// A key the station believes pin sets of, written down on its
        /// Connections page.
        /// </summary>
        private ECCKeyPair ASigner()
        {

            var signer = ECCKeyPair.GenerateKeys()!;

            Assert.That(station!.Connections.TryAddPinSetSigner(Convert.ToBase64String(signer.PublicKeyBytes), "The CSMS", out var error, out _),
                        Is.True, error);

            return signer;

        }

        /// <summary>
        /// The entries of a pin set.
        /// </summary>
        private static (String Variable, String Value)[] PinSetOf(String Pins, UInt64 Version)

            => [ ("ServerKeyPins", Pins), ("ServerKeyPinsVersion", Version.ToString()) ];

        /// <summary>
        /// A SetVariables request of the TLSClientController instance of the given connection, signed by
        /// the given key or by none, as a CSMS sends it: for the station as it knows it - "cs001", the last
        /// segment of the URL the station dials - and with the message id it is to go under, which the
        /// signature names, or "*" and a nonce.
        /// </summary>
        /// <remarks>
        /// Signed as the CSMS's adapter signs: its policy bound to the CSMS, which names the protocol
        /// version. This is what phase B3 sends.
        /// </remarks>
        private static (JObject Payload, String Id) Signed(String                                     Connection,
                                                           IEnumerable<(String Variable, String Value)>  Values,
                                                           ECCKeyPair?                                Signer,
                                                           String                                     Destination   = "cs001",
                                                           DateTimeOffset?                            SignedAt      = null,
                                                           Boolean                                    Nonce         = false)
        {

            var id      = Guid.NewGuid().ToString();

            var request = new SetVariablesRequest(
                              SourceRouting.To(NetworkingNode_Id.Parse(Destination)),
                              [.. Values.Select(value => new SetVariableData(new Component("TLSClientController", Connection),
                                                                             new Variable(value.Variable),
                                                                             value.Value))],
                              RequestId: Request_Id.Parse(id)
                          );

            if (Signer is not null)
                Assert.That(new SignaturePolicy { NonceInsteadOfMessageId = Nonce }.
                                BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv2_1.Version.WebSocketSubProtocolId).
                                SignRequestMessage(request, request.ToJSON(true), out var notSigned,
                                                   Signer.ToSignInfo1("CSMS", I18NString.Create("pin set"), SignedAt ?? DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            return (request.ToJSON(false), id);

        }

        /// <summary>
        /// Send a SetVariables request over the station's connection to the Local Controller, under the
        /// message id it was signed for or under the given one.
        /// </summary>
        private Task<JArray> Send((JObject Payload, String Id) Request, String? Id = null)

            => lc!.Ask("SetVariables", Request.Payload, Id ?? Request.Id);

        private static String?[] Statuses(JArray Answer)
            => [.. Answer[2]!["setVariableResult"]!.Select(result => result.Value<String>("attributeStatus"))];

        private static String?[] Reasons(JArray Answer)
            => [.. Answer[2]!["setVariableResult"]!.Select(result => result["attributeStatusInfo"]?.Value<String>("reasonCode"))];

        private static String Infos(JArray Answer)
            => String.Join(" | ", Answer[2]!["setVariableResult"]!.Select(result => result["attributeStatusInfo"]?.Value<String>("additionalInfo")));

        private static Boolean IsSecurityError(JArray Answer)
            => Answer[0].Value<Int32>() == 4 && Answer[2].Value<String>() == "SecurityError";

        private static JObject Read(String Component, String? Instance, String Variable)

            => new (new JProperty("component", Instance is not null
                                                   ? new JObject(new JProperty("name", Component), new JProperty("instance", Instance))
                                                   : new JObject(new JProperty("name", Component))),
                    new JProperty("variable",  new JObject(new JProperty("name", Variable))));

        private TLSClientController[] Instances
            => [.. station!.OCPPv2_1Node.GetComponentConfigs(nameof(TLSClientController)).OfType<TLSClientController>()];

        #endregion


        #region AnOCPPPinSet_ConfirmsTheLearnedKeyPin_AndEndsTheProbation()

        /// <summary>
        /// A signed SetVariables of TLSClientController[id].ServerKeyPins and ServerKeyPinsVersion with
        /// the learned key pin confirms it: both are Accepted, the probation ends, what the server asks is
        /// executed again, and the same version again is refused.
        /// </summary>
        [Test]
        public async Task AnOCPPPinSet_ConfirmsTheLearnedKeyPin_AndEndsTheProbation()
        {

            var signer  = ASigner();
            var id      = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var pin     = KeyPin.FromKey(lcKey).ToHex();

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var alone      = await Send(Signed(id, [ ("ServerKeyPins", pin) ], signer));
            var confirmed  = await Send(Signed(id, PinSetOf(pin, 1), signer));

            Assert.Multiple(() => {
                Assert.That(Reasons(alone),              Is.All.EqualTo("InvalidValue"), alone.ToString());
                Assert.That(Infos(alone),                Does.Contain("ServerKeyPins and ServerKeyPinsVersion in one request"));
                Assert.That(Statuses(confirmed),         Is.All.EqualTo("Accepted"), confirmed.ToString());
                Assert.That(Entry.ServerKeyPinState,     Is.EqualTo(KeyPinState.Confirmed));
                Assert.That(Entry.ServerKeyPins,         Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }));
                Assert.That(Entry.ServerKeyPinsVersion,  Is.EqualTo(1));
                Assert.That(Entry.ServerKeyPinLearned,   Is.Null);
            });

            var heartbeat  = await lc!.Ask("TriggerMessage", new JObject(new JProperty("requestedMessage", "Heartbeat")));
            var again      = await Send(Signed(id, PinSetOf(pin, 1), signer));

            Assert.Multiple(() => {
                Assert.That(heartbeat[0].Value<Int32>(), Is.EqualTo(3), $"a request was refused after the probation ended: {heartbeat}");
                Assert.That(Statuses(again),             Is.All.EqualTo("Rejected"), again.ToString());
                Assert.That(Reasons(again),              Is.All.EqualTo("InvalidValue"));
                Assert.That(Infos(again),                Does.Contain("only a higher one is taken"));
                Assert.That(IsConnected(id),             Is.True, "a confirmed connection was dropped");
            });

            await Until(() => Reported("KeyPinConfirmed").Length > 0, "Reporting the key pin confirmed");

        }

        #endregion

        #region AnUnsignedPinSet_AndOneOfAStranger_AreRefusedAsUnsigned()

        /// <summary>
        /// A pin set without a signature, and one signed by a key that is not among the pin set signers,
        /// is refused entry by entry as "Unsigned": the learned key pin stays on probation.
        /// </summary>
        [Test]
        public async Task AnUnsignedPinSet_AndOneOfAStranger_AreRefusedAsUnsigned()
        {

            ASigner();

            var stranger  = ECCKeyPair.GenerateKeys()!;
            var id        = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var pin       = KeyPin.FromKey(lcKey).ToHex();

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var unsigned  = await Send(Signed(id, PinSetOf(pin, 1), null));
            var strange   = await Send(Signed(id, PinSetOf(pin, 1), stranger));
            var reset     = await lc!.Ask("Reset", new JObject(new JProperty("type", "Immediate")));

            Assert.Multiple(() => {
                Assert.That(Statuses(unsigned),        Is.All.EqualTo("Rejected"), unsigned.ToString());
                Assert.That(Reasons (unsigned),        Is.All.EqualTo("Unsigned"));
                Assert.That(Statuses(strange),         Is.All.EqualTo("Rejected"), strange.ToString());
                Assert.That(Reasons (strange),         Is.All.EqualTo("Unsigned"));
                Assert.That(Entry.ServerKeyPins,       Is.Empty, "a pin set was taken unsigned");
                Assert.That(Entry.ServerKeyPinState,   Is.EqualTo(KeyPinState.Learned));
                Assert.That(IsSecurityError(reset),    Is.True, $"the probation ended: {reset}");
            });

        }

        #endregion

        #region APinSetSignedLongAgo_OrAheadOfTheClock_IsTaken()

        /// <summary>
        /// A pin set is taken whenever it was signed: a month before - long past MaxSignatureAge - or a
        /// year ahead of the station's clock. Its version keeps an older one from coming back, and needs no
        /// clock; a station that learns on first use has frequently none it could trust.
        /// </summary>
        [Test]
        public async Task APinSetSignedLongAgo_OrAheadOfTheClock_IsTaken()
        {

            var signer  = ASigner();
            var id      = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var pin     = KeyPin.FromKey(lcKey).ToHex();

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var longAgo  = await Send(Signed(id, PinSetOf(pin, 1), signer, SignedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(30)));
            var ahead    = await Send(Signed(id, PinSetOf(pin, 2), signer, SignedAt: DateTimeOffset.UtcNow + TimeSpan.FromDays(365)));

            Assert.Multiple(() => {
                Assert.That(Statuses(longAgo),          Is.All.EqualTo("Accepted"), longAgo.ToString());
                Assert.That(Statuses(ahead),            Is.All.EqualTo("Accepted"), ahead.ToString());
                Assert.That(Entry.ServerKeyPins,        Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }));
                Assert.That(Entry.ServerKeyPinsVersion, Is.EqualTo(2));
            });

        }

        #endregion

        #region AnOCPPPinSetWithoutTheLearnedKeyPin_IsAnswered_ThenTheImpostorIsDropped()

        /// <summary>
        /// A signed pin set without the learned key pin is answered over the connection to the server that
        /// answered first - Accepted, its pins are taken - and that connection is dropped after the answer,
        /// not before; the server is refused from then on.
        /// </summary>
        [Test]
        public async Task AnOCPPPinSetWithoutTheLearnedKeyPin_IsAnswered_ThenTheImpostorIsDropped()
        {

            // The impostor answers first.
            lc!.Certificate = Carrier(otherKey);

            var signer  = ASigner();
            var id      = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the impostor's key pin", () => SaidOf(id));

            var taken   = await Send(Signed(id, PinSetOf(KeyPin.FromKey(lcKey).ToHex(), 1), signer));

            Assert.That(Statuses(taken), Is.All.EqualTo("Accepted"), taken.ToString());

            await Until(() => Reported("KeyPinRejected").Length > 0, "Reporting the key pin rejected");

            await Until(() => Reported("InvalidCsmsCertificate").Length > 0 && !IsConnected(id),
                        "Refusing the impostor at the next handshake", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(Entry.ServerKeyPins,  Is.EqualTo(new[] { KeyPin.FromKey(lcKey) }));
                Assert.That(lc.Accepted,          Is.EqualTo(1), "the impostor was believed again");
            });

        }

        #endregion

        #region APinSetSignedWithANonce_IsTakenUnderTheMessageIdOfARelay()

        /// <summary>
        /// A Local Controller sends a request on under a message id of its own. A pin set signed with "*"
        /// and a nonce instead of the message id is taken under any; one signed for its message id is not
        /// taken under another.
        /// </summary>
        [Test]
        public async Task APinSetSignedWithANonce_IsTakenUnderTheMessageIdOfARelay()
        {

            var signer   = ASigner();
            var id       = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var pin      = KeyPin.FromKey(lcKey).ToHex();

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var forItsId = await Send(Signed(id, PinSetOf(pin, 1), signer),             Id: Guid.NewGuid().ToString());
            var nonce    = await Send(Signed(id, PinSetOf(pin, 1), signer, Nonce: true), Id: Guid.NewGuid().ToString());

            Assert.Multiple(() => {
                Assert.That(Reasons(forItsId),        Is.All.EqualTo("Unsigned"), forItsId.ToString());
                Assert.That(Infos(forItsId),          Does.Contain("message id"));
                Assert.That(Statuses(nonce),          Is.All.EqualTo("Accepted"), nonce.ToString());
                Assert.That(Entry.ServerKeyPinState,  Is.EqualTo(KeyPinState.Confirmed));
            });

        }

        #endregion

        #region UntilItIsClosed_AConnectionToARejectedServer_IsRefusedEverything()

        /// <summary>
        /// Between a pin set that rejects the learned key pin and the close of the connection to that
        /// server - which waits for the answer to the pin set - the server is refused everything: off
        /// probation, it is not let through the gate for being off it.
        /// </summary>
        [Test]
        public async Task UntilItIsClosed_AConnectionToARejectedServer_IsRefusedEverything()
        {

            // The impostor answers first.
            lc!.Certificate = Carrier(otherKey);

            var id        = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the impostor's key pin", () => SaidOf(id));

            // An answer that is not out yet holds the close back.
            var answered  = new TaskCompletionSource();

            Assert.That(station!.TryTakeSignedServerKeyPins(id, KeyPin.FromKey(lcKey).ToHex(), 1, "The CSMS", out var error, answered.Task), Is.True, error);

            var heartbeat = await lc.Ask("TriggerMessage", new JObject(new JProperty("requestedMessage", "Heartbeat")));

            Assert.Multiple(() => {
                Assert.That(IsSecurityError(heartbeat),  Is.True, $"a request of the rejected server was executed before its connection was closed: {heartbeat}");
                Assert.That(heartbeat[3]?.Value<String>(), Does.Contain("no longer believes"));
                Assert.That(IsConnected(id),             Is.True, "the connection was closed before its answer was out");
            });

            answered.SetResult();

            await Until(() => !IsConnected(id), "Closing the connection once the answer is out", () => SaidOf(id));

        }

        #endregion

        #region ThePinSetNamesTheStationAsItIsKnownOnThatConnection_OrByItsNode_AndNoOtherStation()

        /// <summary>
        /// The signature's OCPPDestination names the station as its CSMS knows it on that connection -
        /// "cs001", the last segment of the URL it dials - or by the id of its OCPP node, which an
        /// incoming request is addressed to; a pin set for another station is refused.
        /// </summary>
        [Test]
        public async Task ThePinSetNamesTheStationAsItIsKnownOnThatConnection_OrByItsNode_AndNoOtherStation()
        {

            var signer   = ASigner();
            var pin      = KeyPin.FromKey(lcKey).ToHex();
            var id       = Added(pin);

            await Start();

            await Until(() => IsConnected(id), "Connecting by key pin", () => SaidOf(id));

            var asKnown  = await Send(Signed(id, PinSetOf(pin, 1), signer, Destination: "cs001"));
            var byNode   = await Send(Signed(id, PinSetOf(pin, 2), signer, Destination: station!.OCPPv2_1Node.Id.ToString()));
            var another  = await Send(Signed(id, PinSetOf(pin, 3), signer, Destination: "cs002"));

            Assert.Multiple(() => {
                Assert.That(Statuses(asKnown),           Is.All.EqualTo("Accepted"), asKnown.ToString());
                Assert.That(Statuses(byNode),            Is.All.EqualTo("Accepted"), byNode.ToString());
                Assert.That(Reasons (another),           Is.All.EqualTo("Unsigned"), another.ToString());
                Assert.That(Infos   (another),           Does.Contain("destination 'cs002'"));
                Assert.That(Entry.ServerKeyPinsVersion,  Is.EqualTo(2));
            });

        }

        #endregion

        #region OnProbation_TLSClientControllerIsRead_ItsOwnInstanceWritten_AndNothingElse()

        /// <summary>
        /// On probation, a CSMS finds the instance of the connection: GetVariables and GetReport of
        /// TLSClientController are let through, and the report names the instance with its learned key pin.
        /// Everything else is still refused: another component, a mix, GetBaseReport, a GetReport of
        /// everything, a write of another instance or of another component beside it.
        /// </summary>
        [Test]
        public async Task OnProbation_TLSClientControllerIsRead_ItsOwnInstanceWritten_AndNothingElse()
        {

            var id   = Added(null, new ServerKeyPinSettings(null, "Key", null));
            var pin  = KeyPin.FromKey(lcKey).ToHex();

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var read          = await lc!.Ask("GetVariables", new JObject(new JProperty("getVariableData", new JArray(
                                                  Read("TLSClientController", id,   "URL"),
                                                  Read("TLSClientController", id,   "LearnedKeyPin"),
                                                  Read("TLSClientController", id,   "ServerKeyPinState"),
                                                  Read("TLSClientController", null, "ClientKeyPin")))));

            var report        = await lc.Ask("GetReport", new JObject(new JProperty("requestId", 7),
                                                                      new JProperty("componentVariable", new JArray(
                                                                          new JObject(new JProperty("component", new JObject(new JProperty("name", "TLSClientController"))))))));

            await Until(() => lc.Requests("NotifyReport").Length > 0, "The report of TLSClientController");

            var reportData    = lc.Requests("NotifyReport").SelectMany(request => request[3]!["reportData"]!).ToArray();

            var other         = await lc.Ask("GetVariables", new JObject(new JProperty("getVariableData", new JArray(
                                                  Read("OCPPCommCtrlr", null, "NetworkConfigurationPriority")))));

            var mixed         = await lc.Ask("GetVariables", new JObject(new JProperty("getVariableData", new JArray(
                                                  Read("TLSClientController", id,   "URL"),
                                                  Read("SecurityCtrlr",       null, "BasicAuthPassword")))));

            var baseReport    = await lc.Ask("GetBaseReport", new JObject(new JProperty("requestId", 8), new JProperty("reportBase", "FullInventory")));
            var everything    = await lc.Ask("GetReport",     new JObject(new JProperty("requestId", 9)));

            var elsewhere     = await Send(Signed("someone-else", PinSetOf(pin, 1), null));

            var besides       = await lc.Ask("SetVariables", new JObject(new JProperty("setVariableData", new JArray(
                                                  new JObject(new JProperty("component",      new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", id))),
                                                              new JProperty("variable",       new JObject(new JProperty("name", "ServerKeyPins"))),
                                                              new JProperty("attributeValue", pin)),
                                                  new JObject(new JProperty("component",      new JObject(new JProperty("name", "OCPPCommCtrlr"))),
                                                              new JProperty("variable",       new JObject(new JProperty("name", "HeartbeatInterval"))),
                                                              new JProperty("attributeValue", "10"))))));

            var reset         = await lc.Ask("Reset", new JObject(new JProperty("type", "Immediate")));

            String? ValueOf(Int32 Index)
                => read[2]?["getVariableResult"]?[Index]?.Value<String>("attributeValue");

            Assert.Multiple(() => {

                Assert.That(read[0].Value<Int32>(), Is.EqualTo(3), $"TLSClientController was not read on probation: {read}");
                Assert.That(read[2]?["getVariableResult"]?.Select(result => result.Value<String>("attributeStatus")), Is.All.EqualTo("Accepted"), read.ToString());
                Assert.That(ValueOf(0),             Is.EqualTo(URL));
                Assert.That(ValueOf(1),             Is.EqualTo(pin));
                Assert.That(ValueOf(2),             Is.EqualTo("Learned"));
                Assert.That(ValueOf(3) ?? "",       Is.Empty, "a station that names no client certificate showed a key of its own");

                Assert.That(report[0].Value<Int32>(), Is.EqualTo(3), $"a report of TLSClientController was refused on probation: {report}");
                Assert.That(reportData.Any(data => data["component"]?.Value<String>("name")     == "TLSClientController" &&
                                                   data["component"]?.Value<String>("instance") == id                    &&
                                                   data["variable"] ?.Value<String>("name")     == "LearnedKeyPin"       &&
                                                   data["variableAttribute"]?[0]?.Value<String>("value") == pin),
                            Is.True, "the report does not name the instance of the connection with its learned key pin");

                Assert.That(IsSecurityError(other),       Is.True, $"another component was read on probation: {other}");
                Assert.That(IsSecurityError(mixed),       Is.True, $"another component was read beside TLSClientController: {mixed}");
                Assert.That(IsSecurityError(baseReport),  Is.True, $"GetBaseReport was let through: {baseReport}");
                Assert.That(IsSecurityError(everything),  Is.True, $"a report of everything was let through: {everything}");
                Assert.That(IsSecurityError(elsewhere),   Is.True, $"a write of another instance was let through: {elsewhere}");
                Assert.That(IsSecurityError(besides),     Is.True, $"a write of another component was let through beside it: {besides}");
                Assert.That(IsSecurityError(reset),       Is.True, $"a Reset was executed on probation: {reset}");

            });

        }

        #endregion

        #region HowItBelievesItsServer_IsWrittenSigned_WithinTheTimeWindow()

        /// <summary>
        /// RawPublicKeys, TrustOnFirstUse and ProbationTime are written by a signed SetVariables, and
        /// reported as ReconfigurationOfSecurityParameters. They carry no version, so their signature is
        /// held to its time window, by the station's clock: one signed a month before is refused as
        /// "OutOfTime"; so is an unsigned one, as "Unsigned". Require is refused: the station cannot offer
        /// raw public keys yet, and would cut itself off.
        /// </summary>
        [Test]
        public async Task HowItBelievesItsServer_IsWrittenSigned_WithinTheTimeWindow()
        {

            var signer    = ASigner();
            var id        = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();

            await Until(() => IsConnected(id), "Connecting by key pin", () => SaidOf(id));

            var written   = await Send(Signed(id, [ ("RawPublicKeys", "Prefer"), ("TrustOnFirstUse", "Key"), ("ProbationTime", "120") ], signer));
            var require   = await Send(Signed(id, [ ("RawPublicKeys", "Require") ], signer));
            var unsigned  = await Send(Signed(id, [ ("ProbationTime", "300") ], null));
            var longAgo   = await Send(Signed(id, [ ("ProbationTime", "300") ], signer, SignedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(30)));

            Assert.Multiple(() => {

                Assert.That(Statuses(written),                Is.All.EqualTo("Accepted"), written.ToString());
                Assert.That(Entry.RawPublicKeys,              Is.EqualTo(RawPublicKeyMode.Prefer));
                Assert.That(Entry.TrustOnFirstUse,            Is.EqualTo(KeyTrustOnFirstUse.Key));
                Assert.That(Entry.ServerKeyPinProbationTime,  Is.EqualTo(TimeSpan.FromSeconds(120)));

                Assert.That(Reasons(require),                 Is.All.EqualTo("NotSupported"), require.ToString());
                Assert.That(Reasons(unsigned),                Is.All.EqualTo("Unsigned"),     unsigned.ToString());
                Assert.That(Reasons(longAgo),                 Is.All.EqualTo("OutOfTime"),    longAgo.ToString());

                Assert.That(Entry.RawPublicKeys,              Is.EqualTo(RawPublicKeyMode.Prefer), "Require was taken");
                Assert.That(Entry.ServerKeyPinProbationTime,  Is.EqualTo(TimeSpan.FromSeconds(120)), "an unsigned or an old change was taken");

                Assert.That(IsConnected(id),                  Is.True, "a change read at the next handshake dropped the connection");
                Assert.That(lc!.Accepted,                     Is.EqualTo(1));

            });

            await Until(() => Reported("ReconfigurationOfSecurityParameters").Any(@event => @event.Value<String>("techInfo")?.Contains("believes its server") == true),
                        "Reporting the change");

        }

        #endregion

        #region TrustOnFirstUseSwitchedOff_OnProbation_ForgetsTheLearnedKeyPin_AndAsksTheCA()

        /// <summary>
        /// A signed TrustOnFirstUse = None on a connection on probation forgets the learned key pin - nothing
        /// learns it any more - and dials the connection again, checked by its CA: the carrier certificate
        /// no CA vouches for is not believed any more.
        /// </summary>
        [Test]
        public async Task TrustOnFirstUseSwitchedOff_OnProbation_ForgetsTheLearnedKeyPin_AndAsksTheCA()
        {

            var signer  = ASigner();
            var id      = Added(null, new ServerKeyPinSettings(null, "Key", null));

            await Start();

            await Until(() => IsConnected(id) && Entry.ServerKeyPinLearned is not null, "Learning the key pin", () => SaidOf(id));

            var off     = await Send(Signed(id, [ ("TrustOnFirstUse", "None") ], signer));

            Assert.Multiple(() => {
                Assert.That(Statuses(off),              Is.All.EqualTo("Accepted"), off.ToString());
                Assert.That(Entry.TrustOnFirstUse,      Is.EqualTo(KeyTrustOnFirstUse.None));
                Assert.That(Entry.ServerKeyPinLearned,  Is.Null, "the learned key pin outlived trust on first use");
                Assert.That(Entry.IsKeyPinned,          Is.False);
            });

            await Until(() => !IsConnected(id), "Dialling it again, checked by its CA", () => SaidOf(id));

            await Task.Delay(TimeSpan.FromSeconds(1));

            Assert.Multiple(() => {
                Assert.That(IsConnected(id),  Is.False, "a certificate no CA vouches for was believed");
                Assert.That(lc!.Accepted,     Is.EqualTo(1), "the server learned before was believed again");
            });

        }

        #endregion

        #region TheInstancesFollowTheEndpoints_AndTheStationsOwnKeyIsReadOnly()

        /// <summary>
        /// TLSClientController has an instance for every wss:// connection, named by its id, and one for
        /// the station's own key: added and removed with the connections, none for ws:// or ssh://.
        /// ClientKeyPin is the key pin of the client certificate a connection names, and neither it nor
        /// ServerKeyPinState is written.
        /// </summary>
        [Test]
        public async Task TheInstancesFollowTheEndpoints_AndTheStationsOwnKeyIsReadOnly()
        {

            var pin  = KeyPin.FromKey(lcKey).ToHex();
            var id   = Added(pin);

            await Start();

            await Until(() => IsConnected(id), "Connecting by key pin", () => SaidOf(id));

            Assert.Multiple(() => {
                Assert.That(Instances.Count(instance => instance.Connection is null), Is.EqualTo(1), "the station's own key has no instance");
                Assert.That(Instances.Single(instance => instance.Connection is not null).Instance, Is.EqualTo(id));
            });

            Assert.That(station!.Connections.TryAddConnection("Another one", "wss://127.0.0.1:1/cs001", "CSMS", false, null, null, out var other, out var error),
                        Is.True, error);
            Assert.That(station.Connections.TryAddConnection("Plain",       "ws://127.0.0.1:1/cs001",  "CSMS", false, null, null, out var plain, out error),
                        Is.True, error);
            Assert.That(station.Connections.TryAddConnection("Over SSH",    "ssh://cs001@127.0.0.1:1", "CSMS", false, null, null, out var ssh, out error,
                                                             SshHostKey: "SHA256:" + Convert.ToBase64String(new Byte[32]).TrimEnd('=')),
                        Is.True, error);

            await Until(() => Instances.Any(instance => instance.Connection == other), "An instance of the new connection");

            Assert.Multiple(() => {
                Assert.That(Instances.Select(instance => instance.Connection), Does.Not.Contain(plain).And.Not.Contain(ssh));
                Assert.That(Instances.Single(instance => instance.Connection == other).VariableConfigs.Single(variable => variable.Name == "URL").Value,
                            Is.EqualTo("wss://127.0.0.1:1/cs001"));
            });

            Assert.That(station.Connections.TryRemoveConnection(other, out error), Is.True, error);

            await Until(() => !Instances.Any(instance => instance.Connection == other), "The instance of the connection removed going with it");

            // A client certificate the connection names: its key is the station's key.
            Assert.That(station.ClientCertificates.TryCreateKey("cs001.example.org", "ecdsa-p256", out var certificateId, out var csr, out error), Is.True, error);
            Assert.That(station.ClientCertificates.TryAddCertificate(ACertificateFor(csr!), out _, out _, out error), Is.True, error);
            Assert.That(station.Connections.TryAddConnection("With a certificate", "wss://127.0.0.1:1/cs001", "CSMS", false, null, certificateId,
                                                             out _, out error),
                        Is.True, error);

            var certificate = station.ClientCertificates.Entries.Single(entry => entry.Id == certificateId).Certificate!;

            var key         = await lc!.Ask("GetVariables", new JObject(new JProperty("getVariableData", new JArray(Read("TLSClientController", null, "ClientKeyPin")))));

            var readOnly    = await lc.Ask("SetVariables", new JObject(new JProperty("setVariableData", new JArray(
                                               new JObject(new JProperty("component",      new JObject(new JProperty("name", "TLSClientController"))),
                                                           new JProperty("variable",       new JObject(new JProperty("name", "ClientKeyPin"))),
                                                           new JProperty("attributeValue", pin)),
                                               new JObject(new JProperty("component",      new JObject(new JProperty("name", "TLSClientController"), new JProperty("instance", id))),
                                                           new JProperty("variable",       new JObject(new JProperty("name", "ServerKeyPinState"))),
                                                           new JProperty("attributeValue", "Confirmed"))))));

            Assert.Multiple(() => {
                Assert.That(key[2]?["getVariableResult"]?[0]?.Value<String>("attributeValue"), Is.EqualTo(KeyPin.FromCertificate(certificate).ToHex()), key.ToString());
                Assert.That(Statuses(readOnly),     Is.All.EqualTo("Rejected"), readOnly.ToString());
                Assert.That(Reasons (readOnly),     Is.All.EqualTo("ReadOnly"));
            });

        }

        /// <summary>
        /// What a certificate authority sends back for the given signing request.
        /// </summary>
        private static String ACertificateFor(String CSR)
        {

            var request  = (new PemReader(new StringReader(CSR)).ReadObject() as Pkcs10CertificationRequest)!;
            var caKeys   = PKIFactory.GenerateECCKeyPair("secp256r1");
            var ca       = PKIFactory.CreateRootCACertificate(RootKeyPair: caKeys, SubjectName: "Test CSMS CA");
            var issued   = PKIFactory.SignClientCertificate(request, caKeys.Private, ca, lifeTime: TimeSpan.FromDays(30));

            return String.Join(Environment.NewLine,
                               System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", issued.GetEncoded()),
                               System.Security.Cryptography.PemEncoding.WriteString("CERTIFICATE", ca.    GetEncoded())) + Environment.NewLine;

        }

        #endregion

        #region TheSSHPeers_AreWrittenByASignedRequest_AndByNoOther()

        private static (JObject Payload, String Id) SignedPeers(IEnumerable<(String? Peer, String Variable, String Value)>  Values,
                                                                ECCKeyPair?                                              Signer,
                                                                DateTimeOffset?                                          SignedAt   = null)
        {

            var id      = Guid.NewGuid().ToString();
            var request = new SetVariablesRequest(
                              SourceRouting.To(NetworkingNode_Id.Parse("cs001")),
                              [.. Values.Select(value => new SetVariableData(new Component("SSHPeerController", value.Peer),
                                                                             new Variable(value.Variable),
                                                                             value.Value))],
                              DataConsistencyModel.ACID,
                              RequestId: Request_Id.Parse(id)
                          );

            if (Signer is not null)
                Assert.That(new SignaturePolicy().
                                BindTo(NetworkingNode_Id.CSMS, protocols.OCPPv2_1.Version.WebSocketSubProtocolId).
                                SignRequestMessage(request, request.ToJSON(true), out var notSigned,
                                                   Signer.ToSignInfo1("CSMS", I18NString.Create("SSH peers"), SignedAt ?? DateTimeOffset.UtcNow)),
                            Is.True, notSigned);

            return (request.ToJSON(false), id);

        }

        /// <summary>
        /// The SSH peers of the station - who a host is believed by, and where
        /// its files may go - are written over OCPP by a request one of its pin
        /// set signers signed, within the time window, and by no other.
        /// </summary>
        [Test]
        public async Task TheSSHPeers_AreWrittenByASignedRequest_AndByNoOther()
        {

            var signer    = ASigner();
            var stranger  = ECCKeyPair.GenerateKeys()!;
            var id        = Added(KeyPin.FromKey(lcKey).ToHex());

            await Start();
            await Until(() => IsConnected(id), "Connecting by key pin", () => SaidOf(id));

            (String? Peer, String Variable, String Value)[] aFileServer = [
                (null,    "Peers",          "files"),
                ("files", "Address",        "logs.example.org"),
                ("files", "HostKeyPins",    "SHA256:" + new String('A', 43)),
                ("files", "FileTransfers",  "Upload"),
                ("files", "UploadPaths",    "/upload/{id}/")
            ];

            var unsigned    = await Send(SignedPeers(aFileServer, null));
            var ofAStranger = await Send(SignedPeers(aFileServer, stranger));
            var longAgo     = await Send(SignedPeers(aFileServer, signer, SignedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(30)));

            Assert.Multiple(() => {
                Assert.That(Statuses(unsigned),      Is.All.EqualTo("Rejected"),  unsigned.   ToString());
                Assert.That(Reasons (unsigned),      Is.All.EqualTo("Unsigned"),  unsigned.   ToString());
                Assert.That(Reasons (ofAStranger),   Is.All.EqualTo("Unsigned"),  ofAStranger.ToString());
                Assert.That(Statuses(longAgo),       Is.All.EqualTo("Rejected"),  longAgo.    ToString());
                Assert.That(station!.ConfiguredSSHPeers, Is.Empty, "an unsigned, a stranger's or an old request made a peer");
            });

            var signed = await Send(SignedPeers(aFileServer, signer));

            Assert.Multiple(() => {
                Assert.That(Statuses(signed),                                 Is.All.EqualTo("Accepted"), signed.ToString());
                Assert.That(station!.ConfiguredSSHPeers.Select(peer => peer.Id),   Is.EqualTo(new[] { "files" }));
                Assert.That(station.FileServers.Select(server => server.Host),     Is.EqualTo(new[] { "logs.example.org" }));
            });

            // What is there is changed the same way: its pins, and what it may be used for.
            var pinsUnsigned  = await Send(SignedPeers([ ("files", "HostKeyPins",   "SHA256:" + new String('B', 43)) ], null));
            var usesUnsigned  = await Send(SignedPeers([ ("files", "FileTransfers", "Upload,Download") ],               null));
            var usesSigned    = await Send(SignedPeers([ ("files", "FileTransfers", "Upload,Download") ],               signer));

            Assert.Multiple(() => {
                Assert.That(Reasons (pinsUnsigned),  Is.All.EqualTo("Unsigned"),  pinsUnsigned.ToString());
                Assert.That(Reasons (usesUnsigned),  Is.All.EqualTo("Unsigned"),  usesUnsigned.ToString());
                Assert.That(Statuses(usesSigned),    Is.All.EqualTo("Accepted"),  usesSigned.  ToString());
                Assert.That(station!.ConfiguredSSHPeers[0].Trust.Pins,        Is.EqualTo(new[] { "SHA256:" + new String('A', 43) }), "an unsigned request changed the pins of a peer");
                Assert.That(station.FileServers[0].FileTransfers.Allows(cloud.charging.open.protocols.WWCP.Node.SecureShell.FileTransferDirections.Download), Is.True);
                Assert.That(IsConnected(id),                                  Is.True);
            });

        }

        #endregion

    }

}
