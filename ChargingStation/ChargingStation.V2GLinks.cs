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

using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.EVSEs;
using cloud.charging.open.ChargingStation.ISO15118;

using cloud.charging.open.protocols.ISO15118.SLAC.Transport;
using cloud.charging.open.protocols.ISO15118.T1S;
using cloud.charging.open.protocols.ISO15118.T1S.Transport;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The wire below each charging cable: one V2G link per EVSE.
    /// </summary>
    /// <remarks>
    /// What a link offers comes from three places, each laid over the one
    /// before. The station's defaults: nothing at all, port 15118 for the first
    /// EVSE and the next ports for the others, an EVSE identification ending in
    /// the EVSE's number. The command line, which speaks of one link and has
    /// always meant the first EVSE's - and whose certificate every link shows,
    /// because a certificate is the station's. And the file's "v2g" section,
    /// which has the last word on each link.
    ///
    /// Two links cannot share what only one of them can hold: a TCP port, the
    /// SDP answer on one interface, the SLAC listener on one powerline
    /// interface, the group of one emulated 10BASE-T1S bus. A change that would
    /// make two of them share one is refused before anything is written, and a
    /// file that already says so starts the first of the two and says why not
    /// the second.
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        private readonly ConcurrentDictionary<Byte, V2GLink>  v2gLinks = [];

        private V2GLinksConfiguration                          v2gFile  = V2GLinksConfiguration.Empty;

        #endregion

        #region Properties

        /// <summary>
        /// The link below each EVSE that has one up, by the EVSE's id.
        /// </summary>
        public IReadOnlyDictionary<Byte, V2GLink> V2GLinks
            => v2gLinks;

        #endregion


        #region V2GLinkOf(EVSE)

        /// <summary>
        /// The link below the given EVSE, where one is up.
        /// </summary>
        public V2GLink? V2GLinkOf(Byte EVSE)

            => v2gLinks.TryGetValue(EVSE, out var link)
                   ? link
                   : null;

        #endregion

        #region V2GOptionsOf(EVSE)

        /// <summary>
        /// What the link below the given EVSE is to offer, as it is configured
        /// now.
        /// </summary>
        public V2GOptions V2GOptionsOf(Byte EVSE)

            => V2GOptionsOf(EVSE, v2gFile);

        private V2GOptions V2GOptionsOf(Byte                   EVSE,
                                        V2GLinksConfiguration  File)

            => File.LinkOf(EVSE).Apply(DefaultV2GOptionsOf(EVSE));

        #endregion

        #region (private) DefaultV2GOptionsOf(EVSE)

        /// <summary>
        /// What the link below an EVSE offers where the file says nothing.
        /// </summary>
        private V2GOptions DefaultV2GOptionsOf(Byte EVSE)

            => EVSE == 1
                   ? V2GOptions
                   : V2GOptions.Off with {

                         ServerCertificate       = V2GOptions.ServerCertificate,
                         ServerCertificateChain  = V2GOptions.ServerCertificateChain,
                         SessionTimeout          = V2GOptions.SessionTimeout,
                         MulticastLoopback       = V2GOptions.MulticastLoopback,

                         // The simulated medium of a bench has one endpoint,
                         // and it is the first EVSE's.
                         SlacTransport           = V2GOptions.SlacTransport == SlacTransportKind.UDP
                                                       ? SlacTransportKind.Auto
                                                       : V2GOptions.SlacTransport,

                         V2GPort                 = (UInt16) (V2GOptions.DefaultV2GPort + EVSE - 1),
                         EVSEId                  = V2GOptions.DefaultEVSEIdOf(EVSE)

                     };

        #endregion

        #region (private) V2GLabelOf(EVSE)

        /// <summary>
        /// An EVSE as the log and the pages name it: "EVSE A".
        /// </summary>
        private String V2GLabelOf(Byte EVSE)

            => $"EVSE {EVSEs.FirstOrDefault(evse => evse.Id == EVSE)?.PhysicalReference ?? EVSE.ToString()}";

        #endregion


        #region (static) ConflictBetween(A, B)

        /// <summary>
        /// What two links would have to share that only one can hold, or null
        /// where they can both be up.
        /// </summary>
        public static String? ConflictBetween(V2GOptions  A,
                                              V2GOptions  B)
        {

            if (!A.Enabled || !B.Enabled)
                return null;

            if (A.V2GPort != 0 && A.V2GPort == B.V2GPort)
                return $"both would listen on port {A.V2GPort}";

            var sameInterface = String.Equals(A.InterfaceName ?? "", B.InterfaceName ?? "", StringComparison.OrdinalIgnoreCase);
            var on            = A.InterfaceName is null ? "the interface the station picks" : $"'{A.InterfaceName}'";

            if (sameInterface && A.SDP && B.SDP)
                return $"both would answer SDP on {on}";

            if (sameInterface && A.SlacTransport != SlacTransportKind.None && B.SlacTransport != SlacTransportKind.None &&
                !(A.SlacTransport == SlacTransportKind.UDP && B.SlacTransport == SlacTransportKind.UDP && !Equals(A.SlacUDPEndpoint, B.SlacUDPEndpoint)))
                return $"both would listen for SLAC on {on}";

            if (A.T1S is { Transport: not T1STransportKind.None } busA &&
                B.T1S is { Transport: not T1STransportKind.None } busB &&
                Equals(busA.Group ?? T1SConstants.DefaultMulticastEndpoint, busB.Group ?? T1SConstants.DefaultMulticastEndpoint) &&
                String.Equals(busA.InterfaceName ?? A.InterfaceName ?? "", busB.InterfaceName ?? B.InterfaceName ?? "", StringComparison.OrdinalIgnoreCase))
                return "both would coordinate the same 10BASE-T1S bus";

            return null;

        }

        #endregion

        #region (private) ConflictIn(File)

        /// <summary>
        /// The first two EVSEs whose links the given file would make share
        /// something, said as a sentence; null where there are none.
        /// </summary>
        private String? ConflictIn(V2GLinksConfiguration File)
        {

            var options = EVSEs.Select(evse => (evse.Id, Options: V2GOptionsOf(evse.Id, File))).ToArray();

            for (var i = 0; i < options.Length; i++)
                for (var j = i + 1; j < options.Length; j++)
                    if (ConflictBetween(options[i].Options, options[j].Options) is String conflict)
                        return $"{V2GLabelOf(options[i].Id)} and {V2GLabelOf(options[j].Id)} cannot both be up: {conflict}.";

            return null;

        }

        #endregion


        #region (private) StartV2GLinks(CancellationToken)

        /// <summary>
        /// Bring up the link below every EVSE that is to have one.
        /// </summary>
        private async Task StartV2GLinks(CancellationToken CancellationToken = default)
        {
            foreach (var evse in EVSEs)
                await StartV2GLink(evse.Id, CancellationToken);
        }

        #endregion

        #region (private) StartV2GLink(EVSE, CancellationToken)

        /// <summary>
        /// Bring up the link below one EVSE, as it is configured now - unless it
        /// would need what a link already up holds.
        /// </summary>
        private async Task StartV2GLink(Byte               EVSE,
                                        CancellationToken  CancellationToken = default)
        {

            var options = V2GOptionsOf(EVSE);

            if (!options.Enabled)
                return;

            foreach (var (other, up) in v2gLinks)
                if (other != EVSE && ConflictBetween(options, up.Options) is String conflict)
                {
                    Log.Error($"{V2GLabelOf(EVSE)}: the V2G link does not come up, because {V2GLabelOf(other)}'s is up and {conflict}.",
                              "15118", "config");
                    return;
                }

            if (await V2GLink.TryStart(options, Log, TimeProvider, CancellationToken, About: V2GLabelOf(EVSE)) is { } link)
                v2gLinks[EVSE] = link;

        }

        #endregion

        #region (private) RestartV2GLink(EVSE, CancellationToken)

        /// <summary>
        /// Take the wire below one cable down and bring it up again as it is now
        /// configured; the other EVSEs' links stay as they are.
        /// </summary>
        /// <remarks>
        /// Does nothing at all before Start(), so that a station reconfigured
        /// between construction and start does not open sockets early - the
        /// settings are simply what Start() will then read.
        ///
        /// A vehicle in the middle of a SLAC match or an open V2G session on
        /// this cable goes down with the link. That is the honest outcome of
        /// changing which interface a station listens on, and the log says so
        /// rather than the page pretending the change was free.
        /// </remarks>
        private async Task RestartV2GLink(Byte               EVSE,
                                          CancellationToken  CancellationToken)
        {

            if (!started)
                return;

            await EndV2GLink(EVSE);

            await StartV2GLink(EVSE, CancellationToken);

        }

        #endregion

        #region (private) EndV2GLink(EVSE) / EndTheV2GLinks()

        /// <summary>
        /// End the link below one EVSE, where one is up.
        /// </summary>
        private async Task EndV2GLink(Byte EVSE)
        {

            if (!v2gLinks.TryRemove(EVSE, out var link))
                return;

            var sessions = link.ActiveSLACSessions;

            if (sessions > 0)
                Log.Warning($"{V2GLabelOf(EVSE)}: the V2G link is being taken down with {sessions} SLAC session(s) open; they end here.",
                            "15118", "config");

            await link.DisposeAsync();

        }

        /// <summary>
        /// End the link below every cable - the first thing a station does when
        /// it stops.
        /// </summary>
        /// <remarks>
        /// Each is let go of even where ending it fails: the station is stopping,
        /// and a link it could not end is not one it can use again. What failed
        /// first is thrown on once all of them were ended.
        /// </remarks>
        protected virtual async Task EndTheV2GLinks()
        {

            Exception? failed = null;

            foreach (var evse in v2gLinks.Keys.ToArray())
            {
                try
                {
                    await EndV2GLink(evse);
                }
                catch (Exception e)
                {
                    failed ??= e;
                }
            }

            if (failed is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failed).Throw();

        }

        #endregion

        #region (private) EndTheLinksOfEVSEsThatAreGone()

        /// <summary>
        /// End the links below EVSEs this station no longer has, after its EVSEs
        /// were changed.
        /// </summary>
        private async Task EndTheLinksOfEVSEsThatAreGone()
        {

            foreach (var evse in v2gLinks.Keys.ToArray())
                if (!EVSEs.Any(candidate => candidate.Id == evse))
                {
                    Log.Notice($"EVSE {evse} is gone, and the V2G link below it with it.", "15118", "config");
                    await EndV2GLink(evse);
                }

        }

        #endregion

    }

}
