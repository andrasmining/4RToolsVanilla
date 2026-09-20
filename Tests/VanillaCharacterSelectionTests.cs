using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaCharacterSelectionTests
    {
        private static int passed, failed;
        internal static int Run()
        {
            Test("Every target from every initial slot uses observed layout and selection", EverySlot);
            Test("Unknown and stale observations authorize no character input", UnknownAndStale);
            Test("Wrapping at the origin never authorizes Enter", WrappedClamp);
            Test("Stalled navigation never authorizes Enter", StalledNavigation);
            Test("Changed layout and wrong final selection never authorize Enter", ChangedLayout);
            Test("Cancellation and lost input focus stop character selection", Cancellation);
            Test("Captured input proof rejects window, focus, geometry and age changes", VisualInputProofBinding);
            Test("Character selection cannot combine evidence from replacement windows", ChangedSurface);
            Test("Character structural detector follows scaled and softened synthetic cards", SyntheticCards);
            Test("Character production OCR recognizes rendered labels and requires unique selection", ProductionOcrSurface);
            Test("Character detector rejects missing identity and ambiguous frames", RejectUnknownSurface);
            Console.WriteLine("Character selector: {0} passed; {1} failed. Synthetic fixtures; no live character layout was validated.", passed, failed);
            return failed;
        }

        private sealed class Simulation
        {
            internal int Position, Columns, Enters, KeysSent, Moves;
            internal long Frame;
            internal bool Wrap, Stalled, Cancelled, Stale, ChangeLayout, LoseSelection;
            internal Simulation(int columns, int position) { Columns = columns; Position = position; }
            internal VanillaCharacterSelectionObservation Observe()
            {
                return new VanillaCharacterSelectionObservation
                {
                    FrameId = Stale ? 1 : ++Frame,
                    Cards = Enumerable.Range(0, 15).Select(i => new Rectangle((ChangeLayout && KeysSent > 0 ? 30 : 10)
                        + i % Columns * 90, 30 + i / Columns * 75, 80, 65)).ToArray(),
                    Columns = Columns, Selected = LoseSelection && KeysSent > 0 ? -1 : Position
                };
            }
            internal void Press(Keys key)
            {
                KeysSent++;
                if (key == Keys.Enter) { Enters++; return; }
                if (Stalled) return;
                int row = Position / Columns, column = Position % Columns, rows = 15 / Columns;
                if (key == Keys.Up) row = row > 0 ? row - 1 : Wrap ? rows - 1 : 0;
                else if (key == Keys.Left) column = column > 0 ? column - 1 : Wrap ? Columns - 1 : 0;
                else if (key == Keys.Right) column = Math.Min(Columns - 1, column + 1);
                else if (key == Keys.Down) row = Math.Min(rows - 1, row + 1);
                else throw new Exception("Unexpected key " + key);
                int next = row * Columns + column;
                if (Position != next) Moves++;
                Position = next;
            }
            internal void Run(int target)
            { VanillaCharacterSelector.Select(target, Observe, Press, _ => { }, () => Cancelled); }
        }

        private static void EverySlot()
        {
            foreach (int columns in new[] { 1, 3, 5, 15 })
            for (int start = 0; start < 15; start++)
            for (int target = 1; target <= 15; target++)
            {
                var sim = new Simulation(columns, start);
                sim.Run(target);
                Assert(sim.Position == target - 1 && sim.Enters == 1, "Wrong target/confirmation on observed " + columns + "-column layout.");
                Assert(sim.Frame >= sim.KeysSent * 2, "Navigation was not freshly verified after every key.");
                Assert(sim.Moves > 0, "Selection was confirmed without any observed keyboard-driven movement.");
                if (start == 0 && target == 1) Assert(sim.Moves == 2, "Slot one must verify an outward/return probe.");
            }
        }

        private static void UnknownAndStale()
        {
            int keys = 0;
            Reject(() => VanillaCharacterSelector.Select(1, () => null, _ => keys++, _ => { }, () => false));
            Assert(keys == 0, "Unknown surface sent input.");
            var sim = new Simulation(5, 7) { Stale = true };
            Reject(() => sim.Run(3));
            Assert(sim.KeysSent == 0, "Reused frame authorized input.");
            sim = new Simulation(5, 7) { LoseSelection = true };
            Reject(() => sim.Run(3));
            Assert(sim.Enters == 0, "Missing selection after a key authorized Enter.");
        }

        private static void WrappedClamp()
        {
            foreach (int start in Enumerable.Range(0, 15))
            {
                var sim = new Simulation(5, start) { Wrap = true };
                Reject(() => sim.Run(9));
                Assert(sim.Enters == 0, "Wrapped origin authorized Enter.");
            }
        }

        private static void StalledNavigation()
        {
            foreach (int columns in new[] { 3, 5 })
            {
                var sim = new Simulation(columns, 14) { Stalled = true };
                Reject(() => sim.Run(1));
                Assert(sim.KeysSent == 1 && sim.Enters == 0, "Stalled interior key was retried/confirmed.");
                sim = new Simulation(columns, 0) { Stalled = true };
                Reject(() => sim.Run(15));
                Assert(sim.Enters == 0, "Stalled outbound navigation authorized Enter.");
                sim = new Simulation(columns, 0) { Stalled = true };
                Reject(() => sim.Run(1));
                Assert(sim.Enters == 0, "A static first-card frame authorized Enter without proving selection response.");
            }
        }

        private static void ChangedLayout()
        {
            var sim = new Simulation(5, 7) { ChangeLayout = true };
            Reject(() => sim.Run(7));
            Assert(sim.Enters == 0, "Layout changed during navigation but Enter was sent.");
            sim = new Simulation(5, 0);
            int observations = 0;
            Reject(() => VanillaCharacterSelector.Select(1, () =>
            {
                observations++;
                if (observations >= 11) sim.Position = 1; // Target moves after the round-trip probe, before final confirmation.
                return sim.Observe();
            }, sim.Press, _ => { }, () => false));
            Assert(sim.Enters == 0, "Target changed before final confirmation.");
        }

        private static void Cancellation()
        {
            var sim = new Simulation(5, 14);
            bool cancelled = false;
            try
            {
                VanillaCharacterSelector.Select(3, sim.Observe, key => { sim.Press(key); sim.Cancelled = true; }, _ => { }, () => sim.Cancelled);
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && sim.KeysSent == 1 && sim.Enters == 0, "Cancellation continued input.");
            sim = new Simulation(5, 14);
            Reject(() => VanillaCharacterSelector.Select(1, sim.Observe, _ => { throw new InvalidOperationException("Focus lost"); }, _ => { }, () => false));
            Assert(sim.KeysSent == 0 && sim.Enters == 0, "Lost focus still confirmed selection.");
        }

        private static void SyntheticCards()
        {
            foreach (int columns in new[] { 3, 5 })
            foreach (double scale in new[] { .8, 1.0, 1.5, 2.0 })
            using (Bitmap original = Surface(columns, 7))
            using (var scaled = new Bitmap((int)(original.Width * scale), (int)(original.Height * scale)))
            {
                using (Graphics g = Graphics.FromImage(scaled))
                { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(original, 0, 0, scaled.Width, scaled.Height); }
                VanillaCharacterSelectionObservation result;
                string evidence;
                Assert(VanillaCharacterPattern.TryDetect(scaled, Labels(columns, scale), out result, out evidence),
                    "Synthetic " + columns + " columns x " + scale + " rejected: " + evidence);
                Assert(result.Columns == columns && result.Selected == 7, "Detected the wrong layout/selection.");
            }
        }

        private static void ChangedSurface()
        {
            var sim = new Simulation(5, 14);
            Reject(() => VanillaCharacterSelector.Select(1, () =>
            {
                var observation = sim.Observe();
                observation.InputProof = new VanillaVisualInputProof(42, new IntPtr(sim.KeysSent == 0 ? 100 : 200),
                    new Size(800, 600), Point.Empty, Stopwatch.GetTimestamp());
                return observation;
            }, sim.Press, _ => { }, () => false));
            Assert(sim.KeysSent == 1 && sim.Enters == 0, "Replacement HWND was treated as the same selected character surface.");
        }

        private static void VisualInputProofBinding()
        {
            IntPtr window = new IntPtr(100);
            var size = new Size(800, 600);
            var origin = new Point(20, 30);
            long captured = Stopwatch.Frequency * 10;
            var proof = new VanillaVisualInputProof(42, window, size, origin, captured);
            proof.RequireMatches(42, window, window, size, origin, captured);
            Assert(proof.ControlCenter(new Rectangle(100, 200, 80, 20)) == new Point(140, 210), "Click escaped detected control.");
            Reject(() => proof.ControlCenter(new Rectangle(790, 200, 80, 20)));
            for (int failure = 0; failure < 7; failure++)
            {
                int condition = failure, sent = 0;
                Reject(() => VanillaForegroundInput.DispatchGuardedKey(Keys.Enter, () => proof.RequireMatches(
                    condition == 0 ? 43 : 42,
                    condition == 1 ? new IntPtr(101) : window,
                    condition == 2 ? IntPtr.Zero : window,
                    condition == 3 ? new Size(900, 600) : size,
                    condition == 4 ? new Point(21, 30) : origin,
                    condition == 5 ? captured + Stopwatch.Frequency * 6 : condition == 6 ? captured - 1 : captured),
                    (key, up) => sent++, _ => { }));
                Assert(sent == 0, "Invalid capture proof emitted keyboard input.");
            }
            var events = new List<bool>();
            bool cancelled = false;
            try
            {
                VanillaForegroundInput.DispatchGuardedKey(Keys.Enter, () => { }, (key, up) => events.Add(up),
                    _ => { throw new OperationCanceledException(); });
            }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && events.SequenceEqual(new[] { false, true }), "Cancellation left a proof-bound key held down.");
        }

        private static void RejectUnknownSurface()
        {
            using (Bitmap image = Surface(5, 3))
            {
                VanillaCharacterSelectionObservation result;
                string evidence;
                Assert(!VanillaCharacterPattern.TryDetect(image, new VanillaTextLine[0], out result, out evidence), "Cards without semantic surface accepted.");
                var unrelated = Labels(5, 1); unrelated[0].Text = "Inventory";
                Assert(!VanillaCharacterPattern.TryDetect(image, unrelated, out result, out evidence), "Unrelated surface accepted.");
            }
            foreach (int selected in new[] { -1, -2 })
            using (Bitmap image = Surface(5, selected))
            {
                VanillaCharacterSelectionObservation result;
                string evidence;
                Assert(!VanillaCharacterPattern.TryDetect(image, Labels(5, 1), out result, out evidence), "No unique selection was accepted.");
            }
            using (Bitmap image = Surface(5, 3))
            {
                using (Graphics g = Graphics.FromImage(image)) g.FillRectangle(Brushes.White, 58 + 4 * 90, 88 + 2 * 75, 86, 71);
                VanillaCharacterSelectionObservation result;
                string evidence;
                Assert(!VanillaCharacterPattern.TryDetect(image, Labels(5, 1), out result, out evidence), "Fourteen cards accepted as fifteen.");
            }
        }

        private static void ProductionOcrSurface()
        {
            foreach (int columns in new[] { 3, 5 })
            using (Bitmap image = LabeledSurface(columns, 7, "Character Select"))
            {
                SaveFixture(image, "character-ocr-" + columns + "-columns-selected");
                VanillaCharacterSelectionObservation observation;
                string evidence;
                Assert(VanillaCharacterPattern.TryDetect(image, out observation, out evidence),
                    "Production OCR did not recognize rendered character surface: " + evidence);
                Assert(observation.Columns == columns && observation.Selected == 7,
                    "Production OCR changed observed card layout or selected slot.");
            }
            foreach (int selected in new[] { -1, -2 })
            using (Bitmap image = LabeledSurface(5, selected, "Character Select"))
            {
                SaveFixture(image, selected == -1 ? "character-ocr-no-selection" : "character-ocr-multiple-selection");
                VanillaCharacterSelectionObservation observation;
                string evidence;
                Assert(!VanillaCharacterPattern.TryDetect(image, out observation, out evidence),
                    "Production OCR accepted missing/ambiguous selection.");
            }
            using (Bitmap image = LabeledSurface(5, 7, "Inventory"))
            {
                SaveFixture(image, "character-ocr-wrong-title");
                VanillaCharacterSelectionObservation observation;
                string evidence;
                Assert(!VanillaCharacterPattern.TryDetect(image, out observation, out evidence),
                    "Production OCR accepted cards on an unrelated named screen.");
            }
        }

        private static Bitmap LabeledSurface(int columns, int selected, string title)
        {
            Bitmap image = Surface(columns, selected);
            VanillaTextLine[] labels = Labels(columns, 1);
            using (Graphics graphics = Graphics.FromImage(image))
            using (var font = new Font("Tahoma", 16, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(title, font, Brushes.Black, labels[0].Bounds, StringFormat.GenericTypographic);
                graphics.DrawString(labels[1].Text, font, Brushes.Black, labels[1].Bounds, StringFormat.GenericTypographic);
            }
            return image;
        }

        private static void SaveFixture(Bitmap image, string name)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)) return;
            string directory = Path.Combine(Environment.CurrentDirectory, "dist", "recognition");
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, name + ".png"), ImageFormat.Png);
        }

        private static Bitmap Surface(int columns, int selected)
        {
            var bitmap = new Bitmap(620, 600);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.White);
                for (int i = 0; i < 15; i++)
                {
                    bool highlight = i == selected || (selected == -2 && i < 2);
                    using (var pen = new Pen(highlight ? Color.DeepSkyBlue : Color.FromArgb(70, 70, 70), 2))
                        g.DrawRectangle(pen, 60 + i % columns * 90, 90 + i / columns * 75, 80, 65);
                }
            }
            return bitmap;
        }

        private static VanillaTextLine[] Labels(int columns, double scale)
        {
            Func<Rectangle, Rectangle> scaled = r => new Rectangle((int)(r.Left * scale), (int)(r.Top * scale), (int)(r.Width * scale), (int)(r.Height * scale));
            return new[]
            {
                new VanillaTextLine { Text = "Select Character", Bounds = scaled(new Rectangle(50, 40, 180, 20)), Confidence = 95 },
                new VanillaTextLine { Text = "GAME START", Bounds = scaled(new Rectangle(360, 100 + 15 / columns * 75, 130, 20)), Confidence = 95 }
            };
        }

        private static void Reject(Action action)
        {
            bool rejected = false;
            try { action(); } catch (InvalidOperationException) { rejected = true; }
            Assert(rejected, "Unsafe character state was accepted.");
        }
        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex.Message); }
        }
        private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
