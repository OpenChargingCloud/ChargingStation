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
using System.Text;
using System.Buffers.Binary;
using System.Security.Cryptography;

using Newtonsoft.Json.Linq;

using NUnit.Framework;

using org.GraphDefined.Vanaheimr.Hermod.SSH;
using org.GraphDefined.Vanaheimr.Hermod.SSH.SFTP;
using org.GraphDefined.Vanaheimr.Hermod.SSH.Client;

using cloud.charging.open.protocols.WWCP.Node.SecureShell;
using cloud.charging.open.protocols.WWCP.Node.TestKit;

#endregion

namespace cloud.charging.open.ChargingStation.Tests
{

    /// <summary>
    /// The logos the display shows, put up over SFTP: by whoever may edit the
    /// providers - the CPO among them, and nobody who may only dim the screen
    /// - under the id of a provider this station knows, checked for being the
    /// picture its name says and nothing that could act; and on the display
    /// before what the configuration file says, from its own port, under a
    /// URL that changes with the picture.
    /// </summary>
    [TestFixture]
    public class LogoTests : AChargingStationTests
    {

        #region Data

        private const String  Provider       = "emp-one";
        private const String  CardOfKnown    = "04A2112233";
        private const String  ConfiguredEMP  = "https://example.org/emp-one.png";
        private const String  ConfiguredCPO  = "https://example.org/operator.png";

        private UInt16        sshPort;

        #endregion

        #region Configuration

        /// <summary>
        /// One outlet, a card reader whose cards are typed in, an operator with
        /// a logo of its own and one provider with one - by URL - and SFTP on;
        /// and a role "screen" from the file, which may dim the screen and put
        /// messages on it, and nothing else.
        /// </summary>
        protected override JObject Configuration
        {
            get
            {

                sshPort = TestPorts.Free();

                return new (

                    new JProperty("nts",       new JObject(new JProperty("groups", new JObject(new JProperty("legal", new JObject(new JProperty("enabled", false))))))),

                    new JProperty("evses",     new JArray(
                        new JObject(
                            new JProperty("id",                 1),
                            new JProperty("connectors",         new JArray("sType2")),
                            new JProperty("maxPower_kW",        22.0),
                            new JProperty("operative",          true),
                            new JProperty("physicalReference",  "A")
                        )
                    )),

                    new JProperty("rfid",      new JArray(
                        new JObject(
                            new JProperty("id",       "reader"),
                            new JProperty("kind",     "GraphDefined.FakeRFID"),
                            new JProperty("evse",     null),
                            new JProperty("enabled",  true)
                        )
                    )),

                    new JProperty("operator",  new JObject(
                        new JProperty("name",  "Stadtwerke Musterstadt"),
                        new JProperty("logo",  ConfiguredCPO),
                        new JProperty("emps",  new JArray(
                            new JObject(
                                new JProperty("id",             Provider),
                                new JProperty("name",           "Elektro Mobil GmbH"),
                                new JProperty("logo",           ConfiguredEMP),
                                new JProperty("tokenPrefixes",  new JArray("04A2"))
                            )
                        ))
                    )),

                    new JProperty("ssh",       new JObject(
                        new JProperty("enabled",  true),
                        new JProperty("port",     sshPort),
                        new JProperty("sftp",     true)
                    )),

                    new JProperty("roles",     new JObject(
                        new JProperty("screen", new JArray("display:edit", "display:run"))
                    ))

                );

            }
        }

        #endregion


        #region (private) Sftp() / Refusal(Operation) / InTheProvidersFolder()

        /// <summary>
        /// The station's one account, in whatever role it holds now, with an
        /// SFTP session open.
        /// </summary>
        private async Task<(SshClient Client, SftpClient Sftp)> Sftp()
        {

            var key    = SshHostKey.GenerateEd25519();
            var added  = await Station.AuthorizeSSHKey(ChargingStation.DefaultAdminUser, SshPublicKey.FromHostKey(key).ToAuthorizedKeyLine());

            Assert.That(added.IsAdded, Is.True, added.Reason);

            var client = await SshClient.ConnectAsync("127.0.0.1",
                                                      Station.SSHPort!.Value.ToUInt16(),
                                                      new SshClientOptions {
                                                          Username       = ChargingStation.DefaultAdminUser,
                                                          VerifyHostKey  = blob => Station.SSHHostKey == $"ssh-ed25519 {SshFingerprint.Sha256(blob)}",
                                                          Credentials    = [ key ]
                                                      });

            return (client, await client.OpenSftpClientAsync());

        }

        /// <summary>
        /// The status and the sentence SFTP refused the given operation with -
        /// or null, where it was not refused.
        /// </summary>
        private static async Task<(SftpStatusCode Code, String Why)?> Refusal(Func<Task> Operation)
        {

            try
            {
                await Operation();
                return null;
            }
            catch (SftpException refused)
            {
                return (refused.Code, refused.Message);
            }

        }

        /// <summary>
        /// Every file in the providers' folder on the disk, hidden ones too.
        /// </summary>
        private IReadOnlyList<String> InTheProvidersFolder()
        {

            var folder = Path.Combine(Station.LogosPath, "providers");

            return System.IO.Directory.Exists(folder)
                       ? [.. System.IO.Directory.GetFiles(folder).Select(file => Path.GetFileName(file)!).Order(StringComparer.Ordinal)]
                       : [];

        }

        #endregion

        #region (private static) PNG(Width, Height) / SVG(Body)

        /// <summary>
        /// A PNG whose header says it is of the given size.
        /// </summary>
        private static Byte[] PNG(UInt32 Width, UInt32 Height)
        {

            var png = new Byte[8 + 25 + 12];

            new Byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(png, 0);

            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8),  13);
            "IHDR"u8.CopyTo(png.AsSpan(12));
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16), Width);
            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20), Height);
            png[24] = 8;
            png[25] = 6;

            BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(33), 0);
            "IEND"u8.CopyTo(png.AsSpan(37));

            return png;

        }

        /// <summary>
        /// An SVG with the given inside.
        /// </summary>
        private static Byte[] SVG(String Body)

            => Encoding.UTF8.GetBytes($"""<svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10">{Body}</svg>""");

        #endregion


        #region TheLogosLieBesideTheConfigurationFile

        [Test]
        public void TheLogosLieBesideTheConfigurationFile()

            => Assert.Multiple(() => {
                   Assert.That(Station.LogosPath,                                                   Is.EqualTo(Path.Combine(Directory, ChargingStation.DefaultLogosDirectory)));
                   Assert.That(Station.SftpAreas.Select(area => area.Path),                         Is.SupersetOf(new[] { ChargingStation.LogosArea, ChargingStation.ProviderLogosArea }));
                   Assert.That(Station.SftpAreas.Where(area => area.Path.StartsWith("logos")).Select(area => (area.Read?.ToString(), area.Write?.ToString())),
                               Has.All.EqualTo(("providers:read", "providers:edit")));
               });

        #endregion

        #region ACPOPutsUpAProvidersLogoAndTheDisplayShowsItAtTheNextSwipe

        /// <summary>
        /// A CPO puts up a provider's logo; at the next swipe of one of its
        /// cards the display is told the file's URL rather than the one the
        /// configuration file says, and fetches it from its own port, without
        /// signing in: the picture, as what it is, with nothing in it let act -
        /// and a 304 for a display that has it already.
        /// </summary>
        [Test]
        public async Task ACPOPutsUpAProvidersLogoAndTheDisplayShowsItAtTheNextSwipe()
        {

            await Become("cpo", "systemadmin");

            var picture         = PNG(64, 32);
            var sha256          = Convert.ToHexStringLower(SHA256.HashData(picture));

            var (client, sftp)  = await Sftp();

            await using (client)
            await using (sftp)
                await sftp.UploadAsync($"/logos/providers/{Provider}.png", picture);

            Assert.That(Station.TryPresentToken("reader", 1, CardOfKnown, out _, out var error), Is.True, error);

            var logo            = Station.KioskJSON()["evses"]![0]!["session"]!["provider"]!.Value<String>("logo");

            Assert.That(logo, Is.EqualTo($"/api/kiosk/logos/providers/{Provider}.png?v={sha256[..8]}"));

            using var display   = AtTheDisplay();
            using var fetched   = await display.GetAsync(logo!.TrimStart('/'));

            var etag            = fetched.Headers.ETag?.ToString();

            using var again     = new HttpRequestMessage(HttpMethod.Get, logo.TrimStart('/'));
            again.Headers.TryAddWithoutValidation("If-None-Match", etag);
            using var unchanged = await display.SendAsync(again);

            await Assert.MultipleAsync(async () => {
                Assert.That(fetched.StatusCode,                                         Is.EqualTo(HttpStatusCode.OK));
                Assert.That(await fetched.Content.ReadAsByteArrayAsync(),               Is.EqualTo(picture));
                Assert.That(fetched.Content.Headers.ContentType?.MediaType,             Is.EqualTo("image/png"));
                Assert.That(etag,                                                       Is.EqualTo($"\"{sha256}\""));
                Assert.That(String.Join("; ", fetched.Headers.GetValues("Content-Security-Policy")),  Does.Contain("default-src 'none'").And.Contain("sandbox"));
                Assert.That(fetched.Headers.GetValues("X-Content-Type-Options"),        Is.EqualTo(new[] { "nosniff" }));
                Assert.That(unchanged.StatusCode,                                       Is.EqualTo(HttpStatusCode.NotModified));
            });

        }

        #endregion

        #region TheFileWinsOverTheConfigurationFileWhichStaysTheFallback

        /// <summary>
        /// Before a file is there, and once it is removed again, the display is
        /// told what the configuration file says; while it is there, the file -
        /// for the operator's logo as for a provider's.
        /// </summary>
        [Test]
        public async Task TheFileWinsOverTheConfigurationFileWhichStaysTheFallback()
        {

            var before          = (Station.LogoURL(Provider, ConfiguredEMP), Station.KioskJSON()["station"]!.Value<String>("logo"));

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            await sftp.UploadAsync($"/logos/providers/{Provider}.png", PNG(10, 10));
            await sftp.UploadAsync("/logos/operator.svg",              SVG("<circle cx='5' cy='5' r='4'/>"));

            var during          = (Station.LogoURL(Provider, ConfiguredEMP), Station.KioskJSON()["station"]!.Value<String>("logo"));

            await sftp.RemoveAsync($"/logos/providers/{Provider}.png");
            await sftp.RemoveAsync("/logos/operator.svg");

            var after           = (Station.LogoURL(Provider, ConfiguredEMP), Station.KioskJSON()["station"]!.Value<String>("logo"));

            Assert.Multiple(() => {
                Assert.That(before,         Is.EqualTo((ConfiguredEMP, ConfiguredCPO)));
                Assert.That(during.Item1,   Does.StartWith($"/api/kiosk/logos/providers/{Provider}.png?v="));
                Assert.That(during.Item2,   Does.StartWith("/api/kiosk/logos/operator.svg?v="));
                Assert.That(after,          Is.EqualTo((ConfiguredEMP, ConfiguredCPO)));
            });

        }

        #endregion

        #region ANewKindOfPictureTakesThePlaceOfTheOld

        /// <summary>
        /// One picture to a provider: an SVG put up takes away the PNG that was
        /// there, and the display is told the SVG.
        /// </summary>
        [Test]
        public async Task ANewKindOfPictureTakesThePlaceOfTheOld()
        {

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            await sftp.UploadAsync($"/logos/providers/{Provider}.png", PNG(10, 10));
            await sftp.UploadAsync($"/logos/providers/{Provider}.svg", SVG("<rect width='10' height='10'/>"));

            Assert.Multiple(() => {
                Assert.That(InTheProvidersFolder(),                        Is.EqualTo(new[] { $"{Provider}.svg" }));
                Assert.That(Station.LogoURL(Provider, ConfiguredEMP),      Does.StartWith($"/api/kiosk/logos/providers/{Provider}.svg?v="));
            });

        }

        #endregion

        #region WhoMayNotEditTheProvidersMayNotPutUpALogo

        /// <summary>
        /// Neither a viewer nor a role that may dim the screen and put messages
        /// on it - "display:edit" is not "providers:edit" - and both are told
        /// what it needs; the viewer still sees the logos, the other role, which
        /// may not read them, not even the folder.
        /// </summary>
        [TestCase("viewer", true)]
        [TestCase("screen", false)]
        public async Task WhoMayNotEditTheProvidersMayNotPutUpALogo(String  Role,
                                                                    Boolean SeesThem)
        {

            await Become(Role, "systemadmin");

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            var refused         = await Refusal(async () => await sftp.UploadAsync($"/logos/providers/{Provider}.png", PNG(10, 10)));
            var root            = (await sftp.ListDirectoryAsync("/")).Select(entry => entry.Name).ToList();

            Assert.Multiple(() => {
                Assert.That(refused,                                Is.EqualTo((SftpStatusCode.PermissionDenied, "Writing /logos/providers needs providers:edit.")));
                Assert.That(root.Contains(ChargingStation.LogosArea), Is.EqualTo(SeesThem));
                Assert.That(InTheProvidersFolder(),                 Is.Empty);
            });

        }

        #endregion

        #region ANameThatIsNoProvidersIsRefusedBeforeAByteIsSent

        /// <summary>
        /// A logo is bound to its provider by its name: a name that is no
        /// provider's of this station is refused when it is opened, with the
        /// providers there are - and so is another kind of file, and a provider's
        /// logo in the operator's folder.
        /// </summary>
        [Test]
        public async Task ANameThatIsNoProvidersIsRefusedBeforeAByteIsSent()
        {

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            var nobody          = await Refusal(async () => await sftp.UploadAsync("/logos/providers/nobody.png",       PNG(10, 10)));
            var gif             = await Refusal(async () => await sftp.UploadAsync($"/logos/providers/{Provider}.gif", "GIF89a"u8.ToArray()));
            var misplaced       = await Refusal(async () => await sftp.UploadAsync($"/logos/{Provider}.png",           PNG(10, 10)));

            Assert.Multiple(() => {
                Assert.That(nobody,             Is.EqualTo((SftpStatusCode.PermissionDenied, $"'nobody' is no e-mobility provider of this station; its providers are '{Provider}'.")));
                Assert.That(gif?.Why,           Does.Contain("A logo is a .png, .jpg, .jpeg, .webp or .svg file"));
                Assert.That(misplaced?.Why,     Does.Contain("holds the operator's logo alone"));
                Assert.That(InTheProvidersFolder(), Is.Empty);
            });

        }

        #endregion

        #region WhatIsNoPictureOfItsNameIsRefusedAndLeavesNothing

        /// <summary>
        /// A file that is not the picture its name says, one larger than a
        /// screen could use, and SVGs that would run something, handle an event
        /// or fetch from somewhere else: refused when they are closed, with
        /// nothing of them left - and a plain SVG put up.
        /// </summary>
        [Test]
        public async Task WhatIsNoPictureOfItsNameIsRefusedAndLeavesNothing()
        {

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            async Task<String?> Why(String Name, Byte[] Content)
                => (await Refusal(async () => await sftp.UploadAsync($"/logos/providers/{Name}", Content)))?.Why;

            var refusals = new Dictionary<String, String?> {
                               ["not a PNG"]         = await Why($"{Provider}.png", "This is no picture."u8.ToArray()),
                               ["a PNG named .jpg"]  = await Why($"{Provider}.jpg", PNG(10, 10)),
                               ["too wide"]          = await Why($"{Provider}.png", PNG(4096, 10)),
                               ["a script"]          = await Why($"{Provider}.svg", SVG("<script>alert(1)</script>")),
                               ["an event"]          = await Why($"{Provider}.svg", SVG("<rect width='1' height='1' onclick='alert(1)'/>")),
                               ["foreign HTML"]      = await Why($"{Provider}.svg", SVG("<foreignObject><div xmlns='http://www.w3.org/1999/xhtml'>x</div></foreignObject>")),
                               ["an outside image"]  = await Why($"{Provider}.svg", SVG("<image xlink:href='https://example.org/x.png' width='1' height='1'/>")),
                               ["an outside style"]  = await Why($"{Provider}.svg", SVG("<style>@import url(https://example.org/x.css);</style>")),
                               ["a document type"]   = await Why($"{Provider}.svg", Encoding.UTF8.GetBytes("<!DOCTYPE svg [<!ENTITY x 'y'>]><svg xmlns='http://www.w3.org/2000/svg'>&x;</svg>")),
                               ["no SVG"]            = await Why($"{Provider}.svg", Encoding.UTF8.GetBytes("<html/>"))
                           };

            var leftAfterRefusals = InTheProvidersFolder();

            var plain = await Refusal(async () => await sftp.UploadAsync($"/logos/providers/{Provider}.svg",
                                                                          SVG("<defs><linearGradient id='g'/></defs><rect width='10' height='10' style='fill:url(#g)'/><use href='#g'/>")));

            Assert.Multiple(() => {

                foreach (var (what, why) in refusals)
                    Assert.That(why, Is.Not.Null, $"{what} was put up");

                Assert.That(refusals["too wide"],   Does.Contain("4096×10 pixels"));
                Assert.That(refusals["a script"],   Does.Contain("<script>"));
                Assert.That(leftAfterRefusals,      Is.Empty, "something of a refused upload was left");
                Assert.That(plain,                  Is.Null,  "a plain SVG was refused");
                Assert.That(InTheProvidersFolder(), Is.EqualTo(new[] { $"{Provider}.svg" }));

            });

        }

        #endregion

        #region WinSCPsTemporaryNameIsPutInPlaceWhenRenamed

        /// <summary>
        /// As WinSCP uploads a picture above 100 KB: under "&lt;name&gt;.filepart",
        /// renamed into place after - and only then on the display.
        /// </summary>
        [Test]
        public async Task WinSCPsTemporaryNameIsPutInPlaceWhenRenamed()
        {

            var picture         = PNG(100, 50).Concat(new Byte[150 * 1024]).ToArray();

            var (client, sftp)  = await Sftp();

            await using var _   = client;
            await using var __  = sftp;

            await sftp.UploadAsync($"/logos/providers/{Provider}.png.filepart", picture);

            var whileHeld       = Station.LogoURL(Provider, ConfiguredEMP);

            await sftp.RenameAsync($"/logos/providers/{Provider}.png.filepart", $"/logos/providers/{Provider}.png");

            Assert.Multiple(() => {
                Assert.That(whileHeld,                                     Is.EqualTo(ConfiguredEMP));
                Assert.That(InTheProvidersFolder(),                        Is.EqualTo(new[] { $"{Provider}.png" }));
                Assert.That(File.ReadAllBytes(Path.Combine(Station.LogosPath, "providers", $"{Provider}.png")), Is.EqualTo(picture));
                Assert.That(Station.LogoURL(Provider, ConfiguredEMP),      Does.StartWith($"/api/kiosk/logos/providers/{Provider}.png?v="));
            });

        }

        #endregion

        #region TheDisplayServesNothingButALogoOfAKnownName

        /// <summary>
        /// The display's port serves a logo by a name its area shows, and
        /// nothing else of the folder: not another file lying there, not a path
        /// out of it.
        /// </summary>
        [Test]
        public async Task TheDisplayServesNothingButALogoOfAKnownName()
        {

            System.IO.Directory.CreateDirectory(Path.Combine(Station.LogosPath, "providers"));
            File.WriteAllText(Path.Combine(Station.LogosPath, "providers", "notes.txt"), "not a logo");
            File.WriteAllBytes(Path.Combine(Station.LogosPath, "providers", "nobody.png"), PNG(10, 10));

            using var display = AtTheDisplay();

            var answers = new Dictionary<String, HttpStatusCode>();

            foreach (var path in new[] { "api/kiosk/logos/providers/notes.txt",
                                         "api/kiosk/logos/providers/nobody.png",
                                         "api/kiosk/logos/providers/..%2F..%2Fconfiguration.json",
                                         "api/kiosk/logos/providers/%2E%2E%5Cconfiguration.json" })
            {
                using var answer = await display.GetAsync(path);
                answers[path]    = answer.StatusCode;
            }

            // A name that is no logo is not found; a path that would climb out
            // of the folder does not even reach the display's routes.
            Assert.That(answers,
                        Is.EqualTo(new Dictionary<String, HttpStatusCode> {
                            ["api/kiosk/logos/providers/notes.txt"]                   = HttpStatusCode.NotFound,
                            ["api/kiosk/logos/providers/nobody.png"]                  = HttpStatusCode.NotFound,
                            ["api/kiosk/logos/providers/..%2F..%2Fconfiguration.json"] = HttpStatusCode.BadRequest,
                            ["api/kiosk/logos/providers/%2E%2E%5Cconfiguration.json"]  = HttpStatusCode.BadRequest
                        }));

        }

        #endregion

    }

}
