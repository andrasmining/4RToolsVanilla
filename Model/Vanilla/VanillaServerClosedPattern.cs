using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Recognizes the exact server-unavailable message inside an observed Message form.
    /// A separate Please wait form is not evidence and does not hide the error above it.
    /// No input, desktop capture, fixed screen rectangle or expected-text OCR hints.
    /// </summary>
    internal static class VanillaServerClosedPattern
    {
        internal static bool TryDetect(Bitmap bitmap, out Rectangle dialog, out string evidence)
        {
            dialog = Rectangle.Empty;
            evidence = "exact Server Closed.(1) Message dialog not found";
            if (bitmap == null || bitmap.Width < 320 || bitmap.Height < 240
                || bitmap.Width > 4096 || bitmap.Height > 4096) return false;
            var pixels = new VanillaRecognitionPixels(bitmap);
            var headers = VanillaServiceRecognition.FindHeaders(pixels);
            if (headers.Count > 24) { evidence = "too many ambiguous dialog headers"; return false; }
            var matches = new List<Rectangle>();
            foreach (Rectangle header in headers)
            {
                Rectangle titleArea;
                Rectangle titleSurface = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), Rectangle.Inflate(header, 0, 2));
                if (!VanillaServiceRecognition.TryTitleTextArea(pixels, titleSurface, out titleArea, null, 1)) continue;
                VanillaTextLine[] title;
                string readEvidence;
                if (!VanillaTextRecognition.TryRead(bitmap, titleArea, true, out title, out readEvidence)
                    || !HasExactTitle(title)) continue;
                foreach (Rectangle body in FindBodies(bitmap, header))
                {
                    VanillaTextLine[] observed;
                    if (!VanillaTextRecognition.TryRead(bitmap, body, false, out observed, out readEvidence)) continue;
                    var lines = VanillaServiceRecognition.CombineAlignedText(observed);
                    if (!HasExactBody(bitmap, body, header.Height, lines)) continue;
                    Rectangle found = Rectangle.Union(header, body);
                    if (!matches.Any(previous => previous.IntersectsWith(found))) matches.Add(found);
                    break;
                }
            }
            if (matches.Count != 1)
            {
                if (matches.Count > 1) evidence = "multiple exact Server Closed.(1) dialogs are ambiguous";
                return false;
            }
            dialog = matches[0];
            evidence = "exact Server Closed.(1) text, Message title and OK control in observed dialog " + dialog;
            return true;
        }

        private static bool HasExactTitle(VanillaTextLine[] lines)
        {
            return lines.Length == 1 && lines[0].Confidence >= 65
                && string.Equals(lines[0].Text.Trim(), "Message", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasExactBody(Bitmap bitmap, Rectangle body, int titleHeight, VanillaTextLine[] lines)
        {
            // The screenshot's form contains exactly the message and its OK control.
            // Do not combine a phrase from one form with an OK in a different form.
            Rectangle ok;
            if (!TryFindOk(bitmap, body, titleHeight, out ok)) return false;
            VanillaTextLine[] messageLines = lines.Where(line => !line.Bounds.IntersectsWith(ok)).ToArray();
            if (messageLines.Length != 1) return false;
            VanillaTextLine message = messageLines[0];
            if (ok.Top <= message.Bounds.Bottom) return false;
            if (IsExactMessage(message)) return true;
            // Re-read only the actual observed line. This preserves exact punctuation
            // and the numeric code; no approximate spelling or semantic substitutions.
            Rectangle area = Rectangle.Intersect(new Rectangle(Point.Empty, bitmap.Size), Rectangle.Inflate(message.Bounds, 2, 2));
            int exactAgreements = 0;
            foreach (VanillaTextLine line in ReadMessage(bitmap, area))
            {
                if (IsExactMessage(line)) return true;
                // Tiny joined punctuation can reduce the word score despite both
                // bicubic and integral enlargement yielding the complete exact text.
                // Require two separate reads in that case, within this confirmed form.
                // Runtime still requires another fresh frame before any recovery.
                if (MatchesMessage(line.Text) && line.Confidence >= 65
                    && line.Words.All(word => word.Confidence >= 50) && ++exactAgreements >= 2) return true;
            }
            return false;
        }

        private static bool IsExactMessage(VanillaTextLine line)
        {
            return line.Confidence >= 70 && line.Words.All(word => word.Confidence >= 60)
                && MatchesMessage(line.Text);
        }

        private static bool MatchesMessage(string text)
        {
            return Regex.IsMatch(text.Trim(), @"^Server\s+Closed\s*\.\s*\(\s*1\s*\)$", RegexOptions.IgnoreCase);
        }

        private static IEnumerable<VanillaTextLine> ReadMessage(Bitmap bitmap, Rectangle area)
        {
            VanillaTextLine[] lines;
            string evidence;
            if (VanillaTextRecognition.TryRead(bitmap, area, true, out lines, out evidence) && lines.Length == 1) yield return lines[0];
            if (VanillaTextRecognition.TryReadAccurate(bitmap, area, true, out lines, out evidence) && lines.Length == 1) yield return lines[0];
            if (VanillaTextRecognition.TryReadPixelPreservingLine(bitmap, area, out lines, out evidence) && lines.Length == 1) yield return lines[0];
            if (VanillaTextRecognition.TryReadCompactLine(bitmap, area, out lines, out evidence) && lines.Length == 1) yield return lines[0];
        }

        private static bool TryFindOk(Bitmap bitmap, Rectangle body, int titleHeight, out Rectangle button)
        {
            button = Rectangle.Empty;
            var edges = new List<Rectangle>();
            for (int y = body.Top; y < body.Bottom; y++)
            {
                int x = body.Left;
                while (x < body.Right)
                {
                    if (!FramePixel(bitmap, x, y)) { x++; continue; }
                    int left = x, last = x;
                    while (++x < body.Right)
                    {
                        if (FramePixel(bitmap, x, y)) last = x;
                        else if (x - last > 1) break;
                    }
                    int width = last - left + 1;
                    if (width >= Math.Max(18, titleHeight) && width <= Math.Min(body.Width / 2, titleHeight * 10))
                        edges.Add(new Rectangle(left, y, width, 1));
                    if (edges.Count > 256) return false;
                }
            }
            foreach (Rectangle top in edges)
            foreach (Rectangle bottom in edges)
            {
                int height = bottom.Top - top.Top;
                int tolerance = Math.Max(2, titleHeight / 5);
                if (height < Math.Max(8, titleHeight / 2) || height > titleHeight * 3
                    || Math.Abs(top.Left - bottom.Left) > tolerance || Math.Abs(top.Right - bottom.Right) > tolerance
                    || top.Width < height * 1.2 || top.Width > height * 6) continue;
                Rectangle box = Rectangle.Union(top, bottom);
                if (!HasVerticalFrame(bitmap, box, box.Left, tolerance)
                    || !HasVerticalFrame(bitmap, box, box.Right - 1, tolerance)) continue;
                Rectangle text = Rectangle.Inflate(box, -Math.Max(1, titleHeight / 9), -Math.Max(1, titleHeight / 9));
                text = ButtonInk(bitmap, text);
                if (text.IsEmpty) continue;
                VanillaTextLine[] words;
                string evidence;
                if (!VanillaTextRecognition.TryRead(bitmap, text, true, out words, out evidence)
                    || words.Length != 1 || words[0].Confidence < 65
                    || !string.Equals(words[0].Text.Trim(), "OK", StringComparison.OrdinalIgnoreCase)) continue;
                if (!button.IsEmpty && !button.IntersectsWith(box)) return false;
                button = box;
            }
            return !button.IsEmpty;
        }

        private static Rectangle ButtonInk(Bitmap bitmap, Rectangle area)
        {
            Rectangle ink = Rectangle.Empty;
            for (int y = area.Top; y < area.Bottom; y++)
            for (int x = area.Left; x < area.Right; x++)
            {
                Color color = bitmap.GetPixel(x, y);
                if (color.R + color.G + color.B >= 300) continue;
                var pixel = new Rectangle(x, y, 1, 1);
                ink = ink.IsEmpty ? pixel : Rectangle.Union(ink, pixel);
            }
            return ink.IsEmpty ? ink : Rectangle.Intersect(area, Rectangle.Inflate(ink, 2, 2));
        }

        private static bool HasVerticalFrame(Bitmap bitmap, Rectangle box, int axis, int tolerance)
        {
            int matched = 0, total = 0;
            for (int y = box.Top + 1; y < box.Bottom - 1; y++)
            {
                total++;
                for (int x = Math.Max(0, axis - tolerance); x <= Math.Min(bitmap.Width - 1, axis + tolerance); x++)
                    if (FramePixel(bitmap, x, y)) { matched++; break; }
            }
            return total > 0 && matched >= total * .70;
        }

        private static bool FramePixel(Bitmap bitmap, int x, int y)
        {
            Color color = bitmap.GetPixel(x, y);
            int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
            int minimum = Math.Min(color.R, Math.Min(color.G, color.B));
            return minimum >= 55 && maximum <= 225 && maximum - minimum <= 35;
        }

        private static IEnumerable<Rectangle> FindBodies(Bitmap bitmap, Rectangle header)
        {
            // A real horizontal form boundary below the title defines each candidate's
            // bottom. Geometry bounds work only; it never supplies a guessed action.
            int inset = Math.Max(2, header.Height / 5);
            int left = header.Left + inset, right = header.Right - inset;
            int start = header.Bottom + Math.Max(20, header.Height * 2);
            int end = Math.Min(bitmap.Height, header.Bottom + Math.Min(650, header.Width * 2));
            int previous = -10, found = 0;
            for (int y = start; y < end; y++)
            {
                int neutral = 0, total = 0;
                for (int x = left; x < right; x += 2)
                {
                    Color color = bitmap.GetPixel(x, y);
                    int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                    int minimum = Math.Min(color.R, Math.Min(color.G, color.B));
                    if (minimum >= 55 && maximum <= 225 && maximum - minimum <= 35) neutral++;
                    total++;
                }
                if (total == 0 || neutral < total * .80) continue;
                if (y - previous <= Math.Max(2, header.Height / 3)) { previous = y; continue; }
                previous = y;
                var body = Rectangle.FromLTRB(left, header.Bottom + 1, right, y);
                if (!HasLightBody(bitmap, body)) continue;
                yield return body;
                if (++found >= 6) yield break;
            }
        }

        private static bool HasLightBody(Bitmap bitmap, Rectangle body)
        {
            int light = 0, total = 0;
            for (int y = body.Top; y < body.Bottom; y += Math.Max(1, body.Height / 16))
            for (int x = body.Left; x < body.Right; x += Math.Max(1, body.Width / 32))
            {
                Color color = bitmap.GetPixel(x, y);
                int maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                int minimum = Math.Min(color.R, Math.Min(color.G, color.B));
                if (minimum >= 215 && maximum - minimum <= 35) light++;
                total++;
            }
            return total > 0 && light >= total * .78;
        }
    }
}
