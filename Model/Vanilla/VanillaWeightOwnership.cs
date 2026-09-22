using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaWeightCartResult
    {
        internal bool RequiresManualIntervention;
        internal bool Deferred;
        internal bool CartFull;
        internal bool StoppedForCartSafety;
        internal bool RetryLater;
        internal int RetryAfterSeconds;
        internal int ItemsMoved;
        internal string Message;
    }

    internal sealed class VanillaCartWeightSample
    {
        internal uint Current;
        internal uint Maximum;
        internal decimal Percent;
        internal uint Remaining { get { return Maximum >= Current ? Maximum - Current : 0; } }
    }

    internal sealed class VanillaCartItemRule
    {
        internal int Category;
        internal string CategoryName;
        internal string ItemName;
        internal uint UnitWeight;
    }

    internal sealed class VanillaWeightMaintenanceToken
    {
        internal string AccountId;
        internal int ProcessId;
        internal int Generation;
        internal VanillaReconnectAccount Account;
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private readonly HashSet<string> weightManualHolds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> weightCompletedHolds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int weightMaintenanceGeneration;

        internal bool IsWeightEnabledForProcess(int pid)
        {
            lock (gate)
            {
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                return runtime != null && runtime.Account.EffectiveWeightPolicyEnabled;
            }
        }

        internal bool IsCartMaintenanceEnabledForProcess(int pid)
        {
            lock (gate)
            {
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                return runtime != null && runtime.Account.EffectiveCartMaintenanceEnabled;
            }
        }

        internal bool IsWeightEmailEnabledForProcess(int pid)
        {
            lock (gate)
            {
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                return runtime != null && runtime.Account.EffectiveWeightEmailEnabled;
            }
        }

        internal bool TryBeginWeightMaintenance(int pid, out VanillaWeightMaintenanceToken token, out string reason)
        {
            token = null; reason = null;
            lock (gate)
            {
                if (disposed || !running) { reason = "reconnect supervision is not running"; return false; }
                Runtime runtime = runtimes.Values.FirstOrDefault(item => item.ProcessId == pid && item.Account.Enabled);
                if (runtime == null) { reason = "the client is not assigned to an enabled character row"; return false; }
                if (!runtime.Account.EffectiveCartMaintenanceEnabled) { reason = "Cart maintenance is disabled for this character"; return false; }
                if (weightManualHolds.Contains(runtime.Account.Id)) { reason = "the character is waiting for manual cart emptying"; return false; }
                if (weightCompletedHolds.Contains(runtime.Account.Id)) { reason = "farming is complete for this character; clear the Weight hold before resuming"; return false; }
                if (TemporaryActionRegistered(pid) || OtherRecoveryOwner(runtime) != null)
                { reason = "temporary action or another recovery owns this client/input"; return false; }
                if (runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery
                    || runtimes.Values.Any(item => !ReferenceEquals(item, runtime) && (item.ScriptRunning || item.RecoveryOwned || item.ClosingForRecovery)))
                { reason = "another serialized startup/recovery/UI operation owns the input lease"; return false; }
                runtime.ScriptRunning = true;
                int generation = ++weightMaintenanceGeneration;
                token = new VanillaWeightMaintenanceToken
                {
                    AccountId = runtime.Account.Id, ProcessId = pid, Generation = generation, Account = runtime.Account.Clone()
                };
                SetStage(runtime, VanillaReconnectStage.Online, "Weight/cart maintenance owns the serialized client input lease");
                return true;
            }
        }

        internal bool WeightMaintenanceCancelled(VanillaWeightMaintenanceToken token)
        {
            if (token == null) return true;
            lock (gate)
            {
                Runtime runtime;
                return disposed || !running || token.Generation != weightMaintenanceGeneration
                    || !runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId
                    || !runtime.Account.Enabled || !runtime.Account.EffectiveCartMaintenanceEnabled || CharacterOwnershipChanged(runtime, token.ProcessId);
            }
        }

        internal void CompleteWeightMaintenance(VanillaWeightMaintenanceToken token, bool manualHold, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                if (token.Generation != weightMaintenanceGeneration) return;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                if (manualHold)
                {
                    weightManualHolds.Add(token.AccountId);
                    SetStage(runtime, VanillaReconnectStage.Error, detail ?? "Weight/cart maintenance needs manual attention");
                }
                else
                {
                    weightManualHolds.Remove(token.AccountId);
                    weightCompletedHolds.Remove(token.AccountId);
                    SetStage(runtime, VanillaReconnectStage.Online, detail ?? "Weight/cart maintenance completed");
                }
            }
            RaiseUpdated();
        }

        internal bool IsWeightManualHold(string accountId)
        { lock (gate) return !string.IsNullOrWhiteSpace(accountId)
            && (weightManualHolds.Contains(accountId) || weightCompletedHolds.Contains(accountId)); }

        internal void CompleteWeightFarmingDone(VanillaWeightMaintenanceToken token, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                if (token.Generation != weightMaintenanceGeneration) return;
                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.RecoveryOwned = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                weightManualHolds.Remove(token.AccountId);
                weightCompletedHolds.Add(token.AccountId);
                SetStage(runtime, VanillaReconnectStage.Stopped,
                    detail ?? "Farming complete: Cart >=99% and carried weight >=50%; Autobattle intentionally OFF");
            }
            RaiseUpdated();
        }

        internal bool IsWeightCompletedHold(string accountId)
        { lock (gate) return !string.IsNullOrWhiteSpace(accountId) && weightCompletedHolds.Contains(accountId); }

        internal void MarkWeightMaintenanceCancelled(VanillaWeightMaintenanceToken token, bool autobattleMayBePaused, string detail)
        {
            if (token == null) return;
            lock (gate)
            {
                // Once Autobattle may have been toggled OFF, retain the hold by stable
                // character-row ID even if a settings edit removed/rebuilt its runtime.
                if (autobattleMayBePaused) weightManualHolds.Add(token.AccountId);

                Runtime runtime;
                if (!runtimes.TryGetValue(token.AccountId, out runtime) || runtime.ProcessId != token.ProcessId) return;
                runtime.ScriptRunning = false;
                runtime.MovementWatchdog.Reset();
                runtime.MovementRecoveryPending = false;
                runtime.NonMinimizedSince = null;
                if (autobattleMayBePaused)
                {
                    if (running)
                        SetStage(runtime, VanillaReconnectStage.Error, detail ?? "Weight/cart maintenance was cancelled after Autobattle may have been paused");
                    else
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Supervisor stopped; manual Weight/Cart hold retained");
                }
                else if (running && token.Generation == weightMaintenanceGeneration)
                    SetStage(runtime, VanillaReconnectStage.Online, detail ?? "Weight/cart maintenance cancelled before pausing Autobattle");
            }
            RaiseUpdated();
        }

        public void ClearWeightManualHolds()
        {
            lock (gate)
            {
                var held = new HashSet<string>(weightManualHolds.Concat(weightCompletedHolds), StringComparer.OrdinalIgnoreCase);
                weightManualHolds.Clear();
                weightCompletedHolds.Clear();
                foreach (Runtime runtime in runtimes.Values)
                    if (held.Contains(runtime.Account.Id) && runtime.ProcessId.HasValue && runtime.Account.Enabled
                        && (runtime.Stage == VanillaReconnectStage.Error || runtime.Stage == VanillaReconnectStage.Stopped))
                        SetStage(runtime, VanillaReconnectStage.Online, "Weight/Cart hold explicitly cleared");
            }
            VanillaDebugLog.Write("WEIGHT", "event=cart-holds-cleared source=explicit-user-action manualAndCompleted=true.");
            RaiseUpdated();
        }

        internal bool MinimizeWeightMaintenanceClient(VanillaWeightMaintenanceToken token)
        {
            if (WeightMaintenanceCancelled(token)) return false;
            return MinimizeAssignedClientCore(token.AccountId, false, true);
        }

        internal void LogWeightMaintenanceActivity(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            // Reuse the supervisor/session log so Weight/Cart progress is visible in the
            // Recovery & relog Log pane as it happens, not only in COPY DEBUG LOG.
            Log("[WEIGHT] " + text);
        }
    }

}
