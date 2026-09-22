using System;

namespace _4RTools.Model.Vanilla
{
    public sealed partial class VanillaReconnectSupervisor
    {
        internal bool TryRestartAfterCartFailure(VanillaWeightMaintenanceToken token, string reason)
        {
            lock (gate)
            {
                if (WeightMaintenanceCancelled(token) || !settings.AutoRecover) return false;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || !runtime.ScriptRunning || runtime.RecoveryOwned) return false;
                runtime.ScriptRunning = false;
                runtime.NextRecoveryAt = null;
                runtime.ResumeVerificationFailed = false;
                weightManualHolds.Remove(token.AccountId);
                weightCompletedHolds.Remove(token.AccountId);
                QueueClientRestart(runtime, restartEnvironment.UtcNow,
                    "Cart paused-state recovery: " + reason, false,
                    () => VanillaLauncherUpdateProcess.Read(token.ProcessId).StartedUtc);
                return runtime.ClosingForRecovery && runtime.RecoveryOwned;
            }
        }
    }
}
