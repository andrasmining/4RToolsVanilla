using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaServiceRow
    {
        public string Name { get; set; }
        public Rectangle Bounds { get; set; }
        public bool IsHighlighted { get; set; }
    }

    internal sealed class VanillaServiceDialog
    {
        internal Rectangle Bounds;
        internal VanillaTextLine[] Lines;
        internal VanillaRecognitionPixels Pixels;
        internal List<Rectangle> CandidateTextAreas;
        internal string Evidence;
    }

    /// <summary>
    /// Locates a real service form before interpreting its text. Geometry only limits the
    /// OCR region; every actionable row comes from observed words, never an index/offset.
    /// Selection is independently proved from the strip behind that same observed row.
    /// </summary>
    internal static class VanillaServiceRecognition
    {
        private sealed class HeaderBand
        {
            internal Rectangle Bounds;
            internal int LastY;
        }
        private sealed class Detection
        {
            internal VanillaServiceDialog Dialog;
            internal string Evidence;
        }
        // Cache only within the exact bitmap object. A fresh capture always receives
        // fresh OCR/highlight evidence, even if its dimensions/position did not change.
        private static readonly ConditionalWeakTable<Bitmap, Detection> Cache = new ConditionalWeakTable<Bitmap, Detection>();

        internal static bool TryDetect(Bitmap bitmap, out VanillaServiceDialog dialog, out string evidence)
        {
            dialog = null;
            evidence = "service dialog image unavailable";
            if (bitmap == null || bitmap.Width < 320 || bitmap.Height < 240
                || bitmap.Width > 8192 || bitmap.Height > 8192) return false;
            Detection detection = Cache.GetValue(bitmap, ReadDialog);
            dialog = detection.Dialog; evidence = detection.Evidence;
            return dialog != null;
        }

        private static Detection ReadDialog(Bitmap bitmap)
        {
            return ReadDialog(bitmap, null);
        }

        // Tests call this explicitly on generated fixtures. Production recognition never
        // records OCR text from captures, regardless of environment variables.
        internal static string DescribeSyntheticFixture(Bitmap bitmap)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOURRTOOLS_ISOLATED_DESKTOP")))
                return "Synthetic diagnostics are available only in isolated tests.";
            var report = new StringBuilder();
            Detection detection = ReadDialog(bitmap, text => report.AppendLine(text));
            if (detection.Dialog != null)
            {
                foreach (Rectangle area in ObservedTextAreas(detection.Dialog))
                {
                    VanillaTextLine[] accurate;
                    string evidence;
                    VanillaTextRecognition.TryReadPixelPreservingLine(bitmap, area, out accurate, out evidence);
                    report.AppendLine("Nearest-neighbor accurate crop=" + area + "; " + evidence + "; " + DescribeLines(accurate));
                }
                report.Append(VanillaProxyPattern.DescribeSyntheticSuffixes(bitmap, detection.Dialog));
            }
            report.AppendLine("Result: " + detection.Evidence);
            return report.ToString();
        }

        private static Detection ReadDialog(Bitmap bitmap, Action<string> trace)
        {
            var result = new Detection { Evidence = "no positively recognized Select Service form" };
            var pixels = new VanillaRecognitionPixels(bitmap);
            List<Rectangle> headers = FindHeaders(pixels);
            var candidateTextAreas = new List<Rectangle>();
            if (trace != null) trace("Header candidates=" + headers.Count + ": " + string.Join("; ", headers));
            if (headers.Count > 16)
            { result.Evidence = "too many possible service forms; no input authorized"; return result; }
            foreach (Rectangle header in headers)
            {
                Rectangle titleArea;
                if (!TryTitleTextArea(pixels, header, out titleArea, trace)) continue;
                candidateTextAreas.Add(titleArea);
                VanillaTextLine[] title;
                string ocrEvidence;
                if (!VanillaTextRecognition.TryRead(bitmap, titleArea, true, out title, out ocrEvidence))
                { result.Evidence = ocrEvidence; return result; }
                if (trace != null) trace("Title header=" + header + "; crop=" + titleArea + "; " + DescribeLines(title));
                if (!title.Any(IsServiceTitle)) continue;

                // The title bar is detected anywhere on the captured client. This bounded
                // region is for observation only; it does not manufacture a clickable row.
                Rectangle body = Rectangle.FromLTRB(Math.Max(0, header.Left + 2), header.Bottom + 1,
                    Math.Min(bitmap.Width, header.Right - 2),
                    Math.Min(bitmap.Height, header.Bottom + Math.Max(100, (int)(header.Width * 1.15))));
                VanillaTextLine[] lines;
                if (!VanillaTextRecognition.TryRead(bitmap, body, false, out lines, out ocrEvidence))
                { result.Evidence = ocrEvidence; return result; }
                lines = CombineAlignedText(lines);
                if (trace != null) trace("Body crop=" + body + "; " + DescribeLines(lines));
                if (result.Dialog != null)
                {
                    result.Dialog = null;
                    result.Evidence = "multiple Select Service forms; ambiguous capture";
                    return result;
                }
                result.Dialog = new VanillaServiceDialog { Bounds = Rectangle.Union(header, body), Lines = lines,
                    Pixels = pixels, CandidateTextAreas = candidateTextAreas,
                    Evidence = "Select Service title recognized; observed text lines=" + lines.Length };
                result.Evidence = result.Dialog.Evidence;
            }
            return result;
        }

        internal static VanillaTextLine[] CombineAlignedText(VanillaTextLine[] lines)
        {
            // Sparse OCR may split one physical row into several blocks at high DPI.
            // Reassemble only observed words that share a baseline and a bounded gap.
            // No expected row count, service order or configured label participates.
            var groups = new List<List<VanillaTextWord>>();
            VanillaTextWord[] observed = lines.SelectMany(line => line.Words ?? new VanillaTextWord[0]).Take(513).ToArray();
            if (observed.Length > 512) return new VanillaTextLine[0];
            foreach (VanillaTextWord word in observed.OrderBy(word => word.Bounds.Top + word.Bounds.Height / 2.0).ThenBy(word => word.Bounds.Left))
            {
                List<VanillaTextWord>[] adjacent = groups.Where(candidate => candidate.Any(other =>
                {
                    int overlap = Math.Min(word.Bounds.Bottom, other.Bounds.Bottom) - Math.Max(word.Bounds.Top, other.Bounds.Top);
                    int gap = Math.Max(word.Bounds.Left - other.Bounds.Right, other.Bounds.Left - word.Bounds.Right);
                    return overlap >= Math.Min(word.Bounds.Height, other.Bounds.Height) * .5
                        && gap <= Math.Max(24, Math.Max(word.Bounds.Height, other.Bounds.Height) * 3);
                })).ToArray();
                if (adjacent.Length == 0) groups.Add(new List<VanillaTextWord> { word });
                else
                {
                    adjacent[0].Add(word);
                    foreach (List<VanillaTextWord> other in adjacent.Skip(1))
                    { adjacent[0].AddRange(other); groups.Remove(other); }
                }
            }
            return groups.Select(group =>
            {
                VanillaTextWord[] words = group.OrderBy(word => word.Bounds.Left).ToArray();
                Rectangle bounds = words[0].Bounds;
                foreach (VanillaTextWord word in words.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
                return new VanillaTextLine { Text = string.Join(" ", words.Select(word => word.Text)), Bounds = bounds,
                    Confidence = words.Average(word => word.Confidence), Words = words };
            }).OrderBy(line => line.Bounds.Top).ToArray();
        }

        private static Rectangle[] ObservedTextAreas(VanillaServiceDialog dialog)
        {
            return dialog.CandidateTextAreas.Concat(dialog.Lines.Select(line => Rectangle.Intersect(dialog.Bounds,
                    Rectangle.Inflate(line.Bounds, 2, 2))))
                .Where(area => area.Top > dialog.Bounds.Top && dialog.Bounds.Contains(area)).Distinct().Take(24).ToArray();
        }

        private static string DescribeLines(VanillaTextLine[] lines)
        {
            return string.Join(" | ", lines.Select(line => "'" + line.Text + "' @" + line.Bounds
                + " confidence=" + line.Confidence.ToString("0.0") + " words="
                + string.Join(", ", (line.Words ?? new VanillaTextWord[0]).Select(word => word.Text + ":" + word.Confidence.ToString("0.0")))));
        }

        internal static bool TryTitleTextArea(VanillaRecognitionPixels pixels, Rectangle header, out Rectangle area, Action<string> trace, int minimumComponents = 2)
        {
            area = Rectangle.Empty;
            var ink = new bool[header.Width * header.Height];
            for (int y = header.Top; y < header.Bottom; y++)
                for (int x = header.Left; x < header.Right; x++)
                    ink[(y - header.Top) * header.Width + x - header.Left] = pixels.NeutralInk(x, y);
            var queue = new int[ink.Length];
            var kept = new List<Rectangle>();
            for (int seed = 0; seed < ink.Length; seed++)
            {
                if (!ink[seed]) continue;
                int count = 1, next = 0;
                queue[0] = seed; ink[seed] = false;
                int left = header.Width, right = -1, top = header.Height, bottom = -1;
                while (next < count)
                {
                    int position = queue[next++], x = position % header.Width, y = position / header.Width;
                    left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if (nx < 0 || nx >= header.Width || ny < 0 || ny >= header.Height) continue;
                            int neighbor = ny * header.Width + nx;
                            if (!ink[neighbor]) continue;
                            ink[neighbor] = false; queue[count++] = neighbor;
                        }
                }
                var component = new Rectangle(header.Left + left, header.Top + top, right - left + 1, bottom - top + 1);
                // Resampling breaks borders into fragments, so row/column averages are
                // insufficient. Exclude connected edge fragments and long frame rules.
                // All retained glyph bounds must be inside the observed title surface.
                if (count < 2 || left <= 1 || top <= 1 || right >= header.Width - 2 || bottom >= header.Height - 2
                    || (component.Height >= header.Height * .75 && component.Height >= component.Width * 3)
                    || (component.Width >= header.Width * .15 && component.Width >= component.Height * 8)) continue;
                kept.Add(component);
            }
            if (trace != null) trace("Retained title ink components=" + string.Join("; ", kept));
            if (kept.Count < minimumComponents) return false;
            Rectangle text = kept[0];
            foreach (Rectangle component in kept.Skip(1)) text = Rectangle.Union(text, component);
            if (text.Width < 20 || text.Height < 5) return false;
            area = Rectangle.Intersect(header, Rectangle.Inflate(text, 2, 2));
            return area.Width > 20 && area.Height > 4;
        }

        private static bool IsServiceTitle(VanillaTextLine line)
        {
            if (line.Confidence < 55) return false;
            if (Letters(line.Text) == "SELECTSERVICE") return true;
            // The native title begins with a small round blue icon. OCR may expose it
            // as an isolated letter; ignore only one geometrically separate icon-sized
            // token, not an arbitrary prefix or any part of either title word.
            VanillaTextWord[] words = line.Words ?? new VanillaTextWord[0];
            return words.Length == 3 && words[0].Bounds.Width <= words[0].Bounds.Height * 1.4
                && words[0].Bounds.Right < words[1].Bounds.Left
                && words[1].Confidence >= 65 && words[2].Confidence >= 65
                && Letters(words[1].Text) == "SELECT" && Letters(words[2].Text) == "SERVICE";
        }

        internal static List<Rectangle> FindHeaders(VanillaRecognitionPixels pixels)
        {
            var bands = new List<HeaderBand>();
            for (int y = 0; y < pixels.Height; y++)
            {
                int x = 0;
                while (x < pixels.Width)
                {
                    if (!pixels.Blue(x, y)) { x++; continue; }
                    int left = x, lastBlue = x;
                    while (++x < pixels.Width)
                    {
                        if (pixels.Blue(x, y)) lastBlue = x;
                        else if (x - lastBlue > 3) break;
                    }
                    int width = lastBlue - left + 1;
                    if (width < 110 || width > 1600) continue;
                    var line = new Rectangle(left, y, width, 1);
                    HeaderBand prior = bands.LastOrDefault(band => y - band.LastY <= 1
                        && Math.Min(band.Bounds.Right, line.Right) - Math.Max(band.Bounds.Left, line.Left)
                            >= Math.Min(band.Bounds.Width, line.Width) * .65);
                    if (prior == null) bands.Add(new HeaderBand { Bounds = line, LastY = y });
                    else { prior.Bounds = Rectangle.Union(prior.Bounds, line); prior.LastY = y; }
                    if (bands.Count > 512) return new List<Rectangle>();
                }
            }
            return bands.Where(band => band.Bounds.Height >= 4 && band.Bounds.Height <= 65
                    && band.Bounds.Width >= band.Bounds.Height * 5
                    && band.Bounds.Width <= band.Bounds.Height * 70
                    && HasTitleInk(pixels, band.Bounds))
                .Select(band => band.Bounds).ToList();
        }

        private static bool HasTitleInk(VanillaRecognitionPixels pixels, Rectangle bounds)
        {
            int dark = 0, blue = 0, total = bounds.Width * bounds.Height;
            for (int y = bounds.Top; y < bounds.Bottom; y++)
                for (int x = bounds.Left; x < bounds.Right; x++)
                {
                    if (pixels.Dark(x, y)) dark++;
                    if (pixels.Blue(x, y)) blue++;
                }
            return dark >= 12 && dark < total * .35 && blue > total * .45;
        }

        internal static string Letters(string text)
        {
            return new string((text ?? "").Where(char.IsLetter).Select(char.ToUpperInvariant).ToArray());
        }

        internal static string Normalize(string text)
        {
            return Regex.Replace((text ?? "").Trim(), @"\s+", " ");
        }

        internal static bool IsHighlighted(VanillaServiceDialog dialog, Rectangle text)
        {
            VanillaRecognitionPixels pixels = dialog.Pixels;
            if (text.Width < 8 || text.Height < 4 || !dialog.Bounds.Contains(text)) return false;
            int blue = 0, total = 0;
            for (int y = text.Top; y < text.Bottom; y++)
                for (int x = text.Left; x < text.Right; x++)
                { total++; if (pixels.Blue(x, y)) blue++; }
            if (blue < total * .42) return false;

            // Prove one finite horizontal selection strip, including background beyond
            // the word. A blue title, blue wallpaper or a uniformly blue list is not a row.
            int centerY = text.Top + text.Height / 2;
            int anchor = -1;
            for (int x = text.Right; x < dialog.Bounds.Right - 2; x++)
                if (pixels.Blue(x, centerY)) { anchor = x; break; }
            if (anchor < 0) return false;
            int left = anchor, right = anchor;
            while (left > dialog.Bounds.Left && pixels.Blue(left - 1, centerY)) left--;
            while (right + 1 < dialog.Bounds.Right && pixels.Blue(right + 1, centerY)) right++;
            // Text interrupts horizontal runs, so the clear tail must extend past the
            // actual recognized label and form a tall-enough vertical background strip.
            if (right - anchor < Math.Max(3, text.Height / 3)) return false;
            int top = centerY, bottom = centerY;
            while (top > dialog.Bounds.Top && pixels.Blue(anchor, top - 1)) top--;
            while (bottom + 1 < dialog.Bounds.Bottom && pixels.Blue(anchor, bottom + 1)) bottom++;
            int stripHeight = bottom - top + 1;
            return top <= text.Top + 2 && bottom >= text.Bottom - 3
                && stripHeight >= text.Height * .7 && stripHeight <= text.Height * 2.8 + 2;
        }

        internal static bool TryServer(Bitmap bitmap, out VanillaServerLayout layout, out string evidence)
        {
            layout = null;
            VanillaServiceDialog dialog;
            if (!TryDetect(bitmap, out dialog, out evidence)) return false;
            var matches = new List<VanillaServiceRow>();
            foreach (VanillaTextLine line in dialog.Lines)
                AddServerMatch(dialog, line, matches);
            if (matches.Count == 0)
            {
                // Use the accurate model only on observed text or selected-strip ink.
                // This includes tiny text sparse OCR omitted, without inventing a row.
                Rectangle[] areas = ObservedTextAreas(dialog);
                foreach (Rectangle area in areas)
                {
                    VanillaTextLine[] accurate;
                    string accurateEvidence;
                    if (!VanillaTextRecognition.TryReadPixelPreservingLine(bitmap, area, out accurate, out accurateEvidence)) continue;
                    foreach (VanillaTextLine line in CombineAlignedText(accurate)) AddServerMatch(dialog, line, matches);
                }
            }
            if (matches.Count != 1)
            { evidence = "Select Service form has " + matches.Count + " exact Vanilla MMO rows; no selection authorized"; return false; }
            VanillaServiceRow row = matches[0];
            evidence = "recognized exact Vanilla MMO service; highlighted=" + row.IsHighlighted + "; textBounds=" + row.Bounds;
            layout = new VanillaServerLayout { Dialog = dialog.Bounds, ServerName = row.Name,
                ServerRow = row.Bounds, IsHighlighted = row.IsHighlighted, Evidence = evidence };
            return true;
        }

        private static void AddServerMatch(VanillaServiceDialog dialog, VanillaTextLine line, List<VanillaServiceRow> matches)
        {
            // Status is optional and semantically distinct from the server identity.
            // Never accept a substring such as Other Vanilla MMO or Vanilla MMO Test.
            if (line.Confidence < 65 || !Regex.IsMatch(Normalize(line.Text),
                @"^(?:(?:Crowded|Normal|Busy)\s+)?Vanilla\s+MMO$", RegexOptions.IgnoreCase)) return;
            VanillaTextWord[] identity = line.Words.Skip(Math.Max(0, line.Words.Length - 2)).ToArray();
            if (identity.Length != 2 || identity.Any(word => word.Confidence < 55)) return;
            Rectangle bounds = Rectangle.Union(identity[0].Bounds, identity[1].Bounds);
            if (matches.Any(match => match.Bounds.IntersectsWith(bounds))) return;
            matches.Add(new VanillaServiceRow { Name = "Vanilla MMO", Bounds = bounds,
                IsHighlighted = IsHighlighted(dialog, bounds) });
        }
    }
}
