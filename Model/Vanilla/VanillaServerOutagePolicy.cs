using System;
using System.Diagnostics;
using System.Threading;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaServerClosedException : Exception
    {
        internal VanillaServerClosedException() : base("Server Closed.(1) confirmed in two fresh captures; server availability checks wait 15 minutes.") { }
    }

    // Session-wide, monotonic schedule. Call under the supervisor gate. Wall time is
    // display-only; clocks changing cannot start an early connection attempt.
    internal sealed class VanillaServerOutagePolicy
    {
        internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(15);
        internal bool Active { get; private set; }
        internal string ProbeOwner { get; private set; }
        internal DateTimeOffset NextCheckAt { get; private set; }
        private TimeSpan deadline;

        internal void Confirm(string accountId, TimeSpan now, DateTimeOffset utcNow)
        {
            if (string.IsNullOrEmpty(accountId)) throw new ArgumentException("Outage account identity is required.");
            if (Active) return; // repeated observations never postpone the original check
            Active = true;
            SetDeadline(now, utcNow);
        }

        internal TimeSpan Remaining(TimeSpan now)
        { return !Active || now >= deadline ? TimeSpan.Zero : deadline - now; }

        internal bool TryBeginProbe(string accountId, TimeSpan now, DateTimeOffset utcNow)
        {
            if (!Active) return true;
            if (ProbeOwner != null) return string.Equals(ProbeOwner, accountId, StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(accountId) || Remaining(now) > TimeSpan.Zero) return false;
            ProbeOwner = accountId;
            return true;
        }

        internal void CompleteFailure(string accountId, TimeSpan now, DateTimeOffset utcNow)
        {
            if (!Active || !string.Equals(ProbeOwner, accountId, StringComparison.OrdinalIgnoreCase)) return;
            ProbeOwner = null;
            SetDeadline(now, utcNow);
        }

        internal bool CompleteVerifiedRecovery(string accountId)
        {
            if (!Active || !string.Equals(ProbeOwner, accountId, StringComparison.OrdinalIgnoreCase)) return false;
            Cancel();
            return true;
        }

        internal void Cancel() { Active = false; ProbeOwner = null; deadline = TimeSpan.Zero; NextCheckAt = default(DateTimeOffset); }
        private void SetDeadline(TimeSpan now, DateTimeOffset utcNow)
        { deadline = now + RetryInterval; NextCheckAt = utcNow + RetryInterval; }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private readonly VanillaServerOutagePolicy serverOutage = new VanillaServerOutagePolicy();

        private static void ThrowIfConfirmedServerClosed(int pid, Func<bool> cancelled)
        {
            if (cancelled()) throw new OperationCanceledException();
            using (var process = Process.GetProcessById(pid))
            {
                process.Refresh();
                IntPtr window = process.MainWindowHandle;
                if (window == IntPtr.Zero || VanillaVisualProbe.Classify(window) != VanillaVisualState.ServerClosed) return;
                DateTime identity = process.StartTime.ToUniversalTime();
                Thread.Sleep(150);
                if (cancelled()) throw new OperationCanceledException();
                process.Refresh();
                if (!process.HasExited && process.MainWindowHandle == window && process.StartTime.ToUniversalTime() == identity
                    && VanillaVisualProbe.Classify(window) == VanillaVisualState.ServerClosed)
                    throw new VanillaServerClosedException();
                throw new InvalidOperationException("Server unavailable dialog changed during confirmation; no input sent.");
            }
        }

        private void ConfirmServerOutageLocked(Runtime runtime)
        {
            bool first = !serverOutage.Active;
            serverOutage.Confirm(runtime.Account.Id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
            runtime.ServerOutagePending = true;
            runtime.HasBeenOnline = runtime.ResumeSent = false;
            runtime.MovementRecoveryPending = false;
            runtime.GameplaySince = runtime.LoginLikeSince = null;
            runtime.NextRecoveryAt = null;
            runtime.MovementWatchdog.Reset();
            if (first)
                foreach (Runtime pending in runtimes.Values)
                    if (pending.Account.Enabled && !pending.ScriptRunning
                        && (!pending.ProcessId.HasValue || pending.ResumeVerificationFailed || pending.Stage == VanillaReconnectStage.Backoff))
                    {
                        pending.NextRecoveryAt = null;
                        pending.ServerOutagePending = true;
                    }
            if (first) Log(runtime.Account.Label + ": confirmed Server Closed.(1). Server availability checks are shared across accounts and repeat every 15 minutes until verified recovery.");
        }

        private string ServerOutageDetail()
        {
            if (serverOutage.ProbeOwner != null)
                return "Server unavailable; one account is checking through the normal login flow. Other reconnects are queued.";
            return "Server unavailable: next check " + serverOutage.NextCheckAt.ToLocalTime().ToString("HH:mm:ss")
                + " (in " + FormatDelay((int)Math.Min(int.MaxValue, serverOutage.Remaining(restartEnvironment.MonotonicNow).TotalMilliseconds)) + "; every 15 minutes)";
        }

        private void ParkForServerOutageLocked(Runtime runtime)
        {
            runtime.ServerOutagePending = true;
            runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = false;
            runtime.NextRecoveryAt = null; // monotonic shared policy owns this deadline
            SetStage(runtime, VanillaReconnectStage.WaitingForServer, ServerOutageDetail());
        }

        // Invoke only after ordinary configuration/lease checks, immediately before
        // starting a close/launch/login. A healthy observed sibling never claims it.
        private bool MayStartServerOutageProbeLocked(Runtime runtime)
        {
            if (!serverOutage.Active) return true;
            runtime.ServerOutagePending = true;
            if (serverOutage.TryBeginProbe(runtime.Account.Id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow))
                return true;
            ParkForServerOutageLocked(runtime);
            return false;
        }

        private void FinishServerOutageFailureLocked(Runtime runtime, string reason)
        {
            serverOutage.CompleteFailure(runtime.Account.Id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
            ParkForServerOutageLocked(runtime);
            Log(runtime.Account.Label + ": " + reason + ". " + ServerOutageDetail() + ". Recovery input lease released during the wait.");
        }

        private bool DeferReservedServerProbeLocked(Runtime runtime, string reason)
        {
            if (!serverOutage.Active || !string.Equals(serverOutage.ProbeOwner, runtime.Account.Id, StringComparison.OrdinalIgnoreCase))
                return false;
            FinishServerOutageFailureLocked(runtime, reason);
            return true;
        }

        private void CancelServerOutageLocked()
        {
            serverOutage.Cancel();
            foreach (Runtime runtime in runtimes.Values) runtime.ServerOutagePending = false;
        }

        private bool IsParkedServerOutageClient(int pid)
        {
            lock (gate)
                foreach (Runtime runtime in runtimes.Values)
                    if (runtime.ProcessId == pid && runtime.ServerOutagePending && !runtime.ScriptRunning) return true;
            return false;
        }

        private void ReconcileServerOutageOwnerLocked()
        {
            string id = serverOutage.ProbeOwner;
            if (id == null) return;
            Runtime owner;
            if (!runtimes.TryGetValue(id, out owner) || !owner.Account.Enabled
                || weightCompletedHolds.Contains(id) || weightManualHolds.Contains(id)
                || (!owner.ScriptRunning && !owner.RecoveryOwned))
            {
                serverOutage.CompleteFailure(id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
                if (owner != null)
                {
                    owner.ScriptRunning = owner.RecoveryOwned = owner.ClosingForRecovery = false;
                    owner.ServerOutagePending = true;
                }
                Log("Server availability attempt lost its account/runtime owner; stale work cancelled and next check remains on the 15-minute schedule.");
            }
        }
    }
}
