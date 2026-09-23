using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    public static class VanillaCapturedPointTests
    {
        private static readonly Point Target = new Point(560, 380);
        public static int Run()
        {
            var tests = new Dictionary<string, Action>
            {
                { "Captured point ignores animated target and spell pixels", AnimatedTarget },
                { "Captured point follows terrain through repeated rest camera translations", RepeatedTranslation },
                { "Captured point registers fine terrain at odd half-resolution shifts", FineTerrain },
                { "Captured point tolerates softened terrain and partial occlusion", SoftenedTerrain },
                { "Captured point rejects unknown scenes, geometry changes and excessive camera travel", InvalidScenes },
                { "Captured point rejects ambiguous repeated terrain", AmbiguousTerrain },
                { "Capture permits an animated or featureless target patch", FeaturelessTarget }
            };
            int failed = 0;
            foreach (var test in tests)
            {
                try { test.Value(); Console.WriteLine("PASS " + test.Key); }
                catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Key + ": " + ex); }
            }
            Console.WriteLine("Captured stationary points: {0} passed; {1} failed. Synthetic images only; no game/input opened.", tests.Count - failed, failed);
            return failed;
        }

        private static void AnimatedTarget()
        {
            using (Bitmap reference = Terrain(7))
            using (Bitmap current = (Bitmap)reference.Clone())
            {
                using (Graphics g = Graphics.FromImage(reference)) g.FillRectangle(Brushes.Purple, Target.X - 20, Target.Y - 40, 40, 70);
                using (Graphics g = Graphics.FromImage(current))
                {
                    g.FillEllipse(Brushes.Cyan, Target.X - 65, Target.Y - 65, 130, 130);
                    g.FillEllipse(Brushes.White, Target.X - 35, Target.Y - 60, 70, 100);
                }
                var clock = Stopwatch.StartNew();
                var tracker = new VanillaCapturedPointTracker(reference, Target);
                Console.WriteLine("Initial 1024x768 terrain registration: {0} ms", clock.ElapsedMilliseconds);
                clock.Restart();
                Equal(Target, tracker.Locate(current), 0);
                Console.WriteLine("Unchanged 1024x768 scene with animated target: {0} ms", clock.ElapsedMilliseconds);
            }
        }

        private static void RepeatedTranslation()
        {
            using (Bitmap reference = Terrain(11))
            {
                var tracker = new VanillaCapturedPointTracker(reference, Target);
                foreach (Point shift in new[] { new Point(32, -18), new Point(-28, 14), new Point(57, -31), Point.Empty })
                using (Bitmap current = Shift(reference, shift))
                {
                    using (Graphics g = Graphics.FromImage(current))
                        g.FillEllipse(Brushes.Cyan, Target.X + shift.X - 40, Target.Y + shift.Y - 60, 80, 110);
                    var clock = Stopwatch.StartNew();
                    Equal(new Point(Target.X + shift.X, Target.Y + shift.Y), tracker.Locate(current), 2);
                    Console.WriteLine("1024x768 scene translation ({0},{1}): {2} ms", shift.X, shift.Y, clock.ElapsedMilliseconds);
                }
            }
        }

        private static void SoftenedTerrain()
        {
            using (Bitmap reference = Terrain(23))
            using (Bitmap shifted = Shift(reference, new Point(30, 16)))
            using (var small = new Bitmap(820, 614))
            using (var current = new Bitmap(1024, 768))
            {
                using (Graphics g = Graphics.FromImage(small))
                { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(shifted, new Rectangle(Point.Empty, small.Size)); }
                using (Graphics g = Graphics.FromImage(current))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(small, new Rectangle(Point.Empty, current.Size));
                    g.FillRectangle(Brushes.DarkSlateGray, 120, 170, 150, 470);
                }
                Equal(new Point(Target.X + 30, Target.Y + 16), new VanillaCapturedPointTracker(reference, Target).Locate(current), 2);
            }
        }

        private static void FineTerrain()
        {
            using (Bitmap reference = Terrain(83, 2))
            using (Bitmap current = Shift(reference, new Point(26, -18)))
                Equal(new Point(Target.X + 26, Target.Y - 18), new VanillaCapturedPointTracker(reference, Target).Locate(current), 2);
        }

        private static void InvalidScenes()
        {
            using (Bitmap reference = Terrain(31))
            using (Bitmap unrelated = Terrain(99))
            using (Bitmap far = Shift(reference, new Point(230, 0)))
            using (var resized = new Bitmap(1025, 768))
            using (var blank = new Bitmap(1024, 768))
            {
                var tracker = new VanillaCapturedPointTracker(reference, Target);
                Reject(() => tracker.Locate(unrelated));
                Reject(() => tracker.Locate(far));
                Reject(() => tracker.Locate(resized));
                Reject(() => tracker.Locate(blank));
                Reject(() => new VanillaCapturedPointTracker(blank, Target));
                Reject(() => new VanillaCapturedPointTracker(reference, new Point(1024, 10)));
            }
        }

        private static void AmbiguousTerrain()
        {
            using (Bitmap tile = Terrain(47))
            using (var reference = new Bitmap(1024, 768))
            {
                using (Graphics g = Graphics.FromImage(reference))
                for (int y = 0; y < reference.Height; y += 64)
                for (int x = 0; x < reference.Width; x += 64)
                    g.DrawImage(tile, new Rectangle(x, y, 64, 64), new Rectangle(300, 200, 64, 64), GraphicsUnit.Pixel);
                using (Bitmap current = Shift(reference, new Point(18, 14)))
                    Reject(() => new VanillaCapturedPointTracker(reference, Target).Locate(current));
            }
        }

        private static void FeaturelessTarget()
        {
            using (var image = new Bitmap(1024, 768))
                Assert(!string.IsNullOrEmpty(VanillaCapturedTarget.Capture(image, Target)), "User's point was rejected because the sprite patch was featureless.");
        }

        private static Bitmap Terrain(int seed, int scale = 8)
        {
            var image = new Bitmap(1024, 768);
            using (var texture = new Bitmap(1024 / scale, 768 / scale))
            {
                var random = new Random(seed);
                for (int y = 0; y < texture.Height; y++) for (int x = 0; x < texture.Width; x++)
                {
                    int light = random.Next(25, 200);
                    texture.SetPixel(x, y, Color.FromArgb(Math.Max(0, light - 15), Math.Min(255, light + 35), Math.Max(0, light - 30)));
                }
                using (Graphics g = Graphics.FromImage(image))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(texture, new Rectangle(Point.Empty, image.Size));
                }
            }
            return image;
        }

        private static Bitmap Shift(Bitmap reference, Point shift)
        {
            var image = new Bitmap(reference.Width, reference.Height);
            using (Graphics g = Graphics.FromImage(image))
            { g.Clear(Color.Black); g.DrawImageUnscaled(reference, shift.X, shift.Y); }
            return image;
        }
        private static void Equal(Point expected, Point actual, int tolerance)
        { Assert(Math.Abs(expected.X - actual.X) <= tolerance && Math.Abs(expected.Y - actual.Y) <= tolerance, "Expected " + expected + ", got " + actual + "."); }
        private static void Reject(Action action)
        { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("Ambiguous/unusable scene authorized a captured-point click."); }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
