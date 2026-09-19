using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Ordinary Windows foreground input for reconnect/login diagnostics.
    /// This does not inject into Vanilla/Gepard or modify game memory.
    ///
    /// A Vanilla process briefly owns a Gepard splash top-level window before its real game
    /// window appears. Never bind input to that transient splash. Re-resolve the best top-level
    /// window belonging to the same PID before every action so the input session follows the
    /// process across splash -> game-window transitions and can restore an already-running
    /// supervised client that is currently minimized/hidden.
    /// </summary>
    internal sealed class VanillaForegroundInput : IDisposable
    {
        internal const int DeliberateDragStartHoldMs = 250;
        internal const int DeliberateDragMoveSteps = 12;
        internal const int DeliberateDragStepDelayMs = 100;
        internal const int DeliberateDragDestinationHoldMs = 300;
        internal const int DeliberateDragPostReleaseMs = 500;

        private static readonly object ForegroundGate = new object();
        private readonly Process process;
        private readonly IntPtr preferredWindow;
        private IntPtr window;
        private string lastResolutionEvidence;
        internal Func<bool> CancellationRequested { get; set; }
        internal IntPtr Window
        {
            get
            {
                ThrowIfCancelled();
                string evidence;
                if (!TryRefreshWindow(out evidence)) throw new InvalidOperationException("Vanilla window unavailable: " + evidence);
                return window;
            }
        }

        private void ThrowIfCancelled()
        {
            if (CancellationRequested != null && CancellationRequested())
                throw new OperationCanceledException("Vanilla input cancelled; no further keys sent.");
        }

        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public INPUTUNION U; }
        [StructLayout(LayoutKind.Explicit)] private struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }
        [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
        {
            public int dx, dy;
            public uint mouseData, dwFlags, time;
            public UIntPtr dwExtraInfo;
        }
        [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
        {
            public ushort wVk, wScan;
            public uint dwFlags, time;
            public UIntPtr dwExtraInfo;
        }

        private sealed class WindowCandidate
        {
            public IntPtr Handle;
            public string ClassName;
            public string Title;
            public bool Visible;
            public bool Iconic;
            public bool KnownGame;
            public int Width;
            public int Height;
            public int Score;
        }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        private const uint INPUT_MOUSE = 0;
        private const uint INPUT_KEYBOARD = 1;
        private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        private const uint MOUSEEVENTF_LEFTUP = 0x0004;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const int SW_RESTORE = 9;
        private const int ActivationTimeoutMs = 12000;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);

        public VanillaForegroundInput(int processId) : this(processId, IntPtr.Zero) { }

        public VanillaForegroundInput(int processId, IntPtr preferredWindow)
        {
            process = Process.GetProcessById(processId);
            this.preferredWindow = preferredWindow;
            string evidence;
            TryRefreshWindow(out evidence);
            VanillaDebugLog.Write("INPUT", "Input session created for PID=" + processId + ", window=" + DescribeWindow(window)
                + ", preferred=" + DescribeWindow(preferredWindow) + ", resolver=" + evidence + ".");
        }

        internal static bool IsTransientBootstrapWindow(string className, string title)
        {
            string cls = className ?? string.Empty;
            string caption = title ?? string.Empty;
            // Vanilla creates a hidden 1x1 GDI+ hook/helper before the actual game window.
            // It contains "Vanilla MMO" in its caption but is never an interactive game surface.
            // Restoring it makes the helper visible in the taskbar, so reject it just like Gepard splash windows.
            return cls.IndexOf("Gepard_Splash", StringComparison.OrdinalIgnoreCase) >= 0
                || caption.IndexOf("GepardSplash", StringComparison.OrdinalIgnoreCase) >= 0
                || cls.IndexOf("GDI+ Hook Window Class", StringComparison.OrdinalIgnoreCase) >= 0
                || caption.StartsWith("GDI+ Window (", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsKnownVanillaGameWindow(string className, string title)
        {
            string cls = className ?? string.Empty;
            string caption = title ?? string.Empty;
            if (IsTransientBootstrapWindow(cls, caption)) return false;
            if (caption.IndexOf("Launcher", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return cls.IndexOf("Vanilla MMO", StringComparison.OrdinalIgnoreCase) >= 0
                || caption.IndexOf("Vanilla MMO", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static int WindowCandidateScore(bool sameProcess, bool visible, int width, int height,
            bool foreground, bool preferred, string className, string title, bool iconic = false)
        {
            if (!sameProcess || IsTransientBootstrapWindow(className, title)) return int.MinValue;

            bool knownGame = IsKnownVanillaGameWindow(className, title);
            if (!knownGame && (!visible || width < 200 || height < 120)) return int.MinValue;

            int score = 100;
            if (preferred) score += 1000;
            if (foreground) score += 500;
            if (knownGame) score += 900;
            if (visible) score += 100;
            if (iconic) score += knownGame ? 50 : -50;
            string caption = title ?? string.Empty;
            if (caption.IndexOf("Launcher", StringComparison.OrdinalIgnoreCase) >= 0) score += preferred ? 200 : 20;
            if (width > 0 && height > 0) score += Math.Min(200, (width * height) / 10000);
            return score;
        }

        public void Activate()
        {
            lock (ForegroundGate) ActivateCore();
        }

        private void ActivateCore()
        {
            ThrowIfCancelled();
            if (process.HasExited) throw new InvalidOperationException("Vanilla client exited.");

            IntPtr originalForeground = GetForegroundWindow();
            IntPtr previousTarget = window;
            Stopwatch watch = Stopwatch.StartNew();
            int focusAttempt = 0;
            long nextWaitingLogAt = 0;
            string lastEvidence = null;

            while (watch.ElapsedMilliseconds < ActivationTimeoutMs)
            {
                ThrowIfCancelled();
                if (process.HasExited) throw new InvalidOperationException("Vanilla client exited.");

                string evidence;
                if (!TryRefreshWindow(out evidence))
                {
                    lastEvidence = evidence;
                    if (watch.ElapsedMilliseconds >= nextWaitingLogAt)
                    {
                        VanillaDebugLog.Write("FOCUS", "PID=" + process.Id
                            + " waiting for interactive Vanilla window; elapsedMs=" + watch.ElapsedMilliseconds
                            + "; " + evidence + ". No input sent.");
                        nextWaitingLogAt = watch.ElapsedMilliseconds + 1000;
                    }
                    Thread.Sleep(120);
                    continue;
                }

                if (window != previousTarget)
                {
                    VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " window transition: "
                        + DescribeWindow(previousTarget) + " -> " + DescribeWindow(window)
                        + "; resolver=" + evidence + ".");
                    previousTarget = window;
                    focusAttempt = 0;
                }

                IntPtr current = GetForegroundWindow();
                if (current == window)
                {
                    VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " focus already correct; target="
                        + DescribeWindow(window) + ", elapsedMs=" + watch.ElapsedMilliseconds + ".");
                    return;
                }

                if (IsUsableWindowForProcess(current, process.Id))
                {
                    IntPtr old = window;
                    window = current;
                    VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " adopted same-process foreground window "
                        + DescribeWindow(window) + " instead of stale/secondary " + DescribeWindow(old) + ".");
                    return;
                }

                focusAttempt++;
                ShowWindow(window, SW_RESTORE);
                BringWindowToTop(window);
                SetForegroundWindow(window);
                Thread.Sleep(90);
                current = GetForegroundWindow();
                if (current == window)
                {
                    VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " focus OK attempt=" + focusAttempt
                        + ", target=" + DescribeWindow(window) + ", before=" + DescribeWindow(originalForeground)
                        + ", elapsedMs=" + watch.ElapsedMilliseconds + ".");
                    return;
                }

                if (IsUsableWindowForProcess(current, process.Id))
                {
                    IntPtr old = window;
                    window = current;
                    VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " focus transitioned to same-process game window "
                        + DescribeWindow(window) + " while targeting " + DescribeWindow(old) + "; accepting transition.");
                    return;
                }

                VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " focus attempt=" + focusAttempt
                    + " not yet target; foreground=" + DescribeWindow(current) + ", target=" + DescribeWindow(window)
                    + ", elapsedMs=" + watch.ElapsedMilliseconds + ".");
                Thread.Sleep(110);
            }

            IntPtr after = GetForegroundWindow();
            VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " focus FAILED after " + watch.ElapsedMilliseconds
                + " ms; foreground=" + DescribeWindow(after) + ", target=" + DescribeWindow(window)
                + ", resolver=" + (lastEvidence ?? lastResolutionEvidence ?? "unknown") + ". No input sent.");
            throw new InvalidOperationException("The selected Vanilla process did not expose a usable foreground game window in time. No input was sent.");
        }

        public void ClickNormalized(double x, double y, bool requireForeground = true)
        {
            ClickNormalizedWithDiagnostics(x, y, requireForeground);
        }

        public string ClickNormalizedWithDiagnostics(double x, double y, bool requireForeground = true)
        {
            lock (ForegroundGate) return ClickCore(x, y, requireForeground);
        }

        private string ClickCore(double x, double y, bool requireForeground)
        {
            ThrowIfCancelled();
            if (requireForeground) Activate();
            else
            {
                string evidence;
                if (!TryRefreshWindow(out evidence))
                    throw new InvalidOperationException("Selected window is not ready for mouse input: " + evidence);
                ShowWindow(window, SW_RESTORE);
                BringWindowToTop(window);
                SetForegroundWindow(window);
                Thread.Sleep(120);
            }
            if (x < 0 || x > 1 || y < 0 || y > 1) throw new ArgumentOutOfRangeException("Normalized coordinates must be within 0..1.");

            IntPtr foregroundBefore = GetForegroundWindow();
            RECT rect;
            if (!GetClientRect(window, out rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Vanilla client area.");
            int width = Math.Max(1, rect.Right - rect.Left), height = Math.Max(1, rect.Bottom - rect.Top);
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map Vanilla client origin.");
            var target = new POINT
            {
                X = Math.Max(0, Math.Min(width - 1, (int)Math.Round(x * width))),
                Y = Math.Max(0, Math.Min(height - 1, (int)Math.Round(y * height)))
            };
            var clientTarget = target;
            if (!ClientToScreen(window, ref target)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map Vanilla client coordinate.");

            POINT previous;
            bool restore = GetCursorPos(out previous);
            IntPtr hitBeforeMove = WindowFromPoint(target);
            if (!SetCursorPos(target.X, target.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the mouse position.");
            Thread.Sleep(120);
            POINT actual;
            GetCursorPos(out actual);
            IntPtr hitAtClick = WindowFromPoint(actual);

            VerifyForeground();
            uint hitPid;
            GetWindowThreadProcessId(hitAtClick, out hitPid);
            if (hitPid != (uint)process.Id)
                throw new InvalidOperationException("Mouse target no longer belongs to the intended client; no click sent.");
            var down = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } } };
            uint downSent = SendInput(1, down, Marshal.SizeOf(typeof(INPUT)));
            int downError = downSent == 1 ? 0 : Marshal.GetLastWin32Error();
            Thread.Sleep(110);
            var up = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } } };
            uint upSent = SendInput(1, up, Marshal.SizeOf(typeof(INPUT)));
            int upError = upSent == 1 ? 0 : Marshal.GetLastWin32Error();
            Thread.Sleep(130);

            IntPtr foregroundAfter = GetForegroundWindow();
            string diagnostics = string.Format(
                "main={0}; visible={1}; dpi={2}; client={3}x{4}; normalized=({5:0.0000},{6:0.0000}); origin=({7},{8}); targetClient=({9},{10}); targetScreen=({11},{12}); cursorActual=({13},{14}); foregroundBefore={15}; foregroundAfter={16}; hitBefore={17}; hitAtClick={18}; SendInputDown={19}/1 err={20}; SendInputUp={21}/1 err={22}",
                DescribeWindow(window), IsWindowVisible(window), SafeDpi(window), width, height, x, y,
                origin.X, origin.Y, clientTarget.X, clientTarget.Y, target.X, target.Y, actual.X, actual.Y,
                DescribeWindow(foregroundBefore), DescribeWindow(foregroundAfter), DescribeWindow(hitBeforeMove), DescribeWindow(hitAtClick),
                downSent, downError, upSent, upError);

            if (restore) SetCursorPos(previous.X, previous.Y);
            VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " CLICK; " + diagnostics);
            if (downSent != 1 || upSent != 1)
                throw new Win32Exception(downError != 0 ? downError : upError, "Windows SendInput did not send the complete mouse click. " + diagnostics);
            return diagnostics;
        }

        public void DragNormalized(double fromX, double fromY, double toX, double toY)
        {
            DragNormalizedCore(fromX, fromY, toX, toY, 100, 6, 55, 0, 180, "DRAG");
        }

        internal void DragNormalizedDeliberate(double fromX, double fromY, double toX, double toY)
        {
            // Cart maintenance deliberately moves much more slowly than an ordinary click/drag.
            // This tolerates RDP/client lag without changing unrelated mouse automation.
            DragNormalizedCore(fromX, fromY, toX, toY,
                DeliberateDragStartHoldMs, DeliberateDragMoveSteps, DeliberateDragStepDelayMs,
                DeliberateDragDestinationHoldMs, DeliberateDragPostReleaseMs, "SLOW DRAG");
        }

        private void DragNormalizedCore(double fromX, double fromY, double toX, double toY,
            int startHoldMs, int moveSteps, int stepDelayMs, int destinationHoldMs, int postReleaseMs, string label)
        {
            lock (ForegroundGate)
            {
                ThrowIfCancelled();
                ActivateCore();
                VerifyForeground();
                if (fromX < 0 || fromX > 1 || fromY < 0 || fromY > 1 || toX < 0 || toX > 1 || toY < 0 || toY > 1)
                    throw new ArgumentOutOfRangeException("Normalized drag coordinates must be within 0..1.");

                RECT rect;
                if (!GetClientRect(window, out rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Vanilla client area.");
                int width = Math.Max(1, rect.Right - rect.Left), height = Math.Max(1, rect.Bottom - rect.Top);
                var origin = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map Vanilla client origin.");
                Func<double, double, POINT> point = (nx, ny) => new POINT
                {
                    X = origin.X + Math.Max(0, Math.Min(width - 1, (int)Math.Round(nx * width))),
                    Y = origin.Y + Math.Max(0, Math.Min(height - 1, (int)Math.Round(ny * height)))
                };
                POINT from = point(fromX, fromY), to = point(toX, toY), previous;
                bool restore = GetCursorPos(out previous);
                try
                {
                    if (!SetCursorPos(from.X, from.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the drag start position.");
                    Thread.Sleep(startHoldMs);
                    VerifyMouseOwner(from, "drag start");
                    VerifyForeground();
                    var down = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTDOWN } } } };
                    if (SendInput(1, down, Marshal.SizeOf(typeof(INPUT))) != 1)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the drag mouse-down.");
                    try
                    {
                        for (int step = 1; step <= moveSteps; step++)
                        {
                            ThrowIfCancelled(); VerifyForeground();
                            int x = from.X + (to.X - from.X) * step / moveSteps;
                            int y = from.Y + (to.Y - from.Y) * step / moveSteps;
                            if (!SetCursorPos(x, y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected drag movement.");
                            Thread.Sleep(stepDelayMs);
                        }
                        VerifyMouseOwner(to, "drag destination");
                        if (destinationHoldMs > 0) Thread.Sleep(destinationHoldMs);
                    }
                    finally
                    {
                        var up = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } } };
                        if (SendInput(1, up, Marshal.SizeOf(typeof(INPUT))) != 1)
                            VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " drag mouse-up was not fully accepted by Windows.");
                    }
                    Thread.Sleep(postReleaseMs);
                    VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " " + label + " client normalized ("
                        + fromX.ToString("0.0000") + "," + fromY.ToString("0.0000") + ") -> ("
                        + toX.ToString("0.0000") + "," + toY.ToString("0.0000") + "); startHoldMs="
                        + startHoldMs + ", steps=" + moveSteps + ", stepDelayMs=" + stepDelayMs
                        + ", destinationHoldMs=" + destinationHoldMs + ", postReleaseMs=" + postReleaseMs + ".");
                }
                finally { if (restore) SetCursorPos(previous.X, previous.Y); }
            }
        }

        private void VerifyMouseOwner(POINT point, string context)
        {
            IntPtr hit = WindowFromPoint(point);
            uint pid;
            GetWindowThreadProcessId(hit, out pid);
            if (pid != (uint)process.Id)
                throw new InvalidOperationException("Mouse " + context + " no longer belongs to the intended Vanilla client; no unsafe drag continued.");
        }

        internal Bitmap CaptureClientBitmap()
        {
            Activate();
            RECT rect;
            if (!GetClientRect(window, out rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Vanilla client area for visual recognition.");
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width < 200 || height < 120) throw new InvalidOperationException("Vanilla client area is too small for visual recognition: " + width + "x" + height);
            var origin = new POINT { X = 0, Y = 0 };
            if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map Vanilla client for visual recognition.");
            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
            return bitmap;
        }

        public void Press(Keys key)
        {
            lock (ForegroundGate)
            {
                Activate();
                VerifyForeground();
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " KEY " + key + " press.");
                SendKey(key, false);
                try { Thread.Sleep(70); }
                finally { SendKey(key, true); }
                Thread.Sleep(45);
            }
        }

        public void Chord(bool ctrl, bool alt, bool shift, Keys key)
        {
            Activate();
            ChordInVerifiedForeground(ctrl, alt, shift, key);
        }

        // Resume takes its fresh baseline after Activate; do not refocus/sleep again before the chord.
        internal void ChordInVerifiedForeground(bool ctrl, bool alt, bool shift, Keys key)
        {
            lock (ForegroundGate) ChordCore(ctrl, alt, shift, key);
        }

        private void VerifyForeground()
        {
            ThrowIfCancelled();
            if (process.HasExited || window == IntPtr.Zero || GetForegroundWindow() != window
                || !IsUsableWindowForProcess(window, process.Id))
                throw new InvalidOperationException("The intended client no longer owns foreground focus; no input sent.");
        }

        private void ChordCore(bool ctrl, bool alt, bool shift, Keys key)
        {
            VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " CHORD " + ChordText(ctrl, alt, shift, key) + " begin.");
            DispatchGuardedChord(ctrl, alt, shift, key, () =>
            {
                ThrowIfCancelled();
                if (process.HasExited || window == IntPtr.Zero || GetForegroundWindow() != window
                    || !IsUsableWindowForProcess(window, process.Id))
                    throw new InvalidOperationException("Vanilla lost the verified foreground; hotkey stopped.");
            }, SendKey, Thread.Sleep);
            VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " CHORD " + ChordText(ctrl, alt, shift, key) + " complete.");
        }

        internal static void DispatchGuardedChord(bool ctrl, bool alt, bool shift, Keys key,
            System.Action ensureAllowed, System.Action<Keys, bool> send, System.Action<int> pause)
        {
            var held = new Stack<Keys>();
            Exception failure = null;
            System.Action<Keys> down = value => { ensureAllowed(); send(value, false); held.Push(value); };
            try
            {
                if (ctrl) down(Keys.ControlKey);
                if (alt) down(Keys.Menu);
                if (shift) down(Keys.ShiftKey);
                pause(80);
                down(key);
                pause(85);
            }
            catch (Exception ex) { failure = ex; throw; }
            finally
            {
                Exception releaseFailure = null;
                while (held.Count > 0)
                {
                    try { send(held.Pop(), true); }
                    catch (Exception ex) { releaseFailure = releaseFailure ?? ex; }
                }
                if (failure == null && releaseFailure != null) throw releaseFailure;
            }
            pause(150);
        }

        public void SelectAll() { Chord(true, false, false, Keys.A); }

        public void ReplaceFocusedText(string text)
        {
            SelectAll();
            Press(Keys.Back);
            TypeText(text ?? string.Empty);
        }

        public void TypeText(string text)
        {
            lock (ForegroundGate) TypeTextCore(text);
        }

        private void TypeTextCore(string text)
        {
            if (text == null) return;
            Activate();
            VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " typing text length=" + text.Length + " (content intentionally not logged). target="
                + DescribeWindow(window) + ".");
            foreach (char c in text)
            {
                VerifyForeground();
                SendUnicode(c, false);
                SendUnicode(c, true);
                Thread.Sleep(22);
            }
            Thread.Sleep(80);
        }

        private bool TryRefreshWindow(out string evidence)
        {
            evidence = null;
            if (process.HasExited)
            {
                window = IntPtr.Zero;
                evidence = "process exited";
                return false;
            }
            process.Refresh();

            IntPtr foreground = GetForegroundWindow();
            var candidates = new List<WindowCandidate>();
            var observed = new List<string>();
            EnumWindows(delegate(IntPtr hwnd, IntPtr lParam)
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != (uint)process.Id) return true;

                string cls = WindowClass(hwnd);
                string title = WindowTitle(hwnd);
                bool visible = IsWindowVisible(hwnd);
                bool iconic = IsIconic(hwnd);
                bool knownGame = IsKnownVanillaGameWindow(cls, title);
                RECT rect;
                int width = 0, height = 0;
                if (GetClientRect(hwnd, out rect))
                {
                    width = Math.Max(0, rect.Right - rect.Left);
                    height = Math.Max(0, rect.Bottom - rect.Top);
                }

                int score = WindowCandidateScore(true, visible, width, height, hwnd == foreground,
                    preferredWindow != IntPtr.Zero && hwnd == preferredWindow, cls, title, iconic);
                observed.Add(DescribeWindow(hwnd) + " visible=" + visible + " iconic=" + iconic
                    + " client=" + width + "x" + height + " knownGame=" + knownGame + " score=" + score);

                if (score != int.MinValue)
                {
                    candidates.Add(new WindowCandidate
                    {
                        Handle = hwnd,
                        ClassName = cls,
                        Title = title,
                        Visible = visible,
                        Iconic = iconic,
                        KnownGame = knownGame,
                        Width = width,
                        Height = height,
                        Score = score
                    });
                }
                return true;
            }, IntPtr.Zero);

            WindowCandidate best = candidates.OrderByDescending(c => c.Score).FirstOrDefault();
            if (best == null)
            {
                window = IntPtr.Zero;
                evidence = "no usable top-level window for PID " + process.Id
                    + (observed.Count == 0 ? "; no top-level windows enumerated" : "; observed=[" + string.Join(" | ", observed) + "]");
                lastResolutionEvidence = evidence;
                return false;
            }

            window = best.Handle;
            if (best.KnownGame && (!best.Visible || best.Iconic || best.Width < 200 || best.Height < 120))
            {
                VanillaDebugLog.Write("FOCUS", "PID=" + process.Id + " restoring existing Vanilla game window before visual/input use: "
                    + DescribeWindow(window) + "; visible=" + best.Visible + ", iconic=" + best.Iconic
                    + ", client=" + best.Width + "x" + best.Height + ".");
                ShowWindow(window, SW_RESTORE);
                Thread.Sleep(140);

                RECT restoredRect;
                bool restoredVisible = IsWindowVisible(window);
                bool restoredIconic = IsIconic(window);
                int restoredWidth = 0, restoredHeight = 0;
                if (GetClientRect(window, out restoredRect))
                {
                    restoredWidth = Math.Max(0, restoredRect.Right - restoredRect.Left);
                    restoredHeight = Math.Max(0, restoredRect.Bottom - restoredRect.Top);
                }
                if (!restoredVisible || restoredIconic || restoredWidth < 200 || restoredHeight < 120)
                {
                    evidence = "known Vanilla game window found but restore is not ready yet: " + DescribeWindow(window)
                        + "; visible=" + restoredVisible + ", iconic=" + restoredIconic
                        + ", client=" + restoredWidth + "x" + restoredHeight
                        + "; observed=[" + string.Join(" | ", observed) + "]";
                    lastResolutionEvidence = evidence;
                    return false;
                }

                best.Visible = restoredVisible;
                best.Iconic = restoredIconic;
                best.Width = restoredWidth;
                best.Height = restoredHeight;
            }

            evidence = "selected=" + DescribeWindow(best.Handle) + ", client=" + best.Width + "x" + best.Height
                + ", visible=" + best.Visible + ", iconic=" + best.Iconic + ", knownGame=" + best.KnownGame
                + ", score=" + best.Score + ", candidates=" + candidates.Count
                + "; observed=[" + string.Join(" | ", observed) + "]";
            lastResolutionEvidence = evidence;
            return true;
        }

        private static bool IsUsableWindowForProcess(IntPtr hwnd, int processId)
        {
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            if (pid != (uint)processId) return false;
            string cls = WindowClass(hwnd), title = WindowTitle(hwnd);
            if (IsTransientBootstrapWindow(cls, title)) return false;
            RECT rect;
            if (!GetClientRect(hwnd, out rect)) return false;
            return rect.Right - rect.Left >= 200 && rect.Bottom - rect.Top >= 120;
        }

        private void SendKey(Keys key, bool up)
        {
            uint scan = MapVirtualKey((uint)key, 0);
            Send(new[]
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wVk = 0, wScan = (ushort)scan, dwFlags = KEYEVENTF_SCANCODE | (up ? KEYEVENTF_KEYUP : 0) }
                    }
                }
            });
        }

        private void SendUnicode(char c, bool up)
        {
            Send(new[]
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) }
                    }
                }
            });
        }

        private static void Send(INPUT[] inputs)
        {
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
            if (sent != inputs.Length) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows SendInput did not send the complete input sequence.");
        }

        private static string ChordText(bool ctrl, bool alt, bool shift, Keys key)
        {
            var text = new StringBuilder();
            if (ctrl) text.Append("Ctrl+");
            if (alt) text.Append("Alt+");
            if (shift) text.Append("Shift+");
            text.Append(key);
            return text.ToString();
        }

        private static uint SafeDpi(IntPtr hwnd)
        {
            try { return hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd); }
            catch { return 0; }
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var text = new StringBuilder(256);
            try { GetWindowText(hwnd, text, text.Capacity); } catch { }
            return Clean(text.ToString());
        }

        private static string WindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            var text = new StringBuilder(128);
            try { GetClassName(hwnd, text, text.Capacity); } catch { }
            return Clean(text.ToString());
        }

        private static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "none";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return string.Format("0x{0:X} pid={1} class='{2}' title='{3}'", hwnd.ToInt64(), pid, WindowClass(hwnd), WindowTitle(hwnd));
        }

        private static string Clean(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public void Dispose()
        {
            VanillaDebugLog.Write("INPUT", "Input session disposed for PID=" + process.Id + ".");
            process.Dispose();
        }
    }
}
