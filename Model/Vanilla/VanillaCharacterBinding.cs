using System;
using System.Collections.Generic;
using System.Linq;

namespace _4RTools.Model.Vanilla
{
    public sealed partial class VanillaReconnectSupervisor
    {
        private Func<IReadOnlyList<VanillaCharacterIdentity>> characterSource;

        internal void SetCharacterSource(Func<IReadOnlyList<VanillaCharacterIdentity>> source)
        { lock (gate) characterSource = source ?? throw new ArgumentNullException(nameof(source)); }

        internal IReadOnlyList<VanillaCharacterIdentity> ObservedCharacters()
        {
            var source = characterSource;
            if (source == null) return new VanillaCharacterIdentity[0];
            try { return source() ?? new VanillaCharacterIdentity[0]; }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("IDENTITY", "Character observations unavailable: " + ex.Message);
                return new VanillaCharacterIdentity[0];
            }
        }

        private VanillaCharacterIdentity CurrentCharacter(int pid)
        {
            var matches = ObservedCharacters().Where(i => i != null && i.ProcessId == pid && i.IsFresh(DateTimeOffset.UtcNow)).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }

        private bool CharacterOwnershipChanged(Runtime runtime, int pid)
        {
            var observed = CurrentCharacter(pid);
            // An unavailable field is not proof of replacement. Known owners keep their
            // failure/watchdog state; unknown observations may not establish a new owner.
            if (observed == null) return false;
            return (runtime.CharacterSession.HasValue && runtime.CharacterSession.Value != observed.Session)
                || (!string.IsNullOrWhiteSpace(runtime.Account.CharacterName)
                    && !VanillaCharacterRoster.Same(runtime.Account.CharacterName, observed.CharacterName))
                || (!string.IsNullOrWhiteSpace(runtime.Account.UserName) && observed.UserName != null
                    && !VanillaCharacterRoster.Same(runtime.Account.UserName, observed.UserName))
                || (runtime.Account.CharacterSlot.HasValue && observed.CharacterSlot.HasValue
                    && runtime.Account.CharacterSlot != observed.CharacterSlot);
        }

        private void ReleaseChangedCharacter(Runtime runtime)
        {
            serverOutage.CompleteFailure(runtime.Account.Id, restartEnvironment.MonotonicNow, restartEnvironment.UtcNow);
            runtime.ServerOutagePending = false;
            int? pid = runtime.ProcessId;
            runtime.ResumeOperationGeneration++;
            runtime.ProcessId = null;
            runtime.CharacterSession = null;
            runtime.ConfirmedCharacter = null;
            runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = false;
            runtime.ResumeSent = runtime.HasBeenOnline = runtime.MovementRecoveryPending = false;
            runtime.NonMinimizedSince = null;
            runtime.MovementWatchdog.Reset();
            ResetTerminalEvidence(runtime);
            SetStage(runtime, VanillaReconnectStage.WaitingForClient, "Character/session changed; old client left untouched");
            Log(runtime.Account.Label + ": released PID " + pid + " after character/session replacement; no input or close sent.");
        }

        private VanillaCharacterIdentity FindUnclaimedCharacter(Runtime runtime, IEnumerable<int> pids)
        {
            var observed = ObservedCharacters();
            var now = DateTimeOffset.UtcNow;
            var match = VanillaCharacterRoster.FindUnique(runtime.Account, observed, pids, now);
            if (match == null) return null;
            // A named row and an old unnamed row must not race to claim the same client.
            if (settings.Accounts.Count(a => a.Enabled && VanillaCharacterRoster.Matches(a, match, now)) != 1) return null;
            return match;
        }

        internal IReadOnlyDictionary<string, VanillaCharacterIdentity> ConfirmedCharacters()
        {
            lock (gate)
            {
                return runtimes.Values.Where(r => r.ProcessId.HasValue && r.ConfirmedCharacter != null
                    && r.ConfirmedCharacter.ProcessId == r.ProcessId && r.ResumeSent && !r.ScriptRunning
                    && !r.ResumeVerificationFailed && !CharacterOwnershipChanged(r, r.ProcessId.Value))
                    .ToDictionary(r => r.Account.Id, r => r.ConfirmedCharacter);
            }
        }

        internal bool CharacterDiscoveryPending(int pid)
        {
            lock (gate) return runtimes.Values.Any(r => r.ProcessId == pid && (r.ScriptRunning || r.RecoveryOwned)
                && string.IsNullOrWhiteSpace(r.Account.CharacterName));
        }

        // Optional identity metadata discovery must not call Apply: that cancels running recovery.
        // Only fill missing fields; never overwrite user choices, passwords, proxies or enabled state.
        internal void EnrichCharacterMetadata(IEnumerable<VanillaReconnectAccount> catalog)
        {
            bool changed = false;
            lock (gate)
            {
                if (disposed) return;
                foreach (var row in catalog ?? Enumerable.Empty<VanillaReconnectAccount>())
                {
                    Runtime runtime;
                    if (!runtimes.TryGetValue(row.Id, out runtime) || runtime.ScriptRunning || runtime.RecoveryOwned) continue;
                    var sample = VanillaCharacterRoster.FindUnique(row, ObservedCharacters(),
                        ObservedCharacters().Where(i => i != null).Select(i => i.ProcessId), DateTimeOffset.UtcNow);
                    if (sample == null) continue;
                    bool ownedConfirmation = runtime.ConfirmedCharacter != null && runtime.ProcessId == sample.ProcessId
                        && runtime.ResumeSent && VanillaCharacterRoster.Key(runtime.ConfirmedCharacter) != null
                        && VanillaCharacterRoster.Key(runtime.ConfirmedCharacter) == VanillaCharacterRoster.Key(sample);
                    if (!ownedConfirmation && !VanillaCharacterRoster.Compatible(runtime.Account, sample)) continue;
                    changed |= VanillaCharacterRoster.FillMissing(runtime.Account, sample);
                    var stored = settings.Accounts.FirstOrDefault(a => a.Id == row.Id);
                    if (stored != null) changed |= VanillaCharacterRoster.FillMissing(stored, sample);
                }
                if (changed) store.Save(settings);
            }
        }

        internal static void ValidateExpectedCharacter(VanillaReconnectAccount row, VanillaClientState state)
        {
            var identity = VanillaCharacterIdentity.FromState(state);
            if (identity == null || !identity.IsFresh(DateTimeOffset.UtcNow) || identity.UserName == null
                || !VanillaCharacterRoster.Same(row.UserName, identity.UserName))
                throw new InvalidOperationException(row.Label + ": expected login username is not verified in this client; no Autobattle hotkey sent.");
            if (string.IsNullOrWhiteSpace(row.CharacterName)) return; // legacy configured launch learns its name after verified resume
            if (!VanillaCharacterRoster.Matches(row, identity, DateTimeOffset.UtcNow))
                throw new InvalidOperationException(row.Label + ": expected character '" + row.CharacterName
                    + "' is not verified in this client; no Autobattle hotkey sent.");
        }

        internal static string MissingCharacterConfiguration(VanillaReconnectAccount row)
        {
            if (string.IsNullOrWhiteSpace(row.UserName)) return "Username is unknown";
            if (string.IsNullOrWhiteSpace(row.ProtectedPassword)) return "Password is not set";
            if (!row.CharacterSlot.HasValue || row.CharacterSlot < 1 || row.CharacterSlot > 15) return "Character slot is unknown or invalid";
            if (row.ProxyNeedsConfiguration) return "Proxy is not selected";
            return null;
        }
    }
}
