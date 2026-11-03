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

using System.Buffers.Binary;
using System.Text.RegularExpressions;
using System.Xml;

#endregion

namespace cloud.charging.open.ChargingStation.Kiosk
{

    /// <summary>
    /// What a logo on the display may be, as a file: a PNG, a JPEG, a WebP or
    /// an SVG, whose content is what its name says it is - and, for a picture
    /// of pixels, no larger than the screen could use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The display is public, and a picture on it is fetched by a browser on a
    /// screen in a car park. So what is accepted is short and boring: the
    /// first bytes say the format the name says, the header says how large
    /// the picture is, and an SVG is a picture and nothing else - no script,
    /// no foreign HTML, no handler of an event, and nothing it would fetch
    /// from anywhere. What remains is served with a policy that would stop
    /// the rest anyway; the check is there so that a refusal is said to whoever
    /// uploads, rather than a screen silently showing nothing.
    /// </para>
    /// <para>
    /// The size of a file is the area's to limit, before a byte too many is
    /// written - see <see cref="MaxFileSize"/>.
    /// </para>
    /// </remarks>
    public static partial class Logos
    {

        #region Data

        /// <summary>
        /// The largest file a logo may be.
        /// </summary>
        public const Int64  MaxFileSize  = 512 * 1024;

        /// <summary>
        /// The most pixels a logo may be wide, and high.
        /// </summary>
        public const Int32  MaxPixels    = 2048;

        /// <summary>
        /// The extensions a logo may have, the one preferred first, and what it
        /// is served as.
        /// </summary>
        public static readonly IReadOnlyList<(String Extension, String ContentType)>  Kinds = [
                                                                                          (".svg",   "image/svg+xml"),
                                                                                          (".png",   "image/png"),
                                                                                          (".webp",  "image/webp"),
                                                                                          (".jpg",   "image/jpeg"),
                                                                                          (".jpeg",  "image/jpeg")
                                                                                      ];

        /// <summary>
        /// The extensions, as a sentence names them.
        /// </summary>
        public const String  KindsSaid  = ".png, .jpg, .jpeg, .webp or .svg";

        #endregion


        #region (static) Split(Name, out Stem, out ContentType)

        /// <summary>
        /// Whether the given name ends with the extension of a logo - exactly
        /// so, in lower case - and what is before it, and what it is served as.
        /// </summary>
        public static Boolean Split(String       Name,
                                    out String   Stem,
                                    out String   ContentType)
        {

            foreach (var (extension, contentType) in Kinds)
            {
                if (Name.Length > extension.Length && Name.EndsWith(extension, StringComparison.Ordinal))
                {
                    Stem        = Name[..^extension.Length];
                    ContentType = contentType;
                    return true;
                }
            }

            Stem        = "";
            ContentType = "";

            return false;

        }

        #endregion

        #region (static) WhyNot(Name, File)

        /// <summary>
        /// Why the given file may not be the logo of the given name, as a
        /// sentence - or null, where it may.
        /// </summary>
        /// <param name="Name">The name it is to have: "ionity.svg".</param>
        /// <param name="File">Where it is.</param>
        public static String? WhyNot(String  Name,
                                     String  File)
        {

            if (!Split(Name, out _, out var contentType))
                return $"A logo is a {KindsSaid} file, and '{Name}' is none.";

            Byte[] content;

            try
            {
                content = System.IO.File.ReadAllBytes(File);
            }
            catch (Exception problem) when (problem is IOException or UnauthorizedAccessException)
            {
                return $"'{Name}' could not be read here: {problem.Message}";
            }

            if (content.Length == 0)
                return $"'{Name}' is empty.";

            if (content.Length > MaxFileSize)
                return $"'{Name}' is larger than the {MaxFileSize / 1024} KiB a logo may be.";

            return contentType switch {
                       "image/png"      => Pixels(Name, "PNG",  PNGSize (content)),
                       "image/jpeg"     => Pixels(Name, "JPEG", JPEGSize(content)),
                       "image/webp"     => Pixels(Name, "WebP", WebPSize(content)),
                       "image/svg+xml"  => SVGWhyNot(Name, content),
                       _                => $"A logo is a {KindsSaid} file, and '{Name}' is none."
                   };

        }

        #endregion


        #region (private static) Pixels(Name, Format, Size)

        /// <summary>
        /// Why a picture of the given size may not be a logo - not the format
        /// its name says, or larger than a screen could use - or null.
        /// </summary>
        private static String? Pixels(String                       Name,
                                      String                       Format,
                                      (Int32 Width, Int32 Height)? Size)

            => Size is not (Int32 width, Int32 height)
                   ? $"'{Name}' is no {Format} picture, as its name says it is."
                   : width < 1 || height < 1
                         ? $"'{Name}' says it is {width}×{height} pixels, which is no picture."
                         : width > MaxPixels || height > MaxPixels
                               ? $"'{Name}' is {width}×{height} pixels; a logo may be {MaxPixels}×{MaxPixels} at most."
                               : null;

        #endregion

        #region (private static) PNGSize(Content) / JPEGSize(Content) / WebPSize(Content)

        private static readonly Byte[] PNGSignature = [ 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A ];

        /// <summary>
        /// The size a PNG's header says - its signature, then the IHDR chunk -
        /// or null where it is no PNG.
        /// </summary>
        private static (Int32 Width, Int32 Height)? PNGSize(ReadOnlySpan<Byte> Content)

            => Content.Length >= 24 &&
               Content[..8].SequenceEqual(PNGSignature) &&
               Content.Slice(12, 4).SequenceEqual("IHDR"u8)
                   ? ((Int32) Math.Min(BinaryPrimitives.ReadUInt32BigEndian(Content.Slice(16, 4)), Int32.MaxValue),
                      (Int32) Math.Min(BinaryPrimitives.ReadUInt32BigEndian(Content.Slice(20, 4)), Int32.MaxValue))
                   : null;

        /// <summary>
        /// The size a JPEG's frame header says - the first start-of-frame
        /// marker after the start of the image - or null where it is no JPEG.
        /// </summary>
        private static (Int32 Width, Int32 Height)? JPEGSize(ReadOnlySpan<Byte> Content)
        {

            if (Content.Length < 4 || Content[0] != 0xFF || Content[1] != 0xD8 || Content[2] != 0xFF)
                return null;

            var at = 2;

            while (at + 4 <= Content.Length)
            {

                if (Content[at] != 0xFF)
                    return null;

                var marker = Content[at + 1];

                // Fill bytes before a marker.
                if (marker == 0xFF)
                {
                    at++;
                    continue;
                }

                // Markers without a length.
                if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
                {
                    at += 2;
                    continue;
                }

                // The end of the image, or the start of the scan, before a frame.
                if (marker is 0xD9 or 0xDA)
                    return null;

                var length = BinaryPrimitives.ReadUInt16BigEndian(Content.Slice(at + 2, 2));

                if (length < 2)
                    return null;

                // A start of frame: every SOFn but DHT, JPG and DAC.
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return at + 9 <= Content.Length
                               ? (BinaryPrimitives.ReadUInt16BigEndian(Content.Slice(at + 7, 2)),
                                  BinaryPrimitives.ReadUInt16BigEndian(Content.Slice(at + 5, 2)))
                               : null;

                at += 2 + length;

            }

            return null;

        }

        /// <summary>
        /// The size a WebP's first chunk says - lossy, lossless or extended -
        /// or null where it is no WebP.
        /// </summary>
        private static (Int32 Width, Int32 Height)? WebPSize(ReadOnlySpan<Byte> Content)
        {

            if (Content.Length < 30 ||
                !Content[..4].  SequenceEqual("RIFF"u8) ||
                !Content[8..12].SequenceEqual("WEBP"u8))
                return null;

            var chunk = Content[12..16];

            if (chunk.SequenceEqual("VP8 "u8))
                return Content[23] == 0x9D && Content[24] == 0x01 && Content[25] == 0x2A
                           ? (BinaryPrimitives.ReadUInt16LittleEndian(Content.Slice(26, 2)) & 0x3FFF,
                              BinaryPrimitives.ReadUInt16LittleEndian(Content.Slice(28, 2)) & 0x3FFF)
                           : null;

            if (chunk.SequenceEqual("VP8L"u8))
            {

                if (Content[20] != 0x2F)
                    return null;

                var bits = BinaryPrimitives.ReadUInt32LittleEndian(Content.Slice(21, 4));

                return ((Int32) (bits & 0x3FFF) + 1, (Int32) ((bits >> 14) & 0x3FFF) + 1);

            }

            if (chunk.SequenceEqual("VP8X"u8))
                return (1 + (Content[24] | Content[25] << 8 | Content[26] << 16),
                        1 + (Content[27] | Content[28] << 8 | Content[29] << 16));

            return null;

        }

        #endregion

        #region (private static) SVGWhyNot(Name, Content)

        [GeneratedRegex(@"url\s*\(\s*['""]?\s*(?!#)", RegexOptions.IgnoreCase)]
        private static partial Regex OutsideURL();

        /// <summary>
        /// Why the given SVG may not be a logo, or null: it must be XML without
        /// a document type, its root an svg, and nothing in it may run, hold
        /// foreign markup, handle an event or reach outside itself.
        /// </summary>
        private static String? SVGWhyNot(String  Name,
                                         Byte[]  Content)
        {

            var settings = new XmlReaderSettings {
                               DtdProcessing                 = DtdProcessing.Prohibit,
                               XmlResolver                   = null,
                               IgnoreProcessingInstructions  = true,
                               IgnoreComments                = true,
                               MaxCharactersInDocument       = MaxFileSize * 4
                           };

            try
            {

                using var stream = new MemoryStream(Content, writable: false);
                using var reader = XmlReader.Create(stream, settings);

                var root = true;

                while (reader.Read())
                {

                    switch (reader.NodeType)
                    {

                        case XmlNodeType.Element:

                            if (root)
                            {

                                if (reader.LocalName != "svg")
                                    return $"'{Name}' is XML, but no SVG: its root is <{reader.Name}>.";

                                root = false;

                            }

                            if (reader.LocalName.Equals("script",        StringComparison.OrdinalIgnoreCase) ||
                                reader.LocalName.Equals("foreignObject", StringComparison.OrdinalIgnoreCase) ||
                                reader.LocalName.Equals("iframe",        StringComparison.OrdinalIgnoreCase))
                                return $"'{Name}' holds a <{reader.LocalName}>, which a logo may not.";

                            if (reader.MoveToFirstAttribute())
                            {
                                do
                                {

                                    if (reader.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                                        return $"'{Name}' handles an event ({reader.Name}), which a logo may not.";

                                    if (reader.LocalName == "href" && !reader.Value.TrimStart().StartsWith('#'))
                                        return $"'{Name}' refers to '{Shortened(reader.Value)}', outside itself, which a logo may not.";

                                    if (OutsideURL().IsMatch(reader.Value) || reader.Value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                                        return $"'{Name}' fetches something from outside itself ({reader.Name}), which a logo may not.";

                                }
                                while (reader.MoveToNextAttribute());

                                reader.MoveToElement();

                            }

                            break;

                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:

                            if (OutsideURL().IsMatch(reader.Value) || reader.Value.Contains("@import", StringComparison.OrdinalIgnoreCase))
                                return $"'{Name}' fetches something from outside itself in a style, which a logo may not.";

                            break;

                    }

                }

                return root
                           ? $"'{Name}' holds no SVG."
                           : null;

            }
            catch (XmlException problem)
            {
                return $"'{Name}' is no SVG that can be read: {problem.Message}";
            }

        }

        private static String Shortened(String Text)

            => Text.Length <= 60 ? Text : Text[..57] + "...";

        #endregion

    }

}
