using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
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
                SetStage(runtime, VanillaReconnectStage.Online, "Smart Teleport owns the serialized foreground-input lease");
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

        internal void SmartTeleportCleanup(VanillaSmartTeleportToken token, System.Action action)
        {
            lock (gate)
            {
                if (!SmartTeleportCancelled(token)) action();
            }
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

    internal sealed class VanillaTeleportMovementGuard
    {
        private readonly VanillaClientState baseline;
        private readonly Func<VanillaClientState> read;
        private readonly Func<DateTimeOffset> utcNow;
        internal bool MovementObserved { get; private set; }

        internal VanillaTeleportMovementGuard(VanillaClientState baseline, Func<VanillaClientState> read,
            Func<DateTimeOffset> utcNow = null)
        {
            this.baseline = baseline ?? throw new ArgumentNullException(nameof(baseline));
            this.read = read ?? throw new ArgumentNullException(nameof(read));
            this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
            VanillaAutobattleResumeVerifier.ValidateSample(baseline, null, baseline.ProcessId ?? 0, this.utcNow());
        }

        internal bool MovementResumed()
        {
            if (MovementObserved) return true;
            VanillaClientState current = read();
            VanillaAutobattleResumeVerifier.ValidateSample(current, baseline, baseline.ProcessId ?? 0, utcNow());
            MovementObserved = current.X.Value != baseline.X.Value || current.Y.Value != baseline.Y.Value
                || (current.LastMovementAtUtc.HasValue && current.LastMovementAtUtc > baseline.SampledAtUtc)
                || (current.X.LastChangedAtUtc.HasValue && current.X.LastChangedAtUtc > baseline.SampledAtUtc)
                || (current.Y.LastChangedAtUtc.HasValue && current.Y.LastChangedAtUtc > baseline.SampledAtUtc);
            return MovementObserved;
        }
    }

    // Tests replace this entire native boundary; the production implementation only
    // captures and sends input through the shared verified foreground transport.
    internal interface IVanillaTeleportInput : IDisposable
    {
        void Activate();
        Bitmap Capture();
        void Chord(VanillaReconnectAccount account);
        void ConfirmWarp();
        void Minimize(System.Action<System.Action> ownedStep);
    }

    internal sealed class VanillaForegroundTeleportInput : IVanillaTeleportInput
    {
        private readonly Process process;
        private readonly DateTime created;
        private readonly Func<bool> cancelled;
        private readonly VanillaForegroundInput input;
        private IntPtr window;
        private VanillaVisualInputProof firstProof;
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

        internal VanillaForegroundTeleportInput(int pid, Func<bool> cancelled)
        {
            this.cancelled = cancelled;
            process = Process.GetProcessById(pid);
            try
            {
                created = process.StartTime.ToUniversalTime();
                input = new VanillaForegroundInput(pid, IntPtr.Zero, Cancelled);
            }
            catch { process.Dispose(); throw; }
        }

        private bool Cancelled()
        {
            if (cancelled()) return true;
            process.Refresh();
            return process.HasExited || process.StartTime.ToUniversalTime() != created;
        }

        public void Activate() { window = input.Window; input.Activate(); }
        public Bitmap Capture()
        {
            Bitmap frame = input.CaptureClientBitmap();
            try
            {
                var proof = input.LastCaptureProof;
                if (proof == null || proof.Window != window || proof.ProcessId != process.Id
                    || (firstProof != null && (firstProof.ClientSize != proof.ClientSize || firstProof.ClientOrigin != proof.ClientOrigin)))
                    throw new InvalidOperationException("The teleport window/geometry changed; no further input sent.");
                if (!VanillaTeleportVision.FrameLooksUsable(frame))
                    throw new InvalidOperationException("The owned foreground teleport capture is unusable; no input sent.");
                firstProof = firstProof ?? proof;
                return frame;
            }
            catch { frame.Dispose(); throw; }
        }
        public void Chord(VanillaReconnectAccount account)
        { input.ChordInVerifiedForeground(account.SmartTeleportCtrl, account.SmartTeleportAlt,
            account.SmartTeleportShift, (Keys)account.SmartTeleportKey); }
        public void ConfirmWarp() { input.PressFromProof(Keys.Enter, input.LastCaptureProof); }
        public void Minimize(System.Action<System.Action> ownedStep)
        {
            if (window == IntPtr.Zero) return;
            ownedStep(() =>
            {
                process.Refresh();
                uint owner;
                if (process.HasExited || process.StartTime.ToUniversalTime() != created
                    || GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)process.Id) return;
                if (!IsIconic(window)) ShowWindow(window, 6);
                if (!IsIconic(window)) throw new InvalidOperationException("Could not confirm teleport client minimization.");
            });
        }
        public void Dispose() { try { input.Dispose(); } finally { process.Dispose(); } }
    }

    internal static class VanillaVerifiedTeleportAction
    {
        internal static bool TryExecute(int pid, VanillaReconnectAccount account, Func<bool> cancelled, string mode,
            out string detail, Func<bool> movementResumed, System.Action<System.Action> ownedCleanup = null)
        {
            var clock = Stopwatch.StartNew();
            return TryExecute(pid, account, cancelled, mode, out detail, movementResumed,
                (processId, cancel) => new VanillaForegroundTeleportInput(processId, cancel),
                () => clock.Elapsed, Thread.Sleep, ownedCleanup);
        }

        internal static bool TryExecute(int pid, VanillaReconnectAccount account, Func<bool> cancelled, string mode,
            out string detail, Func<bool> movementResumed,
            Func<int, Func<bool>, IVanillaTeleportInput> factory, Func<TimeSpan> clock, System.Action<int> pause,
            System.Action<System.Action> ownedCleanup = null)
        {
            string context = " mode=" + (string.IsNullOrWhiteSpace(mode) ? "verified" : mode)
                + " accountId=" + account?.Id + " pid=" + pid;
            VanillaDebugLog.Write("TELEPORT", "event=teleport-start" + context + "; foreground-only=true.");
            try
            {
                bool completed = TryExecuteCore(pid, account, cancelled, mode, out detail, movementResumed,
                    factory, clock, pause, ownedCleanup);
                string result = completed ? "teleport-complete"
                    : detail.StartsWith("Smart Teleport stopped:", StringComparison.Ordinal) ? "teleport-failed" : "teleport-deferred";
                VanillaDebugLog.Write("TELEPORT", "event=" + result + context + "; " + detail);
                return completed;
            }
            catch (OperationCanceledException)
            {
                VanillaDebugLog.Write("TELEPORT", "event=teleport-cancelled" + context + "; ownership changed; no further input sent.");
                throw;
            }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("TELEPORT", "event=teleport-failed" + context + "; " + ex.Message);
                throw;
            }
        }

        private static bool TryExecuteCore(int pid, VanillaReconnectAccount account, Func<bool> cancelled, string mode,
            out string detail, Func<bool> movementResumed,
            Func<int, Func<bool>, IVanillaTeleportInput> factory, Func<TimeSpan> clock, System.Action<int> pause,
            System.Action<System.Action> ownedCleanup)
        {
            if (pid <= 0) throw new ArgumentOutOfRangeException(nameof(pid));
            if (account == null || cancelled == null || movementResumed == null || factory == null || clock == null || pause == null)
                throw new ArgumentNullException("Teleport requires complete ownership, foreground and timing services.");
            mode = string.IsNullOrWhiteSpace(mode) ? "verified" : mode;
            detail = "Smart Teleport cancelled";
            if (account.SmartTeleportKey < 8 || account.SmartTeleportKey > 254)
            {
                detail = "Smart Teleport skipped: no teleport hotkey is configured";
                Report(account, pid, mode, "skipped", detail);
                return false;
            }
            bool moved = false, confirmed = false;
            Func<bool> inputCancelled = () =>
            {
                if (cancelled()) return true;
                if (movementResumed()) moved = true;
                return moved;
            };
            System.Action check = () =>
            {
                if (inputCancelled()) throw new OperationCanceledException("Teleport ownership changed or movement resumed.");
            };
            try
            {
                check();
                using (var input = factory(pid, inputCancelled))
                {
                    bool activationAttempted = false;
                    try
                    {
                        check();
                        activationAttempted = true;
                        input.Activate();
                        check();
                        using (Bitmap before = input.Capture())
                        {
                            check();
                            if (VanillaTeleportVision.HasWarpDialog(null, before))
                            {
                                detail = "Smart Teleport deferred: a warp-selection dialog was already open";
                                Report(account, pid, mode, "deferred", detail);
                                return false;
                            }
                            VanillaDebugLog.Write("TELEPORT", "event=teleport-hotkey mode=" + mode + " pid=" + pid
                                + " hotkey='" + account.SmartTeleportHotkeyText + "' foreground=verified.");
                            input.Chord(account);
                            if (!WaitForDialog(input, before, true, check, clock, pause, 3000))
                            {
                                detail = "Smart Teleport stopped: expected warp-selection dialog was not detected; Enter was not sent";
                                Report(account, pid, mode, "failed", detail);
                                return false;
                            }
                            check();
                            using (Bitmap confirmation = input.Capture())
                            {
                                check();
                                RequireSameGeometry(before, confirmation);
                                if (!VanillaTeleportVision.HasWarpDialog(null, confirmation))
                                {
                                    detail = "Smart Teleport stopped: warp selection changed before confirmation; Enter was not sent";
                                    Report(account, pid, mode, "failed", detail);
                                    return false;
                                }
                            }
                            check();
                            Report(account, pid, mode, "enter", "Fresh warp popup confirmed; first choice selected.");
                            input.ConfirmWarp();
                            confirmed = true;
                            if (!WaitForDialog(input, before, false, check, clock, pause, 2500))
                            {
                                detail = "Smart Teleport stopped: warp-selection dialog did not clear after Enter";
                                Report(account, pid, mode, "failed", detail);
                                return false;
                            }
                        }
                    }
                    finally
                    {
                        if (activationAttempted && ownedCleanup != null) input.Minimize(ownedCleanup);
                    }
                }
                detail = "Smart Teleport completed in the owned foreground client";
                Report(account, pid, mode, "complete", detail);
                return true;
            }
            catch (OperationCanceledException) when (moved && !cancelled())
            {
                detail = "Movement resumed; no further Smart Teleport input sent";
                Report(account, pid, mode, "movement", detail);
                return false;
            }
            catch (InvalidOperationException ex) when (confirmed && !cancelled()
                && ex.Message == "The client is loading or no longer ready for autobattle.")
            {
                // A warp may enter Loading before its dialog-clear capture. This is not
                // visual confirmation and authorizes no retry/input; the caller observes X/Y.
                detail = "Warp confirmation sent; fresh gameplay/dialog-clear evidence is pending";
                Report(account, pid, mode, "pending", detail);
                return false;
            }
        }

        private static void Report(VanillaReconnectAccount account, int pid, string mode, string result, string detail)
        {
            VanillaDebugLog.Write("TELEPORT", "event=teleport-" + result + " mode=" + mode + " account='"
                + account.Label + "' accountId=" + account.Id + " pid=" + pid + " detail='" + detail + "'.");
        }

        private static bool WaitForDialog(IVanillaTeleportInput input, Bitmap before, bool expected,
            System.Action check, Func<TimeSpan> clock, System.Action<int> pause, int timeoutMs)
        {
            TimeSpan started = clock(), last = started;
            int consecutive = 0;
            while (clock() - started < TimeSpan.FromMilliseconds(timeoutMs))
            {
                check();
                TimeSpan current = clock();
                if (current < last) throw new InvalidOperationException("Teleport clock moved backwards.");
                last = current;
                using (Bitmap frame = input.Capture())
                {
                    check();
                    RequireSameGeometry(before, frame);
                    bool matches = VanillaTeleportVision.HasWarpDialog(expected ? before : null, frame) == expected;
                    consecutive = matches ? consecutive + 1 : 0;
                    if (consecutive >= 2) return true;
                }
                pause(150);
            }
            return false;
        }

        private static void RequireSameGeometry(Bitmap before, Bitmap after)
        {
            if (before != null && before.Size != after.Size)
                throw new InvalidOperationException("The teleport capture geometry changed; no further input sent.");
        }
    }

    internal sealed class VanillaSmartTeleportService : IDisposable
    {
        private sealed class CharacterState
        {
            internal bool Running;
        }

        private readonly VanillaFleetMonitor fleet;
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly Dictionary<string, CharacterState> states = new Dictionary<string, CharacterState>(StringComparer.OrdinalIgnoreCase);
        private readonly object gate = new object();
        private bool started;
        private bool disposed;

        internal bool IsRunning { get { lock (gate) return started && !disposed; } }

        internal VanillaSmartTeleportService(VanillaFleetMonitor fleet, VanillaReconnectSupervisor supervisor)
        {
            this.fleet = fleet ?? throw new ArgumentNullException(nameof(fleet));
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        internal void Start()
        {
            lock (gate)
            {
                if (disposed || started) return;
                started = true;
            }
            VanillaDebugLog.Write("TELEPORT", "Smart Teleport enabled; automatic stationary recovery is owned by the serialized supervisor. Manual tests use the same foreground action.");
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
            ThreadPool.QueueUserWorkItem(_ => Execute(accountId, pid, state));
            return "Smart Teleport test queued for " + account.Label + " (PID " + pid + ").";
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

        private void Execute(string accountId, int pid, CharacterState state)
        {
            VanillaSmartTeleportToken token = null;
            string reason;
            const string mode = "manual-test";
            string completionDetail = "Smart Teleport cancelled";
            try
            {
                if (!supervisor.TryBeginSmartTeleport(pid, out token, out reason))
                {
                    VanillaDebugLog.Write("TELEPORT", "event=teleport-deferred mode=" + mode + " accountId=" + accountId
                        + " pid=" + pid + " reason='" + reason + "'.");
                    return;
                }
                if (!string.Equals(token.AccountId, accountId, StringComparison.OrdinalIgnoreCase))
                {
                    completionDetail = "Smart Teleport cancelled: selected character binding changed";
                    return;
                }

                VanillaDebugLog.Write("TELEPORT", "event=teleport-manual-lease mode=" + mode + " account='" + token.Account.Label
                    + "' accountId=" + token.AccountId + " pid=" + pid + " hotkey='" + token.Account.SmartTeleportHotkeyText + "'.");
                Func<bool> cancelled = () => disposed || supervisor.SmartTeleportCancelled(token);
                Func<VanillaClientState> read = () =>
                {
                    VanillaFleetClientInfo client = fleet.Poll().FirstOrDefault(value => value.ProcessId == pid);
                    if (client == null || !VanillaCharacterRoster.Matches(token.Account, client.Identity, DateTimeOffset.UtcNow))
                        throw new InvalidOperationException("Fresh teleport character identity is unavailable.");
                    return client.Snapshot;
                };
                var movement = new VanillaTeleportMovementGuard(read(), read);
                string detail;
                VanillaVerifiedTeleportAction.TryExecute(pid, token.Account, cancelled, mode, out detail,
                    movement.MovementResumed, step => { if (!disposed) supervisor.SmartTeleportCleanup(token, step); });
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
                VanillaDebugLog.Write("TELEPORT", "event=teleport-service-error mode=" + mode + " account='"
                    + (token?.Account?.Label ?? accountId) + "' pid=" + pid + " stage=exception reason='" + ex.Message
                    + "'; no blind Enter was sent.");
                completionDetail = "Smart Teleport failed safely: " + ex.Message;
            }
            finally
            {
                if (token != null)
                {
                    try { supervisor.CompleteSmartTeleport(token, completionDetail); }
                    catch (Exception ex) { VanillaDebugLog.Write("TELEPORT", "Teleport completion notification failed: " + ex.Message); }
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
                started = false;
                states.Clear();
            }
        }
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
