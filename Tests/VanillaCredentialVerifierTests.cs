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
            Test("Remembered credential contents are cleared and visibly empty before append-only typing", ClearedReplacement);
            Test("A full remembered field may clear before its custom caret becomes visible", FullFieldCaretAfterClear);
            Test("Text returning during post-clear focus proof blocks replacement", RecheckEmptyAfterFocus);
            Test("Failed and partial username/password clears withhold all new field text", FailedClearing);
            Test("Cleared fields wait for caret-off frames without extra clicks", CaretAfterClearing);
            Test("STOP and ownership changes during clearing block later typing", ClearingOwnership);
            Test("STOP immediately after empty proof blocks new text", CancelAfterEmptyProof);
            Test("Remembered username selection is collapsed before custom caret proof", SelectedPrefilledUserName);
            Test("Caret preparation without a visible blink never authorizes typing", RevealWithoutBlink);
            Test("STOP after a field click withholds caret preparation", CancelAfterClick);
            Test("STOP after caret preparation withholds all typing", CancelAfterReveal);
            Test("Native focus contradiction after preparation withholds typing", WrongFocusAfterReveal);
            Test("Changed credential controls withhold caret preparation", ChangedClickedControls);
            Test("A sibling custom caret cannot authorize the clicked field", SiblingCaretAfterReveal);
            Test("Wrong field focus blocks all credential typing", WrongFieldFocus);
            Test("Unproven custom field focus blocks all credential typing", UnknownFocus);
            Test("Wrong username blocks password entry", WrongUserName);
            Test("Unmasked or incomplete password blocks submission", WrongMask);
            Test("Manual submit cannot bypass fresh credential verification", SubmitGuard);
            Test("STOP during credentials cancels password and submit", Cancellation);
            Test("Password field is redetected after username entry", MovedForm);
            Test("Field focus proof cannot combine different windows", ChangedSurface);
            Test("Credential content proof cannot combine moving controls", ChangingContentGeometry);
            Test("One-pixel control bottom variation preserves native and custom focus/content proof", ControlBottomVariation);
            Test("Blink evidence requires changing vertical caret inside the intended field", CaretEvidence);
            Test("Empty fields require pale neutral interiors at native and softened scales", EmptyCredentialFields);
            Test("Empty field proof rejects residual glyphs, masks and visible carets", NonemptyCredentialFields);
            Test("Empty field proof rejects unknown backgrounds and invalid regions", UnknownCredentialFields);
            Test("Password mask recognizer accepts known repeated glyphs across UI scales", KnownMasks);
            Test("Password mask recognizer tolerates softened mask rendering", SoftenedMasks);
            Test("Password mask recognizer rejects plaintext, wrong count and empty content", RejectUnknownMasks);
            Test("Visible username OCR requires exact content and case at native and softened scales", VisibleUserName);
            Test("Small username uses accurate-model confidence while preserving exact digit and case checks", SmallUserNameAccurateFallback);
            Test("Low-confidence whole-field username misreading cannot authorize another account", SmallUserNameWrongFallback);
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
            Assert(input.Revealed.Count == 2 && input.Revealed[0] == VanillaCredentialField.UserName
                && input.Revealed[1] == VanillaCredentialField.Password, "Caret preparation did not follow both field clicks.");
        }
        private static void SelectedPrefilledUserName()
        {
            var input = new FakeInput { SelectedPrefilled = true, FocusEvidence = VanillaFieldFocus.Unknown };
            new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
            Assert(input.Typed.Count == 2 && input.Submits == 1, "Selected remembered username never became a verified custom caret.");
            Assert(input.Revealed.Count == 2 && input.CustomCaretFrames >= 6,
                "Entry did not independently prove the revealed custom caret in both fields.");
        }

        private static void ClearedReplacement()
        {
            var input = new FakeInput();
            new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
            Assert(input.UserText == "test-account" && input.PasswordText == "synthetic-secret",
                "Append-only typing retained part of a remembered credential.");
            Assert(string.Join(",", input.EntryEvents) == "clear-UserName,empty-UserName,empty-UserName,type-UserName,clear-Password,empty-Password,empty-Password,type-Password",
                "Credential text was typed before its field had independently cleared.");
            Assert(input.Clears == 2 && input.Submits == 1, "Credential replacement did not clear each field once.");
        }

        private static void FullFieldCaretAfterClear()
        {
            var input = new FakeInput
            {
                UserText = new string('x', 34),
                PasswordText = new string('y', 24),
                SelectedPrefilled = true,
                HideCaretUntilClear = true,
                FocusEvidence = VanillaFieldFocus.Unknown,
                ToggleControlBottomEachCapture = true
            };
            new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
            Assert(input.Clears == 2 && input.Typed.Count == 2 && input.Submits == 1,
                "A full remembered field required an invisible pre-clear caret.");
            Assert(input.CustomCaretFrames >= 6 && input.EmptyProofs == 4,
                "Clearing bypassed custom focus proof or the fresh empty recheck before typing.");
            Assert(input.UserText == "test-account" && input.PasswordText == "synthetic-secret",
                "Remembered text remained after clearing the full fields.");
        }

        private static void RecheckEmptyAfterFocus()
        {
            var input = new FakeInput { RestoreContentsAfterEmpty = true };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true));
            Assert(input.Clears == 1 && input.EmptyProofs == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "The first empty frame authorized typing after content returned during focus verification.");
        }

        private static void FailedClearing()
        {
            foreach (int failedClear in new[] { 1, 2 })
            foreach (bool partial in new[] { false, true })
            {
                var input = new FakeInput { FailedClearNumber = failedClear, PartialClear = partial };
                Fails(() => new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true));
                Assert(input.Typed.Count == failedClear - 1 && input.Submits == 0,
                    "A failed or partial clear authorized new credential text.");
                Assert(input.EmptyChecks >= 20, "A retained field did not exercise the bounded empty-field wait.");
            }
        }

        private static void CaretAfterClearing()
        {
            var input = new FakeInput { CaretFramesAfterClear = 3 };
            new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
            Assert(input.PostClearCaretCaptures == 6 && input.EmptyChecks >= 8,
                "A visible post-clear caret was mistaken for an empty field.");
            Assert(input.Clicks.Count == 2 && input.Clears == 2 && input.Submits == 1,
                "Waiting for caret-off repeatedly clicked or cleared a field.");
        }

        private static void ClearingOwnership()
        {
            foreach (int clearNumber in new[] { 1, 2 })
            foreach (string change in new[] { "cancel", "position", "surface", "focus", "form", "size" })
            foreach (bool afterEmpty in new[] { false, true })
            {
                var input = new FakeInput
                {
                    ChangeOnClearNumber = clearNumber, ChangeAfterClear = change, ChangeAfterFirstEmpty = afterEmpty
                };
                bool rejected = false;
                try { new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true); }
                catch (InvalidOperationException) { rejected = true; }
                catch (OperationCanceledException) { rejected = true; }
                Assert(rejected && input.Typed.Count == clearNumber - 1 && input.Submits == 0,
                    "Clearing continued after " + change + " changed for field " + clearNumber + ".");
            }
        }

        private static void CancelAfterEmptyProof()
        {
            var input = new FakeInput { CancelWhenEmpty = true };
            bool cancelled = false;
            try { new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true); }
            catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && input.EmptyProofs == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "STOP between empty proof and typing sent credential text.");
        }
        private static void RevealWithoutBlink()
        {
            var input = new FakeInput { SelectedPrefilled = true, SuppressCaret = true, FocusEvidence = VanillaFieldFocus.Unknown };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Revealed.Count == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "Home alone authorized credential typing without focus evidence.");
            Assert(input.Clears == 1 && input.EmptyProofs == 1,
                "The missing caret blocked safe clearing instead of blocking new credential text.");
        }
        private static void CancelAfterClick()
        {
            var input = new FakeInput { CancelWhenClicked = true };
            try { new VanillaCredentialVerifier(input).Fill("test", "secret", true); }
            catch (OperationCanceledException) { }
            Assert(input.Clicks.Count == 1 && input.Revealed.Count == 0 && input.Typed.Count == 0,
                "STOP after click permitted caret preparation or typing.");
        }
        private static void CancelAfterReveal()
        {
            var input = new FakeInput { CancelWhenRevealed = true };
            try { new VanillaCredentialVerifier(input).Fill("test", "secret", true); }
            catch (OperationCanceledException) { }
            Assert(input.Revealed.Count == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "STOP after Home permitted credential typing.");
        }
        private static void WrongFocusAfterReveal()
        {
            var input = new FakeInput { ContradictAfterReveal = true };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Revealed.Count == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "Caret preparation bypassed a later native focus contradiction.");
        }
        private static void ChangedClickedControls()
        {
            var input = new FakeInput { MoveAfterClick = true };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Revealed.Count == 0 && input.Typed.Count == 0,
                "Changed controls received Home from an earlier field click.");
        }
        private static void SiblingCaretAfterReveal()
        {
            var input = new FakeInput { SelectedPrefilled = true, SiblingCaret = true, FocusEvidence = VanillaFieldFocus.Unknown };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Revealed.Count == 1 && input.Typed.Count == 0 && input.Submits == 0,
                "Password-field caret authorized username entry.");
        }
        private static void WrongFieldFocus()
        {
            var input = new FakeInput { FocusEvidence = VanillaFieldFocus.Contradicted };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Revealed.Count == 0 && input.Typed.Count == 0 && input.Submits == 0,
                "Home or credentials sent despite contradictory field focus.");
        }
        private static void UnknownFocus()
        {
            var input = new FakeInput { FocusEvidence = VanillaFieldFocus.Unknown };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 0 && input.Submits == 0, "Unproven focus authorized credentials.");
            Assert(input.Clears == 1 && input.EmptyProofs == 1,
                "Unknown focus did not remain a typing guard after the observed clear.");
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
                input.UserText = "test";
                input.PasswordText = "secret";
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

        private static void ChangedSurface()
        {
            var input = new FakeInput { ChangeSurfaceEachCapture = true };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Typed.Count == 0 && input.Submits == 0, "Field focus proof crossed a window replacement.");
        }

        private static void ChangingContentGeometry()
        {
            var input = new FakeInput { MoveAfterMaskCapture = true };
            Fails(() => new VanillaCredentialVerifier(input).Fill("test", "secret", true));
            Assert(input.Submits == 0, "Credential confirmations combined shifting controls.");
        }

        private static void ControlBottomVariation()
        {
            foreach (VanillaFieldFocus focus in new[] { VanillaFieldFocus.Confirmed, VanillaFieldFocus.Unknown })
            {
                var input = new FakeInput
                {
                    ToggleControlBottomEachCapture = true,
                    SelectedPrefilled = true,
                    FocusEvidence = focus
                };
                new VanillaCredentialVerifier(input).Fill("test-account", "synthetic-secret", true);
                Assert(input.Typed.Count == 2 && input.Submits == 1,
                    "Harmless bottom-edge variation interrupted verified credential entry with " + focus + " focus.");
                Assert(input.NameChecks >= 4 && input.MaskChecks >= 4,
                    "Bottom-edge tolerance bypassed repeated credential content checks.");
            }
        }
        private static void EmptyCredentialFields()
        {
            foreach (Color background in new[] { Color.White, Color.FromArgb(245, 245, 245), Color.FromArgb(225, 228, 225) })
            {
                Rectangle field;
                using (Bitmap image = EmptyFieldFixture(background, "", out field))
                {
                    Assert(VanillaCredentialPattern.IsEmptyField(image, field), "Pale empty field was rejected.");
                    foreach (double scale in new[] { .625, .75, .875 })
                    {
                        Rectangle reducedField;
                        using (Bitmap reduced = Resample(image, field, scale, out reducedField))
                            Assert(VanillaCredentialPattern.IsEmptyField(reduced, reducedField), "Softened pale blank rejected at " + scale);
                    }
                    using (Graphics graphics = Graphics.FromImage(image))
                    using (var border = new Pen(Color.FromArgb(120, 120, 120)))
                        graphics.DrawRectangle(border, field.X, field.Y, field.Width - 1, field.Height - 1);
                    Assert(VanillaCredentialPattern.IsEmptyField(image, field), "The detected control's one-pixel bevel became content.");
                }
            }
        }

        private static void NonemptyCredentialFields()
        {
            foreach (string value in new[] { "l", "i", ".", "*", "\u2022", "sample08" })
            {
                Rectangle field;
                using (Bitmap image = EmptyFieldFixture(Color.White, value, out field))
                {
                    Assert(!VanillaCredentialPattern.IsEmptyField(image, field), "Remaining glyph accepted as empty: " + value);
                    foreach (double scale in new[] { .625, .75, .875 })
                    {
                        Rectangle reducedField;
                        using (Bitmap reduced = Resample(image, field, scale, out reducedField))
                            Assert(!VanillaCredentialPattern.IsEmptyField(reduced, reducedField), "Softened residual glyph accepted at " + scale);
                    }
                }
            }
            foreach (int offset in new[] { 1, 4, 76, 152 })
            foreach (Color ink in new[] { Color.Black, Color.FromArgb(224, 224, 224) })
            {
                Rectangle field;
                using (Bitmap image = EmptyFieldFixture(Color.White, "", out field))
                {
                    using (Graphics graphics = Graphics.FromImage(image))
                    using (var brush = new SolidBrush(ink))
                        graphics.FillRectangle(brush, field.Left + offset, field.Top + 3, 1, 11);
                    Assert(!VanillaCredentialPattern.IsEmptyField(image, field), "Visible caret or faint lone stroke accepted as empty.");
                }
            }
        }

        private static void UnknownCredentialFields()
        {
            foreach (Color background in new[] { Color.Black, Color.Gray, Color.FromArgb(200, 200, 200), Color.SteelBlue,
                Color.FromArgb(220, 235, 255), Color.FromArgb(0, 255, 255, 255) })
            {
                Rectangle field;
                using (Bitmap image = EmptyFieldFixture(background, "", out field))
                    Assert(!VanillaCredentialPattern.IsEmptyField(image, field), "Unknown or selected field background became empty proof.");
            }
            Rectangle valid;
            using (Bitmap image = EmptyFieldFixture(Color.White, "", out valid))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                    graphics.FillRectangle(Brushes.SteelBlue, valid.Left + 3, valid.Top + 2, 24, valid.Height - 4);
                Assert(!VanillaCredentialPattern.IsEmptyField(image, valid), "Partial text selection became empty proof.");
                foreach (Rectangle invalid in new[] { Rectangle.Empty, new Rectangle(-1, 0, 30, 20), new Rectangle(0, -1, 30, 20),
                    new Rectangle(0, 0, 3, 20), new Rectangle(0, 0, 30, 4), new Rectangle(190, 0, 30, 20),
                    new Rectangle(int.MaxValue, 0, 30, 20), new Rectangle(0, 0, -1, 20) })
                    Assert(!VanillaCredentialPattern.IsEmptyField(image, invalid), "Invalid field region was accepted.");
                Assert(!VanillaCredentialPattern.IsEmptyField(null, valid), "Missing capture became empty proof.");
            }
        }

        private static Bitmap EmptyFieldFixture(Color background, string value, out Rectangle field)
        {
            field = new Rectangle(10, 10, 154, 17);
            var image = new Bitmap(200, 70);
            using (Graphics graphics = Graphics.FromImage(image))
            using (var font = new Font("Tahoma", 12, FontStyle.Regular, GraphicsUnit.Pixel))
            {
                graphics.Clear(background);
                graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                graphics.DrawString(value, font, Brushes.Black, new PointF(field.Left + 3, field.Top + 1), StringFormat.GenericTypographic);
            }
            return image;
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

        private static void SmallUserNameAccurateFallback()
        {
            Rectangle field;
            using (Bitmap image = SmallUserNameFixture("Tahoma", "sample07", false, out field))
            {
                VanillaTextLine[] fast, accurate;
                string evidence;
                Rectangle text = Rectangle.Inflate(field, -1, -1);
                Assert(VanillaTextRecognition.TryRead(image, text, true, out fast, out evidence)
                    && fast.Length == 1 && fast[0].Confidence < 80,
                    "The small-font fixture no longer exercises accurate OCR fallback.");
                Assert(VanillaTextRecognition.TryReadAccurate(image, text, true, out accurate, out evidence)
                    && accurate.Length == 1 && accurate[0].Text.Trim() == "sample07"
                    && accurate[0].Confidence >= 65 && accurate[0].Confidence < 80,
                    "The accurate small-font fixture did not reproduce its model-specific confidence range.");
                Assert(VanillaCredentialInput.VerifyVisibleUserName(image, field, "sample07"),
                    "Accurate whole-field recognition below 80 rejected the exact small username.");
                foreach (string wrong in new[] { "sample08", "Sample07", "sampleO7" })
                    Assert(!VanillaCredentialInput.VerifyVisibleUserName(image, field, wrong),
                        "The accurate fallback accepted a different username digit or case.");
            }
        }

        private static void SmallUserNameWrongFallback()
        {
            Rectangle field;
            using (Bitmap image = SmallUserNameFixture("Arial", "sample08", true, out field))
            {
                VanillaTextLine[] accurate;
                string evidence;
                Assert(VanillaTextRecognition.TryReadAccurate(image, Rectangle.Inflate(field, -1, -1), true, out accurate, out evidence)
                    && accurate.Length == 1 && accurate[0].Text.Trim() == "sample(8" && accurate[0].Confidence < 65,
                    "The softened whole-field fixture no longer reproduces the bounded wrong-name observation.");
                Assert(!VanillaCredentialInput.VerifyVisibleUserName(image, field, "sample(8"),
                    "A low-confidence OCR misreading was accepted as a different configured account.");
                Assert(!VanillaCredentialInput.VerifyVisibleUserName(image, field, "sample08"),
                    "The configured username overrode ambiguous observed pixels.");
            }
        }

        private static Bitmap SmallUserNameFixture(string family, string value, bool soften, out Rectangle field)
        {
            field = new Rectangle(10, 10, 154, 17);
            using (var image = new Bitmap(174, 37))
            {
                using (Graphics graphics = Graphics.FromImage(image))
                using (var font = new Font(family, 12, FontStyle.Regular, GraphicsUnit.Pixel))
                {
                    graphics.Clear(Color.White);
                    graphics.TextRenderingHint = TextRenderingHint.SingleBitPerPixelGridFit;
                    graphics.DrawString(value, font, Brushes.Black, new PointF(field.Left + 3, field.Top + 1), StringFormat.GenericTypographic);
                }
                if (!soften) return (Bitmap)image.Clone();
                using (var reduced = new Bitmap((int)(image.Width * .875), (int)(image.Height * .875)))
                {
                    using (Graphics graphics = Graphics.FromImage(reduced))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, new Rectangle(Point.Empty, reduced.Size));
                    }
                    var result = new Bitmap(image.Width, image.Height);
                    using (Graphics graphics = Graphics.FromImage(result))
                    {
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(reduced, new Rectangle(Point.Empty, result.Size));
                    }
                    return result;
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
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FOURRTOOLS_ISOLATED_DESKTOP"))) return;
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
            internal bool ChangeSurfaceEachCapture, MoveAfterMaskCapture;
            internal bool ToggleControlBottomEachCapture;
            internal bool SelectedPrefilled, SuppressCaret, SiblingCaret, MoveAfterClick;
            internal bool CancelWhenClicked, CancelWhenRevealed, ContradictAfterReveal;
            internal bool PartialClear, CancelWhenEmpty;
            internal bool HideCaretUntilClear, RestoreContentsAfterEmpty, ChangeAfterFirstEmpty;
            internal int FailedClearNumber, CaretFramesAfterClear, ChangeOnClearNumber;
            internal string ChangeAfterClear;
            internal string UserText = "remembered-account", PasswordText = "old-masked-value";
            internal VanillaFieldFocus FocusEvidence = VanillaFieldFocus.Confirmed;
            internal readonly List<string> Typed = new List<string>();
            internal readonly List<Rectangle> Clicks = new List<Rectangle>();
            internal readonly List<VanillaCredentialField> Revealed = new List<VanillaCredentialField>();
            internal readonly List<string> EntryEvents = new List<string>();
            internal int Submits, NameChecks, MaskChecks, CustomCaretFrames;
            internal int Clears, EmptyChecks, EmptyProofs, PostClearCaretCaptures;
            private int captures;
            private int pendingCaretFrames;
            private int currentEmptyProofs;
            private readonly HashSet<VanillaCredentialField> clearedFields = new HashSet<VanillaCredentialField>();
            private VanillaCredentialField currentField;
            private bool caretRevealed;
            private bool ClearChanged(string change)
            {
                return ChangeOnClearNumber > 0 && Clears >= ChangeOnClearNumber && ChangeAfterClear == change
                    && (!ChangeAfterFirstEmpty || currentEmptyProofs > 0);
            }
            private int ControlLeft
            {
                get
                {
                    int x = MoveAfterFirstEntry && Typed.Count > 0 ? 40 : 10;
                    if (MoveAfterClick && Clicks.Count > 0) x++;
                    if (MoveAfterMaskCapture && Typed.Count >= 2) x += captures % 2;
                    if (ClearChanged("position")) x++;
                    return x;
                }
            }
            public long SurfaceId { get { return ChangeSurfaceEachCapture ? captures : ClearChanged("surface") ? 2 : 1; } }
            public Bitmap Capture()
            {
                captures++;
                if (RestoreContentsAfterEmpty && EmptyProofs > 0 && Typed.Count == 0) UserText = "l";
                var image = new Bitmap(ClearChanged("size") ? 241 : 240, 120);
                using (Graphics graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.White);
                    DrawFieldContents(graphics, UserText, ControlLeft, 10);
                    DrawFieldContents(graphics, PasswordText, ControlLeft, 40);
                    if (SelectedPrefilled && !caretRevealed)
                        graphics.FillRectangle(Brushes.SteelBlue, 20, 14, 55, 12);
                    if (SelectedPrefilled && caretRevealed && !SuppressCaret
                        && (!HideCaretUntilClear || clearedFields.Contains(currentField)))
                    {
                        CustomCaretFrames++;
                        bool user = currentField == VanillaCredentialField.UserName;
                        if (SiblingCaret) user = !user;
                        if (captures % 2 == 0) graphics.FillRectangle(Brushes.Black, 75, user ? 14 : 44, 1, 12);
                    }
                    if (pendingCaretFrames > 0)
                    {
                        pendingCaretFrames--;
                        PostClearCaretCaptures++;
                        graphics.FillRectangle(Brushes.Black, ControlLeft + 4,
                            currentField == VanillaCredentialField.UserName ? 14 : 44, 1, 12);
                    }
                }
                return image;
            }
            private static void DrawFieldContents(Graphics graphics, string contents, int left, int top)
            {
                for (int index = 0; index < Math.Min(contents.Length, 28); index++)
                    graphics.FillRectangle(Brushes.Black, left + 6 + index * 4, top + 5, 2, 7);
            }
            public bool Detect(Bitmap image, out VanillaLoginLayout layout)
            {
                int x = ControlLeft;
                int height = 20 + (ToggleControlBottomEachCapture ? captures % 2 : 0);
                layout = new VanillaLoginLayout
                {
                    UserName = new Rectangle(x, 10, 150, height), Password = new Rectangle(x, 40, 150, height),
                    UserNameControl = new Rectangle(x, 10, 150, height), PasswordControl = new Rectangle(x, 40, 150, height)
                };
                return FormVisible && !ClearChanged("form");
            }
            public VanillaFieldFocus Focus(VanillaLoginLayout layout, VanillaCredentialField field)
            {
                return (ContradictAfterReveal && Revealed.Count > 0) || ClearChanged("focus")
                    ? VanillaFieldFocus.Contradicted : FocusEvidence;
            }
            public bool VerifyUserName(Bitmap image, VanillaLoginLayout layout, string expected)
            {
                NameChecks++;
                return NameMatches && UserText == expected;
            }
            public bool VerifyPasswordMask(Bitmap image, VanillaLoginLayout layout, int expectedLength)
            {
                MaskChecks++;
                return MaskMatches && PasswordText.Length == expectedLength;
            }
            public bool IsEmpty(Bitmap image, VanillaLoginLayout layout, VanillaCredentialField field)
            {
                EmptyChecks++;
                bool empty = VanillaCredentialPattern.IsEmptyField(image,
                    field == VanillaCredentialField.UserName ? layout.UserNameControl : layout.PasswordControl);
                if (empty)
                {
                    EmptyProofs++;
                    currentEmptyProofs++;
                    EntryEvents.Add("empty-" + field);
                }
                return empty;
            }
            public void Click(Rectangle field, Size imageSize)
            {
                Clicks.Add(field);
                currentField = field.Top < 30 ? VanillaCredentialField.UserName : VanillaCredentialField.Password;
                caretRevealed = false;
            }
            public void RevealCaret(VanillaLoginLayout layout, VanillaCredentialField field)
            {
                Revealed.Add(field);
                caretRevealed = true;
            }
            public void Clear()
            {
                Clears++;
                currentEmptyProofs = 0;
                EntryEvents.Add("clear-" + currentField);
                if (Clears != FailedClearNumber || PartialClear)
                {
                    string remainder = Clears == FailedClearNumber ? "l" : "";
                    if (currentField == VanillaCredentialField.UserName) UserText = remainder;
                    else PasswordText = remainder;
                    if (remainder.Length == 0) clearedFields.Add(currentField);
                }
                pendingCaretFrames = CaretFramesAfterClear;
            }
            public void Type(string value)
            {
                Typed.Add(value);
                EntryEvents.Add("type-" + currentField);
                // A custom edit appends input; it does not magically replace its
                // previous contents when Ctrl+A is unsupported or clearing fails.
                if (currentField == VanillaCredentialField.UserName) UserText += value;
                else PasswordText += value;
            }
            public void Submit() { Submits++; }
            public void Pause(int milliseconds) { CheckCancelled(); }
            public void CheckCancelled()
            {
                if (CancelAfterFirstEntry && Typed.Count > 0) throw new OperationCanceledException();
                if (CancelWhenClicked && Clicks.Count > 0) throw new OperationCanceledException();
                if (CancelWhenRevealed && Revealed.Count > 0) throw new OperationCanceledException();
                if (ClearChanged("cancel") || (CancelWhenEmpty && EmptyProofs > 0)) throw new OperationCanceledException();
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
