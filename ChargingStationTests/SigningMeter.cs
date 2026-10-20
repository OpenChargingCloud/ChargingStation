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

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using org.GraphDefined.Vanaheimr.Hermod.SunSpecModbusTLS.Common;

using cloud.charging.open.ChargingStation.Meters;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// A meter of integers with the session model behind its meter model, as
    /// OpenChargingCloud's Modbus/TLS meter has it: the parameters of a command
    /// each connection's own, a session begun and ended by the command
    /// register, and the document it ends in signed - with the key of
    /// ChargyCore's OCMF fixtures, whose private half is 1.
    /// </summary>
    internal sealed class SigningMeter : IModbusBackendFactory
    {

        #region Data

        /// <summary>
        /// The public key of ChargyCore's OCMF fixtures, as a SubjectPublicKeyInfo:
        /// the generator of P-256, so that its private key is 1.
        /// </summary>
        public const String PublicKey = "3059301306072A8648CE3D020106082A8648CE3D030107034200046B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C2964FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5";

        private readonly Lock  sync = new ();

        #endregion

        #region Properties

        /// <summary>The registers that are not the session model's.</summary>
        public SunSpecImage  Image            { get; }

        /// <summary>Where the session model's points begin.</summary>
        public Int32         At               { get; private set; }

        /// <summary>
        /// What a role that may write what is commanded may write: the command,
        /// and what it is given.
        /// </summary>
        public IReadOnlySet<UInt16>  Commanded
            => Enumerable.Range(At + MeterSessionModel.Command, 1).
                   Concat(Enumerable.Range(At + MeterSessionModel.SessionId, MeterSessionModel.CurrentSessionId - MeterSessionModel.SessionId)).
                   Select(address => (UInt16) address).
                   ToHashSet();

        /// <summary>What each start was given, in order: the points by name.</summary>
        public List<Dictionary<String, String?>>  Starts  { get; } = [];

        /// <summary>How many stops it was asked for.</summary>
        public Int32         Stops            { get; private set; }

        /// <summary>The session it measures, by the id it was begun with; null for none.</summary>
        public String?       Running          { get; set; }

        /// <summary>When the session it measures began.</summary>
        public DateTimeOffset  RunningSince   { get; set; } = DateTimeOffset.UtcNow.AddHours(-1);

        /// <summary>The session id of the running or the last stopped session.</summary>
        public String?       Current          { get; private set; }

        /// <summary>The document of the last stopped session.</summary>
        public String?       Document         { get; private set; }

        /// <summary>What the counter stands at when a session begins, and what a session adds, in kWh.</summary>
        public Decimal       Counter          { get; set; } = 100.0m;
        public Decimal       PerSession       { get; set; } = 12.5m;

        /// <summary>Hands out a document whose signature is not of its payload.</summary>
        public Boolean       Tampers          { get; set; }

        #endregion

        #region Constructor(s)

        public SigningMeter()
        {

            Image = new SunSpecImage().
                        Common("Signer", "SM-1", "1.0", "SNSIGN").
                        Model(203, 105, (image, at) => {
                            image.Write(at, 480, 160, 160, 160, unchecked((UInt16) (-1)));
                            image.Write(at + 16, 1104, 368, 368, 368, 1);
                        }).
                        Model(MeterSessionModel.Id, MeterSessionModel.Length, (image, at) => At = at).
                        End();

        }

        #endregion


        #region IModbusBackendFactory

        public Task<IModbusBackend> CreateAsync(Int64 ConnectionId, String? Role, CancellationToken CancellationToken)

            => Task.FromResult<IModbusBackend>(new Connection(this));

        #endregion


        #region (private) Connection

        /// <summary>
        /// One connection: what it wrote for its next command, and why its last
        /// was refused.
        /// </summary>
        private sealed class Connection(SigningMeter Meter) : IModbusBackend
        {

            private readonly UInt16[]  given      = new UInt16[MeterSessionModel.Length];
            private          UInt16    error;
            private          String    errorText  = "";

            public async Task<Byte[]> ProcessRequestAsync(Byte UnitId, ReadOnlyMemory<Byte> RequestPDU, CancellationToken CancellationToken)
            {

                var pdu      = RequestPDU.ToArray();
                var function = (ModbusFunctionCodes) pdu[0];
                var first    = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));

                switch (function)
                {

                    case ModbusFunctionCodes.ReadHoldingRegisters:
                        {

                            var count  = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                            var values = new UInt16[count];

                            for (var i = 0; i < count; i++)
                            {

                                var point = first + i - Meter.At;

                                if (point >= 0 && point < MeterSessionModel.Length)
                                    values[i] = At(point);

                                else
                                {
                                    try
                                    {
                                        values[i] = (await Meter.Image.Read((UInt16) (first + i), 1))[0];
                                    }
                                    catch (ModbusException e)
                                    {
                                        return ModbusPDU.BuildException(function, e.Code);
                                    }
                                }

                            }

                            return ModbusPDU.BuildReadHoldingResponse(values);

                        }

                    case ModbusFunctionCodes.WriteSingleRegister:
                        {

                            var value = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));

                            return Write(function, first, [ value ]) ?? ModbusPDU.BuildWriteSingleRegisterResponse(first, value);

                        }

                    case ModbusFunctionCodes.WriteMultipleRegisters:
                        {

                            var count  = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                            var values = new UInt16[count];

                            for (var i = 0; i < count; i++)
                                values[i] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + 2 * i, 2));

                            return Write(function, first, values) ?? ModbusPDU.BuildWriteMultipleRegistersResponse(first, count);

                        }

                    default:
                        return ModbusPDU.BuildException(function, ModbusExceptionCode.IllegalFunction);

                }

            }

            private Byte[]? Write(ModbusFunctionCodes Function, UInt16 First, UInt16[] Values)
            {

                UInt16? command = null;

                for (var i = 0; i < Values.Length; i++)
                {

                    var point = First + i - Meter.At;

                    if (point == MeterSessionModel.Command)
                        command = Values[i];

                    else if (point >= MeterSessionModel.SessionId && point < MeterSessionModel.CurrentSessionId)
                        given[point] = Values[i];

                    else
                        return ModbusPDU.BuildException(Function, ModbusExceptionCode.IllegalDataAddress);

                }

                if (command is null)
                    return null;

                (error, errorText) = Meter.Do(command.Value, Text(MeterSessionModel.SessionId,           MeterSessionModel.SessionIdLength),
                                                            Text(MeterSessionModel.IdentificationType,  MeterSessionModel.IdentificationTypeLength),
                                                            Text(MeterSessionModel.Identification,      MeterSessionModel.IdentificationLength),
                                                            Text(MeterSessionModel.ChargePointIdType,   MeterSessionModel.ChargePointIdTypeLength),
                                                            Text(MeterSessionModel.ChargePointId,       MeterSessionModel.ChargePointIdLength),
                                                            Text(MeterSessionModel.TariffText,          MeterSessionModel.TariffTextLength));

                Array.Clear(given);

                return error == 0
                           ? null
                           : ModbusPDU.BuildException(Function, ModbusExceptionCode.IllegalDataValue);

            }

            private String? Text(UInt16 Point, UInt16 Length)
            {
                var text = MeterSessionModel.Decode(given[Point..(Point + Length)]);
                return String.IsNullOrEmpty(text) ? null : text;
            }

            private UInt16 At(Int32 Point)
            {

                lock (Meter.sync)
                {

                    if (Point == MeterSessionModel.State)
                        return Meter.Running is not null ? MeterSessionModel.StateRunning
                                   : Meter.Document is not null ? MeterSessionModel.StateStopped
                                                                : MeterSessionModel.StateNone;

                    if (Point == MeterSessionModel.Error)
                        return error;

                    if (Point >= MeterSessionModel.ErrorText && Point < MeterSessionModel.ErrorText + MeterSessionModel.ErrorTextLength)
                        return MeterSessionModel.Encode(errorText, MeterSessionModel.ErrorTextLength)[Point - MeterSessionModel.ErrorText];

                    if (Point >= MeterSessionModel.CurrentSessionId && Point < MeterSessionModel.CurrentSessionId + MeterSessionModel.SessionIdLength)
                        return MeterSessionModel.Encode(Meter.Current, MeterSessionModel.SessionIdLength)[Point - MeterSessionModel.CurrentSessionId];

                    var document = Meter.Running is null ? Meter.Document : null;
                    var key      = document is not null ? PublicKey : null;

                    if (Point == MeterSessionModel.PublicKeyLength)
                        return (UInt16) (key?.Length ?? 0);

                    if (Point >= MeterSessionModel.PublicKey && Point < MeterSessionModel.PublicKey + MeterSessionModel.PublicKeyRegisters)
                        return MeterSessionModel.Encode(key, MeterSessionModel.PublicKeyRegisters)[Point - MeterSessionModel.PublicKey];

                    if (Point == MeterSessionModel.DocumentLength)
                        return (UInt16) MeterSessionModel.BytesOf(document);

                    if (Point >= MeterSessionModel.Document)
                        return MeterSessionModel.Encode(document, MeterSessionModel.DocumentRegisters)[Point - MeterSessionModel.Document];

                    return Point is >= MeterSessionModel.SessionId and < MeterSessionModel.CurrentSessionId
                               ? given[Point]
                               : (UInt16) 0;

                }

            }

            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

        }

        #endregion

        #region (private) Do(Command, SessionId, IT, ID, CT, CI, TT)

        private (UInt16 Error, String Why) Do(UInt16   Command,
                                              String?  SessionId,
                                              String?  IdentificationType,
                                              String?  Identification,
                                              String?  ChargePointIdType,
                                              String?  ChargePointId,
                                              String?  TariffText)
        {

            lock (sync)
            {

                if (Command == MeterSessionModel.Start)
                {

                    if (Running is not null)
                        return (1, $"A charging session is already running here ('{Running}').");

                    Starts.Add(new Dictionary<String, String?> {
                        ["sessionId"]           = SessionId,
                        ["identificationType"]  = IdentificationType,
                        ["identification"]      = Identification,
                        ["chargePointIdType"]   = ChargePointIdType,
                        ["chargePointId"]       = ChargePointId,
                        ["tariffText"]          = TariffText
                    });

                    Running       = SessionId ?? "";
                    RunningSince  = DateTimeOffset.UtcNow;
                    Current       = SessionId;
                    Document = null;

                    return (0, "");

                }

                if (Command == MeterSessionModel.Stop)
                {

                    Stops++;

                    if (Running is null)
                        return (2, "No charging session is running here, so there is nothing to stop.");

                    if (SessionId is not null && SessionId != Running)
                        return (3, $"The session running here is '{Running}', not '{SessionId}'. It is still running.");

                    var parameters = Starts.LastOrDefault(start => start["sessionId"] == Running) ?? [];

                    Current  = Running;
                    Running  = null;
                    Document = Signed(parameters, RunningSince, DateTimeOffset.UtcNow, Counter, Counter + PerSession, Tampers);
                    Counter += PerSession;

                    return (0, "");

                }

                return (9, $"{Command} is no command.");

            }

        }

        #endregion

        #region (static) Signed(Parameters, From, To, Tampered)

        /// <summary>
        /// An OCMF document of a session, signed with the fixtures' key.
        /// </summary>
        public static String Signed(IReadOnlyDictionary<String, String?>  Parameters,
                                    DateTimeOffset                        Began,
                                    DateTimeOffset                        Ended,
                                    Decimal                               From,
                                    Decimal                               To,
                                    Boolean                               Tampered = false)
        {

            var payload = new JObject(
                              new JProperty("FV",  Parameters.GetValueOrDefault("tariffText") is not null ? "1.1" : "1.0"),
                              new JProperty("GI",  "SigningMeter"),
                              new JProperty("GS",  "SNSIGN"),
                              new JProperty("GV",  "1.0"),
                              new JProperty("PG",  "T1"),
                              new JProperty("MV",  "Signer"),
                              new JProperty("MM",  "SM-1"),
                              new JProperty("MS",  "SNSIGN"),
                              new JProperty("MF",  "1.0")
                          );

            if (Parameters.GetValueOrDefault("identification") is String identification)
            {
                payload["IS"] = true;
                payload["IT"] = Parameters.GetValueOrDefault("identificationType") ?? "UNDEFINED";
                payload["ID"] = identification;
            }
            else
                payload["IS"] = false;

            if (Parameters.GetValueOrDefault("tariffText") is String tariff)
                payload["TT"] = tariff;

            if (Parameters.GetValueOrDefault("chargePointId") is String chargePoint)
            {
                payload["CT"] = Parameters.GetValueOrDefault("chargePointIdType") ?? "EVSEID";
                payload["CI"] = chargePoint;
            }

            payload["RD"] = new JArray(
                                Reading(Began, "B", From),
                                Reading(Ended, "E", To)
                            );

            var text = payload.ToString(Formatting.None);

            using var key = ECDsa.Create(new ECParameters {
                                Curve = ECCurve.NamedCurves.nistP256,
                                D     = [ .. new Byte[31], 1 ],
                                Q     = new ECPoint {
                                            X = Convert.FromHexString(PublicKey[^128..^64]),
                                            Y = Convert.FromHexString(PublicKey[^64..])
                                        }
                            });

            var signature = key.SignData(Encoding.UTF8.GetBytes(Tampered ? text.Replace("\"T1\"", "\"T2\"") : text),
                                         HashAlgorithmName.SHA256,
                                         DSASignatureFormat.Rfc3279DerSequence);

            var signatureJSON = new JObject(
                                    new JProperty("SA",  "ECDSA-secp256r1-SHA256"),
                                    new JProperty("SE",  "hex"),
                                    new JProperty("SM",  "application/x-der"),
                                    new JProperty("SD",  Convert.ToHexString(signature))
                                );

            return $"OCMF|{text}|{signatureJSON.ToString(Formatting.None)}";

        }

        private static JObject Reading(DateTimeOffset Timestamp, String Transaction, Decimal Value)

            => new (
                   new JProperty("TM",  Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss','fff", System.Globalization.CultureInfo.InvariantCulture) + "+0000 S"),
                   new JProperty("TX",  Transaction),
                   new JProperty("RV",  Value),
                   new JProperty("RI",  "01-00:01.08.00*FF"),
                   new JProperty("RU",  "kWh"),
                   new JProperty("RT",  "AC"),
                   new JProperty("EF",  ""),
                   new JProperty("ST",  "G")
               );

        #endregion

    }

}
