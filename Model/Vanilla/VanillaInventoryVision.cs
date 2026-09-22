using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaUiSlotGrid
    {
        internal Rectangle Panel;
        internal int[] Columns;
        internal int[] Rows;
        internal int EmptyPaleThreshold;
    }

    internal enum VanillaInventorySlotState
    {
        Unknown,
        Empty,
        Occupied
    }

    internal sealed class VanillaInventoryFirstSlotObservation
    {
        internal VanillaInventorySlotState State;
        internal Point Center;
        internal Point ReferenceEmptyCenter;
        internal double PaleRatio;
        internal double TemplateDifference;
    }

    internal sealed class VanillaInventoryCategoryTabs
    {
        internal Rectangle[] Tabs;
        internal Rectangle RailBounds;
        internal int RightBorderX;
        internal int SelectedIndex;
        // Higher means the tab is structurally more "open" into the inventory body.
        internal double[] SelectionScores;
    }

    internal static class VanillaInventoryVision
    {
        private sealed class Component
        {
            internal Rectangle Bounds;
            internal int Area;
            internal Point Center;
        }

        private sealed class CategoryRuleLine
        {
            internal int Y;
            internal int Left;
            internal int Right;
            internal int Length { get { return Math.Max(0, Right - Left + 1); } }
        }

        internal static bool TryFindToggledPanel(Bitmap before, Bitmap after, out Rectangle panel, out bool opened)
        {
            panel = Rectangle.Empty; opened = false;
            Rectangle added = FindChangedLightPanel(after, before);
            Rectangle removed = FindChangedLightPanel(before, after);
            int addedScore = ScorePanel(after, added), removedScore = ScorePanel(before, removed);
            if (addedScore <= 0 && removedScore <= 0) return false;
            opened = addedScore >= removedScore;
            panel = opened ? added : removed;
            return !panel.IsEmpty;
        }

        internal static bool PanelStillPresent(Bitmap frame, Rectangle panel)
        {
            if (frame == null || panel.IsEmpty) return false;
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, frame.Size), panel);
            if (clipped.Width < 100 || clipped.Height < 50) return false;
            PixelBuffer pixels = PixelBuffer.Read(frame);
            int light = 0, total = 0;
            int step = Math.Max(1, Math.Min(clipped.Width, clipped.Height) / 80);
            for (int y = clipped.Top; y < clipped.Bottom; y += step)
                for (int x = clipped.Left; x < clipped.Right; x += step) { total++; if (pixels.IsLight(x, y)) light++; }
            return total > 0 && light / (double)total > 0.35;
        }

        internal static VanillaUiSlotGrid DetectSlotGrid(Bitmap frame, Rectangle panel)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, frame.Size), panel);
            if (clipped.Width < 120 || clipped.Height < 60) throw new InvalidOperationException("Detected item panel is too small for slot recognition.");
            PixelBuffer pixels = PixelBuffer.Read(frame);
            List<Component> components = ConnectedComponents(pixels, clipped, p => p.Pale, 18, 55, 8, 30, 120, 1000);
            var rows = new List<List<Component>>();
            foreach (Component item in components.OrderBy(c => c.Center.Y))
            {
                List<Component> row = rows.FirstOrDefault(r => Math.Abs(r.Average(c => c.Center.Y) - item.Center.Y) <= 5);
                if (row == null) { row = new List<Component>(); rows.Add(row); }
                row.Add(item);
            }
            var useful = rows.Select(row => row.OrderBy(c => c.Center.X).ToList())
                .Where(row => LongestRegularRun(row.Select(c => c.Center.X).ToArray()).Length >= 4).ToList();
            if (useful.Count == 0) throw new InvalidOperationException("No regular Vanilla item-slot grid was detected inside the toggled panel.");

            int[] columns = useful.Select(row => LongestRegularRun(row.Select(c => c.Center.X).ToArray()))
                .OrderByDescending(run => run.Length).First();
            if (columns.Length < 4) throw new InvalidOperationException("Item-slot column lattice is incomplete.");
            int columnSpacing = MedianSpacing(columns);
            var rowCenters = useful.Select(row => (int)Math.Round(row.Average(c => c.Center.Y))).OrderBy(v => v).ToList();
            int rowSpacing = rowCenters.Count > 1 ? MedianSpacing(rowCenters.ToArray()) : columnSpacing;
            rowSpacing = Math.Max(24, Math.Min(60, rowSpacing));
            int first = rowCenters.First();
            while (first - rowSpacing - clipped.Top > rowSpacing * 2) first -= rowSpacing;
            int last = rowCenters.Last();
            while (clipped.Bottom - (last + rowSpacing) > rowSpacing) last += rowSpacing;
            var allRows = new List<int>();
            for (int y = first; y <= last; y += rowSpacing) allRows.Add(y);

            int emptyMedian = components.OrderBy(c => Math.Abs(c.Center.Y - rowCenters[0])).Take(Math.Max(1, columns.Length))
                .Select(c => pixels.PaleCount(c.Center.X, c.Center.Y, 18, 10)).OrderBy(v => v).ElementAt(Math.Max(0, Math.Min(columns.Length - 1, columns.Length / 2)));
            int threshold = Math.Max(240, (int)Math.Round(emptyMedian * 0.66));
            return new VanillaUiSlotGrid { Panel = clipped, Columns = columns, Rows = allRows.ToArray(), EmptyPaleThreshold = threshold };
        }

        internal static VanillaInventoryCategoryTabs DetectCategoryTabs(Bitmap frame, VanillaUiSlotGrid grid)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (grid == null || grid.Columns == null || grid.Columns.Length < 4)
                throw new InvalidOperationException("A detected item-slot grid is required before category-tab detection.");

            PixelBuffer pixels = PixelBuffer.Read(frame);
            int columnSpacing = MedianSpacing(grid.Columns);
            int rowSpacing = grid.Rows != null && grid.Rows.Length > 1 ? MedianSpacing(grid.Rows) : columnSpacing;
            int firstColumn = grid.Columns.Min();

            // The tab rail is not addressed by a percentage or screen coordinate. Its horizontal
            // search area is derived from the detected panel edge and detected first slot column.
            int railRight = firstColumn - Math.Max(6, (int)Math.Round(columnSpacing * 0.65));
            Rectangle railSearch = Rectangle.Intersect(grid.Panel,
                new Rectangle(grid.Panel.Left, grid.Panel.Top, Math.Max(0, railRight - grid.Panel.Left), grid.Panel.Height));
            railSearch.Intersect(new Rectangle(Point.Empty, frame.Size));
            if (railSearch.Width < 12 || railSearch.Height < 80)
                throw new InvalidOperationException("The inventory category rail could not be separated from the detected slot grid.");

            int minimumRun = Math.Max(8, (int)Math.Round(railSearch.Width * 0.45));
            var raw = new List<CategoryRuleLine>();
            for (int y = railSearch.Top; y < railSearch.Bottom; y++)
            {
                int left, right;
                if (LongestCategoryRuleRun(pixels, railSearch.Left, railSearch.Right, y, out left, out right) >= minimumRun)
                    raw.Add(new CategoryRuleLine { Y = y, Left = left, Right = right });
            }

            var rules = new List<CategoryRuleLine>();
            CategoryRuleLine best = null;
            int previousY = int.MinValue;
            foreach (CategoryRuleLine line in raw.OrderBy(item => item.Y))
            {
                if (best == null || line.Y > previousY + 1)
                {
                    if (best != null) rules.Add(best);
                    best = line;
                }
                else if (line.Length > best.Length) best = line;
                previousY = line.Y;
            }
            if (best != null) rules.Add(best);

            int lattice = Math.Max(1, Math.Min(columnSpacing, rowSpacing));
            double minStep = Math.Max(20.0, lattice * 1.15);
            double maxStep = Math.Max(minStep + 4.0, Math.Max(columnSpacing, rowSpacing) * 2.40);
            int bestHits = 0;
            double bestError = double.MaxValue;
            double bestOrigin = 0, bestStep = 0;
            int bestTolerance = 0;

            for (int i = 0; i < rules.Count; i++)
            for (int j = i + 1; j < rules.Count; j++)
            for (int divisor = 1; divisor <= 4; divisor++)
            {
                double step = (rules[j].Y - rules[i].Y) / (double)divisor;
                if (step < minStep || step > maxStep) continue;
                int tolerance = Math.Max(3, (int)Math.Round(step * 0.13));
                for (int indexAtFirst = 0; indexAtFirst + divisor <= 4; indexAtFirst++)
                {
                    double origin = rules[i].Y - indexAtFirst * step;
                    double end = origin + 4 * step;
                    if (origin < grid.Panel.Top - tolerance || end > grid.Panel.Bottom + tolerance) continue;

                    int hits = 0;
                    double error = 0;
                    for (int k = 0; k <= 4; k++)
                    {
                        double predicted = origin + k * step;
                        double nearest = rules.Count == 0 ? double.MaxValue : rules.Min(rule => Math.Abs(rule.Y - predicted));
                        if (nearest <= tolerance) { hits++; error += nearest; }
                    }
                    if (hits > bestHits || (hits == bestHits && error < bestError))
                    {
                        bestHits = hits;
                        bestError = error;
                        bestOrigin = origin;
                        bestStep = step;
                        bestTolerance = tolerance;
                    }
                }
            }

            if (bestHits < 3 || bestStep <= 0)
                throw new InvalidOperationException("The four-tab inventory rail was not positively detected from repeated separator structure.");

            int[] boundaries = Enumerable.Range(0, 5).Select(k => (int)Math.Round(bestOrigin + k * bestStep)).ToArray();
            for (int k = 1; k < boundaries.Length; k++)
                if (boundaries[k] - boundaries[k - 1] < Math.Max(12, (int)Math.Round(lattice * 0.75)))
                    throw new InvalidOperationException("Detected category-tab boundaries are incoherent.");

            // Vanilla's Fav tab is blue even while another category is active, so color is
            // not a selected-tab signal. The actual active tab is the one whose right edge is
            // open/merged into the inventory body; inactive tabs keep a vertical right border.
            // Recover both vertical rail borders from repeated grayscale rule pixels.
            var vertical = new List<Tuple<int, double>>();
            for (int x = railSearch.Left; x < railSearch.Right; x++)
            {
                double coverage = CategoryVerticalRuleCoverage(pixels, x, boundaries);
                if (coverage >= 0.38) vertical.Add(Tuple.Create(x, coverage));
            }
            if (vertical.Count < 2)
                throw new InvalidOperationException("Inventory category rail vertical borders were not positively detected.");

            int leftBorder = vertical.Min(item => item.Item1);
            int rightBorder = vertical.Max(item => item.Item1);
            if (rightBorder - leftBorder < 8)
                throw new InvalidOperationException("Detected inventory category rail is too narrow.");

            // Pick the strongest pixel column inside the rightmost border cluster. This stabilizes
            // anti-aliasing/DPI differences while preserving the structural open-edge signal.
            int clusterStart = rightBorder;
            while (clusterStart > leftBorder && vertical.Any(item => item.Item1 == clusterStart - 1))
                clusterStart--;
            int rightRuleX = vertical.Where(item => item.Item1 >= clusterStart)
                .OrderByDescending(item => item.Item2).ThenByDescending(item => item.Item1).First().Item1;

            int clickLeft = leftBorder + 1;
            int clickRight = rightRuleX - 1;
            if (clickRight - clickLeft < 6)
                throw new InvalidOperationException("Detected category rail has no safe interior click band.");

            var tabs = new Rectangle[4];
            var scores = new double[4];
            for (int k = 0; k < 4; k++)
            {
                int top = Math.Max(grid.Panel.Top, boundaries[k] + 1);
                int bottom = Math.Min(grid.Panel.Bottom, boundaries[k + 1] - 1);
                if (bottom <= top) throw new InvalidOperationException("Detected category tab has no usable area.");
                tabs[k] = Rectangle.FromLTRB(clickLeft, top, clickRight + 1, bottom);
                double closedFraction = CategoryRightBorderFraction(pixels, rightRuleX, top, bottom);
                scores[k] = Math.Max(0, Math.Min(1, 1.0 - closedFraction));
            }

            int selected = -1;
            int maxIndex = 0;
            for (int k = 1; k < scores.Length; k++) if (scores[k] > scores[maxIndex]) maxIndex = k;
            double second = scores.Where((value, index) => index != maxIndex).DefaultIfEmpty(0).Max();
            if (scores[maxIndex] >= 0.52 && scores[maxIndex] - second >= 0.16)
                selected = maxIndex;

            return new VanillaInventoryCategoryTabs
            {
                Tabs = tabs,
                RailBounds = Rectangle.FromLTRB(leftBorder, boundaries[0], rightRuleX + 1, boundaries[4]),
                RightBorderX = rightRuleX,
                SelectedIndex = selected,
                SelectionScores = scores
            };
        }

        internal static VanillaInventoryFirstSlotObservation ObserveFirstSlot(Bitmap frame, VanillaUiSlotGrid grid)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (grid == null || grid.Columns == null || grid.Columns.Length == 0 || grid.Rows == null || grid.Rows.Length == 0)
                throw new InvalidOperationException("Detected inventory slot grid is empty.");

            PixelBuffer pixels = PixelBuffer.Read(frame);
            int columnSpacing = grid.Columns.Length > 1 ? MedianSpacing(grid.Columns) : 41;
            int rowSpacing = grid.Rows.Length > 1 ? MedianSpacing(grid.Rows) : columnSpacing;
            int rx = Math.Max(10, Math.Min(28, (int)Math.Round(columnSpacing * 0.42)));
            int ry = Math.Max(6, Math.Min(18, (int)Math.Round(rowSpacing * 0.24)));

            Point first = new Point(grid.Columns[0], grid.Rows[0]);
            var candidates = new List<Tuple<Point, int>>();
            foreach (int y in grid.Rows)
            foreach (int x in grid.Columns)
            {
                if (x == first.X && y == first.Y) continue;
                if (!grid.Panel.Contains(x, y)) continue;
                int pale = pixels.PaleCount(x, y, rx, ry);
                candidates.Add(Tuple.Create(new Point(x, y), pale));
            }
            if (candidates.Count == 0)
                throw new InvalidOperationException("No reference slot is available for first-slot classification.");

            Tuple<Point, int> reference = candidates.OrderByDescending(item => item.Item2).First();
            int firstPale = pixels.PaleCount(first.X, first.Y, rx, ry);
            double paleRatio = firstPale / (double)Math.Max(1, reference.Item2);
            double difference = pixels.MeanPatchColorDistance(first, reference.Item1, rx, ry);

            VanillaInventorySlotState state = VanillaInventorySlotState.Unknown;
            if (paleRatio >= 0.80 && difference <= 24.0)
                state = VanillaInventorySlotState.Empty;
            else if (paleRatio <= 0.68 || difference >= 34.0)
                state = VanillaInventorySlotState.Occupied;

            return new VanillaInventoryFirstSlotObservation
            {
                State = state,
                Center = first,
                ReferenceEmptyCenter = reference.Item1,
                PaleRatio = paleRatio,
                TemplateDifference = difference
            };
        }

        internal static Point CartDropPoint(Rectangle cartPanel, int sequence)
        {
            if (cartPanel.Width < 40 || cartPanel.Height < 30)
                throw new InvalidOperationException("Detected Cart panel is too small for a safe interior drop.");

            // Vanilla accepts the dragged item anywhere in the Cart item body. Use only the
            // positively detected Cart rectangle and keep a generous inset from borders/tabs.
            // A deterministic rotation avoids depending on any particular Cart slot being empty
            // or even visually detectable once the Cart contains items.
            double[] xs = { 0.50, 0.35, 0.65, 0.25, 0.75, 0.50, 0.40, 0.60 };
            double[] ys = { 0.55, 0.55, 0.55, 0.55, 0.55, 0.35, 0.72, 0.72 };
            int index = Math.Abs(sequence) % xs.Length;
            int insetX = Math.Max(10, Math.Min(28, cartPanel.Width / 10));
            int insetY = Math.Max(8, Math.Min(20, cartPanel.Height / 7));
            int left = cartPanel.Left + insetX;
            int right = cartPanel.Right - insetX - 1;
            int top = cartPanel.Top + insetY;
            int bottom = cartPanel.Bottom - insetY - 1;
            if (right <= left || bottom <= top)
                throw new InvalidOperationException("Detected Cart panel has no safe interior drop area.");
            int x = left + (int)Math.Round((right - left) * xs[index]);
            int y = top + (int)Math.Round((bottom - top) * ys[index]);
            Point point = new Point(Math.Max(left, Math.Min(right, x)), Math.Max(top, Math.Min(bottom, y)));
            if (!cartPanel.Contains(point))
                throw new InvalidOperationException("Detected Cart drop point escaped the detected Cart panel.");
            return point;
        }

        internal static Point CartDropPoint(VanillaUiSlotGrid grid, int sequence)
        {
            if (grid == null) throw new ArgumentNullException(nameof(grid));
            return CartDropPoint(grid.Panel, sequence);
        }

        internal static Point? FirstOccupiedSlot(Bitmap frame, VanillaUiSlotGrid grid)
        {
            PixelBuffer pixels = PixelBuffer.Read(frame);
            foreach (int y in grid.Rows)
                foreach (int x in grid.Columns)
                    if (grid.Panel.Contains(x, y) && pixels.PaleCount(x, y, 18, 10) < grid.EmptyPaleThreshold) return new Point(x, y);
            return null;
        }

        internal static Point? FirstOccupiedSlotNear(Bitmap frame, VanillaUiSlotGrid grid, Point previous)
        {
            PixelBuffer pixels = PixelBuffer.Read(frame);
            Point? nearest = null; double best = double.MaxValue;
            foreach (int y in grid.Rows)
                foreach (int x in grid.Columns)
                {
                    if (!grid.Panel.Contains(x, y) || pixels.PaleCount(x, y, 18, 10) >= grid.EmptyPaleThreshold) continue;
                    double distance = Math.Abs(x - previous.X) + Math.Abs(y - previous.Y);
                    if (distance < best) { best = distance; nearest = new Point(x, y); }
                }
            return best <= 14 ? nearest : null;
        }

        internal static bool HasQuantityPrompt(Bitmap frame)
        {
            if (frame == null) return false;
            PixelBuffer pixels = PixelBuffer.Read(frame);
            Rectangle whole = new Rectangle(Point.Empty, frame.Size);
            // The Vanilla quantity prompt is a short, wide white modal containing a focused
            // blue-selected numeric edit field. Basic Info, inventory/cart panes and chat also
            // contain white/blue pixels, so dimensions/aspect/fill are mandatory before Enter
            // can ever be authorized. Missing/ambiguous evidence always means NO Enter.
            List<Component> white = ConnectedComponents(pixels, whole, p => p.Light, 100, 440, 30, 95, 900, 60000);
            foreach (Component component in white)
            {
                Rectangle box = component.Bounds;
                double aspect = box.Width / (double)Math.Max(1, box.Height);
                double fill = component.Area / (double)Math.Max(1, box.Width * box.Height);
                if (aspect < 2.4 || aspect > 7.0 || fill < 0.45) continue;
                Rectangle leftBody = new Rectangle(box.Left, box.Top + box.Height / 4,
                    Math.Max(1, (int)Math.Round(box.Width * 0.68)), Math.Max(1, box.Height * 3 / 4));
                if (pixels.BlueSelectionCount(leftBody) >= 18) return true;
            }
            return false;
        }

        private static Rectangle FindChangedLightPanel(Bitmap primary, Bitmap secondary)
        {
            if (primary == null || secondary == null || primary.Size != secondary.Size) return Rectangle.Empty;
            PixelBuffer a = PixelBuffer.Read(primary), b = PixelBuffer.Read(secondary);
            int width = a.Width, height = a.Height;
            bool[] mask = new bool[width * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    mask[y * width + x] = a.IsLight(x, y) && a.ColorDistance(b, x, y) >= 32;
            List<Component> components = ConnectedComponents(a, new Rectangle(0, 0, width, height), mask, 80, 700, 40, 760, 1200, width * height);
            if (components.Count == 0) return Rectangle.Empty;
            Component best = components.OrderByDescending(c => c.Area).First();
            Rectangle r = best.Bounds;
            r.Inflate(8, 8);
            r.Intersect(new Rectangle(0, 0, width, height));
            return r;
        }

        private static int ScorePanel(Bitmap frame, Rectangle panel)
        {
            if (panel.IsEmpty || panel.Width < 120 || panel.Height < 50) return 0;
            try
            {
                VanillaUiSlotGrid grid = DetectSlotGrid(frame, panel);
                return panel.Width * panel.Height + grid.Columns.Length * grid.Rows.Length * 100;
            }
            catch { return 0; }
        }

        private static int[] LongestRegularRun(int[] values)
        {
            values = values.Distinct().OrderBy(v => v).ToArray();
            int[] best = new int[0];
            for (int i = 0; i < values.Length; i++)
            {
                var run = new List<int> { values[i] };
                int spacing = 0;
                for (int j = i + 1; j < values.Length; j++)
                {
                    int delta = values[j] - run[run.Count - 1];
                    if (run.Count == 1)
                    {
                        if (delta < 28 || delta > 58) continue;
                        spacing = delta; run.Add(values[j]);
                    }
                    else if (Math.Abs(delta - spacing) <= 5) run.Add(values[j]);
                    else if (delta > spacing + 6) break;
                }
                if (run.Count > best.Length) best = run.ToArray();
            }
            return best;
        }

        private static int MedianSpacing(int[] values)
        {
            if (values == null || values.Length < 2) return 41;
            int[] d = values.Skip(1).Select((value, index) => value - values[index]).Where(v => v > 0).OrderBy(v => v).ToArray();
            return d.Length == 0 ? 41 : d[d.Length / 2];
        }

        private static int LongestCategoryRuleRun(PixelBuffer pixels, int left, int right, int y, out int bestLeft, out int bestRight)
        {
            bestLeft = left; bestRight = left - 1;
            int currentLeft = left;
            bool inRun = false;
            for (int x = left; x < right; x++)
            {
                bool rule = pixels.At(x, y).CategoryRule;
                if (rule && !inRun) { currentLeft = x; inRun = true; }
                bool end = inRun && (!rule || x == right - 1);
                if (!end) continue;
                int currentRight = rule && x == right - 1 ? x : x - 1;
                if (currentRight - currentLeft > bestRight - bestLeft)
                {
                    bestLeft = currentLeft;
                    bestRight = currentRight;
                }
                inRun = false;
            }
            return Math.Max(0, bestRight - bestLeft + 1);
        }

        private static double CategoryVerticalRuleCoverage(PixelBuffer pixels, int x, int[] boundaries)
        {
            int rule = 0, total = 0;
            for (int k = 0; k < 4; k++)
            {
                int top = boundaries[k] + 4;
                int bottom = boundaries[k + 1] - 4;
                for (int y = top; y <= bottom; y++)
                {
                    if (x < 0 || x >= pixels.Width || y < 0 || y >= pixels.Height) continue;
                    total++;
                    if (pixels.At(x, y).CategoryRule) rule++;
                }
            }
            return total == 0 ? 0 : rule / (double)total;
        }

        private static double CategoryRightBorderFraction(PixelBuffer pixels, int x, int top, int bottom)
        {
            int rule = 0, total = 0;
            int innerTop = top + Math.Max(2, (bottom - top) / 12);
            int innerBottom = bottom - Math.Max(2, (bottom - top) / 12);
            for (int y = innerTop; y <= innerBottom; y++)
            {
                bool hit = false;
                for (int dx = -1; dx <= 1; dx++)
                {
                    int sx = x + dx;
                    if (sx >= 0 && sx < pixels.Width && y >= 0 && y < pixels.Height
                        && pixels.At(sx, y).CategoryRule)
                    {
                        hit = true;
                        break;
                    }
                }
                total++;
                if (hit) rule++;
            }
            return total == 0 ? 1 : rule / (double)total;
        }

        private static List<Component> ConnectedComponents(PixelBuffer pixels, Rectangle area, Func<PixelInfo, bool> predicate,
            int minWidth, int maxWidth, int minHeight, int maxHeight, int minArea, int maxArea)
        {
            bool[] mask = new bool[pixels.Width * pixels.Height];
            for (int y = area.Top; y < area.Bottom; y++)
                for (int x = area.Left; x < area.Right; x++) mask[y * pixels.Width + x] = predicate(pixels.At(x, y));
            return ConnectedComponents(pixels, area, mask, minWidth, maxWidth, minHeight, maxHeight, minArea, maxArea);
        }

        private static List<Component> ConnectedComponents(PixelBuffer pixels, Rectangle area, bool[] mask,
            int minWidth, int maxWidth, int minHeight, int maxHeight, int minArea, int maxArea)
        {
            var result = new List<Component>();
            int width = pixels.Width;
            int[] queue = new int[Math.Max(1, area.Width * area.Height)];
            for (int y0 = area.Top; y0 < area.Bottom; y0++)
            for (int x0 = area.Left; x0 < area.Right; x0++)
            {
                int start = y0 * width + x0;
                if (!mask[start]) continue;
                int head = 0, tail = 0; queue[tail++] = start; mask[start] = false;
                int minX = x0, maxX = x0, minY = y0, maxY = y0, count = 0;
                while (head < tail)
                {
                    int index = queue[head++], y = index / width, x = index - y * width; count++;
                    if (x < minX) minX = x; if (x > maxX) maxX = x; if (y < minY) minY = y; if (y > maxY) maxY = y;
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < area.Left || nx >= area.Right || ny < area.Top || ny >= area.Bottom) continue;
                        int ni = ny * width + nx;
                        if (!mask[ni]) continue;
                        mask[ni] = false; queue[tail++] = ni;
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w >= minWidth && w <= maxWidth && h >= minHeight && h <= maxHeight && count >= minArea && count <= maxArea)
                    result.Add(new Component { Bounds = new Rectangle(minX, minY, w, h), Area = count, Center = new Point((minX + maxX) / 2, (minY + maxY) / 2) });
            }
            return result;
        }

        internal struct PixelInfo
        {
            internal byte R, G, B;
            internal bool Light { get { return R >= 235 && G >= 235 && B >= 235; } }
            internal bool CategoryRule
            {
                get
                {
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    int mean = (R + G + B) / 3;
                    return max - min <= 18 && mean >= 115 && mean <= 245;
                }
            }
            internal bool Pale
            {
                get
                {
                    int max = Math.Max(R, Math.Max(G, B)), min = Math.Min(R, Math.Min(G, B));
                    return R > 180 && G > 185 && B > 190 && B - R >= 5 && max - min <= 48;
                }
            }
        }

        internal sealed class PixelBuffer
        {
            private readonly byte[] data;
            internal int Width { get; private set; }
            internal int Height { get; private set; }
            private PixelBuffer(int width, int height, byte[] data) { Width = width; Height = height; this.data = data; }
            internal static PixelBuffer Read(Bitmap source)
            {
                using (var copy = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(copy)) g.DrawImageUnscaled(source, 0, 0);
                    Rectangle rect = new Rectangle(0, 0, copy.Width, copy.Height);
                    BitmapData bits = copy.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                    try
                    {
                        int stride = bits.Stride, row = copy.Width * 3;
                        byte[] raw = new byte[row * copy.Height];
                        byte[] scan = new byte[Math.Abs(stride) * copy.Height];
                        Marshal.Copy(bits.Scan0, scan, 0, scan.Length);
                        for (int y = 0; y < copy.Height; y++) Buffer.BlockCopy(scan, y * Math.Abs(stride), raw, y * row, row);
                        return new PixelBuffer(copy.Width, copy.Height, raw);
                    }
                    finally { copy.UnlockBits(bits); }
                }
            }
            internal PixelInfo At(int x, int y)
            {
                int index = (y * Width + x) * 3;
                return new PixelInfo { B = data[index], G = data[index + 1], R = data[index + 2] };
            }
            internal bool IsLight(int x, int y) { return At(x, y).Light; }
            internal int ColorDistance(PixelBuffer other, int x, int y)
            {
                PixelInfo a = At(x, y), b = other.At(x, y);
                return Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
            }
            internal int PaleCount(int cx, int cy, int rx, int ry)
            {
                int count = 0;
                for (int y = Math.Max(0, cy - ry); y <= Math.Min(Height - 1, cy + ry); y++)
                    for (int x = Math.Max(0, cx - rx); x <= Math.Min(Width - 1, cx + rx); x++) if (At(x, y).Pale) count++;
                return count;
            }

            internal double MeanPatchColorDistance(Point a, Point b, int rx, int ry)
            {
                long total = 0;
                int count = 0;
                for (int dy = -ry; dy <= ry; dy++)
                for (int dx = -rx; dx <= rx; dx++)
                {
                    int ax = a.X + dx, ay = a.Y + dy, bx = b.X + dx, by = b.Y + dy;
                    if (ax < 0 || ax >= Width || ay < 0 || ay >= Height
                        || bx < 0 || bx >= Width || by < 0 || by >= Height) continue;
                    PixelInfo pa = At(ax, ay), pb = At(bx, by);
                    total += Math.Abs(pa.R - pb.R) + Math.Abs(pa.G - pb.G) + Math.Abs(pa.B - pb.B);
                    count++;
                }
                return count == 0 ? double.MaxValue : total / (double)(count * 3);
            }
            internal int BlueSelectionCount(Rectangle box)
            {
                int count = 0;
                Rectangle clipped = Rectangle.Intersect(new Rectangle(0, 0, Width, Height), box);
                for (int y = clipped.Top; y < clipped.Bottom; y++)
                    for (int x = clipped.Left; x < clipped.Right; x++)
                    {
                        PixelInfo p = At(x, y);
                        if (p.B >= 180 && p.B - p.R >= 60 && p.B - p.G >= 30) count++;
                    }
                return count;
            }
        }
    }
}
