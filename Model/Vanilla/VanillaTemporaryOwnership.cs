using System;
using System.Collections.Generic;
using System.Linq;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaTemporaryOwner
    {
        internal int ProcessId, Generation;
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private readonly Dictionary<int, VanillaTemporaryOwner> temporaryOwners = new Dictionary<int, VanillaTemporaryOwner>();
        private VanillaTemporaryOwner temporaryInputOwner;
        private readonly Runtime temporaryInputRuntime = new Runtime
        {
            Account = new VanillaReconnectAccount { Label = "Temporary action" }, ScriptRunning = true
        };

        internal VanillaTemporaryOwner RegisterTemporaryAction(int pid)
        {
            lock (gate)
            {
                if (disposed || FarmingEmergencyHeld(pid) || hardenedStartupRunning || OtherRecoveryOwner(null) != null || temporaryOwners.ContainsKey(pid))
                    throw new InvalidOperationException("Another startup/recovery/input operation is active; temporary action was not started.");
                var owner = new VanillaTemporaryOwner { ProcessId = pid, Generation = diagnosticGeneration };
                temporaryOwners.Add(pid, owner);
                return owner;
            }
        }

        internal bool TemporaryActionCancelled(VanillaTemporaryOwner owner)
        {
            lock (gate)
            {
                VanillaTemporaryOwner current;
                return owner == null || disposed || FarmingEmergencyHeld(owner.ProcessId) || hardenedStartupRunning || owner.Generation != diagnosticGeneration
                    || !temporaryOwners.TryGetValue(owner.ProcessId, out current) || !ReferenceEquals(owner, current);
            }
        }

        internal bool TryAcquireTemporaryInput(VanillaTemporaryOwner owner)
        {
            lock (gate)
            {
                if (TemporaryActionCancelled(owner)) return false;
                if (ReferenceEquals(temporaryInputOwner, owner)) return true;
                if (temporaryInputOwner != null || OtherRecoveryOwner(null) != null) return false;
                temporaryInputOwner = owner;
                return true;
            }
        }

        internal void ReleaseTemporaryInput(VanillaTemporaryOwner owner)
        { lock (gate) { if (ReferenceEquals(temporaryInputOwner, owner)) temporaryInputOwner = null; } }

        internal void UnregisterTemporaryAction(VanillaTemporaryOwner owner)
        {
            lock (gate)
            {
                ReleaseTemporaryInput(owner);
                VanillaTemporaryOwner current;
                if (owner != null && temporaryOwners.TryGetValue(owner.ProcessId, out current) && ReferenceEquals(owner, current))
                {
                    temporaryOwners.Remove(owner.ProcessId);
                    foreach (Runtime runtime in runtimes.Values.Where(r => r.ProcessId == owner.ProcessId))
                    { runtime.MovementWatchdog.Reset(); runtime.NonMinimizedSince = null; }
                }
            }
        }

        private bool TemporaryActionRegistered(int pid)
        {
            VanillaTemporaryOwner owner;
            return temporaryOwners.TryGetValue(pid, out owner) && !TemporaryActionCancelled(owner);
        }
    }
}
