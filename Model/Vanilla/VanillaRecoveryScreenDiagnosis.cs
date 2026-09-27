using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaRecoveryScreenFrame
    {
        internal VanillaVisualState State;
        internal VanillaVisualInputProof Proof;
        internal DateTimeOffset At;
    }

    // Observation and restoration of an original window state only: no gameplay input.
    internal interface IVanillaRecoveryScreenSession : IDisposable
    {
        bool WasMinimized { get; }
        VanillaRecoveryScreenFrame Capture();
        void RestoreMinimizedState(System.Action<System.Action> ownedStep);
    }

    internal static class VanillaRecoveryScreenDiagnosis
    {
        internal const int MinimumStallSeconds = 30, RetrySeconds = 60, MaximumDurationMs = 5000;

        internal static int DelaySeconds(VanillaReconnectAccount account)
        {
            return account != null && account.SmartTeleportEnabled && account.SmartTeleportKey >= 8
                && account.SmartTeleportKey <= 254
                ? Math.Min(MinimumStallSeconds, account.SmartTeleportIdleSeconds) : MinimumStallSeconds;
        }

        internal static VanillaRecoveryScreenFrame[] Observe(IVanillaRecoveryScreenSession session,
            Func<bool> cancelled, System.Action<System.Action> ownedCleanup, System.Action<int> pause, int loginStableMs = 0)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            System.Action check = () => { if (cancelled()) throw new OperationCanceledException("Foreground screen diagnosis cancelled."); };
            try
            {
                check();
                VanillaRecoveryScreenFrame first = session.Capture();
                check();
                int settle = first?.State == VanillaVisualState.LoginShell && loginStableMs > 150
                    && loginStableMs <= MaximumDurationMs - 1000 ? loginStableMs : 150;
                pause(settle);
                check();
                VanillaRecoveryScreenFrame second = session.Capture();
                check();
                if (!SameFreshWindow(first, second))
                    throw new InvalidOperationException("Foreground diagnosis frames were unavailable, stale or from different windows.");
                return new[] { first, second };
            }
            finally
            {
                try { if (session.WasMinimized) session.RestoreMinimizedState(ownedCleanup); }
                finally { session.Dispose(); }
            }
        }

        private static bool SameFreshWindow(VanillaRecoveryScreenFrame first, VanillaRecoveryScreenFrame second)
        {
            var a = first?.Proof; var b = second?.Proof;
            return a != null && b != null && a.ProcessId > 0 && a.Window != IntPtr.Zero
                && a.ProcessId == b.ProcessId && a.Window == b.Window && a.ClientOrigin == b.ClientOrigin
                && a.ClientSize == b.ClientSize && a.ClientSize.Width >= 200 && a.ClientSize.Height >= 120
                && a.CapturedAt > 0 && b.CapturedAt > a.CapturedAt && second.At > first.At;
        }
    }

    internal sealed class VanillaRecoveryScreenSession : IVanillaRecoveryScreenSession
    {
        private readonly Process process;
        private readonly int pid;
        private readonly DateTime created;
        private readonly Func<bool> cancelled;
        private readonly IntPtr window;
        private VanillaForegroundInput input;
        private bool captureStarted;
        public bool WasMinimized { get; private set; }

        private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder value, int length);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder value, int length);

        internal VanillaRecoveryScreenSession(int pid, DateTime created, Func<bool> cancelled)
        {
            this.pid = pid; this.created = created; this.cancelled = cancelled;
            process = Process.GetProcessById(pid);
            try
            {
                CheckProcess();
                var windows = new List<IntPtr>();
                EnumWindows((hwnd, parameter) =>
                {
                    uint owner;
                    if (GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != (uint)pid) return true;
                    var cls = new StringBuilder(256); var title = new StringBuilder(512);
                    GetClassName(hwnd, cls, cls.Capacity); GetWindowText(hwnd, title, title.Capacity);
                    if (VanillaForegroundInput.IsKnownVanillaGameWindow(cls.ToString(), title.ToString())) windows.Add(hwnd);
                    return true;
                }, IntPtr.Zero);
                if (windows.Count != 1) throw new InvalidOperationException("No unique owned Vanilla game window for foreground diagnosis.");
                window = windows[0]; WasMinimized = IsIconic(window);
                CheckIdentity();
            }
            catch { process.Dispose(); throw; }
        }

        private void CheckProcess()
        {
            process.Refresh();
            if (process.HasExited || process.StartTime.ToUniversalTime() != created)
                throw new OperationCanceledException("Screen diagnosis process identity changed.");
        }
        private void CheckIdentity()
        {
            CheckProcess();
            uint owner;
            if (!IsWindow(window) || GetWindowThreadProcessId(window, out owner) == 0 || owner != (uint)pid)
                throw new OperationCanceledException("Screen diagnosis window ownership changed.");
        }
        private bool InputCancelled()
        {
            if (cancelled()) return true;
            CheckIdentity();
            return false;
        }
        public VanillaRecoveryScreenFrame Capture()
        {
            if (InputCancelled()) throw new OperationCanceledException();
            captureStarted = true;
            if (input == null) input = new VanillaForegroundInput(pid, window, InputCancelled);
            using (Bitmap image = input.CaptureClientBitmapForObservation())
            {
                if (InputCancelled()) throw new OperationCanceledException();
                if (input.LastCaptureProof == null || input.LastCaptureProof.Window != window
                    || !VanillaTeleportVision.FrameLooksUsable(image))
                    throw new InvalidOperationException("Foreground diagnosis returned an unusable or changed-window frame.");
                return new VanillaRecoveryScreenFrame
                { State = VanillaVisualProbe.Classify(image), Proof = input.LastCaptureProof, At = DateTimeOffset.UtcNow };
            }
        }
        public void RestoreMinimizedState(System.Action<System.Action> ownedStep)
        {
            if (!captureStarted || !WasMinimized) return;
            ownedStep(() =>
            {
                CheckIdentity();
                if (!IsIconic(window)) ShowWindow(window, 6);
            });
        }
        public void Dispose() { try { input?.Dispose(); } finally { process.Dispose(); } }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private sealed class ScreenDiagnosisAttempt
        {
            internal Runtime Runtime;
            internal int Pid, DiagnosticGeneration, ResumeGeneration;
            internal DateTime Created;
            internal TimeSpan QueuedAt;
            internal long ProgressVersion;
            internal bool Active, ModalRecheck;
        }
        private readonly Dictionary<string, ScreenDiagnosisAttempt> screenDiagnosisAttempts = new Dictionary<string, ScreenDiagnosisAttempt>();
        internal Func<int, DateTime, Func<bool>, IVanillaRecoveryScreenSession> RecoveryScreenDiagnosisFactory { get; set; }

        private bool TryQueueRecoveryScreenDiagnosis(Runtime runtime, DateTimeOffset now, Func<DateTime> startTimeUtc, double stalled)
        {
            bool modalRecheck = runtime.Visual == VanillaVisualState.ModalDialog;
            if (disposed || !running || (!settings.AutoRecover && !runtime.Account.SmartTeleportEnabled) || hardenedStartupRunning || runtime.ProcessId == null
                || !runtime.Account.Enabled || runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery
                || (!runtime.HasBeenOnline && !runtime.ResumeSent) || FarmingEmergencyHeld(runtime)
                || weightManualHolds.Contains(runtime.Account.Id) || weightCompletedHolds.Contains(runtime.Account.Id)
                || TemporaryActionRegistered(runtime.ProcessId.Value) || OtherRecoveryOwner(runtime) != null
                || stalled < VanillaRecoveryScreenDiagnosis.DelaySeconds(runtime.Account)) return false;
            var factory = RecoveryScreenDiagnosisFactory;
            if (factory == null)
            {
                // Injected restart environments never fall through to native desktop work.
                if (!(restartEnvironment is VanillaRecoveryRestartEnvironment)) return false;
                factory = (pid, created, cancelled) => new VanillaRecoveryScreenSession(pid, created, cancelled);
            }
            ScreenDiagnosisAttempt previous;
            TimeSpan clock = restartEnvironment.MonotonicNow;
            if (screenDiagnosisAttempts.TryGetValue(runtime.Account.Id, out previous)
                && ReferenceEquals(previous.Runtime, runtime) && previous.Pid == runtime.ProcessId
                && previous.DiagnosticGeneration == diagnosticGeneration
                && previous.ProgressVersion == runtime.MovementWatchdog.ProgressVersion && clock >= previous.QueuedAt
                && (clock - previous.QueuedAt).TotalSeconds < VanillaRecoveryScreenDiagnosis.RetrySeconds) return false;
            DateTime createdAt;
            try { createdAt = startTimeUtc(); }
            catch (Exception ex) { Log(runtime.Account.Label + ": screen diagnosis identity unavailable: " + ex.Message); return false; }
            var attempt = new ScreenDiagnosisAttempt
            {
                Runtime = runtime, Pid = runtime.ProcessId.Value, Created = createdAt, QueuedAt = clock, Active = true,
                ProgressVersion = runtime.MovementWatchdog.ProgressVersion,
                DiagnosticGeneration = diagnosticGeneration, ResumeGeneration = runtime.ResumeOperationGeneration, ModalRecheck = modalRecheck
            };
            screenDiagnosisAttempts[runtime.Account.Id] = attempt;
            runtime.ScriptRunning = true;
            Log(runtime.Account.Label + ": X/Y movement has stalled; observing only this client in the foreground before any recovery input.");
            try { restartEnvironment.Queue(() => RunRecoveryScreenDiagnosis(attempt, factory)); }
            catch { attempt.Active = false; runtime.ScriptRunning = false; throw; }
            return true;
        }

        private bool ScreenDiagnosisLeaseCurrent(ScreenDiagnosisAttempt attempt)
        {
            Runtime current; ScreenDiagnosisAttempt active;
            return attempt.Active && diagnosticGeneration == attempt.DiagnosticGeneration
                && runtimes.TryGetValue(attempt.Runtime.Account.Id, out current) && ReferenceEquals(current, attempt.Runtime)
                && screenDiagnosisAttempts.TryGetValue(current.Account.Id, out active) && ReferenceEquals(active, attempt)
                && current.ProcessId == attempt.Pid && current.ResumeOperationGeneration == attempt.ResumeGeneration;
        }
        private bool ScreenDiagnosisOwns(ScreenDiagnosisAttempt attempt)
        {
            Runtime current = attempt.Runtime;
            return ScreenDiagnosisLeaseCurrent(attempt) && !disposed && running && (settings.AutoRecover || current.Account.SmartTeleportEnabled) && !hardenedStartupRunning
                && current.ScriptRunning && !current.RecoveryOwned && !current.ClosingForRecovery && current.Account.Enabled
                && !FarmingEmergencyHeld(current) && !weightManualHolds.Contains(current.Account.Id)
                && !weightCompletedHolds.Contains(current.Account.Id) && !TemporaryActionRegistered(attempt.Pid)
                && OtherRecoveryOwner(current) == null && !CharacterOwnershipChanged(current, attempt.Pid);
        }

        private bool ScreenDiagnosisCancelled(ScreenDiagnosisAttempt attempt, Stopwatch elapsed)
        {
            lock (gate)
            {
                if (!ScreenDiagnosisOwns(attempt) || elapsed.ElapsedMilliseconds >= VanillaRecoveryScreenDiagnosis.MaximumDurationMs) return true;
                VanillaPositionSample sample = null;
                try { sample = positionSource?.Invoke(attempt.Pid); }
                catch { /* Existing reader failure stays unavailable, never fabricated movement. */ }
                attempt.Runtime.MovementWatchdog.Observe(attempt.Pid, sample, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
                double stalled = attempt.Runtime.MovementWatchdog.StalledSeconds(restartEnvironment.MonotonicNow);
                return stalled < VanillaRecoveryScreenDiagnosis.DelaySeconds(attempt.Runtime.Account);
            }
        }

        private void RunRecoveryScreenDiagnosis(ScreenDiagnosisAttempt attempt,
            Func<int, DateTime, Func<bool>, IVanillaRecoveryScreenSession> factory)
        {
            var elapsed = Stopwatch.StartNew();
            DateTimeOffset startedAt = restartEnvironment.UtcNow;
            VanillaRecoveryScreenFrame[] frames = null;
            try
            {
                Func<bool> cancelled = () => ScreenDiagnosisCancelled(attempt, elapsed);
                if (cancelled()) return;
                if (restartEnvironment.GetStartTimeUtc(attempt.Pid) != attempt.Created) return;
                int loginStableMs;
                lock (gate) loginStableMs = settings.LoginStableMs;
                IVanillaRecoveryScreenSession session = factory(attempt.Pid, attempt.Created, cancelled);
                frames = VanillaRecoveryScreenDiagnosis.Observe(session, cancelled,
                    action => { lock (gate) { if (ScreenDiagnosisOwns(attempt)) action(); } },
                    milliseconds =>
                    {
                        for (int waited = 0; waited < milliseconds; waited += 25)
                        { if (cancelled()) throw new OperationCanceledException(); Thread.Sleep(Math.Min(25, milliseconds - waited)); }
                    }, loginStableMs);
                if (cancelled()) frames = null;
            }
            catch (OperationCanceledException) { Log(attempt.Runtime.Account.Label + ": foreground screen diagnosis cancelled; no recovery input sent."); }
            catch (Exception ex) { Log(attempt.Runtime.Account.Label + ": foreground screen diagnosis unavailable: " + ex.Message); }
            finally
            {
                lock (gate)
                {
                    bool publish = false;
                    try
                    {
                        publish = frames != null && !ScreenDiagnosisCancelled(attempt, elapsed)
                            && restartEnvironment.GetStartTimeUtc(attempt.Pid) == attempt.Created
                            && frames.All(frame => frame.Proof.ProcessId == attempt.Pid)
                            && frames[0].At >= startedAt && frames[1].At <= restartEnvironment.UtcNow
                            && (frames[1].At - frames[0].At).TotalMilliseconds <= VanillaRecoveryScreenDiagnosis.MaximumDurationMs;
                    }
                    catch (Exception ex) { Log(attempt.Runtime.Account.Label + ": screen diagnosis completion unavailable: " + ex.Message); }
                    if (ScreenDiagnosisLeaseCurrent(attempt))
                    {
                        Runtime runtime = attempt.Runtime;
                        runtime.ScriptRunning = false;
                        attempt.Active = false;
                        if (publish)
                        {
                            runtime.ForegroundGameplayVersion = frames.Length == 2
                                && frames.All(frame => !AutobattleVisualBlocksInput(frame.State))
                                ? runtime.MovementWatchdog.ProgressVersion : -1;
                            bool retainedModal = runtime.Visual == VanillaVisualState.ModalDialog
                                && !(frames[0].State == frames[1].State
                                    && frames[0].State != VanillaVisualState.Unknown
                                    && frames[0].State != VanillaVisualState.ModalDialog);
                            if (retainedModal && !frames.Any(frame => frame.State == VanillaVisualState.ModalDialog))
                            {
                                runtime.LastVisualObservation = new VanillaRecoveryVisualObservation(frames[1].State, true, null);
                                runtime.VisualObservationSequence += frames.Length;
                                Log(runtime.Account.Label + ": foreground frames did not establish dismissal of the previously observed modal.");
                                HandleTerminalVisual(runtime, VanillaVisualState.ModalDialog, frames[1].At, () => attempt.Created);
                            }
                            else
                            {
                                // A partially observed unknown modal is not proof of dismissal.
                                if (frames.Any(frame => frame.State == VanillaVisualState.ModalDialog))
                                    frames = new[] { frames.First(frame => frame.State == VanillaVisualState.ModalDialog) };
                                ResetTerminalEvidence(runtime);
                                foreach (var frame in frames)
                                {
                                    runtime.LastVisualObservation = new VanillaRecoveryVisualObservation(frame.State, true, null);
                                    runtime.Visual = frame.State; runtime.VisualObservationSequence++;
                                    Log(runtime.Account.Label + ": foreground screen diagnosis=" + frame.State + ".");
                                    HandleTerminalVisual(runtime, frame.State, frame.At, () => attempt.Created);
                                }
                                if (runtime.HasBeenOnline && frames.Length == 2
                                    && frames.All(frame => frame.State == VanillaVisualState.LoginShell)
                                    && (frames[1].At - frames[0].At).TotalMilliseconds >= settings.LoginStableMs)
                                    QueueClientRestart(runtime, restartEnvironment.UtcNow,
                                        "Confirmed return to login/service screen during foreground diagnosis", false, () => attempt.Created);
                                if (!runtime.ScriptRunning && !runtime.RecoveryOwned
                                    && runtime.ForegroundGameplayVersion == runtime.MovementWatchdog.ProgressVersion)
                                    TryQueueStalledAutobattleRecovery(runtime);
                            }
                        }
                    }
                    else attempt.Active = false;
                }
                RaiseUpdated();
            }
        }
    }
}
