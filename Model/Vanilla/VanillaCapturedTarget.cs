using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace _4RTools.Model.Vanilla
{
    internal static class VanillaCapturedTarget
    {
        // Retain saved captures. Animated sprite pixels are not an input gate.
        internal static string Capture(Bitmap image, Point point)
        {
            const int half = 24;
            var area = new Rectangle(point.X - half, point.Y - half, half * 2, half * 2);
            if (image == null || !new Rectangle(Point.Empty, image.Size).Contains(area))
                throw new InvalidOperationException("Capture a target at least 24 pixels inside the game area.");
            using (Bitmap patch = image.Clone(area, PixelFormat.Format24bppRgb))
            using (var stream = new MemoryStream())
            {
                patch.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
        }
    }

    // User-authorized stationary point, translated by terrain; no character recognition.
    // Register against the initial scene, never cumulatively against later frames.
    internal sealed class VanillaCapturedPointTracker
    {
        private const int Samples = 12, Radius = 12, MaximumShift = 80;
        private const double MinimumScore = .84, MinimumMargin = .045;
        private readonly Size size;
        private readonly Point point;
        private readonly List<Anchor> anchors = new List<Anchor>();
        private Point previousShift;

        internal VanillaCapturedPointTracker(Bitmap reference, Point capturedPoint)
        {
            ValidateImage(reference);
            size = reference.Size; point = capturedPoint;
            if (!new Rectangle(2, 2, size.Width - 4, size.Height - 4).Contains(point))
                throw new InvalidOperationException("Captured point is outside the game.");
            var gray = new Gray(reference);
            // Observation areas, never input destinations. Exclude HUD, chat,
            // player, target and central spell effects.
            foreach (double y in new[] { .29, .44, .60, .77 })
            foreach (double x in new[] { .20, .38, .62, .80 })
            {
                var center = new Point((int)(gray.Width * x), (int)(gray.Height * y));
                var original = new Point(center.X * 2, center.Y * 2);
                if (Math.Abs(original.X - point.X) < 100 && Math.Abs(original.Y - point.Y) < 110) continue;
                if (Math.Abs(original.X - size.Width / 2) < size.Width * .17
                    && Math.Abs(original.Y - size.Height / 2) < size.Height * .20) continue;
                Anchor anchor = Anchor.Create(gray, center);
                // Establish uniqueness once before the fast unchanged-frame path
                // can be used. A periodic texture cannot identify camera position.
                if (anchor != null && Find(gray, anchor, true) != null) anchors.Add(anchor);
            }
            if (anchors.Count < 4)
                throw new InvalidOperationException("Not enough distinct terrain is visible around the captured point; capture with the game area unobstructed.");
        }

        internal Point Locate(Bitmap current)
        {
            ValidateImage(current);
            if (current.Size != size)
                throw new InvalidOperationException("Game size changed; stop and capture the stationary target again.");
            var gray = new Gray(current);
            var matches = new List<Match>();
            foreach (Anchor anchor in anchors)
            {
                Match match = Find(gray, anchor);
                if (match != null) matches.Add(match);
            }
            int required = Math.Max(3, anchors.Count / 2 + 1);
            List<Match> best = null;
            foreach (Match candidate in matches)
            {
                var agreeing = matches.Where(m => Math.Abs(m.Shift.X - candidate.Shift.X) <= 2
                    && Math.Abs(m.Shift.Y - candidate.Shift.Y) <= 2).ToList();
                if (best == null || agreeing.Count > best.Count) best = agreeing;
            }
            if (best == null || best.Count < required
                || best.Max(m => m.Anchor.Center.X) - best.Min(m => m.Anchor.Center.X) < gray.Width / 4
                || best.Max(m => m.Anchor.Center.Y) - best.Min(m => m.Anchor.Center.Y) < gray.Height / 5)
                throw new InvalidOperationException("Surrounding terrain does not establish the captured point's location; target click withheld.");
            int dx = best.Select(m => m.Shift.X).OrderBy(v => v).ElementAt(best.Count / 2);
            int dy = best.Select(m => m.Shift.Y).OrderBy(v => v).ElementAt(best.Count / 2);
            if (best.Count(m => m.Anchor.Score(gray, m.Anchor.Center.X + dx, m.Anchor.Center.Y + dy) >= MinimumScore) < required)
                throw new InvalidOperationException("Terrain matches disagree on camera movement; target click withheld.");
            var located = new Point(point.X + dx * 2, point.Y + dy * 2);
            if (!new Rectangle(2, 2, size.Width - 4, size.Height - 4).Contains(located))
                throw new InvalidOperationException("Captured point moved outside the game area; target click withheld.");
            previousShift = new Point(dx, dy);
            return located;
        }

        private Match Find(Gray image, Anchor anchor, bool requireDistinct = false)
        {
            if (!requireDistinct && anchor.Score(image, anchor.Center.X + previousShift.X, anchor.Center.Y + previousShift.Y) >= .9995)
                return new Match { Anchor = anchor, Shift = previousShift, Score = 1 };
            var candidates = new List<Match>();
            // Visit every half-resolution offset. Skipping odd offsets can miss
            // detailed grass completely after a two-pixel camera displacement.
            // A sparse prefilter keeps the bounded search inexpensive.
            for (int dy = -MaximumShift; dy <= MaximumShift; dy++)
            for (int dx = -MaximumShift; dx <= MaximumShift; dx++)
            {
                double score = anchor.CoarseScore(image, anchor.Center.X + dx, anchor.Center.Y + dy);
                if (score < .55) continue;
                candidates.Add(new Match { Anchor = anchor, Shift = new Point(dx, dy), Score = score });
                if (candidates.Count > 128) candidates = candidates.OrderByDescending(m => m.Score).Take(64).ToList();
            }
            var refined = new List<Match>();
            foreach (Match candidate in candidates.OrderByDescending(m => m.Score))
            {
                if (refined.Any(m => Math.Abs(m.Shift.X - candidate.Shift.X) <= 3 && Math.Abs(m.Shift.Y - candidate.Shift.Y) <= 3)) continue;
                Match winner = new Match { Anchor = anchor, Shift = candidate.Shift,
                    Score = anchor.Score(image, anchor.Center.X + candidate.Shift.X, anchor.Center.Y + candidate.Shift.Y) };
                for (int dy = Math.Max(-MaximumShift, candidate.Shift.Y - 2); dy <= Math.Min(MaximumShift, candidate.Shift.Y + 2); dy++)
                for (int dx = Math.Max(-MaximumShift, candidate.Shift.X - 2); dx <= Math.Min(MaximumShift, candidate.Shift.X + 2); dx++)
                {
                    double score = anchor.Score(image, anchor.Center.X + dx, anchor.Center.Y + dy);
                    if (score > winner.Score) winner = new Match { Anchor = anchor, Shift = new Point(dx, dy), Score = score };
                }
                refined.Add(winner);
                if (refined.Count == 12) break;
            }
            Match best = refined.OrderByDescending(m => m.Score).FirstOrDefault();
            if (best == null || best.Score < MinimumScore) return null;
            double rival = refined.Where(m => Math.Abs(m.Shift.X - best.Shift.X) > 5 || Math.Abs(m.Shift.Y - best.Shift.Y) > 5)
                .Select(m => m.Score).DefaultIfEmpty(-1).Max();
            return best.Score - rival >= MinimumMargin ? best : null;
        }

        private static void ValidateImage(Bitmap image)
        {
            if (image == null || image.Width < 320 || image.Height < 240 || (long)image.Width * image.Height > 16000000)
                throw new InvalidOperationException("A usable current game image is required for the captured point.");
        }
        private sealed class Match { internal Anchor Anchor; internal Point Shift; internal double Score; }
        private sealed class Anchor
        {
            internal Point Center;
            private double[] values;
            private double norm;
            private double[] coarseValues;
            private double coarseNorm;
            internal static Anchor Create(Gray image, Point center)
            {
                if (!image.Contains(center.X, center.Y)) return null;
                var values = new double[Samples * Samples];
                for (int y = 0; y < Samples; y++) for (int x = 0; x < Samples; x++)
                    values[y * Samples + x] = image.At(center.X - Radius + 1 + x * 2, center.Y - Radius + 1 + y * 2);
                double mean = values.Average();
                for (int i = 0; i < values.Length; i++) values[i] -= mean;
                double norm = Math.Sqrt(values.Sum(v => v * v));
                if (norm < 7 * Samples) return null;
                var coarse = new double[Samples * Samples / 4];
                for (int y = 0; y < Samples / 2; y++) for (int x = 0; x < Samples / 2; x++)
                    coarse[y * (Samples / 2) + x] = values[y * 2 * Samples + x * 2];
                double coarseMean = coarse.Average();
                for (int i = 0; i < coarse.Length; i++) coarse[i] -= coarseMean;
                double coarseNorm = Math.Sqrt(coarse.Sum(v => v * v));
                if (coarseNorm < 7 * Samples / 2) return null;
                return new Anchor { Center = center, values = values, norm = norm,
                    coarseValues = coarse, coarseNorm = coarseNorm };
            }
            internal double CoarseScore(Gray image, int cx, int cy)
            {
                if (!image.Contains(cx, cy)) return -1;
                double sum = 0, square = 0, dot = 0;
                for (int y = 0; y < Samples / 2; y++) for (int x = 0; x < Samples / 2; x++)
                {
                    double value = image.At(cx - Radius + 1 + x * 4, cy - Radius + 1 + y * 4);
                    sum += value; square += value * value; dot += value * coarseValues[y * (Samples / 2) + x];
                }
                double actualNorm = Math.Sqrt(Math.Max(0, square - sum * sum / (Samples * Samples / 4)));
                return actualNorm < 7 * Samples / 2 ? -1 : dot / (actualNorm * coarseNorm);
            }
            internal double Score(Gray image, int cx, int cy)
            {
                if (!image.Contains(cx, cy)) return -1;
                double sum = 0, square = 0, dot = 0;
                for (int y = 0; y < Samples; y++) for (int x = 0; x < Samples; x++)
                {
                    double value = image.At(cx - Radius + 1 + x * 2, cy - Radius + 1 + y * 2);
                    sum += value; square += value * value; dot += value * values[y * Samples + x];
                }
                double actualNorm = Math.Sqrt(Math.Max(0, square - sum * sum / (Samples * Samples)));
                return actualNorm < 7 * Samples ? -1 : dot / (actualNorm * norm);
            }
        }
        private sealed class Gray
        {
            internal readonly int Width, Height;
            private readonly byte[] values;
            internal Gray(Bitmap source)
            {
                int sourceWidth = source.Width, sourceHeight = source.Height;
                Width = sourceWidth / 2; Height = sourceHeight / 2; values = new byte[Width * Height];
                var full = new byte[sourceWidth * sourceHeight];
                using (var image = new Bitmap(sourceWidth, sourceHeight, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(image)) g.DrawImageUnscaled(source, 0, 0);
                    BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    var row = new byte[sourceWidth * 3];
                    try
                    {
                        for (int y = 0; y < sourceHeight; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (int x = 0; x < sourceWidth; x++)
                                full[y * sourceWidth + x] = (byte)((row[x * 3] * 11 + row[x * 3 + 1] * 59 + row[x * 3 + 2] * 30) / 100);
                        }
                    }
                    finally { image.UnlockBits(data); }
                }
                // Smooth before reducing for antialiasing/subpixel camera motion.
                for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                {
                    int sum = 0;
                    for (int yy = 0; yy < 4; yy++) for (int xx = 0; xx < 4; xx++)
                        sum += full[Math.Min(sourceHeight - 1, y * 2 + yy) * sourceWidth + Math.Min(sourceWidth - 1, x * 2 + xx)];
                    values[y * Width + x] = (byte)(sum / 16);
                }
            }
            internal bool Contains(int x, int y) { return x >= Radius && y >= Radius && x + Radius < Width && y + Radius < Height; }
            internal byte At(int x, int y) { return values[y * Width + x]; }
        }
    }
}
