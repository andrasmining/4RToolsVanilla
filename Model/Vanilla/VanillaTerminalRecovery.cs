using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace _4RTools.Model.Vanilla
{
    // A narrow native boundary lets the real supervisor/lease transitions run in
    // regression tests without launching, closing or sending input to a process.
    internal interface IVanillaRecoveryRestartEnvironment
    {
        DateTimeOffset UtcNow { get; }
        TimeSpan MonotonicNow { get; }
        DateTime GetStartTimeUtc(int pid);
        void Queue(System.Action work);
        void CloseClient(int pid, DateTime expectedStartTimeUtc, Func<bool> cancelled, System.Action<System.Action> ownedStep);
    }

    internal sealed class VanillaRecoveryRestartEnvironment : IVanillaRecoveryRestartEnvironment
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
        public TimeSpan MonotonicNow { get { return clock.Elapsed; } }
        public DateTime GetStartTimeUtc(int pid)
        { using (var process = Process.GetProcessById(pid)) return process.StartTime.ToUniversalTime(); }
        public void Queue(System.Action work)
        {
            Task.Run(work).ContinueWith(task => VanillaDebugLog.Write("RECOVERY", "Close worker failed: " + task.Exception),
                TaskContinuationOptions.OnlyOnFaulted);
        }
        public void CloseClient(int pid, DateTime expectedStartTimeUtc, Func<bool> cancelled, System.Action<System.Action> ownedStep)
        {
            if (cancelled()) throw new OperationCanceledException();
            using (var target = new VanillaClientCloseHandle(pid, expectedStartTimeUtc))
            {
                var elapsed = Stopwatch.StartNew();
                VanillaClientCloseProtocol.Run(target.HasExited,
                    () => ownedStep(target.CloseWindow), () => ownedStep(target.Terminate),
                    cancelled, () => elapsed.Elapsed, Thread.Sleep);
            }
        }
    }

    internal static class VanillaClientCloseProtocol
    {
        internal const int GracefulWaitMs = 3000, TerminationWaitMs = 3000;
        internal static void Run(Func<bool> exited, System.Action close, System.Action terminate,
            Func<bool> cancelled, Func<TimeSpan> clock, System.Action<int> wait)
        {
            System.Action check = () => { if (cancelled()) throw new OperationCanceledException("Client close cancelled."); };
            Func<int, bool> waitForExit = milliseconds =>
            {
                TimeSpan before = clock(), deadline = before + TimeSpan.FromMilliseconds(milliseconds);
                while (true)
                {
                    check();
                    if (exited()) return true;
                    TimeSpan now = clock();
                    if (now < before) throw new InvalidOperationException("Close clock moved backwards.");
                    before = now;
                    if (now >= deadline) return false;
                    wait(Math.Max(1, Math.Min(50, (int)Math.Ceiling((deadline - now).TotalMilliseconds))));
                }
            };
            check();
            if (exited()) return;
            check();
            close();
            if (waitForExit(GracefulWaitMs)) return;
            check();
            // Recheck after the graceful deadline before the only permitted fallback.
            if (exited()) return;
            check();
            terminate();
            if (!waitForExit(TerminationWaitMs))
                throw new TimeoutException("Client exit was not confirmed after close/termination; no replacement will be launched.");
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private readonly IVanillaRecoveryRestartEnvironment restartEnvironment;

        internal static bool IsTerminalDisconnect(VanillaVisualState visual)
        {
            return visual == VanillaVisualState.LoggingOut || visual == VanillaVisualState.Disconnected;
        }

        private static void ResetTerminalEvidence(Runtime runtime)
        {
            runtime.TerminalSamples = 0;
            runtime.TerminalVisual = VanillaVisualState.Unknown;
            runtime.TerminalObservedAt = null;
        }

        /// <summary>Called for each newly captured frame, before generic gameplay logic.</summary>
        private bool HandleTerminalVisual(Runtime runtime, VanillaVisualState visual, DateTimeOffset now, Func<DateTime> startTimeUtc)
        {
            bool outage = visual == VanillaVisualState.ServerClosed;
            if (!IsTerminalDisconnect(visual) && !outage)
            {
                ResetTerminalEvidence(runtime);
                if (visual != VanillaVisualState.ModalDialog) return false;
                runtime.GameplaySince = runtime.LoginLikeSince = null;
                SetStage(runtime, VanillaReconnectStage.WaitingForGameplay,
                    "Unrecognized modal; no dismissal, close or recovery input sent");
                return true;
            }
            runtime.GameplaySince = runtime.LoginLikeSince = null;
            string reason = outage ? "Server Closed.(1)" : visual == VanillaVisualState.LoggingOut ? "Now Logging Out." : "Disconnected from Server.";
            if (!running || disposed || !settings.VisualWatchdog || !settings.AutoRecover || !runtime.Account.Enabled)
            {
                ResetTerminalEvidence(runtime);
                if (running && !disposed) SetStage(runtime, VanillaReconnectStage.Error, reason + " Automatic recovery is disabled.");
                return true;
            }
            // An exact known terminal dialog is stronger evidence than a generic X/Y stall.
            // Two fresh matching captures may therefore recover immediately; unknown modals
            // still receive no dismissal, close or blind input.
            double gap = runtime.TerminalObservedAt.HasValue ? (now - runtime.TerminalObservedAt.Value).TotalMilliseconds : double.MaxValue;
            runtime.TerminalSamples = runtime.TerminalVisual == visual && gap > 0 && gap <= Math.Max(5000, settings.PollMs * 2)
                ? Math.Min(2, runtime.TerminalSamples + 1) : 1;
            runtime.TerminalVisual = visual;
            runtime.TerminalObservedAt = now;
            if (runtime.TerminalSamples < 2)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForGameplay, reason + " Confirming terminal dialog (1/2)");
                return true;
            }
            if (outage) ConfirmServerOutageLocked(runtime);
            QueueClientRestart(runtime, now, reason, runtime.RecoveryOwned, startTimeUtc);
            return true;
        }

        // START can encounter a terminal dialog before continuous monitoring was
        // enabled. It uses the same process-close protocol and keeps the cold-start
        // gate while replacing this client; later accounts cannot start in parallel.
        private bool CloseTerminalBeforeStartup(Runtime runtime, int pid, int startupGeneration,
            VanillaReconnectSettings config, Func<VanillaVisualState> readVisual, Func<DateTime> startTimeUtc, System.Action<int> pause)
        {
            if (StartupCancelled(startupGeneration)) throw new OperationCanceledException();
            if (!config.VisualWatchdog) return false;
            VanillaVisualState visual = readVisual();
            bool outage = visual == VanillaVisualState.ServerClosed;
            if (!IsTerminalDisconnect(visual) && !outage) return false;
            if (!config.AutoRecover) throw new InvalidOperationException("Terminal dialog detected; automatic recovery is disabled.");
            DateTime identity = startTimeUtc();
            pause(Math.Max(250, Math.Min(1500, config.PollMs)));
            if (StartupCancelled(startupGeneration)) throw new OperationCanceledException();
            if (readVisual() != visual) throw new InvalidOperationException("Terminal dialog confirmation changed; no client close sent.");
            int operation;
            lock (gate)
            {
                RunOwnedClientStep(runtime, pid, () => StartupCancelled(startupGeneration), () =>
                {
                    if (runtime.ScriptRunning || OtherRecoveryOwner(runtime) != null)
                        throw new InvalidOperationException("Another client operation still owns recovery; no close sent.");
                    return true;
                });
                if (outage) ConfirmServerOutageLocked(runtime);
                operation = Interlocked.Increment(ref resumeVerificationGeneration);
                runtime.ResumeOperationGeneration = operation;
                runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = true;
                runtime.ResumeSent = false;
                SetStage(runtime, VanillaReconnectStage.ClosingClient, "Sequential startup: confirmed " + visual + "; closing before replacement");
            }
            Func<bool> cancelled = () => StartupCancelled(startupGeneration) || ResumeWorkerCancelled(runtime, pid, operation);
            try
            {
                restartEnvironment.CloseClient(pid, identity, cancelled, action =>
                    RunOwnedClientStep(runtime, pid, cancelled, () => { action(); return true; }));
                lock (gate)
                {
                    if (cancelled()) throw new OperationCanceledException();
                    CompleteClientRestartClose(runtime, restartEnvironment.UtcNow, visual.ToString(), false, null);
                    if (outage) ParkForServerOutageLocked(runtime);
                }
                Log(runtime.Account.Label + ": terminal client exit confirmed; replacement must finish before the next account starts.");
                RaiseUpdated();
                return true;
            }
            catch (Exception ex)
            {
                lock (gate)
                    if (!cancelled()) CompleteClientRestartClose(runtime, restartEnvironment.UtcNow, visual.ToString(), false, ex.Message);
                throw;
            }
        }

        private void QueueClientRestart(Runtime runtime, DateTimeOffset now, string reason, bool failedAttempt, Func<DateTime> startTimeUtc)
        {
            if (!running || disposed || !settings.AutoRecover || !runtime.Account.Enabled
                || !runtime.ProcessId.HasValue || runtime.ScriptRunning) return;
            if (serverOutage.Active)
            {
                string missing = MissingCharacterConfiguration(runtime.Account);
                if (missing == null && (string.IsNullOrWhiteSpace(settings.LaunchExecutable) || !System.IO.File.Exists(settings.LaunchExecutable)))
                    missing = "Set the Vanilla launch executable";
                if (missing != null)
                {
                    serverOutage.CompleteFailure(runtime.Account.Id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
                    runtime.RecoveryOwned = false;
                    SetStage(runtime, VanillaReconnectStage.NeedsConfiguration, missing);
                    return;
                }
            }
            if (runtime.NextRecoveryAt.HasValue && runtime.NextRecoveryAt.Value > now)
            {
                SetStage(runtime, VanillaReconnectStage.Backoff, BackoffDetail(runtime, now));
                return;
            }
            Runtime owner = OtherRecoveryOwner(runtime);
            if (owner != null)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForClient,
                    reason + " Queued: waiting for " + owner.Account.Label + " to finish recovery before closing this client");
                return;
            }
            if (!MayStartServerOutageProbeLocked(runtime)) return;
            // Read identity only once the operation can obtain the lease. A fresh
            // frame must reach this method again after any time spent in the queue.
            DateTime identity;
            try { identity = startTimeUtc(); }
            catch (Exception ex)
            {
                ScheduleRecoveryFailureLocked(runtime, now, "Cannot verify client identity for close: " + ex.Message);
                return;
            }
            int pid = runtime.ProcessId.Value;
            int generation = Interlocked.Increment(ref resumeVerificationGeneration);
            runtime.ResumeOperationGeneration = generation;
            runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = true;
            runtime.ResumeSent = false;
            SetStage(runtime, VanillaReconnectStage.ClosingClient, reason + " Closing affected client; other recovery requests remain queued");
            Log(runtime.Account.Label + ": " + reason + " Confirmed; closing PID " + pid + " under the sequential recovery lease.");
            Func<bool> cancelled = () => !IsRunning || ResumeWorkerCancelled(runtime, pid, generation);
            try
            {
                restartEnvironment.Queue(() =>
                {
                    string error = null;
                    try
                    {
                        if (cancelled()) throw new OperationCanceledException();
                        restartEnvironment.CloseClient(pid, identity, cancelled, action =>
                            RunOwnedClientStep(runtime, pid, cancelled, () => { action(); return true; }));
                    }
                    catch (OperationCanceledException) { return; }
                    catch (Exception ex) { error = ex.Message; }
                    lock (gate)
                    {
                        if (cancelled()) return;
                        CompleteClientRestartClose(runtime, restartEnvironment.UtcNow, reason, failedAttempt, error);
                    }
                    RaiseUpdated();
                });
            }
            catch (Exception ex)
            {
                runtime.ClosingForRecovery = false;
                ScheduleRecoveryFailureLocked(runtime, now, "Could not schedule client close: " + ex.Message);
            }
        }

        private void CompleteClientRestartClose(Runtime runtime, DateTimeOffset now, string reason, bool failedAttempt, string error)
        {
            runtime.ClosingForRecovery = runtime.ScriptRunning = false;
            ResetTerminalEvidence(runtime);
            if (error != null)
            {
                runtime.ResumeVerificationFailed = true;
                runtime.ResumeFailureDetail = "Client close failed: " + error;
                ScheduleRecoveryFailureLocked(runtime, now, runtime.ResumeFailureDetail);
                return;
            }
            int exitedPid = runtime.ProcessId.GetValueOrDefault();
            try { positionClientExited?.Invoke(exitedPid); }
            catch (Exception ex) { Log(runtime.Account.Label + ": exited reader cleanup failed: " + ex.Message); }
            runtime.MovementRecoveryPending = false;
            runtime.MovementWatchdog.Reset();
            runtime.ProcessId = null;
            runtime.ResumeSent = runtime.HasBeenOnline = runtime.ResumeVerificationFailed = false;
            runtime.ResumeFailureDetail = null;
            runtime.Visual = VanillaVisualState.Unknown;
            runtime.LoginLikeSince = runtime.GameplaySince = runtime.LastLaunch = null;
            if (failedAttempt)
                ScheduleRecoveryFailureLocked(runtime, now, reason + " Client exit confirmed after failed recovery");
            else
            {
                // Keep the SAME lease across close -> relaunch -> login -> the restart-only
                // hotkey verifier -> movement proof -> safe minimize.
                runtime.RecoveryOwned = true;
                runtime.NextRecoveryAt = now;
                SetStage(runtime, VanillaReconnectStage.WaitingForClient, reason + " Client exit confirmed; sequential relaunch queued.");
                Log(runtime.Account.Label + ": client exit confirmed; recovery lease retained through relaunch, restart-only hotkey verification, verified movement and minimization.");
            }
        }
    }
}
