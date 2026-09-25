using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    /// <summary>
    /// Strict cold-start orchestration for START SUPERVISOR.
    /// One client must complete launcher -> proxy -> login -> server -> character -> gameplay
    /// -> verified autobattle movement -> minimize before the next configured client may start.
    /// </summary>
    public sealed partial class VanillaReconnectSupervisor
    {
        private int hardenedStartupGeneration;
        private bool hardenedStartupRunning;



        public bool IsHardenedStartupRunning
        {
            get { lock (gate) return hardenedStartupRunning; }
        }

        internal static bool SequentialStartupMayAdvance(bool gameplayConfirmed, bool movementVerified, bool minimized, bool failed)
        {
            return gameplayConfirmed && movementVerified && minimized && !failed;
        }

        internal static bool ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState visual)
        {
            return visual == VanillaVisualState.LoginShell
                || visual == VanillaVisualState.LoggingOut
                || visual == VanillaVisualState.Disconnected || visual == VanillaVisualState.ServerClosed;
        }

        public void StartHardenedSequentialStartup(System.Action<bool, string> completed)
        {
            VanillaReconnectSettings config;
            VanillaReconnectAccount[] configured;
            int generation;

            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(VanillaReconnectSupervisor));
                if (running) throw new InvalidOperationException("Stop the reconnect supervisor before starting the serialized cold-start sequence.");
                if (hardenedStartupRunning) throw new InvalidOperationException("A serialized startup sequence is already running.");
                settings.Validate();
                RebuildRuntimes();
                AdoptExistingClients(false);
                config = settings.Clone();
                configured = config.Accounts.Where(a => a.Enabled).ToArray();
                if (configured.Length == 0) throw new InvalidOperationException("Enable at least one account first.");
                if (configured.Length > 2) throw new InvalidOperationException("Vanilla supports at most two enabled clients on this PC.");
                hardenedStartupRunning = true;
                generation = Interlocked.Increment(ref hardenedStartupGeneration);
            }

            Log("Sequential startup BEGIN: " + configured.Length + " enabled client(s). Client 2 is hard-blocked until client 1 has gameplay confirmed, autobattle movement verified, and minimize confirmed.");
            VanillaDebugLog.Write("STARTUP", "BEGIN strict sequential startup for " + configured.Length + " client(s).");
            ThreadPool.QueueUserWorkItem(_ => HardenedStartupWorker(generation, configured, config, completed));
        }

        public void CancelHardenedSequentialStartup()
        {
            lock (gate)
            {
                CancelServerOutageLocked();
                Interlocked.Increment(ref hardenedStartupGeneration);
                Interlocked.Increment(ref resumeVerificationGeneration);
                hardenedStartupRunning = false;
                foreach (Runtime runtime in runtimes.Values)
                {
                    if ((runtime.Detail != null && runtime.Detail.StartsWith("Sequential startup", StringComparison.Ordinal))
                        || runtime.Stage == VanillaReconnectStage.WaitingForServer)
                    {
                        runtime.ScriptRunning = false;
                        runtime.RecoveryOwned = false;
                        runtime.ClosingForRecovery = false;
                        ResetTerminalEvidence(runtime);
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Sequential startup stopped by user");
                    }
                }
            }
            Log("Sequential startup STOP requested. No later client will be launched.");
            VanillaDebugLog.Write("STARTUP", "STOP requested.");
            RaiseUpdated();
        }

        private bool StartupCancelled(int generation)
        {
            return disposed || generation != Volatile.Read(ref hardenedStartupGeneration);
        }

        private bool StartupAccountCancelled(int generation, VanillaReconnectAccount account)
        {
            lock (gate)
            {
                Runtime runtime;
                return StartupCancelled(generation) || FarmingEmergencyHeld(account)
                    || (runtimes.TryGetValue(account.Id, out runtime) && FarmingEmergencyHeld(runtime));
            }
        }

        private void HardenedStartupWorker(int generation, VanillaReconnectAccount[] accounts, VanillaReconnectSettings config,
            System.Action<bool, string> completed)
        {
            bool success = false;
            string result = null;
            try
            {
                int updateSerial = launcherUpdateResetSerial;
                for (int index = 0; index < accounts.Length; index++)
                {
                    if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                    VanillaReconnectAccount account = accounts[index];
                    try
                    {
                        Runtime runtime;
                        int? existingPid;
                        lock (gate)
                        {
                            if (!runtimes.TryGetValue(account.Id, out runtime)) throw new InvalidOperationException("Runtime disappeared for " + account.Label + ".");
                            if (FarmingEmergencyHeld(runtime))
                            {
                                Log(account.Label + ": farming emergency hold; sequential startup skipped this character.");
                                continue;
                            }
                            existingPid = runtime.ProcessId.HasValue && IsAlive(runtime.ProcessId.Value) ? runtime.ProcessId : null;

                        }

                        if (existingPid.HasValue)
                        {
                            using (var process = Process.GetProcessById(existingPid.Value))
                            {
                                try
                                {
                                    if (CloseTerminalBeforeStartup(runtime, existingPid.Value, generation, config,
                                        () => VanillaVisualProbe.ObserveProcess(existingPid.Value).State,
                                        () => process.StartTime.ToUniversalTime(), milliseconds => PauseCharacterSelection(
                                            () => StartupAccountCancelled(generation, account), milliseconds)))
                                        existingPid = null;
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception) when (runtime.ServerOutagePending)
                                {
                                    // The verified outage remains a scheduled retry even if its
                                    // owned close failed. The next attempt must close before launch.
                                    existingPid = null;
                                }
                            }
                        }
                        if (existingPid.HasValue)
                        {
                            lock (gate)
                            {
                                if (!CanAdoptExistingGameplayClient(runtime.ResumeSent, runtime.ResumeVerificationFailed, runtime.ScriptRunning))
                                    throw new InvalidOperationException(account.Label + ": existing client has an unfinished or failed startup. Verify it with the Resume hotkey test before starting later clients.");
                            }
                            Log(account.Label + ": existing PID " + existingPid.Value + " found. Verifying gameplay before releasing the sequential gate.");
                            VanillaDebugLog.Write("STARTUP", account.Label + ": existing PID " + existingPid.Value + " verification begin.");
                            try
                            {
                                WaitForExistingClientGameplayReady(account, existingPid.Value,
                                    () => StartupAccountCancelled(generation, account), 15000, account.Label + " existing client");
                            }
                            catch (VanillaServerClosedException)
                            {
                                lock (gate) { ConfirmServerOutageLocked(runtime); ParkForServerOutageLocked(runtime); }
                                RunOneColdStart(generation, account, config, index + 1, accounts.Length);
                                continue;
                            }
                            VanillaVisualState supplementalVisual = VanillaVisualState.Unknown;
                            try
                            {
                                using (var process = Process.GetProcessById(existingPid.Value))
                                {
                                    process.Refresh();
                                    supplementalVisual = VanillaVisualProbe.Classify(process.MainWindowHandle);
                                }
                            }
                            catch (Exception ex)
                            {
                                VanillaDebugLog.Write("STARTUP", account.Label
                                    + ": supplemental visual probe failed after memory verification: " + ex.Message);
                            }
                            VanillaDebugLog.Write("STARTUP", account.Label + ": existing PID " + existingPid.Value
                                + " accepted by fresh verified memory gameplay state; supplemental visual=" + supplementalVisual
                                + (supplementalVisual == VanillaVisualState.Unknown ? " (Unknown is non-blocking)." : "."));
                            if (supplementalVisual == VanillaVisualState.ServerClosed)
                            {
                                try { ThrowIfConfirmedServerClosed(existingPid.Value, () => StartupAccountCancelled(generation, account)); }
                                catch (VanillaServerClosedException)
                                {
                                    lock (gate) { ConfirmServerOutageLocked(runtime); ParkForServerOutageLocked(runtime); }
                                    RunOneColdStart(generation, account, config, index + 1, accounts.Length);
                                    continue;
                                }
                            }
                            if (ExistingClientVisualBlocksMemoryAdoption(supplementalVisual))
                                throw new InvalidOperationException(account.Label
                                    + ": fresh verified memory says gameplay, but supplemental visual is " + supplementalVisual
                                    + "; existing client was not adopted.");

                            if (!WaitForOwnedClientSafeMinimize(runtime, existingPid.Value, () => StartupAccountCancelled(generation, account),
                                account.Label + ": existing client", false))
                                throw new InvalidOperationException(account.Label + ": existing gameplay client could not be confirmed minimized; next client was NOT started.");

                            lock (gate)
                            {
                                Runtime current;
                                if (StartupAccountCancelled(generation, account) || !runtimes.TryGetValue(account.Id, out current)
                                    || !ReferenceEquals(current, runtime) || runtime.ProcessId != existingPid.Value)
                                    throw new OperationCanceledException("Sequential startup client changed.");
                                runtime.ResumeSent = true; // never toggle an adopted already-running client.
                                runtime.HasBeenOnline = true;
                                runtime.ScriptRunning = false;
                                runtime.RecoveryOwned = false;
                                SetStage(runtime, VanillaReconnectStage.Online, "Existing gameplay client verified and minimized");
                            }
                            Log(account.Label + ": existing client verified/minimized. Sequential gate released for next account.");
                            RaiseUpdated();
                            continue;
                        }

                        RunOneColdStart(generation, account, config, index + 1, accounts.Length);
                        if (updateSerial != launcherUpdateResetSerial)
                        {
                            // The update may have closed an earlier completed row: revisit before supervision starts.
                            updateSerial = launcherUpdateResetSerial;
                            index = -1;
                            Log("Launcher update completed; rechecking all enabled characters sequentially.");
                        }
                    }
                    catch (Exception) when (!StartupCancelled(generation) && StartupAccountCancelled(generation, account))
                    {
                        // The synchronous account operation has fully unwound before
                        // the next eligible character may acquire startup ownership.
                        // An emergency close can also make an in-flight process query fail.
                        Log(account.Label + ": farming emergency hold cancelled this startup; continuing with eligible characters.");
                        if (updateSerial != launcherUpdateResetSerial)
                        {
                            updateSerial = launcherUpdateResetSerial; index = -1;
                            Log("Launcher update reset interrupted by an emergency hold; rechecking eligible characters sequentially.");
                        }
                    }
                }

                lock (gate)
                {
                    if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                    hardenedStartupRunning = false;
                    Start();
                }
                success = true;
                result = "Sequential startup complete. Eligible enabled clients reached verified gameplay and minimization; farming emergency holds remain stopped. Continuous supervisor is ON.";
                Log(result);
                VanillaDebugLog.Write("STARTUP", result);
            }
            catch (OperationCanceledException ex)
            {
                result = ex.Message;
                lock (gate) { if (!StartupCancelled(generation)) hardenedStartupRunning = false; }
                Log("Sequential startup stopped: " + result);
                VanillaDebugLog.Write("STARTUP", "STOPPED: " + result);
            }
            catch (Exception ex)
            {
                result = ex.Message;
                lock (gate) { if (!StartupCancelled(generation)) hardenedStartupRunning = false; }
                Log("Sequential startup FAILED. Later queued clients were NOT started: " + result);
                VanillaDebugLog.Write("STARTUP", "FAILED: " + ex);
            }
            finally
            {
                RaiseUpdated();
                if (completed != null && !StartupCancelled(generation))
                {
                    try { completed(success, result ?? (success ? "Sequential startup completed." : "Sequential startup stopped.")); }
                    catch { }
                }
            }
        }

        private void RunOneColdStart(int generation, VanillaReconnectAccount account, VanillaReconnectSettings config, int ordinal, int total)
        {
            if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled for this character.");
            if (string.IsNullOrWhiteSpace(config.LaunchExecutable) || !File.Exists(config.LaunchExecutable))
                throw new InvalidOperationException("Set the Vanilla launch executable before starting the supervisor.");
            string missing = MissingCharacterConfiguration(account);
            if (missing != null) throw new InvalidOperationException(account.Label + ": " + missing + ".");
            int failureCount = 0;
            Exception last = null;
            while (true)
            {
                Runtime runtime;
                lock (gate)
                {
                    if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled.");
                    runtime = runtimes[account.Id];
                }
                WaitForStartupServerAvailability(generation, runtime);
                try
                {
                    if (runtime.ServerOutagePending && runtime.ProcessId.HasValue)
                        CloseColdStartClientForRestart(generation, runtime, failureCount, "Scheduled server availability check");
                    RunOneColdStartAttempt(generation, account, config, ordinal, total);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; failureCount = Math.Min(30, failureCount + 1); }

                lock (gate)
                {
                    if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled.");
                    if (last is VanillaServerClosedException) ConfirmServerOutageLocked(runtime);
                }
                Log(account.Label + ": startup/restart attempt failed: " + last.Message + ". Closing any failed client before retry.");
                VanillaDebugLog.Write("STARTUP", account.Label + ": failure " + failureCount + ": " + last.Message);
                try { CloseColdStartClientForRestart(generation, runtime, failureCount, last.Message); }
                catch (OperationCanceledException) { throw; }
                catch (Exception closeEx)
                {
                    last = closeEx;
                    Log(account.Label + ": failed client could not be closed cleanly: " + closeEx.Message);
                }
                lock (gate)
                {
                    if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled.");
                    if (serverOutage.Active)
                    {
                        FinishServerOutageFailureLocked(runtime, last.Message);
                        continue;
                    }
                }
                int delay = VanillaRecoveryPolicy.RetryDelayMs(failureCount, config.RetryBackoffMs, config.MaxRetryBackoffMs);
                Log(account.Label + ": next sequential restart/login attempt in " + FormatDelay(delay)
                    + "; retry intervals double and cap at 1 hour. Later clients remain blocked behind this recovery lease.");
                PauseStartupRetryCore(() => StartupAccountCancelled(generation, account), delay);
            }
        }

        private void WaitForStartupServerAvailability(int generation, Runtime runtime)
        {
            while (true)
            {
                lock (gate)
                {
                    Runtime current;
                    if (StartupCancelled(generation) || !runtimes.TryGetValue(runtime.Account.Id, out current)
                        || !ReferenceEquals(runtime, current) || !runtime.Account.Enabled || FarmingEmergencyHeld(runtime))
                        throw new OperationCanceledException("Sequential startup cancelled during server wait.");
                    if (MayStartServerOutageProbeLocked(runtime)) return;
                    foreach (Runtime queued in runtimes.Values.Where(r => r.Account.Enabled && !ReferenceEquals(r, runtime)
                        && !r.HasBeenOnline && !r.ScriptRunning && !FarmingEmergencyHeld(r)))
                        SetStage(queued, VanillaReconnectStage.WaitingForServer, "Queued behind server availability check; " + ServerOutageDetail());
                }
                RaiseUpdated();
                PauseStartupRetryCore(() => StartupAccountCancelled(generation, runtime.Account), 500);
            }
        }

        private static void PauseStartupRetryCore(Func<bool> cancelled, int milliseconds)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                if (cancelled()) throw new OperationCanceledException("Sequential startup cancelled during recovery backoff.");
                int slice = Math.Min(500, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        private void CloseColdStartClientForRestart(int generation, Runtime runtime, int failureCount, string reason)
        {
            int? pid;
            int operation = 0;
            lock (gate)
            {
                if (StartupAccountCancelled(generation, runtime.Account)) throw new OperationCanceledException("Sequential startup cancelled.");
                pid = runtime.ProcessId;
                runtime.MovementRecoveryPending = false;
                runtime.ResumeSent = false;
                runtime.ResumeVerificationFailed = true;
                runtime.ResumeFailureDetail = reason;
                if (!pid.HasValue)
                {
                    runtime.ScriptRunning = false;
                    runtime.RecoveryOwned = true;
                    return;
                }
                operation = Interlocked.Increment(ref resumeVerificationGeneration);
                runtime.ResumeOperationGeneration = operation;
                runtime.ScriptRunning = runtime.RecoveryOwned = runtime.ClosingForRecovery = true;
                SetStage(runtime, VanillaReconnectStage.ClosingClient,
                    "Sequential recovery failure " + failureCount + ": closing failed client before backoff");
            }
            try
            {
                DateTime identity = restartEnvironment.GetStartTimeUtc(pid.Value);
                Func<bool> cancelled = () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, operation);
                restartEnvironment.CloseClient(pid.Value, identity, cancelled, action =>
                    RunOwnedClientStep(runtime, pid.Value, cancelled, () => { action(); return true; }));
                lock (gate)
                {
                    if (cancelled()) throw new OperationCanceledException("Sequential startup restart cancelled.");
                    try { positionClientExited?.Invoke(pid.Value); }
                    catch (Exception ex) { Log(runtime.Account.Label + ": exited reader cleanup failed: " + ex.Message); }
                    runtime.ProcessId = null;
                    runtime.CharacterSession = null;
                    runtime.ConfirmedCharacter = null;
                    runtime.ScriptRunning = false;
                    runtime.RecoveryOwned = true;
                    runtime.ClosingForRecovery = false;
                    runtime.ResumeSent = false;
                    runtime.ResumeVerificationFailed = false;
                    runtime.ResumeFailureDetail = null;
                    runtime.HasBeenOnline = false;
                    runtime.GameplaySince = runtime.LoginLikeSince = null;
                    runtime.MovementWatchdog.Reset();
                    ResetTerminalEvidence(runtime);
                    SetStage(runtime, VanillaReconnectStage.Backoff,
                        "Failed client exited; waiting before sequential recovery retry " + (failureCount + 1));
                }
            }
            catch
            {
                lock (gate)
                {
                    runtime.ScriptRunning = false;
                    runtime.RecoveryOwned = false;
                    runtime.ClosingForRecovery = false;
                }
                throw;
            }
        }

        private void RunOneColdStartAttempt(int generation, VanillaReconnectAccount account, VanillaReconnectSettings config, int ordinal, int total)
        {
            if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled for this character.");
            if (string.IsNullOrWhiteSpace(config.LaunchExecutable) || !File.Exists(config.LaunchExecutable))
                throw new InvalidOperationException("Set the Vanilla launch executable before starting the supervisor.");
            string missing = MissingCharacterConfiguration(account);
            if (missing != null) throw new InvalidOperationException(account.Label + ": " + missing + ".");
            var alreadyRunning = GetVanillaProcesses();
            try
            {
                if (alreadyRunning.Count >= 2)
                    throw new InvalidOperationException("Two Vanilla clients are already running; no third client will be started.");
                if (alreadyRunning.Any(p => VanillaCharacterRoster.Key(CurrentCharacter(p.Id)) == null
                    && !IsParkedServerOutageClient(p.Id)))
                    throw new InvalidOperationException("A running client has no verified character identity yet; no duplicate client will be started.");
            }
            finally { foreach (var process in alreadyRunning) process.Dispose(); }

            Runtime runtime;
            int resumeGeneration;
            lock (gate)
            {
                if (StartupAccountCancelled(generation, account)) throw new OperationCanceledException("Sequential startup cancelled.");
                resumeGeneration = Interlocked.Increment(ref resumeVerificationGeneration);
                runtime = runtimes[account.Id];
                runtime.ResumeOperationGeneration = resumeGeneration;
                runtime.ScriptRunning = true;
                runtime.RecoveryOwned = true;
                runtime.ResumeSent = false;
                runtime.ResumeVerificationFailed = false;
                runtime.ResumeFailureDetail = null;
                runtime.HasBeenOnline = false;
                runtime.NextRecoveryAt = null;
                SetStage(runtime, VanillaReconnectStage.Launching,
                    "Sequential startup " + ordinal + "/" + total + ": current client owns startup gate");
            }
            RaiseUpdated();
            Log(account.Label + ": owns sequential startup gate (" + ordinal + "/" + total + "). No later client can launch yet.");
            VanillaDebugLog.Write("STARTUP", account.Label + ": gate acquired; launcher start.");

            int? pid = null;
            try
            {
                pid = VanillaPatcherLauncher.Launch(config.LaunchExecutable, config.LaunchArguments,
                    message =>
                    {
                        Log(account.Label + ": " + message);
                        VanillaDebugLog.Write("LAUNCHER", account.Label + ": " + message);
                    },
                    () => StartupAccountCancelled(generation, account),
                    debugDirectory: Path.Combine(baseDirectory, "Logs"),
                    recoverUpdate: (blocked, stillBlocked) => RecoverLauncherUpdate(runtime, resumeGeneration,
                        () => StartupAccountCancelled(generation, account), blocked, stillBlocked),
                    startOwned: start => RunOwnedLauncherStart(runtime, resumeGeneration,
                        () => StartupAccountCancelled(generation, account), start));

                if (!pid.HasValue) throw new InvalidOperationException(account.Label + ": launcher did not produce a Vanilla MMO PID.");
                lock (gate)
                {
                    Runtime current;
                    if (StartupAccountCancelled(generation, account) || !runtimes.TryGetValue(account.Id, out current)
                        || !ReferenceEquals(runtime, current) || runtime.ResumeOperationGeneration != resumeGeneration)
                        throw new OperationCanceledException("Sequential startup cancelled before client binding.");
                    Bind(runtime, pid.Value, true, "Sequential startup: launcher produced Vanilla client");
                    runtime.ScriptRunning = true;
                    runtime.RecoveryOwned = true;
                }
                RaiseUpdated();
                VanillaDebugLog.Write("STARTUP", account.Label + ": bound PID " + pid.Value + ". Waiting for the interactive window and configured service settle before Enter.");

                WaitForWindow(pid.Value, 60000, () => StartupAccountCancelled(generation, account)
                    || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration));
                using (var input = new VanillaForegroundInput(pid.Value))
                {
                    input.CancellationRequested = () => StartupCancelled(generation)
                        || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration);
                    ConfirmDefaultService(input, VanillaServiceStep.Proxy, account.Label + ": sequential: ");

                    string password = store.UnprotectPassword(account.ProtectedPassword);
                    if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(account.Label + ": decrypted password is empty.");
                    PauseCharacterSelection(() => StartupAccountCancelled(generation, account), 160);
                    input.Activate();
                    FillDetectedCredentials(input, account, password, pid.Value, true, account.Label + ": sequential: ");
                    VanillaDebugLog.Write("STARTUP", account.Label + ": credentials submitted after login controls were detected and focus verified.");

                    if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                    input.Activate();
                    ConfirmDefaultService(input, VanillaServiceStep.GameServer, account.Label + ": sequential: ");

                    WaitForCharacterSurface(input, pid.Value, generation);
                    SelectConfiguredCharacterWithoutCoordinates(input, pid.Value, account,
                        () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                        account.Label + ": sequential: ");

                    WaitForAutobattleReady(account, pid.Value,
                        () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                        60000, "Sequential startup post-character");
                    ResumeProgress(runtime, pid.Value, resumeGeneration, "Sequential startup: verified character online; settling 10s before recovery cycle 1/3 (" + account.HotkeyText + " -> 10s -> teleport -> 10s)");
                    PauseCharacterSelection(() => StartupAccountCancelled(generation, account), VanillaAutobattleResumeVerifier.PostLoginSettleMs);
                    ResumeProgress(runtime, pid.Value, resumeGeneration, "Sequential startup: 10s settle complete; starting shared 3-cycle autoattack + verified teleport recovery with 180s restart deadline");
                    VerifyAutobattleResumeAsync(account, pid.Value,
                        () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                        detail => ResumeProgress(runtime, pid.Value, resumeGeneration, "Sequential startup: " + detail))
                        .GetAwaiter().GetResult();
                }

                if (StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration))
                    throw new OperationCanceledException("Sequential startup cancelled.");
                bool minimized = WaitForOwnedClientSafeMinimize(runtime, pid.Value,
                    () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                    account.Label + ": sequential startup", true);
                if (!SequentialStartupMayAdvance(true, true, minimized, false))
                    throw new InvalidOperationException(account.Label + ": could not confirm minimization; next client was NOT started.");

                lock (gate)
                {
                    if (StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration))
                        throw new OperationCanceledException("Sequential startup cancelled.");
                    runtime.ResumeSent = true;
                    runtime.HasBeenOnline = true;
                    runtime.ScriptRunning = false;
                    runtime.RecoveryOwned = false;
                    CompleteAutobattleRecoverySuccessLocked(runtime);
                    SetStage(runtime, VanillaReconnectStage.Online, "Sequential startup complete: gameplay + movement verified + minimized");
                }
                RaiseUpdated();
                Log(account.Label + ": COMPLETE + MINIMIZED. Only now may the next account start.");
                VanillaDebugLog.Write("STARTUP", account.Label + ": gate released after gameplay + verified movement + minimize.");
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    Runtime current;
                    if (runtimes.TryGetValue(account.Id, out current) && ReferenceEquals(runtime, current)
                        && runtime.ResumeOperationGeneration == resumeGeneration && runtime.ScriptRunning)
                    {
                        runtime.ScriptRunning = false;
                        runtime.RecoveryOwned = false;
                        runtime.ResumeVerificationFailed = true;
                        runtime.ResumeFailureDetail = "Sequential startup attempt failed: " + ex.Message;
                        SetStage(runtime, ex is OperationCanceledException ? VanillaReconnectStage.Stopped : VanillaReconnectStage.Error,
                            runtime.ResumeFailureDetail);
                    }
                }
                RaiseUpdated();
                // The outer sequential owner closes the failed client and retries with capped exponential backoff.
                throw;
            }
        }

        private void SelectConfiguredCharacterWithoutCoordinates(VanillaForegroundInput input, int pid,
            VanillaReconnectAccount account, Func<bool> cancelled, string logPrefix)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            int slot = account.RequiredCharacterSlot();
            long frame = 0;
            VanillaVisualInputProof proof = null;
            VanillaCharacterSelector.Select(slot, () =>
            {
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    VanillaCharacterSelectionObservation observation;
                    string evidence;
                    if (!VanillaCharacterPattern.TryDetect(image, out observation, out evidence))
                        throw new InvalidOperationException("Character selection is unverified; no further input was sent. " + evidence);
                    observation.FrameId = ++frame;
                    proof = input.LastCaptureProof;
                    observation.InputProof = proof;
                    return observation;
                }
            }, key => input.PressFromProof(key, proof), milliseconds => PauseCharacterSelection(cancelled, milliseconds), cancelled);
            string expected = string.IsNullOrWhiteSpace(account.CharacterName) ? "<learn after gameplay>" : account.CharacterName;
            string detail = logPrefix + "character selection verified its detected grid and configured occupied slot " + slot
                + " for '" + expected + "'; awaiting independent gameplay identity.";
            Log(detail);
            VanillaDebugLog.Write("STARTUP", "PID=" + pid + "; " + detail);
        }

        private void WaitForCharacterSurfaceCancellable(VanillaForegroundInput input, int pid, Func<bool> cancelled,
            int timeoutMs, string context)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int consecutive = 0;
            string last = "not sampled";
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) throw new OperationCanceledException(context + ": character selection cancelled.");
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    VanillaCharacterSelectionObservation observation;
                    string evidence;
                    bool recognized = VanillaCharacterPattern.TryDetect(image, out observation, out evidence);
                    last = evidence;
                    if (recognized && watch.ElapsedMilliseconds >= 450)
                    {
                        consecutive++;
                        if (consecutive >= 3)
                        {
                            SaveUiCapture(image, "character-screen-ready.png");
                            VanillaDebugLog.Write("STARTUP", context + ": character surface PID=" + pid
                                + " stable after " + watch.ElapsedMilliseconds + " ms; " + last + ".");
                            return;
                        }
                    }
                    else consecutive = 0;
                }
                PauseCharacterSelection(cancelled, 160);
            }
            throw new InvalidOperationException(context + ": character surface was not safely detected within "
                + (timeoutMs / 1000) + "s. No character-selection input was sent. Last state: " + last);
        }

        private static void PauseCharacterSelection(Func<bool> cancelled, int milliseconds)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                if (cancelled()) throw new OperationCanceledException("Character selection cancelled.");
                int slice = Math.Min(50, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
        }

        private void WaitForCharacterSurface(VanillaForegroundInput input, int pid, int generation)
        {
            WaitForCharacterSurfaceCancellable(input, pid, () => StartupCancelled(generation), 30000, "Sequential startup");
        }

        private void WaitForGameplayStable(VanillaForegroundInput input, int pid, int generation, int timeoutMs, string context)
        {
            WaitForGameplayStableCancellable(input, pid, () => StartupCancelled(generation), timeoutMs, context);
        }

        private void WaitForGameplayStableCancellable(VanillaForegroundInput input, int pid, Func<bool> cancelled, int timeoutMs, string context)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int consecutive = 0;
            VanillaVisualState last = VanillaVisualState.Unknown;
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (cancelled()) throw new OperationCanceledException(context + ": gameplay wait cancelled.");
                input.Activate();
                using (var process = Process.GetProcessById(pid))
                {
                    process.Refresh();
                    if (process.HasExited) throw new InvalidOperationException(context + ": Vanilla client exited while waiting for gameplay.");
                    if (process.MainWindowHandle == IntPtr.Zero)
                    {
                        consecutive = 0;
                        Thread.Sleep(150);
                        continue;
                    }
                    last = VanillaVisualProbe.Classify(process.MainWindowHandle);
                }

                VanillaDebugLog.Write("VISUAL", context + ": PID=" + pid + ", state=" + last + ", elapsedMs=" + watch.ElapsedMilliseconds + ".");
                if (last == VanillaVisualState.Gameplay && watch.ElapsedMilliseconds >= 600)
                {
                    consecutive++;
                    if (consecutive >= 3)
                    {
                        Log(context + ": gameplay confirmed in three consecutive focused samples after " + watch.ElapsedMilliseconds + " ms.");
                        return;
                    }
                }
                else consecutive = 0;
                Thread.Sleep(180);
            }
            throw new InvalidOperationException(context + ": gameplay not stably confirmed within " + (timeoutMs / 1000)
                + "s (last=" + last + "). No autobattle hotkey was sent.");
        }

        private static bool IsAlive(int pid)
        {
            var processes = Process.GetProcessesByName("Vanilla MMO");
            try { return processes.Any(process => process.Id == pid); }
            finally { foreach (var process in processes) process.Dispose(); }
            // Enumeration failure propagates; it is not a missing client.
        }
    }

    internal sealed partial class VanillaReconnectForm
    {
        private bool hardenedSupervisorButtonsInstalled;

        internal void InstallHardenedSupervisorButtons()
        {
            if (hardenedSupervisorButtonsInstalled) return;
            hardenedSupervisorButtonsInstalled = true;
            ReplaceSupervisorButton(this, "START SUPERVISOR", "START SUPERVISOR", StartSupervisorHardened,
                "Cold-start enabled clients strictly one at a time. A stationary client gets up to three autoattack/10s/teleport/10s recovery cycles; movement stops input immediately, and restart escalation waits for the 180-second deadline.");
            ReplaceSupervisorButton(this, "STOP", "STOP", StopSupervisorHardened,
                "Stop continuous supervision and cancel any in-progress serialized startup. Running Vanilla clients are left open.");
        }

        private void ReplaceSupervisorButton(Control root, string oldText, string newText, System.Action action, string tooltip)
        {
            foreach (Control child in root.Controls.Cast<Control>().ToArray())
            {
                var button = child as Button;
                if (button != null && string.Equals(button.Text, oldText, StringComparison.Ordinal))
                {
                    Control parent = button.Parent;
                    int index = parent.Controls.GetChildIndex(button);
                    parent.Controls.Remove(button);
                    button.Dispose();
                    var replacement = new Button { Text = newText, AutoSize = true, Margin = new Padding(4) };
                    replacement.Click += (s, e) => action();
                    parent.Controls.Add(replacement);
                    parent.Controls.SetChildIndex(replacement, index);
                    help.SetToolTip(replacement, tooltip);
                    return;
                }
                if (child.HasChildren) ReplaceSupervisorButton(child, oldText, newText, action, tooltip);
            }
        }

        private void StartSupervisorHardened()
        {
            try
            {
                if (testRunning) throw new InvalidOperationException("Stop the current diagnostic test before starting the supervisor.");
                if (supervisor.IsHardenedStartupRunning) throw new InvalidOperationException("Sequential startup is already running.");
                if (accountCatalogStore == null) throw new InvalidOperationException("The saved character catalog could not be loaded; no clients will be started.");
                autosaveTimer?.Stop();
                DiscoverCharacters(false);
                SynchronizeSupervisorAccountsFromCatalog();
                SynchronizeDerivedUiValues();
                ReadTop();
                settings.LaunchArguments = string.Empty;
                settings.MaxClients = Math.Max(1, settings.Accounts.Count(a => a.Enabled));
                supervisor.Apply(settings, true);
                if (supervisor.IsRunning) supervisor.Stop();
                supervisor.DetectRunningClients();
                runState.Text = "STARTING";
                runState.ForeColor = Color.DarkOrange;
                testState.Text = "Sequential startup: one client at a time; acting on observed UI states.";
                testState.ForeColor = Color.DarkSlateBlue;
                VanillaDebugLog.Write("UI", "START SUPERVISOR clicked. enabledAccounts=" + settings.Accounts.Count(a => a.Enabled) + ".");
                supervisor.StartHardenedSequentialStartup(HardenedSupervisorStartupCompleted);
            }
            catch (Exception ex)
            {
                VanillaDebugLog.Write("UI", "START SUPERVISOR failed: " + ex);
                MessageBox.Show(this, ex.Message, "Cannot start reconnect supervisor", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void StopSupervisorHardened()
        {
            try { supervisor.CancelHardenedSequentialStartup(); } catch { }
            try { supervisor.Stop(); } catch { }
            VanillaDebugLog.Write("UI", "STOP supervisor clicked.");
            RefreshStatus();
        }

        private void HardenedSupervisorStartupCompleted(bool success, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                BeginInvoke((MethodInvoker)(() => HardenedSupervisorStartupCompleted(success, message)));
                return;
            }
            RefreshStatus();
            testState.Text = success ? "SEQUENTIAL STARTUP COMPLETE" : "SEQUENTIAL STARTUP STOPPED";
            testState.ForeColor = success ? Color.DarkGreen : Color.DarkRed;
            if (!success) MessageBox.Show(this, message, "Sequential startup stopped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
