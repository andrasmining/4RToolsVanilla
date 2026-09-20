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
                Match match = Regex.Match(VanillaServiceRecognition.Normalize(line.Text),
                    @"^[\[\(\|]?\s*Proxy\s+Connection\s*[\]\)\|]?\s+(Global|Manila|Singapore|Tokyo|Hong Kong|Los Angeles|Australia|UAE)$",
                    RegexOptions.IgnoreCase);
                if (!match.Success || line.Confidence < 60) continue;
                string observed = match.Groups[1].Value;
                string name = Enum.GetValues(typeof(VanillaProxyRoute)).Cast<VanillaProxyRoute>()
                    .Select(NameForRoute).FirstOrDefault(candidate => string.Equals(candidate, observed, StringComparison.OrdinalIgnoreCase));
                if (name == null) continue;
                int wordCount = name.Split(' ').Length;
                VanillaTextWord[] words = line.Words.Skip(Math.Max(0, line.Words.Length - wordCount)).ToArray();
                if (words.Length != wordCount || words.Any(word => word.Confidence < 55)) continue;
                Rectangle bounds = words[0].Bounds;
                foreach (VanillaTextWord word in words.Skip(1)) bounds = Rectangle.Union(bounds, word.Bounds);
                services.Add(new VanillaServiceRow { Name = name, Bounds = bounds,
                    IsHighlighted = VanillaServiceRecognition.IsHighlighted(dialog, bounds) });
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
    }
}
