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

using System.Net;
using System.Diagnostics.CodeAnalysis;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.ISO15118.T1S;
using cloud.charging.open.protocols.ISO15118.T1S.Monitoring;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

using cloud.charging.open.ChargingStation.ISO15118;

#endregion

namespace cloud.charging.open.ChargingStation.Configuration
{

    /// <summary>
    /// The "v2g" section of the configuration file: what this charging station
    /// offers a vehicle on the wire below the charging cable.
    /// </summary>
    /// <remarks>
    /// As in the DNS and NTS sections, null means "the file does not say", and
    /// what is missing keeps whatever the station was given at construction -
    /// which for this section is whatever the command line asked for.
    ///
    /// Two things are deliberately not here. The V2G server certificate is a
    /// file and a password and belongs where the other certificates are, not
    /// in a settings document; without one the endpoint speaks plain TCP and
    /// says so out loud. And the simulated SLAC medium over UDP - its endpoint
    /// and its peers - stays on the command line, because it exists for a
    /// bench that has no powerline modem, and a station configured from a file
    /// is not that bench.
    /// </remarks>
    /// <param name="Enabled">Whether anything at all comes up below the cable.</param>
    /// <param name="InterfaceName">The powerline interface, or null to let the station pick one.</param>
    /// <param name="V2GPort">The TCP port of the V2G endpoint; 0 lets the system pick one, which is what SDP then advertises.</param>
    /// <param name="SDP">Whether the SECC Discovery Protocol answers vehicles looking for that endpoint.</param>
    /// <param name="Loopback">Whether SDP also answers a vehicle running on this same machine - for a bench, and off in the field.</param>
    /// <param name="SlacTransport">Which medium the SLAC listener listens on.</param>
    /// <param name="EVSEId">The EVSE identification SLAC hands to a vehicle.</param>
    /// <param name="T1STransport">Which medium the 10BASE-T1S bus of a megawatt coupler is on: "none", "auto", "afpacket" or "udp". "none" is how a station stops coordinating one.</param>
    /// <param name="T1SBus">The multicast <c>group:port</c> of the emulated medium. Naming one and no transport means "udp", because a group is a thing only that medium has.</param>
    /// <param name="T1SInterface">The adapter, for AF_PACKET - the V2G interface when this says nothing; the interface to join the group on, for UDP.</param>
    /// <param name="T1SName">What the station calls itself as the bus coordinator.</param>
    /// <param name="T1SCycle">The pause between cycles, which is how often every node on the bus is asked.</param>
    /// <param name="T1SWarning_C">The temperature at which a pin of the coupler is called warm.</param>
    /// <param name="T1SOverload_C">The temperature at which it is called overloaded. Given together with the warning, never alone.</param>
    public sealed record V2GConfiguration(Boolean?            Enabled         = null,
                                          String?             InterfaceName   = null,
                                          UInt16?             V2GPort         = null,
                                          Boolean?            SDP             = null,
                                          Boolean?            Loopback        = null,
                                          SlacTransportKind?  SlacTransport   = null,
                                          String?             EVSEId          = null,
                                          T1STransportKind?   T1STransport    = null,
                                          String?             T1SBus          = null,
                                          String?             T1SInterface    = null,
                                          String?             T1SName         = null,
                                          TimeSpan?           T1SCycle        = null,
                                          Double?             T1SWarning_C    = null,
                                          Double?             T1SOverload_C   = null)
    {

        #region Data

        /// <summary>
        /// The name of this section in the configuration file.
        /// </summary>
        public const String  SectionName             = "v2g";

        /// <summary>
        /// The longest a network interface name may be written. Linux stops at
        /// 16, Windows hands out names that are a GUID with decoration around
        /// them, and this is comfortably longer than either.
        /// </summary>
        public const Int32   MaxInterfaceNameLength  = 128;

        /// <summary>
        /// The longest an EVSE identification may be: HomePlug carries exactly
        /// this many bytes of it.
        /// </summary>
        public const Int32   MaxEVSEIdLength         = V2GLink.EVSEIdSize;

        /// <summary>
        /// The longest the bus and the coordinator's name may be written.
        /// </summary>
        public const Int32   MaxT1SNameLength        = T1SConstants.MaxNameLength;

        /// <summary>
        /// The shortest and longest a cycle may be, in milliseconds. Below the
        /// first the bus is a busy loop over a socket; above the second a pin
        /// could pass its limit and go unnoticed for half a minute.
        /// </summary>
        public const Double  MinT1SCycleMs           = 50;
        public const Double  MaxT1SCycleMs           = 10000;

        /// <summary>
        /// The range a thermal limit may be written in. A coupler colder than
        /// this is in a freezer and one hotter is on fire; both are typing
        /// mistakes rather than benches.
        /// </summary>
        public const Double  MinT1STemperature_C     = -50;
        public const Double  MaxT1STemperature_C     = 300;

        #endregion


        #region (static) TryParse(JSON, out Configuration, out Error)

        /// <summary>
        /// The "v2g" section, or the one sentence that says what is wrong with it.
        /// </summary>
        public static Boolean TryParse(JObject                                     JSON,
                                       [NotNullWhen(true)]  out V2GConfiguration?  Configuration,
                                       [NotNullWhen(false)] out String?            Error)
        {

            Configuration  = null;
            Error          = null;

            if (!ConfigurationReader.TryReadBoolean(JSON, "enabled",    SectionName, out var enabled,       out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "sdp",        SectionName, out var sdp,           out Error) ||
                !ConfigurationReader.TryReadBoolean(JSON, "loopback",   SectionName, out var loopback,      out Error) ||
                !ConfigurationReader.TryReadString (JSON, "interface",  SectionName, MaxInterfaceNameLength, out var interfaceName, out Error) ||
                !TryReadV2GPort                    (JSON, "port",       SectionName, out var port,          out Error) ||
                !TryReadSlacTransport              (JSON, "slac",       SectionName, out var transport,     out Error) ||
                !ConfigurationReader.TryReadString (JSON, "evseId",       SectionName, MaxEVSEIdLength,       out var evseId,        out Error) ||
                !TryReadT1STransport               (JSON, "t1sTransport", SectionName,                        out var t1sTransport,  out Error) ||
                !ConfigurationReader.TryReadString (JSON, "t1sBus",       SectionName, MaxT1SNameLength,      out var t1sBus,        out Error) ||
                !ConfigurationReader.TryReadString (JSON, "t1sInterface", SectionName, MaxInterfaceNameLength, out var t1sInterface, out Error) ||
                !ConfigurationReader.TryReadString (JSON, "t1sName",      SectionName, MaxT1SNameLength,      out var t1sName,       out Error) ||
                !TryReadNumber                     (JSON, "t1sCycleMs",   SectionName, MinT1SCycleMs,       MaxT1SCycleMs,       out var t1sCycleMs,  out Error) ||
                !TryReadNumber                     (JSON, "t1sWarningC",  SectionName, MinT1STemperature_C, MaxT1STemperature_C, out var t1sWarning,  out Error) ||
                !TryReadNumber                     (JSON, "t1sOverloadC", SectionName, MinT1STemperature_C, MaxT1STemperature_C, out var t1sOverload, out Error))
            {
                return false;
            }

            #region The bus, refused here rather than at the socket

            // The emulated medium is IPv4 multicast and nothing else, so a bus
            // that is not a group is refused where the message can name the
            // field, rather than by a station that comes up coordinating
            // nothing and says so in a log nobody is reading yet.
            if (t1sBus is not null && !T1SConstants.TryParseBus(t1sBus, out _))
            {
                Error = $"'{SectionName}.t1sBus' must be an IPv4 multicast group and port, like {T1SConstants.DefaultMulticastEndpoint}.";
                return false;
            }

            // Half a pair is refused rather than merged, because the two make
            // sense only against each other: a warning above the overload is a
            // station that calls a pin warm after it has already stopped
            // charging over it, and nothing in a merge could catch that.
            if (t1sWarning.HasValue != t1sOverload.HasValue)
            {
                Error = $"'{SectionName}.t1sWarningC' and '{SectionName}.t1sOverloadC' belong together; give both or neither.";
                return false;
            }

            if (t1sWarning.HasValue && t1sOverload.HasValue && t1sWarning.Value >= t1sOverload.Value)
            {
                Error = $"'{SectionName}.t1sWarningC' ({t1sWarning.Value:F1} °C) must be below " +
                        $"'{SectionName}.t1sOverloadC' ({t1sOverload.Value:F1} °C).";
                return false;
            }

            #endregion

            Configuration = new V2GConfiguration(
                                enabled,
                                interfaceName,
                                port,
                                sdp,
                                loopback,
                                transport,
                                evseId,
                                t1sTransport,
                                t1sBus,
                                t1sInterface,
                                t1sName,
                                t1sCycleMs.HasValue ? TimeSpan.FromMilliseconds(t1sCycleMs.Value) : null,
                                t1sWarning,
                                t1sOverload
                            );

            return true;

        }

        #endregion

        #region (static) TryReadV2GPort(JSON, Name, Path, out Port, out Error)

        /// <summary>
        /// The port of the V2G endpoint, where zero is a real answer.
        /// </summary>
        /// <remarks>
        /// ConfigurationReader.TryReadPort refuses zero on purpose, because
        /// every other port in this file belongs to a client and zero means
        /// nothing to one. This port belongs to a listener, where zero is how
        /// "any free one" is spelled - and SDP exists precisely so that a
        /// vehicle can still find it afterwards.
        /// </remarks>
        public static Boolean TryReadV2GPort(JObject                           JSON,
                                             String                            Name,
                                             String?                           Path,
                                             out UInt16?                       Port,
                                             [NotNullWhen(false)] out String?  Error)
        {

            Port   = null;
            Error  = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path is null ? Name : $"{Path}.{Name}";

            if (token.Type != JTokenType.Integer)
            {
                Error = $"'{where}' must be a port number, or 0 to let the system pick one.";
                return false;
            }

            var number = token.Value<Int64>();

            if (number < 0 || number > UInt16.MaxValue)
            {
                Error = $"'{where}' must be between 0 and {UInt16.MaxValue}.";
                return false;
            }

            Port = (UInt16) number;
            return true;

        }

        #endregion

        #region (static) TryReadT1STransport(JSON, Name, Path, out Transport, out Error)

        /// <summary>
        /// Which medium the bus below a megawatt coupler is on, written the
        /// way somebody would write it.
        /// </summary>
        public static Boolean TryReadT1STransport(JObject                           JSON,
                                                  String                            Name,
                                                  String?                           Path,
                                                  out T1STransportKind?             Transport,
                                                  [NotNullWhen(false)] out String?  Error)
        {

            Transport  = null;
            Error      = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where   = Path is null ? Name : $"{Path}.{Name}";
            var choices = String.Join(", ", T1STransportKinds.Words.Select(word => $"\"{word}\""));

            if (token.Type != JTokenType.String)
            {
                Error = $"'{where}' must be one of {choices}.";
                return false;
            }

            var text = token.Value<String>()?.Trim() ?? "";

            if (text.Length == 0)
                return true;

            if (!T1STransportKinds.TryParse(text, out var parsed))
            {
                Error = $"'{where}' is \"{text}\", which is not one of {choices}.";
                return false;
            }

            Transport = parsed;
            return true;

        }

        #endregion

        #region (static) TryReadNumber(JSON, Name, Path, Minimum, Maximum, out Value, out Error)

        /// <summary>
        /// An optional number within a range, refused by name where it is not
        /// one.
        /// </summary>
        public static Boolean TryReadNumber(JObject                           JSON,
                                            String                            Name,
                                            String?                           Path,
                                            Double                            Minimum,
                                            Double                            Maximum,
                                            out Double?                       Value,
                                            [NotNullWhen(false)] out String?  Error)
        {

            Value  = null;
            Error  = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path is null ? Name : $"{Path}.{Name}";

            if (token.Type != JTokenType.Integer && token.Type != JTokenType.Float)
            {
                Error = $"'{where}' must be a number.";
                return false;
            }

            var number = token.Value<Double>();

            if (Double.IsNaN(number) || number < Minimum || number > Maximum)
            {
                Error = $"'{where}' must be between {Minimum:0.###} and {Maximum:0.###}.";
                return false;
            }

            Value = number;
            return true;

        }

        #endregion

        #region (static) TryReadSlacTransport(JSON, Name, Path, out Transport, out Error)

        /// <summary>
        /// Which medium the SLAC listener listens on, written the way somebody
        /// would write it.
        /// </summary>
        /// <remarks>
        /// Matched without regard to case, and the message lists the choices,
        /// because "must be a valid SlacTransportKind" over a settings file
        /// has told nobody anything.
        /// </remarks>
        public static Boolean TryReadSlacTransport(JObject                           JSON,
                                                   String                            Name,
                                                   String?                           Path,
                                                   out SlacTransportKind?            Transport,
                                                   [NotNullWhen(false)] out String?  Error)
        {

            Transport  = null;
            Error      = null;

            var token = JSON[Name];

            if (token is null || token.Type == JTokenType.Null)
                return true;

            var where = Path is null ? Name : $"{Path}.{Name}";

            if (token.Type != JTokenType.String)
            {
                Error = $"'{where}' must be one of {Choices()}.";
                return false;
            }

            var text = token.Value<String>()?.Trim() ?? "";

            if (text.Length == 0)
                return true;

            if (!Enum.TryParse<SlacTransportKind>(text, ignoreCase: true, out var parsed) ||
                !Enum.IsDefined(parsed))
            {
                Error = $"'{where}' is \"{text}\", which is not one of {Choices()}.";
                return false;
            }

            Transport = parsed;
            return true;

        }

        #region (private static) Choices()

        /// <summary>
        /// The transports, listed the way a sentence lists things.
        /// </summary>
        private static String Choices()

            => String.Join(", ", Enum.GetNames<SlacTransportKind>().
                                      Select(name => $"\"{name.ToLowerInvariant()}\""));

        #endregion

        #endregion

        #region ToJSON()

        /// <summary>
        /// The section as it is written to the file; what this station was not
        /// told about is not written.
        /// </summary>
        public JObject ToJSON()
        {

            var json = new JObject();

            if (Enabled.HasValue)           json.Add("enabled",    Enabled.Value);
            if (InterfaceName is not null)  json.Add("interface",  InterfaceName);
            if (V2GPort.HasValue)           json.Add("port",       V2GPort.Value);
            if (SDP.HasValue)               json.Add("sdp",        SDP.Value);
            if (Loopback.HasValue)          json.Add("loopback",   Loopback.Value);
            if (SlacTransport.HasValue)     json.Add("slac",       SlacTransport.Value.ToString().ToLowerInvariant());
            if (EVSEId is not null)         json.Add("evseId",     EVSEId);

            if (T1STransport.HasValue)      json.Add("t1sTransport", T1STransport.Value.Write());
            if (T1SBus       is not null)   json.Add("t1sBus",       T1SBus);
            if (T1SInterface is not null)   json.Add("t1sInterface", T1SInterface);
            if (T1SName      is not null)   json.Add("t1sName",      T1SName);
            if (T1SCycle.HasValue)          json.Add("t1sCycleMs",   Math.Round(T1SCycle.Value.TotalMilliseconds));
            if (T1SWarning_C.HasValue)      json.Add("t1sWarningC",  T1SWarning_C.Value);
            if (T1SOverload_C.HasValue)     json.Add("t1sOverloadC", T1SOverload_C.Value);

            return json;

        }

        #endregion

        #region Apply(Options)

        /// <summary>
        /// This section laid on top of the options a station already has: what
        /// the file says wins, and what it does not mention is left alone.
        /// </summary>
        public V2GOptions Apply(V2GOptions Options)
            => Options with {
                   Enabled        = Enabled        ?? Options.Enabled,
                   InterfaceName  = InterfaceName  ?? Options.InterfaceName,
                   V2GPort        = V2GPort        ?? Options.V2GPort,
                   SDP                = SDP       ?? Options.SDP,
                   MulticastLoopback  = Loopback  ?? Options.MulticastLoopback,
                   SlacTransport  = SlacTransport  ?? Options.SlacTransport,
                   EVSEId         = EVSEId         ?? Options.EVSEId,
                   T1S            = ApplyT1S(Options.T1S)
               };

        #endregion

        #region (private) ApplyT1S(Current)

        /// <summary>
        /// The bus below a megawatt coupler, as this section leaves it.
        /// </summary>
        /// <remarks>
        /// Four rules, and they are the same four the vehicle follows. A
        /// section that says nothing about the bus leaves it alone. "none" is
        /// how a bus is taken away, and the only way - a station that stopped
        /// coordinating because somebody renamed it would be a surprise
        /// nobody asked for. A group named without a transport means the
        /// emulated medium, because a group is a thing only that medium has.
        /// And everything else is laid over what the station already had.
        /// </remarks>
        private T1SOptions? ApplyT1S(T1SOptions? Current)
        {

            if (T1STransport is null && T1SBus is null && T1SInterface is null && T1SName is null &&
                !T1SCycle.HasValue && !T1SWarning_C.HasValue && !T1SOverload_C.HasValue)
                return Current;

            if (T1STransport == T1STransportKind.None)
                return null;

            var transport = T1STransport
                                ?? (Current is { Transport: not T1STransportKind.None } ? Current.Transport : (T1STransportKind?) null)
                                ?? (T1SBus is not null ? T1STransportKind.UDP : (T1STransportKind?) null);

            // Somebody named a cycle or a limit for a bus that does not exist
            // and did not say which medium it would be on. Nothing to
            // coordinate, so nothing changes.
            if (transport is null)
                return Current;

            var thermal = Current?.Thermal;

            if (T1SWarning_C.HasValue && T1SOverload_C.HasValue)
                thermal = (thermal ?? CableThermalMonitorOptions.Default) with {
                              Warning_C   = T1SWarning_C. Value,
                              Overload_C  = T1SOverload_C.Value
                          };

            return new T1SOptions(
                       Transport:      transport.Value,
                       InterfaceName:  T1SInterface ?? Current?.InterfaceName,
                       Group:          T1SBusEndpoint ?? Current?.Group,
                       Name:           T1SName ?? Current?.Name ?? "EVSE",
                       Thermal:        thermal,
                       CycleGap:       T1SCycle ?? Current?.CycleGap
                   );

        }

        #endregion

        #region T1SBusEndpoint

        /// <summary>
        /// The bus as a group and a port, or null where none is named. What
        /// was named has been checked, so this never fails on a saved file.
        /// </summary>
        public IPEndPoint? T1SBusEndpoint
            => T1SConstants.TryParseBus(T1SBus, out var bus)
                   ? bus
                   : null;

        #endregion

        #region (override) ToString()

        public override String ToString()

            => Enabled == false
                   ? "V2G off"
                   : String.Join(", ",
                         new[] {
                             Enabled       == true     ? "V2G on"                              : null,
                             InterfaceName is not null ? $"on \"{InterfaceName}\""             : null,
                             V2GPort.HasValue          ? V2GPort.Value == 0
                                                             ? "on any free port"
                                                             : $"on port {V2GPort.Value}"      : null,
                             SDP.HasValue              ? SDP.Value ? "SDP answering"
                                                                   : "SDP silent"              : null,
                             Loopback      == true     ? "SDP hearing this machine too"       : null,
                             SlacTransport.HasValue    ? $"SLAC over {SlacTransport.Value}"    : null,
                             EVSEId        is not null ? $"as \"{EVSEId}\""                    : null,
                             T1STransport == T1STransportKind.None
                                                       ? "no T1S bus"
                                                       : T1STransport.HasValue
                                                             ? $"T1S over {T1STransport.Value.Write()}"  : null,
                             T1SBus        is not null ? $"on the bus at {T1SBus}"              : null,
                             T1SInterface  is not null ? $"T1S on \"{T1SInterface}\""           : null,
                             T1SName       is not null ? $"coordinating as \"{T1SName}\""       : null,
                             T1SCycle.HasValue         ? $"a cycle every {T1SCycle.Value.TotalMilliseconds:F0} ms"  : null,
                             T1SWarning_C.HasValue     ? $"warm at {T1SWarning_C.Value:F0} °C"  : null,
                             T1SOverload_C.HasValue    ? $"overloaded at {T1SOverload_C.Value:F0} °C"  : null
                         }.Where(part => part is not null));

        #endregion

    }

}
