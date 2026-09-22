using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Matches a user-captured target patch after camera/DPI changes; never guesses a new target.</summary>
    internal static class VanillaCapturedTarget
    {
        private const int Samples = 12;
        internal static string Capture(Bitmap image, Point point)
        {
            const int half = 24;
            var area = new Rectangle(point.X - half, point.Y - half, half * 2, half * 2);
            if (image == null || !new Rectangle(Point.Empty, image.Size).Contains(area))
                throw new InvalidOperationException("Capture a target at least 24 pixels inside the game area.");
            using (Bitmap patch = image.Clone(area, PixelFormat.Format24bppRgb))
            using (var stream = new MemoryStream())
            {
                if (Template(patch).Item3 < 8) throw new InvalidOperationException("Target capture is too featureless; aim at the character or its name.");
                patch.Save(stream, ImageFormat.Png);
                return Convert.ToBase64String(stream.ToArray());
            }
        }

        internal static bool TryLocate(Bitmap image, string encoded, Point expected, out Point found, out string evidence)
        {
            found = Point.Empty;
            evidence = "captured target patch unavailable";
            if (image == null || (long)image.Width * image.Height > 16000000 || string.IsNullOrEmpty(encoded) || encoded.Length > 65536) return false;
            try
            {
                using (var stream = new MemoryStream(Convert.FromBase64String(encoded)))
                using (var patch = new Bitmap(stream))
                {
                    if (patch.Width < 16 || patch.Height < 16 || patch.Width > 128 || patch.Height > 128) return false;
                    var template = Template(patch);
                    if (template.Item3 < 8) return false;
                    var pixels = new Gray(image);
                    int half = patch.Width / 2;
                    double direct = Score(pixels, expected.X, expected.Y, half, template);
                    if (direct >= .88)
                    { found = expected; evidence = "captured target confirmed at its configured position"; return true; }
                    int step = Math.Max(4, (int)Math.Ceiling(Math.Sqrt(image.Width * (double)image.Height / 24000)));
                    var candidates = new List<Candidate>();
                    foreach (double factor in new[] { .8, 1.0, 1.25, 1.5 })
                    {
                        int radius = Math.Max(10, (int)Math.Round(half * factor));
                        for (int y = radius; y < image.Height - radius; y += step)
                        for (int x = radius; x < image.Width - radius; x += step)
                        {
                            double score = Score(pixels, x, y, radius, template);
                            if (score < .65) continue;
                            candidates.Add(new Candidate { Point = new Point(x, y), Radius = radius, Score = score });
                            if (candidates.Count > 32) candidates = candidates.OrderByDescending(c => c.Score).Take(16).ToList();
                        }
                    }
                    var refined = new List<Candidate>();
                    foreach (Candidate candidate in candidates.OrderByDescending(c => c.Score).Take(12))
                    {
                        Candidate best = candidate;
                        for (int y = candidate.Point.Y - step; y <= candidate.Point.Y + step; y++)
                        for (int x = candidate.Point.X - step; x <= candidate.Point.X + step; x++)
                        {
                            double score = Score(pixels, x, y, candidate.Radius, template);
                            if (score > best.Score) best = new Candidate { Point = new Point(x, y), Radius = candidate.Radius, Score = score };
                        }
                        refined.Add(best);
                    }
                    Candidate winner = refined.OrderByDescending(c => c.Score).FirstOrDefault();
                    if (winner == null || winner.Score < .83) { evidence = "captured target is not confidently visible"; return false; }
                    double rival = refined.Where(c => Math.Abs(c.Point.X - winner.Point.X) > winner.Radius
                        || Math.Abs(c.Point.Y - winner.Point.Y) > winner.Radius).Select(c => c.Score).DefaultIfEmpty(0).Max();
                    if (winner.Score - rival < .06) { evidence = "captured target has multiple plausible matches"; return false; }
                    found = winner.Point;
                    evidence = "captured target reacquired from its image patch";
                    return true;
                }
            }
            catch (ArgumentException) { evidence = "target capture is invalid; capture again"; return false; }
            catch (FormatException) { evidence = "target capture is invalid; capture again"; return false; }
        }

        private sealed class Candidate { internal Point Point; internal int Radius; internal double Score; }
        private static Tuple<double[], double, double> Template(Bitmap image)
        {
            var pixels = new Gray(image);
            var values = new double[Samples * Samples];
            for (int y = 0; y < Samples; y++) for (int x = 0; x < Samples; x++)
                values[y * Samples + x] = pixels.At((2 * x + 1) * image.Width / (2 * Samples), (2 * y + 1) * image.Height / (2 * Samples));
            double mean = values.Average(), norm = Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)));
            return Tuple.Create(values.Select(v => v - mean).ToArray(), norm, norm / Samples);
        }
        private static double Score(Gray image, int centerX, int centerY, int radius, Tuple<double[], double, double> template)
        {
            if (centerX < radius || centerY < radius || centerX + radius >= image.Width || centerY + radius >= image.Height) return -1;
            double sum = 0, square = 0, dot = 0;
            for (int y = 0; y < Samples; y++) for (int x = 0; x < Samples; x++)
            {
                double value = image.At(centerX - radius + (2 * x + 1) * radius / Samples,
                    centerY - radius + (2 * y + 1) * radius / Samples);
                sum += value; square += value * value; dot += value * template.Item1[y * Samples + x];
            }
            double norm = Math.Sqrt(Math.Max(0, square - sum * sum / (Samples * Samples)));
            return norm < 8 * Samples ? -1 : dot / (norm * template.Item2);
        }
        private sealed class Gray
        {
            internal int Width, Height;
            private readonly byte[] values;
            internal Gray(Bitmap source)
            {
                Width = source.Width; Height = source.Height; values = new byte[Width * Height];
                using (var image = new Bitmap(Width, Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(image)) g.DrawImageUnscaled(source, 0, 0);
                    BitmapData data = image.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    var row = new byte[Width * 3];
                    try
                    {
                        for (int y = 0; y < Height; y++)
                        {
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                            for (int x = 0; x < Width; x++) values[y * Width + x] = (byte)((row[x * 3] * 11 + row[x * 3 + 1] * 59 + row[x * 3 + 2] * 30) / 100);
                        }
                    }
                    finally { image.UnlockBits(data); }
                }
            }
            internal byte At(int x, int y) { return values[y * Width + x]; }
        }
    }
}
