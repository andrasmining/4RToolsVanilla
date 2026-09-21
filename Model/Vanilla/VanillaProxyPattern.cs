using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaProxyLayout
    {
        public Rectangle SearchArea { get; set; }
        public VanillaServiceRow[] Services { get; set; }
        public string Evidence { get; set; }

        internal bool TryFind(VanillaProxyRoute route, out VanillaServiceRow row)
        {
            string name = VanillaProxyPattern.NameForRoute(route);
            VanillaServiceRow[] matches = (Services ?? new VanillaServiceRow[0])
                .Where(service => string.Equals(service.Name, name, StringComparison.Ordinal)).ToArray();
            row = matches.Length == 1 ? matches[0] : null;
            return row != null;
        }
    }

    internal static class VanillaProxyPattern
    {
        internal static string NameForRoute(VanillaProxyRoute route)
        {
            switch (route)
            {
                case VanillaProxyRoute.Global: return "Global";
                case VanillaProxyRoute.Manila: return "Manila";
                case VanillaProxyRoute.Singapore: return "Singapore";
                case VanillaProxyRoute.Tokyo: return "Tokyo";
                case VanillaProxyRoute.HongKong: return "Hong Kong";
                case VanillaProxyRoute.LosAngeles: return "Los Angeles";
                case VanillaProxyRoute.Australia: return "Australia";
                case VanillaProxyRoute.UAE: return "UAE";
                default: return null;
            }
        }

        internal static bool TryDetect(Bitmap bitmap, out VanillaProxyLayout layout, out string evidence)
        {
            layout = null;
            VanillaServiceDialog dialog;
            if (!VanillaServiceRecognition.TryDetect(bitmap, out dialog, out evidence)) return false;
            var services = new List<VanillaServiceRow>();
            foreach (VanillaTextLine line in dialog.Lines)
            {
                VanillaServiceRow service;
                if (TryParseRow(dialog, line, out service)) { services.Add(service); continue; }
                if (!Regex.IsMatch(VanillaServiceRecognition.Normalize(line.Text),
                    @"^[\[\(\|]?\s*Proxy\s+Connection(?:\s|[\]\)\|])", RegexOptions.IgnoreCase)) continue;
                // The fast model sometimes loses a letter in tiny softened text. Re-read
                // the observed row with the accurate model; no aliases or spelling repair.
                VanillaTextLine[] accurate;
                string accurateEvidence;
                Rectangle crop = Rectangle.Intersect(dialog.Bounds, Rectangle.Inflate(line.Bounds, 2, 2));
                int before = services.Count;
                if (VanillaTextRecognition.TryReadPixelPreservingLine(bitmap, crop, out accurate, out accurateEvidence))
                    foreach (VanillaTextLine candidate in VanillaServiceRecognition.CombineAlignedText(accurate))
                        if (TryParseRow(dialog, candidate, out service)) services.Add(service);
                if (services.Count == before && TryReadServiceSuffix(bitmap, dialog, line, out service)) services.Add(service);
            }
            if (services.Count < 2)
            { evidence = "Select Service form has fewer than two confident named proxy choices"; return false; }
            if (services.GroupBy(service => service.Name).Any(group => group.Count() != 1))
            { evidence = "duplicate named proxy choices; ambiguous capture"; return false; }
            if (services.Count(service => service.IsHighlighted) > 1)
            { evidence = "multiple highlighted proxy choices; ambiguous capture"; return false; }
            evidence = "recognized named proxy choices: " + string.Join(", ", services.Select(service => service.Name
                + (service.IsHighlighted ? " [selected]" : "")));
            layout = new VanillaProxyLayout { SearchArea = dialog.Bounds, Services = services.ToArray(), Evidence = evidence };
            return true;
        }

        private static bool TrySuffixArea(VanillaServiceDialog dialog, VanillaTextLine line, out Rectangle area)
        {
            area = Rectangle.Empty;
            if (line.Confidence < 60 || !Regex.IsMatch(VanillaServiceRecognition.Normalize(line.Text),
                @"^[\[\(\|]?\s*Proxy\s+Connection\s*[\]\)\|]", RegexOptions.IgnoreCase)) return false;
            // The closing delimiter is itself an observed word. All subsequent words,
            // including unknown suffixes, form the complete name region. No row offset or
            // expected name length is used to cut away text that disagrees with a route.
            int delimiter = Array.FindIndex(line.Words, word => word.Text == "]" || word.Text == ")" || word.Text == "|");
            if (delimiter < 2 || delimiter + 1 >= line.Words.Length) return false;
            VanillaTextWord[] suffix = line.Words.Skip(delimiter + 1).ToArray();
            Rectangle bounds = suffix[0].Bounds;
            foreach (VanillaTextWord word in suffix.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
            area = Rectangle.Intersect(dialog.Bounds, Rectangle.Inflate(bounds, 2, 2));
            return area.Width >= 5 && area.Height >= 5 && area.Width <= 400 && area.Height <= 60;
        }

        private static bool TryReadServiceSuffix(Bitmap bitmap, VanillaServiceDialog dialog, VanillaTextLine source, out VanillaServiceRow service)
        {
            service = null;
            Rectangle area;
            if (!TrySuffixArea(dialog, source, out area)) return false;
            var matches = new List<VanillaServiceRow>();
            foreach (VanillaTextLine line in ReadSuffix(bitmap, area))
            {
                string observed = VanillaServiceRecognition.Normalize(line.Text);
                string name = Enum.GetValues(typeof(VanillaProxyRoute)).Cast<VanillaProxyRoute>().Select(NameForRoute)
                    .FirstOrDefault(candidate => string.Equals(candidate, observed, StringComparison.OrdinalIgnoreCase));
                if (name == null || line.Confidence < 60 || line.Words.Any(word => word.Confidence < 55)) continue;
                matches.Add(new VanillaServiceRow { Name = name, Bounds = line.Bounds,
                    IsHighlighted = VanillaServiceRecognition.IsHighlighted(dialog, line.Bounds) });
            }
            if (matches.Select(match => match.Name).Distinct().Count() != 1) return false;
            service = matches[0];
            return true;
        }

        private static IEnumerable<VanillaTextLine> ReadSuffix(Bitmap bitmap, Rectangle area)
        {
            VanillaTextLine[] lines;
            string evidence;
            if (VanillaTextRecognition.TryRead(bitmap, area, true, out lines, out evidence))
                foreach (VanillaTextLine line in lines) yield return line;
            if (VanillaTextRecognition.TryReadAccurate(bitmap, area, true, out lines, out evidence))
                foreach (VanillaTextLine line in lines) yield return line;
            if (VanillaTextRecognition.TryReadPixelPreservingLine(bitmap, area, out lines, out evidence))
                foreach (VanillaTextLine line in lines) yield return line;
            if (VanillaTextRecognition.TryReadCompactLine(bitmap, area, out lines, out evidence))
                foreach (VanillaTextLine line in lines) yield return line;
        }

        internal static string DescribeSyntheticSuffixes(Bitmap bitmap, VanillaServiceDialog dialog)
        {
            if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true") return "";
            var report = new System.Text.StringBuilder();
            foreach (VanillaTextLine source in dialog.Lines)
            {
                VanillaServiceRow parsed;
                Rectangle area;
                if (TryParseRow(dialog, source, out parsed) || !TrySuffixArea(dialog, source, out area)) continue;
                report.AppendLine("Observed suffix crop=" + area + "; independent prefix='Proxy Connection ]'");
                foreach (VanillaTextLine line in ReadSuffix(bitmap, area))
                    report.AppendLine("Suffix read='" + line.Text + "' @" + line.Bounds + "; confidence=" + line.Confidence.ToString("0.0")
                        + "; words=" + string.Join(", ", line.Words.Select(word => word.Text + ":" + word.Confidence.ToString("0.0"))));
            }
            return report.ToString();
        }

        private static bool TryParseRow(VanillaServiceDialog dialog, VanillaTextLine line, out VanillaServiceRow service)
        {
            service = null;
            Match match = Regex.Match(VanillaServiceRecognition.Normalize(line.Text),
                @"^[\[\(\|]?\s*Proxy\s+Connection(?:\s*[\]\)\|]\s*|\s+)(Global|Manila|Singapore|Tokyo|Hong Kong|Los Angeles|Australia|UAE)$",
                RegexOptions.IgnoreCase);
            if (!match.Success || line.Confidence < 60) return false;
            string observed = match.Groups[1].Value;
            string name = Enum.GetValues(typeof(VanillaProxyRoute)).Cast<VanillaProxyRoute>()
                .Select(NameForRoute).FirstOrDefault(candidate => string.Equals(candidate, observed, StringComparison.OrdinalIgnoreCase));
            if (name == null) return false;
            int wordCount = name.Split(' ').Length;
            VanillaTextWord[] words = line.Words.Skip(Math.Max(0, line.Words.Length - wordCount)).ToArray();
            if (words.Length != wordCount || words.Any(word => word.Confidence < 55)) return false;
            Rectangle bounds = words[0].Bounds;
            foreach (VanillaTextWord word in words.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
            service = new VanillaServiceRow { Name = name, Bounds = bounds,
                IsHighlighted = VanillaServiceRecognition.IsHighlighted(dialog, bounds) };
            return true;
        }
    }
}
