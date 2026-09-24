using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal enum VanillaCredentialField { UserName, Password }
    internal enum VanillaFieldFocus { Unknown, Confirmed, Contradicted }

    internal interface IVanillaCredentialInput
    {
        long SurfaceId { get; }
        Bitmap Capture();
        bool Detect(Bitmap image, out VanillaLoginLayout layout);
        VanillaFieldFocus Focus(VanillaLoginLayout layout, VanillaCredentialField field);
        bool VerifyUserName(Bitmap image, VanillaLoginLayout layout, string expected);
        bool VerifyPasswordMask(Bitmap image, VanillaLoginLayout layout, int expectedLength);
        void Click(Rectangle field, Size imageSize);
        void RevealCaret(VanillaLoginLayout layout, VanillaCredentialField field);
        void Replace(string value);
        void Submit();
        void Pause(int milliseconds);
        void CheckCancelled();
    }

    /// <summary>
    /// Shared by startup, recovery and TESTS. No credential frame is ever written to disk.
    /// A detected rectangle alone cannot authorize typing or submitting credentials.
    /// </summary>
    internal sealed class VanillaCredentialVerifier
    {
        private readonly IVanillaCredentialInput input;

        internal VanillaCredentialVerifier(IVanillaCredentialInput input)
        {
            this.input = input ?? throw new ArgumentNullException(nameof(input));
        }

        internal void Fill(string userName, string password, bool submit)
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(password))
                throw new InvalidOperationException("Username/password is missing.");

            ClickFreshField(VanillaCredentialField.UserName);
            ConfirmFieldFocus(VanillaCredentialField.UserName);
            input.CheckCancelled();
            input.Replace(userName);

            // Re-detect after entry; never reuse a password point from the first screenshot.
            ClickFreshField(VanillaCredentialField.Password);
            ConfirmFieldFocus(VanillaCredentialField.Password);
            // Defocus the username before OCR so its insertion caret cannot become text.
            VerifyContents(userName, null);
            input.CheckCancelled();
            input.Replace(password);
            VerifyContents(userName, password.Length);
            if (submit) Submit(userName, password.Length);
        }

        internal void Submit(string userName, int passwordLength)
        {
            if (string.IsNullOrWhiteSpace(userName) || passwordLength <= 0)
                throw new InvalidOperationException("Username/password is missing; login was not submitted.");
            ConfirmFieldFocus(VanillaCredentialField.Password);
            VerifyContents(userName, passwordLength);
            input.CheckCancelled();
            input.Submit();
        }

        private void ClickFreshField(VanillaCredentialField field)
        {
            // Proxy submission can leave the native login controls repainting for
            // several seconds on RDP. Wait up to about 15 seconds, but never type
            // until the named form and both credential controls are recognized.
            for (int attempt = 0; attempt < 75; attempt++)
            {
                input.CheckCancelled();
                using (Bitmap image = input.Capture())
                {
                    VanillaLoginLayout layout;
                    if (input.Detect(image, out layout))
                    {
                        long clickedSurface = input.SurfaceId;
                        input.Click(field == VanillaCredentialField.UserName ? layout.UserName : layout.Password, image.Size);
                        input.Pause(120);
                        RevealClickedFieldCaret(field, layout, image.Size, clickedSurface);
                        return;
                    }
                }
                input.Pause(200);
            }
            Trace(field, "detect", "failed");
            throw new InvalidOperationException("The login form and " + field + " control were not recognized; no credentials typed.");
        }

        private void RevealClickedFieldCaret(VanillaCredentialField field, VanillaLoginLayout clickedLayout,
            Size clickedSize, long clickedSurface)
        {
            input.CheckCancelled();
            using (Bitmap image = input.Capture())
            {
                VanillaLoginLayout layout;
                if (!input.Detect(image, out layout) || image.Size != clickedSize || input.SurfaceId != clickedSurface
                    || layout.UserNameControl != clickedLayout.UserNameControl || layout.PasswordControl != clickedLayout.PasswordControl)
                {
                    Trace(field, "reveal-caret", "captured controls changed; End withheld");
                    throw new InvalidOperationException("Login controls changed after clicking " + field + "; caret preparation stopped.");
                }
                if (input.Focus(layout, field) == VanillaFieldFocus.Contradicted)
                {
                    Trace(field, "reveal-caret", "native focus contradicted; End withheld");
                    throw new InvalidOperationException("A different control owns keyboard focus after clicking " + field + "; credential input blocked.");
                }
                input.CheckCancelled();
                // Vanilla selects a remembered username on click and suppresses its
                // custom caret while selected. End collapses that selection without
                // changing text. It is preparation, never sufficient proof for typing.
                input.RevealCaret(layout, field);
                Trace(field, "reveal-caret", "End completed; awaiting independent focus proof");
            }
        }

        private static void Trace(VanillaCredentialField field, string stage, string result)
        {
            VanillaDebugLog.Write("CREDENTIAL", "field=" + field + "; stage=" + stage + "; " + result);
        }

        private void ConfirmFieldFocus(VanillaCredentialField field)
        {
            Bitmap previous = null;
            Rectangle previousBounds = Rectangle.Empty;
            Rectangle firstCaret = Rectangle.Empty;
            long previousSurface = 0;
            int transitions = 0, nativeConfirmations = 0;
            try
            {
                for (int attempt = 0; attempt < 18; attempt++)
                {
                    input.CheckCancelled();
                    Bitmap current = input.Capture();
                    try
                    {
                        VanillaLoginLayout layout;
                        if (!input.Detect(current, out layout))
                            throw new InvalidOperationException("Login controls changed while checking " + field + " focus; no further credentials typed.");
                        Rectangle bounds = field == VanillaCredentialField.UserName ? layout.UserNameControl : layout.PasswordControl;
                        bool sameSurface = previous != null && previousSurface == input.SurfaceId
                            && previousBounds == bounds && previous.Size == current.Size;
                        if (previous != null && !sameSurface)
                        {
                            nativeConfirmations = transitions = 0;
                            firstCaret = Rectangle.Empty;
                        }
                        VanillaFieldFocus native = input.Focus(layout, field);
                        if (native == VanillaFieldFocus.Contradicted)
                        {
                            Trace(field, "focus", "native focus contradicted");
                            throw new InvalidOperationException("A different control owns keyboard focus while checking " + field + "; credential input blocked.");
                        }
                        if (native == VanillaFieldFocus.Confirmed)
                        {
                            if (++nativeConfirmations >= 2)
                            {
                                Trace(field, "focus", "confirmed by repeated native caret observations");
                                return;
                            }
                        }
                        else
                        {
                            nativeConfirmations = 0;
                            Rectangle caret;
                            if (sameSurface
                                && VanillaCredentialPattern.TryDetectCaretBlink(previous, current, bounds, out caret))
                            {
                                if (transitions == 0 || firstCaret == caret)
                                {
                                    firstCaret = caret;
                                    if (++transitions >= 2)
                                    {
                                        Trace(field, "focus", "confirmed by repeated custom caret blinks; bounds=" + caret);
                                        return;
                                    }
                                }
                                else { transitions = 1; firstCaret = caret; }
                            }
                        }
                        if (previous != null) previous.Dispose();
                        previous = current;
                        current = null;
                        previousBounds = bounds;
                        previousSurface = input.SurfaceId;
                    }
                    finally { if (current != null) current.Dispose(); }
                    input.Pause(120);
                }
            }
            finally { if (previous != null) previous.Dispose(); }
            Trace(field, "focus", "unverified; nativeConfirmations=" + nativeConfirmations + "; caretTransitions=" + transitions);
            throw new InvalidOperationException("Text focus was not positively verified inside the " + field + " credential field; input blocked.");
        }

        private void VerifyContents(string userName, int? passwordLength)
        {
            int confirmations = 0;
            Rectangle previousUser = Rectangle.Empty, previousPassword = Rectangle.Empty;
            Size previousSize = Size.Empty;
            long previousSurface = 0;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                input.CheckCancelled();
                using (Bitmap image = input.Capture())
                {
                    VanillaLoginLayout layout;
                    bool detected = input.Detect(image, out layout);
                    if (detected && input.Focus(layout, VanillaCredentialField.Password) == VanillaFieldFocus.Contradicted)
                        throw new InvalidOperationException("Credential field lost keyboard focus; login submission blocked.");
                    bool valid = detected
                        && input.VerifyUserName(image, layout, userName)
                        && (!passwordLength.HasValue || input.VerifyPasswordMask(image, layout, passwordLength.Value));
                    if (valid)
                    {
                        if (layout.UserNameControl != previousUser || layout.PasswordControl != previousPassword
                            || image.Size != previousSize || previousSurface != input.SurfaceId)
                            confirmations = 0;
                        previousUser = layout.UserNameControl;
                        previousPassword = layout.PasswordControl;
                        previousSize = image.Size;
                        previousSurface = input.SurfaceId;
                        if (++confirmations >= 2) return;
                    }
                    else confirmations = 0;
                }
                input.Pause(140);
            }
            throw new InvalidOperationException(passwordLength.HasValue
                ? "Exact username and expected password masking could not both be verified; login was not submitted."
                : "The visible username did not exactly match the configured username; password entry blocked.");
        }
    }

    internal sealed class VanillaCredentialInput : IVanillaCredentialInput
    {
        private readonly VanillaForegroundInput input;
        private Rectangle focusField;
        private VanillaVisualInputProof proof;
        private string lastDetectionEvidence;
        internal VanillaCredentialInput(VanillaForegroundInput input)
        {
            this.input = input;
            // Build the immutable tiny-label references before any frame starts its input
            // proof lifetime; cold reference preparation cannot expire a field's capture.
            VanillaSmallLabelPattern.Prepare();
        }
        public long SurfaceId { get { return proof == null ? 0 : proof.Window.ToInt64(); } }
        public Bitmap Capture()
        {
            Bitmap image = input.CaptureClientBitmap();
            proof = input.LastCaptureProof;
            return image;
        }
        public bool Detect(Bitmap image, out VanillaLoginLayout layout)
        {
            string evidence;
            bool detected = VanillaAuthPattern.TryDetectLogin(image, out layout, out evidence);
            if (!detected)
            {
                // Evidence contains only geometry/fixed-label scores, never credential
                // contents. Keep one copy per changed failure mode for field diagnostics.
                if (!string.Equals(lastDetectionEvidence, evidence, StringComparison.Ordinal))
                {
                    lastDetectionEvidence = evidence;
                    VanillaDebugLog.Write("CREDENTIAL", "Login form detection blocked: " + evidence);
                }
            }
            else if (lastDetectionEvidence != null)
            {
                VanillaDebugLog.Write("CREDENTIAL", "Login form detection recovered after a prior blocked frame.");
                lastDetectionEvidence = null;
            }
            return detected;
        }
        public VanillaFieldFocus Focus(VanillaLoginLayout layout, VanillaCredentialField field)
        {
            focusField = field == VanillaCredentialField.UserName ? layout.UserNameControl : layout.PasswordControl;
            return proof == null ? VanillaFieldFocus.Contradicted : VanillaCredentialFocus.Observe(proof.Window, focusField);
        }
        public bool VerifyUserName(Bitmap image, VanillaLoginLayout layout, string expected)
        {
            return VerifyVisibleUserName(image, layout.UserNameControl, expected);
        }
        internal static bool VerifyVisibleUserName(Bitmap image, Rectangle field, string expected)
        {
            VanillaTextLine[] lines;
            string evidence;
            Rectangle text = Rectangle.Inflate(field, -1, -1);
            if (VanillaTextRecognition.TryRead(image, text, true, out lines, out evidence)
                && lines.Length == 1 && lines[0].Confidence >= 80)
                return string.Equals(lines[0].Text.Trim(), expected, StringComparison.Ordinal);
            // The accurate model may resolve uncertain pixels, but must never override a
            // confident contradictory username or receive the configured name as an OCR hint.
            return VanillaTextRecognition.TryReadAccurate(image, text, true, out lines, out evidence)
                && lines.Length == 1 && lines[0].Confidence >= 80
                && string.Equals(lines[0].Text.Trim(), expected, StringComparison.Ordinal);
        }
        public bool VerifyPasswordMask(Bitmap image, VanillaLoginLayout layout, int expectedLength)
        {
            return VanillaCredentialPattern.VerifyPasswordMask(image, layout.PasswordControl, expectedLength);
        }
        public void Click(Rectangle field, Size imageSize)
        {
            if (proof == null || proof.ClientSize != imageSize)
                throw new InvalidOperationException("Credential control capture no longer matches the input surface.");
            input.ClickFromProof(field, proof);
        }
        public void RevealCaret(VanillaLoginLayout layout, VanillaCredentialField field)
        {
            CheckCancelled();
            if (proof == null || Focus(layout, field) == VanillaFieldFocus.Contradicted)
                throw new InvalidOperationException("Credential focus changed before revealing the " + field + " caret; End withheld.");
            input.PressFromProof(Keys.End, proof);
        }
        public void Replace(string value)
        {
            Rectangle intended = focusField;
            // The callback is scoped to this call, so cancellation/failure cannot leave a
            // credential guard attached to an unrelated later action. A custom-drawn caret
            // retains its just-established blink proof; any native contradiction stops typing.
            VanillaVisualInputProof currentProof = proof;
            input.ReplaceFocusedTextFromProof(value, currentProof, () =>
            {
                CheckCancelled();
                if (currentProof == null || VanillaCredentialFocus.Observe(currentProof.Window, intended) == VanillaFieldFocus.Contradicted)
                    throw new InvalidOperationException("Credential keyboard focus changed during entry; typing stopped.");
            });
        }
        public void Submit() { input.PressFromProof(Keys.Enter, proof); }
        public void Pause(int milliseconds)
        {
            for (int remaining = milliseconds; remaining > 0; remaining -= 40)
            {
                CheckCancelled();
                Thread.Sleep(Math.Min(40, remaining));
            }
            CheckCancelled();
        }
        public void CheckCancelled()
        {
            if (input.CancellationRequested != null && input.CancellationRequested())
                throw new OperationCanceledException("Credential verification cancelled.");
        }
    }

    internal static class VanillaCredentialFocus
    {
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
        {
            public uint Size, Flags;
            public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
            public Rect CaretRect;
        }
        [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll", SetLastError = true)] private static extern int MapWindowPoints(IntPtr from, IntPtr to, ref Rect points, uint count);
        [DllImport("kernel32.dll", EntryPoint = "SetLastError")] private static extern void ClearLastError(uint error);

        internal static VanillaFieldFocus Observe(IntPtr window, Rectangle field)
        {
            if (window == IntPtr.Zero || GetForegroundWindow() != window) return VanillaFieldFocus.Contradicted;
            uint processId;
            uint thread = GetWindowThreadProcessId(window, out processId);
            if (thread == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot query the captured client's UI thread; credential input stopped.");
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf(typeof(GuiThreadInfo)) };
            if (!GetGUIThreadInfo(thread, ref info))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot read client field-focus information; credential input stopped.");
            if (info.Focus == IntPtr.Zero || (info.Focus != window && !IsChild(window, info.Focus)))
                return VanillaFieldFocus.Contradicted;
            if (info.Caret == IntPtr.Zero || info.CaretRect.Bottom <= info.CaretRect.Top)
                return VanillaFieldFocus.Unknown;
            if (info.Caret != window && !IsChild(window, info.Caret)) return VanillaFieldFocus.Contradicted;
            Rect caret = info.CaretRect;
            if (info.Caret != window)
            {
                ClearLastError(0);
                if (MapWindowPoints(info.Caret, window, ref caret, 2) == 0 && Marshal.GetLastWin32Error() != 0)
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Cannot map the client caret; credential input stopped.");
            }
            Rectangle observed = Rectangle.FromLTRB(caret.Left, caret.Top, Math.Max(caret.Left + 1, caret.Right), caret.Bottom);
            return field.Contains(observed) ? VanillaFieldFocus.Confirmed : VanillaFieldFocus.Contradicted;
        }
    }
}
