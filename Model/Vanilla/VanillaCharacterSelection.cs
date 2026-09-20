using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaCharacterSelectionObservation
    {
        internal long FrameId;
        internal Rectangle[] Cards;
        internal int Columns;
        internal int Selected;
        internal string Evidence;
        internal int Rows { get { return Cards.Length / Columns; } }

        internal bool SameLayout(VanillaCharacterSelectionObservation other)
        {
            return other != null && Cards != null && other.Cards != null && Columns == other.Columns
                && Cards.Length == other.Cards.Length && Cards.Zip(other.Cards, (a, b) =>
                    Math.Abs(a.Left - b.Left) <= 3 && Math.Abs(a.Top - b.Top) <= 3
                    && Math.Abs(a.Width - b.Width) <= 3 && Math.Abs(a.Height - b.Height) <= 3).All(v => v);
        }
    }

    /// <summary>
    /// Each key is an observed transition, including the two edge clamps. A key plan alone
    /// cannot establish a slot. The separately verified gameplay identity remains mandatory.
    /// </summary>
    internal static class VanillaCharacterSelector
    {
        internal static void Select(int oneBasedSlot, Func<VanillaCharacterSelectionObservation> observe,
            Action<Keys> press, Action<int> pause, Func<bool> cancelled)
        {
            if (oneBasedSlot < 1 || oneBasedSlot > 15) throw new ArgumentOutOfRangeException(nameof(oneBasedSlot));
            if (observe == null || press == null || pause == null || cancelled == null) throw new ArgumentNullException();
            long frame = -1;
            VanillaCharacterSelectionObservation current = Stable(observe, pause, cancelled, ref frame);
            var origin = current;
            Action<Keys, int> move = (key, expected) =>
            {
                CheckCancelled(cancelled);
                press(key);
                pause(140);
                var next = Stable(observe, pause, cancelled, ref frame, expected);
                if (!origin.SameLayout(next) || next.Selected != expected)
                    throw new InvalidOperationException("Character selection did not verify " + key
                        + " at slot " + (expected + 1) + "; no confirmation was sent.");
                current = next;
            };
            while (current.Selected / current.Columns > 0)
                move(Keys.Up, current.Selected - current.Columns);
            // Prove the edge clamps: wrapping, lost focus and stalled interior transitions fail closed.
            move(Keys.Up, current.Selected);
            while (current.Selected % current.Columns > 0) move(Keys.Left, current.Selected - 1);
            move(Keys.Left, current.Selected);
            int target = oneBasedSlot - 1;
            for (int column = 0; column < target % current.Columns; column++) move(Keys.Right, current.Selected + 1);
            for (int row = 0; row < target / current.Columns; row++) move(Keys.Down, current.Selected + current.Columns);
            var confirmed = Stable(observe, pause, cancelled, ref frame, target);
            if (!origin.SameLayout(confirmed) || confirmed.Selected != target)
                throw new InvalidOperationException("Configured character slot is no longer selected; no confirmation was sent.");
            CheckCancelled(cancelled);
            press(Keys.Enter);
        }

        private static VanillaCharacterSelectionObservation Stable(Func<VanillaCharacterSelectionObservation> observe,
            Action<int> pause, Func<bool> cancelled, ref long lastFrame, int? expected = null)
        {
            VanillaCharacterSelectionObservation previous = null;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                CheckCancelled(cancelled);
                var current = observe();
                CheckCancelled(cancelled);
                if (current == null || current.Cards == null || current.Cards.Length != 15
                    || current.Columns < 1 || current.Columns > 15 || 15 % current.Columns != 0
                    || current.Selected < 0 || current.Selected >= 15 || current.FrameId <= lastFrame)
                    throw new InvalidOperationException("Fresh character cards and a unique selected slot are unavailable; no further input was sent.");
                lastFrame = current.FrameId;
                if (current.SameLayout(previous) && current.Selected == previous.Selected
                    && (!expected.HasValue || current.Selected == expected.Value)) return current;
                previous = current;
                pause(120);
            }
            throw new InvalidOperationException("Character selection did not stabilize; no confirmation was sent.");
        }

        private static void CheckCancelled(Func<bool> cancelled)
        {
            if (cancelled()) throw new OperationCanceledException("Character selection cancelled.");
        }
    }

    /// <summary>
    /// Conservative supported surface: explicit character-selection and GAME START labels,
    /// fifteen independently bordered cards, and one continuous colored selection frame.
    /// No supplied live fixture establishes that every Vanilla skin uses this surface.
    /// An unknown skin/layout must remain unsupported rather than authorizing blind keys.
    /// </summary>
    internal static class VanillaCharacterPattern
    {
        internal static bool TryDetect(Bitmap bitmap, out VanillaCharacterSelectionObservation observation, out string evidence)
        {
            observation = null;
            VanillaTextLine[] text;
            if (bitmap == null) { evidence = "character capture missing"; return false; }
            if (!VanillaTextRecognition.TryRead(bitmap, new Rectangle(Point.Empty, bitmap.Size), false, out text, out evidence)) return false;
            return TryDetect(bitmap, text, out observation, out evidence);
        }

        internal static bool TryDetect(Bitmap bitmap, VanillaTextLine[] text,
            out VanillaCharacterSelectionObservation observation, out string evidence)
        {
            observation = null;
            evidence = "character selection labels, card borders or unique selection frame unavailable";
            if (bitmap == null || bitmap.Width < 320 || bitmap.Height < 240 || text == null) return false;
            var titles = text.Where(t => t.Confidence >= 60 && (Canonical(t.Text) == "CHARACTERSELECT"
                || Canonical(t.Text) == "SELECTCHARACTER" || Canonical(t.Text) == "CHARACTERSELECTION")).ToArray();
            var starts = text.Where(t => t.Confidence >= 60 && Canonical(t.Text) == "GAMESTART").ToArray();
            if (titles.Length != 1 || starts.Length != 1) return false;
            int top = titles[0].Bounds.Bottom, bottom = starts[0].Bounds.Top;
            if (top < 0 || bottom > bitmap.Height || bottom - top < 60) return false;
            var pixels = new Pixels(bitmap);
            List<Rectangle> boxes = FindCards(pixels, top, bottom);
            var groups = new List<Rectangle[]>();
            foreach (Rectangle seed in boxes)
            {
                Rectangle[] group = boxes.Where(b => Math.Abs(b.Width - seed.Width) <= Math.Max(3, seed.Width * .08)
                    && Math.Abs(b.Height - seed.Height) <= Math.Max(3, seed.Height * .08)).ToArray();
                if (group.Length != 15 || groups.Any(g => g.SequenceEqual(group))) continue;
                groups.Add(group);
            }
            VanillaCharacterSelectionObservation match = null;
            foreach (var group in groups)
            {
                Rectangle[] ordered;
                int columns;
                if (!TryOrderGrid(group, out ordered, out columns)) continue;
                double[] accent = ordered.Select(pixels.AccentBorder).ToArray();
                int[] selected = Enumerable.Range(0, accent.Length).Where(i => accent[i] >= .62).ToArray();
                if (selected.Length != 1 || accent.Where((v, i) => i != selected[0]).Any(v => v > .20)) continue;
                if (match != null) { evidence = "multiple plausible character grids; selection refused"; return false; }
                match = new VanillaCharacterSelectionObservation { Cards = ordered, Columns = columns, Selected = selected[0] };
            }
            if (match == null) return false;
            evidence = "explicit character labels; fifteen bordered cards; observed grid=" + match.Columns + "x" + match.Rows
                + "; unique selected frame at slot " + (match.Selected + 1);
            match.Evidence = evidence;
            observation = match;
            return true;
        }

        private static string Canonical(string value)
        { return new string((value ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray()); }

        internal static bool TryOrderGrid(Rectangle[] boxes, out Rectangle[] ordered, out int columns)
        {
            ordered = null; columns = 0;
            if (boxes == null || boxes.Length != 15 || boxes.Any(b => b.Width < 28 || b.Height < 28)) return false;
            var rows = new List<List<Rectangle>>();
            foreach (Rectangle box in boxes.OrderBy(b => b.Top).ThenBy(b => b.Left))
            {
                List<Rectangle> row = rows.FirstOrDefault(r => Math.Abs(r[0].Top - box.Top) <= Math.Max(3, box.Height * .06));
                if (row == null) { row = new List<Rectangle>(); rows.Add(row); }
                row.Add(box);
            }
            columns = rows[0].Count;
            if (rows.Any(r => r.Count != rows[0].Count)) return false;
            foreach (var row in rows) row.Sort((a, b) => a.Left.CompareTo(b.Left));
            for (int r = 0; r < rows.Count; r++)
            for (int c = 0; c < columns; c++)
            {
                Rectangle box = rows[r][c];
                if (c > 0 && box.Left <= rows[r][c - 1].Right) return false;
                if (r > 0 && (box.Top <= rows[r - 1][c].Bottom || Math.Abs(box.Left - rows[0][c].Left) > 3
                    || Math.Abs(box.Width - rows[0][c].Width) > 3)) return false;
                if (c > 1 && Math.Abs((box.Left - rows[r][c - 1].Left) - (rows[r][1].Left - rows[r][0].Left)) > 4) return false;
                if (r > 1 && Math.Abs((box.Top - rows[r - 1][c].Top) - (rows[1][c].Top - rows[0][c].Top)) > 4) return false;
            }
            ordered = rows.SelectMany(r => r).ToArray();
            return true;
        }

        private struct Edge { internal int Left, Right, Y; }

        private static List<Rectangle> FindCards(Pixels pixels, int top, int bottom)
        {
            var edges = new List<Edge>();
            for (int y = top; y < bottom; y++)
            for (int x = 0; x < pixels.Width; x++)
            {
                if (!pixels.Border(x, y)) continue;
                int left = x, last = x, gap = 0;
                while (++x < pixels.Width)
                {
                    if (pixels.Border(x, y)) { last = x; gap = 0; }
                    else if (++gap > 1) break;
                }
                if (last - left >= 27 && last - left < pixels.Width * .7)
                    edges.Add(new Edge { Left = left, Right = last, Y = y });
                if (edges.Count > 4000) return new List<Rectangle>();
            }
            var boxes = new List<Rectangle>();
            foreach (Edge upper in edges)
            foreach (Edge lower in edges)
            {
                int height = lower.Y - upper.Y + 1;
                if (height < 28 || height > (bottom - top) * .9 || Math.Abs(upper.Left - lower.Left) > 2
                    || Math.Abs(upper.Right - lower.Right) > 2) continue;
                var rectangle = Rectangle.FromLTRB(upper.Left, upper.Y, upper.Right + 1, lower.Y + 1);
                if (!pixels.VerticalBorder(rectangle.Left, rectangle.Top, rectangle.Bottom)
                    || !pixels.VerticalBorder(rectangle.Right - 1, rectangle.Top, rectangle.Bottom)) continue;
                if (boxes.Any(b => Math.Abs(b.Left - rectangle.Left) <= 4 && Math.Abs(b.Top - rectangle.Top) <= 4
                    && Math.Abs(b.Right - rectangle.Right) <= 4 && Math.Abs(b.Bottom - rectangle.Bottom) <= 4)) continue;
                boxes.Add(rectangle);
                if (boxes.Count > 200) return new List<Rectangle>();
            }
            return boxes;
        }

        private sealed class Pixels
        {
            internal readonly int Width, Height;
            private readonly byte[] rgb;
            internal Pixels(Bitmap source)
            {
                Width = source.Width; Height = source.Height;
                rgb = new byte[Width * Height * 3];
                using (var bitmap = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.DrawImageUnscaled(source, 0, 0);
                    BitmapData data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try { for (int y = 0; y < Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), rgb, y * Width * 3, Width * 3); }
                    finally { bitmap.UnlockBits(data); }
                }
            }
            internal bool Border(int x, int y)
            {
                int i = (y * Width + x) * 3;
                return (rgb[i] * 11 + rgb[i + 1] * 59 + rgb[i + 2] * 30) / 100 < 135 || Accent(x, y);
            }
            private bool Accent(int x, int y)
            {
                int i = (y * Width + x) * 3;
                int high = Math.Max(rgb[i], Math.Max(rgb[i + 1], rgb[i + 2]));
                int low = Math.Min(rgb[i], Math.Min(rgb[i + 1], rgb[i + 2]));
                return high > 140 && high - low > 55;
            }
            internal bool VerticalBorder(int x, int top, int bottom)
            {
                int count = 0;
                for (int y = top; y < bottom; y++)
                    if (Border(x, y) || (x > 0 && Border(x - 1, y)) || (x + 1 < Width && Border(x + 1, y))) count++;
                return count >= (bottom - top) * .88;
            }
            internal double AccentBorder(Rectangle box)
            {
                int count = 0, total = 0;
                for (int x = box.Left + 3; x < box.Right - 3; x++)
                { total += 2; if (Accent(x, box.Top)) count++; if (Accent(x, box.Bottom - 1)) count++; }
                for (int y = box.Top + 3; y < box.Bottom - 3; y++)
                { total += 2; if (Accent(box.Left, y)) count++; if (Accent(box.Right - 1, y)) count++; }
                return count / (double)Math.Max(1, total);
            }
        }
    }
}
