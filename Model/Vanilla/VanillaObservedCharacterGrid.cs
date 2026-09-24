using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// The supplied Vanilla skin has no "Character Select" heading. Its actual
    /// surface is fifteen light cards, a cyan/blue selected outline and a separate
    /// Character List control. Detect these objects, not desktop/grid coordinates.
    /// </summary>
    internal static class VanillaObservedCharacterGrid
    {
        private sealed class Component
        {
            internal Rectangle Bounds;
            internal int Area;
        }
        internal static bool TryDetect(Bitmap image, out VanillaCharacterSelectionObservation observation, out string evidence)
        {
            observation = null;
            evidence = "observed character cards/Character List control unavailable";
            if (image == null || image.Width < 320 || image.Height < 240 || (long)image.Width * image.Height > 16000000) return false;
            var pixels = new Pixels(image);
            List<Component> components = pixels.Components();
            var candidates = components.Where(c => c.Bounds.Width >= 28 && c.Bounds.Height >= 36
                && c.Bounds.Width < image.Width * .5 && c.Bounds.Height < image.Height * .7
                && c.Bounds.Width >= c.Bounds.Height * .45 && c.Bounds.Width <= c.Bounds.Height * 1.4
                && c.Area >= c.Bounds.Width * c.Bounds.Height * .65).ToArray();
            if (candidates.Length > 100) return false;
            var tried = new HashSet<string>();
            foreach (Component seed in candidates)
            {
                // The selected card's blue name strip is not part of its connected
                // light face. It is shorter than the fourteen neutral card bodies.
                Rectangle[] group = candidates.Where(c => Math.Abs(c.Bounds.Width - seed.Bounds.Width) <= Math.Max(4, seed.Bounds.Width * .09)
                    && c.Bounds.Height >= seed.Bounds.Height * .68
                    && c.Bounds.Height <= seed.Bounds.Height + Math.Max(4, seed.Bounds.Height * .09))
                    .Select(c => c.Bounds).OrderBy(c => c.Top).ThenBy(c => c.Left).ToArray();
                if (group.Length != 15)
                { if (tried.Count == 0) evidence = "light card group count=" + group.Length + "; fifteen required"; continue; }
                string key = string.Join(";", group.Select(c => c.ToString()));
                if (!tried.Add(key)) continue;
                int[] selected = Enumerable.Range(0, 15).Where(i => pixels.SelectedOutline(group[i])).ToArray();
                if (selected.Length != 1) { evidence = "character grid selected outline count=" + selected.Length + "; one required"; continue; }
                if (!NormalizeSelectedCard(group, selected[0]))
                { evidence = "selected face does not match the independently observed card rows/columns"; continue; }
                Rectangle selectedCard = group[selected[0]];
                Rectangle[] cards; int columns;
                if (!VanillaCharacterPattern.TryOrderGrid(group, out cards, out columns))
                { evidence = "fifteen light faces do not form a regular character grid"; continue; }
                int selectedSlot = Array.IndexOf(cards, selectedCard);
                Rectangle grid = cards.Aggregate(Rectangle.Union);
                // Locate a separate light control beside the grid. Observed text must
                // establish the role of this surface; arbitrary slot grids fail.
                Component[] listControls = components.Where(c => c.Bounds.Left >= grid.Right
                    && c.Bounds.Width >= seed.Bounds.Width * .7 && c.Bounds.Width <= seed.Bounds.Width * 2
                    && c.Bounds.Height >= seed.Bounds.Height * .18 && c.Bounds.Height <= seed.Bounds.Height * .8
                    && c.Bounds.Bottom >= grid.Bottom - seed.Bounds.Height && c.Bounds.Top < grid.Bottom
                    // The page-navigation control occupies much of this panel in blue.
                    // The connected light portion still contains the required title.
                    && c.Area >= c.Bounds.Width * c.Bounds.Height * .45).ToArray();
                if (listControls.Length > 4 || !listControls.Any(c => HasCharacterListTitle(image, c.Bounds)))
                { evidence = "character grid found but separate Character List title was not verified"; continue; }
                if (observation != null) { observation = null; evidence = "multiple character grids; input refused"; return false; }
                observation = new VanillaCharacterSelectionObservation
                { Cards = cards, Columns = columns, Selected = selectedSlot, Occupied = cards.Select(pixels.HasCharacterSprite).ToArray() };
            }
            if (observation == null) return false;
            var observed = observation;
            evidence = "observed fifteen-card " + observed.Columns + "x" + observed.Rows
                + " grid; Character List title; cyan/blue selection at slot " + (observed.Selected + 1)
                + "; occupied slots=" + string.Join(",", Enumerable.Range(0, 15).Where(i => observed.Occupied[i]).Select(i => i + 1));
            observation.Evidence = evidence;
            return true;
        }
        private static bool NormalizeSelectedCard(Rectangle[] cards, int selected)
        {
            Rectangle face = cards[selected];
            Rectangle[] neutral = cards.Where((card, index) => index != selected).ToArray();
            int width = Median(neutral.Select(card => card.Width)), height = Median(neutral.Select(card => card.Height));
            if (neutral.Any(card => Math.Abs(card.Width - width) > Math.Max(4, width * .09)
                || Math.Abs(card.Height - height) > Math.Max(4, height * .09))) return false;
            // Anchor the selected full card to other observed cards in its own column
            // and row, not to screen percentages. This keeps layout identity stable
            // when the shorter selected face moves to another slot after a key.
            int[] lefts = neutral.Where(card => Math.Abs(card.Left - face.Left) <= Math.Max(6, width * .08))
                .Select(card => card.Left).ToArray();
            int[] tops = neutral.Where(card => Math.Abs(card.Top - face.Top) <= Math.Max(6, height * .08))
                .Select(card => card.Top).ToArray();
            if (lefts.Length == 0 || tops.Length == 0)
                return Math.Abs(face.Height - height) <= Math.Max(4, height * .09);
            var full = new Rectangle(Median(lefts), Median(tops), width, height);
            Rectangle overlap = Rectangle.Intersect(face, full);
            if (overlap.Width < face.Width * .90 || overlap.Height < face.Height * .90
                || Math.Abs(face.Left - full.Left) > Math.Max(6, width * .08)
                || Math.Abs(face.Top - full.Top) > Math.Max(6, height * .08)
                || face.Height < height * .68 || face.Height > height * 1.09) return false;
            cards[selected] = full;
            return true;
        }
        private static int Median(IEnumerable<int> values)
        {
            int[] ordered = values.OrderBy(value => value).ToArray();
            return ordered[ordered.Length / 2];
        }
        private static bool HasCharacterListTitle(Bitmap image, Rectangle control)
        {
            VanillaTextLine[] lines; string ignored;
            if (VanillaTextRecognition.TryRead(image, control, false, out lines, out ignored) && lines.Any(IsTitle)) return true;
            var recognition = new VanillaRecognitionPixels(image);
            foreach (Rectangle area in VanillaServiceRecognition.FindBodyTextAreas(recognition, control))
                if (VanillaTextRecognition.TryReadPixelPreservingLine(image, area, out lines, out ignored) && lines.Any(IsTitle)) return true;
            return false;
        }
        private static bool IsTitle(VanillaTextLine line)
        { return line.Confidence >= 55 && VanillaServiceRecognition.Letters(line.Text) == "CHARACTERLIST"; }
        private sealed class Pixels
        {
            private readonly byte[] rgb;
            private readonly int width, height;
            internal Pixels(Bitmap source)
            {
                width = source.Width; height = source.Height; rgb = new byte[width * height * 3];
                using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(bitmap)) g.DrawImageUnscaled(source, 0, 0);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try { for (int y = 0; y < height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), rgb, y * width * 3, width * 3); }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            private bool Light(int p)
            {
                int i = p * 3, low = Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]));
                int high = Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2]));
                return low >= 218 && high - low <= 38;
            }
            private bool Cyan(int x, int y)
            {
                if (x < 0 || y < 0 || x >= width || y >= height) return false;
                int i = (y * width + x) * 3;
                return rgb[i] >= 195 && rgb[i + 1] >= 195 && rgb[i + 1] - rgb[i + 2] >= 35
                    && rgb[i] - rgb[i + 2] >= 35 && Math.Abs(rgb[i] - rgb[i + 1]) < 45;
            }
            private bool SelectionColor(int x, int y)
            {
                if (Cyan(x, y)) return true;
                if (x < 0 || y < 0 || x >= width || y >= height) return false;
                int i = (y * width + x) * 3;
                // Selected blue/lavender footer and cyan-to-blue side gradient.
                // Neutral card backgrounds and white borders do not satisfy this.
                return rgb[i] >= 195 && rgb[i + 1] >= 175
                    && rgb[i] - rgb[i + 2] >= 30 && rgb[i] - rgb[i + 1] >= 20;
            }
            internal List<Component> Components()
            {
                var remaining = new bool[width * height];
                for (int i = 0; i < remaining.Length; i++) remaining[i] = Light(i);
                var queue = new int[remaining.Length];
                var result = new List<Component>();
                for (int seed = 0; seed < remaining.Length; seed++)
                {
                    if (!remaining[seed]) continue;
                    remaining[seed] = false; queue[0] = seed;
                    int count = 1, next = 0, left = width, right = 0, top = height, bottom = 0;
                    while (next < count)
                    {
                        int p = queue[next++], x = p % width, y = p / width;
                        left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                        for (int d = 0; d < 4; d++)
                        {
                            int nx = x + (d == 0 ? -1 : d == 1 ? 1 : 0), ny = y + (d == 2 ? -1 : d == 3 ? 1 : 0);
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                            int q = ny * width + nx;
                            if (!remaining[q]) continue;
                            remaining[q] = false; queue[count++] = q;
                        }
                    }
                    if (right - left >= 20 && bottom - top >= 10)
                        result.Add(new Component { Bounds = Rectangle.FromLTRB(left, top, right + 1, bottom + 1), Area = count });
                    if (result.Count > 512) return new List<Component>();
                }
                return result;
            }
            internal bool SelectedOutline(Rectangle card)
            {
                int margin = Math.Max(2, (int)Math.Ceiling(Math.Min(card.Width, card.Height) * .04));
                double total = 0;
                int cyanTop = 0, topSamples = 0;
                for (int side = 0; side < 4; side++)
                {
                    int found = 0, samples = 0;
                    int length = side < 2 ? card.Height : card.Width;
                    for (int step = length / 8; step < length * 7 / 8; step++)
                    {
                        bool colored = false;
                        for (int offset = -1; offset <= margin; offset++)
                        {
                            int x = side == 0 ? card.Left - offset : side == 1 ? card.Right - 1 + offset : card.Left + step;
                            int y = side == 2 ? card.Top - offset : side == 3 ? card.Bottom - 1 + offset : card.Top + step;
                            if (side == 2 && Cyan(x, y)) cyanTop++;
                            if (SelectionColor(x, y)) { colored = true; break; }
                        }
                        if (side == 2) topSamples++;
                        samples++; if (colored) found++;
                    }
                    double ratio = found / (double)Math.Max(1, samples);
                    if (ratio < .45) return false;
                    total += ratio;
                }
                return total >= 2.8 && cyanTop >= topSamples * .45;
            }
            internal bool HasCharacterSprite(Rectangle card)
            {
                int count = 0, top = height, bottom = -1;
                for (int y = card.Top + card.Height / 8; y < card.Top + card.Height * 4 / 5; y++)
                for (int x = card.Left + card.Width / 8; x < card.Right - card.Width / 8; x++)
                {
                    int i = (y * width + x) * 3;
                    int low = Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]));
                    int high = Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2]));
                    if ((rgb[i] + rgb[i + 1] + rgb[i + 2]) < 390 || (high - low > 65 && low < 180))
                    { count++; top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
                }
                return count >= Math.Max(12, card.Width * card.Height * .005) && bottom - top >= card.Height * .15;
            }
        }
    }
}
