using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    public enum VanillaReconnectTestStep
    {
        LauncherGameStart,
        ProxySelection,
        FillCredentials,
        SubmitCredentials,
        SelectGameServer,
        SelectCharacter,
        ResumeHotkey
    }

    public sealed partial class VanillaReconnectSupervisor
    {
        private int diagnosticGeneration;

        public void CancelDiagnosticTest()
        {
            Interlocked.Increment(ref diagnosticGeneration);
            lock (gate)
            {
                foreach (var runtime in runtimes.Values)
                {
                    if (runtime.Detail != null && runtime.Detail.StartsWith("Diagnostic", StringComparison.Ordinal))
                    {
                        runtime.ScriptRunning = false;
                        SetStage(runtime, VanillaReconnectStage.Stopped, "Diagnostic test stopped by user");
                    }
                }
            }
            RaiseUpdated();
        }

        private bool DiagnosticCancelled(int generation)
        {
            return disposed || generation != Volatile.Read(ref diagnosticGeneration);
        }

        private bool DiagnosticWorkerCancelled(Runtime owner, VanillaReconnectAccount account, int? pid, int generation)
        {
            lock (gate)
            {
                Runtime current;
                return DiagnosticCancelled(generation) || !runtimes.TryGetValue(account.Id, out current)
                    || !ReferenceEquals(current, owner) || !current.ScriptRunning
                    || (pid.HasValue && current.ProcessId != pid)
                    || FarmingEmergencyHeld(current) || FarmingEmergencyHeld(account);
            }
        }

        private Process RunOwnedDiagnosticStart(Runtime owner, VanillaReconnectAccount account, int generation, Func<Process> start)
        {
            lock (gate)
            {
                if (DiagnosticWorkerCancelled(owner, account, null, generation))
                    throw new OperationCanceledException("Diagnostic launcher ownership changed.");
                return start();
            }
        }

        public void AssignSingleDiagnosticClient(string accountId)
        {
            lock (gate)
            {
                Runtime runtime;
                if (!runtimes.TryGetValue(accountId, out runtime)) throw new ArgumentException("Unknown account.");
                if (FarmingEmergencyHeld(runtime)) throw new InvalidOperationException(FarmingEmergencyDetail(runtime));

                if (runtime.ProcessId.HasValue && IsProcessAlive(runtime.ProcessId.Value))
                {
                    int pid = runtime.ProcessId.Value;
                    string firstOwner = settings.Accounts
                        .Where(a => runtimes.ContainsKey(a.Id) && runtimes[a.Id].ProcessId == pid)
                        .Select(a => a.Id)
                        .FirstOrDefault();
                    if (string.Equals(firstOwner, accountId, StringComparison.OrdinalIgnoreCase)) return;
                    runtime.ProcessId = null;
                }

                var claimedByOthers = new System.Collections.Generic.HashSet<int>(
                    runtimes.Values
                        .Where(r => !string.Equals(r.Account.Id, accountId, StringComparison.OrdinalIgnoreCase)
                            && r.ProcessId.HasValue && IsProcessAlive(r.ProcessId.Value))
                        .Select(r => r.ProcessId.Value));
                var processes = GetVanillaProcesses()
                    .Where(p => !claimedByOthers.Contains(p.Id))
                    .OrderBy(p => SafeStart(p))
                    .ToList();
                try
                {
                    if (processes.Count == 0)
                        throw new InvalidOperationException("No unassigned running Vanilla client was found for the selected account.");
                    if (processes.Count > 1)
                        throw new InvalidOperationException("Multiple unassigned Vanilla clients are running, so the selected account cannot be identified safely. Use DETECT RUNNING CLIENTS first or leave only the intended unassigned client.");
                    runtime.ProcessId = processes[0].Id;
                    runtime.ResumeSent = true;
                    runtime.ScriptRunning = false;
                    SetStage(runtime, VanillaReconnectStage.Stopped, "Selected for one-client diagnostic (PID " + runtime.ProcessId.Value + ")");
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            RaiseUpdated();
        }

        private static bool IsProcessAlive(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch { return false; }
        }

        public void RunDiagnosticStep(string accountId, VanillaReconnectTestStep step)
        {
            VanillaReconnectAccount account;
            VanillaReconnectSettings config;
            int? pid = null;
            int generation;
            Runtime owner;
            lock (gate)
            {
                Runtime runtime;
                if (!runtimes.TryGetValue(accountId, out runtime)) throw new ArgumentException("Unknown account.");
                if (FarmingEmergencyHeld(runtime)) throw new InvalidOperationException(FarmingEmergencyDetail(runtime));
                if (running || hardenedStartupRunning || OtherRecoveryOwner(runtime) != null || runtime.ScriptRunning)
                    throw new InvalidOperationException("Stop the active startup/recovery action before running a diagnostic.");
                if (step != VanillaReconnectTestStep.LauncherGameStart)
                {
                    if (!runtime.ProcessId.HasValue) throw new InvalidOperationException("No Vanilla client is assigned to this selected account for the step test.");
                    pid = runtime.ProcessId.Value;
                }
                generation = Interlocked.Increment(ref diagnosticGeneration);
                owner = runtime;
                runtime.ScriptRunning = true;
                account = runtime.Account.Clone();
                config = settings.Clone();
                SetStage(runtime, VanillaReconnectStage.LoggingIn, "Diagnostic step running: " + step);
            }
            ThreadPool.QueueUserWorkItem(_ => DiagnosticStepWorker(owner, accountId, account, config, pid, step, generation));
            RaiseUpdated();
        }

        private void DiagnosticStepWorker(Runtime owner, string accountId, VanillaReconnectAccount account, VanillaReconnectSettings config, int? pid, VanillaReconnectTestStep step, int generation)
        {
            string error = null;
            string stopped = null;
            int? discoveredPid = pid;
            Func<bool> cancelled = () => DiagnosticWorkerCancelled(owner, account, pid, generation);
            try
            {
                if (cancelled()) throw new OperationCanceledException("Diagnostic ownership changed or farming emergency hold is active.");
                Log("TEST " + account.Label + ": starting step " + step + ".");
                if (step == VanillaReconnectTestStep.LauncherGameStart)
                {
                    discoveredPid = VanillaPatcherLauncher.Launch(config.LaunchExecutable, config.LaunchArguments,
                        message => Log("TEST " + account.Label + ": " + message), cancelled,
                        debugDirectory: Path.Combine(baseDirectory, "Logs"),
                        startOwned: start => RunOwnedDiagnosticStart(owner, account, generation, start));
                    if (!discoveredPid.HasValue) throw new InvalidOperationException("Launcher test did not produce a Vanilla process ID.");
                }
                else
                {
                    using (var input = new VanillaForegroundInput(discoveredPid.Value))
                    {
                        input.CancellationRequested = cancelled;
                        switch (step)
                        {
                            case VanillaReconnectTestStep.ProxySelection:
                                ConfirmDefaultService(input, VanillaServiceStep.Proxy, "TEST " + account.Label + ": ");
                                break;
                            case VanillaReconnectTestStep.FillCredentials:
                                string password = store.UnprotectPassword(account.ProtectedPassword);
                                FillDetectedCredentials(input, account, password, discoveredPid.Value, false, "TEST " + account.Label + ": ");
                                break;
                            case VanillaReconnectTestStep.SubmitCredentials:
                                string submitPassword = store.UnprotectPassword(account.ProtectedPassword);
                                new VanillaCredentialVerifier(new VanillaCredentialInput(input)).Submit(account.UserName,
                                    string.IsNullOrEmpty(submitPassword) ? 0 : submitPassword.Length);
                                Log("TEST " + account.Label + ": login submitted after fresh field-focus, exact username and password-mask verification.");
                                break;
                            case VanillaReconnectTestStep.SelectGameServer:
                                ConfirmDefaultService(input, VanillaServiceStep.GameServer, "TEST " + account.Label + ": ");
                                break;
                            case VanillaReconnectTestStep.SelectCharacter:
                                Func<bool> characterCancelled = cancelled;
                                WaitForCharacterSurfaceCancellable(input, discoveredPid.Value, characterCancelled, 30000, "TEST " + account.Label);
                                SelectConfiguredCharacterWithoutCoordinates(input, discoveredPid.Value, account,
                                    characterCancelled, "TEST " + account.Label + ": ");
                                WaitForAutobattleReady(account, discoveredPid.Value, characterCancelled, 60000, "TEST post-character identity");
                                break;
                            case VanillaReconnectTestStep.ResumeHotkey:
                                int resumeGeneration = Volatile.Read(ref resumeVerificationGeneration);
                                Func<bool> resumeCancelled = () =>
                                {
                                    lock (gate)
                                    {
                                        Runtime current;
                                        return cancelled() || resumeGeneration != Volatile.Read(ref resumeVerificationGeneration)
                                            || !runtimes.TryGetValue(accountId, out current) || current.ProcessId != discoveredPid.Value
                                            || !current.Account.Enabled || !current.ScriptRunning;
                                    }
                                };
                                VerifyAutobattleResumeAsync(account, discoveredPid.Value,
                                    resumeCancelled, detail =>
                                    {
                                        lock (gate)
                                        {
                                            Runtime current;
                                            if (cancelled() || !runtimes.TryGetValue(accountId, out current)
                                                || current.ProcessId != discoveredPid.Value) return;
                                            SetStage(current, VanillaReconnectStage.VerifyingAutobattle, "Diagnostic: " + detail);
                                        }
                                        Log("TEST " + account.Label + ": " + detail);
                                        RaiseUpdated();
                                    }).GetAwaiter().GetResult();
                                break;
                        }
                    }
                }
            }
            catch (OperationCanceledException ex) { stopped = ex.Message; }
            catch (Exception ex)
            {
                bool closed = false;
                if (discoveredPid.HasValue)
                {
                    try { using (var process = Process.GetProcessById(discoveredPid.Value)) closed = process.HasExited; }
                    catch { closed = true; }
                }
                if (closed) stopped = "Vanilla window/process was closed.";
                else error = ex.Message;
            }
            finally
            {
                if (generation == Volatile.Read(ref diagnosticGeneration))
                {
                    lock (gate)
                    {
                        Runtime runtime;
                        if (!DiagnosticCancelled(generation) && runtimes.TryGetValue(accountId, out runtime) && ReferenceEquals(runtime, owner)
                            && (step == VanillaReconnectTestStep.LauncherGameStart || runtime.ProcessId == discoveredPid))
                        {
                            if (FarmingEmergencyHeld(runtime))
                            {
                                // Input has been disposed. Release only this diagnostic's
                                // lease, retaining the persistent hold and its PID evidence.
                                runtime.ScriptRunning = false;
                                stopped = FarmingEmergencyDetail(runtime);
                                SetStage(runtime, VanillaReconnectStage.Error, stopped);
                            }
                            else
                            {
                                if (discoveredPid.HasValue) runtime.ProcessId = discoveredPid;
                                if (step == VanillaReconnectTestStep.ResumeHotkey)
                                    RecordDiagnosticResumeResult(runtime, error == null && stopped == null, error ?? stopped);
                                runtime.ScriptRunning = false;
                                if (stopped != null) SetStage(runtime, VanillaReconnectStage.Stopped, "Diagnostic test stopped: " + stopped);
                                else SetStage(runtime, error == null ? VanillaReconnectStage.WaitingForGameplay : VanillaReconnectStage.Error,
                                    error == null ? "Diagnostic step completed: " + step : "Diagnostic step failed: " + error);
                            }
                        }
                    }
                    if (stopped != null) Log("TEST " + account.Label + ": STOPPED: " + stopped);
                    else if (error == null) Log("TEST " + account.Label + ": step " + step + " completed.");
                    else Log("TEST " + account.Label + ": step " + step + " FAILED: " + error);
                    RaiseUpdated();
                }
            }
        }
    }

    internal sealed partial class VanillaReconnectForm
    {
        private Control BuildStepTests()
        {
            var box = new GroupBox
            {
                Text = "Step-by-step selected-account diagnostic",
                Dock = DockStyle.Top,
                Height = 72,
                Padding = new Padding(8),
                AutoSize = false
            };
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = false,
                WrapContents = true,
                Padding = new Padding(0, 4, 0, 0)
            };
            AddButton(row, "1 GAME START", () => RunStepTest(VanillaReconnectTestStep.LauncherGameStart));
            AddButton(row, "2 PROXY", () => RunStepTest(VanillaReconnectTestStep.ProxySelection));
            AddButton(row, "3 FILL USER/PW", () => RunStepTest(VanillaReconnectTestStep.FillCredentials));
            AddButton(row, "4 SUBMIT LOGIN", () => RunStepTest(VanillaReconnectTestStep.SubmitCredentials));
            AddButton(row, "5 SERVER", () => RunStepTest(VanillaReconnectTestStep.SelectGameServer));
            AddButton(row, "6 CHARACTER", () => RunStepTest(VanillaReconnectTestStep.SelectCharacter));
            AddButton(row, "7 RESUME HOTKEY", () => RunStepTest(VanillaReconnectTestStep.ResumeHotkey));
            AddButton(row, "STOP TEST", StopCurrentTest);
            box.Controls.Add(row);
            return box;
        }

        private void ConfigureStepTestHoverHelp()
        {
            TipByText(this, "1 GAME START", "Tests the selected account only. If that account already owns a running Vanilla client, step 1 is already satisfied. Otherwise a new client may be started while other configured clients remain open, up to the configured Clients limit.");
            TipByText(this, "2 PROXY", "Selected account only. If its Vanilla client exists, test only Proxy. If it does not exist and the configured client limit has room, start a new selected-account client and run through Proxy.");
            TipByText(this, "3 FILL USER/PW", "Selected account only. Existing selected client: fill credentials only. Missing selected client: start it first if the configured client limit has room. Credentials are filled without submitting.");
            TipByText(this, "4 SUBMIT LOGIN", "Selected account only. Existing selected client: submit only. Missing selected client: start it and run prerequisite steps first.");
            TipByText(this, "5 SERVER", "Selected account only. Existing selected client: server step only. Missing selected client: start it and run prerequisite steps first.");
            TipByText(this, "6 CHARACTER", "Selected account only. Existing selected client: character step only. Missing selected client: start it and run prerequisite steps first.");
            TipByText(this, "7 RESUME HOTKEY", "Selected account only. Existing selected client: send only the resume hotkey. Missing selected client: start it and run the complete prerequisite sequence first.");
            TipByText(this, "STOP TEST", "Cancel the current diagnostic/recovery test immediately. Closing the launcher or the selected diagnostic Vanilla client also stops the test automatically.");
        }

        private void StopCurrentTest()
        {
            supervisor.CancelDiagnosticTest();
            if (supervisor.IsRunning) supervisor.Stop();
            testGeneration++;
            testRunning = false;
            testState.Text = "TEST STOPPED";
            testState.ForeColor = Color.DarkOrange;
        }

        private void CompleteTestStopped(int generation, string message)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)(() => CompleteTestStopped(generation, message))); return; }
            if (generation != testGeneration) return;
            testRunning = false;
            testState.Text = "TEST STOPPED";
            testState.ForeColor = Color.DarkOrange;
        }

        private void RunStepTest(VanillaReconnectTestStep step)
        {
            var selected = SelectedAccount();
            if (selected == null) { MessageBox.Show(this, "Select one account row first.", "Step test"); return; }
            try
            {
                if (supervisor.FarmingEmergencyHeld(selected))
                    throw new InvalidOperationException("This character is on a farming emergency hold; diagnostic input is blocked.");
                if (testRunning)
                {
                    supervisor.CancelDiagnosticTest();
                    testRunning = false;
                    testGeneration++;
                }

                ReadTop();
                supervisor.Apply(settings, true);
                supervisor.Stop();
                int detected = supervisor.DetectRunningClients();

                var live = Process.GetProcessesByName("Vanilla MMO");
                int[] livePids;
                try { livePids = live.Where(p => !p.HasExited).OrderBy(p => p.StartTime).Select(p => p.Id).ToArray(); }
                finally { foreach (var process in live) process.Dispose(); }

                var selectedStatus = supervisor.Statuses().FirstOrDefault(s => string.Equals(s.AccountId, selected.Id, StringComparison.OrdinalIgnoreCase));
                bool selectedRunning = selectedStatus != null && selectedStatus.ProcessId.HasValue && IsRunningProcess(selectedStatus.ProcessId.Value);
                supervisor.RecordTestLog("step=" + step + "; selected='" + selected.Label + "'; detected=" + detected
                    + "; livePIDs=[" + string.Join(",", livePids) + "]; selectedPID="
                    + (selectedRunning ? selectedStatus.ProcessId.Value.ToString() : "none") + "; maxClients=" + settings.MaxClients + ".");

                if (selectedRunning)
                {
                    if (step == VanillaReconnectTestStep.LauncherGameStart)
                    {
                        int already = BeginTest("STEP 1 already satisfied for " + selected.Label);
                        CompleteTest(already, true, "The selected account already has a running Vanilla client (PID " + selectedStatus.ProcessId.Value
                            + "). GAME START is already satisfied for this selected account. Other configured clients do not block testing.");
                        return;
                    }

                    int generation = BeginTest("STEP TEST: " + step + " for " + selected.Label + " (PID " + selectedStatus.ProcessId.Value + ")");
                    supervisor.RunDiagnosticStep(selected.Id, step);
                    WaitForDiagnosticStep(generation, selected.Id, step, 45000);
                    return;
                }

                if (livePids.Length >= settings.MaxClients)
                    throw new InvalidOperationException("The selected account has no assigned running Vanilla client and the configured client limit ("
                        + settings.MaxClients + ") is already reached. Select the account that owns one of the running clients, or stop one before starting another.");

                int chainGeneration = BeginTest("CHAIN TEST for selected " + selected.Label + " from GAME START through " + step
                    + " (" + livePids.Length + "/" + settings.MaxClients + " other/current clients running)");
                RunDiagnosticChainFromScratch(chainGeneration, selected.Id, step);
            }
            catch (Exception ex) { FailTestImmediately("Step test " + step, ex); }
        }

        private static bool IsRunningProcess(int pid)
        {
            try { using (var process = Process.GetProcessById(pid)) return !process.HasExited; }
            catch { return false; }
        }

        private void RunDiagnosticChainFromScratch(int generation, string accountId, VanillaReconnectTestStep targetStep)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    for (int value = (int)VanillaReconnectTestStep.LauncherGameStart; value <= (int)targetStep; value++)
                    {
                        if (generation != testGeneration) return;
                        var step = (VanillaReconnectTestStep)value;
                        if (step != VanillaReconnectTestStep.LauncherGameStart) supervisor.AssignSingleDiagnosticClient(accountId);
                        supervisor.RunDiagnosticStep(accountId, step);
                        string failure;
                        if (!WaitForDiagnosticCompletion(accountId, step, step == VanillaReconnectTestStep.LauncherGameStart ? 150000 : 45000, out failure))
                        {
                            if (failure != null && failure.StartsWith("STOPPED:", StringComparison.Ordinal)) CompleteTestStopped(generation, failure.Substring(8).Trim());
                            else CompleteTest(generation, false, failure);
                            return;
                        }
                        if (step == targetStep)
                        {
                            CompleteTest(generation, true, "Chain test passed through " + targetStep + ". If the selected account screen is correct, continue with the next numbered step.");
                            return;
                        }

                        int delay = step == VanillaReconnectTestStep.LauncherGameStart
                            ? Math.Max(1000, settings.GepardWaitMs)
                            : step == VanillaReconnectTestStep.SelectCharacter
                                ? Math.Max(1000, settings.GameLoadMs)
                                : Math.Max(500, settings.StageDelayMs);
                        int waited = 0;
                        while (waited < delay)
                        {
                            if (generation != testGeneration) return;
                            int slice = Math.Min(250, delay - waited);
                            Thread.Sleep(slice);
                            waited += slice;
                        }
                    }
                }
                catch (Exception ex)
                {
                    if (ex.Message.IndexOf("No unassigned running Vanilla client", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("No running Vanilla client", StringComparison.OrdinalIgnoreCase) >= 0
                        || ex.Message.IndexOf("Vanilla client exited", StringComparison.OrdinalIgnoreCase) >= 0)
                        CompleteTestStopped(generation, "Selected Vanilla window/process was closed; test stopped.");
                    else CompleteTest(generation, false, "Chain test failed: " + ex.Message);
                }
            });
        }

        private bool WaitForDiagnosticCompletion(string accountId, VanillaReconnectTestStep step, int timeoutMs, out string failure)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            string successDetail = "Diagnostic step completed: " + step;
            string failurePrefix = "Diagnostic step failed:";
            string stoppedPrefix = "Diagnostic test stopped:";
            while (DateTime.UtcNow < deadline)
            {
                var current = supervisor.Statuses().FirstOrDefault(s => string.Equals(s.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
                if (current != null)
                {
                    if (string.Equals(current.Detail, successDetail, StringComparison.Ordinal))
                    {
                        failure = null;
                        return true;
                    }
                    if (current.Detail != null && current.Detail.StartsWith(stoppedPrefix, StringComparison.Ordinal))
                    {
                        failure = "STOPPED: " + current.Detail;
                        return false;
                    }
                    if (current.Detail != null && current.Detail.StartsWith(failurePrefix, StringComparison.Ordinal))
                    {
                        failure = current.Detail;
                        return false;
                    }
                }
                Thread.Sleep(250);
            }
            failure = "Step " + step + " timed out. Press COPY LOG and paste the reconnect log for analysis.";
            return false;
        }

        private void WaitForDiagnosticStep(int generation, string accountId, VanillaReconnectTestStep step, int timeoutMs)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                string successDetail = "Diagnostic step completed: " + step;
                string failurePrefix = "Diagnostic step failed:";
                string stoppedPrefix = "Diagnostic test stopped:";
                while (DateTime.UtcNow < deadline)
                {
                    var current = supervisor.Statuses().FirstOrDefault(s => string.Equals(s.AccountId, accountId, StringComparison.OrdinalIgnoreCase));
                    if (current != null)
                    {
                        if (string.Equals(current.Detail, successDetail, StringComparison.Ordinal))
                        {
                            CompleteTest(generation, true, "Step " + step + " passed for the selected account. Continue with the next numbered step when that Vanilla screen is ready.");
                            return;
                        }
                        if (current.Detail != null && current.Detail.StartsWith(stoppedPrefix, StringComparison.Ordinal))
                        {
                            CompleteTestStopped(generation, current.Detail);
                            return;
                        }
                        if (current.Detail != null && current.Detail.StartsWith(failurePrefix, StringComparison.Ordinal))
                        {
                            CompleteTest(generation, false, current.Detail);
                            return;
                        }
                    }
                    Thread.Sleep(250);
                }
                CompleteTest(generation, false, "Step " + step + " timed out. Press COPY LOG and paste the reconnect log for analysis.");
            });
        }
    }
}
