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

using System.Text;

using org.GraphDefined.Vanaheimr.Illias;
using org.GraphDefined.Vanaheimr.Hermod.HTTP;

#endregion

namespace cloud.charging.open.ChargingStation.Web
{

    /// <summary>
    /// The web interface at "/": the files of the frontend bundle, and the
    /// single-page-application stub for every other page URL.
    /// </summary>
    /// <remarks>
    /// A browser that reloads on "/logs" or follows a bookmark there has to
    /// receive the same index.html as one that started at "/" - the router in
    /// the bundle then renders the page behind that path. So anything that
    /// does not name a file of the bundle answers with the stub; anything that
    /// does name a file and is not there answers 404, because a missing
    /// stylesheet should say so and not arrive as HTML.
    ///
    /// The JSON API lives in its own HTTPAPI at "/api", and Hermod dispatches a
    /// request to the most specific API first - so an unknown /api path never
    /// reaches the stub here and gets the JSON 404 it deserves.
    /// </remarks>
    public sealed class WebFrontend : HTTPAPI
    {

        #region Data

        /// <summary>
        /// The file served for "/" and for every page URL.
        /// </summary>
        public const String IndexFile    = "index.html";

        /// <summary>
        /// What a browser asks for when it wants a tab icon and has not read
        /// the &lt;link&gt; of the page yet.
        /// </summary>
        public const String FaviconICO   = "favicon.ico";

        /// <summary>
        /// The icon this bundle actually carries.
        /// </summary>
        public const String FaviconSVG   = "favicon.svg";

        private readonly Func<String, String>?  indexTransform;
        private          String?                cachedIndex;
        private          String?                cachedIndexETag;
        private readonly Lock                   indexLock = new();

        #endregion

        #region Properties

        /// <summary>
        /// Where the bundle comes from: this assembly, or a directory on disk.
        /// </summary>
        public IStaticContentSource   Content          { get; }

        /// <summary>
        /// The security related header fields sent with every response.
        /// </summary>
        public SecurityHeaderOptions  SecurityHeaders  { get; }

        #endregion

        #region Constructor(s)

        /// <summary>
        /// Serve the given bundle within the given HTTP server.
        /// </summary>
        /// <param name="HTTPServer">The HTTP server.</param>
        /// <param name="Content">Where the bundle comes from.</param>
        /// <param name="IndexTransform">Fills the {{placeholders}} of index.html; applied once and cached.</param>
        /// <param name="SecurityHeaders">The security related header fields; a strict same-origin policy by default.</param>
        /// <param name="RootPath">The root path of the web interface, "/" by default.</param>
        public WebFrontend(HTTPServer              HTTPServer,
                           IStaticContentSource    Content,
                           Func<String, String>?   IndexTransform    = null,
                           SecurityHeaderOptions?  SecurityHeaders   = null,
                           HTTPPath?               RootPath          = null)

            : base(HTTPServer,
                   RootPath:     RootPath ?? HTTPPath.Root,
                   Description:  I18NString.Create("The web interface of this charging station"))

        {

            this.Content          = Content;
            this.indexTransform   = IndexTransform;
            this.SecurityHeaders  = SecurityHeaders ?? SecurityHeaderOptions.Default;

            RegisterURLTemplates();

        }

        #endregion


        #region (private) RegisterURLTemplates()

        private void RegisterURLTemplates()
        {

            foreach (var method in new[] { HTTPMethod.GET, HTTPMethod.HEAD })
            {
                AddHandler(HTTPPath.Root,                Serve, method);
                AddHandler(HTTPPath.Root + "{path..}",   Serve, method);
            }

        }

        #endregion

        #region (private) Serve(Request)

        /// <summary>
        /// A file of the bundle, the stub, or 404.
        /// </summary>
        private Task<HTTPResponse> Serve(HTTPRequest Request)
        {

            var path = Request.Path.ToString();

            if (Content.TryGet(path, out var file))
                return Task.FromResult(FileResponse(Request, file));

            // Browsers ask for /favicon.ico whatever the page says, and a
            // bundle built by webpack carries an SVG. Answering with it beats
            // a 404 on every visit, which is a line in the log and a broken
            // icon in the tab.
            if (path.Trim('/').Equals(FaviconICO, StringComparison.OrdinalIgnoreCase) &&
                Content.TryGet(FaviconSVG, out var icon))
            {
                return Task.FromResult(FileResponse(Request, icon));
            }

            // A path whose last segment carries an extension was after a file,
            // not after a page: say that it is not there rather than handing
            // back HTML that the browser would then fail to parse as a script.
            if (HasFileExtension(path))
                return Task.FromResult(NotFound(Request));

            return Task.FromResult(IndexResponse(Request));

        }

        #endregion


        #region (private) IndexResponse(Request)

        /// <summary>
        /// index.html with its placeholders filled in. Never cached by the
        /// browser: it is what names the hashed bundle files, so a stale one
        /// would keep a browser on yesterday's frontend forever.
        /// </summary>
        private HTTPResponse IndexResponse(HTTPRequest Request)
        {

            String html;
            String etag;

            lock (indexLock)
            {

                // A content source on disk changes under a running server, so
                // the transform is only cached for an immutable one.
                if (cachedIndex is null || cachedIndexETag is null || !Content.IsImmutable)
                {

                    if (!Content.TryGet(IndexFile, out var index))
                        return ErrorResponse(
                                   Request,
                                   HTTPStatusCode.InternalServerError,
                                   $"The frontend bundle holds no '{IndexFile}'."
                               );

                    var text          = Encoding.UTF8.GetString(index.Content);

                    cachedIndex       = indexTransform is not null
                                            ? indexTransform(text)
                                            : text;

                    cachedIndexETag   = StaticFile.ComputeETag(Encoding.UTF8.GetBytes(cachedIndex));

                }

                html  = cachedIndex;
                etag  = cachedIndexETag;

            }

            if (IsUnchanged(Request, etag))
                return NotModified(Request, etag, "no-cache");

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = HTTPContentType.Text.HTML_UTF8,
                       Content         = Encoding.UTF8.GetBytes(html),
                       ETag            = etag,
                       CacheControl    = "no-cache"
                   }.WithDocumentSecurityHeaders(SecurityHeaders).AsImmutable;

        }

        #endregion

        #region (private) FileResponse (Request, File)

        /// <summary>
        /// One file of the bundle.
        /// </summary>
        private HTTPResponse FileResponse(HTTPRequest  Request,
                                          StaticFile   File)
        {

            // Everything below assets/ carries webpack's content hash in its
            // name, so the file behind a given URL can never change: a browser
            // may keep it for a year and never ask again. Everything else -
            // the favicon, whatever else lies beside index.html - is asked
            // about on every load and answered with a 304 when it still fits.
            var immutable     = Content.IsImmutable &&
                                File.RelativePath.StartsWith("assets/", StringComparison.Ordinal);

            var cacheControl  = immutable
                                    ? "public, max-age=31536000, immutable"
                                    : "no-cache";

            if (IsUnchanged(Request, File.ETag))
                return NotModified(Request, File.ETag, cacheControl);

            return new HTTPResponse.Builder(Request) {
                       HTTPStatusCode  = HTTPStatusCode.OK,
                       ContentType     = File.ContentType,
                       Content         = File.Content,
                       ETag            = File.ETag,
                       CacheControl    = cacheControl
                   }.WithCommonSecurityHeaders(SecurityHeaders).AsImmutable;

        }

        #endregion


        #region (private static) HasFileExtension(Path)

        /// <summary>
        /// Whether the last segment of a path names a file, i.e. carries a dot
        /// that is not its first character.
        /// </summary>
        private static Boolean HasFileExtension(String Path)
        {

            var lastSegment = Path.AsSpan()[(Path.LastIndexOf('/') + 1)..];

            return lastSegment.LastIndexOf('.') > 0;

        }

        #endregion

        #region (private static) IsUnchanged(Request, ETag)

        /// <summary>
        /// Whether the browser already holds this very content.
        /// </summary>
        private static Boolean IsUnchanged(HTTPRequest  Request,
                                           String       ETag)
        {

            var ifNoneMatch = Request.GetHeaderField("If-None-Match");

            if (String.IsNullOrEmpty(ifNoneMatch))
                return false;

            if (ifNoneMatch == "*")
                return true;

            foreach (var candidate in ifNoneMatch.Split(','))
            {

                var tag = candidate.Trim();

                // A cache may weaken a tag it stored; for a file that is served
                // byte for byte, the weak form still names the same content.
                if (tag.StartsWith("W/", StringComparison.Ordinal))
                    tag = tag[2..];

                if (tag == ETag)
                    return true;

            }

            return false;

        }

        #endregion

        #region (private) NotModified/NotFound/ErrorResponse(...)

        private HTTPResponse NotModified(HTTPRequest  Request,
                                         String       ETag,
                                         String       CacheControl)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = HTTPStatusCode.NotModified,
                   ETag            = ETag,
                   CacheControl    = CacheControl
               }.WithCommonSecurityHeaders(SecurityHeaders).AsImmutable;


        private HTTPResponse NotFound(HTTPRequest Request)

            => ErrorResponse(
                   Request,
                   HTTPStatusCode.NotFound,
                   $"'{Request.Path}' is not part of this web interface."
               );


        private HTTPResponse ErrorResponse(HTTPRequest     Request,
                                           HTTPStatusCode  StatusCode,
                                           String          Message)

            => new HTTPResponse.Builder(Request) {
                   HTTPStatusCode  = StatusCode,
                   ContentType     = HTTPContentType.Text.PLAIN,
                   Content         = Encoding.UTF8.GetBytes(Message + Environment.NewLine),
                   CacheControl    = "no-store"
               }.WithCommonSecurityHeaders(SecurityHeaders).AsImmutable;

        #endregion

    }

}
