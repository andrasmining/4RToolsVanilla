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
    /// surface is fifteen light cards, a cyan selected outline and a separate
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
            if (image == null || image.Width < 320 || image.Height < 240
                || (long)image.Width * image.Height > 16000000) return false;
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
                Rectangle[] group = candidates.Where(c => Math.Abs(c.Bounds.Width - seed.Bounds.Width) <= Math.Max(4, seed.Bounds.Width * .09)
                    && Math.Abs(c.Bounds.Height - seed.Bounds.Height) <= Math.Max(4, seed.Bounds.Height * .09))
                    .Select(c => c.Bounds).OrderBy(c => c.Top).ThenBy(c => c.Left).ToArray();
                if (group.Length != 15) continue;
                string key = string.Join(";", group.Select(c => c.ToString()));
                if (!tried.Add(key)) continue;
                Rectangle[] cards;
                int columns;
                if (!VanillaCharacterPattern.TryOrderGrid(group, out cards, out columns)) continue;
                int[] selected = Enumerable.Range(0, 15).Where(i => pixels.SelectedOutline(cards[i])).ToArray();
                if (selected.Length != 1) continue;
                Rectangle grid = cards.Aggregate(Rectangle.Union);
                // Locate a separate light control beside the grid. Its observed text
                // must establish the screen's role; arbitrary photo/slot grids fail.
                Component[] listControls = components.Where(c => c.Bounds.Left >= grid.Right
                    && c.Bounds.Width >= seed.Bounds.Width * .7 && c.Bounds.Width <= seed.Bounds.Width * 2
                    && c.Bounds.Height >= seed.Bounds.Height * .18 && c.Bounds.Height <= seed.Bounds.Height * .8
                    && c.Bounds.Bottom >= grid.Bottom - seed.Bounds.Height
                    && c.Bounds.Top < grid.Bottom && c.Area >= c.Bounds.Width * c.Bounds.Height * .6).ToArray();
                if (listControls.Length > 4 || !listControls.Any(c => HasCharacterListTitle(image, c.Bounds))) continue;
                if (observation != null) { observation = null; evidence = "multiple character grids; input refused"; return false; }
                observation = new VanillaCharacterSelectionObservation
                {
                    Cards = cards, Columns = columns, Selected = selected[0],
                    Occupied = cards.Select(pixels.HasCharacterSprite).ToArray()
                };
            }
            if (observation == null) return false;
            evidence = "observed fifteen-card " + observation.Columns + "x" + observation.Rows
                + " grid; Character List title; cyan selection at slot " + (observation.Selected + 1)
                + "; occupied slots=" + string.Join(",", Enumerable.Range(0, 15).Where(i => observation.Occupied[i]).Select(i => i + 1));
            observation.Evidence = evidence;
            return true;
        }

        private static bool HasCharacterListTitle(Bitmap image, Rectangle control)
        {
            VanillaTextLine[] lines;
            string ignored;
            if (VanillaTextRecognition.TryRead(image, control, false, out lines, out ignored)
                && lines.Any(IsTitle)) return true;
            var recognition = new VanillaRecognitionPixels(image);
            foreach (Rectangle area in VanillaServiceRecognition.FindBodyTextAreas(recognition, control))
                if (VanillaTextRecognition.TryReadPixelPreservingLine(image, area, out lines, out ignored)
                    && lines.Any(IsTitle)) return true;
            return false;
        }

        private static bool IsTitle(VanillaTextLine line)
        {
            return line.Confidence >= 55 && VanillaServiceRecognition.Letters(line.Text) == "CHARACTERLIST";
        }

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
                            if (Cyan(x, y)) { colored = true; break; }
                        }
                        samples++; if (colored) found++;
                    }
                    double ratio = found / (double)Math.Max(1, samples);
                    if (ratio < .45) return false;
                    total += ratio;
                }
                return total >= 2.8;
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
