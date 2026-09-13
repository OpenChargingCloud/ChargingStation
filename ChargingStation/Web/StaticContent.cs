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
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Security.Cryptography;

using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    #region StaticFile

    /// <summary>
    /// One file of the frontend bundle, ready to be sent to a browser.
    /// </summary>
    /// <param name="RelativePath">The normalized path below the bundle root, e.g. "assets/app.3f9c1a.js".</param>
    /// <param name="Content">The file content.</param>
    /// <param name="ContentType">The HTTP content type derived from the file extension.</param>
    /// <param name="ETag">A strong entity tag of the content, including the quotes.</param>
    public sealed record StaticFile(String           RelativePath,
                                    Byte[]           Content,
                                    HTTPContentType  ContentType,
                                    String           ETag)
    {

        #region (static) Create     (RelativePath, Content)

        /// <summary>
        /// Create a static file, deriving content type and entity tag.
        /// </summary>
        public static StaticFile Create(String  RelativePath,
                                        Byte[]  Content)

            => new (RelativePath,
                    Content,
                    ContentTypeFor(RelativePath),
                    ComputeETag(Content));

        #endregion

        #region (static) ComputeETag(Content)

        /// <summary>
        /// A strong entity tag from the content: the first 16 bytes of its
        /// SHA-256 hash as lower-case hex, quoted as the header field requires.
        /// </summary>
        public static String ComputeETag(ReadOnlySpan<Byte> Content)

            => $"\"{Convert.ToHexStringLower(SHA256.HashData(Content).AsSpan(0, 16))}\"";

        #endregion

        #region (static) ContentTypeFor(Path)

        /// <summary>
        /// The content type of a file name, by its extension.
        /// </summary>
        /// <remarks>
        /// Named here rather than left to Hermod's file extension lookup: that
        /// one answers a list, is ambiguous for some extensions ("xml", "json")
        /// and knows nothing of the few web fonts and source maps a webpack
        /// bundle is made of. A browser that is told the wrong type of a module
        /// script refuses to run it, so this is worth being explicit about.
        /// </remarks>
        public static HTTPContentType ContentTypeFor(String Path)

            => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch {
                   ".html"   => HTTPContentType.Text.HTML_UTF8,
                   ".htm"    => HTTPContentType.Text.HTML_UTF8,
                   ".css"    => HTTPContentType.Text.CSS_UTF8,
                   ".js"     => HTTPContentType.Text.JAVASCRIPT_UTF8,
                   ".mjs"    => HTTPContentType.Text.JAVASCRIPT_UTF8,
                   ".json"   => HTTPContentType.Application.JSON_UTF8,
                   // Source maps are JSON, and the browser only ever fetches
                   // them with the developer tools open.
                   ".map"    => HTTPContentType.Application.JSON_UTF8,
                   ".svg"    => HTTPContentType.Image.SVG,
                   ".png"    => HTTPContentType.Image.PNG,
                   ".jpg"    => HTTPContentType.Image.JPEG,
                   ".jpeg"   => HTTPContentType.Image.JPEG,
                   ".gif"    => HTTPContentType.Image.GIF,
                   ".ico"    => HTTPContentType.Image.ICO,
                   ".woff"   => HTTPContentType.Application.WOFF,
                   ".woff2"  => HTTPContentType.Application.WOFF,
                   ".txt"    => HTTPContentType.Text.PLAIN,
                   ".xml"    => HTTPContentType.Text.XML_UTF8,
                   _         => HTTPContentType.Application.OCTETSTREAM
               };

        #endregion

    }

    #endregion

    #region IStaticContentSource

    /// <summary>
    /// Where the frontend bundle comes from: the manifest resources of this
    /// assembly (deployment) or a directory on disk (development).
    /// </summary>
    public interface IStaticContentSource
    {

        /// <summary>
        /// A human readable description, for the console at a start.
        /// </summary>
        String   Description   { get; }

        /// <summary>
        /// Whether the files can never change while the process runs.
        /// Only then may hashed assets be cached long-term by browsers.
        /// </summary>
        Boolean  IsImmutable   { get; }

        /// <summary>
        /// How many files the bundle holds; zero means there is no bundle.
        /// </summary>
        Int32    Count         { get; }

        /// <summary>
        /// The file at the given path below the bundle root.
        /// </summary>
        /// <param name="RelativePath">A slash-separated path, e.g. "assets/app.3f9c1a.js" or "index.html".</param>
        /// <param name="File">The file, when it exists.</param>
        Boolean  TryGet(String                               RelativePath,
                        [NotNullWhen(true)] out StaticFile?  File);

    }

    #endregion

    #region StaticPath

    /// <summary>
    /// The path rules shared by every content source.
    /// </summary>
    public static class StaticPath
    {

        #region (static) TryNormalize(Path, out NormalizedPath)

        /// <summary>
        /// Normalize a request path into a bundle-relative path and reject
        /// everything that could escape the bundle root or is otherwise not a
        /// plain file path.
        /// </summary>
        /// <param name="Path">The raw path, e.g. "/assets/app.js" or "assets/app.js".</param>
        /// <param name="NormalizedPath">The normalized path without leading or trailing slashes.</param>
        public static Boolean TryNormalize(String?                          Path,
                                           [NotNullWhen(true)] out String?  NormalizedPath)
        {

            NormalizedPath = null;

            if (Path is null)
                return false;

            var path = Path.Trim('/');

            if (path.Length == 0 || path.Length > 1024)
                return false;

            if (path.Contains('\\') || path.Contains('\0') || path.Contains("//", StringComparison.Ordinal))
                return false;

            foreach (var segment in path.Split('/'))
            {
                if (segment.Length == 0 || segment == "." || segment == "..")
                    return false;
            }

            NormalizedPath = path;
            return true;

        }

        #endregion

    }

    #endregion


    #region EmbeddedContentSource

    /// <summary>
    /// The frontend bundle as it was embedded into this assembly at build time.
    /// </summary>
    /// <remarks>
    /// The EmbedFrontend target of ChargingStation.csproj names every file of
    /// Frontend/dist "&lt;prefix&gt;&lt;directory&gt;.&lt;file&gt;", e.g.
    /// "cloud.charging.open.ChargingStation.HTTPRoot.assets.app.3f9c1a.js".
    /// A directory name must therefore not contain a dot, or the mapping back
    /// from a URL path would be ambiguous - which is why webpack writes into
    /// "assets/" and nothing else.
    /// </remarks>
    public sealed class EmbeddedContentSource : IStaticContentSource
    {

        #region Data

        private readonly Assembly                                 assembly;
        private readonly String                                   prefix;
        private readonly HashSet<String>                          resourceNames;
        private readonly ConcurrentDictionary<String, StaticFile>  cache = [];

        #endregion

        #region Properties

        /// <summary>
        /// A human readable description, for the console at a start.
        /// </summary>
        public String   Description
            => $"{resourceNames.Count} embedded file(s) of '{assembly.GetName().Name}'";

        /// <summary>
        /// Manifest resources cannot change while the process runs.
        /// </summary>
        public Boolean  IsImmutable
            => true;

        /// <summary>
        /// How many files the bundle holds.
        /// </summary>
        public Int32    Count
            => resourceNames.Count;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The bundle embedded under the given resource name prefix.
        /// </summary>
        /// <param name="Prefix">The resource name prefix, including its trailing dot.</param>
        /// <param name="Assembly">The assembly holding the resources; this one by default.</param>
        public EmbeddedContentSource(String     Prefix,
                                     Assembly?  Assembly   = null)
        {

            this.assembly       = Assembly ?? typeof(EmbeddedContentSource).Assembly;
            this.prefix         = Prefix;

            this.resourceNames  = assembly.GetManifestResourceNames().
                                           Where(name => name.StartsWith(prefix, StringComparison.Ordinal)).
                                           ToHashSet(StringComparer.Ordinal);

        }

        #endregion


        #region TryGet(RelativePath, out File)

        /// <summary>
        /// The file at the given path below the bundle root.
        /// </summary>
        /// <remarks>
        /// The lookup goes from the URL path to the resource name and never the
        /// other way: "assets/app.3f9c1a.js" becomes
        /// "&lt;prefix&gt;assets.app.3f9c1a.js" by turning every slash into a
        /// dot. Reading a resource name back into a path could not be done -
        /// nothing in "assets.app.3f9c1a.js" says which dots were directories
        /// and which belong to the content hash - which is why no directory
        /// below dist/ may carry a dot in its name.
        /// </remarks>
        public Boolean TryGet(String                               RelativePath,
                              [NotNullWhen(true)] out StaticFile?  File)
        {

            File = null;

            if (!StaticPath.TryNormalize(RelativePath, out var path))
                return false;

            if (cache.TryGetValue(path, out File))
                return true;

            var resourceName = prefix + path.Replace('/', '.');

            if (!resourceNames.Contains(resourceName))
                return false;

            using var stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
                return false;

            using var memory = new MemoryStream();
            stream.CopyTo(memory);

            File = cache.GetOrAdd(path, StaticFile.Create(path, memory.ToArray()));
            return true;

        }

        #endregion

    }

    #endregion

    #region FileSystemContentSource

    /// <summary>
    /// The frontend bundle as it lies in a directory on disk - "npm run watch"
    /// beside a running server, without a rebuild of the C# side.
    /// </summary>
    public sealed class FileSystemContentSource : IStaticContentSource
    {

        #region Data

        private readonly String root;

        #endregion

        #region Properties

        /// <summary>
        /// A human readable description, for the console at a start.
        /// </summary>
        public String   Description
            => $"the directory '{root}'";

        /// <summary>
        /// Files on disk change whenever webpack writes them, which is the
        /// whole point of this source.
        /// </summary>
        public Boolean  IsImmutable
            => false;

        /// <summary>
        /// How many files the directory holds right now.
        /// </summary>
        public Int32    Count
            => Directory.Exists(root)
                   ? Directory.GetFiles(root, "*", SearchOption.AllDirectories).Length
                   : 0;

        #endregion

        #region Constructor(s)

        /// <summary>
        /// The bundle in the given directory.
        /// </summary>
        public FileSystemContentSource(String Root)
        {

            if (String.IsNullOrWhiteSpace(Root))
                throw new ArgumentException("The frontend directory must not be empty!", nameof(Root));

            this.root = Path.GetFullPath(Root);

        }

        #endregion


        #region TryGet(RelativePath, out File)

        /// <summary>
        /// The file at the given path below the bundle root.
        /// </summary>
        public Boolean TryGet(String                               RelativePath,
                              [NotNullWhen(true)] out StaticFile?  File)
        {

            File = null;

            if (!StaticPath.TryNormalize(RelativePath, out var path))
                return false;

            var full = Path.GetFullPath(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));

            // Whatever the normalization above let through, the file that is
            // about to be read still has to lie below the root.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !System.IO.File.Exists(full))
            {
                return false;
            }

            try
            {
                File = StaticFile.Create(path, System.IO.File.ReadAllBytes(full));
                return true;
            }
            catch (IOException)
            {
                // Webpack was writing this very file; the browser will ask again.
                return false;
            }

        }

        #endregion

    }

    #endregion

}
