using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaAutobattleResumeTests
    {
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        private static int passed, failed;

        internal static int Run()
        {
            Test("Resume profiles are loaded from the executable directory", ProfileDirectory);
            Test("STOP cancels diagnostic completions", DiagnosticStop);
            Test("Settings changes cancel diagnostic completions", DiagnosticSettingsChange);
            Test("Rejected diagnostic requests keep the current generation", DiagnosticRejectedRequest);
            Test("Resume completion rejects a replaced PID", OwnedCompletion);
            Test("Emergency hold cancels only the affected startup and resume owner", EmergencyStartupAndResumeGates);
            Test("Emergency hold blocks manual resume before opening a process", EmergencyManualResumeGate);
            Test("Emergency hold blocks owned launcher and normal close steps", EmergencyOwnedInputGates);
            Test("Emergency hold cancels teleport and releases its lease without holding a sibling", EmergencyTeleportGate);
            Test("Emergency hold blocks every diagnostic and releases its cancelled owner", EmergencyDiagnosticGates);
            Test("Failed or interrupted startup cannot be adopted as healthy", SafeAdoption);
            Test("Successful explicit resume clears the failed latch", DiagnosticResult);
            Test("X movement verifies the first attempt", () => Success(1, false));
            Test("Y-only movement verifies the first attempt", () => Success(1, true));
            Test("Second attempt succeeds without a third key", () => Success(2, false));
            Test("Third attempt succeeds without a fourth key", () => Success(3, true));
            Test("No movement runs three autoattack/teleport cycles then waits to 180 seconds", BoundedFailure);
            Test("Movement after autoattack suppresses teleport", TeleportSuppressedByMovement);
            Test("Movement caused by teleport stops the recovery cycle immediately", MovementAfterTeleport);
            Test("Restart-only settle, recovery cycles and 180-second deadline stay bounded", RecoveryConstants);
            Test("Unknown visual state does not block verified post-login memory input", VisualGate);
            Test("A completed verifier cannot reset its retry budget", NoRestart);
            Test("Intermediate movement is seen even if the character returns", MovementAndReturn);
            Test("Movement at the deadline prevents another toggle", DeadlineMovement);
            Test("Movement while refocusing prevents a retry", RefocusMovement);
            Test("Autobattle STOP requires five continuous stationary seconds", StopStationaryWindow);
            Test("Autobattle STOP resets stillness when X/Y moves", StopMovementResetsWindow);
            Test("Autobattle STOP retries on the ten-second cadence", StopRetryCadence);
            Test("Autobattle STOP aborts immediately when HP falls by more than ten percent", StopHpDamageAbort);
            Test("Autobattle STOP HP guard stays cumulative across retries", StopHpCumulativeAcrossRetries);
            Test("Autobattle STOP tolerates exactly ten percent HP loss", StopHpExactThreshold);
            Test("Autobattle STOP stops after three moving attempts", StopBoundedFailure);
            Test("Autobattle STOP verification constants stay bounded", StopVerificationConstants);
            Test("Cancellation before startup sends no key", CancelBeforeStart);
            Test("Cancellation during observation sends no late key", CancelDuringWait);
            Test("Cancellation during focus sends no key", CancelDuringFocus);
            Test("Cancellation during baseline read sends no key", CancelDuringRead);
            Test("Focus failure never sends the hotkey", FocusFailure);
            Test("Retry focus failure does not consume another hotkey", RetryFocusFailure);
            Test("Unavailable coordinates are not zero or stillness", UnavailableCoordinates);
            Test("Unverified coordinates cannot authorize input", UnverifiedCoordinates);
            Test("Actual coordinate zero remains valid", ValidZero);
            Test("Old timestamp fails closed", StaleCoordinates);
            Test("Cached snapshot cannot become a fresh observation", CachedSnapshot);
            Test("A slow read cannot authorize a hotkey", SlowRead);
            Test("Session replacement invalidates verification", () => Changed(s => s.SessionId = Guid.NewGuid()));
            Test("Process replacement invalidates verification", () => Changed(s => s.ProcessId = 43));
            Test("Map transition is not verified movement", () => Changed(s => Set(s, VanillaField.Map, "new_map")));
            Test("Character replacement invalidates verification", () => Changed(s => Set(s, VanillaField.CharacterName, "other")));
            Test("Death cancels the remaining attempts", () => Changed(s => Set(s, VanillaField.CurrentHP, 0U)));
            Test("Read failure stops without retrying memory access", ReadFailure);
            Test("Monotonic deadline does not depend on wall-clock jumps", WallClockChange);
            Test("Client positions cannot satisfy another client's check", IndependentClients);
            Test("Startup gate requires verified movement and minimization", StartupGate);
            Test("Verifier and failed client are not automatically minimized", MinimizeGuard);
            Test("Account status exposes verification and retry progress", StatusProgress);
            Test("STOP invalidates a worker even when its PID is retained", WorkerStop);
            Test("Settings changes release the verification lease and latch failure", WorkerSettingsChange);
            Test("Mail-only edits preserve an active input lease but Cart edits still cancel", WorkerMailSettingsChange);
            Test("Normal supervision has no automatic resume queue after a failed diagnostic", WorkerFailureLatch);
            Test("A new operation cannot be completed by the old worker", WorkerReplacement);
            Test("Guarded chord releases every held key", ChordSuccess);
            Test("Cancellation between modifier and key releases the modifier", ChordCancellation);
            Test("Input failure releases previously held modifiers", ChordFailure);
            Test("Release failure does not strand other held modifiers", ChordReleaseFailure);
            Console.WriteLine("Autobattle resume: {0} passed; {1} failed. Fake clock, state and input only.", passed, failed);
            return failed;
        }

        private static void VisualGate()
        {
            Assert(!VanillaReconnectSupervisor.AutobattleVisualBlocksInput(VanillaVisualState.Unknown),
                "Unknown visual state incorrectly blocked verified memory-backed hotkey input.");
            Assert(!VanillaReconnectSupervisor.AutobattleVisualBlocksInput(VanillaVisualState.Gameplay),
                "Gameplay visual state was unexpectedly blocked.");
            foreach (var blocked in new[] { VanillaVisualState.LoginShell, VanillaVisualState.ModalDialog,
                VanillaVisualState.LoggingOut, VanillaVisualState.Disconnected, VanillaVisualState.ServerClosed })
                Assert(VanillaReconnectSupervisor.AutobattleVisualBlocksInput(blocked),
                    "Unsafe visual state did not block input: " + blocked);
        }

        private static void ProfileDirectory()
        {
            Assert(VanillaReconnectSupervisor.AutobattleBuildProfileDirectory ==
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBuilds"), "Profiles came from user data.");
        }
        private static void DiagnosticStop()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                Field(supervisor, "diagnosticGeneration").SetValue(supervisor, 7);
                supervisor.Stop();
                Assert((bool)Method("DiagnosticCancelled").Invoke(supervisor, new object[] { 7 }), "STOP retained an old diagnostic.");
            });
        }
        private static void DiagnosticSettingsChange()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                Field(supervisor, "diagnosticGeneration").SetValue(supervisor, 7);
                supervisor.Apply(supervisor.Settings, false);
                Assert((bool)Method("DiagnosticCancelled").Invoke(supervisor, new object[] { 7 }), "Changed settings retained an old diagnostic.");
            });
        }
        private static void DiagnosticRejectedRequest()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                Field(supervisor, "diagnosticGeneration").SetValue(supervisor, 7);
                try { supervisor.RunDiagnosticStep(supervisor.Settings.Accounts[0].Id, VanillaReconnectTestStep.ResumeHotkey); }
                catch (InvalidOperationException) { }
                Assert((int)Field(supervisor, "diagnosticGeneration").GetValue(supervisor) == 7,
                    "Rejected request cancelled the current diagnostic while retaining its lease.");
            });
        }
        private static FieldInfo Field(object owner, string name) { return owner.GetType().GetField(name, PrivateInstance); }
        private static MethodInfo Method(string name)
        {
            var result = typeof(VanillaReconnectSupervisor).GetMethod(name,
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert(result != null, "Missing guarded operation: " + name);
            return result;
        }
        private static void OwnedCompletion()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                bool called = false;
                Func<bool> action = () => { called = true; return true; };
                Func<bool> active = () => false;
                Assert((bool)Method("RunOwnedClientStep").Invoke(supervisor, new object[] { runtime, 42, active, action }) && called,
                    "Active owner could not complete its step.");
                called = false;
                RuntimeField(runtime, "ProcessId", (int?)43);
                try { Method("RunOwnedClientStep").Invoke(supervisor, new object[] { runtime, 42, active, action }); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is OperationCanceledException, "Wrong cancellation error."); }
                Assert(!called, "Old worker touched the replacement client.");
                RuntimeField(runtime, "ProcessId", (int?)42);
                try { Method("RunOwnedClientStep").Invoke(supervisor, new object[] { runtime, 42, (Func<bool>)(() => true), action }); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is OperationCanceledException, "Wrong cancellation error."); }
                Assert(!called, "Cancelled worker completed an action.");
            });
        }
        private static void SafeAdoption()
        {
            var method = Method("CanAdoptExistingGameplayClient");
            foreach (bool sent in new[] { false, true })
                foreach (bool failed in new[] { false, true })
                    foreach (bool busy in new[] { false, true })
                        Assert((bool)method.Invoke(null, new object[] { sent, failed, busy }) == (sent && !failed && !busy),
                            "Unverified or failed client was treated as healthy.");
        }

        private static VanillaReconnectAccount RuntimeAccount(object runtime)
        { return (VanillaReconnectAccount)runtime.GetType().GetField("Account").GetValue(runtime); }

        private static object EmergencySibling(VanillaReconnectSupervisor supervisor, object runtime)
        {
            var table = (IDictionary)Field(supervisor, "runtimes").GetValue(supervisor);
            object sibling = table.Values.Cast<object>().First(item => !ReferenceEquals(item, runtime));
            var account = RuntimeAccount(sibling);
            account.Enabled = true; account.UserName = "synthetic-farm"; account.CharacterName = "HealthySibling";
            RuntimeField(sibling, "ProcessId", (int?)43);
            RuntimeField(sibling, "Stage", VanillaReconnectStage.Online);
            return sibling;
        }

        private static void InstallEmergencyHold(VanillaReconnectSupervisor supervisor, object runtime)
        {
            var account = RuntimeAccount(runtime);
            account.Enabled = true; account.UserName = "synthetic-farm"; account.CharacterName = "HeldCharacter";
            string path = (string)Field(supervisor, "farmingEmergencyPath").GetValue(supervisor);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                Version = 1,
                Holds = new[] { new { UserName = account.UserName, CharacterName = account.CharacterName,
                    ObservedAt = Epoch, Ratios = "synthetic critical farming ratios", Detail = "Synthetic persistent farming emergency hold" } }
            }));
            Method("InitializeFarmingEmergency").Invoke(supervisor, null);
            Assert(supervisor.FarmingEmergencyHeld(account), "Persisted synthetic hold was not loaded.");
        }

        private static void EmergencyStartupAndResumeGates()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                object sibling = EmergencySibling(supervisor, runtime);
                InstallEmergencyHold(supervisor, runtime);
                Assert(IsCancelled(supervisor, runtime), "Held client retained its resume worker authorization.");
                Assert((bool)Method("StartupAccountCancelled").Invoke(supervisor, new object[] { 0, RuntimeAccount(runtime) }),
                    "Held character remained eligible for sequential startup.");
                Assert(!(bool)Method("StartupAccountCancelled").Invoke(supervisor, new object[] { 0, RuntimeAccount(sibling) }),
                    "One character's hold cancelled its same-username healthy sibling.");
                Assert((int)Field(supervisor, "resumeVerificationGeneration").GetValue(supervisor) == 7
                    && RuntimeFlag(runtime, "ScriptRunning") && RuntimeFlag(runtime, "RecoveryOwned"),
                    "Checking a hold changed the global generation or released input before its worker unwound.");
                int requested = 0;
                supervisor.SetAutobattleResumeTestHook((id, trigger, recovery) => requested++);
                Method("RequestVerifiedResume").Invoke(supervisor, new object[] { runtime, "synthetic", true });
                Assert(requested == 0, "Held character queued an automatic resume.");
                Method("RequestVerifiedResume").Invoke(supervisor, new object[] { sibling, "synthetic", true });
                Assert(requested == 1, "Healthy sibling's resume authorization was cancelled.");
            });
        }

        private static void EmergencyManualResumeGate()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                InstallEmergencyHold(supervisor, runtime);
                var task = (Task)Method("VerifyAutobattleResumeAsync").Invoke(supervisor, new object[]
                {
                    RuntimeAccount(runtime), int.MaxValue, (Func<bool>)(() => false), (Action<string>)(_ => { })
                });
                try { task.GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { return; }
                throw new Exception("Manual resume did not reject an emergency hold before process access.");
            });
        }

        private static void EmergencyOwnedInputGates()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                InstallEmergencyHold(supervisor, runtime);
                bool called = false;
                try { Method("RunOwnedClientStep").Invoke(supervisor, new object[]
                    { runtime, 42, (Func<bool>)(() => false), (Func<bool>)(() => { called = true; return true; }) }); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is OperationCanceledException, "Wrong held-client cancellation."); }
                Assert(!called, "Normal close/input step ran while the emergency worker owned protection.");
                RuntimeField(runtime, "ProcessId", (int?)null);
                try { Method("RunOwnedLauncherStart").Invoke(supervisor, new object[]
                    { runtime, 7, (Func<bool>)(() => false), (Func<System.Diagnostics.Process>)(() => { called = true; return null; }) }); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is OperationCanceledException, "Wrong held-launcher cancellation."); }
                Assert(!called, "A held character launched a replacement process.");
            });
        }

        private static void EmergencyTeleportGate()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                object sibling = EmergencySibling(supervisor, runtime);
                var account = RuntimeAccount(runtime);
                account.UserName = "synthetic-farm"; account.CharacterName = "HeldCharacter"; account.Enabled = true;
                account.SmartTeleportEnabled = true; account.SmartTeleportKey = (int)Keys.F5;
                RuntimeAccount(sibling).SmartTeleportEnabled = true; RuntimeAccount(sibling).SmartTeleportKey = (int)Keys.F6;
                RuntimeField(runtime, "ScriptRunning", false); RuntimeField(runtime, "RecoveryOwned", false);
                RuntimeField(runtime, "Stage", VanillaReconnectStage.Online);
                Field(supervisor, "running").SetValue(supervisor, true);
                VanillaSmartTeleportToken token; string reason;
                Assert(supervisor.TryBeginSmartTeleport(42, out token, out reason), "Synthetic teleport lease failed: " + reason);
                InstallEmergencyHold(supervisor, runtime);
                Assert(supervisor.SmartTeleportCancelled(token), "A newly latched hold did not cancel the in-flight teleport.");
                Assert(RuntimeFlag(runtime, "ScriptRunning"), "Hold released teleport input before operation unwind.");
                supervisor.CompleteSmartTeleport(token, "Synthetic cancellation unwind");
                Assert(!RuntimeFlag(runtime, "ScriptRunning"), "Cancelled teleport retained its input lease.");
                Assert((VanillaReconnectStage)runtime.GetType().GetField("Stage").GetValue(runtime) == VanillaReconnectStage.Error,
                    "Teleport completion overwrote the emergency hold status.");
                Assert(!supervisor.TryBeginSmartTeleport(42, out token, out reason), "Held client acquired a new teleport lease.");
                Assert(supervisor.TryBeginSmartTeleport(43, out token, out reason), "Healthy sibling could not acquire released teleport input: " + reason);
                Assert(!supervisor.SmartTeleportCancelled(token), "Healthy sibling inherited the other character's hold.");
                supervisor.CompleteSmartTeleport(token, "Synthetic sibling complete");
            });
        }

        private static void EmergencyDiagnosticGates()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                object sibling = EmergencySibling(supervisor, runtime);
                InstallEmergencyHold(supervisor, runtime);
                Field(supervisor, "diagnosticGeneration").SetValue(supervisor, 7);
                foreach (VanillaReconnectTestStep step in Enum.GetValues(typeof(VanillaReconnectTestStep)))
                {
                    bool rejected = false;
                    try { supervisor.RunDiagnosticStep(RuntimeAccount(runtime).Id, step); }
                    catch (InvalidOperationException ex) { rejected = ex.Message.Contains("emergency hold"); }
                    Assert(rejected, "Emergency hold did not reject diagnostic input: " + step);
                }
                Assert((int)Field(supervisor, "diagnosticGeneration").GetValue(supervisor) == 7,
                    "Rejecting held diagnostics invalidated another diagnostic generation.");
                bool started = false;
                try { Method("RunOwnedDiagnosticStart").Invoke(supervisor, new object[]
                    { runtime, RuntimeAccount(runtime), 7, (Func<System.Diagnostics.Process>)(() => { started = true; return null; }) }); }
                catch (TargetInvocationException ex) { Assert(ex.InnerException is OperationCanceledException, "Wrong diagnostic launcher cancellation."); }
                Assert(!started, "Held diagnostic started a launcher process.");
                RuntimeField(runtime, "RecoveryOwned", false);
                Method("DiagnosticStepWorker").Invoke(supervisor, new object[]
                    { runtime, RuntimeAccount(runtime).Id, RuntimeAccount(runtime).Clone(), supervisor.Settings,
                        (int?)42, VanillaReconnectTestStep.FillCredentials, 7 });
                Assert(!RuntimeFlag(runtime, "ScriptRunning") && supervisor.FarmingEmergencyHeld(RuntimeAccount(runtime)),
                    "Cancelled diagnostic retained its input lease or removed the emergency hold.");
                Assert(!supervisor.FarmingEmergencyHeld(RuntimeAccount(sibling))
                    && (int)Field(supervisor, "diagnosticGeneration").GetValue(supervisor) == 7,
                    "Diagnostic unwind cancelled its healthy sibling or changed the shared generation.");
            });
        }
        private static void DiagnosticResult()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                RuntimeField(runtime, "ResumeVerificationFailed", true);
                Method("RecordDiagnosticResumeResult").Invoke(null, new object[] { runtime, true, null });
                Assert(RuntimeFlag(runtime, "ResumeSent") && !RuntimeFlag(runtime, "ResumeVerificationFailed"),
                    "A successful explicit retry retained its failed latch.");
                Method("RecordDiagnosticResumeResult").Invoke(null, new object[] { runtime, false, "No movement" });
                Assert(!RuntimeFlag(runtime, "ResumeSent") && RuntimeFlag(runtime, "ResumeVerificationFailed"),
                    "Failed explicit retry was presented as successful.");
            });
        }

        private static readonly BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void RuntimeCase(Action<VanillaReconnectSupervisor, object> test)
        {
            string root = Path.Combine(Path.GetTempPath(), "4r-resume-test-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var supervisor = new VanillaReconnectSupervisor(root))
                {
                    var table = (IDictionary)typeof(VanillaReconnectSupervisor).GetField("runtimes", PrivateInstance).GetValue(supervisor);
                    object runtime = table[supervisor.Settings.Accounts[0].Id];
                    RuntimeField(runtime, "ProcessId", (int?)42);
                    RuntimeField(runtime, "ScriptRunning", true);
                    RuntimeField(runtime, "RecoveryOwned", true);
                    RuntimeField(runtime, "Stage", VanillaReconnectStage.VerifyingAutobattle);
                    RuntimeField(runtime, "ResumeOperationGeneration", 7);
                    typeof(VanillaReconnectSupervisor).GetField("resumeVerificationGeneration", PrivateInstance).SetValue(supervisor, 7);
                    test(supervisor, runtime);
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static void RuntimeField(object runtime, string name, object value)
        {
            runtime.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance).SetValue(runtime, value);
        }
        private static bool RuntimeFlag(object runtime, string name)
        {
            return (bool)runtime.GetType().GetField(name, BindingFlags.Public | BindingFlags.Instance).GetValue(runtime);
        }
        private static bool IsCancelled(VanillaReconnectSupervisor supervisor, object runtime)
        {
            return (bool)typeof(VanillaReconnectSupervisor).GetMethod("ResumeWorkerCancelled", PrivateInstance)
                .Invoke(supervisor, new object[] { runtime, 42, 7 });
        }
        private static void WorkerStop()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                Assert(!IsCancelled(supervisor, runtime), "Active worker was already cancelled.");
                supervisor.Stop();
                Assert(IsCancelled(supervisor, runtime) && !RuntimeFlag(runtime, "ScriptRunning")
                    && !RuntimeFlag(runtime, "RecoveryOwned"), "STOP retained a live input lease.");
                RuntimeField(runtime, "ScriptRunning", true);
                Assert(IsCancelled(supervisor, runtime), "Same PID revived an old worker.");
            });
        }
        private static void WorkerMailSettingsChange()
        {
  RuntimeCase((supervisor, runtime) =>
  {
      Field(supervisor, "weightMaintenanceGeneration").SetValue(supervisor, 7);
      var settings = supervisor.Settings;
      var account = settings.Accounts[0];
      account.CartMaintenanceEnabled = account.EffectiveCartMaintenanceEnabled;
      account.WeightEmailEnabled = !account.EffectiveWeightEmailEnabled;
      account.WeightEnabled = account.EffectiveCartMaintenanceEnabled || account.EffectiveWeightEmailEnabled;
      supervisor.Apply(settings, false);
      Assert(!IsCancelled(supervisor, runtime) && RuntimeFlag(runtime, "ScriptRunning")
          && RuntimeFlag(runtime, "RecoveryOwned") && !RuntimeFlag(runtime, "ResumeVerificationFailed"),
          "Mail-only edit cancelled or failed an active input worker.");
      Assert((int)Field(supervisor, "weightMaintenanceGeneration").GetValue(supervisor) == 7,
          "Mail-only edit invalidated Cart ownership.");
      var updated = (VanillaReconnectAccount)runtime.GetType().GetField("Account").GetValue(runtime);
      Assert(updated.EffectiveWeightEmailEnabled == account.EffectiveWeightEmailEnabled,
          "Mail-only edit was not applied to the live runtime.");
      settings = supervisor.Settings;
      settings.Accounts[0].CartMaintenanceEnabled = !settings.Accounts[0].EffectiveCartMaintenanceEnabled;
      supervisor.Apply(settings, false);
      Assert(IsCancelled(supervisor, runtime) && !RuntimeFlag(runtime, "RecoveryOwned"),
          "Cart policy edit failed to cancel the input worker.");
  });
        }

        private static void WorkerSettingsChange()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                supervisor.Apply(supervisor.Settings, false);
                Assert(IsCancelled(supervisor, runtime) && RuntimeFlag(runtime, "ResumeVerificationFailed")
                    && !RuntimeFlag(runtime, "RecoveryOwned"), "Configuration change did not fail closed.");
            });
        }
        private static void WorkerFailureLatch()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                RuntimeField(runtime, "ScriptRunning", false);
                RuntimeField(runtime, "RecoveryOwned", false);
                RuntimeField(runtime, "ResumeVerificationFailed", true);
                Assert(!RuntimeFlag(runtime, "ScriptRunning") && !RuntimeFlag(runtime, "RecoveryOwned"),
                    "A failed diagnostic unexpectedly armed automatic input.");
            });
        }
        private static void WorkerReplacement()
        {
            RuntimeCase((supervisor, runtime) =>
            {
                RuntimeField(runtime, "ResumeOperationGeneration", 8);
                Assert(IsCancelled(supervisor, runtime), "An old worker still owns the replacement operation.");
            });
        }

        private static void StatusProgress()
        {
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.VerifyingAutobattle,
                "Sequential startup: Autobattle hotkey sent; verifying X/Y movement 1/3 (10s)") == "Verify 1/3", "First attempt hidden.");
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.VerifyingAutobattle,
                "Retrying autobattle 2/3") == "Retry 2/3", "Retry hidden.");
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.VerifyingAutobattle,
                "Movement verified after 3/3 attempts") == "Movement verified", "Success hidden.");
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.Online, "Gameplay") == "Online", "Existing status changed.");
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.WaitingForServer,
                "Server unavailable: next check 12:15:00 (in 15m; every 15 minutes)") == "Server down; check 12:15:00",
                "Server availability deadline hidden from the account row.");
            Assert(VanillaAutobattleStatus.Compact(VanillaReconnectStage.WaitingForServer,
                "Server unavailable; one account is checking through the normal login flow.") == "Server down; queued",
                "Queued server check status was lost.");
        }

        private sealed class Harness
        {
            internal int Pid = 42, Sends, Teleports, Focuses;
            internal long Ms;
            internal bool Cancelled;
            internal readonly Guid Session = Guid.NewGuid();
            internal readonly VanillaAutobattleResumeVerifier Verifier = new VanillaAutobattleResumeVerifier();
            internal readonly List<long> SentAt = new List<long>();
            internal readonly List<long> TeleportAt = new List<long>();
            internal readonly List<string> Progress = new List<string>();
            internal Func<VanillaClientState> ReadOverride;
            internal Func<int, bool> TeleportOverride;
            internal Action OnFocus, OnDelay;
            internal long UtcOffset;
            internal DateTimeOffset Now { get { return Epoch.AddMilliseconds(Ms + UtcOffset); } }
            internal VanillaClientState Sample(int x = 10, int y = 20, uint hp = 100, uint maxHp = 100)
            {
                var state = VanillaClientState.Create(Session, Now, null, new Dictionary<VanillaField, object>
                {
                    { VanillaField.X, x }, { VanillaField.Y, y }, { VanillaField.Map, "map" },
                    { VanillaField.CharacterName, "fake character" }, { VanillaField.CurrentHP, hp }, { VanillaField.MaxHP, maxHp }
                }, null, null);
                state.ProcessId = Pid;
                foreach (var field in state.Fields.Values.Where(v => v.IsAvailable)) field.Validation = StateValidation.Valid;
                return state;
            }
            internal Task RunAsync()
            {
                return Verifier.VerifyAsync(Pid, () => ReadOverride == null ? Sample() : ReadOverride(),
                    () => { Focuses++; OnFocus?.Invoke(); },
                    () => { Sends++; SentAt.Add(Ms); },
                    attempt =>
                    {
                        Teleports++;
                        TeleportAt.Add(Ms);
                        return TeleportOverride == null || TeleportOverride(attempt);
                    },
                    () => Cancelled, () => TimeSpan.FromMilliseconds(Ms), () => Now,
                    milliseconds => { Ms += milliseconds; OnDelay?.Invoke(); return Task.FromResult(0); }, Progress.Add);
            }
            internal void Run() { RunAsync().GetAwaiter().GetResult(); }
        }

        private static void Success(int attempt, bool y)
        {
            var h = new Harness();
            h.ReadOverride = () => h.Sample(h.Sends >= attempt && !y ? 11 : 10, h.Sends >= attempt && y ? 21 : 20);
            h.Run();
            Assert(h.Verifier.MovementVerified && h.Sends == attempt, "Incorrect success or recovery-cycle count.");
            Assert(h.Teleports == attempt - 1, "Teleport ran even though movement had already been verified.");
            Assert(h.Ms == (attempt - 1) * 20000, "Each failed autoattack/teleport cycle must consume two 10-second observation windows.");
        }
        private static void BoundedFailure()
        {
            var h = new Harness();
            Expect<InvalidOperationException>(h.Run, "3 autobattle + teleport recovery cycles within 180 seconds");
            Assert(h.Sends == 3 && h.Teleports == 3 && h.Ms == 180000 && !h.Verifier.MovementVerified,
                "Recovery did not preserve the three-cycle input budget and 180-second restart deadline.");
            Assert(h.SentAt.SequenceEqual(new long[] { 0, 20000, 40000 }), "Unexpected autobattle timing.");
            Assert(h.TeleportAt.SequenceEqual(new long[] { 10000, 30000, 50000 }), "Teleport did not follow each stationary 10-second autobattle window.");
            Assert(h.Progress.Contains("Retrying autobattle recovery cycle 2/3")
                && h.Progress.Contains("Retrying autobattle recovery cycle 3/3"), "Recovery-cycle progress missing.");
            Assert(h.Progress.Contains("Sending autobattle hotkey attempt 1/3")
                && h.Progress.Contains("Sending autobattle hotkey attempt 2/3")
                && h.Progress.Contains("Sending autobattle hotkey attempt 3/3"), "Hotkey sends were not explicitly logged.");
            Assert(h.Progress.Contains("Three recovery cycles exhausted; monitoring X/Y until the 180s restart deadline"),
                "Passive watch to the restart deadline was not reported.");
        }
        private static void TeleportSuppressedByMovement()
        {
            var h = new Harness();
            h.ReadOverride = () => h.Sample(h.Ms >= 500 ? 11 : 10);
            h.Run();
            Assert(h.Sends == 1 && h.Teleports == 0 && h.Ms == 500,
                "Teleport was sent or the 10-second wait continued after verified movement.");
        }
        private static void MovementAfterTeleport()
        {
            var h = new Harness();
            h.ReadOverride = () => h.Sample(h.Teleports > 0 ? 11 : 10);
            h.Run();
            Assert(h.Sends == 1 && h.Teleports == 1 && h.Ms == 10000,
                "Recovery did not stop immediately when teleport produced verified X/Y movement.");
        }
        private static void RecoveryConstants()
        {
            Assert(VanillaAutobattleResumeVerifier.PostLoginSettleMs == 10000, "Restart-only post-login settle must be ten seconds.");
            Assert(VanillaAutobattleResumeVerifier.MaximumAttempts == 3, "Recovery-cycle input budget changed unexpectedly.");
            Assert(VanillaAutobattleResumeVerifier.ObservationWindowMs == 10000, "Movement verification window changed unexpectedly.");
            Assert(VanillaAutobattleResumeVerifier.RecoveryDeadlineMs == 180000, "Stationary recovery must escalate to restart at 180 seconds.");
            Assert(VanillaRecoveryPolicy.RetryDelayMs(1, 30000, 3600000) == 30000
                && VanillaRecoveryPolicy.RetryDelayMs(8, 30000, 3600000) == 3600000
                && VanillaRecoveryPolicy.RetryDelayMs(20, 30000, 3600000) == 3600000,
                "Recovery backoff must continue and cap at one hour rather than a finite restart budget.");
        }
        private static void NoRestart()
        {
            var h = new Harness();
            Expect<InvalidOperationException>(h.Run, "no verified X/Y movement");
            Expect<InvalidOperationException>(h.Run, "cannot be restarted");
            Assert(h.Sends == 3 && h.Teleports == 3, "Recovery input budget was reset.");
        }
        private static void MovementAndReturn()
        {
            var h = new Harness();
            h.ReadOverride = () => h.Sample(h.Ms == 200 ? 11 : 10);
            h.Run();
            Assert(h.Verifier.MovementVerified && h.Ms == 200 && h.Sends == 1, "Intermediate movement was missed.");
        }
        private static void DeadlineMovement()
        {
            var h = new Harness(); h.ReadOverride = () => h.Sample(h.Ms >= 10000 ? 11 : 10);
            h.Run(); Assert(h.Sends == 1 && h.Teleports == 0 && h.Ms == 10000, "Deadline movement caused a teleport or another toggle.");
        }
        private static void RefocusMovement()
        {
            var h = new Harness(); h.ReadOverride = () => h.Sample(h.Focuses >= 2 ? 11 : 10);
            h.Run(); Assert(h.Focuses == 2 && h.Sends == 1 && h.Teleports == 1 && h.Ms == 20000, "Movement after refocus did not prevent a second toggle.");
        }
        private sealed class StopHarness
        {
            internal int Pid = 42, Sends, Focuses;
            internal long Ms;
            internal bool Cancelled;
            internal readonly Guid Session = Guid.NewGuid();
            internal readonly VanillaAutobattleStopVerifier Verifier = new VanillaAutobattleStopVerifier();
            internal readonly List<long> SentAt = new List<long>();
            internal readonly List<string> Progress = new List<string>();
            internal Func<VanillaClientState> ReadOverride;
            internal Action OnDelay, OnFocus;
            internal DateTimeOffset Now { get { return Epoch.AddMilliseconds(Ms); } }

            internal VanillaClientState Sample(int x = 10, int y = 20, uint hp = 100, uint maxHp = 100)
            {
                var state = VanillaClientState.Create(Session, Now, null, new Dictionary<VanillaField, object>
                {
                    { VanillaField.X, x }, { VanillaField.Y, y }, { VanillaField.Map, "map" },
                    { VanillaField.CharacterName, "fake character" }, { VanillaField.CurrentHP, hp }, { VanillaField.MaxHP, maxHp }
                }, null, null);
                state.ProcessId = Pid;
                foreach (var field in state.Fields.Values.Where(v => v.IsAvailable))
                    field.Validation = StateValidation.Valid;
                return state;
            }

            internal Task<bool> RunAsync()
            {
                return Verifier.VerifyAsync(Pid, () => ReadOverride == null ? Sample() : ReadOverride(),
                    () => { Focuses++; OnFocus?.Invoke(); },
                    () => { Sends++; SentAt.Add(Ms); },
                    () => Cancelled, () => TimeSpan.FromMilliseconds(Ms), () => Now,
                    milliseconds => { Ms += milliseconds; OnDelay?.Invoke(); return Task.FromResult(0); }, Progress.Add);
            }

            internal bool Run() { return RunAsync().GetAwaiter().GetResult(); }
        }

        private static void StopStationaryWindow()
        {
            var h = new StopHarness();
            Assert(h.Run(), "Stationary client was not accepted as stopped.");
            Assert(h.Verifier.StationaryVerified && h.Sends == 1 && h.Ms == 5000,
                "STOP must require exactly one full five-second stationary window before Cart input.");
            Assert(h.SentAt.SequenceEqual(new long[] { 0 }), "Stationary STOP sent an unnecessary retry.");
        }

        private static void StopMovementResetsWindow()
        {
            var h = new StopHarness();
            h.ReadOverride = () =>
            {
                int x = h.Ms < 3000 ? 10 + (int)(h.Ms / 500) : 20;
                return h.Sample(x, 20);
            };
            Assert(h.Run(), "Client that became stationary within the attempt window was rejected.");
            Assert(h.Sends == 1 && h.Ms >= 7500 && h.Ms <= 8000,
                "X/Y movement did not reset the required continuous five-second stillness window.");
            Assert(h.Progress.Any(p => p.IndexOf("stationary window reset", StringComparison.OrdinalIgnoreCase) >= 0),
                "STOP verifier did not report movement/reset evidence.");
        }

        private static void StopRetryCadence()
        {
            var h = new StopHarness();
            h.ReadOverride = () =>
            {
                int x = h.Ms < 10000 ? 10 + (int)(h.Ms / 100) : 110;
                return h.Sample(x, 20);
            };
            Assert(h.Run(), "Second STOP attempt did not succeed after movement ceased.");
            Assert(h.Sends == 2 && h.SentAt.SequenceEqual(new long[] { 0, 10000 }) && h.Ms == 15000,
                "STOP retries must occur on a ten-second cadence and still require five stationary seconds.");
        }

        private static void StopHpDamageAbort()
        {
            var h = new StopHarness();
            h.ReadOverride = () => h.Sample(10, 20, h.Ms >= 2000 ? 89U : 100U, 100U);
            Assert(!h.Run(), "HP danger incorrectly authorized Cart input.");
            Assert(h.Verifier.HpDamageDetected && !h.Verifier.StationaryVerified && h.Sends == 1 && h.Ms == 2000,
                "HP >10% damage must abort STOP verification immediately without another STOP attempt.");
            Assert(h.Progress.Any(p => p.IndexOf("HP dropped by more than", StringComparison.OrdinalIgnoreCase) >= 0),
                "HP damage abort was not reported.");
        }

        private static void StopHpCumulativeAcrossRetries()
        {
            var h = new StopHarness();
            h.ReadOverride = () =>
            {
                uint hp = h.Ms >= 10100 ? 89U : h.Ms >= 5000 ? 94U : 100U;
                return h.Sample(10 + (int)(h.Ms / 100), 20, hp, 100U);
            };
            Assert(!h.Run(), "Cumulative HP loss across STOP retries was ignored.");
            Assert(h.Verifier.HpDamageDetected && h.Verifier.HpBaselinePercent == 100m
                && h.Sends == 2 && h.Ms == 10100,
                "STOP retries must retain the original HP baseline rather than re-baselining after each hotkey.");
        }

        private static void StopHpExactThreshold()
        {
            var h = new StopHarness();
            h.ReadOverride = () => h.Sample(10, 20, h.Ms >= 1000 ? 90U : 100U, 100U);
            Assert(h.Run(), "Exactly ten percentage points of HP loss should not cross the >10% abort threshold.");
            Assert(!h.Verifier.HpDamageDetected && h.Verifier.StationaryVerified && h.Sends == 1 && h.Ms == 5000,
                "Exact threshold handling changed unexpectedly.");
        }

        private static void StopBoundedFailure()
        {
            var h = new StopHarness();
            h.ReadOverride = () => h.Sample(10 + (int)(h.Ms / 100), 20);
            Assert(!h.Run(), "Continuously moving client was incorrectly authorized for Cart input.");
            Assert(!h.Verifier.StationaryVerified && h.Sends == 3 && h.Ms == 30000,
                "STOP verification must end after three ten-second moving attempts.");
            Assert(h.SentAt.SequenceEqual(new long[] { 0, 10000, 20000 }),
                "STOP attempts were not sent at 0/10/20 seconds.");
            Assert(h.Progress.Last().IndexOf("Cart/Inventory input is not authorized", StringComparison.Ordinal) >= 0,
                "Failed STOP verification did not explicitly deny Cart/Inventory input.");
        }

        private static void StopVerificationConstants()
        {
            Assert(VanillaAutobattleStopVerifier.MaximumAttempts == 3,
                "STOP verification retry budget changed unexpectedly.");
            Assert(VanillaAutobattleStopVerifier.AttemptWindowMs == 10000,
                "STOP attempts must use ten-second windows.");
            Assert(VanillaAutobattleStopVerifier.RequiredStationaryMs == 5000,
                "STOP verification must require five continuous stationary seconds.");
            Assert(VanillaAutobattleStopVerifier.PollIntervalMs <= 100,
                "STOP X/Y polling became too coarse.");
            Assert(VanillaAutobattleStopVerifier.HpDamageAbortPercent == 10m,
                "STOP HP damage threshold changed unexpectedly.");
        }

        private static void CancelBeforeStart()
        {
            var h = new Harness { Cancelled = true }; Expect<OperationCanceledException>(h.Run);
            Assert(h.Sends == 0 && h.Focuses == 0, "Cancelled startup touched input.");
        }
        private static void CancelDuringWait()
        {
            var h = new Harness(); h.OnDelay = () => h.Cancelled = true;
            Expect<OperationCanceledException>(h.Run); Assert(h.Sends == 1 && h.Ms == 100, "Cancellation was delayed or retried.");
        }
        private static void CancelDuringFocus()
        {
            var h = new Harness(); h.OnFocus = () => h.Cancelled = true;
            Expect<OperationCanceledException>(h.Run); Assert(h.Sends == 0, "Key leaked after focus cancellation.");
        }
        private static void CancelDuringRead()
        {
            var h = new Harness(); h.ReadOverride = () => { h.Cancelled = true; return h.Sample(); };
            Expect<OperationCanceledException>(h.Run); Assert(h.Sends == 0, "Key leaked after read cancellation.");
        }
        private static void FocusFailure()
        {
            var h = new Harness(); h.OnFocus = () => { throw new InvalidOperationException("focus failed"); };
            Expect<InvalidOperationException>(h.Run, "focus failed"); Assert(h.Sends == 0, "Sent key without focus.");
        }
        private static void RetryFocusFailure()
        {
            var h = new Harness(); h.OnFocus = () => { if (h.Focuses == 2) throw new InvalidOperationException("focus failed"); };
            Expect<InvalidOperationException>(h.Run, "focus failed"); Assert(h.Sends == 1, "Failed focus sent a retry.");
        }
        private static void UnavailableCoordinates()
        {
            var h = new Harness(); h.ReadOverride = () => { var s = h.Sample(0, 0); s.X.IsAvailable = false; return s; };
            Expect<InvalidOperationException>(h.Run, "verified X"); Assert(h.Sends == 0, "Unknown became zero.");
        }
        private static void UnverifiedCoordinates()
        {
            var h = new Harness(); h.ReadOverride = () => { var s = h.Sample(); s.Y.Validation = StateValidation.Unverified; return s; };
            Expect<InvalidOperationException>(h.Run, "verified Y"); Assert(h.Sends == 0, "Unverified position authorized input.");
        }
        private static void ValidZero()
        {
            var h = new Harness(); h.ReadOverride = () => h.Sample(h.Sends > 0 ? 1 : 0, 0);
            h.Run(); Assert(h.Sends == 1 && h.Verifier.MovementVerified, "Real zero coordinates were rejected.");
        }
        private static void StaleCoordinates()
        {
            var h = new Harness(); h.ReadOverride = () => { var s = h.Sample(); s.SampledAtUtc = h.Now.AddSeconds(-2); return s; };
            Expect<InvalidOperationException>(h.Run, "stale"); Assert(h.Sends == 0, "Old sample authorized input.");
        }
        private static void CachedSnapshot()
        {
            var h = new Harness(); var cached = h.Sample(); h.ReadOverride = () => cached;
            Expect<InvalidOperationException>(h.Run, "stale"); Assert(h.Sends == 1, "Cached snapshot allowed retries.");
        }
        private static void SlowRead()
        {
            var h = new Harness(); h.ReadOverride = () => { var s = h.Sample(); h.Ms += 2000; return s; };
            Expect<InvalidOperationException>(h.Run, "stale"); Assert(h.Sends == 0, "Slow observation authorized input.");
        }
        private static void Changed(Action<VanillaClientState> change)
        {
            var h = new Harness(); h.ReadOverride = () => { var s = h.Sample(h.Ms >= 100 ? 11 : 10); if (h.Ms >= 100) change(s); return s; };
            Expect<InvalidOperationException>(h.Run); Assert(h.Sends == 1 && !h.Verifier.MovementVerified, "Changed session was movement evidence.");
        }
        private static void Set<T>(VanillaClientState state, VanillaField field, T value)
        {
            var replacement = new StateValue<T>(value) { IsAvailable = true, Validation = StateValidation.Valid, LastObservedAtUtc = state.SampledAtUtc };
            var fields = state.Fields.ToDictionary(p => p.Key, p => p.Value); fields[field] = replacement; state.Fields = fields;
        }
        private static void ReadFailure()
        {
            var h = new Harness(); int reads = 0;
            h.ReadOverride = () => { reads++; if (h.Ms >= 100) throw new InvalidOperationException("read denied"); return h.Sample(); };
            Expect<InvalidOperationException>(h.Run, "read denied"); Assert(h.Sends == 1 && reads == 3, "Read failure was retried.");
        }
        private static void WallClockChange()
        {
            var h = new Harness(); h.OnDelay = () => h.UtcOffset = h.Ms < 15000 ? -3600000 : 3600000;
            Expect<InvalidOperationException>(h.Run, "no verified X/Y movement"); Assert(h.Ms == 180000 && h.Sends == 3 && h.Teleports == 3, "Wall clock changed recovery deadlines.");
        }
        private static void IndependentClients()
        {
            var a = new Harness(); var b = new Harness { Pid = 43 };
            a.ReadOverride = () => a.Sends > 0 ? b.Sample(11) : a.Sample();
            Expect<InvalidOperationException>(a.Run, "different client");
            b.ReadOverride = () => b.Sample(b.Sends > 0 ? 11 : 10); b.Run();
            Assert(!a.Verifier.MovementVerified && b.Verifier.MovementVerified && a.Sends == 1 && b.Sends == 1, "Client state was shared.");
        }
        private static void StartupGate()
        {
            for (int flags = 0; flags < 16; flags++)
            {
                bool game = (flags & 1) != 0, moved = (flags & 2) != 0, minimized = (flags & 4) != 0, error = (flags & 8) != 0;
                Assert(VanillaReconnectSupervisor.SequentialStartupMayAdvance(game, moved, minimized, error)
                    == (game && moved && minimized && !error), "Startup released an incomplete client.");
            }
            Assert(VanillaRecoveryPolicy.BlocksParallelRecovery(true, true), "Verification did not retain the recovery lease.");
        }
        private static void MinimizeGuard()
        {
            Assert(!VanillaReconnectSupervisor.ShouldKeepClientMinimized(VanillaReconnectStage.VerifyingAutobattle, VanillaVisualState.Gameplay), "Verifier was minimized.");
            Assert(!VanillaReconnectSupervisor.ShouldKeepClientMinimized(VanillaReconnectStage.Error, VanillaVisualState.Gameplay), "Failed client was treated as healthy.");
        }
        private static void ChordSuccess()
        {
            var events = new List<string>(); int checks = 0;
            VanillaForegroundInput.DispatchGuardedChord(true, true, true, Keys.D2, () => checks++,
                (key, up) => events.Add(key + (up ? " up" : " down")), _ => { });
            Assert(checks == 4 && events.SequenceEqual(new[] { "ControlKey down", "Menu down", "ShiftKey down", "D2 down",
                "D2 up", "ShiftKey up", "Menu up", "ControlKey up" }), "Incomplete/unguarded chord.");
        }
        private static void ChordCancellation()
        {
            var events = new List<string>(); bool cancelled = false;
            Expect<OperationCanceledException>(() => VanillaForegroundInput.DispatchGuardedChord(true, false, false, Keys.D2,
                () => { if (cancelled) throw new OperationCanceledException(); },
                (key, up) => events.Add(key + (up ? " up" : " down")), _ => cancelled = true));
            Assert(events.SequenceEqual(new[] { "ControlKey down", "ControlKey up" }), "Main key leaked or modifier stayed held.");
        }
        private static void ChordFailure()
        {
            var released = new List<Keys>();
            Expect<InvalidOperationException>(() => VanillaForegroundInput.DispatchGuardedChord(true, true, false, Keys.D2,
                () => { }, (key, up) => { if (up) released.Add(key); else if (key == Keys.D2) throw new InvalidOperationException("input rejected"); }, _ => { }));
            Assert(released.SequenceEqual(new[] { Keys.Menu, Keys.ControlKey }), "Input failure stranded modifiers.");
        }
        private static void ChordReleaseFailure()
        {
            var released = new List<Keys>();
            Expect<InvalidOperationException>(() => VanillaForegroundInput.DispatchGuardedChord(true, true, false, Keys.D2,
                () => { }, (key, up) => { if (up) { released.Add(key); if (key == Keys.D2) throw new InvalidOperationException("release failed"); } }, _ => { }));
            Assert(released.SequenceEqual(new[] { Keys.D2, Keys.Menu, Keys.ControlKey }), "Cleanup stopped before other key releases.");
        }
        private static void Expect<T>(Action action, string text = null) where T : Exception
        {
            try { action(); }
            catch (T ex) { Assert(text == null || ex.Message.Contains(text), "Unexpected error: " + ex.Message); return; }
            throw new Exception("Expected " + typeof(T).Name);
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
    }
}
