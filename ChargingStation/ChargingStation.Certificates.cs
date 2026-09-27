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

using cloud.charging.open.protocols.WWCP.Node.Certificates;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The certificate store of this station's node: what it believes, what
    /// it presents, and what it recognises a server by.
    /// </summary>
    /// <remarks>
    /// The store is the node's - the same WWCP_Node CertificateStore a vehicle
    /// keeps, a directory of files with an index beside them, "certificates"
    /// beside the configuration file unless the file says otherwise. What a
    /// charging station keeps in it is decided here, in
    /// <see cref="StoredCertificateKinds"/>; what is done with each kind is the
    /// node's.
    /// </remarks>
    public partial class ChargingStation
    {

        #region StoredCertificateKinds

        /// <summary>
        /// The kinds of certificate this station's store keeps: TLS's four, and
        /// the three roots a vehicle's certificates chain to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// TLS's four because any node connects to servers and is connected to:
        /// the roots a time server or a name server may be vouched for by, the
        /// roots a client connecting here has to chain to, the certificates a
        /// server may be held to by its fingerprint, and what this station
        /// presents itself.
        /// </para>
        /// <para>
        /// The V2G, Mobility Operator and OEM roots because they are what Plug
        /// &amp; Charge checks a vehicle's chain, a contract and an OEM
        /// provisioning certificate against - the three kept apart for the
        /// reason the node gives. What only a vehicle holds - its own
        /// certificate, its contracts, its provisioning certificate, the key it
        /// checks a tariff with - is not offered: a page offering a kind that
        /// means nothing here would be offering a mistake.
        /// </para>
        /// <para>
        /// The keys this station dials its back ends with are not among them.
        /// They are made on this station and never imported, which a store that
        /// takes files cannot promise - see <see cref="OCPP.ClientCertificateStore"/>.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyList<CertificateKind> StoredCertificateKinds = [
            CertificateKind.V2GRoot,
            CertificateKind.MORoot,
            CertificateKind.OEMRoot,
            CertificateKind.TLSRoot,
            CertificateKind.ClientRoot,
            CertificateKind.TLSServer,
            CertificateKind.TLSIdentity
        ];

        #endregion

        #region CertificatesJSON()

        /// <summary>
        /// Everything in this station's certificate store, grouped the way it is
        /// shown.
        /// </summary>
        /// <remarks>
        /// Groups and not one list, as on the vehicle. The roots are what this
        /// station <i>believes</i>: any number of each kind may be on at once. A
        /// TLS identity is what it <i>presents</i>. A server certificate is
        /// neither: what it <i>recognises</i>, kept for a time server or a name
        /// server to be held to by its fingerprint. A page that put them in one
        /// table would have to explain that difference in a column heading.
        /// </remarks>
        public JObject CertificatesJSON()
        {

            // The kinds this store keeps: a page offering a kind the store
            // refuses would be offering a refusal.
            var kinds  = Certificates.Kinds;
            var byKind = new JObject();

            foreach (var kind in kinds)
                byKind.Add(kind.AsText(),
                           new JArray(Certificates.ByKind(kind).Select(entry => entry.ToJSON(WithDiagnostics: true))));

            return new JObject(

                       new JProperty("directory",    Certificates.Directory),

                       new JProperty("trustAnchors", new JArray(
                           kinds.Where(kind =>  kind.IsTrustAnchor()).Select(kind => kind.AsText())
                       )),

                       new JProperty("credentials",  new JArray(
                           kinds.Where(kind => !kind.IsTrustAnchor() && !kind.MustNotCarryPrivateKey()).Select(kind => kind.AsText())
                       )),

                       // Neither believed nor presented, and never with a key: a
                       // server certificate, kept to recognise a server by. Shown
                       // among what the station presents, it read as something
                       // the station would present.
                       new JProperty("recognised",   new JArray(
                           kinds.Where(kind => !kind.IsTrustAnchor() &&  kind.MustNotCarryPrivateKey()).Select(kind => kind.AsText())
                       )),

                       new JProperty("kinds",        new JObject(
                           kinds.Select(kind =>
                               new JProperty(kind.AsText(), new JObject(
                                   new JProperty("description",     kind.Describe()),
                                   new JProperty("trustAnchor",     kind.IsTrustAnchor()),
                                   new JProperty("needsPrivateKey", kind.NeedsPrivateKey()),
                                   new JProperty("hasUsages",       kind.HasUsages())
                               )))
                       )),

                       // What a TLS root or a server certificate may be told it is
                       // for, so that a page offers these and nothing the store
                       // would refuse.
                       new JProperty("usages",       new JArray(Certificates.Usages)),

                       new JProperty("certificates", byKind),

                       // Said here because this is the page where somebody is
                       // looking at the consequences of it, rather than only in
                       // the log at a start.
                       new JProperty("keysAreUnencrypted", Certificates.Entries.Any(entry => entry.HasPrivateKey))

                   );

        }

        #endregion

    }

}
