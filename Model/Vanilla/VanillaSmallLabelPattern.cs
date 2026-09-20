using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Exact fixed-label raster recognition for a tiny Vanilla MMO service label. This
    /// supplements OCR only inside an independently detected login form. It cannot match
    /// arbitrary expected usernames or return a guessed/corrected OCR string.
    /// </summary>
    internal static class VanillaSmallLabelPattern
    {
        private const int Columns = 96, Rows = 16;
        private sealed class Signature
        {
            internal float[] Ink;
            internal int Width, Height;
            internal bool Target;
        }
        private static readonly Lazy<Signature[]> References = new Lazy<Signature[]>(BuildReferences);
        internal static void Prepare() { var unused = References.Value; }

        internal static bool IsVanillaMmo(Bitmap image, Rectangle area, out string evidence)
        {
            evidence = "fixed-label pixels unavailable";
            if (image == null || area.Width > 200 || area.Height > 20 || area.Width < 25 || area.Height < 5
                || !new Rectangle(Point.Empty, image.Size).Contains(area)) return false;
            Signature candidate = ReadSignature(image, area);
            if (candidate == null) return false;
            double target = 0, opponent = 0, targetBlock = 0;
            foreach (Signature reference in References.Value)
            {
                // Do not stretch a shorter name into the expected label's width.
                if (Math.Abs(reference.Width - candidate.Width) > 1 || Math.Abs(reference.Height - candidate.Height) > 1) continue;
                double block;
                double score = Similarity(reference, candidate, out block);
                if (reference.Target)
                {
                    if (score > target) { target = score; targetBlock = block; }
                }
                else opponent = Math.Max(opponent, score);
            }
            evidence = "fixed-label full-shape=" + target.ToString("0.000") + "; weakest-block=" + targetBlock.ToString("0.000")
                + "; separation=" + (target - opponent).ToString("0.000")
                + "; glyph-size=" + candidate.Width + "x" + candidate.Height;
            return target >= .975 && targetBlock >= .91 && target - opponent >= .018;
        }

        private static Signature[] BuildReferences()
        {
            var result = new List<Signature>();
            foreach (string label in new[] { "Vanilla MMO", "Vanila MMO", "Vania MMO", "Vandy MMO", "Vara MMO", "Other MMO", "Vannila MMO", "Vanilla MMD" })
            foreach (string family in new[] { "Tahoma", "Arial" })
            foreach (float size in new[] { 11f, 12f, 12.5f, 13f, 14f, 16f })
            foreach (TextRenderingHint hint in new[] { TextRenderingHint.SystemDefault, TextRenderingHint.AntiAliasGridFit })
            using (var original = new Bitmap(160, 40, PixelFormat.Format24bppRgb))
            {
                using (Graphics graphics = Graphics.FromImage(original))
                using (var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var ink = new SolidBrush(Color.FromArgb(70, 70, 70)))
                {
                    graphics.Clear(Color.FromArgb(247, 247, 247));
                    graphics.TextRenderingHint = hint;
                    graphics.DrawString(label, font, ink, new PointF(8, 6));
                }
                foreach (float scale in new[] { .5f, .625f, .75f, .875f, 1f })
                foreach (float x in new[] { 0f, .5f })
                foreach (float y in new[] { 0f, .5f })
                using (var resized = new Bitmap(164, 44, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(resized))
                    {
                        graphics.Clear(Color.FromArgb(247, 247, 247));
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(original, new RectangleF(x, y, original.Width * scale, original.Height * scale));
                    }
                    Signature signature = ReadSignature(resized, new Rectangle(Point.Empty, resized.Size));
                    if (signature == null || signature.Height > 14) continue;
                    signature.Target = label == "Vanilla MMO";
                    result.Add(signature);
                }
            }
            return result.ToArray();
        }

        private static Signature ReadSignature(Bitmap source, Rectangle area)
        {
            using (Bitmap crop = source.Clone(area, PixelFormat.Format24bppRgb))
            {
                BitmapData data = crop.LockBits(new Rectangle(Point.Empty, crop.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                byte[] gray = new byte[crop.Width * crop.Height];
                try
                {
                    var row = new byte[crop.Width * 3];
                    for (int y = 0; y < crop.Height; y++)
                    {
                        Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                        for (int x = 0; x < crop.Width; x++)
                            gray[y * crop.Width + x] = (byte)((row[x * 3] * 11 + row[x * 3 + 1] * 59 + row[x * 3 + 2] * 30) / 100);
                    }
                }
                finally { crop.UnlockBits(data); }
                int[] histogram = new int[256];
                foreach (byte value in gray) histogram[value]++;
                int total = 0, background = 255;
                for (int value = 0; value < 256; value++)
                {
                    total += histogram[value];
                    if (total >= gray.Length * .85) { background = value; break; }
                }
                if (background < 205) return null;
                // Remove only full-width frame rules adjacent to the detected crop edge.
                for (int y = 0; y < crop.Height; y++)
                {
                    if (y > 2 && y < crop.Height - 3) continue;
                    int dark = 0;
                    for (int x = 0; x < crop.Width; x++) if (gray[y * crop.Width + x] < background - 35) dark++;
                    if (dark < crop.Width * .9) continue;
                    for (int x = 0; x < crop.Width; x++) gray[y * crop.Width + x] = (byte)background;
                }
                int left = crop.Width, right = -1, top = crop.Height, bottom = -1, minimum = background;
                for (int y = 0; y < crop.Height; y++)
                for (int x = 0; x < crop.Width; x++)
                {
                    int value = gray[y * crop.Width + x];
                    if (value >= background - 45) continue;
                    left = Math.Min(left, x); right = Math.Max(right, x);
                    top = Math.Min(top, y); bottom = Math.Max(bottom, y); minimum = Math.Min(minimum, value);
                }
                int width = right - left + 1, height = bottom - top + 1;
                if (width < 25 || height < 4 || height > 18 || width > 150 || minimum >= background - 50) return null;
                var ink = new float[Columns * Rows];
                for (int y = 0; y < Rows; y++)
                for (int x = 0; x < Columns; x++)
                {
                    double sx = Math.Max(0, Math.Min(width - 1, (x + .5) * width / Columns - .5));
                    double sy = Math.Max(0, Math.Min(height - 1, (y + .5) * height / Rows - .5));
                    int ix = (int)sx, iy = (int)sy;
                    double fx = sx - ix, fy = sy - iy;
                    double a = gray[(top + iy) * crop.Width + left + ix] * (1 - fx)
                        + gray[(top + iy) * crop.Width + left + Math.Min(ix + 1, width - 1)] * fx;
                    double b = gray[(top + Math.Min(iy + 1, height - 1)) * crop.Width + left + ix] * (1 - fx)
                        + gray[(top + Math.Min(iy + 1, height - 1)) * crop.Width + left + Math.Min(ix + 1, width - 1)] * fx;
                    ink[y * Columns + x] = (float)Math.Max(0, (background - (a * (1 - fy) + b * fy)) / (background - minimum));
                }
                return new Signature { Ink = ink, Width = width, Height = height };
            }
        }

        private static double Similarity(Signature reference, Signature observed, out double weakestBlock)
        {
            double best = 0;
            weakestBlock = 0;
            for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
            {
                double dot = 0, a2 = 0, b2 = 0, minimum = 1;
                for (int block = 0; block < 8; block++)
                {
                    double blockDot = 0, blockA2 = 0, blockB2 = 0;
                    for (int y = 1; y < Rows - 1; y++)
                    for (int x = block * 12; x < (block + 1) * 12; x++)
                    {
                        int shifted = x + dx;
                        if (shifted < 0 || shifted >= Columns) continue;
                        double a = reference.Ink[y * Columns + x], b = observed.Ink[(y + dy) * Columns + shifted];
                        blockDot += a * b; blockA2 += a * a; blockB2 += b * b;
                    }
                    minimum = Math.Min(minimum, blockA2 > 0 && blockB2 > 0 ? blockDot / Math.Sqrt(blockA2 * blockB2) : 0);
                    dot += blockDot; a2 += blockA2; b2 += blockB2;
                }
                double score = a2 > 0 && b2 > 0 ? dot / Math.Sqrt(a2 * b2) : 0;
                if (score > best) { best = score; weakestBlock = minimum; }
            }
            return best;
        }
    }
}
