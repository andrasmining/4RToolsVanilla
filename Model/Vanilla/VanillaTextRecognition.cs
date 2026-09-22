using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Tesseract;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaTextWord
    {
        internal string Text { get; set; }
        internal Rectangle Bounds { get; set; }
        internal float Confidence { get; set; }
    }

    internal sealed class VanillaTextLine
    {
        internal string Text { get; set; }
        internal Rectangle Bounds { get; set; }
        internal float Confidence { get; set; }
        internal VanillaTextWord[] Words { get; set; }
    }

    /// <summary>
    /// Local, bounded OCR of captured pixels. No desktop capture, input, files containing
    /// recognized text, network request or expected-word whitelist is used here. Callers
    /// must compare the observed text and independently establish the control's role.
    /// </summary>
    internal static class VanillaTextRecognition
    {
        private static readonly object Gate = new object();
        private static TesseractEngine engine;
        private static TesseractEngine accurateEngine;
        private static TesseractEngine digitsEngine;

        internal static bool TryRead(Bitmap bitmap, Rectangle area, bool singleLine,
            out VanillaTextLine[] lines, out string evidence)
        {
            return TryReadCore(bitmap, area, singleLine, out lines, out evidence, false);
        }

        internal static bool TryReadAccurate(Bitmap bitmap, Rectangle area, bool singleLine,
            out VanillaTextLine[] lines, out string evidence)
        {
            lines = new VanillaTextLine[0];
            evidence = "accurate OCR requires a bounded detected text control";
            if (!singleLine || area.Height > 80 || area.Width > 1200) return false;
            return TryReadCore(bitmap, area, singleLine, out lines, out evidence, true);
        }

        internal static bool TryReadPixelPreservingLine(Bitmap bitmap, Rectangle area,
            out VanillaTextLine[] lines, out string evidence)
        {
            lines = new VanillaTextLine[0];
            evidence = "pixel-preserving OCR requires a bounded observed text line";
            if (area.Width > 1200 || area.Height > 80) return false;
            // Nearest-neighbor enlargement retains the original small glyph gaps.
            // Synthetic degraded service captures verified this fixed fourfold path.
            return TryReadCore(bitmap, area, true, out lines, out evidence, true, 4);
        }

        internal static bool TryReadDigitsPixelPreserving(Bitmap bitmap, Rectangle area,
            out VanillaTextLine[] lines, out string evidence)
        {
            lines = new VanillaTextLine[0];
            evidence = "digit OCR requires a bounded observed numeric field";
            if (area.Width > 400 || area.Height > 80) return false;

            // The caller already crops to the observed numeric glyph bounds. Windows
            // Tesseract benefits from enlargement for some short numbers (for example
            // 123), while repeated narrow glyphs such as 9999 can be distorted by that
            // same enlargement. Read both independent pixel scales and accept only one
            // unambiguous numeric interpretation. If both scales recognize digits they
            // must agree exactly; disagreement is unknown and therefore authorizes no
            // quantity input.
            var candidates = new List<Tuple<string, VanillaTextLine>>();
            var attempts = new List<string>();
            foreach (int scale in new[] { 1, 4 })
            foreach (PageSegMode mode in new[] { PageSegMode.SingleWord, PageSegMode.SingleLine })
            {
                VanillaTextLine[] observed;
                string attemptEvidence;
                bool completed = TryReadCore(bitmap, area, true, out observed, out attemptEvidence,
                    true, scale, true, mode);
                string profile = scale + "x/" + mode;
                attempts.Add(profile + "=" + attemptEvidence);
                if (!completed || observed == null || observed.Length != 1) continue;
                string text = (observed[0].Text ?? string.Empty).Trim();
                if (text.Length == 0 || text.Any(ch => ch < '0' || ch > '9')) continue;
                candidates.Add(Tuple.Create(profile, observed[0]));
            }
            if (candidates.Count == 0)
            {
                evidence = "digit OCR produced no numeric observations; " + string.Join("; ", attempts);
                return false;
            }

            // Digit count is independently observable from the reconstructed monochrome
            // glyph image. Use it to reject OCR hallucinations that add/drop digits when
            // enlargement changes a narrow repeated glyph (the 9999 regression). This is
            // not character recognition: it only counts bounded connected ink components.
            int glyphCount = CountDigitGlyphs(bitmap, area);
            var geometryMatched = glyphCount > 0 && glyphCount <= 7
                ? candidates.Where(candidate => candidate.Item2.Text.Trim().Length == glyphCount).ToList()
                : new List<Tuple<string, VanillaTextLine>>();
            var votingPool = geometryMatched.Count > 0 ? geometryMatched : candidates;

            var groups = votingPool
                .GroupBy(candidate => candidate.Item2.Text.Trim(), StringComparer.Ordinal)
                .Select(group => new
                {
                    Text = group.Key,
                    Count = group.Count(),
                    Members = group.ToArray(),
                    Confidence = group.Max(candidate => candidate.Item2.Confidence)
                })
                .OrderByDescending(group => group.Count)
                .ThenByDescending(group => group.Confidence)
                .ToArray();
            bool uniqueConsensus = groups[0].Count >= 2
                && !(groups.Length > 1 && groups[1].Count == groups[0].Count);
            bool geometryBackedSingle = geometryMatched.Count == 1
                && geometryMatched[0].Item2.Confidence >= 65;

            if (!uniqueConsensus && !geometryBackedSingle)
            {
                evidence = "digit OCR had no unique geometry-backed consensus; glyphs=" + glyphCount
                    + "; observations=" + string.Join(",", candidates.Select(candidate => candidate.Item1 + ":" + candidate.Item2.Text.Trim()));
                return false;
            }

            var consensus = geometryBackedSingle
                ? new { Text = geometryMatched[0].Item2.Text.Trim(), Count = 1,
                    Members = new[] { geometryMatched[0] }, Confidence = geometryMatched[0].Item2.Confidence }
                : groups[0];
            VanillaTextLine winner = consensus.Members
                .OrderByDescending(candidate => candidate.Item2.Confidence)
                .ThenBy(candidate => candidate.Item1, StringComparer.Ordinal)
                .First().Item2;
            lines = new[] { winner };
            evidence = "digit OCR consensus=" + consensus.Count + "/" + candidates.Count
                + "; glyphs=" + glyphCount + "; valueLength=" + consensus.Text.Length
                + "; agreeingProfiles=" + string.Join(",", consensus.Members.Select(candidate => candidate.Item1));
            return true;
        }

        private static int CountDigitGlyphs(Bitmap bitmap, Rectangle area)
        {
            if (bitmap == null || area.Width < 1 || area.Height < 1) return 0;
            using (var copy = bitmap.Clone(area, PixelFormat.Format24bppRgb))
            {
                int width = copy.Width, height = copy.Height;
                var dark = new bool[width * height];
                BitmapData data = copy.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    byte[] row = new byte[Math.Abs(data.Stride)];
                    for (int y = 0; y < height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < width; x++)
                        {
                            int i = x * 3;
                            int luminance = (row[i] * 114 + row[i + 1] * 587 + row[i + 2] * 299) / 1000;
                            dark[y * width + x] = luminance < 210;
                        }
                    }
                }
                finally { copy.UnlockBits(data); }

                int[] queue = new int[dark.Length];
                int glyphs = 0;
                int minimumArea = Math.Max(2, area.Height / 8);
                for (int seed = 0; seed < dark.Length; seed++)
                {
                    if (!dark[seed]) continue;
                    dark[seed] = false;
                    int head = 0, tail = 0, pixels = 0;
                    queue[tail++] = seed;
                    while (head < tail)
                    {
                        int point = queue[head++], y = point / width, x = point - y * width;
                        pixels++;
                        for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            int next = ny * width + nx;
                            if (!dark[next]) continue;
                            dark[next] = false;
                            queue[tail++] = next;
                        }
                    }
                    if (pixels >= minimumArea) glyphs++;
                    if (glyphs > 7) return glyphs;
                }
                return glyphs;
            }
        }

        internal static bool TryReadCompactLine(Bitmap bitmap, Rectangle area,
            out VanillaTextLine[] lines, out string evidence)
        {
            lines = new VanillaTextLine[0];
            evidence = "compact OCR requires a bounded observed text line";
            if (area.Width > 400 || area.Height > 60) return false;
            // A fixed integral scale preserves the separate stems in small names such
            // as Manila. Fractional bicubic enlargement can join those stems into 'd'.
            return TryReadCore(bitmap, area, true, out lines, out evidence, false, 3);
        }

        private static bool TryReadCore(Bitmap bitmap, Rectangle area, bool singleLine,
            out VanillaTextLine[] lines, out string evidence, bool accurate, int pixelScale = 0,
            bool digitsOnly = false, PageSegMode? pageModeOverride = null)
        {
            lines = new VanillaTextLine[0];
            evidence = "OCR region is invalid";
            if (bitmap == null || area.Width < 3 || area.Height < 3
                || bitmap.Width > 8192 || bitmap.Height > 8192
                || !new Rectangle(Point.Empty, bitmap.Size).Contains(area)) return false;
            try
            {
                // The wrapper's default loader inspects Assembly.Location before its
                // application-directory fallback. An embedded assembly can have an empty
                // location; explicitly bind native dependencies to this portable package.
                lock (Gate) TesseractEnviornment.CustomSearchPath = AppDomain.CurrentDomain.BaseDirectory;
                // Small native UI text needs enlargement. Bound both dimensions and total
                // pixels; a full-screen character scan remains bounded on a 4K desktop.
                double scale = singleLine ? Math.Min(4, 60.0 / area.Height) : 3.0;
                if (pixelScale > 0) scale = pixelScale;
                scale = Math.Min(scale, Math.Min(4096.0 / area.Width, 3072.0 / area.Height));
                scale = Math.Min(scale, Math.Sqrt(8000000.0 / ((double)area.Width * area.Height)));
                int width = Math.Max(3, (int)Math.Round(area.Width * scale));
                int height = Math.Max(3, (int)Math.Round(area.Height * scale));
                const int padding = 12;
                using (var prepared = new Bitmap(width + padding * 2, height + padding * 2, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(prepared))
                    using (var attributes = new ImageAttributes())
                    {
                        graphics.Clear(Color.White);
                        graphics.InterpolationMode = pixelScale > 0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // Grayscale removes colored selection/background fringing without
                        // fabricating or substituting any character in the recognized text.
                        attributes.SetColorMatrix(new ColorMatrix(new[] {
                            new[] { .299f, .299f, .299f, 0f, 0f },
                            new[] { .587f, .587f, .587f, 0f, 0f },
                            new[] { .114f, .114f, .114f, 0f, 0f },
                            new[] { 0f, 0f, 0f, 1f, 0f },
                            new[] { 0f, 0f, 0f, 0f, 1f }
                        }));
                        graphics.DrawImage(bitmap, new Rectangle(padding, padding, width, height),
                            area.X, area.Y, area.Width, area.Height, GraphicsUnit.Pixel, attributes);
                    }
                    using (var stream = new MemoryStream())
                    {
                        prepared.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                        using (Pix pix = Pix.LoadFromMemory(stream.ToArray()))
                        {
                            lock (Gate)
                            {
                                TesseractEngine activeEngine = digitsOnly ? digitsEngine : (accurate ? accurateEngine : engine);
                                if (activeEngine == null)
                                {
                                    activeEngine = new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                        accurate ? "tessdata-best" : "tessdata"), "eng", EngineMode.LstmOnly);
                                    activeEngine.SetVariable("user_defined_dpi", 300);
                                    // Do not adapt the model from another account's username.
                                    activeEngine.SetVariable("classify_enable_learning", 0);
                                    if (digitsOnly)
                                    {
                                        activeEngine.SetVariable("tessedit_char_whitelist", "0123456789");
                                        activeEngine.SetVariable("classify_bln_numeric_mode", 1);
                                    }
                                    if (digitsOnly) digitsEngine = activeEngine;
                                    else if (accurate) accurateEngine = activeEngine;
                                    else engine = activeEngine;
                                }
                                PageSegMode pageMode = pageModeOverride
                                    ?? (digitsOnly ? PageSegMode.SingleWord
                                        : singleLine ? PageSegMode.SingleLine : PageSegMode.SparseText);
                                using (Page page = activeEngine.Process(pix, pageMode))
                                using (ResultIterator iterator = page.GetIterator())
                                {
                                    var output = new List<VanillaTextLine>();
                                    var words = new List<VanillaTextWord>();
                                    iterator.Begin();
                                    do
                                    {
                                        if (iterator.IsAtBeginningOf(PageIteratorLevel.TextLine) && words.Count > 0)
                                        { output.Add(MakeLine(words)); words.Clear(); }
                                        Rect bounds;
                                        string text = iterator.GetText(PageIteratorLevel.Word);
                                        if (!string.IsNullOrWhiteSpace(text) && iterator.TryGetBoundingBox(PageIteratorLevel.Word, out bounds))
                                        {
                                            Rectangle mapped = Rectangle.FromLTRB(
                                                area.Left + (int)Math.Floor((bounds.X1 - padding) * area.Width / (double)width),
                                                area.Top + (int)Math.Floor((bounds.Y1 - padding) * area.Height / (double)height),
                                                area.Left + (int)Math.Ceiling((bounds.X2 - padding) * area.Width / (double)width),
                                                area.Top + (int)Math.Ceiling((bounds.Y2 - padding) * area.Height / (double)height));
                                            mapped.Intersect(area);
                                            if (mapped.Width > 0 && mapped.Height > 0)
                                                words.Add(new VanillaTextWord { Text = text.Trim(), Bounds = mapped,
                                                    Confidence = iterator.GetConfidence(PageIteratorLevel.Word) });
                                        }
                                        if (output.Count > 256 || words.Count > 128)
                                        { evidence = "OCR exceeded the bounded text count"; return false; }
                                    } while (iterator.Next(PageIteratorLevel.Word));
                                    if (words.Count > 0) output.Add(MakeLine(words));
                                    lines = output.ToArray();
                                }
                            }
                        }
                    }
                }
                evidence = "local " + (accurate ? "accurate " : "") + "OCR completed; lines=" + lines.Length;
                return true;
            }
            catch (Exception ex)
            {
                // Exception messages from an OCR implementation need not be free of text.
                // Keep diagnostics useful without exposing recognized credentials.
                var causes = new List<string>();
                Exception cause = ex;
                while (cause != null && causes.Count < 4)
                { causes.Add(cause.GetType().Name); cause = cause.InnerException; }
                string native = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Environment.Is64BitProcess ? "x64" : "x86");
                evidence = "local OCR unavailable: " + string.Join(" -> ", causes)
                    + "; English model=" + File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, accurate ? "tessdata-best" : "tessdata", "eng.traineddata"))
                    + "; native engine=" + File.Exists(Path.Combine(native, "tesseract50.dll"))
                    + "; native image library=" + File.Exists(Path.Combine(native, "leptonica-1.82.0.dll"));
                return false;
            }
        }

        private static VanillaTextLine MakeLine(List<VanillaTextWord> words)
        {
            Rectangle bounds = words[0].Bounds;
            foreach (VanillaTextWord word in words.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
            return new VanillaTextLine { Text = string.Join(" ", words.Select(word => word.Text)), Bounds = bounds,
                Confidence = words.Average(word => word.Confidence), Words = words.ToArray() };
        }
    }

    internal sealed class VanillaRecognitionPixels
    {
        private readonly byte[] pixels;
        internal readonly int Width, Height;
        internal VanillaRecognitionPixels(Bitmap original)
        {
            Width = original.Width; Height = original.Height;
            using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(original, 0, 0);
                BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    pixels = new byte[Width * Height * 3];
                    for (int y = 0; y < Height; y++)
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), pixels, y * Width * 3, Width * 3);
                }
                finally { bitmap.UnlockBits(data); }
            }
        }

        internal bool Blue(int x, int y)
        {
            int offset = (y * Width + x) * 3;
            int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
            return b >= 145 && r >= 75 && b - r >= 9 && g - r >= 3 && b >= g;
        }

        internal bool Dark(int x, int y)
        {
            int offset = (y * Width + x) * 3;
            return pixels[offset] + pixels[offset + 1] + pixels[offset + 2] < 450;
        }

        internal bool NeutralInk(int x, int y)
        {
            int offset = (y * Width + x) * 3;
            int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
            return r + g + b < 510 && Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < 45;
        }
    }
}
