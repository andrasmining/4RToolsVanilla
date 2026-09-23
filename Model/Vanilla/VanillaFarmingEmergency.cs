using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaFarmingEmergencyEvidence
    {
        internal VanillaCharacterIdentity Identity;
        internal DateTimeOffset At;
        internal uint Weight, MaxWeight, Sp, MaxSp, Hp, MaxHp;
        internal string Ratios
        {
            get
            {
                return "Weight " + Weight + "/" + MaxWeight + " (" + Percent(Weight, MaxWeight)
                    + "%), SP " + Sp + "/" + MaxSp + " (" + Percent(Sp, MaxSp)
                    + "%), HP " + Hp + "/" + MaxHp + " (" + Percent(Hp, MaxHp) + "%)";
            }
        }
        private static string Percent(uint value, uint maximum)
        { return (value * 100m / maximum).ToString("0.00", CultureInfo.InvariantCulture); }
    }

    internal static class VanillaFarmingEmergency
    {
        internal const int MaximumSampleAgeMs = 1000;

        internal static bool TryEvaluate(VanillaClientState state, int pid, DateTimeOffset now,
            out VanillaFarmingEmergencyEvidence evidence)
        {
            evidence = null;
            if (state == null || state.IsDemo || state.Error != null || state.Fields == null
                || pid <= 0 || state.ProcessId != pid || state.SessionId == Guid.Empty
                || state.SampledAtUtc > now || now - state.SampledAtUtc > TimeSpan.FromMilliseconds(MaximumSampleAgeMs)) return false;
            foreach (VanillaField field in new[] { VanillaField.UserName, VanillaField.CharacterName, VanillaField.Map,
                VanillaField.CurrentHP, VanillaField.MaxHP, VanillaField.CurrentSP, VanillaField.MaxSP,
                VanillaField.CurrentWeight, VanillaField.MaxWeight })
                if (!Fresh(state, field)) return false;
            if ((Fresh(state, VanillaField.Loading) && state.Loading.Value)
                || (Fresh(state, VanillaField.ClientReady) && !state.ClientReady.Value)
                || string.IsNullOrWhiteSpace(state.Map.Value)) return false;
            VanillaCharacterIdentity identity = VanillaCharacterIdentity.FromState(state);
            if (VanillaCharacterRoster.Key(identity) == null) return false;
            uint hp = state.CurrentHP.Value, maxHp = state.MaxHP.Value;
            uint sp = state.CurrentSP.Value, maxSp = state.MaxSP.Value;
            uint weight = state.CurrentWeight.Value, maxWeight = state.MaxWeight.Value;
            if (maxHp == 0 || hp > maxHp || maxSp == 0 || sp > maxSp
                || VanillaWeightValidation.PairError(weight, maxWeight) != null) return false;
            // Decimal arithmetic preserves the strict boundaries without rounding or overflow.
            if (weight * 100m <= maxWeight * 50m || sp * 100m >= maxSp * 25m || hp * 100m >= maxHp * 50m) return false;
            evidence = new VanillaFarmingEmergencyEvidence
            {
                Identity = identity, At = state.SampledAtUtc,
                Weight = weight, MaxWeight = maxWeight, Sp = sp, MaxSp = maxSp, Hp = hp, MaxHp = maxHp
            };
            return true;
        }

        private static bool Fresh(VanillaClientState state, VanillaField field)
        {
            StateValue value;
            return state.Fields.TryGetValue(field, out value) && value != null && value.IsAvailable
                && value.Validation == StateValidation.Valid && value.Error == null
                && value.LastObservedAtUtc == state.SampledAtUtc;
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private sealed class FarmingEmergencyHold
        {
            public string UserName { get; set; }
            public string CharacterName { get; set; }
            public DateTimeOffset ObservedAt { get; set; }
            public string Ratios { get; set; }
            public string Detail { get; set; }
            public string CloseState { get; set; } = "pending";
            [JsonIgnore] internal bool ClosePending;
            [JsonIgnore] internal int? ConfirmedExitedPid;
            [JsonIgnore] internal bool PendingRestored;
        }

        private sealed class FarmingEmergencyDocument
        {
            public int Version { get; set; } = 1;
            public List<FarmingEmergencyHold> Holds { get; set; } = new List<FarmingEmergencyHold>();
        }

        private readonly Dictionary<string, FarmingEmergencyHold> farmingEmergencyHolds
            = new Dictionary<string, FarmingEmergencyHold>(StringComparer.Ordinal);
        private string farmingEmergencyPath;
        private string farmingEmergencyStoreFailure;

        public bool HasFarmingEmergencyHolds
        { get { lock (gate) return farmingEmergencyStoreFailure != null || farmingEmergencyHolds.Count != 0; } }

        public string FarmingEmergencyStatus
        {
            get
            {
                lock (gate)
                {
                    if (farmingEmergencyStoreFailure != null) return farmingEmergencyStoreFailure;
                    return farmingEmergencyHolds.Count == 0
                        ? "Emergency protection active: Weight >50%, SP <25% and HP <50% closes the affected client."
                        : string.Join(" | ", farmingEmergencyHolds.Values.Select(hold => hold.CharacterName + ": " + hold.Detail));
                }
            }
        }

        private void InitializeFarmingEmergency()
        {
            farmingEmergencyPath = Path.Combine(Path.GetDirectoryName(store.FilePath), "farming-emergency-holds.json");
            try
            {
                if (!File.Exists(farmingEmergencyPath)) return;
                var document = JsonConvert.DeserializeObject<FarmingEmergencyDocument>(File.ReadAllText(farmingEmergencyPath));
                if (document == null || document.Version != 1 || document.Holds == null)
                    throw new InvalidDataException("Emergency hold document is empty or unsupported.");
                foreach (FarmingEmergencyHold hold in document.Holds)
                {
                    string key = hold == null ? null : VanillaCharacterRoster.Key(hold.UserName, hold.CharacterName);
                    if (key == null || string.IsNullOrWhiteSpace(hold.Detail) || hold.ObservedAt == default(DateTimeOffset)
                        || farmingEmergencyHolds.ContainsKey(key)
                        || !new[] { "pending", "closed", "failed", "cancelled" }.Contains(hold.CloseState))
                        throw new InvalidDataException("Emergency hold identity or evidence is invalid.");
                    hold.PendingRestored = hold.CloseState == "pending";
                    farmingEmergencyHolds.Add(key, hold);
                }
            }
            catch (Exception ex)
            {
                farmingEmergencyStoreFailure = "Emergency hold file could not be read: " + ex.Message
                    + " Automation remains held until emergency holds are explicitly cleared.";
                VanillaDebugLog.Write("EMERGENCY", farmingEmergencyStoreFailure);
            }
        }

        internal bool FarmingEmergencyHeld(VanillaReconnectAccount account)
        {
            lock (gate)
            {
                string key = VanillaCharacterRoster.Key(account);
                return farmingEmergencyStoreFailure != null || (key != null && farmingEmergencyHolds.ContainsKey(key));
            }
        }

        private bool FarmingEmergencyHeld(Runtime runtime)
        { return runtime != null && FarmingEmergencyHeld(runtime.Account); }

        private bool FarmingEmergencyHeld(int pid)
        {
            lock (gate)
            {
                if (farmingEmergencyStoreFailure != null) return true;
                if (runtimes.Values.Any(runtime => runtime.ProcessId == pid && FarmingEmergencyHeld(runtime))) return true;
                string key = VanillaCharacterRoster.Key(CurrentCharacter(pid));
                return key != null && farmingEmergencyHolds.ContainsKey(key);
            }
        }

        private string FarmingEmergencyDetail(Runtime runtime)
        {
            if (farmingEmergencyStoreFailure != null) return farmingEmergencyStoreFailure;
            FarmingEmergencyHold hold;
            string key = VanillaCharacterRoster.Key(runtime?.Account);
            return key != null && farmingEmergencyHolds.TryGetValue(key, out hold)
                ? hold.Detail : "Emergency stop: explicit emergency hold clear is required before resuming.";
        }

        internal bool ObserveFarmingEmergency(VanillaFleetClientInfo client)
        {
            if (client == null || client.Error != null) return false;
            lock (gate)
            {
                if (disposed) return false;
                VanillaCharacterIdentity observed = VanillaCharacterIdentity.FromState(client.Snapshot);
                string key = VanillaCharacterRoster.Key(observed);
                FarmingEmergencyHold hold = null;
                bool alreadyHeld = key != null && farmingEmergencyHolds.TryGetValue(key, out hold);
                if (alreadyHeld && !hold.PendingRestored) return true;
                VanillaFarmingEmergencyEvidence evidence;
                if (!VanillaFarmingEmergency.TryEvaluate(client.Snapshot, client.ProcessId, restartEnvironment.UtcNow, out evidence))
                    return alreadyHeld;
                VanillaReconnectAccount[] accounts = settings.Accounts.Where(account => account.Enabled
                    && VanillaCharacterRoster.Matches(account, evidence.Identity, restartEnvironment.UtcNow)).ToArray();
                if (accounts.Length != 1) return alreadyHeld;
                var sameCharacters = ObservedCharacters().Where(identity => identity != null
                    && identity.IsFresh(restartEnvironment.UtcNow) && VanillaCharacterRoster.Key(identity) == key).ToArray();
                if (sameCharacters.Length != 1 || sameCharacters[0].ProcessId != client.ProcessId
                    || sameCharacters[0].Session != evidence.Identity.Session) return alreadyHeld;
                Runtime runtime;
                if (!runtimes.TryGetValue(accounts[0].Id, out runtime)
                    || runtimes.Values.Any(other => !ReferenceEquals(other, runtime) && other.ProcessId == client.ProcessId)) return alreadyHeld;

                if (!alreadyHeld)
                {
                    hold = new FarmingEmergencyHold { UserName = evidence.Identity.UserName, CharacterName = evidence.Identity.CharacterName };
                    farmingEmergencyHolds.Add(key, hold);
                }
                hold.PendingRestored = false;
                hold.CloseState = "pending";
                hold.ObservedAt = evidence.At; hold.Ratios = evidence.Ratios;
                hold.Detail = "EMERGENCY STOP: " + evidence.Ratios + ". Closing affected client; explicit emergency hold clear required.";
                runtime.MovementWatchdog.Reset(); runtime.MovementRecoveryPending = false;
                runtime.NextRecoveryAt = null;
                if (!runtime.ScriptRunning) runtime.RecoveryOwned = runtime.ClosingForRecovery = false;
                // Leave an existing input worker's lease intact until its cancellation
                // unwinds (including releasing a held drag mouse button).
                VanillaTemporaryOwner temporary;
                if (temporaryOwners.TryGetValue(client.ProcessId, out temporary))
                {
                    temporaryOwners.Remove(client.ProcessId);
                    // The active atomic input owner releases itself in its finally block.
                }
                PersistFarmingEmergencyLocked();
                SetStage(runtime, VanillaReconnectStage.Error, hold.Detail);
                Log(runtime.Account.Label + ": " + hold.Detail);
                VanillaDebugLog.Write("EMERGENCY", "event=farming-emergency-triggered accountId=" + runtime.Account.Id
                    + " pid=" + client.ProcessId + " session=" + evidence.Identity.Session
                    + " observed=" + evidence.At.ToString("O", CultureInfo.InvariantCulture) + " " + evidence.Ratios + ".");
                QueueFarmingEmergencyCloseLocked(runtime, client.ProcessId, evidence.Identity.Session, hold);
                RaiseUpdated();
                return true;
            }
        }

        private void QueueFarmingEmergencyCloseLocked(Runtime runtime, int pid, Guid session, FarmingEmergencyHold hold)
        {
            string key = VanillaCharacterRoster.Key(hold.UserName, hold.CharacterName);
            VanillaReconnectSettings configuration = settings;
            int operation = runtime.ResumeOperationGeneration;
            int diagnosticOperation = diagnosticGeneration;
            int? assignedPid = runtime.ProcessId;
            DateTime started;
            try { started = restartEnvironment.GetStartTimeUtc(pid); }
            catch (Exception ex)
            {
                CompleteFarmingEmergencyCloseLocked(runtime, pid, hold, "Cannot pin client creation time: " + ex.Message, false);
                return;
            }
            hold.ClosePending = true;
            bool closeIssued = false;
            Func<bool> cancelled = () =>
            {
                lock (gate)
                {
                    FarmingEmergencyHold currentHold;
                    Runtime currentRuntime;
                    if (disposed || diagnosticGeneration != diagnosticOperation || !ReferenceEquals(settings, configuration)
                        || !farmingEmergencyHolds.TryGetValue(key, out currentHold) || !ReferenceEquals(currentHold, hold)
                        || !runtimes.TryGetValue(runtime.Account.Id, out currentRuntime) || !ReferenceEquals(currentRuntime, runtime)
                        || !runtime.Account.Enabled || VanillaCharacterRoster.Key(runtime.Account) != key
                        || (!closeIssued && runtime.ProcessId != assignedPid) || runtime.ResumeOperationGeneration != operation) return true;
                    // Once the pinned close was issued, only the owned handle can prove
                    // exit. The fleet removing an exited PID must not cancel that proof.
                    if (closeIssued) return false;
                    VanillaCharacterIdentity current = CurrentCharacter(pid);
                    if (current == null || current.Session != session || VanillaCharacterRoster.Key(current) != key) return true;
                    return ObservedCharacters().Count(identity => identity != null
                        && identity.IsFresh(restartEnvironment.UtcNow) && VanillaCharacterRoster.Key(identity) == key) != 1;
                }
            };
            try
            {
                restartEnvironment.Queue(() =>
                {
                    string error = null;
                    bool exited = false;
                    try
                    {
                        if (cancelled()) throw new OperationCanceledException("Emergency close ownership changed.");
                        restartEnvironment.CloseClient(pid, started, cancelled, action =>
                        {
                            lock (gate)
                            {
                                if (cancelled()) throw new OperationCanceledException("Emergency close ownership changed.");
                                action();
                                closeIssued = true;
                            }
                        }, immediate: true);
                        exited = true;
                    }
                    catch (OperationCanceledException)
                    { error = "Close cancelled because ownership, settings or observed session changed; emergency hold retained."; }
                    catch (Exception ex) { error = "Client close failed: " + ex.Message; }
                    lock (gate)
                    {
                        hold.ClosePending = false;
                        FarmingEmergencyHold current;
                        if (!disposed && farmingEmergencyHolds.TryGetValue(key, out current) && ReferenceEquals(current, hold))
                            CompleteFarmingEmergencyCloseLocked(runtime, pid, hold, error, exited);
                    }
                    RaiseUpdated();
                });
            }
            catch (Exception ex)
            {
                hold.ClosePending = false;
                CompleteFarmingEmergencyCloseLocked(runtime, pid, hold, "Cannot queue client close: " + ex.Message, false);
            }
        }

        private void CompleteFarmingEmergencyCloseLocked(Runtime runtime, int pid, FarmingEmergencyHold hold, string error, bool exited)
        {
            hold.CloseState = exited ? "closed" : error != null && error.StartsWith("Close cancelled", StringComparison.Ordinal) ? "cancelled" : "failed";
            hold.Detail = "EMERGENCY STOP: " + hold.Ratios + ". "
                + (exited ? "Affected client exit confirmed; automatic relaunch blocked." : error)
                + " Explicit emergency hold clear is required before resuming.";
            if (exited) hold.ConfirmedExitedPid = pid;
            Runtime current;
            if (runtimes.TryGetValue(runtime.Account.Id, out current) && ReferenceEquals(current, runtime)
                && VanillaCharacterRoster.Key(runtime.Account) == VanillaCharacterRoster.Key(hold.UserName, hold.CharacterName))
            {
                if (exited && runtime.ProcessId == pid && !runtime.ScriptRunning)
                {
                    runtime.ProcessId = null; runtime.CharacterSession = null; runtime.ConfirmedCharacter = null;
                    runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = false;
                    runtime.ResumeSent = runtime.HasBeenOnline = false;
                    runtime.NextRecoveryAt = null; runtime.NonMinimizedSince = null;
                    runtime.Visual = VanillaVisualState.Unknown;
                    ResetTerminalEvidence(runtime);
                }
                SetStage(runtime, VanillaReconnectStage.Error, hold.Detail);
            }
            if (exited)
            {
                try { positionClientExited?.Invoke(pid); }
                catch (Exception ex) { Log("Emergency exited-reader cleanup failed: " + ex.Message); }
            }
            PersistFarmingEmergencyLocked();
            Log(hold.CharacterName + ": " + hold.Detail);
            VanillaDebugLog.Write("EMERGENCY", "event=farming-emergency-close-result pid=" + pid
                + " exited=" + exited + " detail='" + hold.Detail + "'.");
        }

        private bool PersistFarmingEmergencyLocked()
        {
            try
            {
                WriteFarmingEmergencyDocument(farmingEmergencyHolds.Values);
                return true;
            }
            catch (Exception ex)
            {
                farmingEmergencyStoreFailure = "Emergency hold could not be saved: " + ex.Message
                    + " Automation remains held; explicit emergency hold clear is required.";
                VanillaDebugLog.Write("EMERGENCY", farmingEmergencyStoreFailure);
                Log(farmingEmergencyStoreFailure);
                return false;
            }
        }

        private void WriteFarmingEmergencyDocument(IEnumerable<FarmingEmergencyHold> holds)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(farmingEmergencyPath));
            string temporary = farmingEmergencyPath + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(new FarmingEmergencyDocument
                { Holds = holds.ToList() }, Formatting.Indented));
            if (File.Exists(farmingEmergencyPath)) File.Replace(temporary, farmingEmergencyPath, farmingEmergencyPath + ".bak");
            else File.Move(temporary, farmingEmergencyPath);
        }

        public void ClearFarmingEmergencyHolds()
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(VanillaReconnectSupervisor));
                if (farmingEmergencyHolds.Values.Any(hold => hold.ClosePending))
                    throw new InvalidOperationException("An emergency close is still completing; wait for its result before clearing emergency holds.");
                var affected = runtimes.Values.Where(FarmingEmergencyHeld).ToArray();
                if (affected.Any(runtime => runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery)
                    || (temporaryInputOwner != null && FarmingEmergencyHeld(temporaryInputOwner.ProcessId)))
                    throw new InvalidOperationException("Affected client input is still stopping; wait for it to finish before clearing emergency holds.");
                // Do not release the in-memory guard unless the durable clear succeeds.
                WriteFarmingEmergencyDocument(new FarmingEmergencyHold[0]);
                var cleared = new Dictionary<string, FarmingEmergencyHold>(farmingEmergencyHolds, StringComparer.Ordinal);
                string clearedFailure = farmingEmergencyStoreFailure;
                farmingEmergencyHolds.Clear(); farmingEmergencyStoreFailure = null;
                foreach (Runtime runtime in affected)
                {
                    FarmingEmergencyHold hold;
                    string key = VanillaCharacterRoster.Key(runtime.Account);
                    bool explicitHold = key != null && cleared.TryGetValue(key, out hold);
                    if (!explicitHold && runtime.Detail != clearedFailure) continue;
                    hold = explicitHold ? cleared[key] : null;
                    if (hold != null && hold.ConfirmedExitedPid.HasValue && runtime.ProcessId == hold.ConfirmedExitedPid)
                    { runtime.ProcessId = null; runtime.CharacterSession = null; runtime.ConfirmedCharacter = null; }
                    runtime.NextRecoveryAt = null;
                    if (weightManualHolds.Contains(runtime.Account.Id))
                    { SetStage(runtime, VanillaReconnectStage.Error, "Weight/Cart manual hold remains after emergency hold clear"); continue; }
                    if (weightCompletedHolds.Contains(runtime.Account.Id))
                    { SetStage(runtime, VanillaReconnectStage.Stopped, "Farming-complete Weight hold remains after emergency hold clear"); continue; }
                    SetStage(runtime, runtime.ProcessId.HasValue ? VanillaReconnectStage.Online : VanillaReconnectStage.WaitingForClient,
                        "Emergency hold explicitly cleared");
                }
            }
            Log("Emergency farming holds explicitly cleared by user.");
            VanillaDebugLog.Write("EMERGENCY", "event=farming-emergency-holds-cleared source=explicit-user-action.");
            RaiseUpdated();
        }
    }
}
