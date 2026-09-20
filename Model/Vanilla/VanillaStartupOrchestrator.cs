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

        private sealed class ProxyReady
        {
            public Bitmap Image;
            public VanillaProxyLayout Layout;
            public string Evidence;
        }

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
                || visual == VanillaVisualState.Disconnected;
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
                Interlocked.Increment(ref hardenedStartupGeneration);
                Interlocked.Increment(ref resumeVerificationGeneration);
                hardenedStartupRunning = false;
                foreach (Runtime runtime in runtimes.Values)
                {
                    if (runtime.Detail != null && runtime.Detail.StartsWith("Sequential startup", StringComparison.Ordinal))
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
                    Runtime runtime;
                    int? existingPid;
                    lock (gate)
                    {
                        if (!runtimes.TryGetValue(account.Id, out runtime)) throw new InvalidOperationException("Runtime disappeared for " + account.Label + ".");
                        existingPid = runtime.ProcessId.HasValue && IsAlive(runtime.ProcessId.Value) ? runtime.ProcessId : null;

                    }

                    if (existingPid.HasValue)
                    {
                        using (var process = Process.GetProcessById(existingPid.Value))
                        {
                            if (CloseTerminalBeforeStartup(runtime, existingPid.Value, generation, config,
                                () => { process.Refresh(); return VanillaVisualProbe.Classify(process.MainWindowHandle); },
                                () => process.StartTime.ToUniversalTime(), milliseconds => BriefPause(generation, milliseconds)))
                                existingPid = null;
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
                        WaitForExistingClientGameplayReady(account, existingPid.Value,
                            () => StartupCancelled(generation), 15000, account.Label + " existing client");
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
                        if (ExistingClientVisualBlocksMemoryAdoption(supplementalVisual))
                            throw new InvalidOperationException(account.Label
                                + ": fresh verified memory says gameplay, but supplemental visual is " + supplementalVisual
                                + "; existing client was not adopted.");

                        if (!WaitForOwnedClientSafeMinimize(runtime, existingPid.Value, () => StartupCancelled(generation),
                            account.Label + ": existing client", false))
                            throw new InvalidOperationException(account.Label + ": existing gameplay client could not be confirmed minimized; next client was NOT started.");

                        lock (gate)
                        {
                            Runtime current;
                            if (StartupCancelled(generation) || !runtimes.TryGetValue(account.Id, out current)
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

                lock (gate)
                {
                    if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                    hardenedStartupRunning = false;
                    Start();
                }
                success = true;
                result = "Sequential startup complete. Every enabled client reached gameplay, completed autobattle movement verification (or was adopted without toggling), and was minimized before the next client started. Continuous supervisor is ON.";
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
            if (string.IsNullOrWhiteSpace(config.LaunchExecutable) || !File.Exists(config.LaunchExecutable))
                throw new InvalidOperationException("Set the Vanilla launch executable before starting the supervisor.");
            string missing = MissingCharacterConfiguration(account);
            if (missing != null) throw new InvalidOperationException(account.Label + ": " + missing + ".");
            int failureCount = 0;
            Exception last = null;
            while (true)
            {
                try
                {
                    RunOneColdStartAttempt(generation, account, config, ordinal, total);
                    return;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { last = ex; failureCount = Math.Min(30, failureCount + 1); }

                Runtime runtime;
                lock (gate) runtime = runtimes[account.Id];
                Log(account.Label + ": startup/restart attempt failed: " + last.Message + ". Closing any failed client before retry.");
                VanillaDebugLog.Write("STARTUP", account.Label + ": failure " + failureCount + ": " + last.Message);
                try { CloseColdStartClientForRestart(generation, runtime, failureCount, last.Message); }
                catch (OperationCanceledException) { throw; }
                catch (Exception closeEx)
                {
                    last = closeEx;
                    Log(account.Label + ": failed client could not be closed cleanly: " + closeEx.Message);
                }
                int delay = VanillaRecoveryPolicy.RetryDelayMs(failureCount, config.RetryBackoffMs, config.MaxRetryBackoffMs);
                Log(account.Label + ": next sequential restart/login attempt in " + FormatDelay(delay)
                    + "; retry intervals double and cap at 1 hour. Later clients remain blocked behind this recovery lease.");
                PauseStartupRetry(generation, delay);
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

        private void PauseStartupRetry(int generation, int milliseconds)
        { PauseStartupRetryCore(() => StartupCancelled(generation), milliseconds); }

        private void CloseColdStartClientForRestart(int generation, Runtime runtime, int failureCount, string reason)
        {
            int? pid;
            int operation = 0;
            lock (gate)
            {
                if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
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
            if (string.IsNullOrWhiteSpace(config.LaunchExecutable) || !File.Exists(config.LaunchExecutable))
                throw new InvalidOperationException("Set the Vanilla launch executable before starting the supervisor.");
            string missing = MissingCharacterConfiguration(account);
            if (missing != null) throw new InvalidOperationException(account.Label + ": " + missing + ".");
            var alreadyRunning = GetVanillaProcesses();
            try
            {
                if (alreadyRunning.Count >= 2)
                    throw new InvalidOperationException("Two Vanilla clients are already running; no third client will be started.");
                if (alreadyRunning.Any(p => VanillaCharacterRoster.Key(CurrentCharacter(p.Id)) == null))
                    throw new InvalidOperationException("A running client has no verified character identity yet; no duplicate client will be started.");
            }
            finally { foreach (var process in alreadyRunning) process.Dispose(); }

            Runtime runtime;
            int resumeGeneration;
            lock (gate)
            {
                if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
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
                    () => StartupCancelled(generation),
                    debugDirectory: Path.Combine(baseDirectory, "Logs"),
                    recoverUpdate: (blocked, stillBlocked) => RecoverLauncherUpdate(runtime, resumeGeneration,
                        () => StartupCancelled(generation), blocked, stillBlocked));

                if (!pid.HasValue) throw new InvalidOperationException(account.Label + ": launcher did not produce a Vanilla MMO PID.");
                lock (gate)
                {
                    Runtime current;
                    if (StartupCancelled(generation) || !runtimes.TryGetValue(account.Id, out current)
                        || !ReferenceEquals(runtime, current) || runtime.ResumeOperationGeneration != resumeGeneration)
                        throw new OperationCanceledException("Sequential startup cancelled before client binding.");
                    Bind(runtime, pid.Value, true, "Sequential startup: launcher produced Vanilla client");
                    runtime.ScriptRunning = true;
                    runtime.RecoveryOwned = true;
                }
                RaiseUpdated();
                VanillaDebugLog.Write("STARTUP", account.Label + ": bound PID " + pid.Value + ". Waiting for expected UI states, not fixed loading delays.");

                WaitForWindow(pid.Value, 60000);
                using (var input = new VanillaForegroundInput(pid.Value))
                {
                    input.CancellationRequested = () => StartupCancelled(generation)
                        || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration);
                    SelectProxyWhenVisible(input, pid.Value, account, config, generation);

                    string password = store.UnprotectPassword(account.ProtectedPassword);
                    if (string.IsNullOrEmpty(password)) throw new InvalidOperationException(account.Label + ": decrypted password is empty.");
                    BriefPause(generation, 160);
                    input.Activate();
                    FillDetectedCredentials(input, account, password, pid.Value, true, account.Label + ": sequential: ");
                    VanillaDebugLog.Write("STARTUP", account.Label + ": credentials submitted after login controls were detected and focus verified.");

                    if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                    input.Activate();
                    SelectDetectedGameServer(input, pid.Value, 700, account.Label + ": sequential: ");
                    VanillaDebugLog.Write("STARTUP", account.Label + ": server dialog handled after visual detection.");

                    WaitForCharacterSurface(input, pid.Value, generation);
                    SelectConfiguredCharacterWithoutCoordinates(input, pid.Value, account,
                        () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                        account.Label + ": sequential: ");

                    WaitForAutobattleReady(account, pid.Value,
                        () => StartupCancelled(generation) || ResumeWorkerCancelled(runtime, pid.Value, resumeGeneration),
                        60000, "Sequential startup post-character");
                    ResumeProgress(runtime, pid.Value, resumeGeneration, "Sequential startup: verified character online; settling 10s before recovery cycle 1/3 (" + account.HotkeyText + " -> 10s -> teleport -> 10s)");
                    BriefPause(generation, VanillaAutobattleResumeVerifier.PostLoginSettleMs);
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

        internal static Keys[] CharacterSelectionKeyPlan(int oneBasedSlot)
        {
            if (oneBasedSlot < 1 || oneBasedSlot > 15)
                throw new ArgumentOutOfRangeException(nameof(oneBasedSlot));
            int zero = oneBasedSlot - 1;
            int row = zero / 5, column = zero % 5;
            var keys = new System.Collections.Generic.List<Keys>();
            // Character selection owns keyboard focus. Clamp to the top-left card first,
            // then navigate from a known origin. This is independent of resolution/DPI.
            keys.Add(Keys.Up); keys.Add(Keys.Up);
            for (int i = 0; i < 4; i++) keys.Add(Keys.Left);
            for (int i = 0; i < column; i++) keys.Add(Keys.Right);
            for (int i = 0; i < row; i++) keys.Add(Keys.Down);
            keys.Add(Keys.Enter);
            return keys.ToArray();
        }

        private void SelectConfiguredCharacterWithoutCoordinates(VanillaForegroundInput input, int pid,
            VanillaReconnectAccount account, Func<bool> cancelled, string logPrefix)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (cancelled == null) throw new ArgumentNullException(nameof(cancelled));
            int slot = account.RequiredCharacterSlot();
            Keys[] plan = CharacterSelectionKeyPlan(slot);
            input.Activate();
            for (int i = 0; i < plan.Length; i++)
            {
                if (cancelled()) throw new OperationCanceledException("Character selection cancelled before input.");
                input.Press(plan[i]);
                if (i + 1 < plan.Length) PauseCharacterSelection(cancelled, 70);
            }
            string expected = string.IsNullOrWhiteSpace(account.CharacterName) ? "<learn after gameplay>" : account.CharacterName;
            string detail = logPrefix + "character selection used keyboard-only navigation to configured slot " + slot
                + " for '" + expected + "'; no character-grid or GAME START coordinates were clicked.";
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
                    VanillaLoginLayout login;
                    VanillaServerLayout server;
                    VanillaProxyLayout proxyLayout;
                    string evidence;
                    bool loginVisible = VanillaAuthPattern.TryDetectLogin(image, out login, out evidence);
                    bool serverVisible = VanillaAuthPattern.TryDetectServerDialog(image, out server, out evidence);
                    bool proxyVisible = VanillaProxyPattern.TryDetect(image, out proxyLayout, out evidence);
                    bool interactive = IsInteractiveFrame(image);
                    last = "login=" + loginVisible + ", server=" + serverVisible + ", proxy=" + proxyVisible + ", interactive=" + interactive;
                    if (!loginVisible && !serverVisible && !proxyVisible && interactive && watch.ElapsedMilliseconds >= 450)
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

        private void SelectProxyWhenVisible(VanillaForegroundInput input, int pid, VanillaReconnectAccount account,
            VanillaReconnectSettings config, int generation)
        {
            ProxyReady ready = WaitForProxyReady(input, generation, 45000);
            using (ready.Image)
            {
                VanillaProxyRoute route = VanillaAccountProxyPreferences.Get(account.Id, config.Proxy);
                int routeIndex = (int)route;
                if (routeIndex < 0 || routeIndex >= ready.Layout.Rows.Length)
                    throw new InvalidOperationException(account.Label + ": configured proxy route is outside detected list.");

                Rectangle safe = ready.Layout.Rows[routeIndex];
                var random = new Random(unchecked(Environment.TickCount ^ pid ^ (routeIndex * 7919)));
                int marginX = Math.Max(1, safe.Width / 4), marginY = Math.Max(1, safe.Height / 4);
                int px = random.Next(safe.Left + marginX, Math.Max(safe.Left + marginX + 1, safe.Right - marginX));
                int py = random.Next(safe.Top + marginY, Math.Max(safe.Top + marginY + 1, safe.Bottom - marginY));

                input.Activate();
                BriefPause(generation, 150);
                input.ClickNormalized((px + 0.5) / ready.Image.Width, (py + 0.5) / ready.Image.Height);
                for (int i = 0; i < 8; i++) { input.Press(Keys.Up); BriefPause(generation, 25); }
                for (int i = 0; i < routeIndex; i++) { input.Press(Keys.Down); BriefPause(generation, 35); }
                input.Activate();
                input.Press(Keys.Enter);
                Log(account.Label + ": account proxy " + route + " selected after two stable visual detections.");
                VanillaDebugLog.Write("STARTUP", account.Label + ": proxy=" + route + "; " + ready.Evidence);
            }
        }

        private ProxyReady WaitForProxyReady(VanillaForegroundInput input, int generation, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int consecutive = 0;
            string last = "not sampled";
            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                Bitmap image = null;
                try
                {
                    image = input.CaptureClientBitmap();
                    VanillaProxyLayout layout;
                    string evidence;
                    if (VanillaProxyPattern.TryDetect(image, out layout, out evidence))
                    {
                        consecutive++;
                        last = evidence;
                        if (consecutive >= 2)
                        {
                            VanillaDebugLog.Write("STARTUP", "Proxy screen stable after " + watch.ElapsedMilliseconds + " ms.");
                            return new ProxyReady { Image = image, Layout = layout, Evidence = evidence };
                        }
                    }
                    else
                    {
                        consecutive = 0;
                        last = evidence;
                    }
                }
                finally { if (consecutive < 2 && image != null) image.Dispose(); }
                Thread.Sleep(150);
            }
            throw new InvalidOperationException("Proxy screen was not stably detected. Client was left running; later clients were NOT started. Last detector: " + last);
        }

        private void WaitForCharacterSurface(VanillaForegroundInput input, int pid, int generation)
        {
            Stopwatch watch = Stopwatch.StartNew();
            int consecutive = 0;
            string last = "not sampled";
            while (watch.ElapsedMilliseconds < 30000)
            {
                if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                using (Bitmap image = input.CaptureClientBitmap())
                {
                    VanillaLoginLayout login;
                    VanillaServerLayout server;
                    VanillaProxyLayout proxyLayout;
                    string evidence;
                    bool loginVisible = VanillaAuthPattern.TryDetectLogin(image, out login, out evidence);
                    bool serverVisible = VanillaAuthPattern.TryDetectServerDialog(image, out server, out evidence);
                    bool proxyVisible = VanillaProxyPattern.TryDetect(image, out proxyLayout, out evidence);
                    bool interactive = IsInteractiveFrame(image);
                    last = "login=" + loginVisible + ", server=" + serverVisible + ", proxy=" + proxyVisible + ", interactive=" + interactive;
                    if (!loginVisible && !serverVisible && !proxyVisible && interactive && watch.ElapsedMilliseconds >= 450)
                    {
                        consecutive++;
                        if (consecutive >= 3)
                        {
                            try
                            {
                                string path = Path.Combine(baseDirectory, "Logs", "character-screen-ready.png");
                                Directory.CreateDirectory(Path.GetDirectoryName(path));
                                image.Save(path);
                            }
                            catch { }
                            VanillaDebugLog.Write("STARTUP", "Character surface PID=" + pid + " stable after " + watch.ElapsedMilliseconds + " ms; " + last + ".");
                            return;
                        }
                    }
                    else consecutive = 0;
                }
                Thread.Sleep(160);
            }
            throw new InvalidOperationException("Character surface was not safely detected. Client was left running; later clients were NOT started. Last state: " + last);
        }

        private static bool IsInteractiveFrame(Bitmap image)
        {
            if (image == null || image.Width < 320 || image.Height < 240) return false;
            long sum = 0, sumSquares = 0;
            int count = 0;
            int stepX = Math.Max(8, image.Width / 48), stepY = Math.Max(8, image.Height / 32);
            for (int y = stepY / 2; y < image.Height; y += stepY)
            for (int x = stepX / 2; x < image.Width; x += stepX)
            {
                Color c = image.GetPixel(x, y);
                int lum = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                sum += lum;
                sumSquares += lum * lum;
                count++;
            }
            if (count == 0) return false;
            double mean = sum / (double)count;
            double variance = sumSquares / (double)count - mean * mean;
            return mean >= 18 && variance >= 180;
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

        private void BriefPause(int generation, int milliseconds)
        {
            int remaining = Math.Max(0, milliseconds);
            while (remaining > 0)
            {
                if (StartupCancelled(generation)) throw new OperationCanceledException("Sequential startup cancelled.");
                int slice = Math.Min(60, remaining);
                Thread.Sleep(slice);
                remaining -= slice;
            }
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
