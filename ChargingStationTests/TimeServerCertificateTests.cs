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

using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.DNS;
using org.GraphDefined.Vanaheimr.Norn.NTS;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// What a time server's test says about its TLS certificate: what the
    /// certificate claims, how long each one of the chain has left, and whether
    /// it could be validated.
    /// </summary>
    /// <remarks>
    /// With certificates made here and a moment fixed to the second, so that
    /// "79 day(s) left" is a number and not a race with the clock. Whether a
    /// chain validates is Norn's to find out and is tested there; what is
    /// tested here is that everything it found is said, the root's validity as
    /// much as the server's - a root can be pinned, and a pinned root that runs
    /// out stops everything relying on it.
    /// </remarks>
    [TestFixture]
    public class TimeServerCertificateTests
    {

        #region Data

        private static readonly DateTimeOffset Now = new (2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

        #endregion


        #region (helper) Root(Name, From, To)

        private static X509Certificate2 Root(String Name, TimeSpan From, TimeSpan To)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));

            return request.CreateSelfSigned(Now + From, Now + To);

        }

        #endregion

        #region (helper) Leaf(Name, Issuer, From, To)

        private static X509Certificate2 Leaf(String Name, X509Certificate2 Issuer, TimeSpan From, TimeSpan To)
        {

            using var key = RSA.Create(2048);

            var request = new CertificateRequest($"CN={Name}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names   = new SubjectAlternativeNameBuilder();

            names.AddDnsName(Name);
            request.CertificateExtensions.Add(names.Build());

            return request.Create(Issuer, Now + From, Now + To, [ 1, 2, 3, 4 ]);

        }

        #endregion

        #region (helper) Steps(...)

        private static (String Level, String Text)[] Steps(X509Certificate2                    Server,
                                                           X509Certificate2[]                  Chain,
                                                           SslPolicyErrors                     Errors,
                                                           X509ChainStatusFlags[]?             Status   = null,
                                                           String                              Hostname = "time.example")

            => [.. ChargingStation.TLSSteps(new NTSKE_TLSInfo(
                                                ServerCertificate:              Server,
                                                CertificateChain:               [ Server ],
                                                CertificatePolicyErrors:        Errors,
                                                NegotiatedCipherSuite:          "TLS_AES_128_GCM_SHA256",
                                                NegotiatedTLSVersion:           "TLS 1.3",
                                                NegotiatedApplicationProtocol:  "ntske/1",
                                                ValidatedChain:                 Chain,
                                                ChainStatus:                    Status ?? [],
                                                CheckedHostname:                DomainName.Parse(Hostname),
                                                RevocationMode:                 X509RevocationMode.Online
                                            ),
                                            Now)];

        #endregion


        #region EveryCertificateIsShownWithBothEndsOfItsValidity()

        /// <summary>
        /// The session, the server's certificate and what it is for, the root
        /// with its fingerprint, each with both ends of its validity and the days
        /// it has left - and the verdict.
        /// </summary>
        /// <remarks>
        /// The fingerprint expected is worked out here rather than asked of the
        /// station, so that the test does not agree with whatever it is given.
        /// </remarks>
        [Test]
        public void EveryCertificateIsShownWithBothEndsOfItsValidity()
        {

            using var root    = Root("Test Root",     TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var server  = Leaf("time.example",  root,  TimeSpan.FromDays(-10),  TimeSpan.FromDays(79));

            var steps = Steps(server, [ server, root ], SslPolicyErrors.None);

            Assert.That(steps.Select(step => step.Text), Is.EqualTo(new[] {
                "TLS 1.3, TLS_AES_128_GCM_SHA256, ALPN ntske/1.",
                "Server certificate: CN=time.example, for time.example; RSA 2048-bit, sha256RSA; valid 2026-09-13 12:00:00 to 2026-12-11 12:00:00 UTC, 79 day(s) left.",
                "Root CA: CN=Test Root; RSA 2048-bit, sha256RSA; valid 2025-09-23 12:00:00 to 2029-06-19 12:00:00 UTC, 1000 day(s) left.",
                $"The root's SHA-256 fingerprint: {Convert.ToHexString(SHA256.HashData(root.RawData)).ToLowerInvariant()}.",
                "Validated: the chain ends at a root this machine trusts, nothing in it is revoked (asked online), and 'time.example' is one of the server certificate's names."
            }));

            Assert.That(steps.Select(step => step.Level), Is.EqualTo(new[] { "info", "info", "info", "info", "notice" }));

        }

        #endregion

        #region ARootThatHasRunOutIsAnError()

        /// <summary>
        /// A root out of its validity is an error on its own line, whatever the
        /// server's certificate says - and the verdict says why, in words.
        /// </summary>
        [Test]
        public void ARootThatHasRunOutIsAnError()
        {

            using var issuer  = Root("Issuing Root",  TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var server  = Leaf("time.example",  issuer, TimeSpan.FromDays(-10), TimeSpan.FromDays(79));
            using var expired = Root("Pinned Root",   TimeSpan.FromDays(-3650), TimeSpan.FromDays(-3));

            var steps = Steps(server, [ server, expired ], SslPolicyErrors.RemoteCertificateChainErrors, [ X509ChainStatusFlags.NotTimeValid ]);
            var root  = steps.Single(step => step.Text.StartsWith("Root CA:", StringComparison.Ordinal));

            Assert.Multiple(() => {
                Assert.That(root.Level,       Is.EqualTo("error"));
                Assert.That(root.Text,        Does.Contain("to 2026-09-20 12:00:00 UTC, expired 3 day(s) ago"));
                Assert.That(steps[^1],        Is.EqualTo(("error", "Not validated: a certificate in it is outside its validity.")));
            });

        }

        #endregion

        #region ACertificateForAnotherNameIsNotValidated()

        [Test]
        public void ACertificateForAnotherNameIsNotValidated()
        {

            using var root    = Root("Test Root",     TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var server  = Leaf("time.example",  root,  TimeSpan.FromDays(-10),  TimeSpan.FromDays(79));

            var steps = Steps(server, [ server, root ], SslPolicyErrors.RemoteCertificateNameMismatch, Hostname: "other.example");

            Assert.That(steps[^1], Is.EqualTo(("error", "Not validated: 'other.example' is not one of the server certificate's names.")));

        }

        #endregion

        #region AWeekBeforeItRunsOutIsAWarningAndBeforeItStartsAnError()

        [Test]
        public void AWeekBeforeItRunsOutIsAWarningAndBeforeItStartsAnError()
        {

            using var root    = Root("Test Root",     TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var soon    = Leaf("time.example",  root,  TimeSpan.FromDays(-80),  TimeSpan.FromDays(5));
            using var early   = Leaf("time.example",  root,  TimeSpan.FromDays(2),    TimeSpan.FromDays(92));

            var soonLine  = Steps(soon,  [ soon,  root ], SslPolicyErrors.None)[1];
            var earlyLine = Steps(early, [ early, root ], SslPolicyErrors.RemoteCertificateChainErrors, [ X509ChainStatusFlags.NotTimeValid ])[1];

            Assert.Multiple(() => {
                Assert.That(soonLine.Level,   Is.EqualTo("warning"));
                Assert.That(soonLine.Text,    Does.EndWith(", 5 day(s) left."));
                Assert.That(earlyLine.Level,  Is.EqualTo("error"));
                Assert.That(earlyLine.Text,   Does.EndWith(", not valid for another 2 day(s)."));
            });

        }

        #endregion

        #region TheRootCAIsNamedAndFingerprintedForTheList()

        /// <summary>
        /// What the NTS page's list shows of a server's last key exchange: the
        /// root its chain ended at, by its common name, its whole subject, and
        /// its SHA-256 fingerprint - and nothing before there was an exchange.
        /// </summary>
        [Test]
        public void TheRootCAIsNamedAndFingerprintedForTheList()
        {

            using var root    = Root("Test Root",     TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var server  = Leaf("time.example",  root,  TimeSpan.FromDays(-10),  TimeSpan.FromDays(79));

            var json = ChargingStation.RootCAJSON(new NTSKE_TLSInfo(ServerCertificate: server, ValidatedChain: [ server, root ]));

            Assert.Multiple(() => {
                Assert.That(json?.Value<String>("name"),                      Is.EqualTo("Test Root"));
                Assert.That(json?.Value<String>("subject"),                   Is.EqualTo("CN=Test Root"));
                Assert.That(json?.Value<String>("fingerprint"),               Is.EqualTo(Convert.ToHexString(SHA256.HashData(root.RawData)).ToLowerInvariant()));
                Assert.That(ChargingStation.RootCAJSON(null),                 Is.Null);
                Assert.That(ChargingStation.RootCAJSON(new NTSKE_TLSInfo()),  Is.Null, "a session that kept no chain names no root");
            });

        }

        #endregion

        #region WithoutARootTheLastOneSaysWhoIssuedIt()

        /// <summary>
        /// A chain that could not be built up to a root ends at a certificate
        /// that is no root, and says so - with who issued it, which is what is
        /// missing.
        /// </summary>
        [Test]
        public void WithoutARootTheLastOneSaysWhoIssuedIt()
        {

            using var root    = Root("Unknown Root",  TimeSpan.FromDays(-365), TimeSpan.FromDays(1000));
            using var server  = Leaf("time.example",  root,  TimeSpan.FromDays(-10),  TimeSpan.FromDays(79));

            var steps = Steps(server, [ server ], SslPolicyErrors.RemoteCertificateChainErrors, [ X509ChainStatusFlags.PartialChain ]);

            Assert.Multiple(() => {
                Assert.That(steps[1].Text,  Does.StartWith("Server certificate: CN=time.example, for time.example, issued by CN=Unknown Root;"));
                Assert.That(steps,          Has.None.Matches<(String Level, String Text)>(step => step.Text.Contains("fingerprint")));
                Assert.That(steps[^1],      Is.EqualTo(("error", "Not validated: no chain up to a root could be built.")));
            });

        }

        #endregion

    }

}
