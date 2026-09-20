using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaCredentialVerifierTests
    {
        private static int passed, failed, fixtureNumber;
        internal static int Run()
        {
            Test("Credential entry requires field focus, exact username and masks before submission", VerifiedFlow);
            Test("Wrong field focus blocks all credential typing", WrongFieldFocus);
            Test("Unproven custom field focus blocks all credential typing", UnknownFocus);
            Test("Wrong username blocks password entry", WrongUserName);
            Test("Unmasked or incomplete password blocks submission", WrongMask);
            Test("Manual submit cannot bypass fresh credential verification", SubmitGuard);
            Test("STOP during credentials cancels password and submit", Cancellation);
            Test("Password field is redetected after username entry", MovedForm);
            Test("Blink evidence requires changing vertical caret inside the intended field", CaretEvidence);
            Test("Password mask recognizer accepts known repeated glyphs across UI scales", KnownMasks);
            Test("Password mask recognizer tolerates softened mask rendering", SoftenedMasks);
            Test("Password mask recognizer rejects plaintext, wrong count and empty content", RejectUnknownMasks);
            Test("Visible username OCR requires exact content and case at native and softened scales", VisibleUserName);
            Console.WriteLine("Credential verification: {0} passed; {1} failed.", passed, failed);
            return failed;
        }

        private static void VerifiedFlow()
        {
            var input = new FakeInput();
            new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
            Assert(input.Typed.Count == 2 && input.Submits == 1, "Verified flow did not complete once.");
            Assert(input.Clicks.Count == 2 && input.NameChecks >= 4 && input.MaskChecks >= 4,
                "Missing repeated exact-content verification.");
        }
        private static void WrongFieldFocus()
        {
            var input = new FakeInput { FocusEvidence = VanillaFieldFocus.Contradicted };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 0 && input.Submits == 0, "Credentials typed into the wrong field.");
        }
        private static void UnknownFocus()
        {
            var input = new FakeInput { FocusEvidence = VanillaFieldFocus.Unknown };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 0 && input.Submits == 0, "Unproven focus authorized credentials.");
        }
        private static void WrongUserName()
        {
            var input = new FakeInput { NameMatches = false };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 1 && input.Submits == 0, "Unverified username authorized password typing.");
        }
        private static void WrongMask()
        {
            var input = new FakeInput { MaskMatches = false };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 2 && input.Submits == 0, "Wrong mask authorized submission.");
        }
        private static void SubmitGuard()
        {
            foreach (var input in new[] {
                new FakeInput { NameMatches = false }, new FakeInput { MaskMatches = false },
                new FakeInput { FocusEvidence = VanillaFieldFocus.Contradicted }, new FakeInput { FormVisible = false } })
            {
                Fails(() => new VanillaCredentialVerifier(input).Submit("test", 6));
                Assert(input.Typed.Count == 0 && input.Submits == 0, "Manual submit bypassed verification.");
            }
        }
        private static void Cancellation()
        {
            var input = new FakeInput { CancelAfterFirstEntry = true };
            try { new VanillaCredentialVerifier(input).Fill("test", "secret", true); }
            catch (OperationCanceledException) { }
            Assert(input.Typed.Count == 1 && input.Submits == 0, "STOP did not cancel remaining input.");
        }
        private static void MovedForm()
        {
            var input = new FakeInput { MoveAfterFirstEntry = true };
            new VanillaCredentialVerifier(input).Fill("test", "secret", false);
            Assert(input.Clicks.Count == 2 && input.Clicks[1].X == 40, "Password click reused stale initial geometry.");
        }
        private static void CaretEvidence()
        {
            Rectangle field = new Rectangle(10, 10, 150, 22);
            using (var first = new Bitmap(200, 100))
            using (var second = new Bitmap(200, 100))
            using (Graphics a = Graphics.FromImage(first))
            using (Graphics b = Graphics.FromImage(second))
            {
                a.Clear(Color.White); b.Clear(Color.White);
                Rectangle caret;
                Assert(!VanillaCredentialPattern.TryDetectCaretBlink(first, second, field, out caret), "Static field became focus proof.");
                b.FillRectangle(Brushes.Black, 60, 14, 1, 13);
                Assert(VanillaCredentialPattern.TryDetectCaretBlink(first, second, field, out caret), "Blinking insertion caret was missed.");
                Assert(caret == new Rectangle(60, 14, 1, 13), "Wrong detected caret bounds.");
                Assert(!VanillaCredentialPattern.TryDetectCaretBlink(first, second, new Rectangle(10, 40, 150, 22), out caret),
                    "Sibling field caret authorized the wrong field.");
                b.FillRectangle(Brushes.Black, 90, 14, 14, 13);
                Assert(!VanillaCredentialPattern.TryDetectCaretBlink(first, second, field, out caret), "Unrelated text changes became caret proof.");
            }
        }
        private static void KnownMasks()
        {
            foreach (int fontSize in new[] { 11, 13, 18, 24 })
            foreach (string mask in new[] { "********", "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022" })
            {
                Rectangle field;
                using (Bitmap image = MaskImage(mask, fontSize, out field))
                {
                    string evidence;
                    Assert(VanillaCredentialPattern.VerifyPasswordMask(image, field, 8, out evidence),
                        "Known mask rejected at font size " + fontSize + ": " + evidence);
                }
            }
        }
        private static void SoftenedMasks()
        {
            foreach (double scale in new[] { .625, .75, .875 })
            foreach (string mask in new[] { "********", "\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022" })
            {
                Rectangle field, reducedField;
                using (Bitmap large = MaskImage(mask, 18, out field))
                using (Bitmap small = Resample(large, field, scale, out reducedField))
                {
                    SaveFixture(small, "mask-softened");
                    string evidence;
                    Assert(VanillaCredentialPattern.VerifyPasswordMask(small, reducedField, 8, out evidence),
                        "Softened masks were rejected at " + scale + ": " + evidence);
                }
            }
        }
        private static void RejectUnknownMasks()
        {
            foreach (string text in new[] { "", "secret12", "aaaaaaaa", "00000000", "xxxxxxxx", "*********", "*******" })
            {
                Rectangle field;
                using (Bitmap image = MaskImage(text, 18, out field))
                {
                    Assert(!VanillaCredentialPattern.VerifyPasswordMask(image, field, 8), "Unknown/unmasked/wrong-length content accepted.");
                    foreach (double scale in new[] { .625, .75, .875 })
                    {
                        Rectangle reducedField;
                        using (Bitmap reduced = Resample(image, field, scale, out reducedField))
                            Assert(!VanillaCredentialPattern.VerifyPasswordMask(reduced, reducedField, 8),
                                "Softened unknown/unmasked/wrong-length content accepted at " + scale);
                    }
                }
            }
        }

        private static void VisibleUserName()
        {
            const string expected = "sampleuser42";
            foreach (int fontSize in new[] { 12, 16, 22 })
            {
                Rectangle field = new Rectangle(10, 10, 300, fontSize * 2);
                using (var image = new Bitmap(340, 90))
                {
                    using (Graphics graphics = Graphics.FromImage(image))
                    using (var font = new Font("Tahoma", fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
                    {
                        graphics.Clear(Color.White);
                        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                        graphics.DrawString(expected, font, Brushes.Black, new PointF(field.Left + 12, field.Top + 3), StringFormat.GenericTypographic);
                    }
                    SaveFixture(image, "username");
                    Assert(VanillaCredentialInput.VerifyVisibleUserName(image, field, expected), "Exact visible username rejected at size " + fontSize);
                    Assert(!VanillaCredentialInput.VerifyVisibleUserName(image, field, "sampleuser43"), "A different username was accepted.");
                    Assert(!VanillaCredentialInput.VerifyVisibleUserName(image, field, "Sampleuser42"), "Username case mismatch was accepted.");
                    Rectangle reducedField;
                    using (Bitmap reduced = Resample(image, field, .875, out reducedField))
                    {
                        SaveFixture(reduced, "username-softened");
                        Assert(VanillaCredentialInput.VerifyVisibleUserName(reduced, reducedField, expected), "Softened exact username rejected at size " + fontSize);
                    }
                }
            }
        }

        private static Bitmap Resample(Bitmap source, Rectangle field, double scale, out Rectangle reducedField)
        {
            var result = new Bitmap((int)(source.Width * scale), (int)(source.Height * scale));
            using (Graphics graphics = Graphics.FromImage(result))
            {
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(source, new Rectangle(Point.Empty, result.Size));
            }
            reducedField = Rectangle.FromLTRB((int)(field.Left * scale), (int)(field.Top * scale),
                (int)(field.Right * scale), (int)(field.Bottom * scale));
            return result;
        }

        private static void SaveFixture(Bitmap image, string name)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase)) return;
            string directory = Path.Combine(Environment.CurrentDirectory, "dist", "recognition");
            Directory.CreateDirectory(directory);
            image.Save(Path.Combine(directory, name + "-" + (++fixtureNumber).ToString("D2") + ".png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        private static Bitmap MaskImage(string mask, int fontSize, out Rectangle field)
        {
            field = new Rectangle(10, 10, 300, fontSize * 2);
            var image = new Bitmap(340, 90);
            using (Graphics graphics = Graphics.FromImage(image))
            using (var font = new Font("Tahoma", fontSize, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(Color.White);
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.DrawString(mask, font, Brushes.Black, new PointF(field.Left + 12, field.Top + 3), StringFormat.GenericTypographic);
            }
            return image;
        }

        private sealed class FakeInput : IVanillaCredentialInput
        {
            internal bool NameMatches = true, MaskMatches = true, FormVisible = true;
            internal bool CancelAfterFirstEntry, MoveAfterFirstEntry;
            internal VanillaFieldFocus FocusEvidence = VanillaFieldFocus.Confirmed;
            internal readonly List<string> Typed = new List<string>();
            internal readonly List<Rectangle> Clicks = new List<Rectangle>();
            internal int Submits, NameChecks, MaskChecks;
            public Bitmap Capture()
            {
                var image = new Bitmap(240, 120);
                using (Graphics graphics = Graphics.FromImage(image)) graphics.Clear(Color.White);
                return image;
            }
            public bool Detect(Bitmap image, out VanillaLoginLayout layout)
            {
                int x = MoveAfterFirstEntry && Typed.Count > 0 ? 40 : 10;
                layout = new VanillaLoginLayout
                {
                    UserName = new Rectangle(x, 10, 150, 20), Password = new Rectangle(x, 40, 150, 20),
                    UserNameControl = new Rectangle(x, 10, 150, 20), PasswordControl = new Rectangle(x, 40, 150, 20)
                };
                return FormVisible;
            }
            public VanillaFieldFocus Focus(VanillaLoginLayout layout, VanillaCredentialField field) { return FocusEvidence; }
            public bool VerifyUserName(Bitmap image, VanillaLoginLayout layout, string expected) { NameChecks++; return NameMatches; }
            public bool VerifyPasswordMask(Bitmap image, VanillaLoginLayout layout, int expectedLength) { MaskChecks++; return MaskMatches; }
            public void Click(Rectangle field, Size imageSize) { Clicks.Add(field); }
            public void Replace(string value) { Typed.Add(value); }
            public void Submit() { Submits++; }
            public void Pause(int milliseconds) { CheckCancelled(); }
            public void CheckCancelled()
            {
                if (CancelAfterFirstEntry && Typed.Count > 0) throw new OperationCanceledException();
            }
        }

        private static void Fails(Action action)
        {
            try { action(); }
            catch (InvalidOperationException) { return; }
            throw new Exception("Expected a fail-closed credential guard.");
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
