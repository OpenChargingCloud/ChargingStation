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
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using NUnit.Framework;

using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;

using org.GraphDefined.Vanaheimr.Hermod.PKI;

using cloud.charging.open.ChargingStation.OCPP;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What the station's own routes answer where the file a change goes to
    /// cannot take it: 500 with why, and nothing changed - as the node's own
    /// routes answer for name resolution, the time source and the certificate
    /// store (WWCP_Node_TestKit's AChangeTheFileCannotTakeIsAServerError and
    /// its kin, in ChargingStationConformance).
    /// </summary>
    /// <remarks>
    /// A file is kept from being written as the kit keeps it: a directory
    /// where its next version is written first. That stops root as well, and
    /// every operating system the station runs on.
    /// </remarks>
    [TestFixture]
    public class FilesThatCannotBeWrittenTests : AChargingStationTests
    {

        #region Data

        /// <summary>
        /// A secret for credentials that go nowhere.
        /// </summary>
        private const String ASecret = "not-a-real-password-for-a-test";

        #endregion

        #region (private static) Said(Response)

        /// <summary>
        /// The status of an answer and what it said, as JSON.
        /// </summary>
        private static async Task<(HttpStatusCode Status, JObject Said)> Said(HttpResponseMessage Response)
        {

            using (Response)
            {

                var text = await Response.Content.ReadAsStringAsync();

                try
                {
                    return (Response.StatusCode, JObject.Parse(text));
                }
                catch (JsonReaderException)
                {
                    return (Response.StatusCode, new JObject(new JProperty("body", text)));
                }

            }

        }

        #endregion


        #region (private) AChangeOf(Section, Now) / WhatIsSaidOf(Section, JSON)

        /// <summary>
        /// A change of the given section that is fine in itself, and not what
        /// the station has now.
        /// </summary>
        private static StringContent AChangeOf(String   Section,
                                               JObject  Now)
        {

            switch (Section)
            {

                case "display":
                    return JSONBody(new JProperty("dimFrom",   "22:00"),
                                    new JProperty("dimUntil",  "06:00"),
                                    new JProperty("dimTo",     0.2));

                case "power":
                    return JSONBody(new JProperty("uplinkPowerLimit_kW", Now.Value<Double?>("uplinkPowerLimit_kW") == 42 ? 43 : 42));

                case "calibration":
                {

                    using var key          = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                    var request            = new CertificateRequest("CN=A Calibration Of This Station", key, HashAlgorithmName.SHA256);
                    using var certificate  = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(365));

                    return JSONBody(new JProperty("certificates", new JArray(
                                        new JObject(
                                            new JProperty("id",   "calibration-1"),
                                            new JProperty("pem",  certificate.ExportCertificatePem())
                                        ))));

                }

                case "evses":
                {

                    var evses  = (JArray) Now["evses"]!.DeepClone();
                    var first  = (JObject) evses[0]!;

                    first["maxPower_kW"] = first.Value<Double>("maxPower_kW") == 11 ? 7.4 : 11;

                    // Its cables say nothing, and may deliver what it does.
                    foreach (var connector in (JArray) first["connectors"]!)
                        connector["maxPower_kW"] = null;

                    return JSONBody(new JProperty("evses", evses));

                }

                case "rfid":
                    return JSONBody(new JProperty("readers", new JArray(
                                        new JObject(
                                            new JProperty("id",       "reader-1"),
                                            new JProperty("kind",     "a reader by the door"),
                                            new JProperty("enabled",  true)
                                        ))));

                case "v2g":
                    return JSONBody(new JProperty("port", Now.Value<Int32?>("port") == 15119 ? 15120 : 15119));

                default:
                    throw new ArgumentException($"No section '{Section}' here.", nameof(Section));

            }

        }

        /// <summary>
        /// What a change of the given section changes, as the page reads it.
        /// </summary>
        private static String WhatIsSaidOf(String   Section,
                                           JObject  JSON)

            => Section switch {
                   "display"      => $"{JSON["dimFrom"]} to {JSON["dimUntil"]} at {JSON["dimTo"]}",
                   "power"        => $"{JSON["uplinkPowerLimit_kW"]}",
                   "calibration"  => String.Join(", ", ((JArray) JSON["certificates"]!).Select(certificate => certificate.Value<String>("id"))),
                   "evses"        => JSON["evses"]!.ToString(Formatting.None),
                   "rfid"         => JSON["readers"]!.ToString(Formatting.None),
                   "v2g"          => $"{JSON["port"]}",
                   _              => throw new ArgumentException($"No section '{Section}' here.", nameof(Section))
               };

        #endregion

        #region AChangeTheConfigurationFileCannotTakeIsAServerError(Section)

        /// <summary>
        /// A change of one of the station's own sections that is fine in
        /// itself, and that the configuration file cannot take, is answered
        /// 500 with why - and changes nothing. It was a 400, as if something
        /// had been wrong with the change.
        /// </summary>
        [TestCase("display")]
        [TestCase("power")]
        [TestCase("calibration")]
        [TestCase("evses")]
        [TestCase("rfid")]
        [TestCase("v2g")]
        public async Task AChangeTheConfigurationFileCannotTakeIsAServerError(String Section)
        {

            using var http      = await SignedIn();

            var path            = $"api/v1/configuration/{Section}";
            var before          = await GetJSON(http, path);
            var change          = AChangeOf(Section, before);

            System.IO.Directory.CreateDirectory(Station.ConfigFile.Path + ".tmp");

            var (status, said)  = await Said(await http.PutAsync(path, change));
            var after           = await GetJSON(http, path);

            Assert.Multiple(() => {
                Assert.That(status,                          Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),     Does.StartWith($"'{Station.ConfigFile.Path}' could not be written: "));
                Assert.That(WhatIsSaidOf(Section, after),    Is.EqualTo(WhatIsSaidOf(Section, before)), "what the page shows as saved");
            });

        }

        #endregion

        #region ARefusalIsWhatItWasWhileTheConfigurationFileCannotBeWritten()

        /// <summary>
        /// What was wrong with a change of the station's own sections is
        /// answered as it was while the configuration file cannot be written:
        /// a 500 is the file's, and only where it was the file that refused.
        /// </summary>
        [Test]
        public async Task ARefusalIsWhatItWasWhileTheConfigurationFileCannotBeWritten()
        {

            using var http = await SignedIn();

            System.IO.Directory.CreateDirectory(Station.ConfigFile.Path + ".tmp");

            var display      = await http.PutAsync("api/v1/configuration/display",      JSONBody(new JProperty("dimFrom",              "25:00"),
                                                                                                  new JProperty("dimUntil",             "06:00")));
            var power        = await http.PutAsync("api/v1/configuration/power",        JSONBody(new JProperty("uplinkPowerLimit_kW",  -5)));
            var calibration  = await http.PutAsync("api/v1/configuration/calibration",  JSONBody(new JProperty("certificates",         new JArray(new JObject(new JProperty("id", "no-pem"))))));
            var evses        = await http.PutAsync("api/v1/configuration/evses",        JSONBody(new JProperty("evses",                "none")));
            var rfid         = await http.PutAsync("api/v1/configuration/rfid",         JSONBody(new JProperty("readers",              new JArray(new JObject(new JProperty("id", "no-kind"))))));
            var v2g          = await http.PutAsync("api/v1/configuration/v2g",          JSONBody(new JProperty("port",                 70000)));

            Assert.Multiple(() => {
                Assert.That(display.    StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "hours that are none");
                Assert.That(power.      StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a limit below nothing");
                Assert.That(calibration.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a calibration certificate without its certificate");
                Assert.That(evses.      StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "EVSEs that are no list");
                Assert.That(rfid.       StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a reader of no kind");
                Assert.That(v2g.        StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a port beyond the last");
            });

        }

        #endregion


        #region (private static) WhatIsKept(JSON)

        /// <summary>
        /// The connections and the credentials in the store's JSON, in one line
        /// each, by their identification.
        /// </summary>
        private static String WhatIsKept(JObject JSON)

            => String.Join(Environment.NewLine,
                           ((JArray) JSON["connections"]!).Select(connection => $"connection {connection["id"]}: '{connection["description"]}', {connection["url"]}, {connection["connectionType"]}").
                           Concat(((JArray) JSON["authentications"]!).Select(credentials => $"credentials {credentials["id"]}: '{credentials["description"]}', {credentials["kind"]} as '{credentials["login"]}'")).
                           Order());

        #endregion

        #region AChangeOfTheConnectionsTheirFileCannotTakeIsAServerError(Change)

        /// <summary>
        /// A change of the connections or the credentials that is fine in
        /// itself, and that their file cannot take, is answered 500 with why -
        /// and changes nothing, now or when the files are read again at the
        /// next start. Both files were written in place: one that could not be
        /// written threw out of a change already made, the web server answered
        /// 500 without saying why, and the station held what it had not kept,
        /// and showed it, until it was started again.
        /// </summary>
        [TestCase("add a connection")]
        [TestCase("change a connection")]
        [TestCase("remove a connection")]
        [TestCase("add credentials")]
        [TestCase("change credentials")]
        [TestCase("remove credentials")]
        public async Task AChangeOfTheConnectionsTheirFileCannotTakeIsAServerError(String Change)
        {

            using var http          = await SignedIn();

            // One of each that is there, for the changes of one that is there.
            var (made, credentials) = await Said(await http.PostAsync("api/v1/configuration/authentications", JSONBody(
                                                                          new JProperty("description",     "The CSMS's login"),
                                                                          new JProperty("kind",            "basic"),
                                                                          new JProperty("login",           "cs001"),
                                                                          new JProperty("secret",          ASecret))));

            var (dialled, connection) = await Said(await http.PostAsync("api/v1/configuration/connections", JSONBody(
                                                                          new JProperty("description",     "The CSMS"),
                                                                          new JProperty("url",             "wss://csms.example.org/cs001"),
                                                                          new JProperty("connectionType",  "CSMS"))));

            Assert.That(made,     Is.EqualTo(HttpStatusCode.Created), credentials.ToString());
            Assert.That(dialled,  Is.EqualTo(HttpStatusCode.Created), connection.ToString());

            var credentialsId       = credentials.Value<String>("id")!;
            var connectionId        = connection. Value<String>("id")!;

            var file                = Path.Combine(Station.Connections.Path,
                                                   Change.EndsWith("credentials")
                                                       ? ConnectionStore.AuthenticationsFileName
                                                       : ConnectionStore.ConnectionsFileName);

            var before              = WhatIsKept(await GetJSON(http, "api/v1/configuration/connections"));

            System.IO.Directory.CreateDirectory(file + ".tmp");

            var (status, said)      = Change switch {
                "add a connection"      => await Said(await http.PostAsync("api/v1/configuration/connections",            JSONBody(new JProperty("description", "The spare CSMS"),  new JProperty("url", "wss://spare.example.org/cs001"), new JProperty("connectionType", "CSMSBackup")))),
                "change a connection"   => await Said(await http.PostAsync("api/v1/configuration/connections/update",     JSONBody(new JProperty("id", connectionId), new JProperty("description", "The CSMS, renamed"), new JProperty("url", "wss://csms.example.org/cs001"), new JProperty("connectionType", "CSMS")))),
                "remove a connection"   => await Said(await http.PostAsync("api/v1/configuration/connections/remove",     JSONBody(new JProperty("id", connectionId)))),
                "add credentials"       => await Said(await http.PostAsync("api/v1/configuration/authentications",        JSONBody(new JProperty("description", "The spare's login"), new JProperty("kind", "basic"), new JProperty("login", "cs002"), new JProperty("secret", ASecret)))),
                "change credentials"    => await Said(await http.PostAsync("api/v1/configuration/authentications/update", JSONBody(new JProperty("id", credentialsId), new JProperty("description", "The CSMS's login, renamed"), new JProperty("kind", "basic"), new JProperty("login", "cs001")))),
                "remove credentials"    => await Said(await http.PostAsync("api/v1/configuration/authentications/remove", JSONBody(new JProperty("id", credentialsId)))),
                _                       => throw new ArgumentException($"No change '{Change}' here.", nameof(Change))
            };

            var held                = WhatIsKept(await GetJSON(http, "api/v1/configuration/connections"));

            // As at the next start: the files read again, with nothing in the
            // way any more.
            System.IO.Directory.Delete(file + ".tmp");

            var readAgain           = WhatIsKept(new ConnectionStore(Station.Connections.Path).ToJSON());

            Assert.Multiple(() => {
                Assert.That(status,                          Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),     Does.StartWith($"'{file}' could not be written: "));
                Assert.That(held,                            Is.EqualTo(before), "what the station holds");
                Assert.That(readAgain,                       Is.EqualTo(before), "what its files hold, read again");
            });

        }

        #endregion

        #region ARefusalOfTheConnectionsIsWhatItWasWhileTheirFilesCannotBeWritten()

        /// <summary>
        /// What was wrong with a change of the connections or the credentials is
        /// answered as it was while their files cannot be written.
        /// </summary>
        [Test]
        public async Task ARefusalOfTheConnectionsIsWhatItWasWhileTheirFilesCannotBeWritten()
        {

            using var http = await SignedIn();

            System.IO.Directory.CreateDirectory(Path.Combine(Station.Connections.Path, ConnectionStore.ConnectionsFileName     + ".tmp"));
            System.IO.Directory.CreateDirectory(Path.Combine(Station.Connections.Path, ConnectionStore.AuthenticationsFileName + ".tmp"));

            var noURL      = await http.PostAsync("api/v1/configuration/connections",         JSONBody(new JProperty("description", "Nowhere"), new JProperty("url", "ftp://csms.example.org"), new JProperty("connectionType", "CSMS")));
            var noSecret   = await http.PostAsync("api/v1/configuration/authentications",     JSONBody(new JProperty("description", "No secret"), new JProperty("kind", "basic"), new JProperty("login", "cs001")));
            var notThere   = await http.PostAsync("api/v1/configuration/connections/remove",  JSONBody(new JProperty("id", "nothing-by-this-id")));

            Assert.Multiple(() => {
                Assert.That(noURL.   StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "an address that cannot be dialled");
                Assert.That(noSecret.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "credentials without their secret");
                Assert.That(notThere.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a connection that is not there");
            });

        }

        #endregion


        #region (private) AKeyMadeHere(HTTP, Subject) / ACertificateFor(CSR)

        /// <summary>
        /// A new client key, made through the web interface: its identification
        /// and its signing request.
        /// </summary>
        private static async Task<(String Id, String CSR)> AKeyMadeHere(HttpClient  HTTP,
                                                                      String      Subject)
        {

            var (status, said) = await Said(await HTTP.PostAsync("api/v1/configuration/certificates", JSONBody(new JProperty("subject", Subject))));

            Assert.That(status, Is.EqualTo(HttpStatusCode.Created), said.ToString());

            return (said.Value<String>("id")!, said.Value<String>("csr")!);

        }

        /// <summary>
        /// What a certificate authority would send back for the given signing
        /// request.
        /// </summary>
        private static String ACertificateFor(String CSR)
        {

            var request  = (new PemReader(new StringReader(CSR)).ReadObject() as Pkcs10CertificationRequest)!;
            var caKeys   = PKIFactory.GenerateECCKeyPair("secp256r1");
            var ca       = PKIFactory.CreateRootCACertificate(RootKeyPair: caKeys, SubjectName: "Test CSMS CA");

            return PemEncoding.WriteString(
                       "CERTIFICATE",
                       PKIFactory.SignClientCertificate(request, caKeys.Private, ca).GetEncoded()
                   ) + Environment.NewLine;

        }

        #endregion

        #region AKeyItsDirectoryCannotTakeIsAServerError()

        /// <summary>
        /// A new client key whose files cannot be written is answered 500 with
        /// why, and nothing of it is kept. It was a 400, as if something had
        /// been wrong with the request.
        /// </summary>
        [Test]
        public async Task AKeyItsDirectoryCannotTakeIsAServerError()
        {

            using var http  = await SignedIn();

            var directory   = Station.ClientCertificates.Path;

            // Where its directory goes is a file.
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory);

            File.WriteAllText(directory, "no directory of keys");

            var (status, said)  = await Said(await http.PostAsync("api/v1/configuration/certificates", JSONBody(new JProperty("subject", "CN=A Station Without Room"))));
            var keys            = (JArray) (await GetJSON(http, "api/v1/configuration/certificates"))["entries"]!;

            File.Delete(directory);

            Assert.Multiple(() => {
                Assert.That(status,                          Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),     Does.StartWith($"The key could not be written to '{directory}': "));
                Assert.That(keys,                            Is.Empty, "the keys the station holds");
            });

        }

        #endregion

        #region ACertificateItsFileCannotTakeIsAServerError()

        /// <summary>
        /// A certificate for a key of this station whose file cannot be written
        /// is answered 500 with why, and the key stays without it - now, and
        /// in its files. It was a 400; and its file was written in place, where
        /// one that did not fit took the certificate before it with it.
        /// </summary>
        [Test]
        public async Task ACertificateItsFileCannotTakeIsAServerError()
        {

            using var http      = await SignedIn();

            var (id, csr)       = await AKeyMadeHere(http, "CN=A Station Whose Certificate Does Not Fit");
            var file            = Path.Combine(Station.ClientCertificates.Path, $"{id}.cert.pem");

            System.IO.Directory.CreateDirectory(file + ".tmp");

            var (status, said)  = await Said(await http.PostAsync("api/v1/configuration/certificates/import", JSONBody(new JProperty("pem", ACertificateFor(csr)))));
            var key             = ((JArray) (await GetJSON(http, "api/v1/configuration/connections"))["certificates"]!).Single(one => one.Value<String>("id") == id);

            System.IO.Directory.Delete(file + ".tmp");

            Assert.Multiple(() => {
                Assert.That(status,                             Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),        Does.StartWith($"The certificate could not be written to '{Station.ClientCertificates.Path}': "));
                Assert.That(key.Value<Boolean>("hasCertificate"),  Is.False, "the key has a certificate");
                Assert.That(File.Exists(file),                  Is.False, "its file is there");
            });

        }

        #endregion

        #region AKeyItsFilesDoNotLetGoOfStaysWhole()

        /// <summary>
        /// A key one of whose files cannot be taken away stays, whole, and the
        /// request is answered 500 with why. Its files were deleted one after
        /// the other: the key itself went, its description stayed, the answer
        /// was a 400 - and the key, still listed, was gone at the next start.
        /// </summary>
        [Test]
        [Platform("Win", Reason = "A file somebody holds open is deleted all the same on Linux, and a test run as root there deletes what it likes: nothing but Windows keeps a file from being taken away.")]
        public async Task AKeyItsFilesDoNotLetGoOfStaysWhole()
        {

            using var http      = await SignedIn();

            var (id, _)         = await AKeyMadeHere(http, "CN=A Station Somebody Holds On To");
            var files           = new[] { "key.pem", "csr.pem", "json" }.Select(extension => Path.Combine(Station.ClientCertificates.Path, $"{id}.{extension}")).ToArray();

            HttpStatusCode  status;
            JObject         said;

            // Its description, the last of its files, held open.
            using (new FileStream(files[^1], FileMode.Open, FileAccess.Read, FileShare.Read))
                (status, said)  = await Said(await http.PostAsync("api/v1/configuration/certificates/remove", JSONBody(new JProperty("id", id))));

            var keys            = ((JArray) (await GetJSON(http, "api/v1/configuration/certificates"))["entries"]!).Select(key => key.Value<String>("id"));

            Assert.Multiple(() => {
                Assert.That(status,                          Is.EqualTo(HttpStatusCode.InternalServerError), said.ToString());
                Assert.That(said.Value<String>("error"),     Does.Contain("could not be removed: "));
                Assert.That(keys,                            Does.Contain(id), "the key is still held");
                Assert.That(files.Where(file => !File.Exists(file)).Select(Path.GetFileName), Is.Empty, "the files of the key that are gone");
            });

        }

        #endregion

        #region ARefusalOfTheKeysIsWhatItWasWhileTheirFilesCannotBeWritten()

        /// <summary>
        /// What was wrong with a request about the client keys is answered as
        /// it was while their files cannot be written.
        /// </summary>
        [Test]
        public async Task ARefusalOfTheKeysIsWhatItWasWhileTheirFilesCannotBeWritten()
        {

            using var http  = await SignedIn();

            var (id, _)     = await AKeyMadeHere(http, "CN=A Station With A Key");

            System.IO.Directory.CreateDirectory(Path.Combine(Station.ClientCertificates.Path, $"{id}.cert.pem.tmp"));

            var noAlgorithm  = await http.PostAsync("api/v1/configuration/certificates",         JSONBody(new JProperty("subject", "CN=Another"), new JProperty("algorithm", "pigeon")));
            var notOne       = await http.PostAsync("api/v1/configuration/certificates/import",  JSONBody(new JProperty("pem", "not a certificate")));
            var notThere     = await http.PostAsync("api/v1/configuration/certificates/remove",  JSONBody(new JProperty("id", "nothing-by-this-id")));

            Assert.Multiple(() => {
                Assert.That(noAlgorithm.StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a key of no kind the station makes");
                Assert.That(notOne.     StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a file that holds no certificate");
                Assert.That(notThere.   StatusCode,  Is.EqualTo(HttpStatusCode.BadRequest), "a key that is not there");
            });

        }

        #endregion

    }

}
