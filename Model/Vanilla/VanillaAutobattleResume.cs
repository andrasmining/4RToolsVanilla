using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using _4RTools.Utils;

namespace _4RTools.Model.Vanilla
{
    /// <summary>One bounded resume attempt, shared by startup, recovery and the resume diagnostic.</summary>
    internal sealed class VanillaAutobattleResumeVerifier
    {
        internal const int MaximumAttempts = 3;
        internal const int ObservationWindowMs = 10000;
        internal const int PostLoginSettleMs = 10000;
        internal const int RecoveryDeadlineMs = 180000;
        internal const int PollIntervalMs = 100;
        internal const int MaximumSampleAgeMs = 1000;
        private bool started;
        internal int Attempts { get; private set; }
        internal bool MovementVerified { get; private set; }

        internal async Task VerifyAsync(int processId, Func<VanillaClientState> read, System.Action focus, System.Action send,
            Func<int, bool> teleport, Func<bool> cancelled, Func<TimeSpan> clock, Func<DateTimeOffset> utcNow,
            Func<int, Task> delay, System.Action<string> report)
        {
            if (started) throw new InvalidOperationException("A resume verification cannot be restarted.");
            started = true;
            if (processId <= 0 || read == null || focus == null || send == null || teleport == null || cancelled == null
                || clock == null || utcNow == null || delay == null || report == null)
                throw new ArgumentException("Resume verification requires a client and complete observation/input services.");

            VanillaClientState identity = null, baseline = null, previousRead = null;
            TimeSpan lastClock = clock();
            Func<TimeSpan> now = () =>
            {
                TimeSpan value = clock();
                if (value < lastClock) throw new InvalidOperationException("Resume verification clock moved backwards.");
                lastClock = value;
                return value;
            };
            System.Action checkCancelled = () =>
            {
                if (cancelled()) throw new OperationCanceledException("Autobattle verification cancelled; no further hotkeys will be sent.");
            };
            Func<VanillaClientState> sample = () =>
            {
                checkCancelled();
                TimeSpan began = now();
                VanillaClientState state = read();
                checkCancelled();
                TimeSpan finished = now();
                if ((finished - began).TotalMilliseconds > MaximumSampleAgeMs || ReferenceEquals(state, previousRead))
                    throw new InvalidOperationException("Autobattle coordinates are stale; verification stopped.");
                ValidateSample(state, identity, processId, utcNow());
                identity = identity ?? state;
                previousRead = state;
                return state;
            };

            TimeSpan recoveryDeadline = now() + TimeSpan.FromMilliseconds(RecoveryDeadlineMs);
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                checkCancelled();
                if (attempt > 1) report("Retrying autobattle recovery cycle " + attempt + "/" + MaximumAttempts);
                focus();
                checkCancelled();
                VanillaClientState current = sample();
                // Focusing may take time. Recheck after focus, before pressing another toggle.
                if (baseline != null && Moved(baseline, current))
                {
                    MovementVerified = true;
                    report("Movement verified after " + Attempts + "/" + MaximumAttempts + " recovery cycles");
                    return;
                }

                baseline = current;
                Attempts = attempt;
                report("Sending autobattle hotkey attempt " + attempt + "/" + MaximumAttempts);
                send();
                checkCancelled();

                TimeSpan phaseDeadline = now() + TimeSpan.FromMilliseconds(ObservationWindowMs);
                if (phaseDeadline > recoveryDeadline) phaseDeadline = recoveryDeadline;
                report("Autobattle hotkey sent; verifying X/Y movement " + attempt + "/" + MaximumAttempts + " (10s)");
                if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, phaseDeadline).ConfigureAwait(false))
                {
                    MovementVerified = true;
                    report("Movement verified after " + attempt + "/" + MaximumAttempts + " recovery cycles");
                    return;
                }

                checkCancelled();
                current = null;
                try { current = sample(); }
                catch (InvalidOperationException ex) when (IsTransientObservationFailure(ex))
                {
                    report("Client is still loading after autobattle; no teleport/input will be sent until fresh X/Y returns");
                }
                if (current == null)
                {
                    if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, recoveryDeadline).ConfigureAwait(false))
                    {
                        MovementVerified = true;
                        report("Movement verified while waiting for fresh gameplay state");
                        return;
                    }
                    break;
                }
                if (Moved(baseline, current))
                {
                    MovementVerified = true;
                    report("Movement verified before teleport " + attempt + "/" + MaximumAttempts + "; teleport suppressed");
                    return;
                }

                baseline = current;
                report("Still stationary after autobattle " + attempt + "/" + MaximumAttempts
                    + "; trying verified Smart Teleport before any retry");
                bool teleportConfirmed = false;
                try { teleportConfirmed = teleport(attempt); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    report("Smart Teleport attempt " + attempt + "/" + MaximumAttempts + " failed safely: " + ex.Message);
                }
                checkCancelled();

                current = null;
                try { current = sample(); }
                catch (InvalidOperationException ex) when (IsTransientObservationFailure(ex))
                {
                    // A confirmed warp may briefly expose Loading. Do not treat that as stillness,
                    // and never send another action until a fresh gameplay sample exists.
                }
                if (current != null)
                {
                    if (Moved(baseline, current))
                    {
                        MovementVerified = true;
                        report("Movement verified immediately after teleport " + attempt + "/" + MaximumAttempts);
                        return;
                    }
                    baseline = current;
                }

                if (now() >= recoveryDeadline) break;
                phaseDeadline = now() + TimeSpan.FromMilliseconds(ObservationWindowMs);
                if (phaseDeadline > recoveryDeadline) phaseDeadline = recoveryDeadline;
                report((teleportConfirmed ? "Verified teleport completed" : "Teleport attempt completed without confirmed warp")
                    + "; verifying X/Y movement " + attempt + "/" + MaximumAttempts + " (10s)");
                if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, phaseDeadline).ConfigureAwait(false))
                {
                    MovementVerified = true;
                    report("Movement verified after teleport " + attempt + "/" + MaximumAttempts);
                    return;
                }
            }

            TimeSpan remaining = recoveryDeadline - now();
            if (remaining > TimeSpan.Zero)
            {
                report("Three recovery cycles exhausted; monitoring X/Y until the 180s restart deadline");
                if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, recoveryDeadline).ConfigureAwait(false))
                {
                    MovementVerified = true;
                    report("Movement verified during final passive recovery watch");
                    return;
                }
            }
            throw new InvalidOperationException(
                "Failed: no verified X/Y movement after 3 autobattle + teleport recovery cycles within 180 seconds.");
        }

        private static async Task<bool> ObserveMovementAsync(VanillaClientState baseline, Func<VanillaClientState> sample,
            Func<bool> cancelled, Func<TimeSpan> clock, Func<int, Task> delay, TimeSpan deadline)
        {
            while (true)
            {
                if (cancelled()) throw new OperationCanceledException("Autobattle verification cancelled; no further hotkeys will be sent.");
                try
                {
                    VanillaClientState current = sample();
                    if (Moved(baseline, current)) return true;
                }
                catch (InvalidOperationException ex) when (IsTransientObservationFailure(ex))
                {
                    // Loading is expected briefly after a verified teleport. It authorizes no input
                    // but may be observed until fresh gameplay/X-Y state returns.
                }

                TimeSpan remaining = deadline - clock();
                if (remaining <= TimeSpan.Zero) return false;
                await delay(Math.Max(1, Math.Min(PollIntervalMs, (int)Math.Ceiling(remaining.TotalMilliseconds))))
                    .ConfigureAwait(false);
            }
        }

        private static bool IsTransientObservationFailure(InvalidOperationException ex)
        {
            return ex != null && string.Equals(ex.Message, "The client is loading or no longer ready for autobattle.",
                StringComparison.Ordinal);
        }

        private static bool Moved(VanillaClientState before, VanillaClientState after)
        {
            return before.X.Value != after.X.Value || before.Y.Value != after.Y.Value;
        }

        internal static void ValidateSample(VanillaClientState state, VanillaClientState identity, int processId, DateTimeOffset now)
        {
            if (state == null || state.IsDemo || state.Error != null || state.ProcessId != processId || state.SessionId == Guid.Empty)
                throw new InvalidOperationException("Autobattle observation is unavailable or belongs to a different client.");
            double age = (now - state.SampledAtUtc).TotalMilliseconds;
            if (age < 0 || age > MaximumSampleAgeMs)
                throw new InvalidOperationException("Autobattle coordinates are stale; verification stopped.");
            foreach (VanillaField field in new[] { VanillaField.X, VanillaField.Y, VanillaField.Map,
                VanillaField.CharacterName, VanillaField.CurrentHP, VanillaField.MaxHP })
            {
                StateValue value;
                if (state.Fields == null || !state.Fields.TryGetValue(field, out value) || value == null
                    || !value.IsAvailable || value.Validation != StateValidation.Valid
                    || value.LastObservedAtUtc != state.SampledAtUtc)
                    throw new InvalidOperationException("Autobattle requires fresh, verified " + field + "; no hotkey sent for an unknown value.");
            }
            if (state.X.Value < 0 || state.Y.Value < 0 || string.IsNullOrWhiteSpace(state.Map.Value)
                || string.IsNullOrWhiteSpace(state.CharacterName.Value) || state.CurrentHP.Value == 0
                || state.MaxHP.Value == 0 || state.CurrentHP.Value > state.MaxHP.Value)
                throw new InvalidOperationException("Autobattle requires valid coordinates, map and a living character.");
            if ((state.Loading.IsAvailable && state.Loading.Validation == StateValidation.Valid && state.Loading.Value)
                || (state.ClientReady.IsAvailable && state.ClientReady.Validation == StateValidation.Valid && !state.ClientReady.Value))
                throw new InvalidOperationException("The client is loading or no longer ready for autobattle.");
            if (identity != null && (identity.SessionId != state.SessionId
                || !string.Equals(identity.Map.Value, state.Map.Value, StringComparison.Ordinal)
                || !string.Equals(identity.CharacterName.Value, state.CharacterName.Value, StringComparison.Ordinal)))
                throw new InvalidOperationException("Client session, map or character changed during autobattle verification.");
        }
    }

    /// <summary>
    /// Verifies the inverse of resume: after the dedicated Weight STOP hotkey,
    /// fresh X/Y must remain unchanged continuously before Cart UI input is authorized.
    /// </summary>
    internal sealed class VanillaAutobattleStopVerifier
    {
        internal const int MaximumAttempts = 3;
        internal const int AttemptWindowMs = 10000;
        internal const int RequiredStationaryMs = 5000;
        internal const int PollIntervalMs = 100;
        internal const decimal HpDamageAbortPercent = 10m;
        private bool started;
        internal int Attempts { get; private set; }
        internal bool StationaryVerified { get; private set; }
        internal bool HpDamageDetected { get; private set; }

        internal async Task<bool> VerifyAsync(int processId, Func<VanillaClientState> read, System.Action focus,
            System.Action sendStop, Func<bool> cancelled, Func<TimeSpan> clock, Func<DateTimeOffset> utcNow,
            Func<int, Task> delay, System.Action<string> report)
        {
            if (started) throw new InvalidOperationException("An autobattle stop verification cannot be restarted.");
            started = true;
            if (processId <= 0 || read == null || focus == null || sendStop == null || cancelled == null
                || clock == null || utcNow == null || delay == null || report == null)
                throw new ArgumentException("Autobattle stop verification requires complete state/input services.");

            VanillaClientState identity = null, previousRead = null;
            TimeSpan lastClock = clock();
            Func<TimeSpan> now = () =>
            {
                TimeSpan value = clock();
                if (value < lastClock) throw new InvalidOperationException("Autobattle stop verification clock moved backwards.");
                lastClock = value;
                return value;
            };
            System.Action checkCancelled = () =>
            {
                if (cancelled()) throw new OperationCanceledException(
                    "Autobattle stop verification cancelled; no further hotkeys will be sent.");
            };
            Func<VanillaClientState> sample = () =>
            {
                checkCancelled();
                TimeSpan began = now();
                VanillaClientState state = read();
                checkCancelled();
                TimeSpan finished = now();
                if ((finished - began).TotalMilliseconds > VanillaAutobattleResumeVerifier.MaximumSampleAgeMs
                    || ReferenceEquals(state, previousRead))
                    throw new InvalidOperationException("Autobattle stop coordinates are stale; verification stopped.");
                VanillaAutobattleResumeVerifier.ValidateSample(state, identity, processId, utcNow());
                identity = identity ?? state;
                previousRead = state;
                return state;
            };

            decimal? hpBaselinePercent = null;
            for (int attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                checkCancelled();
                focus();
                checkCancelled();
                VanillaClientState previous = sample();
                if (!hpBaselinePercent.HasValue) hpBaselinePercent = HpPercent(previous);
                Attempts = attempt;
                report("Sending Autobattle STOP hotkey attempt " + attempt + "/" + MaximumAttempts);
                sendStop();
                checkCancelled();

                TimeSpan attemptStarted = now();
                TimeSpan attemptDeadline = attemptStarted + TimeSpan.FromMilliseconds(AttemptWindowMs);
                TimeSpan stationarySince = attemptStarted;
                report("Autobattle STOP sent; requiring " + (RequiredStationaryMs / 1000)
                    + " continuous seconds of unchanged X/Y within attempt " + attempt + "/"
                    + MaximumAttempts + " (10s window)");

                while (true)
                {
                    checkCancelled();
                    TimeSpan currentTime = now();
                    if ((currentTime - stationarySince).TotalMilliseconds >= RequiredStationaryMs)
                    {
                        // Require one fresh sample at/after the stationary deadline rather than
                        // allowing elapsed wall time alone to authorize Cart input.
                        VanillaClientState confirmation = sample();
                        if (HpDroppedMoreThan(hpBaselinePercent.Value, confirmation, HpDamageAbortPercent))
                        {
                            HpDamageDetected = true;
                            report("HP dropped by more than " + HpDamageAbortPercent.ToString("0.#")
                                + " percentage points after STOP; Cart/Inventory input is not authorized");
                            return false;
                        }
                        if (!Moved(previous, confirmation))
                        {
                            StationaryVerified = true;
                            report("Autobattle STOP verified after " + attempt + "/" + MaximumAttempts
                                + ": X/Y remained unchanged continuously for at least "
                                + (RequiredStationaryMs / 1000) + " seconds and HP stayed within the damage guard");
                            return true;
                        }
                        previous = confirmation;
                        stationarySince = now();
                        report("X/Y movement appeared at the stationary deadline; stillness window reset");
                    }

                    currentTime = now();
                    if (currentTime >= attemptDeadline) break;

                    int wait = Math.Max(1, Math.Min(PollIntervalMs,
                        (int)Math.Ceiling((attemptDeadline - currentTime).TotalMilliseconds)));
                    await delay(wait).ConfigureAwait(false);
                    checkCancelled();

                    VanillaClientState current = sample();
                    if (HpDroppedMoreThan(hpBaselinePercent.Value, current, HpDamageAbortPercent))
                    {
                        HpDamageDetected = true;
                        report("HP dropped by more than " + HpDamageAbortPercent.ToString("0.#")
                            + " percentage points after STOP; Cart/Inventory input is not authorized");
                        return false;
                    }
                    if (Moved(previous, current))
                    {
                        previous = current;
                        stationarySince = now();
                        report("X/Y movement still observed during STOP attempt " + attempt + "/"
                            + MaximumAttempts + "; 5-second stationary window reset");
                    }
                    else
                    {
                        previous = current;
                    }
                }

                if (attempt < MaximumAttempts)
                    report("Autobattle STOP not verified in the 10-second window; retrying STOP attempt "
                        + (attempt + 1) + "/" + MaximumAttempts);
            }

            report("Autobattle STOP could not be verified after " + MaximumAttempts
                + " attempts; Cart/Inventory input is not authorized");
            return false;
        }

        internal static decimal HpPercent(VanillaClientState state)
        {
            if (state == null || !state.CurrentHP.IsAvailable || !state.MaxHP.IsAvailable
                || state.CurrentHP.Validation != StateValidation.Valid || state.MaxHP.Validation != StateValidation.Valid
                || state.MaxHP.Value == 0 || state.CurrentHP.Value > state.MaxHP.Value)
                throw new InvalidOperationException("Autobattle STOP requires fresh verified HP for the damage guard.");
            return state.CurrentHP.Value * 100m / state.MaxHP.Value;
        }

        internal static bool HpDroppedMoreThan(decimal baselinePercent, VanillaClientState current, decimal thresholdPercent)
        {
            return baselinePercent - HpPercent(current) > thresholdPercent;
        }

        private static bool Moved(VanillaClientState before, VanillaClientState after)
        {
            return before.X.Value != after.X.Value || before.Y.Value != after.Y.Value;
        }
    }

    internal static class VanillaAutobattleStatus
    {
        internal static string Compact(VanillaReconnectStage stage, string detail)
        {
            if (stage != VanillaReconnectStage.VerifyingAutobattle) return stage.ToString();
            detail = detail ?? "";
            if (detail.IndexOf("Movement verified", StringComparison.Ordinal) >= 0) return "Movement verified";
            string prefix = detail.IndexOf("Retrying", StringComparison.Ordinal) >= 0 ? "Retry " : "Verify ";
            for (int attempt = 1; attempt <= VanillaAutobattleResumeVerifier.MaximumAttempts; attempt++)
            {
                string number = attempt + "/" + VanillaAutobattleResumeVerifier.MaximumAttempts;
                if (detail.IndexOf(number, StringComparison.Ordinal) >= 0) return prefix + number;
            }
            return "Verifying autobattle";
        }
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        // Invalidates workers even if STOP is immediately followed by another START with the same PID.
        private int resumeVerificationGeneration;
        private System.Action<string, string, bool> autobattleResumeTestHook;

        internal void SetAutobattleResumeTestHook(System.Action<string, string, bool> hook)
        { lock (gate) autobattleResumeTestHook = hook; }

        private void RequestVerifiedResume(Runtime runtime, string trigger, bool movementRecovery)
        {
            var hook = autobattleResumeTestHook;
            if (hook != null) { hook(runtime.Account.Id, trigger, movementRecovery); return; }
            QueueVerifiedResume(runtime, trigger, movementRecovery);
        }

        // Shipped build profiles live beside the executable, never in mutable user data.
        internal static string AutobattleBuildProfileDirectory
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBuilds"); }
        }

        private bool ResumeWorkerCancelled(Runtime owner, int pid, int generation)
        {
            lock (gate)
            {
                Runtime current;
                return disposed || generation != Volatile.Read(ref resumeVerificationGeneration)
                    || !runtimes.TryGetValue(owner.Account.Id, out current) || !ReferenceEquals(owner, current)
                    || current.ProcessId != pid || current.ResumeOperationGeneration != generation
                    || !current.Account.Enabled || !current.ScriptRunning || CharacterOwnershipChanged(current, pid);
            }
        }

        private void ResumeProgress(Runtime owner, int pid, int generation, string detail)
        {
            lock (gate)
            {
                Runtime current;
                if (generation != Volatile.Read(ref resumeVerificationGeneration) || disposed || !runtimes.TryGetValue(owner.Account.Id, out current)
                    || !ReferenceEquals(owner, current) || current.ProcessId != pid
                    || current.ResumeOperationGeneration != generation || !current.ScriptRunning) return;
                SetStage(current, VanillaReconnectStage.VerifyingAutobattle, detail);
            }
            Log(owner.Account.Label + ": " + detail);
            VanillaDebugLog.Write("AUTOBATTLE", "PID=" + pid + "; " + owner.Account.Label + ": " + detail);
            RaiseUpdated();
        }

        internal static bool AutobattleVisualBlocksInput(VanillaVisualState visual)
        {
            return visual == VanillaVisualState.LoginShell || visual == VanillaVisualState.ModalDialog
                || visual == VanillaVisualState.LoggingOut || visual == VanillaVisualState.Disconnected;
        }

        private void WaitForAutobattleReady(VanillaReconnectAccount account, int pid, Func<bool> cancelled,
            int timeoutMs, string context)
        {
            WaitForVerifiedGameplayMemoryState(account, pid, cancelled, timeoutMs, context, true);
        }

        private void WaitForExistingClientGameplayReady(VanillaReconnectAccount account, int pid, Func<bool> cancelled,
            int timeoutMs, string context)
        {
            WaitForVerifiedGameplayMemoryState(account, pid, cancelled, timeoutMs, context, false);
        }

        private void WaitForVerifiedGameplayMemoryState(VanillaReconnectAccount account, int pid, Func<bool> cancelled,
            int timeoutMs, string context, bool postLoginHotkey)
        {
            if (account == null) throw new ArgumentNullException(nameof(account));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            string purpose = postLoginHotkey ? "mandatory post-login hotkey" : "existing-client adoption";
            Log(account.Label + ": " + context + ": waiting for fresh verified username/character/X/Y/map/living HP for " + purpose + ".");
            VanillaDebugLog.Write("MEMORY-GATE", "PID=" + pid + "; " + account.Label + ": " + context
                + ": waiting for verified gameplay memory state; purpose=" + purpose + ".");
            using (var memory = new ReadOnlyProcessMemory(pid))
            {
                var identity = VanillaExecutableIdentity.Read(memory.ExecutablePath);
                var profile = VanillaBuildProfile.Find(AutobattleBuildProfileDirectory, identity,
                    message => Log(account.Label + ": " + message));
                if (profile == null) throw new InvalidOperationException("No verified Vanilla build profile; gameplay memory state cannot be proven.");
                var adapter = new VanillaStateAdapter(profile, identity);
                using (var source = new MemoryStateSource(memory, profile.MemoryMap))
                {
                    var watch = Stopwatch.StartNew();
                    int consecutive = 0;
                    string last = "no valid sample yet";
                    while (watch.ElapsedMilliseconds < timeoutMs)
                    {
                        if (cancelled()) throw new OperationCanceledException(context + ": gameplay memory verification cancelled.");
                        var now = DateTimeOffset.UtcNow;
                        try
                        {
                            var state = source.Poll(now);
                            if (source.IsStopped) throw new InvalidOperationException(state.Error ?? source.Status);
                            adapter.Observe(state, watch.Elapsed);
                            ValidateExpectedCharacter(account, state);
                            VanillaAutobattleResumeVerifier.ValidateSample(state, null, pid, now);
                            consecutive++;
                            last = "verified " + state.UserName.Value + "/" + state.CharacterName.Value
                                + " map=" + state.Map.Value + " xy=" + state.X.Value + "," + state.Y.Value
                                + " hp=" + state.CurrentHP.Value + "/" + state.MaxHP.Value;
                            if (consecutive >= 2)
                            {
                                Log(account.Label + ": " + context + ": gameplay memory state ready after "
                                    + watch.ElapsedMilliseconds + "ms (" + last + ").");
                                VanillaDebugLog.Write("MEMORY-GATE", "PID=" + pid + "; " + account.Label + ": " + context
                                    + ": accepted by two fresh verified gameplay-memory samples; " + last + ".");
                                return;
                            }
                        }
                        catch (InvalidOperationException ex)
                        {
                            if (source.IsStopped) throw;
                            consecutive = 0;
                            last = ex.Message;
                        }
                        Thread.Sleep(150);
                    }
                    throw new InvalidOperationException(context + ": fresh verified gameplay memory state was not ready within "
                        + (timeoutMs / 1000) + "s; "
                        + (postLoginHotkey ? "no autobattle hotkey was sent. " : "existing client was not adopted. ")
                        + "Last state: " + last);
                }
            }
        }

        private async Task VerifyAutobattleResumeAsync(VanillaReconnectAccount account, int pid,
            Func<bool> cancelled, System.Action<string> progress)
        {
            if (cancelled()) throw new OperationCanceledException("Autobattle verification cancelled.");
            using (var memory = new ReadOnlyProcessMemory(pid))
            {
                var identity = VanillaExecutableIdentity.Read(memory.ExecutablePath);
                var profile = VanillaBuildProfile.Find(AutobattleBuildProfileDirectory, identity,
                    message => Log(account.Label + ": " + message));
                if (profile == null) throw new InvalidOperationException("No verified Vanilla build profile; autobattle resume was not sent.");
                var adapter = new VanillaStateAdapter(profile, identity);
                var file = new FileInfo(memory.ExecutablePath);
                long executableLength = file.Length;
                DateTime executableWriteTime = file.LastWriteTimeUtc;
                using (var source = new MemoryStateSource(memory, profile.MemoryMap))
                using (var input = new VanillaForegroundInput(pid))
                {
                    input.CancellationRequested = cancelled;
                    var clock = Stopwatch.StartNew();
                    var verifier = new VanillaAutobattleResumeVerifier();
                    VanillaClientState lastIdentity = null;
                    Func<VanillaClientState> read = () =>
                    {
                        var currentFile = new FileInfo(memory.ExecutablePath);
                        if (!currentFile.Exists || currentFile.Length != executableLength || currentFile.LastWriteTimeUtc != executableWriteTime)
                            throw new InvalidOperationException("The Vanilla executable changed during verification.");
                        VanillaVisualState visual = VanillaVisualState.Unknown;
                        try { visual = VanillaVisualProbe.Classify(input.Window); }
                        catch (Exception ex)
                        {
                            VanillaDebugLog.Write("AUTOBATTLE", "PID=" + pid
                                + "; visual probe unavailable during memory-verified resume: " + ex.Message);
                        }
                        if (AutobattleVisualBlocksInput(visual))
                            throw new InvalidOperationException("Client is in blocking visual state " + visual
                                + "; autobattle verification stopped before input.");
                        var state = source.Poll(DateTimeOffset.UtcNow);
                        if (source.IsStopped) throw new InvalidOperationException(state.Error ?? source.Status);
                        adapter.Observe(state, clock.Elapsed);
                        ValidateExpectedCharacter(account, state);
                        lastIdentity = state;
                        return state;
                    };
                    await verifier.VerifyAsync(pid, read, input.Activate,
                        () => input.ChordInVerifiedForeground(account.ResumeCtrl, account.ResumeAlt, account.ResumeShift,
                            (Keys)account.ResumeKey),
                        attempt =>
                        {
                            string detail;
                            bool confirmed = VanillaVerifiedTeleportAction.TryExecute(pid, account, cancelled,
                                "autobattle-recovery-" + attempt, out detail);
                            progress("Teleport recovery " + attempt + "/" + VanillaAutobattleResumeVerifier.MaximumAttempts
                                + ": " + detail);
                            return confirmed;
                        },
                        cancelled, () => clock.Elapsed, () => DateTimeOffset.UtcNow,
                        milliseconds => Task.Delay(milliseconds), progress).ConfigureAwait(false);
                    lock (gate)
                    {
                        Runtime owner;
                        if (!cancelled() && runtimes.TryGetValue(account.Id, out owner) && owner.ProcessId == pid)
                        {
                            owner.ConfirmedCharacter = VanillaCharacterIdentity.FromState(lastIdentity);
                            var fleetIdentity = CurrentCharacter(pid);
                            if (fleetIdentity != null && owner.ConfirmedCharacter != null
                                && VanillaCharacterRoster.Key(fleetIdentity) != null
                                && VanillaCharacterRoster.Key(fleetIdentity) == VanillaCharacterRoster.Key(owner.ConfirmedCharacter))
                                owner.CharacterSession = fleetIdentity.Session;
                        }
                    }
                }
            }
        }

        private bool RunOwnedClientStep(Runtime owner, int pid, Func<bool> cancelled, Func<bool> action)
        {
            lock (gate)
            {
                Runtime current;
                if (cancelled() || disposed || !runtimes.TryGetValue(owner.Account.Id, out current)
                    || !ReferenceEquals(owner, current) || current.ProcessId != pid || !current.Account.Enabled || CharacterOwnershipChanged(current, pid))
                    throw new OperationCanceledException("Client ownership changed; no window action performed.");
                return action();
            }
        }

        internal static bool CanAdoptExistingGameplayClient(bool resumeSent, bool failed, bool busy)
        {
            return resumeSent && !failed && !busy;
        }

        private static void RecordDiagnosticResumeResult(Runtime runtime, bool succeeded, string failure)
        {
            runtime.ResumeSent = succeeded;
            runtime.ResumeVerificationFailed = !succeeded;
            runtime.ResumeFailureDetail = succeeded ? null : "Autobattle verification failed: " + failure;
            if (succeeded) runtime.HasBeenOnline = true;
        }

        private void CompleteAutobattleRecoverySuccessLocked(Runtime runtime)
        {
            runtime.MovementRecoveryPending = false;
            runtime.MovementWatchdog.Reset();
            ResetRecoverySuccessLocked(runtime);
        }

        private void QueueAutobattleClientRestartLocked(Runtime runtime, DateTimeOffset now, string reason)
        {
            runtime.RecoveryOwned = false;
            runtime.ScriptRunning = false;
            runtime.ResumeSent = false;
            runtime.MovementRecoveryPending = true;
            Runtime owner = OtherRecoveryOwner(runtime);
            if (owner != null)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForClient,
                    "Restart queued behind " + owner.Account.Label + "; no steady-state hotkey will be sent");
                Log(runtime.Account.Label + ": restart is queued behind " + owner.Account.Label
                    + "; the failed 3-cycle autoattack/teleport recovery will not be repeated on the existing client.");
                return;
            }
            int pid = runtime.ProcessId.GetValueOrDefault();
            if (pid <= 0)
            {
                runtime.MovementRecoveryPending = false;
                ScheduleRecoveryFailureLocked(runtime, now, reason + "; affected PID is no longer available");
                return;
            }
            QueueClientRestart(runtime, now, reason + ". Closing this client before the next retry.", true,
                () => restartEnvironment.GetStartTimeUtc(pid));
        }

        private void QueueVerifiedResume(Runtime runtime)
        {
            QueueVerifiedResume(runtime, null, false);
        }

        private void QueueVerifiedResume(Runtime runtime, string trigger, bool movementRecovery)
        {
            if (runtime.ScriptRunning || !runtime.ProcessId.HasValue) return;
            if (runtime.ResumeVerificationFailed && !movementRecovery) return;
            Runtime owner = OtherRecoveryOwner(runtime);
            if (owner != null)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForGameplay,
                    "Queued: waiting for " + owner.Account.Label + " before autobattle verification");
                return;
            }
            int pid = runtime.ProcessId.Value;
            int generation = Interlocked.Increment(ref resumeVerificationGeneration);
            runtime.ResumeOperationGeneration = generation;
            var account = runtime.Account.Clone();
            runtime.ScriptRunning = true;
            runtime.RecoveryOwned = true;
            runtime.ResumeSent = false;
            string preparation = (string.IsNullOrWhiteSpace(trigger) ? "" : trigger + " ")
                + "Preparing autoattack + teleport recovery cycle 1/3";
            SetStage(runtime, VanillaReconnectStage.VerifyingAutobattle, preparation);
            Log(account.Label + ": " + preparation);
            // The continuation is owned by this Task, never an async-void ThreadPool callback.
            Task.Run(async () =>
            {
                string error = null;
                bool cancelled = false;
                try
                {
                    await VerifyAutobattleResumeAsync(account, pid,
                        () => !IsRunning || ResumeWorkerCancelled(runtime, pid, generation),
                        detail => ResumeProgress(runtime, pid, generation, detail)).ConfigureAwait(false);
                    if (!IsRunning || ResumeWorkerCancelled(runtime, pid, generation)) throw new OperationCanceledException();
                    if (!WaitForOwnedClientSafeMinimize(runtime, pid,
                        () => !IsRunning || ResumeWorkerCancelled(runtime, pid, generation), account.Label + ": autobattle recovery", true))
                        throw new InvalidOperationException("Movement verified but client minimization could not be confirmed.");
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex) { error = ex.Message; }
                lock (gate)
                {
                    // An old worker must never publish success or clear a new worker's input lease.
                    Runtime current;
                    if (!runtimes.TryGetValue(account.Id, out current) || !ReferenceEquals(runtime, current)
                        || runtime.ProcessId != pid || runtime.ResumeOperationGeneration != generation || !runtime.ScriptRunning) return;
                    cancelled = cancelled || !running || ResumeWorkerCancelled(runtime, pid, generation);
                    runtime.ScriptRunning = false;
                    if (cancelled)
                    {
                        runtime.RecoveryOwned = false;
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Autobattle verification cancelled");
                    }
                    else if (error != null)
                    {
                        runtime.ResumeVerificationFailed = true;
                        runtime.ResumeFailureDetail = "Autobattle verification failed: " + error;
                        runtime.MovementRecoveryPending = true;
                        Log(account.Label + ": " + runtime.ResumeFailureDetail);
                        runtime.ScriptRunning = false;
                        QueueAutobattleClientRestartLocked(runtime, restartEnvironment.UtcNow, runtime.ResumeFailureDetail);
                    }
                    else
                    {
                        runtime.ResumeSent = true;
                        runtime.ResumeVerificationFailed = false;
                        runtime.ResumeFailureDetail = null;
                        runtime.HasBeenOnline = true;
                        CompleteAutobattleRecoverySuccessLocked(runtime);
                        SetStage(runtime, VanillaReconnectStage.Online, "Movement verified; client minimized; recovery budget reset");
                    }
                }
                RaiseUpdated();
            }).ContinueWith(task =>
            {
                // Consume unexpected infrastructure/UI subscriber failures; do not terminate the application.
                VanillaDebugLog.Write("AUTOBATTLE", "Resume worker failed: " + task.Exception);
            }, TaskContinuationOptions.OnlyOnFaulted);
        }
    }
}
