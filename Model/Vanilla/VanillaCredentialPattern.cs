using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Visual focus and masking evidence only; never recognizes or retains password text.</summary>
    internal static class VanillaCredentialPattern
    {
        private const int Grid = 12;
        private sealed class MaskGlyph { internal string Family; internal double[] Shape; }
        private static readonly Lazy<List<MaskGlyph>> MaskGlyphs = new Lazy<List<MaskGlyph>>(BuildMaskGlyphs);

        internal static bool TryDetectCaretBlink(Bitmap first, Bitmap second, Rectangle field, out Rectangle caret)
        {
            caret = Rectangle.Empty;
            if (first == null || second == null || first.Size != second.Size
                || !new Rectangle(Point.Empty, first.Size).Contains(field) || field.Height < 6) return false;
            int left = field.Right, top = field.Bottom, right = -1, bottom = -1, changed = 0;
            for (int y = field.Top; y < field.Bottom; y++)
            for (int x = field.Left; x < field.Right; x++)
            {
                int a = Light(first.GetPixel(x, y)), b = Light(second.GetPixel(x, y));
                // Blink must switch between an actual dark caret and the pale field background.
                if (Math.Abs(a - b) < 65) continue;
                if (Math.Min(a, b) > 160 || Math.Max(a, b) < 205) return false;
                changed++;
                left = Math.Min(left, x); right = Math.Max(right, x);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y);
            }
            if (changed < 4) return false;
            caret = Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
            if (caret.Height < Math.Max(5, field.Height * 0.45)
                || caret.Width > Math.Max(3, field.Height / 5)
                || caret.Height < caret.Width * 3
                || changed < caret.Width * caret.Height * 0.70)
            {
                caret = Rectangle.Empty;
                return false;
            }
            return true;
        }

        internal static bool VerifyPasswordMask(Bitmap image, Rectangle field, int expectedLength)
        {
            string evidence;
            return VerifyPasswordMask(image, field, expectedLength, out evidence);
        }

        internal static bool VerifyPasswordMask(Bitmap image, Rectangle field, int expectedLength, out string evidence)
        {
            evidence = "invalid mask region or expected length";
            if (image == null || expectedLength <= 0 || expectedLength > 128
                || field.Width < 4 || field.Height < 5 || !new Rectangle(Point.Empty, image.Size).Contains(field)) return false;
            // Edge detection may land on either side of a one-pixel border gradient.
            field.Inflate(-1, -1);
            bool[,] ink = ReadInk(image, field);
            List<Rectangle> components = ColumnComponents(ink);
            // A visible insertion caret is a narrow, tall final component, never a mask symbol.
            if (components.Count > 0)
            {
                Rectangle last = components[components.Count - 1];
                if (last.Height >= field.Height * 0.65 && last.Width <= Math.Max(2, field.Height / 7)
                    && last.Height >= last.Width * 4)
                    components.RemoveAt(components.Count - 1);
            }
            if (components.Count != expectedLength) { evidence = "mask component count mismatch"; return false; }
            Rectangle first = components[0];
            if (first.Height < 3 || first.Width < 3 || first.Height > field.Height * 0.75)
            { evidence = "mask glyph dimensions are implausible"; return false; }
            HashSet<string> families = null;
            for (int index = 0; index < components.Count; index++)
            {
                Rectangle current = components[index];
                if (Math.Abs(current.Width - first.Width) > 1 || Math.Abs(current.Height - first.Height) > 1
                    || Math.Abs(current.Top - first.Top) > 1)
                { evidence = "mask glyph sizes or baselines differ"; return false; }
                double[] sample = Normalize(ink, current);
                var matches = new HashSet<string>(MaskGlyphs.Value
                    .Where(template => Error(sample, template.Shape) <= 0.155).Select(template => template.Family));
                if (families == null) families = matches;
                else families.IntersectWith(matches);
                if (families.Count == 0)
                {
                    evidence = "glyph did not match one consistent known mask family; minimum error="
                        + MaskGlyphs.Value.Min(template => Error(sample, template.Shape)).ToString("0.000");
                    return false;
                }
                if (index > 1)
                {
                    // Fractional resampling can change a glyph's ink width by one pixel.
                    // Verify the symbol advance, not a background gap dependent on that width.
                    int advance = current.Left - components[index - 1].Left;
                    int firstAdvance = components[1].Left - first.Left;
                    if (Math.Abs(advance - firstAdvance) > 1)
                    { evidence = "mask symbol advances are inconsistent"; return false; }
                }
            }
            evidence = "known uniform mask family and expected symbol count confirmed";
            return true;
        }

        private static int Light(Color color) { return (color.R * 30 + color.G * 59 + color.B * 11) / 100; }

        private static bool[,] ReadInk(Bitmap image, Rectangle bounds)
        {
            var light = new int[bounds.Width * bounds.Height];
            for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++) light[y * bounds.Width + x] = Light(image.GetPixel(bounds.X + x, bounds.Y + y));
            int[] ordered = (int[])light.Clone();
            Array.Sort(ordered);
            int background = ordered[(ordered.Length * 3) / 4];
            // Low-contrast/selected/unknown backgrounds must not masquerade as masked text.
            int threshold = Math.Min(185, background - 55);
            var ink = new bool[bounds.Width, bounds.Height];
            if (background < 205) return ink;
            for (int y = 0; y < bounds.Height; y++)
            for (int x = 0; x < bounds.Width; x++) ink[x, y] = light[y * bounds.Width + x] < threshold;
            return ink;
        }

        private static List<Rectangle> ColumnComponents(bool[,] ink)
        {
            var result = new List<Rectangle>();
            int start = -1, top = ink.GetLength(1), bottom = -1;
            for (int x = 0; x <= ink.GetLength(0); x++)
            {
                bool occupied = false;
                if (x < ink.GetLength(0))
                {
                    for (int y = 0; y < ink.GetLength(1); y++)
                    {
                        if (!ink[x, y]) continue;
                        occupied = true;
                        top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    }
                }
                if (occupied && start < 0) start = x;
                if (!occupied && start >= 0)
                {
                    result.Add(Rectangle.FromLTRB(start, top, x, bottom + 1));
                    start = -1; top = ink.GetLength(1); bottom = -1;
                }
            }
            return result;
        }

        private static double[] Normalize(bool[,] ink, Rectangle bounds)
        {
            var values = new double[Grid * Grid];
            for (int row = 0; row < Grid; row++)
            for (int col = 0; col < Grid; col++)
            {
                // Supersampled coverage allows the same known mask shape at several UI scales.
                int hits = 0;
                for (int sy = 0; sy < 3; sy++)
                for (int sx = 0; sx < 3; sx++)
                {
                    int x = bounds.X + Math.Min(bounds.Width - 1, (int)((col + (sx + 0.5) / 3) / Grid * bounds.Width));
                    int y = bounds.Y + Math.Min(bounds.Height - 1, (int)((row + (sy + 0.5) / 3) / Grid * bounds.Height));
                    if (ink[x, y]) hits++;
                }
                values[row * Grid + col] = hits / 9.0;
            }
            return values;
        }

        private static double Error(double[] first, double[] second)
        {
            double sum = 0;
            for (int index = 0; index < first.Length; index++) sum += Math.Abs(first[index] - second[index]);
            return sum / first.Length;
        }

        private static List<MaskGlyph> BuildMaskGlyphs()
        {
            var result = new List<MaskGlyph>();
            foreach (string family in new[] { "Arial", "Tahoma", "Verdana", "Segoe UI" })
            foreach (int size in new[] { 9, 11, 13, 15, 18, 24 })
            foreach (string mask in new[] { "*", "\u2022", "\u25cf" })
            foreach (TextRenderingHint hint in new[] { TextRenderingHint.SingleBitPerPixelGridFit, TextRenderingHint.AntiAliasGridFit })
            {
                using (var glyph = new Bitmap(64, 64))
                using (Graphics graphics = Graphics.FromImage(glyph))
                using (var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.White);
                    graphics.TextRenderingHint = hint;
                    graphics.DrawString(mask, font, Brushes.Black, new PointF(8, 8), StringFormat.GenericTypographic);
                    AddMaskGlyph(result, glyph, mask);
                    // Rendering at a low game resolution and stretching the client causes
                    // different subpixel phases even for identical repeated mask symbols.
                    // Model those known raster variants instead of relaxing the shape test.
                    foreach (float scale in new[] { 0.625f, 0.75f, 0.875f })
                    foreach (float offsetX in new[] { 0f, 0.5f })
                    foreach (float offsetY in new[] { 0f, 0.5f })
                    {
                        using (var scaled = new Bitmap(64, 64))
                        using (Graphics resize = Graphics.FromImage(scaled))
                        {
                            resize.Clear(Color.White);
                            resize.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            resize.DrawImage(glyph, new RectangleF(offsetX, offsetY, glyph.Width * scale, glyph.Height * scale));
                            AddMaskGlyph(result, scaled, mask);
                        }
                    }
                }
            }
            return result;
        }

        private static void AddMaskGlyph(List<MaskGlyph> result, Bitmap glyph, string mask)
        {
            bool[,] pixels = ReadInk(glyph, new Rectangle(0, 0, glyph.Width, glyph.Height));
            List<Rectangle> parts = ColumnComponents(pixels);
            if (parts.Count == 1 && parts[0].Width >= 3 && parts[0].Height >= 3)
                result.Add(new MaskGlyph { Family = mask == "*" ? "asterisk" : "bullet", Shape = Normalize(pixels, parts[0]) });
        }
    }
}
