using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaLoginLayout
    {
        public Rectangle UserName { get; set; }
        public Rectangle Password { get; set; }
        public Rectangle ServiceControl { get; set; }
        public Rectangle UserNameControl { get; set; }
        public Rectangle PasswordControl { get; set; }
        public string Evidence { get; set; }
    }

    internal sealed class VanillaServerLayout
    {
        public Rectangle Dialog { get; set; }
        public Rectangle ServerRow { get; set; }
        public string ServerName { get; set; }
        public bool IsHighlighted { get; set; }
        public string Evidence { get; set; }
    }

    internal static class VanillaAuthPattern
    {
        private sealed class EdgeLine
        {
            public int Y, Left, Right;
            public int Width { get { return Right - Left + 1; } }
            public double CenterX { get { return (Left + Right) / 2.0; } }
        }

        private sealed class ControlBox
        {
            public EdgeLine Top, Bottom;
            public int Left, Right;
            public int Width { get { return Right - Left + 1; } }
            public int Height { get { return Bottom.Y - Top.Y + 1; } }
            public double CenterX { get { return (Left + Right) / 2.0; } }
            public double CenterY { get { return (Top.Y + Bottom.Y) / 2.0; } }
        }

        internal static bool TryDetectLogin(Bitmap bitmap, out VanillaLoginLayout layout, out string evidence)
        {
            List<Rectangle> candidates;
            return TryDetectLogin(bitmap, out layout, out evidence, out candidates);
        }

        internal static bool TryDetectLogin(Bitmap bitmap, out VanillaLoginLayout layout, out string evidence,
            out List<Rectangle> candidateServices)
        {
            layout = null;
            // Bounds-only diagnostics. Production never exports the OCR content of login captures.
            candidateServices = new List<Rectangle>();
            evidence = "login controls not detected";
            if (!Usable(bitmap, out evidence)) return false;

            byte[] gray = Gray(bitmap);
            int width = bitmap.Width, height = bitmap.Height;
            // Locate the form itself. UI scale and window size are independent in Vanilla.
            Rectangle search = new Rectangle(2, 2, width - 4, height - 4);
            List<EdgeLine> lines = FindHorizontalLines(gray, width, height, search,
                55, Math.Min(width - 8, 600), 0.0, 1.0);
            List<ControlBox> boxes = BuildControlBoxes(lines, width,
                7, 70);
            if (boxes.Count < 3 || boxes.Count > 160)
            {
                evidence = "found " + lines.Count + " candidate edges but only " + boxes.Count + " plausible login rectangles in " + search;
                return false;
            }

            var candidates = new List<Tuple<ControlBox[], double>>();
            for (int a = 0; a < boxes.Count - 2; a++)
            for (int b = a + 1; b < boxes.Count - 1; b++)
            for (int c = b + 1; c < boxes.Count; c++)
            {
                ControlBox first = boxes[a], second = boxes[b], third = boxes[c];
                if (first.Top.Y >= second.Top.Y || second.Top.Y >= third.Top.Y) continue;

                double centerSpread = Math.Max(first.CenterX, Math.Max(second.CenterX, third.CenterX))
                    - Math.Min(first.CenterX, Math.Min(second.CenterX, third.CenterX));
                if (centerSpread > width * 0.030) continue;

                double meanWidth = (first.Width + second.Width + third.Width) / 3.0;
                double widthSpread = (Math.Max(first.Width, Math.Max(second.Width, third.Width))
                    - Math.Min(first.Width, Math.Min(second.Width, third.Width))) / Math.Max(1.0, meanWidth);
                if (widthSpread > 0.32) continue;

                double meanHeight = (first.Height + second.Height + third.Height) / 3.0;
                double heightSpread = (Math.Max(first.Height, Math.Max(second.Height, third.Height))
                    - Math.Min(first.Height, Math.Min(second.Height, third.Height))) / Math.Max(1.0, meanHeight);
                if (heightSpread > 0.45) continue;

                int inter1 = second.Top.Y - first.Bottom.Y;
                int inter2 = third.Top.Y - second.Bottom.Y;
                int maxInter = Math.Max(14, (int)(meanHeight * 0.85));
                if (inter1 < -3 || inter2 < -3 || inter1 > maxInter || inter2 > maxInter) continue;

                double topGap1 = second.Top.Y - first.Top.Y;
                double topGap2 = third.Top.Y - second.Top.Y;
                double topGapMean = (topGap1 + topGap2) / 2.0;
                if (topGapMean < 10 || topGapMean > 100) continue;
                double spacingError = Math.Abs(topGap1 - topGap2) / topGapMean;
                if (spacingError > 0.38) continue;

                double meanX = (first.CenterX + second.CenterX + third.CenterX) / 3.0;
                double meanY = (first.CenterY + second.CenterY + third.CenterY) / 3.0;
                double score = spacingError * 3.0 + widthSpread * 2.0 + heightSpread
                    + Math.Abs(meanX / width - 0.49) * 2.0
                    + Math.Abs(meanY / height - 0.65);
                candidates.Add(Tuple.Create(new[] { first, second, third }, score));
            }
            if (candidates.Count == 0 || candidates.Count > 256)
            {
                evidence = "no bounded, unambiguous set of stacked login controls matched among " + boxes.Count + " rectangles";
                return false;
            }

            ControlBox[] best = null;
            double bestScore = double.MaxValue;
            Rectangle serviceControl = Rectangle.Empty;
            var identities = new Dictionary<Rectangle, bool>();
            var labelEvidence = new List<string>();
            foreach (var candidate in candidates.OrderBy(value => value.Item2))
            {
                ControlBox first = candidate.Item1[0];
                Rectangle service = LoginServiceTextInterior(first, gray, width);
                bool recognized;
                if (!identities.TryGetValue(service, out recognized))
                {
                    if (identities.Count >= 24) { evidence = "too many distinct candidate login services"; return false; }
                    candidateServices.Add(service);
                    VanillaTextLine[] serviceText;
                    string textEvidence;
                    recognized = VanillaTextRecognition.TryRead(bitmap, service, true, out serviceText, out textEvidence)
                        && serviceText.Any(line => line.Confidence >= 70
                            && string.Equals(line.Text.Trim(), "Vanilla MMO", StringComparison.OrdinalIgnoreCase));
                    if (!recognized && service.Height <= 18)
                    {
                        recognized = VanillaSmallLabelPattern.IsVanillaMmo(bitmap, service, out textEvidence);
                        labelEvidence.Add(service + ": " + textEvidence);
                    }
                    identities.Add(service, recognized);
                }
                if (!recognized) continue;
                if (best != null)
                {
                    bool same = Enumerable.Range(0, 3).All(index =>
                        Math.Abs(best[index].CenterX - candidate.Item1[index].CenterX) <= 3
                        && Math.Abs(best[index].CenterY - candidate.Item1[index].CenterY) <= 3
                        && Math.Abs(best[index].Width - candidate.Item1[index].Width) <= 6);
                    if (!same) { evidence = "more than one named login form is plausible"; return false; }
                    continue;
                }
                best = candidate.Item1;
                bestScore = candidate.Item2;
                serviceControl = service;
            }
            if (best == null)
            {
                evidence = "stacked rectangles found, but the login service identity was not confirmed"
                    + (labelEvidence.Count == 0 ? "" : "; " + string.Join(" | ", labelEvidence));
                return false;
            }

            Rectangle user = SafeInterior(best[1]);
            Rectangle password = SafeInterior(best[2]);
            if (user.Width < 20 || user.Height < 3 || password.Width < 20 || password.Height < 3)
            {
                evidence = "detected login rectangles produced unsafe interior areas";
                return false;
            }

            evidence = "detected three separate stacked controls; score=" + bestScore.ToString("0.000")
                + "; boxes=" + string.Join(" | ", best.Select((box, index) => index + ":" + Describe(box)))
                + "; username=" + user + "; password=" + password;
            layout = new VanillaLoginLayout
            {
                UserName = user, Password = password, ServiceControl = serviceControl,
                UserNameControl = Rectangle.FromLTRB(best[1].Left + 2, best[1].Top.Y + 1, best[1].Right - 1, best[1].Bottom.Y),
                PasswordControl = Rectangle.FromLTRB(best[2].Left + 2, best[2].Top.Y + 1, best[2].Right - 1, best[2].Bottom.Y),
                Evidence = evidence
            };
            return true;
        }

        private static Rectangle LoginServiceTextInterior(ControlBox box, byte[] gray, int width)
        {
            int top = box.Top.Y + 2, bottom = box.Bottom.Y - 1;
            int right = box.Right - 2;
            // Exclude a dropdown arrow only after finding its actual vertical separator.
            // No arbitrary text suffix or fixed-width crop can establish the service identity.
            for (int x = box.Left + box.Width / 2; x < box.Right - 3; x++)
            {
                int buttonWidth = box.Right - x;
                if (buttonWidth < box.Height * 0.5 || buttonWidth > box.Height * 1.8) continue;
                int edge = 0;
                // The separator spans the control interior. A short capital/letter stroke
                // can span nearly all of a tiny OCR crop after downscaling, so do not trim
                // those top/bottom rows before evaluating the structural vertical edge.
                for (int y = top; y < bottom; y++)
                    if (Math.Abs(gray[y * width + x + 1] - gray[y * width + x - 1]) >= 25) edge++;
                if (edge >= Math.Max(6, (bottom - top) * 0.85)) { right = x - 1; break; }
            }
            return Rectangle.FromLTRB(box.Left + 3, top, right, bottom);
        }

        internal static bool TryDetectServerDialog(Bitmap bitmap, out VanillaServerLayout layout, out string evidence)
        {
            return VanillaServiceRecognition.TryServer(bitmap, out layout, out evidence);
        }
        private static Rectangle SafeInterior(ControlBox box)
        {
            int insetX = Math.Max(2, box.Width / 9);
            int insetY = Math.Max(1, box.Height / 5);
            int left = box.Left + insetX, right = box.Right - insetX;
            int top = box.Top.Y + insetY, bottom = box.Bottom.Y - insetY;
            if (right <= left) { left = box.Left + 1; right = box.Right - 1; }
            if (bottom <= top) { top = box.Top.Y + 1; bottom = box.Bottom.Y - 1; }
            return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        private static List<ControlBox> BuildControlBoxes(List<EdgeLine> lines, int width, int minHeight, int maxHeight)
        {
            var boxes = new List<ControlBox>();
            for (int a = 0; a < lines.Count - 1; a++)
            for (int b = a + 1; b < lines.Count; b++)
            {
                EdgeLine top = lines[a], bottom = lines[b];
                int boxHeight = bottom.Y - top.Y;
                if (boxHeight < minHeight || boxHeight > maxHeight) continue;
                int left = Math.Max(top.Left, bottom.Left), right = Math.Min(top.Right, bottom.Right);
                int overlap = right - left + 1;
                if (overlap < Math.Min(top.Width, bottom.Width) * 0.72) continue;
                if (Math.Abs(top.CenterX - bottom.CenterX) > width * 0.025) continue;
                double widthDiff = Math.Abs(top.Width - bottom.Width) / (double)Math.Max(top.Width, bottom.Width);
                if (widthDiff > 0.28) continue;
                boxes.Add(new ControlBox { Top = top, Bottom = bottom, Left = left, Right = right });
            }
            return boxes.OrderBy(box => box.Top.Y).ThenBy(box => box.Bottom.Y).ToList();
        }

        private static string Describe(ControlBox box)
        {
            return "{" + box.Left + "," + box.Top.Y + ".." + box.Right + "," + box.Bottom.Y + "}";
        }

        private static bool Usable(Bitmap bitmap, out string evidence)
        {
            if (bitmap == null) { evidence = "bitmap is null"; return false; }
            if (bitmap.Width < 480 || bitmap.Height < 320)
            {
                evidence = "bitmap too small: " + bitmap.Width + "x" + bitmap.Height;
                return false;
            }
            evidence = null;
            return true;
        }

        private static List<EdgeLine> FindHorizontalLines(byte[] gray, int width, int height, Rectangle search,
            int minWidth, int maxWidth, double minCenterX, double maxCenterX)
        {
            List<EdgeLine> raw = FindHorizontalLines(gray, width, height, search, minWidth, maxWidth, minCenterX, maxCenterX, 20);
            if (raw.Count < 3) raw = FindHorizontalLines(gray, width, height, search, minWidth, maxWidth, minCenterX, maxCenterX, 11);
            int clusterTolerance = Math.Max(2, height / 320);
            var clustered = new List<EdgeLine>();
            foreach (EdgeLine candidate in raw.OrderBy(line => line.Y).ThenByDescending(line => line.Width))
            {
                int index = clustered.FindIndex(line => Math.Abs(line.Y - candidate.Y) <= clusterTolerance
                    && HorizontalOverlap(line, candidate) >= Math.Min(line.Width, candidate.Width) * 0.50);
                if (index < 0) clustered.Add(candidate);
                else if (candidate.Width > clustered[index].Width) clustered[index] = candidate;
            }
            return clustered.OrderBy(line => line.Y).ToList();
        }

        private static List<EdgeLine> FindHorizontalLines(byte[] gray, int width, int height, Rectangle search,
            int minWidth, int maxWidth, double minCenterX, double maxCenterX, int threshold)
        {
            var lines = new List<EdgeLine>();
            int allowedGap = Math.Max(3, width / 550);
            for (int y = Math.Max(1, search.Top); y < Math.Min(height - 1, search.Bottom); y++)
            {
                int bestLeft = -1, bestRight = -1;
                int start = -1, lastEdge = -1000;
                for (int x = Math.Max(1, search.Left); x < Math.Min(width - 1, search.Right); x++)
                {
                    int gradient = Math.Abs(gray[(y + 1) * width + x] - gray[(y - 1) * width + x]);
                    if (gradient >= threshold)
                    {
                        if (start < 0 || x - lastEdge > allowedGap) start = x;
                        lastEdge = x;
                    }
                    if (start >= 0 && (x - lastEdge > allowedGap || x == search.Right - 1))
                    {
                        int right = lastEdge;
                        int span = right - start + 1;
                        double center = (start + right) / 2.0 / width;
                        if (span >= minWidth && span <= maxWidth && center >= minCenterX && center <= maxCenterX
                            && (bestLeft < 0 || span > bestRight - bestLeft + 1))
                        {
                            bestLeft = start;
                            bestRight = right;
                        }
                        start = -1;
                    }
                }
                if (bestLeft >= 0) lines.Add(new EdgeLine { Y = y, Left = bestLeft, Right = bestRight });
            }
            return lines;
        }

        private static int HorizontalOverlap(EdgeLine a, EdgeLine b)
        {
            return Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + 1);
        }

        private static byte[] Gray(Bitmap bitmap)
        {
            Bitmap source = bitmap.PixelFormat == PixelFormat.Format24bppRgb
                ? bitmap
                : Clone24(bitmap);
            bool dispose = !object.ReferenceEquals(source, bitmap);
            BitmapData data = source.LockBits(new Rectangle(0, 0, source.Width, source.Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] pixels = new byte[stride * source.Height];
                Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                byte[] gray = new byte[source.Width * source.Height];
                for (int y = 0; y < source.Height; y++)
                {
                    int row = data.Stride >= 0 ? y * stride : (source.Height - 1 - y) * stride;
                    for (int x = 0; x < source.Width; x++)
                    {
                        int offset = row + x * 3;
                        int b = pixels[offset], g = pixels[offset + 1], r = pixels[offset + 2];
                        gray[y * source.Width + x] = (byte)((30 * r + 59 * g + 11 * b) / 100);
                    }
                }
                return gray;
            }
            finally
            {
                source.UnlockBits(data);
                if (dispose) source.Dispose();
            }
        }

        private static Bitmap Clone24(Bitmap bitmap)
        {
            var clone = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(clone)) graphics.DrawImageUnscaled(bitmap, 0, 0);
            return clone;
        }
    }
}
