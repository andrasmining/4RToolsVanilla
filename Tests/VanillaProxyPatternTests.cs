using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaProxyPatternTests
    {
        private static int passed, failed, fixtureNumber;
        internal static readonly string[] Names = { "Global", "Manila", "Singapore", "Tokyo", "Hong Kong", "Los Angeles", "Australia", "UAE" };

        internal static int Run()
        {
            Test("Proxy recognition rejects blank screens and anonymous row shapes", RejectShapes);
            Test("All eight proxy names survive resolution, DPI and row reorder", DetectNames);
            Test("Proxy names and selection survive softened resampling", DetectSoftText);
            Test("Proxy selection requires a highlight behind the configured name", HighlightBelongsToName);
            Test("Proxy names reject suffixes, unknown services and duplicate identities", RejectAmbiguousNames);
            Test("Service title separates named service rows from arbitrary text", RequireServiceTitle);
            Test("Service recognition rejects multiple actual forms", RejectMultipleForms);
            Console.WriteLine("Proxy pattern: {0} passed; {1} failed.", passed, failed);
            return failed;
        }

        private static void RejectShapes()
        {
            using (var image = new Bitmap(1280, 720))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                VanillaProxyLayout layout;
                string evidence;
                Assert(!VanillaProxyPattern.TryDetect(image, out layout, out evidence), "Blank image must fail closed.");
            }
            using (var image = new Bitmap(1280, 720))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.White);
                graphics.FillRectangle(Brushes.LightSteelBlue, 440, 310, 300, 20);
                for (int row = 0; row < 8; row++)
                    for (int column = 0; column < 20; column++)
                        graphics.FillRectangle(Brushes.Black, 450 + column * 9, 341 + row * 17, 6, 7);
                VanillaProxyLayout layout;
                string evidence;
                Assert(!VanillaProxyPattern.TryDetect(image, out layout, out evidence), "Eight plausible text shapes are not named services.");
            }
        }

        private static void DetectNames()
        {
            Size[] sizes = { new Size(800, 600), new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440) };
            float[] scales = { .85f, 1f, 1.5f, 2f };
            string[] fonts = { "Tahoma", "Arial", "Segoe UI", "Tahoma" };
            string[] reordered = { "UAE", "Tokyo", "Australia", "Global", "Hong Kong", "Manila", "Los Angeles", "Singapore" };
            for (int index = 0; index < sizes.Length; index++)
            {
                using (Bitmap image = ServiceImage(sizes[index], scales[index], fonts[index], reordered, "UAE", false))
                {
                    VanillaProxyLayout layout;
                    string evidence;
                    Assert(VanillaProxyPattern.TryDetect(image, out layout, out evidence), sizes[index] + ": " + evidence);
                    Assert(layout.Services.Length == 8, "All eight actual names required: " + evidence);
                    foreach (VanillaProxyRoute route in Enum.GetValues(typeof(VanillaProxyRoute)))
                    {
                        VanillaServiceRow row;
                        Assert(layout.TryFind(route, out row), "Missing configured service " + route + ": " + evidence);
                        Assert(row.Name == VanillaProxyPattern.NameForRoute(route), "Row position changed service meaning.");
                        Assert(row.IsHighlighted == (row.Name == "UAE"), "First-row selected proof mismatched " + row.Name);
                        int expected = Array.IndexOf(reordered, row.Name);
                        Rectangle expectedRow = ExpectedRow(sizes[index], scales[index], expected);
                        Assert(expectedRow.Contains(new Point(row.Bounds.Left + row.Bounds.Width / 2, row.Bounds.Top + row.Bounds.Height / 2)),
                            "Recognized name bounds escaped its actual control for " + row.Name);
                    }
                }
            }
        }

        private static void DetectSoftText()
        {
            using (Bitmap image = ServiceImage(new Size(1280, 720), 1f, "Tahoma", Names, "Hong Kong", true))
            {
                VanillaProxyLayout layout;
                VanillaServiceRow row;
                string evidence;
                Assert(VanillaProxyPattern.TryDetect(image, out layout, out evidence), "Softened list rejected: " + evidence);
                Assert(layout.TryFind(VanillaProxyRoute.HongKong, out row) && row.IsHighlighted,
                    "Softened Hong Kong must be recognized and positively selected: " + evidence);
            }
        }

        private static void HighlightBelongsToName()
        {
            foreach (string selected in new[] { "Tokyo", "Global", null })
            using (Bitmap image = ServiceImage(new Size(1280, 720), 1f, "Tahoma", Names, selected, false))
            {
                VanillaProxyLayout layout;
                VanillaServiceRow row;
                string evidence;
                Assert(VanillaProxyPattern.TryDetect(image, out layout, out evidence), evidence);
                Assert(layout.TryFind(VanillaProxyRoute.Tokyo, out row), "Tokyo must be observed by name.");
                Assert(row.IsHighlighted == (selected == "Tokyo"), "Another row's highlight cannot authorize Tokyo.");
            }
        }

        private static void RejectAmbiguousNames()
        {
            using (Bitmap image = ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { "Global", "Tokyo Experimental", "Singapore", "Unknown" }, "Tokyo Experimental", false))
            {
                VanillaProxyLayout layout;
                VanillaServiceRow row;
                string evidence;
                bool detected = VanillaProxyPattern.TryDetect(image, out layout, out evidence);
                Assert(!detected || !layout.TryFind(VanillaProxyRoute.Tokyo, out row), "Substring is not exact Tokyo identity.");
            }
            using (Bitmap image = ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { "Global", "Tokyo", "Tokyo", "Singapore" }, "Tokyo", false))
            {
                VanillaProxyLayout layout;
                string evidence;
                Assert(!VanillaProxyPattern.TryDetect(image, out layout, out evidence), "Duplicate identities must fail closed.");
            }
        }

        private static void RequireServiceTitle()
        {
            using (Bitmap image = ServiceImage(new Size(1280, 720), 1f, "Tahoma", Names, "Tokyo", false, "Account settings"))
            {
                VanillaProxyLayout layout;
                string evidence;
                Assert(!VanillaProxyPattern.TryDetect(image, out layout, out evidence), "Names in another form must not authorize selection.");
            }
        }

        private static void RejectMultipleForms()
        {
            using (Bitmap one = ServiceImage(new Size(800, 600), 1f, "Tahoma", Names, "Tokyo", false))
            using (Bitmap two = new Bitmap(1600, 600))
            using (Graphics graphics = Graphics.FromImage(two))
            {
                graphics.DrawImageUnscaled(one, 0, 0);
                graphics.DrawImageUnscaled(one, 800, 0);
                VanillaProxyLayout layout;
                string evidence;
                Assert(!VanillaProxyPattern.TryDetect(two, out layout, out evidence), "Two service forms are ambiguous.");
            }
        }

        // Fixtures render actual text with several independent Windows fonts and include
        // screenshot-like title/list/selection controls. They are synthetic OCR regressions,
        // not a claim of live Vanilla or arbitrary unreadable-screenshot validation.
        internal static Bitmap ServiceImage(Size size, float scale, string family, string[] names,
            string selected, bool soften, string title = "Select Service:", bool server = false)
        {
            var image = new Bitmap(size.Width, size.Height);
            using (Graphics graphics = Graphics.FromImage(image))
            using (var font = new Font(family, 14f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var blue = new SolidBrush(Color.FromArgb(195, 215, 245)))
            using (var titleBlue = new SolidBrush(Color.FromArgb(170, 192, 231)))
            {
                graphics.Clear(Color.FromArgb(245, 246, 241));
                // Many irrelevant blue rectangular patches must not consume the OCR
                // candidate budget: unlike a title bar, these have no text ink.
                for (int n = 0; n < 24; n++) graphics.FillRectangle(blue, (n % 4) * size.Width / 4,
                    (n / 4) * Math.Max(16, size.Height / 8), 115, 5);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                int left = size.Width / 5, top = size.Height / 4;
                int width = (int)(330 * scale), titleHeight = (int)(21 * scale);
                int height = (int)(242 * scale);
                graphics.FillRectangle(Brushes.WhiteSmoke, left, top, width, height);
                graphics.DrawRectangle(Pens.Gray, left, top, width, height);
                graphics.FillRectangle(titleBlue, left + 1, top + 1, width - 2, titleHeight);
                graphics.DrawEllipse(Pens.SteelBlue, left + 4 * scale, top + 5 * scale, 9 * scale, 9 * scale);
                graphics.DrawLine(Pens.SteelBlue, left + 6 * scale, top + 9 * scale, left + 11 * scale, top + 9 * scale);
                graphics.DrawString(title, font, Brushes.Black, left + 17 * scale, top + scale);
                for (int row = 0; row < names.Length; row++)
                {
                    Rectangle bounds = ExpectedRow(size, scale, row);
                    graphics.FillRectangle(names[row] == selected ? blue : Brushes.WhiteSmoke, bounds);
                    string text = server ? names[row] : "[ Proxy Connection ] " + names[row];
                    graphics.DrawString(text, font, Brushes.Black, bounds.Left + 2 * scale, bounds.Top);
                }
                graphics.DrawString("OK", font, Brushes.Black, left + width - 86 * scale, top + height - 24 * scale);
                graphics.DrawString(server ? "cancel" : "exit", font, Brushes.Black, left + width - 46 * scale, top + height - 24 * scale);
            }
            if (!soften) { SaveFixture(image, server); return image; }
            using (image)
            using (var reduced = new Bitmap((int)(image.Width * .8), (int)(image.Height * .8)))
            {
                using (Graphics graphics = Graphics.FromImage(reduced))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(image, new Rectangle(Point.Empty, reduced.Size));
                }
                var restored = new Bitmap(image.Width, image.Height);
                using (Graphics graphics = Graphics.FromImage(restored))
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.DrawImage(reduced, new Rectangle(Point.Empty, restored.Size));
                }
                SaveFixture(restored, server);
                return restored;
            }
        }

        private static void SaveFixture(Bitmap image, bool server)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)) return;
            string directory = Path.Combine(Environment.CurrentDirectory, "dist", "recognition");
            Directory.CreateDirectory(directory);
            string stem = (server ? "server-" : "proxy-") + (++fixtureNumber).ToString("D2");
            image.Save(Path.Combine(directory, stem + ".png"),
                System.Drawing.Imaging.ImageFormat.Png);
            string report = VanillaServiceRecognition.DescribeSyntheticFixture(image);
            File.WriteAllText(Path.Combine(directory, stem + ".txt"), report);
            Console.WriteLine("Synthetic service fixture " + stem + ": " + report);
        }

        private static Rectangle ExpectedRow(Size size, float scale, int row)
        {
            return new Rectangle(size.Width / 5 + (int)(6 * scale), size.Height / 4 + (int)((24 + row * 23) * scale),
                (int)(318 * scale), (int)(21 * scale));
        }

        private static void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
