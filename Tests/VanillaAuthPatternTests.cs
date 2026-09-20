using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaAuthPatternTests
    {
        private static int passed, failed;

        internal static int Run()
        {
            Test("Login pattern distinguishes username and password across resolutions", LoginAcrossResolutions);
            Test("Login pattern tolerates softened rendering", LoginSoftened);
            Test("Login form may move independently from the client dimensions", LoginMoved);
            Test("Three anonymous rectangles do not authorize credential entry", RejectAnonymousFields);
            Test("A central decoy does not hide the recognized moved login form", LoginWithDecoy);
            Test("Two plausible named login forms are rejected", RejectDuplicateLoginForms);
            Test("Server recognition locates Vanilla MMO by name across resolutions", ServerAcrossResolutions);
            Test("Server recognition tolerates softened first-row selection", ServerSoftened);
            Test("Server recognition rejects wrong names, duplicates and missing highlight", ServerNegativeCases);
            Test("Auth/server pattern rejects unrelated blank screens", RejectBlank);
            Console.WriteLine("Auth/server pattern: {0} passed; {1} failed.", passed, failed);
            return failed;
        }

        private static void LoginAcrossResolutions()
        {
            foreach (Size size in new[] { new Size(800, 600), new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440) })
            {
                using (Bitmap bitmap = LoginImage(size.Width, size.Height, false))
                {
                    VanillaLoginLayout layout;
                    string evidence;
                    Assert(VanillaAuthPattern.TryDetectLogin(bitmap, out layout, out evidence), size + " failed: " + evidence);
                    int top = (int)(size.Height * 0.61);
                    int boxHeight = Math.Max(20, (int)(size.Height * 0.016));
                    int step = Math.Max(26, (int)(size.Height * 0.022));
                    Point expectedUser = new Point((int)(size.Width * 0.49), top + step + boxHeight / 2);
                    Point expectedPassword = new Point((int)(size.Width * 0.49), top + 2 * step + boxHeight / 2);
                    Assert(layout.UserName.Contains(expectedUser), "Username safe area missed the second bordered field at " + size + ": " + layout.UserName + "; " + evidence);
                    Assert(layout.Password.Contains(expectedPassword), "Password safe area missed the third bordered field at " + size + ": " + layout.Password + "; " + evidence);
                    Assert(layout.UserName.Bottom <= layout.Password.Top, "Username and password safe areas must not overlap.");

                }
            }
        }

        private static void LoginSoftened()
        {
            using (Bitmap large = LoginImage(1600, 1000, true))
            using (Bitmap reduced = new Bitmap(1000, 625))
            using (Graphics graphics = Graphics.FromImage(reduced))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(large, new Rectangle(0, 0, reduced.Width, reduced.Height));
                VanillaLoginLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectLogin(reduced, out layout, out evidence), "Softened login failed: " + evidence);
                Assert(layout.UserName.Bottom <= layout.Password.Top, "Softened username/password regions overlapped.");
            }
        }

        private static void LoginMoved()
        {
            using (Bitmap image = LoginImage(1280, 720, false, -190, -210))
            {
                VanillaLoginLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectLogin(image, out layout, out evidence), "Moved form was missed: " + evidence);
                Assert(layout.UserNameControl.Contains(new Point((int)(1280 * 0.49) - 190, (int)(720 * 0.61) + 26 + 10 - 210)),
                    "Moved form username bounds were not detected.");
            }
        }

        private static void RejectAnonymousFields()
        {
            using (Bitmap image = LoginImage(1280, 720, false, 0, 0, false))
            {
                VanillaLoginLayout layout;
                string evidence;
                Assert(!VanillaAuthPattern.TryDetectLogin(image, out layout, out evidence), "Anonymous rectangles became a login form.");
            }
        }

        private static void LoginWithDecoy()
        {
            using (Bitmap image = TwoForms(false))
            {
                VanillaLoginLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectLogin(image, out layout, out evidence), "Central decoy hid the named form: " + evidence);
                Assert(layout.UserName.Left < 500 && layout.UserName.Top < 300, "Selected the anonymous central decoy.");
            }
        }

        private static void RejectDuplicateLoginForms()
        {
            using (Bitmap image = TwoForms(true))
            {
                VanillaLoginLayout layout;
                string evidence;
                Assert(!VanillaAuthPattern.TryDetectLogin(image, out layout, out evidence), "Two login forms authorized credentials.");
            }
        }

        private static Bitmap TwoForms(bool secondNamed)
        {
            Bitmap image = LoginImage(1280, 720, false, -190, -210);
            using (Bitmap second = LoginImage(1280, 720, false, 0, 0, secondNamed))
            using (Graphics graphics = Graphics.FromImage(image))
            {
                var region = new Rectangle((int)(1280 * 0.49) - 70, (int)(720 * 0.61) - 3, 140, 80);
                graphics.DrawImage(second, region, region, GraphicsUnit.Pixel);
            }
            return image;
        }

        private static void ServerAcrossResolutions()
        {
            Size[] sizes = { new Size(800, 600), new Size(1280, 720), new Size(1920, 1080), new Size(2560, 1440) };
            float[] scales = { .85f, 1f, 1.5f, 2f };
            for (int index = 0; index < sizes.Length; index++)
            using (Bitmap bitmap = VanillaProxyPatternTests.ServiceImage(sizes[index], scales[index], "Tahoma",
                new[] { "Crowded Vanilla MMO" }, "Crowded Vanilla MMO", false, server: true))
            {
                VanillaServerLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectServerDialog(bitmap, out layout, out evidence), sizes[index] + " failed: " + evidence);
                Assert(layout.ServerName == "Vanilla MMO", "Crowded is status, not the server identity.");
                Assert(layout.IsHighlighted, "The exact named row must be positively highlighted.");
                Assert(layout.Dialog.Contains(layout.ServerRow), "Recognized text bounds escaped the detected form.");
                Assert(layout.ServerRow.Width > 25, "Actual name glyphs need a nonempty safe target.");
            }
        }

        private static void ServerSoftened()
        {
            using (Bitmap bitmap = VanillaProxyPatternTests.ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { "Crowded Vanilla MMO" }, "Crowded Vanilla MMO", true, server: true))
            {
                VanillaServerLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectServerDialog(bitmap, out layout, out evidence), "Softened named server rejected: " + evidence);
                Assert(layout.IsHighlighted, "Softened first-row selected evidence was lost.");
            }
        }

        private static void ServerNegativeCases()
        {
            foreach (string name in new[] { "Crowded", "Other MMO", "Vanilla MMO Test", "Other Vanilla MMO" })
            using (Bitmap bitmap = VanillaProxyPatternTests.ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { name }, name, false, server: true))
            {
                VanillaServerLayout layout;
                string evidence;
                Assert(!VanillaAuthPattern.TryDetectServerDialog(bitmap, out layout, out evidence), "Wrong server accepted: " + name);
            }
            using (Bitmap bitmap = VanillaProxyPatternTests.ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { "Vanilla MMO" }, null, false, server: true))
            {
                VanillaServerLayout layout;
                string evidence;
                Assert(VanillaAuthPattern.TryDetectServerDialog(bitmap, out layout, out evidence), evidence);
                Assert(!layout.IsHighlighted, "Recognized identity alone is not selection proof.");
            }
            using (Bitmap bitmap = VanillaProxyPatternTests.ServiceImage(new Size(1280, 720), 1f, "Tahoma",
                new[] { "Vanilla MMO", "Vanilla MMO" }, "Vanilla MMO", false, server: true))
            {
                VanillaServerLayout layout;
                string evidence;
                Assert(!VanillaAuthPattern.TryDetectServerDialog(bitmap, out layout, out evidence), "Duplicate server identities must fail closed.");
            }
        }
        private static void RejectBlank()
        {
            using (var bitmap = new Bitmap(1280, 720))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.White);
                VanillaLoginLayout login;
                VanillaServerLayout server;
                string evidence;
                Assert(!VanillaAuthPattern.TryDetectLogin(bitmap, out login, out evidence), "Blank screen became a login form.");
                Assert(!VanillaAuthPattern.TryDetectServerDialog(bitmap, out server, out evidence), "Blank screen became a server dialog.");
            }
        }

        private static Bitmap LoginImage(int width, int height, bool softened, int offsetX = 0, int offsetY = 0, bool serviceName = true)
        {
            var bitmap = new Bitmap(width, height);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.FromArgb(248, 251, 252));
                graphics.SmoothingMode = softened ? SmoothingMode.AntiAlias : SmoothingMode.None;
                int controlWidth = Math.Max(130, (int)(width * 0.085));
                int boxHeight = Math.Max(20, (int)(height * 0.016));
                int step = Math.Max(26, (int)(height * 0.022));
                int left = (int)(width * 0.49) - controlWidth / 2 + offsetX;
                int top = (int)(height * 0.61) + offsetY;
                using (var fill = new SolidBrush(Color.FromArgb(247, 247, 247)))
                using (var pen = new Pen(softened ? Color.FromArgb(145, 145, 145) : Color.FromArgb(100, 100, 100), softened ? 2f : 1f))
                using (var ink = new SolidBrush(Color.FromArgb(70, 70, 70)))
                {
                    for (int row = 0; row < 3; row++)
                    {
                        Rectangle box = new Rectangle(left, top + row * step, controlWidth, boxHeight);
                        graphics.FillRectangle(fill, box);
                        graphics.DrawRectangle(pen, box);
                        if (row == 0 && serviceName)
                        {
                            using (var font = new Font("Tahoma", Math.Max(11, boxHeight * 0.62f), FontStyle.Regular, GraphicsUnit.Pixel))
                            using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                graphics.DrawString("Vanilla MMO", font, ink,
                                    new Rectangle(box.Left, box.Top, box.Width - box.Height, box.Height), format);
                            int divider = box.Right - box.Height;
                            graphics.DrawLine(pen, divider, box.Top, divider, box.Bottom);
                            graphics.FillPolygon(ink, new[] { new Point(divider + box.Height / 4, box.Top + box.Height / 3),
                                new Point(box.Right - box.Height / 4, box.Top + box.Height / 3),
                                new Point(divider + box.Height / 2, box.Bottom - box.Height / 3) });
                        }
                        if (row > 0) graphics.FillRectangle(ink, left + 12, box.Top + Math.Max(3, boxHeight / 2), Math.Max(12, controlWidth / 3), 2);
                    }
                }
            }
            return bitmap;
        }

        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
    }
}
