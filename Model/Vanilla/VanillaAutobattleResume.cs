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
            Func<int, Task> delay, System.Action<string> report, bool teleportFirst = false,
            int recoveryLimitMs = RecoveryDeadlineMs, bool allowResume = true)
        {
            if (started) throw new InvalidOperationException("A resume verification cannot be restarted.");
            started = true;
            if (processId <= 0 || read == null || focus == null || send == null || teleport == null || cancelled == null
                || clock == null || utcNow == null || delay == null || report == null || recoveryLimitMs <= 0)
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

            TimeSpan recoveryDeadline = now() + TimeSpan.FromMilliseconds(Math.Min(RecoveryDeadlineMs, recoveryLimitMs));
            if (teleportFirst)
            {
                baseline = sample();
                focus();
                checkCancelled();
                VanillaClientState current = sample();
                if (Moved(baseline, current))
                { MovementVerified = true; report("Movement resumed while activating; stationary recovery input suppressed"); return; }
                if (now() < recoveryDeadline)
                {
                    baseline = current;
                    report("Stationary gameplay confirmed; trying Smart Teleport before Autobattle recovery");
                    try { teleport(0); }
                    catch (OperationCanceledException) { throw; }
                    catch (VanillaServerClosedException) { throw; }
                    catch (Exception ex) { report("Initial Smart Teleport failed safely: " + ex.Message); }
                    checkCancelled();
                    TimeSpan firstDeadline = now() + TimeSpan.FromMilliseconds(ObservationWindowMs);
                    if (firstDeadline > recoveryDeadline) firstDeadline = recoveryDeadline;
                    if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, firstDeadline).ConfigureAwait(false))
                    { MovementVerified = true; report("Movement verified after initial Smart Teleport; no Autobattle hotkey needed"); return; }
                }
            }
            for (int attempt = 1; allowResume && attempt <= MaximumAttempts; attempt++)
            {
                checkCancelled();
                if (now() >= recoveryDeadline) break;
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
                if (now() >= recoveryDeadline) break;

                baseline = current;
                if (now() >= recoveryDeadline) break;
                Attempts = attempt;
                report("Sending autobattle hotkey attempt " + attempt + "/" + MaximumAttempts);
                current = sample();
                if (Moved(baseline, current))
                { MovementVerified = true; report("Movement resumed before hotkey; remaining recovery input suppressed"); return; }
                if (now() >= recoveryDeadline) break;
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

                if (now() >= recoveryDeadline) break;
                baseline = current;
                report("Still stationary after autobattle " + attempt + "/" + MaximumAttempts
                    + "; trying verified Smart Teleport before any retry");
                bool teleportConfirmed = false;
                try { teleportConfirmed = teleport(attempt); }
                catch (OperationCanceledException) { throw; }
                catch (VanillaServerClosedException) { throw; }
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
                report("Recovery cycles exhausted; passively monitoring X/Y until the remaining restart deadline");
                if (await ObserveMovementAsync(baseline, sample, cancelled, now, delay, recoveryDeadline).ConfigureAwait(false))
                {
                    MovementVerified = true;
                    report("Movement verified during final passive recovery watch");
                    return;
                }
            }
            throw new InvalidOperationException(
                "Failed: no verified X/Y movement before the bounded autobattle/teleport recovery deadline.");
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
            return before != null && (before.X.Value != after.X.Value || before.Y.Value != after.Y.Value
                || (after.LastMovementAtUtc.HasValue && after.LastMovementAtUtc > before.SampledAtUtc)
                || after.X.LastChangedAtUtc > before.SampledAtUtc || after.Y.LastChangedAtUtc > before.SampledAtUtc);
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
        internal decimal? HpBaselinePercent { get; private set; }

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
                if (!hpBaselinePercent.HasValue)
                {
                    hpBaselinePercent = HpPercent(previous);
                    HpBaselinePercent = hpBaselinePercent;
                }
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
            if (stage == VanillaReconnectStage.WaitingForServer)
            {
                const string marker = "next check ";
                int next = (detail ?? "").IndexOf(marker, StringComparison.Ordinal);
                return next >= 0 && detail.Length >= next + marker.Length + 8
                    ? "Server down; check " + detail.Substring(next + marker.Length, 8) : "Server down; queued";
            }
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
            if (FarmingEmergencyHeld(runtime)) return;
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
                    || !current.Account.Enabled || !current.ScriptRunning || FarmingEmergencyHeld(current)
                    || CharacterOwnershipChanged(current, pid);
            }
        }

        private void ResumeProgress(Runtime owner, int pid, int generation, string detail)
        {
            lock (gate)
            {
                Runtime current;
                if (generation != Volatile.Read(ref resumeVerificationGeneration) || disposed || !runtimes.TryGetValue(owner.Account.Id, out current)
                    || !ReferenceEquals(owner, current) || current.ProcessId != pid
                    || current.ResumeOperationGeneration != generation || !current.ScriptRunning || FarmingEmergencyHeld(current)) return;
                SetStage(current, VanillaReconnectStage.VerifyingAutobattle, detail);
            }
            Log(owner.Account.Label + ": " + detail);
            VanillaDebugLog.Write("AUTOBATTLE", "PID=" + pid + "; " + owner.Account.Label + ": " + detail);
            RaiseUpdated();
        }

        internal static bool AutobattleVisualBlocksInput(VanillaVisualState visual)
        {
            return visual == VanillaVisualState.LoginShell || visual == VanillaVisualState.ModalDialog
                || visual == VanillaVisualState.LoggingOut || visual == VanillaVisualState.Disconnected
                || visual == VanillaVisualState.ServerClosed;
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
            await VerifyAutobattleResumeCoreAsync(account, pid, cancelled, progress, false,
                VanillaAutobattleResumeVerifier.RecoveryDeadlineMs, null, true, null).ConfigureAwait(false);
        }

        private async Task<bool> VerifyAutobattleResumeCoreAsync(VanillaReconnectAccount account, int pid,
            Func<bool> cancelled, System.Action<string> progress, bool stationary, int limitMs,
            Func<bool> movementResumed, bool allowResume, VanillaPositionSample stationaryBaseline)
        {
            Func<bool> callerCancelled = cancelled;
            cancelled = () => callerCancelled() || FarmingEmergencyHeld(account) || FarmingEmergencyHeld(pid)
                || FarmingCompletionHeld(account);
            if (cancelled()) throw new OperationCanceledException("Autobattle verification cancelled.");
            bool touched = false;
            var clock = Stopwatch.StartNew();
            VanillaTeleportMovementGuard memoryMovement = null;
            Func<bool> moved = () => (movementResumed != null && movementResumed())
                || (memoryMovement != null && memoryMovement.MovementResumed());
            System.Action checkBudget = () =>
            {
                if (clock.ElapsedMilliseconds >= limitMs)
                    throw new InvalidOperationException("Original movement-recovery deadline reached; no further input authorized.");
            };
            Func<bool> inputCancelled = () =>
            { if (cancelled() || moved()) return true; checkBudget(); return false; };
            try
            {
                // Recheck the original stall before acquiring/restoring any foreground window.
                if (moved()) return false;
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
                    {
                        VanillaClientState lastIdentity = null;
                        Func<VanillaClientState> read = () =>
                        {
                            if (cancelled()) throw new OperationCanceledException();
                            var currentFile = new FileInfo(memory.ExecutablePath);
                            if (!currentFile.Exists || currentFile.Length != executableLength || currentFile.LastWriteTimeUtc != executableWriteTime)
                                throw new InvalidOperationException("The Vanilla executable changed during verification.");
                            var state = source.Poll(DateTimeOffset.UtcNow);
                            if (source.IsStopped) throw new InvalidOperationException(state.Error ?? source.Status);
                            adapter.Observe(state, clock.Elapsed);
                            ValidateExpectedCharacter(account, state);
                            lastIdentity = state;
                            return state;
                        };
                        VanillaClientState initial = read();
                        VanillaAutobattleResumeVerifier.ValidateSample(initial, null, pid, DateTimeOffset.UtcNow);
                        if (stationaryBaseline != null)
                        {
                            if (!string.Equals(initial.Map.Value, stationaryBaseline.Map, StringComparison.Ordinal))
                                throw new OperationCanceledException("Original stationary map changed before recovery.");
                            if (initial.X.Value != stationaryBaseline.X || initial.Y.Value != stationaryBaseline.Y) return false;
                        }
                        memoryMovement = new VanillaTeleportMovementGuard(initial, read);
                        if (moved()) return false;
                        int remaining = limitMs - (int)clock.ElapsedMilliseconds;
                        if (remaining <= 0) throw new InvalidOperationException("Original movement-recovery deadline reached before foreground input.");
                        touched = true;
                        using (var input = new VanillaForegroundInput(pid, IntPtr.Zero, inputCancelled))
                        {
                            System.Action focus = () =>
                            {
                                if (inputCancelled()) throw new OperationCanceledException();
                                using (var image = input.CaptureClientBitmapForObservation())
                                {
                                    if (!VanillaTeleportVision.FrameLooksUsable(image))
                                        throw new InvalidOperationException("Foreground gameplay frame is unavailable; no recovery input sent.");
                                    var visual = VanillaVisualProbe.Classify(image);
                                    if (visual == VanillaVisualState.ServerClosed) ThrowIfConfirmedServerClosed(pid, cancelled);
                                    if (AutobattleVisualBlocksInput(visual))
                                        throw new InvalidOperationException("Blocking foreground state " + visual + "; no recovery input sent.");
                                }
                            };
                            await new VanillaAutobattleResumeVerifier().VerifyAsync(pid, read, focus,
                                () =>
                                {
                                    if (inputCancelled()) throw new OperationCanceledException();
                                    input.ChordInVerifiedForeground(account.ResumeCtrl, account.ResumeAlt, account.ResumeShift,
                                        (Keys)account.ResumeKey);
                                }, attempt =>
                                {
                                    if (inputCancelled()) throw new OperationCanceledException();
                                    if (stationary && !account.SmartTeleportEnabled)
                                    { progress("Smart Teleport disabled for this character; skipping teleport phase"); return false; }
                                    var guard = new VanillaTeleportMovementGuard(read(), read);
                                    string detail;
                                    bool confirmed = VanillaVerifiedTeleportAction.TryExecute(pid, account,
                                        () => { if (cancelled()) return true; checkBudget(); return false; },
                                        "autobattle-recovery-" + attempt, out detail,
                                        () => moved() || guard.MovementResumed());
                                    progress("Teleport recovery " + attempt + "/" + VanillaAutobattleResumeVerifier.MaximumAttempts + ": " + detail);
                                    return confirmed;
                                }, cancelled, () => clock.Elapsed, () => DateTimeOffset.UtcNow,
                                milliseconds => Task.Delay(milliseconds), progress, stationary,
                                Math.Max(1, limitMs - (int)clock.ElapsedMilliseconds), allowResume).ConfigureAwait(false);
                        }
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
            catch (OperationCanceledException) when (!cancelled() && moved())
            { progress("Fresh X/Y movement resumed; remaining recovery input suppressed"); }
            return touched;
        }

        private bool StationaryMovementResumed(Runtime runtime, int pid, long version, VanillaPositionSample baseline)
        {
            lock (gate)
            {
                var sample = positionSource?.Invoke(pid);
                var now = restartEnvironment.UtcNow;
                if (sample == null || !sample.Verified || sample.Error != null || sample.Pid != pid
                    || !sample.X.HasValue || !sample.Y.HasValue || sample.At > now || (now - sample.At).TotalSeconds > 3)
                    throw new InvalidOperationException("Fresh stationary coordinates became unavailable before input.");
                if (sample.Session != baseline.Session || !string.Equals(sample.Map, baseline.Map, StringComparison.Ordinal))
                    throw new OperationCanceledException("Stationary client session/map changed.");
                runtime.MovementWatchdog.Observe(pid, sample, restartEnvironment.MonotonicNow, now);
                return runtime.MovementWatchdog.ProgressVersion != version
                    || sample.X != baseline.X || sample.Y != baseline.Y
                    || (sample.MovementAt.HasValue && sample.MovementAt > baseline.At);
            }
        }

        private bool TryQueueStalledAutobattleRecovery(Runtime runtime)
        {
            bool teleport = runtime.Account.SmartTeleportEnabled && runtime.Account.SmartTeleportKey >= 8;
            if (!running || disposed || (!settings.AutoRecover && !teleport) || !runtime.Account.Enabled
                || !runtime.ProcessId.HasValue || runtime.ScriptRunning || runtime.RecoveryOwned || runtime.ClosingForRecovery
                || (!runtime.HasBeenOnline && !runtime.ResumeSent) || FarmingEmergencyHeld(runtime)
                || FarmingCompletionHeld(runtime.Account) || weightManualHolds.Contains(runtime.Account.Id)
                || TemporaryActionRegistered(runtime.ProcessId.Value) || OtherRecoveryOwner(runtime) != null
                || AutobattleVisualBlocksInput(runtime.Visual)
                || runtime.ForegroundGameplayVersion != runtime.MovementWatchdog.ProgressVersion
                || (runtime.StationaryRecoveryVersion == runtime.MovementWatchdog.ProgressVersion
                    && (settings.AutoRecover || (runtime.LastStationaryRecoveryAt.HasValue
                        && (restartEnvironment.MonotonicNow - runtime.LastStationaryRecoveryAt.Value).TotalSeconds
                            < runtime.Account.SmartTeleportIdleSeconds)))) return false;
            double stalled = runtime.MovementWatchdog.StalledSeconds(restartEnvironment.MonotonicNow);
            int due = teleport ? runtime.Account.SmartTeleportIdleSeconds
                : VanillaRecoveryScreenDiagnosis.MinimumStallSeconds;
            if (stalled < due || (settings.AutoRecover && stalled >= settings.MovementRestartSeconds)) return false;
            runtime.StationaryRecoveryVersion = runtime.MovementWatchdog.ProgressVersion;
            runtime.LastStationaryRecoveryAt = restartEnvironment.MonotonicNow;
            RequestVerifiedResume(runtime, "Fresh foreground and memory evidence confirm stationary gameplay.", true);
            return true;
        }

        private bool RunOwnedClientStep(Runtime owner, int pid, Func<bool> cancelled, Func<bool> action)
        {
            lock (gate)
            {
                Runtime current;
                if (cancelled() || disposed || !runtimes.TryGetValue(owner.Account.Id, out current)
                    || !ReferenceEquals(owner, current) || current.ProcessId != pid || !current.Account.Enabled
                    || FarmingEmergencyHeld(current) || CharacterOwnershipChanged(current, pid))
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
            if (FarmingEmergencyHeld(runtime)) return;
            runtime.RecoveryOwned = false;
            runtime.ScriptRunning = false;
            runtime.ResumeSent = false;
            runtime.MovementRecoveryPending = true;
            Runtime owner = OtherRecoveryOwner(runtime);
            if (owner != null)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForClient,
                    "Restart queued behind " + owner.Account.Label + "; stationary recovery attempt is complete");
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
            if (runtime.ScriptRunning || !runtime.ProcessId.HasValue || FarmingEmergencyHeld(runtime)) return;
            if (runtime.ResumeVerificationFailed && !movementRecovery) return;
            Runtime owner = OtherRecoveryOwner(runtime);
            if (owner != null)
            {
                SetStage(runtime, VanillaReconnectStage.WaitingForGameplay,
                    "Queued: waiting for " + owner.Account.Label + " before autobattle verification");
                return;
            }
            int pid = runtime.ProcessId.Value;
            long stationaryVersion = runtime.MovementWatchdog.ProgressVersion;
            VanillaPositionSample stationaryBaseline = movementRecovery ? positionSource?.Invoke(pid) : null;
            if (movementRecovery && stationaryBaseline == null) return;
            bool allowResume = settings.AutoRecover;
            TimeSpan originalDeadline = restartEnvironment.MonotonicNow + TimeSpan.FromMilliseconds(
                movementRecovery && allowResume ? Math.Max(0, (settings.MovementRestartSeconds
                    - runtime.MovementWatchdog.StalledSeconds(restartEnvironment.MonotonicNow)) * 1000)
                    : movementRecovery ? 30000 : VanillaAutobattleResumeVerifier.RecoveryDeadlineMs);
            DateTime created;
            try { created = restartEnvironment.GetStartTimeUtc(pid); }
            catch (Exception ex) { Log(runtime.Account.Label + ": resume identity unavailable: " + ex.Message); return; }
            int generation = Interlocked.Increment(ref resumeVerificationGeneration);
            runtime.ResumeOperationGeneration = generation;
            var account = runtime.Account.Clone();
            runtime.ScriptRunning = true;
            runtime.RecoveryOwned = true;
            runtime.ResumeSent = false;
            string preparation = (string.IsNullOrWhiteSpace(trigger) ? "" : trigger + " ")
                + (movementRecovery && !allowResume ? "Preparing stationary Smart Teleport; automatic relog is disabled"
                    : "Preparing autoattack + teleport recovery cycle 1/3");
            SetStage(runtime, VanillaReconnectStage.VerifyingAutobattle, preparation);
            Log(account.Label + ": " + preparation);
            // The continuation is owned by this Task, never an async-void ThreadPool callback.
            Task.Run(async () =>
            {
                string error = null;
                bool cancelled = false;
                bool serverClosed = false;
                bool touched = false;
                try
                {
                    Func<bool> workerCancelled = () => !IsRunning || ResumeWorkerCancelled(runtime, pid, generation)
                        || restartEnvironment.GetStartTimeUtc(pid) != created;
                    int remaining = movementRecovery ? (int)Math.Min(VanillaAutobattleResumeVerifier.RecoveryDeadlineMs,
                        Math.Max(0, (originalDeadline - restartEnvironment.MonotonicNow).TotalMilliseconds))
                        : VanillaAutobattleResumeVerifier.RecoveryDeadlineMs;
                    if (remaining <= 0) throw new InvalidOperationException("Original no-movement deadline reached.");
                    touched = await VerifyAutobattleResumeCoreAsync(account, pid, workerCancelled,
                        detail => ResumeProgress(runtime, pid, generation, detail), movementRecovery, remaining,
                        movementRecovery ? (Func<bool>)(() => StationaryMovementResumed(runtime, pid, stationaryVersion, stationaryBaseline)) : null,
                        !movementRecovery || allowResume, stationaryBaseline).ConfigureAwait(false);
                    if (!IsRunning || ResumeWorkerCancelled(runtime, pid, generation)) throw new OperationCanceledException();
                    if (touched && !WaitForOwnedClientSafeMinimize(runtime, pid,
                        () => !IsRunning || ResumeWorkerCancelled(runtime, pid, generation), account.Label + ": autobattle recovery", true))
                        throw new InvalidOperationException("Movement verified but client minimization could not be confirmed.");
                }
                catch (OperationCanceledException) { cancelled = true; }
                catch (VanillaServerClosedException ex) { serverClosed = true; error = ex.Message; }
                catch (Exception ex) { error = ex.Message; }
                lock (gate)
                {
                    // An old worker must never publish success or clear a new worker's input lease.
                    Runtime current;
                    if (!runtimes.TryGetValue(account.Id, out current) || !ReferenceEquals(runtime, current)
                        || runtime.ProcessId != pid || runtime.ResumeOperationGeneration != generation || !runtime.ScriptRunning) return;
                    cancelled = cancelled || !running || ResumeWorkerCancelled(runtime, pid, generation);
                    runtime.ScriptRunning = false;
                    if (movementRecovery) runtime.LastStationaryRecoveryAt = restartEnvironment.MonotonicNow;
                    if (cancelled)
                    {
                        runtime.RecoveryOwned = false;
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Autobattle verification cancelled");
                    }
                    else if (serverClosed)
                    {
                        if (!movementRecovery || allowResume)
                        { ConfirmServerOutageLocked(runtime); FinishServerOutageFailureLocked(runtime, error); }
                        else
                        {
                            runtime.RecoveryOwned = false; runtime.ResumeSent = true;
                            SetStage(runtime, VanillaReconnectStage.Error, error + "; automatic relog is disabled");
                        }
                    }
                    else if (error != null)
                    {
                        runtime.ResumeVerificationFailed = true;
                        runtime.ResumeFailureDetail = "Autobattle verification failed: " + error;
                        runtime.MovementRecoveryPending = !movementRecovery;
                        Log(account.Label + ": " + runtime.ResumeFailureDetail);
                        runtime.ScriptRunning = false;
                        runtime.RecoveryOwned = false;
                        if (!movementRecovery)
                            QueueAutobattleClientRestartLocked(runtime, restartEnvironment.UtcNow, runtime.ResumeFailureDetail);
                        else
                        {
                            runtime.ResumeSent = true;
                            runtime.ForegroundGameplayVersion = -1;
                            screenDiagnosisAttempts.Remove(runtime.Account.Id);
                            SetStage(runtime, VanillaReconnectStage.Online, runtime.ResumeFailureDetail
                                + "; retaining original movement deadline for affected-client recovery");
                        }
                    }
                    else
                    {
                        runtime.ResumeSent = true;
                        runtime.ResumeVerificationFailed = false;
                        runtime.ResumeFailureDetail = null;
                        runtime.HasBeenOnline = true;
                        CompleteAutobattleRecoverySuccessLocked(runtime);
                        SetStage(runtime, VanillaReconnectStage.Online, touched
                            ? "Movement verified; client minimized; recovery budget reset"
                            : "Movement resumed before foreground input; recovery input suppressed");
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
