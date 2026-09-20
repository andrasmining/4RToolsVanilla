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

        // Temporary synthetic-CI diagnostic entry point. Removed once a bounded pipeline
        // has been selected from the independently generated positive/negative fixtures.
        internal static bool TryReadExperimental(Bitmap bitmap, Rectangle area, bool accurate, int scale,
            InterpolationMode interpolation, bool rawLine, bool normalizeBackground,
            out VanillaTextLine[] lines, out string evidence)
        {
            lines = new VanillaTextLine[0]; evidence = "invalid experimental crop";
            if (area.Width > 600 || area.Height > 80) return false;
            return TryReadCore(bitmap, area, true, out lines, out evidence, accurate, scale, interpolation, rawLine, normalizeBackground);
        }

        private static bool TryReadCore(Bitmap bitmap, Rectangle area, bool singleLine,
            out VanillaTextLine[] lines, out string evidence, bool accurate, int scaleOverride = 0,
            InterpolationMode interpolation = InterpolationMode.HighQualityBicubic,
            bool rawLine = false, bool normalizeBackground = false)
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
                if (scaleOverride > 0) scale = scaleOverride;
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
                        graphics.InterpolationMode = interpolation;
                        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        // Grayscale removes colored selection/background fringing without
                        // fabricating or substituting any character in the recognized text.
                        float gain = 1f, offset = 0f;
                        if (normalizeBackground)
                        {
                            var samples = new List<int>();
                            for (int sy = area.Top; sy < area.Bottom; sy++)
                            for (int sx = area.Left; sx < area.Right; sx++)
                            {
                                Color color = bitmap.GetPixel(sx, sy);
                                samples.Add((color.R * 30 + color.G * 59 + color.B * 11) / 100);
                            }
                            samples.Sort();
                            int background = samples[samples.Count * 9 / 10], foreground = samples[0];
                            gain = 255f / Math.Max(1, background - foreground);
                            offset = -foreground * gain / 255f;
                        }
                        attributes.SetColorMatrix(new ColorMatrix(new[] {
                            new[] { .299f * gain, .299f * gain, .299f * gain, 0f, 0f },
                            new[] { .587f * gain, .587f * gain, .587f * gain, 0f, 0f },
                            new[] { .114f * gain, .114f * gain, .114f * gain, 0f, 0f },
                            new[] { 0f, 0f, 0f, 1f, 0f },
                            new[] { offset, offset, offset, 0f, 1f }
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
                                TesseractEngine activeEngine = accurate ? accurateEngine : engine;
                                if (activeEngine == null)
                                {
                                    activeEngine = new TesseractEngine(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                        accurate ? "tessdata-best" : "tessdata"), "eng", EngineMode.LstmOnly);
                                    activeEngine.SetVariable("user_defined_dpi", 300);
                                    // Do not adapt the model from another account's username.
                                    activeEngine.SetVariable("classify_enable_learning", 0);
                                    if (accurate) accurateEngine = activeEngine;
                                    else engine = activeEngine;
                                }
                                using (Page page = activeEngine.Process(pix, rawLine ? PageSegMode.RawLine : singleLine ? PageSegMode.SingleLine : PageSegMode.SparseText))
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
