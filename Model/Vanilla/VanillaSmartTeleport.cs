using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaSmartTeleportToken
    {
        internal string AccountId;
        internal int ProcessId;
        internal int Generation;
        internal VanillaReconnectAccount Account;
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private int smartTeleportGeneration;

        internal bool TryBeginSmartTeleport(int pid, out VanillaSmartTeleportToken token, out string reason)
        {
            token = null;
            reason = null;
            lock (gate)
            {
                if (disposed || !running) { reason = "reconnect supervision is not running"; return false; }
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                if (runtime == null) { reason = "the running process is not bound to an enabled character row"; return false; }
                if (FarmingEmergencyHeld(runtime))
                { reason = "the character is on a farming emergency hold"; return false; }
                if (!runtime.Account.SmartTeleportEnabled)
                { reason = "Smart Teleport is disabled for this character"; return false; }
                if (runtime.Account.SmartTeleportKey < 8 || runtime.Account.SmartTeleportKey > 254)
                { reason = "the character has no valid Smart Teleport hotkey"; return false; }
                if (runtime.Stage != VanillaReconnectStage.Online)
                { reason = "the character is not in the stable Online stage"; return false; }
                if (weightManualHolds.Contains(runtime.Account.Id))
                { reason = "the character is waiting for manual Cart attention"; return false; }
                if (runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery
                    || runtimes.Values.Any(item => !ReferenceEquals(item, runtime)
                        && (item.ScriptRunning || item.RecoveryOwned || item.ClosingForRecovery)))
                { reason = "another serialized startup/recovery/UI operation owns the input lease"; return false; }

                runtime.ScriptRunning = true;
                int generation = ++smartTeleportGeneration;
                token = new VanillaSmartTeleportToken
                {
                    AccountId = runtime.Account.Id,
                    ProcessId = pid,
                    Generation = generation,
                    Account = runtime.Account.Clone()
                };
                SetStage(runtime, VanillaReconnectStage.Online, "Smart Teleport owns the serialized background-input lease");
                return true;
            }
        }

        internal bool SmartTeleportCancelled(VanillaSmartTeleportToken token)
        {
            if (token == null) return true;
            lock (gate)
            {
                Runtime runtime;
                return disposed || !running || token.Generation != smartTeleportGeneration
                    || !runtimes.TryGetValue(token.AccountId, out runtime)
                    || runtime.ProcessId != token.ProcessId || !runtime.Account.Enabled
                    || !runtime.Account.SmartTeleportEnabled || runtime.Stage != VanillaReconnectStage.Online || FarmingEmergencyHeld(runtime)
                    || CharacterOwnershipChanged(runtime, token.ProcessId);
            }
        }

        internal void CompleteSmartTeleport(VanillaSmartTeleportToken token, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                if (token.Generation != smartTeleportGeneration) return;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                SetStage(runtime, VanillaReconnectStage.Online, detail ?? "Smart Teleport completed");
            }
            RaiseUpdated();
        }
    }

    internal sealed class VanillaSmartTeleportTracker
    {
        private bool baseline;
        private int pid, x, y;
        private Guid session;
        private string map;
        private TimeSpan unchangedSince;
        private DateTimeOffset? observedAt;

        internal void Reset(TimeSpan now)
        {
            baseline = false;
            observedAt = null;
            unchangedSince = now;
        }

        internal double StationarySeconds(TimeSpan now)
        {
            return baseline && now >= unchangedSince ? (now - unchangedSince).TotalSeconds : 0;
        }

        internal bool Observe(VanillaPositionSample sample, TimeSpan now, DateTimeOffset utc, int idleSeconds)
        {
            bool fresh = sample != null && sample.Verified && sample.Error == null
                && sample.Pid > 0 && sample.Session != Guid.Empty && sample.X.HasValue && sample.Y.HasValue
                && sample.At <= utc && utc - sample.At <= TimeSpan.FromSeconds(3)
                && (!observedAt.HasValue || sample.At > observedAt.Value);
            if (!fresh)
            {
                // Unknown/stale coordinates never count as stationary.
                Reset(now);
                return false;
            }

            bool identityChanged = baseline && (pid != sample.Pid || session != sample.Session
                || (map != null && sample.Map != null && !string.Equals(map, sample.Map, StringComparison.Ordinal)));
            if (!baseline || identityChanged)
            {
                pid = sample.Pid; session = sample.Session; map = sample.Map;
                x = sample.X.Value; y = sample.Y.Value;
                baseline = true; unchangedSince = now; observedAt = sample.At;
                return false;
            }

            bool intermediateMovement = sample.MovementAt.HasValue && observedAt.HasValue
                && sample.MovementAt.Value > observedAt.Value && sample.MovementAt.Value <= sample.At;
            if (sample.X.Value != x || sample.Y.Value != y || intermediateMovement)
                unchangedSince = now;

            x = sample.X.Value; y = sample.Y.Value; map = sample.Map ?? map; observedAt = sample.At;
            return now - unchangedSince >= TimeSpan.FromSeconds(idleSeconds);
        }
    }

    internal static class VanillaVerifiedTeleportAction
    {
        internal static bool TryExecute(int pid, VanillaReconnectAccount account, Func<bool> cancelled, string mode, out string detail)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            mode = string.IsNullOrWhiteSpace(mode) ? "verified" : mode;

            if (account.SmartTeleportKey < 8 || account.SmartTeleportKey > 254)
            {
                detail = "Smart Teleport recovery skipped: no teleport hotkey is configured";
                VanillaDebugLog.Write("TELEPORT", "event=teleport-skipped mode=" + mode + " account='" + account.Label
                    + "' accountId=" + account.Id + " pid=" + pid + " reason='teleport hotkey not configured'; inputSent=false.");
                return false;
            }

            using (var input = new VanillaBackgroundWindowInput(pid, cancelled))
            using (Bitmap before = input.CaptureClientBitmap())
            {
                if (VanillaTeleportVision.HasWarpDialog(null, before))
                {
                    detail = "Smart Teleport deferred because a warp dialog was already open";
                    VanillaDebugLog.Write("TELEPORT", "event=teleport-deferred mode=" + mode + " account='" + account.Label
                        + "' pid=" + pid + " reason='warp dialog already open before hotkey'; no input sent.");
                    return false;
                }

                VanillaDebugLog.Write("TELEPORT", "event=teleport-hotkey mode=" + mode + " account='" + account.Label
                    + "' pid=" + pid + " idleSeconds=" + account.SmartTeleportIdleSeconds
                    + " hotkey='" + account.SmartTeleportHotkeyText + "'.");
                input.Chord(account.SmartTeleportCtrl, account.SmartTeleportAlt,
                    account.SmartTeleportShift, (Keys)account.SmartTeleportKey);

                if (!WaitForWarpDialog(input, before, cancelled, 3000))
                {
                    detail = "Smart Teleport popup not verified; no Enter sent";
                    VanillaDebugLog.Write("TELEPORT", "event=teleport-failed mode=" + mode + " account='" + account.Label
                        + "' pid=" + pid + " stage=popup reason='expected warp popup not positively detected'; enterSent=false.");
                    return false;
                }

                using (Bitmap confirmation = input.CaptureClientBitmap())
                {
                    if (!VanillaTeleportVision.HasWarpDialog(null, confirmation))
                    {
                        detail = "Smart Teleport popup was no longer present; no Enter sent";
                        VanillaDebugLog.Write("TELEPORT", "event=teleport-failed mode=" + mode + " account='" + account.Label
                            + "' pid=" + pid + " stage=confirmation reason='warp popup disappeared'; enterSent=false.");
                        return false;
                    }
                }

                VanillaDebugLog.Write("TELEPORT", "event=teleport-enter mode=" + mode + " account='" + account.Label
                    + "' pid=" + pid + " popupConfirmed=true firstChoiceSelected=true.");
                input.Press(Keys.Enter);
                if (!WaitForWarpDialogGone(input, cancelled, 2500))
                {
                    detail = "Smart Teleport confirmation did not clear; no further input sent";
                    VanillaDebugLog.Write("TELEPORT", "event=teleport-failed mode=" + mode + " account='" + account.Label
                        + "' pid=" + pid + " stage=post-enter reason='warp popup remained visible'; no further input sent.");
                    return false;
                }

                detail = "Smart Teleport completed in background";
                VanillaDebugLog.Write("TELEPORT", "event=teleport-complete mode=" + mode + " account='" + account.Label
                    + "' accountId=" + account.Id + " pid=" + pid + " popupCleared=true.");
                return true;
            }
        }

        private static bool WaitForWarpDialog(VanillaBackgroundWindowInput input, Bitmap before, Func<bool> cancelled, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int consecutive = 0;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) throw new OperationCanceledException();
                using (Bitmap frame = input.CaptureClientBitmap())
                {
                    if (VanillaTeleportVision.HasWarpDialog(before, frame))
                    {
                        if (++consecutive >= 2) return true;
                    }
                    else consecutive = 0;
                }
                Thread.Sleep(150);
            }
            return false;
        }

        private static bool WaitForWarpDialogGone(VanillaBackgroundWindowInput input, Func<bool> cancelled, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int absent = 0;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) throw new OperationCanceledException();
                using (Bitmap frame = input.CaptureClientBitmap())
                {
                    if (!VanillaTeleportVision.HasWarpDialog(null, frame))
                    {
                        if (++absent >= 2) return true;
                    }
                    else absent = 0;
                }
                Thread.Sleep(150);
            }
            return false;
        }
    }

    internal sealed class VanillaSmartTeleportService : IDisposable
    {
        private sealed class CharacterState
        {
            internal readonly VanillaSmartTeleportTracker Tracker = new VanillaSmartTeleportTracker();
            internal bool Running;
        }

        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly Dictionary<string, CharacterState> states = new Dictionary<string, CharacterState>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private System.Threading.Timer timer;
        private int polling;
        private bool disposed;

        internal bool IsRunning { get { lock (gate) return timer != null && !disposed; } }

        internal VanillaSmartTeleportService(VanillaFleetMonitor fleet, VanillaReconnectSupervisor supervisor)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed || timer != null) return;
                timer = new System.Threading.Timer(_ => Poll(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
            VanillaDebugLog.Write("TELEPORT", "Per-character Smart Teleport service started. It uses verified X/Y only and never requires target/combat state.");
        }

        internal string RunNow(string accountId)
        {
            int pid;
            VanillaReconnectAccount account;
            string reason;
            if (!supervisor.TryResolveOnlineManagedCharacter(accountId, out pid, out account, out reason))
                throw new InvalidOperationException(reason);
            if (supervisor.FarmingEmergencyHeld(account))
                throw new InvalidOperationException("This character is on a farming emergency hold; Smart Teleport is blocked.");
            if (account.SmartTeleportKey < 8 || account.SmartTeleportKey > 254)
                throw new InvalidOperationException("Configure this character's Smart Teleport hotkey first.");

            CharacterState state = State(accountId);
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(VanillaSmartTeleportService));
                if (state.Running) throw new InvalidOperationException("Smart Teleport is already running for this character.");
                state.Running = true;
            }
            VanillaDebugLog.Write("TELEPORT", "event=teleport-manual-request account='" + account.Label
                + "' accountId=" + account.Id + " pid=" + pid + ".");
            ThreadPool.QueueUserWorkItem(_ => Execute(accountId, pid, state, true));
            return "Smart Teleport test queued for " + account.Label + " (PID " + pid + ").";
        }

        private void Poll()
        {
            if (disposed || Interlocked.Exchange(ref polling, 1) != 0) return;
            try
            {
                if (!supervisor.IsRunning)
                {
                    lock (gate) foreach (CharacterState value in states.Values) value.Tracker.Reset(clock.Elapsed);
                    return;
                }

                VanillaReconnectSettings settings = supervisor.Settings;
                VanillaReconnectAccount[] enabled = settings.Accounts.Where(a => a.Enabled && a.SmartTeleportEnabled).ToArray();
                var activeIds = new HashSet<string>(enabled.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);
                lock (gate)
                    foreach (string stale in states.Keys.Where(id => !activeIds.Contains(id)).ToArray()) states.Remove(stale);
                if (enabled.Length == 0) return;

                IReadOnlyList<VanillaReconnectStatus> statuses = supervisor.Statuses();
                IReadOnlyList<VanillaFleetClientInfo> clients = fleet.Poll();
                DateTimeOffset utc = DateTimeOffset.UtcNow;

                foreach (VanillaReconnectAccount account in enabled)
                {
                    if (supervisor.FarmingEmergencyHeld(account))
                    { State(account.Id).Tracker.Reset(clock.Elapsed); continue; }
                    VanillaReconnectStatus status = statuses.FirstOrDefault(s => string.Equals(s.AccountId, account.Id, StringComparison.OrdinalIgnoreCase));
                    if (status == null || !status.ProcessId.HasValue || status.Stage != VanillaReconnectStage.Online) continue;
                    VanillaFleetClientInfo client = clients.FirstOrDefault(item => item.ProcessId == status.ProcessId.Value);
                    if (client == null || client.Identity == null || client.Position == null
                        || !VanillaCharacterRoster.Matches(account, client.Identity, utc))
                    {
                        State(account.Id).Tracker.Reset(clock.Elapsed);
                        continue;
                    }

                    CharacterState state = State(account.Id);
                    bool due = state.Tracker.Observe(client.Position, clock.Elapsed, utc, account.SmartTeleportIdleSeconds);
                    if (!due || state.Running) continue;
                    state.Running = true;
                    ThreadPool.QueueUserWorkItem(_ => Execute(account.Id, status.ProcessId.Value, state, false));
                }
            }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("TELEPORT", "Smart Teleport poll failed safely: " + ex.Message);
            }
            finally { Interlocked.Exchange(ref polling, 0); }
        }

        private CharacterState State(string id)
        {
            lock (gate)
            {
                CharacterState value;
                if (!states.TryGetValue(id, out value)) states[id] = value = new CharacterState();
                return value;
            }
        }

        private void Execute(string accountId, int pid, CharacterState state, bool manual)
        {
            VanillaSmartTeleportToken token = null;
            string reason;
            string mode = manual ? "manual-test" : "automatic-idle";
            string completionDetail = "Smart Teleport cancelled";
            try
            {
                if (!supervisor.TryBeginSmartTeleport(pid, out token, out reason))
                {
                    VanillaDebugLog.Write("TELEPORT", "event=teleport-deferred mode=" + mode + " accountId=" + accountId
                        + " pid=" + pid + " reason='" + reason + "'.");
                    return;
                }

                VanillaDebugLog.Write("TELEPORT", "event=teleport-start mode=" + mode + " account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + " hotkey='" + token.Account.SmartTeleportHotkeyText + "'.");
                state.Tracker.Reset(clock.Elapsed);
                Func<bool> cancelled = () => supervisor.SmartTeleportCancelled(token);
                string detail;
                VanillaVerifiedTeleportAction.TryExecute(pid, token.Account, cancelled, mode, out detail);
                completionDetail = detail;
            }
            catch (OperationCanceledException)
            {
                VanillaDebugLog.Write("TELEPORT", "event=teleport-cancelled mode=" + mode + " account='"
                    + (token?.Account?.Label ?? accountId) + "' pid=" + pid
                    + " reason='STOP/settings/client ownership change'.");
            }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("TELEPORT", "event=teleport-failed mode=" + mode + " account='"
                    + (token?.Account?.Label ?? accountId) + "' pid=" + pid + " stage=exception reason='" + ex.Message
                    + "'; no blind Enter was sent.");
                completionDetail = "Smart Teleport failed safely: " + ex.Message;
            }
            finally
            {
                // A real owned attempt starts a new idle baseline. A deferred attempt did
                // not send input, so keep the existing due state and retry after contention.
                if (token != null)
                {
                    try { supervisor.CompleteSmartTeleport(token, completionDetail); }
                    catch (Exception ex) { VanillaDebugLog.Write("TELEPORT", "Teleport completion notification failed: " + ex.Message); }
                    state.Tracker.Reset(clock.Elapsed);
                }
                lock (gate) state.Running = false;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                timer?.Dispose();
                timer = null;
                states.Clear();
            }
            clock.Stop();
        }
    }

    internal sealed class VanillaBackgroundWindowInput : IDisposable
    {
        private const uint WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101;
        private const uint WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
        private const uint PW_CLIENTONLY = 0x00000001, PW_RENDERFULLCONTENT = 0x00000002;
        private readonly int pid;
        private readonly Func<bool> cancelled;
        private IntPtr window;

        private const int GWL_STYLE = -16, GWL_EXSTYLE = -20;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int Length, Flags, ShowCmd;
            public POINT MinPosition, MaxPosition;
            public RECT NormalPosition;
        }
        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT placement);
        [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] private static extern IntPtr GetMenu(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool AdjustWindowRectEx(ref RECT rect, int style, bool menu, int exStyle);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);

        internal VanillaBackgroundWindowInput(int pid, Func<bool> cancelled)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
            this.pid = pid;
            this.cancelled = cancelled ?? (() => false);
            window = ResolveWindow();
            int captureWidth, captureHeight;
            string captureSource;
            if (!TryGetCaptureSize(window, out captureWidth, out captureHeight, out captureSource))
                throw new InvalidOperationException("Owned Vanilla window was found, but no safe background capture size could be derived.");
            VanillaDebugLog.Write("TELEPORT", "Background input bound to PID=" + pid + ", hwnd=0x" + window.ToInt64().ToString("X")
                + ", capture=" + captureWidth + "x" + captureHeight + " source=" + captureSource
                + ", iconic=" + IsIconic(window) + ". It will not restore or foreground the game window.");
        }

        internal void Chord(bool ctrl, bool alt, bool shift, Keys key)
        {
            EnsureAllowed();
            var held = new Stack<Keys>();
            try
            {
                if (ctrl) { Key(Keys.ControlKey, false, false); held.Push(Keys.ControlKey); }
                if (alt) { Key(Keys.Menu, false, true); held.Push(Keys.Menu); }
                if (shift) { Key(Keys.ShiftKey, false, alt); held.Push(Keys.ShiftKey); }
                Thread.Sleep(55);
                Key(key, false, alt); Thread.Sleep(70); Key(key, true, alt);
            }
            finally
            {
                while (held.Count > 0)
                    try
                    {
                        Keys release = held.Pop();
                        Key(release, true, release == Keys.Menu || (alt && release != Keys.ControlKey));
                    }
                    catch { }
            }
            Thread.Sleep(90);
        }

        internal void Press(Keys key)
        {
            EnsureAllowed();
            Key(key, false, false); Thread.Sleep(70); Key(key, true, false); Thread.Sleep(90);
        }

        internal Bitmap CaptureClientBitmap()
        {
            EnsureAllowed();
            int width, height;
            string sizeSource;
            if (!TryGetCaptureSize(window, out width, out height, out sizeSource))
                throw new InvalidOperationException("Background Vanilla client size is unavailable; no teleport input continued.");

            var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        if (!PrintWindow(window, hdc, PW_CLIENTONLY | PW_RENDERFULLCONTENT))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not capture the background Vanilla client.");
                    }
                    finally { graphics.ReleaseHdc(hdc); }
                }
                if (!VanillaTeleportVision.FrameLooksUsable(bitmap))
                    throw new InvalidOperationException("Background window capture was blank/indeterminate; no teleport input continued.");
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private void Key(Keys key, bool up, bool system)
        {
            EnsureAllowed();
            int value = (int)key;
            if (value < 8 || value > 254) throw new ArgumentException("Invalid Smart Teleport virtual key.");
            uint scan = MapVirtualKey((uint)value, 4);
            uint flags = 1U | ((scan & 0xFF) << 16);
            if ((scan & 0xFF00) != 0) flags |= 1U << 24;
            if (system) flags |= 1U << 29; // Alt/context bit for WM_SYSKEY*.
            if (up) flags |= 0xC0000000U;
            uint message = system ? (up ? WM_SYSKEYUP : WM_SYSKEYDOWN) : (up ? WM_KEYUP : WM_KEYDOWN);
            if (!PostMessage(window, message, new IntPtr(value), new IntPtr(unchecked((int)flags))))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows rejected background key input.");
        }

        private void EnsureAllowed()
        {
            if (cancelled()) throw new OperationCanceledException();
            uint owner;
            if (window == IntPtr.Zero || !IsWindow(window) || GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)pid)
                throw new InvalidOperationException("The bound Vanilla window changed or disappeared.");
        }

        private IntPtr ResolveWindow()
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;
            EnumWindows(delegate(IntPtr hwnd, IntPtr state)
            {
                uint owner;
                if (GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != (uint)pid) return true;
                string cls = Text(hwnd, false), title = Text(hwnd, true);
                if (!VanillaForegroundInput.IsKnownVanillaGameWindow(cls, title)) return true;
                int width, height;
                string sizeSource;
                if (!TryGetCaptureSize(hwnd, out width, out height, out sizeSource)) return true;
                long area = (long)width * height + (IsWindowVisible(hwnd) ? 1000000000L : 0L)
                    + (IsIconic(hwnd) ? 0L : 500000000L);
                if (area > bestArea) { bestArea = area; best = hwnd; }
                return true;
            }, IntPtr.Zero);
            if (best == IntPtr.Zero)
                throw new InvalidOperationException("No owned Vanilla game window is available for background Smart Teleport.");
            return best;
        }

        internal static bool TryGetCaptureSize(IntPtr hwnd, out int width, out int height, out string source)
        {
            width = height = 0;
            source = "none";
            RECT client;
            int clientWidth = 0, clientHeight = 0;
            if (GetClientRect(hwnd, out client))
            {
                clientWidth = client.Right - client.Left;
                clientHeight = client.Bottom - client.Top;
            }
            if (TryResolveCaptureSize(clientWidth, clientHeight, 0, 0, 0, 0, out width, out height, out source))
                return true;

            // A minimized Vanilla top-level window reports a 0x0 client area on the user's
            // machine even though PostMessage/PrintWindow can still address that owned HWND.
            // WINDOWPLACEMENT retains the normal outer bounds. Subtract the current style's
            // non-client frame to recover the last normal client size without restoring,
            // foregrounding or moving the game window.
            var placement = new WINDOWPLACEMENT { Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(hwnd, ref placement)) return false;
            int outerWidth = placement.NormalPosition.Right - placement.NormalPosition.Left;
            int outerHeight = placement.NormalPosition.Bottom - placement.NormalPosition.Top;

            int frameWidth = 0, frameHeight = 0;
            RECT probe = new RECT { Left = 0, Top = 0, Right = 1000, Bottom = 1000 };
            int style = GetWindowLong(hwnd, GWL_STYLE);
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            if (AdjustWindowRectEx(ref probe, style, GetMenu(hwnd) != IntPtr.Zero, exStyle))
            {
                frameWidth = Math.Max(0, (probe.Right - probe.Left) - 1000);
                frameHeight = Math.Max(0, (probe.Bottom - probe.Top) - 1000);
            }
            return TryResolveCaptureSize(clientWidth, clientHeight, outerWidth, outerHeight,
                frameWidth, frameHeight, out width, out height, out source);
        }

        internal static bool TryResolveCaptureSize(int clientWidth, int clientHeight,
            int normalOuterWidth, int normalOuterHeight, int frameWidth, int frameHeight,
            out int width, out int height, out string source)
        {
            width = height = 0;
            source = "none";
            if (clientWidth >= 200 && clientHeight >= 120)
            {
                width = clientWidth;
                height = clientHeight;
                source = "client-rect";
                return true;
            }

            if (normalOuterWidth < 200 || normalOuterHeight < 120) return false;
            int derivedWidth = normalOuterWidth - Math.Max(0, frameWidth);
            int derivedHeight = normalOuterHeight - Math.Max(0, frameHeight);
            if (derivedWidth >= 200 && derivedHeight >= 120)
            {
                width = derivedWidth;
                height = derivedHeight;
                source = frameWidth > 0 || frameHeight > 0
                    ? "normal-placement-minus-frame" : "normal-placement-outer-fallback";
                return true;
            }

            // Conservative fallback: PrintWindow paints at the top-left. A small amount of
            // non-client slack is preferable to restoring a minimized client; visual validation
            // below still rejects blank/indeterminate captures before any teleport key is sent.
            width = normalOuterWidth;
            height = normalOuterHeight;
            source = "normal-placement-outer-fallback";
            return width >= 200 && height >= 120;
        }

        private static string Text(IntPtr hwnd, bool title)
        {
            var value = new System.Text.StringBuilder(title ? 256 : 128);
            if (title) GetWindowText(hwnd, value, value.Capacity); else GetClassName(hwnd, value, value.Capacity);
            return value.ToString().Replace("\r", " ").Replace("\n", " ").Trim();
        }

        public void Dispose() { window = IntPtr.Zero; }
    }

    internal static class VanillaTeleportVision
    {
        private sealed class Component
        {
            internal Rectangle Bounds;
            internal int Area;
        }

        internal static bool FrameLooksUsable(Bitmap frame)
        {
            if (frame == null || frame.Width < 200 || frame.Height < 120) return false;
            VanillaInventoryVision.PixelBuffer pixels = VanillaInventoryVision.PixelBuffer.Read(frame);
            int min = 255, max = 0, nonBlack = 0, total = 0;
            int stepX = Math.Max(1, frame.Width / 40), stepY = Math.Max(1, frame.Height / 30);
            for (int y = 0; y < frame.Height; y += stepY)
            for (int x = 0; x < frame.Width; x += stepX)
            {
                VanillaInventoryVision.PixelInfo p = pixels.At(x, y);
                int lum = (p.R + p.G + p.B) / 3;
                min = Math.Min(min, lum); max = Math.Max(max, lum);
                if (lum > 12) nonBlack++;
                total++;
            }
            return max - min >= 18 && total > 0 && nonBlack >= total / 10;
        }

        internal static bool HasWarpDialog(Bitmap before, Bitmap after)
        {
            if (after == null || after.Width < 300 || after.Height < 200) return false;
            VanillaInventoryVision.PixelBuffer pixels = VanillaInventoryVision.PixelBuffer.Read(after);
            VanillaInventoryVision.PixelBuffer prior = before != null && before.Size == after.Size
                ? VanillaInventoryVision.PixelBuffer.Read(before) : null;
            Rectangle search = new Rectangle((int)(after.Width * 0.18), (int)(after.Height * 0.25),
                (int)(after.Width * 0.64), (int)(after.Height * 0.68));
            int minWidth = Math.Max(180, (int)(after.Width * 0.15));
            int maxWidth = Math.Min(600, (int)(after.Width * 0.50));
            int minHeight = Math.Max(80, (int)(after.Height * 0.08));
            int maxHeight = Math.Min(260, (int)(after.Height * 0.28));
            foreach (Component component in LightComponents(pixels, search, minWidth, maxWidth, minHeight, maxHeight))
            {
                Rectangle box = component.Bounds;
                double aspect = box.Width / (double)Math.Max(1, box.Height);
                double fill = component.Area / (double)Math.Max(1, box.Width * box.Height);
                if (aspect < 1.8 || aspect > 4.5 || fill < 0.45) continue;
                double cx = (box.Left + box.Width / 2.0) / after.Width;
                double cy = (box.Top + box.Height / 2.0) / after.Height;
                if (cx < 0.30 || cx > 0.70 || cy < 0.35 || cy > 0.86) continue;

                Rectangle selection = new Rectangle(box.Left + box.Width / 30, box.Top + box.Height / 6,
                    Math.Max(1, box.Width * 14 / 15), Math.Max(1, box.Height * 2 / 5));
                int blue = 0, blueY = 0;
                Rectangle clipped = Rectangle.Intersect(new Rectangle(Point.Empty, after.Size), selection);
                for (int y = clipped.Top; y < clipped.Bottom; y += 2)
                for (int x = clipped.Left; x < clipped.Right; x += 2)
                {
                    VanillaInventoryVision.PixelInfo p = pixels.At(x, y);
                    if (p.B >= 170 && p.B - p.R >= 45 && p.B - p.G >= 20) { blue++; blueY += y; }
                }
                if (blue < Math.Max(40, clipped.Width / 3)) continue;
                double meanBlueY = blueY / (double)blue;
                double relativeBlueY = (meanBlueY - box.Top) / Math.Max(1.0, box.Height);
                if (relativeBlueY < 0.18 || relativeBlueY > 0.52) continue;

                if (prior != null && ChangedRatio(pixels, prior, box) < 0.12) continue;
                return true;
            }
            return false;
        }

        private static double ChangedRatio(VanillaInventoryVision.PixelBuffer current,
            VanillaInventoryVision.PixelBuffer before, Rectangle box)
        {
            int changed = 0, total = 0;
            int step = Math.Max(2, Math.Min(box.Width, box.Height) / 50);
            for (int y = box.Top; y < box.Bottom; y += step)
            for (int x = box.Left; x < box.Right; x += step)
            {
                total++;
                if (current.ColorDistance(before, x, y) >= 45) changed++;
            }
            return total == 0 ? 0 : changed / (double)total;
        }

        private static List<Component> LightComponents(VanillaInventoryVision.PixelBuffer pixels, Rectangle area,
            int minWidth, int maxWidth, int minHeight, int maxHeight)
        {
            int width = pixels.Width, height = pixels.Height;
            bool[] mask = new bool[width * height];
            Rectangle clipped = Rectangle.Intersect(new Rectangle(0, 0, width, height), area);
            for (int y = clipped.Top; y < clipped.Bottom; y++)
                for (int x = clipped.Left; x < clipped.Right; x++) mask[y * width + x] = pixels.At(x, y).Light;

            var result = new List<Component>();
            int[] queue = new int[Math.Max(1, clipped.Width * clipped.Height)];
            for (int y0 = clipped.Top; y0 < clipped.Bottom; y0++)
            for (int x0 = clipped.Left; x0 < clipped.Right; x0++)
            {
                int start = y0 * width + x0;
                if (!mask[start]) continue;
                int head = 0, tail = 0; queue[tail++] = start; mask[start] = false;
                int minX = x0, maxX = x0, minY = y0, maxY = y0, count = 0;
                while (head < tail)
                {
                    int index = queue[head++], y = index / width, x = index - y * width; count++;
                    minX = Math.Min(minX, x); maxX = Math.Max(maxX, x); minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
                    for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if (nx < clipped.Left || nx >= clipped.Right || ny < clipped.Top || ny >= clipped.Bottom) continue;
                        int ni = ny * width + nx;
                        if (!mask[ni]) continue;
                        mask[ni] = false; queue[tail++] = ni;
                    }
                }
                int w = maxX - minX + 1, h = maxY - minY + 1;
                if (w >= minWidth && w <= maxWidth && h >= minHeight && h <= maxHeight && count >= 800)
                    result.Add(new Component { Bounds = new Rectangle(minX, minY, w, h), Area = count });
            }
            return result;
        }
    }
}
