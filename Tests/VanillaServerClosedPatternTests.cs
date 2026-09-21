using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaServerClosedPatternTests
    {
        private static int passed, failed, fixtureNumber;
        internal static int Run()
        {
            Test("Exact server closed dialog survives scale and position changes", Scales);
            Test("Server closed remains recognizable beside a separate Please wait form", CoexistingWait);
            Test("Softened server closed dialog retains exact identity", Softened);
            Test("Tight native-style Message title retains complete glyphs", TightTitle);
            Test("Other server codes and similar messages remain unknown", OtherMessages);
            Test("Message title and OK must belong to the same complete form", RequireForm);
            Test("Multiple exact server closed forms fail closed", AmbiguousForms);
            Test("Visual classifier preserves exact server closed state", ClassifierWiring);
            Console.WriteLine("Server closed pattern: {0} passed; {1} failed. Synthetic bitmaps only.", passed, failed);
            return failed;
        }

        private static void Scales()
        {
            Size[] sizes = { new Size(800, 600), new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440) };
            float[] scales = { .85f, 1f, 1.5f, 2f };
            for (int n = 0; n < sizes.Length; n++)
            {
                Point position = n % 2 == 0 ? new Point(25, 35) : new Point(sizes[n].Width / 2, sizes[n].Height / 3);
                using (Bitmap image = Scene(sizes[n], position, scales[n], "Server Closed.(1)", false))
                {
                    Rectangle dialog;
                    string evidence;
                    Assert(VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), sizes[n] + ": " + evidence);
                    Assert(dialog.Contains(position.X + (int)(100 * scales[n]), position.Y + (int)(45 * scales[n])), "Detected frame escaped observed message.");
                }
            }
        }

        private static void CoexistingWait()
        {
            foreach (bool soften in new[] { false, true })
            using (Bitmap image = Scene(new Size(1280, 900), new Point(340, 240), 1f, "Server Closed.(1)", soften, true))
            {
                Rectangle dialog;
                string evidence;
                Assert(VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), evidence);
                Assert(dialog.Bottom < 430, "Please wait and server error were merged into one supposed form.");
            }
        }

        private static void Softened()
        {
            using (Bitmap image = Scene(new Size(1280, 720), new Point(403, 270), 1f, "Server Closed.(1)", true))
            {
                Rectangle dialog;
                string evidence;
                Assert(VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), evidence);
            }
        }

        private static void OtherMessages()
        {
            foreach (string text in new[] { "Please wait...", "Server Closed.(2)", "Server Closed.(11)", "Server Closed.(0)",
                "Server Closed (1)", "Server Closed.", "Not Server Closed.(1)", "Server Closed.(1) retry later", "Disconnected from Server." })
            foreach (bool soften in new[] { false, true })
            using (Bitmap image = Scene(new Size(1280, 720), new Point(403, 270), 1f, text, soften))
            {
                Rectangle dialog;
                string evidence;
                Assert(!VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), "Unknown message accepted: " + text);
            }
        }

        private static void TightTitle()
        {
            foreach (bool soften in new[] { false, true })
            using (Bitmap image = Scene(new Size(1280, 720), new Point(403, 270), 1f, "Server Closed.(1)", soften, false, "Message", true, 16))
            {
                Rectangle dialog; string evidence;
                var time = System.Diagnostics.Stopwatch.StartNew();
                Assert(VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), evidence);
                Console.WriteLine("Synthetic server-closed recognition: softened={0}; elapsed={1}ms", soften, time.ElapsedMilliseconds);
            }
        }

        private static void RequireForm()
        {
            using (Bitmap image = Scene(new Size(1280, 720), new Point(403, 270), 1f, "Server Closed.(1)", false, false, "Account", true))
            {
                Rectangle dialog; string evidence;
                Assert(!VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), "Other form title accepted.");
            }
            using (Bitmap image = Scene(new Size(1280, 900), new Point(340, 240), 1f, "Server Closed.(1)", false, true, "Message", false))
            {
                Rectangle dialog; string evidence;
                Assert(!VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), "OK in another form completed an incomplete error form.");
            }
        }

        private static void AmbiguousForms()
        {
            using (Bitmap image = Scene(new Size(1280, 900), new Point(180, 180), 1f, "Server Closed.(1)", false))
            {
                using (Graphics graphics = Graphics.FromImage(image)) DrawForm(graphics, new Point(680, 450), 1f, "Message", "Server Closed.(1)", true);
                Rectangle dialog; string evidence;
                Assert(!VanillaServerClosedPattern.TryDetect(image, out dialog, out evidence), "Multiple error forms are ambiguous.");
            }
        }

        private static void ClassifierWiring()
        {
            using (Bitmap image = Scene(new Size(1280, 900), new Point(340, 240), 1f, "Server Closed.(1)", true, true))
                Assert(VanillaVisualProbe.Classify(image) == VanillaVisualState.ServerClosed, "Exact server error lost in visual classification.");
            using (Bitmap image = Scene(new Size(1280, 900), new Point(340, 240), 1f, "Please wait...", true))
                Assert(VanillaVisualProbe.Classify(image) != VanillaVisualState.ServerClosed, "Please wait became server closed in visual classification.");
        }

        // A synthetic rendering of the reported Message/title/body/OK arrangement.
        // It is not the user's actual screenshot and never contains game/account data.
        private static Bitmap Scene(Size size, Point position, float scale, string message, bool soften,
            bool wait = false, string title = "Message", bool ok = true, int titlePixels = 19)
        {
            var image = new Bitmap(size.Width, size.Height);
            using (Graphics graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.FromArgb(235, 246, 233));
                graphics.FillRectangle(Brushes.DarkOliveGreen, 0, 0, 110, 70);
                DrawForm(graphics, position, scale, title, message, ok, titlePixels);
                if (wait) DrawForm(graphics, new Point(position.X + 34, position.Y + 200), scale, "Message", "Please wait...", true);
            }
            if (!soften) { SaveFixture(image); return image; }
            using (image)
            using (var smaller = new Bitmap((int)(image.Width * .8), (int)(image.Height * .8)))
            {
                using (Graphics graphics = Graphics.FromImage(smaller))
                { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(image, new Rectangle(Point.Empty, smaller.Size)); }
                var restored = new Bitmap(size.Width, size.Height);
                using (Graphics graphics = Graphics.FromImage(restored))
                { graphics.InterpolationMode = InterpolationMode.HighQualityBicubic; graphics.DrawImage(smaller, new Rectangle(Point.Empty, restored.Size)); }
                SaveFixture(restored);
                return restored;
            }
        }

        private static void DrawForm(Graphics graphics, Point position, float scale, string title, string text, bool ok, int titlePixels = 19)
        {
            int width = (int)(280 * scale), height = (int)(124 * scale), titleHeight = (int)(titlePixels * scale);
            int x = position.X, y = position.Y;
            using (var titleBrush = new SolidBrush(Color.FromArgb(174, 199, 236)))
            using (var font = new Font("Tahoma", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var border = new Pen(Color.FromArgb(125, 125, 125), Math.Max(1, scale)))
            {
                graphics.FillRectangle(Brushes.White, x, y, width, height);
                graphics.FillRectangle(titleBrush, x + 1, y + 1, width - 2, titleHeight);
                graphics.DrawRectangle(border, x, y, width, height);
                graphics.DrawEllipse(Pens.SteelBlue, x + 3 * scale, y + 5 * scale, 8 * scale, 8 * scale);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(title, font, Brushes.Black, x + 16 * scale, y + 2 * scale);
                graphics.DrawString(text, font, Brushes.Black, x + 10 * scale, y + 30 * scale);
                if (ok)
                {
                    var button = new Rectangle(x + (int)(233 * scale), y + (int)(100 * scale), (int)(39 * scale), (int)(18 * scale));
                    graphics.FillRectangle(Brushes.WhiteSmoke, button);
                    graphics.DrawRectangle(Pens.Gray, button);
                    graphics.DrawString("OK", font, Brushes.Black, button.Left + 10 * scale, button.Top + scale);
                }
            }
        }

        private static void SaveFixture(Bitmap image)
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOURRTOOLS_ISOLATED_DESKTOP"))) return;
            string directory = Path.Combine(Environment.CurrentDirectory, "dist", "recognition");
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, "server-closed-" + (++fixtureNumber).ToString("D2") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
