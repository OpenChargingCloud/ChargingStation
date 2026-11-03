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
using System.Security.Cryptography;

using cloud.charging.open.protocols.WWCP.Node.Web;
using cloud.charging.open.protocols.WWCP.Node.SecureShell;

using cloud.charging.open.ChargingStation.Kiosk;
using cloud.charging.open.ChargingStation.Configuration;
using cloud.charging.open.ChargingStation.Web;

#endregion

namespace cloud.charging.open.ChargingStation
{

    /// <summary>
    /// The logos the display shows - the operator's, and each e-mobility
    /// provider's - as files on this station, put up over SFTP.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two areas of the node's SFTP tree, in "logos" beside the configuration
    /// file: /logos/operator.&lt;ext&gt; and /logos/providers/&lt;provider id&gt;.&lt;ext&gt;,
    /// read with "providers:read" and written with "providers:edit". A file is
    /// bound to its provider by its name, and a name that is no provider's is
    /// refused before a byte is sent; one picture to a provider, so that
    /// ionity.svg put in place takes away an ionity.png. What a file holds is
    /// checked before it is put in place - see <see cref="Logos"/>.
    /// </para>
    /// <para>
    /// A file wins over the configuration file's "logo", which stays as what
    /// is shown where there is no file: a URL somewhere else, or a data: URI.
    /// The display fetches the file from its own port, without a sign-in -
    /// the screen is public and shows the picture anyway - under a URL that
    /// changes with what the file holds, so that a new logo is on the screen
    /// at the next swipe and nothing has to be told.
    /// </para>
    /// </remarks>
    public partial class ChargingStation
    {

        #region Data

        /// <summary>
        /// Where the logos are kept, beside the configuration file.
        /// </summary>
        public const String  DefaultLogosDirectory  = "logos";

        /// <summary>
        /// The folder of the operator's logo over SFTP.
        /// </summary>
        public const String  LogosArea              = "logos";

        /// <summary>
        /// The folder of the providers' logos over SFTP.
        /// </summary>
        public const String  ProviderLogosArea      = "logos/providers";

        /// <summary>
        /// What the operator's logo is called, before its extension.
        /// </summary>
        public const String  OperatorLogoName       = "operator";

        /// <summary>
        /// Where the display fetches a logo, below the display's API.
        /// </summary>
        public const String  KioskLogosPath         = "kiosk/logos";

        /// <summary>
        /// What each logo file held when it was last looked at - its length
        /// and time, and its SHA-256 - so that a display asking every few
        /// seconds does not have the file read every time.
        /// </summary>
        private readonly ConcurrentDictionary<String, (Int64 Length, DateTime Written, String SHA256)>  logoHashes  = new ();

        #endregion

        #region Properties

        /// <summary>
        /// Where the logos are kept: beside the configuration file.
        /// </summary>
        public String  LogosPath
            => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ConfigFile.Path))!, DefaultLogosDirectory);

        #endregion


        #region (private) AddLogoAreas()

        /// <summary>
        /// The logos' areas of the node's SFTP tree - before the start.
        /// </summary>
        private void AddLogoAreas()
        {

            var operatorLogos  = new SftpArea(LogosArea,
                                              LogosPath,
                                              name => Logos.Split(name, out var stem, out _) && stem == OperatorLogoName,
                                              Read:   Permission.Read(StationAccess.Providers),
                                              Write:  Permission.Edit(StationAccess.Providers)) {
                                     AcceptName     = name => OperatorLogoNameWhyNot(name),
                                     AcceptContent  = Logos.WhyNot,
                                     Replaces       = OtherKindsOf,
                                     MaxFileSize    = Logos.MaxFileSize,
                                     Tags           = [ StationAccess.Providers ]
                                 };

            var providerLogos  = new SftpArea(ProviderLogosArea,
                                              Path.Combine(LogosPath, "providers"),
                                              name => Logos.Split(name, out var stem, out _) && ProviderOf(stem) is not null,
                                              Read:   Permission.Read(StationAccess.Providers),
                                              Write:  Permission.Edit(StationAccess.Providers)) {
                                     AcceptName     = name => ProviderLogoNameWhyNot(name),
                                     AcceptContent  = Logos.WhyNot,
                                     Replaces       = OtherKindsOf,
                                     MaxFileSize    = Logos.MaxFileSize,
                                     Tags           = [ StationAccess.Providers ]
                                 };

            operatorLogos.Changed += LogoChanged;
            providerLogos.Changed += LogoChanged;

            AddSftpArea(operatorLogos);
            AddSftpArea(providerLogos);

        }

        #endregion

        #region (private) OperatorLogoNameWhyNot(Name) / ProviderLogoNameWhyNot(Name) / OtherKindsOf(Name)

        /// <summary>
        /// Why a file of the given name may not be the operator's logo, or null.
        /// </summary>
        private static String? OperatorLogoNameWhyNot(String Name)

            => !Logos.Split(Name, out var stem, out _)
                   ? $"A logo is a {Logos.KindsSaid} file, and '{Name}' is none."
                   : stem != OperatorLogoName
                         ? $"/{LogosArea} holds the operator's logo alone, '{OperatorLogoName}' with a {Logos.KindsSaid}: a provider's goes into /{ProviderLogosArea}, under its id."
                         : null;

        /// <summary>
        /// Why a file of the given name may not be a provider's logo, or null:
        /// its name before the extension must be the id of a provider of this
        /// station.
        /// </summary>
        private String? ProviderLogoNameWhyNot(String Name)
        {

            if (!Logos.Split(Name, out var stem, out _))
                return $"A logo is a {Logos.KindsSaid} file, and '{Name}' is none.";

            if (ProviderOf(stem) is not null)
                return null;

            var known = (Operator.EMPs ?? []).Select(provider => $"'{provider.Id}'").ToList();

            return known.Count == 0
                       ? $"'{stem}' is no e-mobility provider of this station, which knows none: they are named in the configuration file's \"operator\" section."
                       : $"'{stem}' is no e-mobility provider of this station; its providers are {String.Join(", ", known)}.";

        }

        /// <summary>
        /// The other files of the same logo: one picture to a provider.
        /// </summary>
        private static IEnumerable<String> OtherKindsOf(String Name)

            => Logos.Split(Name, out var stem, out _)
                   ? Logos.Kinds.Select(kind => stem + kind.Extension).Where(other => other != Name)
                   : [];

        #endregion

        #region (private) ProviderOf(Id)

        /// <summary>
        /// The provider of the given id, exactly so, or null.
        /// </summary>
        private EMobilityProvider? ProviderOf(String Id)

            => Operator.EMPs?.FirstOrDefault(provider => provider.Id == Id);

        #endregion

        #region (private) LogoChanged(Changed)

        /// <summary>
        /// A logo put up or taken away: said, and shown from the next time the
        /// display asks.
        /// </summary>
        private void LogoChanged(SftpAreaChanged Changed)
        {

            logoHashes.TryRemove(Changed.Area.FileOf(Changed.Name), out _);

            if (!Logos.Split(Changed.Name, out var stem, out _))
                return;

            var whose = Changed.Area.Path == LogosArea
                            ? "the operator's logo"
                            : $"the logo of '{ProviderOf(stem)?.Name ?? stem}'";

            Log.Info(Changed.Change == SftpAreaChange.PutInPlace
                         ? $"The display shows {whose} from '{Changed.Name}' from the next time it asks."
                         : $"'{Changed.Name}' is no longer {whose}: the display shows {(LogoFileOf(Changed.Area.Path, stem) is not null ? "the file left" : "what the configuration file says")} instead.",
                     "sftp", StationAccess.Providers, "kiosk");

        }

        #endregion


        #region LogoURL(ProviderId, Configured)

        /// <summary>
        /// What the display is to show as a logo: the file of the given
        /// provider - or of the operator, for none - where there is one, under a
        /// URL that changes with what it holds; what the configuration file
        /// says otherwise.
        /// </summary>
        /// <param name="ProviderId">The provider, or null for the operator.</param>
        /// <param name="Configured">What the configuration file says of its logo.</param>
        public String? LogoURL(String?  ProviderId,
                               String?  Configured)
        {

            var area = ProviderId is null ? LogosArea : ProviderLogosArea;

            if (LogoFileOf(area, ProviderId ?? OperatorLogoName) is not (String file, String name) ||
                HashOf(file) is not String sha256)
                return Configured;

            return $"{KioskHTTPAPI.DefaultAPIPath}/{KioskLogosPath}/{(ProviderId is null ? "" : "providers/")}{Uri.EscapeDataString(name)}?v={sha256[..8]}";

        }

        #endregion

        #region LogoFile(Area, Name)

        /// <summary>
        /// The file of a logo as the display fetches it - its area, and its
        /// name there - with what it is served as and its SHA-256: only a name
        /// the area shows, and only where it is there.
        /// </summary>
        /// <param name="Area">The area: <see cref="LogosArea"/> or <see cref="ProviderLogosArea"/>.</param>
        /// <param name="Name">The file's name in it.</param>
        public (String File, String ContentType, String SHA256)? LogoFile(String  Area,
                                                                          String  Name)
        {

            if (SftpAreas.FirstOrDefault(area => area.Path == Area) is not SftpArea area ||
                !area.ShowsName(Name) ||
                !Logos.Split(Name, out _, out var contentType))
                return null;

            var file = area.FileOf(Name);

            return File.Exists(file) && HashOf(file) is String sha256
                       ? (file, contentType, sha256)
                       : null;

        }

        #endregion

        #region (private) LogoFileOf(Area, Stem) / HashOf(File)

        /// <summary>
        /// The file of the given logo in the given area, of whichever kind is
        /// there, and its name - or null.
        /// </summary>
        private (String File, String Name)? LogoFileOf(String  Area,
                                                       String  Stem)
        {

            if (SftpAreas.FirstOrDefault(area => area.Path == Area) is not SftpArea area)
                return null;

            foreach (var (extension, _) in Logos.Kinds)
            {

                var name = Stem + extension;

                if (!area.ShowsName(name))
                    continue;

                var file = area.FileOf(name);

                if (File.Exists(file))
                    return (file, name);

            }

            return null;

        }

        /// <summary>
        /// The SHA-256 of the given file, read again only where its length or
        /// its time changed - or null, where it cannot be read.
        /// </summary>
        private String? HashOf(String File)
        {

            try
            {

                var info = new FileInfo(File);

                if (logoHashes.TryGetValue(File, out var known) &&
                    known.Length  == info.Length &&
                    known.Written == info.LastWriteTimeUtc)
                    return known.SHA256;

                using var content = info.OpenRead();

                var sha256 = Convert.ToHexStringLower(SHA256.HashData(content));

                logoHashes[File] = (info.Length, info.LastWriteTimeUtc, sha256);

                return sha256;

            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                return null;
            }

        }

        #endregion

    }

}
