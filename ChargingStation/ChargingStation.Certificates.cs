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

using cloud.charging.open.protocols.WWCP.Node.Certificates;

using Newtonsoft.Json.Linq;

using cloud.charging.open.protocols.WWCP.Node.Logging;

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
        /// The kinds of certificate this station's store keeps: TLS's four, the
        /// identity its web interface shows over HTTPS, and the three roots a
        /// vehicle's certificates chain to.
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
        /// The TLS server identity because it is what the web interface, the
        /// JSON API and the sign-in show where the "web" section says "tls":
        /// { "certificate": "store" }, or --https - kept from the start, so that
        /// --import-certificate tlsServerIdentity=&lt;file&gt; puts it in at the
        /// same start that asks for it, and the page offers it before anybody
        /// has asked.
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
        /// What this station presents - the TLS identity - is also who it is
        /// to a back end it dials, and to a meter. One may be brought, with
        /// its key; or its key is made here, see
        /// <see cref="CertificateStore.RequestKinds"/>, and only its
        /// certificate ever travels: handed to whoever issues certificates as
        /// a signing request, or sent to the CSMS in SignCertificate.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlyList<CertificateKind> StoredCertificateKinds = [
            CertificateKind.V2GRoot,
            CertificateKind.MORoot,
            CertificateKind.OEMRoot,
            CertificateKind.TLSRoot,
            CertificateKind.ClientRoot,
            CertificateKind.TLSServer,
            CertificateKind.TLSIdentity,
            CertificateKind.TLSServerIdentity
        ];

        #endregion

        #region (override) CompleteCertificatesJSON(JSON)

        /// <summary>
        /// What a charging station says about its store beyond what every
        /// node says: that the signing request of a key that waits can be
        /// sent to the CSMS from here, and when each one that still waits was.
        /// </summary>
        protected override void CompleteCertificatesJSON(JObject JSON)
        {

            base.CompleteCertificatesJSON(JSON);

            if (JSON["requests"] is not JObject requests)
                return;

            var sent = SigningRequestsSentToCSMS;

            requests["canBeSentToCSMS"] = true;

            foreach (var request in requests["entries"]?.OfType<JObject>() ?? [])
                if (request.Value<String>("id") is String id && sent.TryGetValue(id, out var at))
                    request["sentToCSMSAt"] = at.ToString("o");

        }

        #endregion

        #region (private) MoveClientKeysIntoTheStore(Directory)

        /// <summary>
        /// The directory beside the configuration file a station of before
        /// kept the keys it dials its back ends with in.
        /// </summary>
        public const String FormerClientKeysDirectoryName = "ocpp-client-keys";

        /// <summary>
        /// Move the keys a station of before kept in a directory of their
        /// own into the certificate store: a key with its certificate is a
        /// TLS identity there, a key whose request is still out waits there
        /// for its certificate - and a connection that showed one shows the
        /// identity it became.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Once: what was moved is removed where it was, a private key in two
        /// places being one place too many, and the directory with the last
        /// of it. What could not be moved stays, is said in the log, and is
        /// tried again at the next start.
        /// </para>
        /// <para>
        /// A certificate that has run out is moved all the same - it was
        /// kept before, and the connection that shows it says what is wrong
        /// with it better than a connection that shows nothing. One this
        /// runtime cannot hold up stays beside its key, as it did.
        /// </para>
        /// </remarks>
        /// <param name="Directory">Where the keys were kept.</param>
        private void MoveClientKeysIntoTheStore(String Directory)
        {

            if (!System.IO.Directory.Exists(Directory))
                return;

            var moved   = 0;
            var stayed  = 0;

            foreach (var keyFile in System.IO.Directory.GetFiles(Directory, "*.key.pem").Order(StringComparer.Ordinal))
            {

                var name  = System.IO.Path.GetFileName(keyFile);
                var id    = name[..name.IndexOf(".key.pem", StringComparison.Ordinal)];

                String Beside(String Extension)
                    => System.IO.Path.Combine(Directory, $"{id}.{Extension}");

                try
                {

                    var meta       = File.Exists(Beside("json")) ? JObject.Parse(File.ReadAllText(Beside("json"))) : [];

                    var createdAt  = DateTimeOffset.TryParse(meta.Value<String>("createdAt"),
                                                             System.Globalization.CultureInfo.InvariantCulture,
                                                             System.Globalization.DateTimeStyles.RoundtripKind,
                                                             out var parsed)
                                         ? parsed
                                         : new DateTimeOffset(File.GetLastWriteTimeUtc(keyFile));

                    if (!Certificates.TryAdoptRequest(CertificateKind.TLSIdentity,
                                                      File.ReadAllText(keyFile),
                                                      File.ReadAllText(Beside("csr.pem")),
                                                      meta.Value<String>("algorithm") ?? CertificateStore.DefaultAlgorithm,
                                                      createdAt,
                                                      null,
                                                      null,
                                                      null,
                                                      out var request,
                                                      out var error,
                                                      out _))
                    {
                        stayed++;
                        Log.Warning($"The key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is: {error}",
                                    "certificates", "security");
                        continue;
                    }

                    if (File.Exists(Beside("cert.pem")))
                    {

                        if (!Certificates.TryAnswerRequest(File.ReadAllText(Beside("cert.pem")), out var identity, out _, out _, out error, out _, AcceptExpired: true))
                        {
                            stayed++;
                            Log.Warning($"The certificate of the key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is, " +
                                        $"with its key: {error}", "certificates", "security");
                            continue;
                        }

                        // What showed the key shows the identity it became.
                        if (identity is not null)
                            foreach (var connection in Connections.Connections.Where(connection => connection.CertificateId == id).ToArray())
                                if (!Connections.TrySetCertificate(connection.Id, identity.Id, out var notSwitched))
                                    Log.Warning($"'{connection.Description}' showed the certificate of the key '{id}', which is the identity '{identity.Id}' now, " +
                                                $"and could not be told so: {notSwitched}", "ocpp", "connections");

                    }

                    foreach (var extension in new[] { "cert.pem", "json", "csr.pem", "key.pem" })
                        File.Delete(Beside(extension));

                    moved++;

                }
                catch (Exception e)
                {
                    stayed++;
                    Log.Warning($"The key '{id}' in '{Directory}' could not be moved into the certificate store and stays where it is: {e.Message}",
                                "certificates", "security");
                }

            }

            try
            {
                if (!System.IO.Directory.EnumerateFileSystemEntries(Directory).Any())
                    System.IO.Directory.Delete(Directory);
            }
            catch (Exception e)
            {
                Log.Debug($"'{Directory}' is empty and could not be removed: {e.Message}", "certificates");
            }

            if (moved + stayed > 0)
                Log.Metrological(LogLevel.Notice,
                                 $"The keys this station dials its back ends with are kept in its certificate store from now on: {moved} moved over from '{Directory}'" +
                                 (stayed > 0 ? $", {stayed} could not be and stayed" : "") + ".",
                                 "certificates", "security");

        }

        #endregion

    }

}
