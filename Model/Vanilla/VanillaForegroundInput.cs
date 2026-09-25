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
        internal const int DeliberateDragMoveSteps = 6;
        internal const int DeliberateDragStepDelayMs = 35;
        internal const int DeliberateDragDestinationHoldMs = 300;
        internal const int DeliberateDragPostReleaseMs = 500;

        private static readonly object ForegroundGate = new object();
        private readonly Process process;
        private readonly IntPtr preferredWindow;
        private IntPtr window;
        private string lastResolutionEvidence;
        internal Func<bool> CancellationRequested { get; set; }
        internal VanillaVisualInputProof LastCaptureProof { get; private set; }
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
        private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        private const uint KEYEVENTF_KEYUP = 0x0002;
        private const uint KEYEVENTF_UNICODE = 0x0004;
        private const uint KEYEVENTF_SCANCODE = 0x0008;
        private const uint MAPVK_VK_TO_VSC_EX = 4;
        private const int SW_RESTORE = 9;
        private const int ActivationTimeoutMs = 12000;

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
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

            IntPtr hitBeforeMove = WindowFromPoint(target);
            VerifyForeground();
            if (!SetCursorPos(target.X, target.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the mouse position.");
            DelayWithCancellation(120);
            POINT actual;
            if (!GetCursorPos(out actual) || actual.X != target.X || actual.Y != target.Y)
                throw new InvalidOperationException("Cursor moved outside the intended target; click withheld.");
            IntPtr hitAtClick = WindowFromPoint(actual);

            VerifyForeground();
            uint hitPid;
            GetWindowThreadProcessId(hitAtClick, out hitPid);
            if (hitPid != (uint)process.Id)
                throw new InvalidOperationException("Mouse target no longer belongs to the intended client; no click sent.");
            uint downSent = 0, upSent = 0;
            int downError = 0, upError = 0;
            DispatchGuardedClick(VerifyForeground, up =>
            {
                var button = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT
                    { dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN } } } };
                uint sent = SendInput(1, button, Marshal.SizeOf(typeof(INPUT)));
                int error = sent == 1 ? 0 : Marshal.GetLastWin32Error();
                if (up) { upSent = sent; upError = error; }
                else { downSent = sent; downError = error; }
                if (sent != 1) throw new Win32Exception(error, "Windows SendInput rejected the mouse " + (up ? "release." : "press."));
            }, DelayWithCancellation, () => MoveCursorAwayFrom(new Rectangle(clientTarget.X, clientTarget.Y, 1, 1)));

            IntPtr foregroundAfter = GetForegroundWindow();
            string diagnostics = string.Format(
                "main={0}; visible={1}; dpi={2}; client={3}x{4}; normalized=({5:0.0000},{6:0.0000}); origin=({7},{8}); targetClient=({9},{10}); targetScreen=({11},{12}); cursorActual=({13},{14}); foregroundBefore={15}; foregroundAfter={16}; hitBefore={17}; hitAtClick={18}; SendInputDown={19}/1 err={20}; SendInputUp={21}/1 err={22}",
                DescribeWindow(window), IsWindowVisible(window), SafeDpi(window), width, height, x, y,
                origin.X, origin.Y, clientTarget.X, clientTarget.Y, target.X, target.Y, actual.X, actual.Y,
                DescribeWindow(foregroundBefore), DescribeWindow(foregroundAfter), DescribeWindow(hitBeforeMove), DescribeWindow(hitAtClick),
                downSent, downError, upSent, upError);

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
                POINT from = point(fromX, fromY), to = point(toX, toY);
                if (!SetCursorPos(from.X, from.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the drag start position.");
                DelayWithCancellation(startHoldMs);
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
                        DelayWithCancellation(stepDelayMs);
                    }
                    VerifyMouseOwner(to, "drag destination");
                    if (destinationHoldMs > 0) DelayWithCancellation(destinationHoldMs);
                }
                finally
                {
                    var up = new[] { new INPUT { type = INPUT_MOUSE, U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = MOUSEEVENTF_LEFTUP } } } };
                    if (SendInput(1, up, Marshal.SizeOf(typeof(INPUT))) != 1)
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the drag mouse-up.");
                }
                MoveCursorAwayFrom(Rectangle.Union(new Rectangle(from.X - origin.X, from.Y - origin.Y, 1, 1),
                    new Rectangle(to.X - origin.X, to.Y - origin.Y, 1, 1)));
                DelayWithCancellation(postReleaseMs);
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " " + label + " client normalized ("
                    + fromX.ToString("0.0000") + "," + fromY.ToString("0.0000") + ") -> ("
                    + toX.ToString("0.0000") + "," + toY.ToString("0.0000") + "); startHoldMs="
                    + startHoldMs + ", steps=" + moveSteps + ", stepDelayMs=" + stepDelayMs
                    + ", destinationHoldMs=" + destinationHoldMs + ", postReleaseMs=" + postReleaseMs + ".");
            }
        }

        private void DelayWithCancellation(int milliseconds)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                ThrowIfCancelled();
                int slice = Math.Min(50, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
            ThrowIfCancelled();
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
            lock (ForegroundGate)
            {
                Bitmap first = CaptureClientBitmapCore();
                try
                {
                    Rectangle dialog; string evidence;
                    if (!VanillaServerClosedPattern.TryDetect(first, out dialog, out evidence)) return first;
                    var firstProof = LastCaptureProof;
                    // A recognized outage modal authorizes no keyboard or mouse input.
                    // Confirm with a new frame, retaining only the fixed dialog identity.
                    DelayWithCancellation(150);
                    using (Bitmap second = CaptureClientBitmapCore())
                    {
                        Rectangle secondDialog; string secondEvidence;
                        if (firstProof.Window == LastCaptureProof.Window && firstProof.ProcessId == LastCaptureProof.ProcessId
                            && firstProof.ClientSize == LastCaptureProof.ClientSize && firstProof.ClientOrigin == LastCaptureProof.ClientOrigin
                            && VanillaServerClosedPattern.TryDetect(second, out secondDialog, out secondEvidence)
                            && Math.Abs(dialog.X - secondDialog.X) <= 3 && Math.Abs(dialog.Y - secondDialog.Y) <= 3
                            && Math.Abs(dialog.Width - secondDialog.Width) <= 3 && Math.Abs(dialog.Height - secondDialog.Height) <= 3)
                            throw new VanillaServerClosedException();
                    }
                    throw new InvalidOperationException("Server unavailable dialog changed during confirmation; no input sent.");
                }
                catch { first.Dispose(); LastCaptureProof = null; throw; }
            }
        }

        private Bitmap CaptureClientBitmapCore()
        {
            lock (ForegroundGate)
            {
                LastCaptureProof = null;
                Activate();
                VerifyForeground();
                RECT rect;
                if (!GetClientRect(window, out rect)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read Vanilla client area for visual recognition.");
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                if (width < 200 || height < 120) throw new InvalidOperationException("Vanilla client area is too small for visual recognition: " + width + "x" + height);
                var origin = new POINT { X = 0, Y = 0 };
                if (!ClientToScreen(window, ref origin)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot map Vanilla client for visual recognition.");
                var proof = new VanillaVisualInputProof(process.Id, window, new Size(width, height),
                    new Point(origin.X, origin.Y), Stopwatch.GetTimestamp());
                var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                try
                {
                    using (Graphics graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
                    VerifyCaptureProof(proof);
                    LastCaptureProof = proof;
                    return bitmap;
                }
                catch { bitmap.Dispose(); throw; }
            }
        }

        private void VerifyCaptureProof(VanillaVisualInputProof proof)
        {
            ThrowIfCancelled();
            if (proof == null) throw new InvalidOperationException("A fresh captured UI proof is required before input.");
            VerifyForeground();
            RECT rectangle;
            var origin = new POINT();
            if (!GetClientRect(window, out rectangle) || !ClientToScreen(window, ref origin))
                throw new InvalidOperationException("The captured client geometry is unavailable; input withheld.");
            proof.RequireMatches(process.Id, window, GetForegroundWindow(),
                new Size(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top),
                new Point(origin.X, origin.Y), Stopwatch.GetTimestamp());
        }

        internal void PressFromProof(Keys key, VanillaVisualInputProof proof)
        {
            lock (ForegroundGate)
            {
                VerifyCaptureProof(proof);
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " verified-capture KEY " + key + ".");
                DispatchGuardedKey(key, () => VerifyCaptureProof(proof), SendKey, Thread.Sleep);
            }
        }

        internal static void DispatchGuardedKey(Keys key, System.Action verify, Action<Keys, bool> send, Action<int> pause)
        {
            verify();
            send(key, false);
            try { pause(70); }
            finally { send(key, true); }
            pause(45);
        }

        internal void ClickFromProof(Rectangle control, VanillaVisualInputProof proof)
        {
            lock (ForegroundGate)
            {
                VerifyCaptureProof(proof);
                Point center = proof.ControlCenter(control);
                var target = new POINT { X = proof.ClientOrigin.X + center.X, Y = proof.ClientOrigin.Y + center.Y };
                if (!SetCursorPos(target.X, target.Y)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected the mouse position.");
                DelayWithCancellation(120);
                DispatchGuardedClick(() =>
                {
                    VerifyCaptureProof(proof);
                    POINT actual;
                    if (!GetCursorPos(out actual) || actual.X != target.X || actual.Y != target.Y)
                        throw new InvalidOperationException("Cursor moved outside the detected control; click withheld.");
                    IntPtr hit = WindowFromPoint(actual);
                    if (hit != proof.Window && !IsChild(proof.Window, hit))
                        throw new InvalidOperationException("Detected control is covered by another window; click withheld.");
                }, SendMouseButton, DelayWithCancellation, () => MoveCursorAwayFrom(control));
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " clicked captured control " + control + ".");
            }
        }

        internal static void DispatchGuardedClick(System.Action verify, Action<bool> sendButton,
            Action<int> pause, System.Action parkCursor)
        {
            verify();
            sendButton(false);
            try { pause(110); }
            finally { sendButton(true); }
            // Parking verifies current foreground ownership again. Do not restore a
            // previous cursor position that may cover the text about to be checked.
            parkCursor();
            pause(130);
        }

        internal const int CursorParkingPadding = 48;

        internal static Point SelectCursorParkingPoint(Size client, Rectangle excludedControl)
        {
            var bounds = new Rectangle(Point.Empty, client);
            if (client.Width <= 0 || client.Height <= 0)
                throw new InvalidOperationException("Client geometry is unavailable for cursor parking.");
            if (excludedControl == Rectangle.Empty)
                excludedControl = new Rectangle(client.Width / 4, client.Height / 4, client.Width / 2, client.Height / 2);
            if (excludedControl.Width <= 0 || excludedControl.Height <= 0 || !bounds.Contains(excludedControl))
                throw new InvalidOperationException("The cursor exclusion area is outside the current client.");
            Rectangle excluded = Rectangle.Inflate(excludedControl, CursorParkingPadding, CursorParkingPadding);
            int insetX = Math.Min(16, (client.Width - 1) / 2), insetY = Math.Min(16, (client.Height - 1) / 2);
            var candidates = new[]
            {
                new Point(client.Width - 1 - insetX, client.Height - 1 - insetY),
                new Point(insetX, client.Height - 1 - insetY),
                new Point(client.Width - 1 - insetX, insetY), new Point(insetX, insetY)
            };
            Point? best = null;
            double bestDistance = -1;
            foreach (Point point in candidates)
            {
                if (excluded.Contains(point)) continue;
                double dx = point.X - (excludedControl.Left + excludedControl.Width / 2.0);
                double dy = point.Y - (excludedControl.Top + excludedControl.Height / 2.0);
                double distance = dx * dx + dy * dy;
                if (distance > bestDistance) { best = point; bestDistance = distance; }
            }
            if (!best.HasValue) throw new InvalidOperationException("No client corner is clear of the control; visual verification withheld.");
            return best.Value;
        }

        internal Point MoveCursorAwayFrom(Rectangle excludedControl)
        {
            lock (ForegroundGate)
            {
                VerifyForeground();
                RECT rectangle;
                var origin = new POINT();
                if (!GetClientRect(window, out rectangle) || !ClientToScreen(window, ref origin))
                    throw new InvalidOperationException("Current client geometry is unavailable for cursor parking.");
                Point point = SelectCursorParkingPoint(new Size(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top), excludedControl);
                var screen = new POINT { X = origin.X + point.X, Y = origin.Y + point.Y };
                IntPtr hit = WindowFromPoint(screen);
                if (hit != window && !IsChild(window, hit))
                    throw new InvalidOperationException("Cursor parking area is covered by another window; visual verification withheld.");
                VerifyForeground();
                if (!SetCursorPos(screen.X, screen.Y))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected cursor parking.");
                DelayWithCancellation(100);
                VerifyForeground();
                POINT actual;
                if (!GetCursorPos(out actual) || actual.X != screen.X || actual.Y != screen.Y)
                    throw new InvalidOperationException("Cursor moved before visual verification; re-observation is required.");
                hit = WindowFromPoint(actual);
                if (hit != window && !IsChild(window, hit))
                    throw new InvalidOperationException("Cursor parking area no longer belongs to the intended client.");
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " cursor parked at client " + point + " outside " + excludedControl + ".");
                return point;
            }
        }

        private static void SendMouseButton(bool up)
        {
            Send(new[] { new INPUT { type = INPUT_MOUSE,
                U = new INPUTUNION { mi = new MOUSEINPUT { dwFlags = up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN } } } });
        }

        internal void ClearFocusedTextFromProof(VanillaVisualInputProof proof, System.Action verifyFieldFocus)
        {
            lock (ForegroundGate)
            {
                System.Action verify = FocusedProofGuard(proof, verifyFieldFocus);
                verify();
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " verified-capture field clear: Home, Shift+End, Backspace.");
                DispatchGuardedKey(Keys.Home, verify, SendKey, Thread.Sleep);
                DispatchGuardedChord(false, false, true, Keys.End, verify, SendKey, Thread.Sleep);
                DispatchGuardedKey(Keys.Back, verify, SendKey, Thread.Sleep);
                DelayWithCancellation(80);
            }
        }

        internal void SelectFocusedTextFromProof(VanillaVisualInputProof proof, System.Action verifyFieldFocus)
        {
            lock (ForegroundGate)
            {
                System.Action verify = FocusedProofGuard(proof, verifyFieldFocus);
                DispatchGuardedKey(Keys.Home, verify, SendKey, Thread.Sleep);
                DispatchGuardedChord(false, false, true, Keys.End, verify, SendKey, Thread.Sleep);
            }
        }

        internal void TypeTextFromProof(string text, VanillaVisualInputProof proof, System.Action verifyFieldFocus)
        {
            lock (ForegroundGate)
            {
                System.Action verify = FocusedProofGuard(proof, verifyFieldFocus);
                verify();
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " verified-capture text entry; contents omitted.");
                foreach (char value in text ?? string.Empty)
                {
                    verify();
                    SendUnicode(value, false);
                    try { Thread.Sleep(22); }
                    finally { SendUnicode(value, true); }
                }
                DelayWithCancellation(80);
            }
        }

        private System.Action FocusedProofGuard(VanillaVisualInputProof proof, System.Action verifyFieldFocus)
        {
            return () =>
            {
                VerifyCaptureProof(proof);
                if (verifyFieldFocus != null) verifyFieldFocus();
                VerifyCaptureProof(proof);
            };
        }

        internal void ReplaceFocusedTextFromProof(string text, VanillaVisualInputProof proof, System.Action verifyFieldFocus)
        {
            lock (ForegroundGate)
            {
                System.Action verify = () =>
                {
                    VerifyCaptureProof(proof);
                    if (verifyFieldFocus != null) verifyFieldFocus();
                    VerifyCaptureProof(proof);
                };
                verify();
                VanillaDebugLog.Write("INPUT", "PID=" + process.Id + " verified-capture credential replacement: Ctrl+A, Backspace, text; contents omitted.");
                DispatchGuardedChord(true, false, false, Keys.A, verify, SendKey, Thread.Sleep);
                DispatchGuardedKey(Keys.Back, verify, SendKey, Thread.Sleep);
                foreach (char value in text ?? string.Empty)
                {
                    verify();
                    SendUnicode(value, false);
                    try { Thread.Sleep(22); }
                    finally { SendUnicode(value, true); }
                }
                DelayWithCancellation(80);
            }
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
            lock (ForegroundGate)
            {
                SelectAll();
                Press(Keys.Back);
                TypeTextCore(text ?? string.Empty);
            }
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

        internal struct ScanCodeKey
        {
            internal readonly ushort ScanCode;
            internal readonly uint Flags;
            internal ScanCodeKey(ushort scanCode, uint flags) { ScanCode = scanCode; Flags = flags; }
        }

        internal static ScanCodeKey EncodeScanCodeKey(Keys key, bool up, Func<uint, uint, uint> map = null)
        {
            uint virtualKey = (uint)key;
            if (virtualKey == 0 || virtualKey > 0xff)
                throw new ArgumentOutOfRangeException(nameof(key), "A single supported virtual key is required; no input sent.");
            // Preserve the E0 prefix. Discarding it turns navigation keys such as
            // Left and End into their keypad equivalents before the game sees them.
            uint mapped = (map ?? MapVirtualKey)(virtualKey, MAPVK_VK_TO_VSC_EX);
            uint prefix = mapped & 0xff00;
            if ((mapped & 0xff) == 0 || (mapped & 0xffff0000) != 0 || (prefix != 0 && prefix != 0xe000))
                throw new InvalidOperationException("The selected key has no supported scan-code sequence; no input sent.");
            // E1 sequences (notably Pause) need a separate multi-code protocol;
            // never silently treat them as ordinary or E0 key strokes.
            // Some Windows keyboard layouts return the bare keypad scan even in
            // EX mode. The navigation virtual key still identifies an E0 key.
            bool extended = prefix == 0xe000 || IsExtendedNavigationKey(key);
            return new ScanCodeKey((ushort)(mapped & 0xff), KEYEVENTF_SCANCODE
                | (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0));
        }

        private static bool IsExtendedNavigationKey(Keys key)
        {
            switch (key)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                case Keys.Home: case Keys.End: case Keys.Insert: case Keys.Delete:
                case Keys.PageUp: case Keys.PageDown:
                    return true;
                default:
                    return false;
            }
        }

        private void SendKey(Keys key, bool up)
        {
            ScanCodeKey encoded = EncodeScanCodeKey(key, up);
            Send(new[]
            {
                new INPUT
                {
                    type = INPUT_KEYBOARD,
                    U = new INPUTUNION
                    {
                        ki = new KEYBDINPUT { wVk = 0, wScan = encoded.ScanCode, dwFlags = encoded.Flags }
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
