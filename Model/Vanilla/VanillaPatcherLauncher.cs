using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Starts the user-configured Vanilla launcher. For patcher.exe / Vanilla Launcher.exe
    /// it visually locates the yellow GAME START control when possible, performs ordinary
    /// Windows input, and waits for a new Vanilla MMO process.
    /// It never inspects/modifies Gepard or game memory.
    /// </summary>
    internal static class VanillaPatcherLauncher
    {
        internal const double DefaultGameStartX = 0.50;
        internal const double DefaultGameStartY = 0.765;
        internal const int DefaultStartTimeoutMs = 120000;
        internal const int DefaultRetryMs = 15000;
        internal const int LauncherSettleMs = 2500;
        internal const int VisualConfirmationDelayMs = 750;
        internal const int MaximumUpdateWaitMs = 600000;
        private sealed class UpdateResetCompletedException : Exception { }

        internal static string RequireLauncher(string executablePath)
        {
            string resolved = IsPatcher(executablePath) ? executablePath : PreferPatcherBesideClient(executablePath);
            if (!IsPatcher(resolved))
                throw new InvalidOperationException("Vanilla must start through Vanilla Launcher.exe or patcher.exe so updates can finish. No direct game fallback is allowed.");
            return Path.GetFullPath(resolved);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public UIntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public POINT pt;
        }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG message, IntPtr hwnd, uint min, uint max, uint remove);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeout, out UIntPtr result);

        private const uint WM_MOUSEMOVE = 0x0200;
        private const uint WM_LBUTTONDOWN = 0x0201;
        private const uint WM_LBUTTONUP = 0x0202;
        private const uint BM_CLICK = 0x00F5;
        private const uint SMTO_ABORTIFHUNG = 0x0002;
        private const int SW_RESTORE = 9;
        internal const int LauncherActivationTimeoutMs = 3000;

        internal static bool IsPatcher(string executablePath)
        {
            string name = Path.GetFileName(executablePath);
            return string.Equals(name, "patcher.exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "Vanilla Launcher.exe", StringComparison.OrdinalIgnoreCase);
        }

        internal static int? Launch(
            string executablePath,
            string arguments,
            Action<string> log,
            Func<bool> cancelled,
            int timeoutMs = DefaultStartTimeoutMs,
            int retryMs = DefaultRetryMs,
            double gameStartX = DefaultGameStartX,
            double gameStartY = DefaultGameStartY,
            string debugDirectory = null,
            Func<VanillaLauncherUpdateProcess, Func<bool>, bool> recoverUpdate = null,
            Func<Func<Process>, Process> startOwned = null)
        {
            executablePath = RequireLauncher(executablePath);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    return LaunchAttempt(executablePath, arguments, log, cancelled, timeoutMs, retryMs,
                        gameStartX, gameStartY, debugDirectory, attempt == 0 ? recoverUpdate : null, startOwned);
                }
                catch (UpdateResetCompletedException)
                {
                    log?.Invoke("event=update-reset-complete All same-installation clients and patchers exited. Restarting the launcher; direct game launch remains forbidden.");
                }
            }
            throw new InvalidOperationException("Launcher update recovery exceeded its single-reset budget.");
        }

        private static int? LaunchAttempt(string executablePath, string arguments, Action<string> log,
            Func<bool> cancelled, int timeoutMs, int retryMs, double gameStartX, double gameStartY,
            string debugDirectory, Func<VanillaLauncherUpdateProcess, Func<bool>, bool> recoverUpdate,
            Func<Func<Process>, Process> startOwned)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
                throw new FileNotFoundException("The configured Vanilla launcher does not exist.", executablePath);
            if (timeoutMs < 5000 || timeoutMs > 600000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (retryMs < 500 || retryMs > 30000) throw new ArgumentOutOfRangeException(nameof(retryMs));
            if (gameStartX < 0 || gameStartX > 1 || gameStartY < 0 || gameStartY > 1)
                throw new ArgumentOutOfRangeException("GAME START coordinates must be normalized to 0..1.");

            if (cancelled != null && cancelled()) throw new OperationCanceledException("Launcher start cancelled.");
            var before = new HashSet<int>(GetVanillaProcessIds());
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = arguments ?? string.Empty,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = true
            };

            Process launched = null;
            try
            {
                launched = startOwned == null ? Process.Start(startInfo)
                    : startOwned(() => Process.Start(startInfo));
                log?.Invoke("Launcher start requested: exe='" + Path.GetFileName(executablePath) + "', startedPID="
                    + (launched == null ? "none" : launched.Id.ToString()) + ", preExistingVanillaPIDs=[" + string.Join(",", before.OrderBy(v => v))
                    + "], timeout=" + timeoutMs + "ms, retry=" + retryMs + "ms.");
                log?.Invoke("Started Vanilla launcher; locating GAME START.");
                var clock = Stopwatch.StartNew();
                TimeSpan deadline = TimeSpan.FromMilliseconds(timeoutMs);
                var patchWatch = new VanillaLauncherPatchWatch();
                string lastPatchSignature = null;
                DateTime nextClick = DateTime.MinValue;
                string launcherName = Path.GetFileNameWithoutExtension(executablePath);
                string launcherDirectory = Path.GetDirectoryName(executablePath);
                int clickAttempt = 0;
                bool sawLauncherWindow = false;
                DateTime? launcherWindowLostAt = null;
                DateTime? launcherWindowStableAt = null;
                int stableLauncherPid = 0;
                int noWindowLogs = 0;

                while (clock.Elapsed < deadline)
                {
                    if (cancelled != null && cancelled())
                    {
                        log?.Invoke("Patcher launch cancelled.");
                        return null;
                    }

                    int? vanillaPid = FindNewVanillaProcess(before, launcherDirectory);
                    if (vanillaPid.HasValue)
                    {
                        log?.Invoke("Patcher started Vanilla MMO (PID " + vanillaPid.Value + ").");
                        return vanillaPid;
                    }

                    if (DateTime.UtcNow >= nextClick)
                    {
                        int? patcherPid = FindLauncherWindowProcessId(launched, launcherName, launcherDirectory);
                        if (patcherPid.HasValue)
                        {
                            sawLauncherWindow = true;
                            launcherWindowLostAt = null;
                            if (stableLauncherPid != patcherPid.Value)
                            {
                                stableLauncherPid = patcherPid.Value;
                                launcherWindowStableAt = DateTime.UtcNow;
                                log?.Invoke("Launcher window appeared for PID " + patcherPid.Value
                                    + "; waiting " + LauncherSettleMs + "ms before any GAME START action.");
                                nextClick = DateTime.UtcNow.AddMilliseconds(250);
                                continue;
                            }
                            if (!launcherWindowStableAt.HasValue
                                || (DateTime.UtcNow - launcherWindowStableAt.Value).TotalMilliseconds < LauncherSettleMs)
                            {
                                nextClick = DateTime.UtcNow.AddMilliseconds(250);
                                continue;
                            }
                            try
                            {
                                IntPtr launcherHwnd = ResolveLauncherWindow(patcherPid.Value);
                                if (launcherHwnd == IntPtr.Zero) throw new InvalidOperationException("No visible launcher top-level window was found for PID " + patcherPid.Value + ".");
                                string windowDescription = DescribeWindow(launcherHwnd);
                                string childInventory;
                                IntPtr nativeGameStart;
                                bool nativeFound = TryFindNativeGameStart(launcherHwnd, out nativeGameStart, out childInventory);
                                log?.Invoke("Launcher window resolved: " + windowDescription + "; foreground=" + DescribeWindow(GetForegroundWindow())
                                    + "; childControls=" + childInventory + ".");

                                string activationEvidence;
                                if (!TryActivateLauncherWindow(patcherPid.Value, launcherHwnd, cancelled, out activationEvidence))
                                {
                                    log?.Invoke("Launcher is visible but Windows foreground activation is not ready; no GAME START action sent. "
                                        + activationEvidence);
                                    patchWatch.Reset();
                                    nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                                    continue;
                                }
                                log?.Invoke("Launcher foreground verified before GAME START detection. " + activationEvidence);

                                clickAttempt++;
                                if (nativeFound)
                                {
                                    patchWatch.Reset();
                                    if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                    UIntPtr result;
                                    IntPtr sent = SendMessageTimeout(nativeGameStart, BM_CLICK, IntPtr.Zero, IntPtr.Zero, SMTO_ABORTIFHUNG, 2000, out result);
                                    int error = sent == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                                    log?.Invoke("GAME START attempt #" + clickAttempt + ": one semantic native-control invoke only; target="
                                        + DescribeWindow(nativeGameStart) + "; result=" + (sent == IntPtr.Zero ? "FAILED" : "OK")
                                        + "; err=" + error + ". No second click is sent in this attempt; waiting " + retryMs + "ms before any retry.");
                                }
                                else
                                {
                                    double firstX = gameStartX, firstY = gameStartY;
                                    string firstEvidence;
                                    if (!TryFindGameStartOnWindow(patcherPid.Value, debugDirectory, out firstX, out firstY, out firstEvidence))
                                    {
                                        clickAttempt--;
                                        log?.Invoke("GAME START is not safely detected yet; no fallback coordinate click was sent. " + firstEvidence);
                                        var frame = ObservePatchFrame(patcherPid.Value, launcherHwnd);
                                        var blocked = VanillaLauncherUpdateProcess.Read(patcherPid.Value);
                                        bool stalled = patchWatch.Observe(patcherPid.Value, launcherHwnd, frame, clock.Elapsed, blocked.StartedUtc.Ticks);
                                        if (frame != null && lastPatchSignature != frame.Signature)
                                        {
                                            deadline = TimeSpan.FromMilliseconds(Math.Min(MaximumUpdateWaitMs, clock.ElapsedMilliseconds + timeoutMs));
                                            log?.Invoke("event=launcher-updating Update progress/status observed; waiting for GAME START (maximum 10 minutes).");
                                        }
                                        lastPatchSignature = frame?.Signature;
                                        if (stalled && recoverUpdate != null)
                                        {
                                            bool recovered = recoverUpdate(blocked, () =>
                                            {
                                                if (cancelled != null && cancelled()) throw new OperationCanceledException();
                                                if (FindNewVanillaProcess(before, launcherDirectory).HasValue) return false;
                                                var fresh = ObservePatchFrame(blocked.Pid, launcherHwnd);
                                                return fresh != null && fresh.Signature == frame.Signature;
                                            });
                                            if (recovered) throw new UpdateResetCompletedException();
                                            patchWatch.Reset();
                                        }
                                        nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                                        continue;
                                    }
                                    patchWatch.Reset();
                                    SleepCancellable(VisualConfirmationDelayMs, cancelled);
                                    int? startedDuringConfirmation = FindNewVanillaProcess(before, launcherDirectory);
                                    if (startedDuringConfirmation.HasValue)
                                    {
                                        log?.Invoke("Patcher started Vanilla MMO (PID " + startedDuringConfirmation.Value + ") during GAME START confirmation.");
                                        return startedDuringConfirmation;
                                    }
                                    double secondX = gameStartX, secondY = gameStartY;
                                    string secondEvidence;
                                    if (!TryFindGameStartOnWindow(patcherPid.Value, debugDirectory, out secondX, out secondY, out secondEvidence)
                                        || !SameGameStartCandidate(firstX, firstY, secondX, secondY))
                                    {
                                        clickAttempt--;
                                        log?.Invoke("GAME START visual candidate changed during confirmation; no click sent. first=["
                                            + firstEvidence + "]; second=[" + secondEvidence + "].");
                                        nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                                        continue;
                                    }
                                    // The button location came from two stable launcher-client captures. Send the one
                                    // click directly to that verified launcher HWND instead of depending on global cursor focus.
                                    string inputEvidence = ClickTargetedWindowAtPoint(patcherPid.Value, secondX, secondY);
                                    log?.Invoke(string.Format(
                                        "GAME START attempt #{0}: one visually confirmed launcher-window message at normalized=({1:0.000},{2:0.000}); first=[{3}]; second=[{4}]; input=[{5}]. No cursor movement or foreground-dependent SendInput was used. Waiting {6}ms before any retry.",
                                        clickAttempt, secondX, secondY, firstEvidence, secondEvidence, inputEvidence, retryMs));
                                }
                            }
                            catch (UpdateResetCompletedException) { throw; }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex)
                            {
                                patchWatch.Reset();
                                log?.Invoke("Launcher GAME START attempt failed before completion: " + ex.GetType().Name + ": " + ex.Message);
                            }
                            nextClick = DateTime.UtcNow.AddMilliseconds(retryMs);
                        }
                        else
                        {
                            patchWatch.Reset();
                            stableLauncherPid = 0;
                            launcherWindowStableAt = null;
                            if (sawLauncherWindow)
                            {
                                if (!launcherWindowLostAt.HasValue) launcherWindowLostAt = DateTime.UtcNow;
                                else if ((DateTime.UtcNow - launcherWindowLostAt.Value).TotalMilliseconds >= 2500)
                                {
                                    log?.Invoke("Launcher window was closed before Vanilla started; stopping this test/recovery launch attempt.");
                                    throw new OperationCanceledException("Vanilla launcher window was closed.");
                                }
                            }
                            noWindowLogs++;
                            if (noWindowLogs <= 5 || noWindowLogs % 5 == 0)
                                log?.Invoke("Launcher process exists but no usable launcher window is visible yet. "
                                    + DescribeLauncherCandidates(launched, launcherName, launcherDirectory));
                            nextClick = DateTime.UtcNow.AddMilliseconds(1000);
                        }
                    }

                    Thread.Sleep(250);
                }

                throw new TimeoutException("Vanilla launcher did not start a new Vanilla MMO client within the bounded wait ("
                    + (int)clock.Elapsed.TotalSeconds + " seconds). No direct-game fallback was used.");
            }
            finally
            {
                if (launched != null) launched.Dispose();
            }
        }


        internal static bool TryActivateLauncherWindow(int processId, IntPtr hwnd, Func<bool> cancelled, out string evidence)
        {
            evidence = "launcher activation not attempted";
            if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd))
            {
                evidence = "launcher HWND is missing or not visible";
                return false;
            }
            uint ownerPid;
            uint targetThread = GetWindowThreadProcessId(hwnd, out ownerPid);
            if (ownerPid != (uint)processId || targetThread == 0)
            {
                evidence = "launcher HWND ownership mismatch; expected PID=" + processId + ", actualPID=" + ownerPid;
                return false;
            }
            if (cancelled != null && cancelled()) throw new OperationCanceledException("Patcher launch cancelled.");

            IntPtr before = GetForegroundWindow();
            if (before == hwnd)
            {
                evidence = "already foreground; target=" + DescribeWindow(hwnd);
                return true;
            }

            // Windows foreground-lock rules can reject SetForegroundWindow when the desktop or
            // another application owns the input queue. Temporarily attach only the involved
            // GUI input queues, activate the verified launcher HWND, then detach immediately.
            MSG message;
            PeekMessage(out message, IntPtr.Zero, 0, 0, 0); // ensure this worker owns a message queue
            uint currentThread = GetCurrentThreadId();
            uint foregroundPid;
            uint foregroundThread = before == IntPtr.Zero ? 0 : GetWindowThreadProcessId(before, out foregroundPid);
            bool attachedTarget = false, attachedForeground = false;
            int targetAttachError = 0, foregroundAttachError = 0;
            bool top = false, foregroundRequested = false;
            try
            {
                if (currentThread != targetThread)
                {
                    attachedTarget = AttachThreadInput(currentThread, targetThread, true);
                    if (!attachedTarget) targetAttachError = Marshal.GetLastWin32Error();
                }
                if (foregroundThread != 0 && foregroundThread != currentThread && foregroundThread != targetThread)
                {
                    attachedForeground = AttachThreadInput(currentThread, foregroundThread, true);
                    if (!attachedForeground) foregroundAttachError = Marshal.GetLastWin32Error();
                }
                bool targetQueueReady = currentThread == targetThread || attachedTarget;
                bool foregroundQueueReady = foregroundThread == 0 || foregroundThread == currentThread
                    || foregroundThread == targetThread || attachedForeground;
                if (!targetQueueReady || !foregroundQueueReady)
                {
                    evidence = "foreground input queues could not be safely joined; currentThread=" + currentThread
                        + ", targetThread=" + targetThread + ", foregroundThread=" + foregroundThread
                        + ", attachTarget=" + attachedTarget + " err=" + targetAttachError
                        + ", attachForeground=" + attachedForeground + " err=" + foregroundAttachError
                        + ". No foreground request or GAME START action sent.";
                    return false;
                }

                if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
                top = BringWindowToTop(hwnd);
                SetActiveWindow(hwnd);
                SetFocus(hwnd);
                foregroundRequested = SetForegroundWindow(hwnd);

                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < LauncherActivationTimeoutMs)
                {
                    if (cancelled != null && cancelled()) throw new OperationCanceledException("Patcher launch cancelled.");
                    if (!IsWindow(hwnd) || !IsWindowVisible(hwnd)) break;
                    if (GetForegroundWindow() == hwnd)
                    {
                        evidence = "foreground acquired in " + watch.ElapsedMilliseconds + "ms; target=" + DescribeWindow(hwnd)
                            + ", before=" + DescribeWindow(before) + ", BringWindowToTop=" + top
                            + ", SetForegroundWindow=" + foregroundRequested + ", attachedTarget=" + attachedTarget
                            + ", attachedForeground=" + attachedForeground + ".";
                        return true;
                    }
                    Thread.Sleep(50);
                }
            }
            finally
            {
                if (attachedForeground) AttachThreadInput(currentThread, foregroundThread, false);
                if (attachedTarget) AttachThreadInput(currentThread, targetThread, false);
            }

            evidence = "foreground activation timed out; target=" + DescribeWindow(hwnd)
                + ", before=" + DescribeWindow(before) + ", after=" + DescribeWindow(GetForegroundWindow())
                + ", BringWindowToTop=" + top + ", SetForegroundWindow=" + foregroundRequested
                + ", attachTarget=" + attachedTarget + " err=" + targetAttachError
                + ", attachForeground=" + attachedForeground + " err=" + foregroundAttachError + ".";
            return false;
        }

        internal static bool SameGameStartCandidate(double firstX, double firstY, double secondX, double secondY)
        {
            return Math.Abs(firstX - secondX) <= 0.025 && Math.Abs(firstY - secondY) <= 0.025;
        }

        private static void SleepCancellable(int milliseconds, Func<bool> cancelled)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                if (cancelled != null && cancelled()) throw new OperationCanceledException("Patcher launch cancelled.");
                int slice = Math.Min(100, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        /// <summary>
        /// Finds the active yellow GAME START surface in the lower-center area of a launcher
        /// snapshot. The detector intentionally ignores the green WEBSITE/WIKI/etc. buttons.
        /// </summary>
        internal static bool TryFindGameStart(Bitmap bitmap, out double x, out double y, out string evidence)
        {
            x = DefaultGameStartX;
            y = DefaultGameStartY;
            evidence = "no yellow GAME START candidate";
            if (bitmap == null || bitmap.Width < 200 || bitmap.Height < 120)
            {
                evidence = bitmap == null ? "bitmap is null" : "bitmap too small: " + bitmap.Width + "x" + bitmap.Height;
                return false;
            }

            int minScanX = (int)(bitmap.Width * 0.30);
            int maxScanX = (int)(bitmap.Width * 0.70);
            int minScanY = (int)(bitmap.Height * 0.63);
            int maxScanY = (int)(bitmap.Height * 0.90);
            int minX = bitmap.Width, minY = bitmap.Height, maxX = -1, maxY = -1, count = 0;

            for (int py = minScanY; py < maxScanY; py++)
            {
                for (int px = minScanX; px < maxScanX; px++)
                {
                    Color c = bitmap.GetPixel(px, py);
                    if (!IsGameStartYellow(c)) continue;
                    count++;
                    if (px < minX) minX = px;
                    if (px > maxX) maxX = px;
                    if (py < minY) minY = py;
                    if (py > maxY) maxY = py;
                }
            }

            if (count < 90 || maxX <= minX || maxY <= minY)
            {
                evidence = "yellow detector found " + count + " matching pixels in bitmap " + bitmap.Width + "x" + bitmap.Height
                    + " scan=[" + minScanX + "," + minScanY + ".." + maxScanX + "," + maxScanY + "]";
                return false;
            }
            double widthRatio = (double)(maxX - minX + 1) / bitmap.Width;
            double heightRatio = (double)(maxY - minY + 1) / bitmap.Height;
            double centerX = ((minX + maxX) / 2.0) / bitmap.Width;
            double centerY = ((minY + maxY) / 2.0) / bitmap.Height;

            if (widthRatio < 0.08 || heightRatio < 0.02 || centerX < 0.38 || centerX > 0.62 || centerY < 0.68 || centerY > 0.88)
            {
                evidence = string.Format("yellow candidate rejected: center=({0:0.000},{1:0.000}), size={2:0.000}x{3:0.000}, pixels={4}, bitmap={5}x{6}",
                    centerX, centerY, widthRatio, heightRatio, count, bitmap.Width, bitmap.Height);
                return false;
            }

            x = centerX;
            y = centerY;
            evidence = string.Format("visual yellow-button detector, center=({0:0.000},{1:0.000}), box={2}x{3}, pixels={4}, bitmap={5}x{6}",
                centerX, centerY, maxX - minX + 1, maxY - minY + 1, count, bitmap.Width, bitmap.Height);
            return true;
        }

        private static bool IsGameStartYellow(Color c)
        {
            return c.R >= 220
                && c.G >= 135 && c.G <= 235
                && c.B <= 120
                && c.R - c.B >= 105
                && c.G - c.B >= 45;
        }

        private static bool TryFindGameStartOnWindow(int processId, string debugDirectory, out double x, out double y, out string evidence)
        {
            x = DefaultGameStartX;
            y = DefaultGameStartY;
            evidence = "window capture unavailable";
            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    process.Refresh();
                    IntPtr hwnd = ResolveLauncherWindow(processId);
                    if (hwnd == IntPtr.Zero) { evidence = "main window handle is zero"; return false; }
                    RECT rect;
                    if (!GetClientRect(hwnd, out rect)) { evidence = "GetClientRect failed err=" + Marshal.GetLastWin32Error(); return false; }
                    int width = rect.Right - rect.Left;
                    int height = rect.Bottom - rect.Top;
                    if (width < 200 || height < 120) { evidence = "client too small: " + width + "x" + height; return false; }

                    string printEvidence = "PrintWindow not attempted";
                    using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                    {
                        using (var graphics = Graphics.FromImage(bitmap))
                        {
                            IntPtr hdc = graphics.GetHdc();
                            bool captured;
                            int captureError;
                            try { captured = PrintWindow(hwnd, hdc, 1); captureError = captured ? 0 : Marshal.GetLastWin32Error(); }
                            finally { graphics.ReleaseHdc(hdc); }
                            SaveDebugBitmap(debugDirectory, "launcher-print-last.png", bitmap);
                            double printX, printY;
                            string detector = captured ? "detector not yet evaluated" : "PrintWindow failed before detector";
                            if (captured && TryFindGameStart(bitmap, out printX, out printY, out detector))
                            {
                                x = printX; y = printY;
                                evidence = "PrintWindow OK; " + detector + DebugCaptureSuffix(debugDirectory, "launcher-print-last.png");
                                return true;
                            }
                            printEvidence = "PrintWindow=" + captured + " err=" + captureError + "; detector=" + detector;
                        }

                        var origin = new POINT { X = 0, Y = 0 };
                        if (!ClientToScreen(hwnd, ref origin))
                        {
                            evidence = printEvidence + "; ClientToScreen failed err=" + Marshal.GetLastWin32Error();
                            return false;
                        }
                        using (var graphics = Graphics.FromImage(bitmap))
                            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
                        SaveDebugBitmap(debugDirectory, "launcher-screen-last.png", bitmap);
                        string screenEvidence;
                        if (!TryFindGameStart(bitmap, out x, out y, out screenEvidence))
                        {
                            evidence = printEvidence + "; visibleScreen detector=" + screenEvidence + "; origin=(" + origin.X + "," + origin.Y + ")"
                                + DebugCaptureSuffix(debugDirectory, "launcher-screen-last.png");
                            return false;
                        }
                        evidence = printEvidence + "; visibleScreen OK; " + screenEvidence + "; origin=(" + origin.X + "," + origin.Y + ")"
                            + DebugCaptureSuffix(debugDirectory, "launcher-screen-last.png");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                evidence = "window capture failed: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        private static VanillaLauncherPatchFrame ObservePatchFrame(int pid, IntPtr hwnd)
        {
            try
            {
                uint owner;
                if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetForegroundWindow() != hwnd
                    || GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != (uint)pid
                    || !string.Equals(WindowClass(hwnd), "TThorForm", StringComparison.OrdinalIgnoreCase)) return null;
                RECT rect;
                if (!GetClientRect(hwnd, out rect)) return null;
                int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
                if (width < 200 || height < 120 || width > 4096 || height > 2160) return null;
                using (var image = new Bitmap(width, height, PixelFormat.Format24bppRgb))
                {
                    bool captured;
                    using (var graphics = Graphics.FromImage(image))
                    {
                        IntPtr dc = graphics.GetHdc();
                        try { captured = PrintWindow(hwnd, dc, 1); }
                        finally { graphics.ReleaseHdc(dc); }
                    }
                    var frame = captured ? VanillaLauncherPatchFrame.Read(image) : null;
                    if (frame != null) return frame;
                    var origin = new POINT();
                    if (!ClientToScreen(hwnd, ref origin) || GetForegroundWindow() != hwnd) return null;
                    foreach (int dx in new[] { width / 12, width / 2, width * 11 / 12 })
                    {
                        IntPtr at = WindowFromPoint(new POINT { X = origin.X + dx, Y = origin.Y + height * 93 / 100 });
                        if (at != hwnd && !IsChild(hwnd, at)) return null;
                    }
                    using (var graphics = Graphics.FromImage(image))
                        graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, new Size(width, height));
                    return GetForegroundWindow() == hwnd ? VanillaLauncherPatchFrame.Read(image) : null;
                }
            }
            catch { return null; } // Failed capture is unknown, never stalled-update evidence.
        }

        private static string ClickTargetedWindowAtPoint(int processId, double x, double y)
        {
            IntPtr main = ResolveLauncherWindow(processId);
            if (main == IntPtr.Zero || !IsWindow(main) || !IsWindowVisible(main))
                throw new InvalidOperationException("Launcher main window is no longer available.");
            uint ownerPid;
            GetWindowThreadProcessId(main, out ownerPid);
            if (ownerPid != (uint)processId)
                throw new InvalidOperationException("Launcher HWND ownership changed; no GAME START message sent.");
            RECT rect;
            if (!GetClientRect(main, out rect)) throw new InvalidOperationException("Cannot read launcher client rectangle; err=" + Marshal.GetLastWin32Error());
            int width = Math.Max(1, rect.Right - rect.Left), height = Math.Max(1, rect.Bottom - rect.Top);
            if (width < 200 || height < 120) throw new InvalidOperationException("Launcher client is too small for verified GAME START input.");
            var clientPoint = new POINT
            {
                X = Math.Max(0, Math.Min(width - 1, (int)Math.Round(x * width))),
                Y = Math.Max(0, Math.Min(height - 1, (int)Math.Round(y * height)))
            };
            IntPtr packed = new IntPtr((clientPoint.Y << 16) | (clientPoint.X & 0xFFFF));
            bool moved = PostMessage(main, WM_MOUSEMOVE, IntPtr.Zero, packed);
            bool down = PostMessage(main, WM_LBUTTONDOWN, new IntPtr(1), packed);
            Thread.Sleep(100);
            bool up = PostMessage(main, WM_LBUTTONUP, IntPtr.Zero, packed);
            int error = (!moved || !down || !up) ? Marshal.GetLastWin32Error() : 0;
            if (!moved || !down || !up)
                throw new InvalidOperationException("Launcher rejected the verified GAME START window message; err=" + error + ".");
            return "main=" + DescribeWindow(main) + "; ownerPID=" + ownerPid
                + "; client=" + width + "x" + height + "; targetClient=(" + clientPoint.X + "," + clientPoint.Y + ")"
                + "; foregroundAtMessage=" + DescribeWindow(GetForegroundWindow())
                + "; PostMessage(move/down/up)=" + moved + "/" + down + "/" + up + "; err=" + error;
        }

        private static bool TryFindNativeGameStart(IntPtr root, out IntPtr control, out string inventory)
        {
            IntPtr found = IntPtr.Zero;
            var rows = new List<string>();
            if (root == IntPtr.Zero) { control = IntPtr.Zero; inventory = "root=none"; return false; }
            EnumChildWindows(root, (hwnd, state) =>
            {
                string title = WindowTitle(hwnd);
                string cls = WindowClass(hwnd);
                if (rows.Count < 16) rows.Add("0x" + hwnd.ToInt64().ToString("X") + ":" + cls + ":'" + Clean(title) + "'");
                if (found == IntPtr.Zero && IsWindowVisible(hwnd) && IsWindowEnabled(hwnd)
                    && title.IndexOf("GAME START", StringComparison.OrdinalIgnoreCase) >= 0)
                    found = hwnd;
                return true;
            }, IntPtr.Zero);
            control = found;
            inventory = rows.Count == 0 ? "none" : string.Join(" | ", rows);
            return found != IntPtr.Zero;
        }

        private static IntPtr ResolveLauncherWindow(int processId)
        {
            IntPtr best = IntPtr.Zero;
            long bestScore = long.MinValue;
            EnumWindows((hwnd, state) =>
            {
                uint pid;
                GetWindowThreadProcessId(hwnd, out pid);
                if (pid != (uint)processId || !IsWindowVisible(hwnd)) return true;

                RECT rect;
                int width = 0, height = 0;
                if (GetClientRect(hwnd, out rect))
                {
                    width = Math.Max(0, rect.Right - rect.Left);
                    height = Math.Max(0, rect.Bottom - rect.Top);
                }

                string title = WindowTitle(hwnd);
                string cls = WindowClass(hwnd);
                long score = (long)width * height;
                if (title.IndexOf("Vanilla MMO Launcher", StringComparison.OrdinalIgnoreCase) >= 0) score += 1000000000L;
                if (cls.IndexOf("TThorForm", StringComparison.OrdinalIgnoreCase) >= 0) score += 500000000L;
                if (width < 100 || height < 100) score -= 100000000L;
                if (score > bestScore)
                {
                    best = hwnd;
                    bestScore = score;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static void SaveDebugBitmap(string debugDirectory, string fileName, Bitmap bitmap)
        {
            if (string.IsNullOrWhiteSpace(debugDirectory) || bitmap == null) return;
            try
            {
                Directory.CreateDirectory(debugDirectory);
                bitmap.Save(Path.Combine(debugDirectory, fileName), ImageFormat.Png);
            }
            catch { }
        }

        private static string DebugCaptureSuffix(string debugDirectory, string fileName)
        {
            if (string.IsNullOrWhiteSpace(debugDirectory)) return "";
            return "; capture='" + Path.Combine(debugDirectory, fileName) + "'";
        }

        internal static string PreferPatcherBesideClient(string clientExecutablePath)
        {
            if (string.IsNullOrWhiteSpace(clientExecutablePath)) return clientExecutablePath;
            string directory = Path.GetDirectoryName(clientExecutablePath);
            if (string.IsNullOrWhiteSpace(directory)) return clientExecutablePath;
            string vanillaLauncher = Path.Combine(directory, "Vanilla Launcher.exe");
            if (File.Exists(vanillaLauncher)) return vanillaLauncher;
            string patcher = Path.Combine(directory, "patcher.exe");
            return File.Exists(patcher) ? patcher : clientExecutablePath;
        }

        private static int[] GetVanillaProcessIds()
        {
            var processes = Process.GetProcessesByName("Vanilla MMO");
            try { return processes.Select(p => p.Id).ToArray(); }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        private static int? FindNewVanillaProcess(HashSet<int> before, string directory)
        {
            foreach (int pid in GetVanillaProcessIds())
            {
                if (before.Contains(pid)) continue;
                try
                {
                    var identity = VanillaLauncherUpdateProcess.Read(pid);
                    if (identity.Game && string.Equals(Path.GetDirectoryName(identity.Executable),
                        Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase)) return pid;
                }
                catch { /* Unverified identity never authorizes binding/input. */ }
            }
            return null;
        }

        private static int? FindLauncherWindowProcessId(Process launched, string launcherName, string launcherDirectory)
        {
            var candidates = GetLauncherCandidates(launched, launcherName);
            try
            {
                Process best = null;
                DateTime bestStart = DateTime.MinValue;
                foreach (var process in candidates.Values)
                {
                    try
                    {
                        process.Refresh();
                        IntPtr resolvedWindow = ResolveLauncherWindow(process.Id);
                        if (resolvedWindow == IntPtr.Zero || !IsWindowVisible(resolvedWindow)) continue;
                        if (!string.IsNullOrWhiteSpace(launcherDirectory))
                        {
                            try
                            {
                                string candidateDirectory = Path.GetDirectoryName(VanillaLauncherUpdateProcess.Read(process.Id).Executable);
                                if (!string.Equals(Path.GetFullPath(candidateDirectory), Path.GetFullPath(launcherDirectory), StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }
                            catch { continue; }
                        }
                        DateTime started = process.StartTime;
                        if (best == null || started > bestStart)
                        {
                            best = process;
                            bestStart = started;
                        }
                    }
                    catch { }
                }
                return best == null ? (int?)null : best.Id;
            }
            finally { foreach (var process in candidates.Values) process.Dispose(); }
        }

        private static Dictionary<int, Process> GetLauncherCandidates(Process launched, string launcherName)
        {
            var candidates = new Dictionary<int, Process>();
            Action<Process> add = process =>
            {
                if (process == null) return;
                if (!candidates.ContainsKey(process.Id)) candidates.Add(process.Id, process);
                else process.Dispose();
            };
            if (launched != null)
            {
                try { if (!launched.HasExited) add(Process.GetProcessById(launched.Id)); }
                catch { }
            }
            foreach (string processName in new[] { launcherName, "Vanilla Launcher", "patcher" }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                Process[] found;
                try { found = Process.GetProcessesByName(processName); }
                catch { continue; }
                foreach (var process in found) add(process);
            }
            return candidates;
        }

        private static string DescribeLauncherCandidates(Process launched, string launcherName, string launcherDirectory)
        {
            var candidates = GetLauncherCandidates(launched, launcherName);
            try
            {
                if (candidates.Count == 0) return "candidateProcesses=none";
                var rows = new List<string>();
                foreach (var process in candidates.Values)
                {
                    try
                    {
                        process.Refresh();
                        string path = "?";
                        try { path = process.MainModule.FileName; } catch { }
                        rows.Add("PID=" + process.Id + " exited=" + process.HasExited + " processMain=" + DescribeWindow(process.MainWindowHandle)
                            + " resolvedLauncher=" + DescribeWindow(ResolveLauncherWindow(process.Id))
                            + " path='" + path + "' expectedDir='" + launcherDirectory + "'");
                    }
                    catch (Exception ex) { rows.Add("PID=" + process.Id + " inspectError=" + ex.Message); }
                }
                return "candidates=[" + string.Join(" || ", rows) + "]";
            }
            finally { foreach (var process in candidates.Values) process.Dispose(); }
        }

        private static string DescribeWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "none";
            uint pid;
            GetWindowThreadProcessId(hwnd, out pid);
            return "0x" + hwnd.ToInt64().ToString("X") + " pid=" + pid + " visible=" + IsWindowVisible(hwnd)
                + " class='" + Clean(WindowClass(hwnd)) + "' title='" + Clean(WindowTitle(hwnd)) + "'";
        }

        private static string WindowTitle(IntPtr hwnd)
        {
            var value = new StringBuilder(512);
            try { GetWindowText(hwnd, value, value.Capacity); } catch { }
            return value.ToString();
        }

        private static string WindowClass(IntPtr hwnd)
        {
            var value = new StringBuilder(256);
            try { GetClassName(hwnd, value, value.Capacity); } catch { }
            return value.ToString();
        }

        private static string Clean(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
