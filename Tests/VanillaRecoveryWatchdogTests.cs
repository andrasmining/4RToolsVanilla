using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaRecoveryWatchdogTests
    {
        private static int passed, failed;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly Guid Session = Guid.NewGuid();
        private const BindingFlags Flags = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
        internal static int Run()
        {
            Test("Health diagnostics retain a 30s heartbeat without per-poll noise", DiagnosticHeartbeat);
            Test("Flapping health diagnostics are bounded and process replacement is reported", DiagnosticTransitions);
            Test("Unchanged coordinates expire at exactly 30 seconds", Deadline);
            Test("X-only movement resets the deadline", () => Axis(true));
            Test("Y-only movement resets the deadline", () => Axis(false));
            Test("Zero is a valid coordinate, not an unavailable reading", Zero);
            Test("Missing coordinates expire without a popup", Missing);
            Test("Unreadable coordinates expire without inventing zero", Invalid);
            Test("Unverified coordinates cannot reset the watchdog", Unverified);
            Test("First late baseline is not movement", LateBaseline);
            Test("Stale and future readings cannot establish movement", Stale);
            Test("Repeated timestamps cannot authorize movement", Cached);
            Test("Intermediate movement survives a return to the same position", Intermediate);
            Test("First observed intermediate movement resets the deadline", FirstIntermediate);
            Test("Fleet tracks intermediate movement without a readiness field", FleetMovement);
            Test("Fleet does not infer motion across an invalid observation", FleetInvalidMovement);
            Test("Map and session replacement start new baselines", Context);
            Test("Unknown map flapping cannot hide stationary coordinates", UnknownMap);
            Test("Process replacement resets the deadline", ProcessChange);
            Test("Monotonic rollback resets safely", ClockRollback);
            Test("Explicit watchdog reset clears the old deadline", Reset);
            Test("Another client's movement cannot satisfy this client", Isolation);
            Test("Healthy sibling remains untouched by a position restart", OneClient);
            Test("Unavailable coordinates restart at the configured three-minute default", UnreadableRestart);
            Test("No-movement restart threshold is configurable", ConfigurableRestartThreshold);
            Test("Both positional failures restart sequentially through the full lease", BothPosition);
            Test("Mixed dialog and position failures use one shared recovery lease", Mixed);
            Test("Movement while queued cancels the stale restart decision", QueuedMovement);
            Test("STOP cancels queued close and prevents late completion", Stop);
            Test("Settings changes cancel queued close", Settings);
            Test("STOP at the owned close boundary sends no close", StopAtBoundary);
            Test("Replacement PID cannot be closed by an old worker", ReplacedPid);
            Test("Replacement operation cannot be closed by an old worker", ReplacedOperation);
            Test("Close failure retains the PID and exponential backoff", CloseFailure);
            Test("Recovery OFF disables position restarts", RecoveryOff);
            Test("Visual watchdog OFF does not disable the position watchdog", VisualOff);
            Test("Stalled unreadable health queues foreground terminal diagnosis with legacy visual OFF", HealthTerminalDiagnosis);
            Test("Healthy movement avoids forced diagnosis when visual monitoring is OFF", HealthyVisualOff);
            Test("Capture failure cannot bypass the unavailable-health restart deadline", FailedVisualDiagnosis);
            Test("Online capture failures never rearm login from an old launch", OnlineCaptureFailure);
            Test("An adopted waiting stage cannot grant login ownership", AdoptedWaitingStage);
            Test("Replacement capture failure still reaches bounded login readiness", ReplacementCaptureFailure);
            Test("Failed replacement with retained PID retries after backoff under one lease", RetainedReplacementRetry);
            Test("Retained replacement retry respects previously diagnosed unknown modals", RetainedReplacementModal);
            Test("An adopted client failure cannot arm a replacement retry", RetainedReplacementAdopted);
            Test("STOP cancels retained replacement retry evidence and queued close", RetainedReplacementStop);
            Test("Settings cancel retained replacement retry evidence and queued close", RetainedReplacementSettings);
            Test("Retained replacement retry rejects recycled process identity", RetainedReplacementIdentity);
            Test("Retained replacement retries remain serialized behind a sibling", RetainedReplacementSibling);
            Test("Retained replacement close failure preserves capped retry progression", RetainedReplacementCloseFailure);
            Test("Samples produced during memory reads are fresh rather than future data", CaptureClock);
            Test("Unknown modal blocks a reached movement restart deadline", ModalBeforeRestart);
            Test("Unavailable or ambiguous captures cannot clear a known modal", ModalCaptureGap);
            Test("Failed capture breaks terminal confirmation continuity", FailedTerminalConfirmation);
            Test("STOP during visual diagnosis withholds recovery", CancelVisualDiagnosis);
            Test("Timed out native captures are bounded and late frames discarded", BoundedVisualDiagnosis);
            Test("Minimized windows stay minimized and never render background screenshots", MinimizedTerminalCapture);
            Test("Startup and recovery do not accrue movement timeouts", StartupGrace);
            Test("Automatic minimization requires 60s visible and 60s cursor idle", MinimizeGrace);
            Test("Verified recovery movement bypasses the 60s minimize grace", VerifiedMovementMinimize);
            Test("Steady-state stillness waits for the restart threshold and sends no autobattle hotkey", WatchdogHotkeyFirst);
            Test("Recovery backoff continues beyond three failures and caps at one hour", RestartBudget);
            Test("Verified restart recovery resets exponential backoff", RestartBudgetReset);
            Test("Manual Cart hold survives unrelated recovery settings apply", WeightHoldSurvivesApply);
            Test("Cancelled Cart retains its hold after the active runtime is removed", WeightHoldSurvivesRuntimeRemoval);
            Test("Clearing Cart holds leaves unrelated error states untouched", WeightHoldClearIsolation);
            Test("Manual character actions require a fresh exact username + character identity", ManualActionIdentityGate);
            Test("One terminal observation does not close a client", OneTerminal);
            Test("Unknown modal is never dismissed or closed as a disconnect", UnknownModal);
            Test("Changing terminal messages need new confirmation", ChangedTerminal);
            Test("Stale terminal observations need new confirmation", StaleTerminal);
            for (int a = 0; a < 2; a++) for (int b = 0; b < 2; b++)
            { int aa = a, bb = b; Test("Both terminal dialogs are serialized " + a + "/" + b, () => BothTerminal(aa, bb)); }
            Test("Terminal dialog present before START is closed under the startup gate", ColdStart);
            Test("START confirms terminal dialogs with continuous visual monitoring OFF", ColdStartVisualOff);
            Test("Cold-start changed confirmation sends no close", ColdStartChanged);
            Test("Normal close confirms exit without termination", NormalClose);
            Test("Unresponsive close has one bounded termination attempt", ForcedClose);
            Test("Failed process exit is not successful recovery", CloseTimeout);
            Test("STOP during close wait prevents termination", StopDuringClose);
            Test("Unknown exit observation cannot become successful exit", UnknownExit);
            Test("Close permission denial does not try another access path", CloseDenied);
            Test("Emergency close terminates immediately without WM_CLOSE or graceful wait", EmergencyClose);
            Test("Emergency close skips an already exited client", EmergencyAlreadyExited);
            Test("Cancelled emergency close never terminates", EmergencyCancelled);
            Test("Emergency cancellation after exit query prevents termination", EmergencyCancelledAtBoundary);
            Test("Emergency cancellation during exit verification stops waiting", EmergencyCancelledDuringWait);
            Test("Denied emergency termination is a failure without retries", EmergencyDenied);
            Test("Emergency exit timeout is bounded and remains a failure", EmergencyExitTimeout);
            Test("Unknown emergency exit state never authorizes termination", EmergencyUnknownExit);
            Test("Unknown exit after emergency termination cannot confirm success", EmergencyUnknownExitAfterTermination);
            Test("Process close requests no memory-write or injection rights", CloseRights);
            Test("Position cache clears only the confirmed exited client's reader", FleetCache);
            foreach (string name in new[] { "LoggingOut", "Disconnected" })
                foreach (float scale in new[] { .8f, 1f, 1.25f, 1.5f, 1.75f, 2f })
                { string n = name; float s = scale; Test("Terminal image " + n + " scale " + s, () => ImageCase(n, s)); }
            Test("Uniform captures are unknown rather than gameplay", BlankImage);
            Test("Wrong or partial popup text is not a terminal match", WrongImage);
            Console.WriteLine("Recovery watchdog: {0} passed; {1} failed. Fake time/processes and cropped dialog images only.", passed, failed);
            return failed;
        }
        private static VanillaPositionSample S(double sec, int x = 10, int y = 20, int pid = 101, Guid? session = null,
            string map = "map", bool verified = true, string error = null, double? moved = null)
        { return new VanillaPositionSample(pid, session ?? Session, Epoch.AddSeconds(sec), x, y, map, verified, error, moved.HasValue ? (DateTimeOffset?)Epoch.AddSeconds(moved.Value) : null); }
        private static string Check(VanillaMovementWatchdog w, double sec, VanillaPositionSample s, int pid = 101)
        { return w.Observe(pid, s, TimeSpan.FromSeconds(sec), Epoch.AddSeconds(sec)); }
        private static void DiagnosticHeartbeat()
        {
            var rate = new VanillaMovementDiagnosticRate();
            Assert(rate.ShouldWrite(101, "observing", TimeSpan.Zero));
            for (int second = 1; second < 30; second++) Assert(!rate.ShouldWrite(101, "observing", TimeSpan.FromSeconds(second)));
            Assert(rate.ShouldWrite(101, "observing", TimeSpan.FromSeconds(30)));
            Assert(!rate.ShouldWrite(101, "observing", TimeSpan.FromSeconds(59.999)));
            Assert(rate.ShouldWrite(101, "observing", TimeSpan.FromSeconds(60)));
        }
        private static void DiagnosticTransitions()
        {
            var rate = new VanillaMovementDiagnosticRate();
            Assert(rate.ShouldWrite(101, "unavailable", TimeSpan.Zero));
            for (int tenth = 1; tenth < 50; tenth++)
                Assert(!rate.ShouldWrite(101, tenth % 2 == 0 ? "unavailable" : "observing", TimeSpan.FromSeconds(tenth / 10.0)));
            Assert(rate.ShouldWrite(101, "observing", TimeSpan.FromSeconds(5)));
            Assert(!rate.ShouldWrite(101, "unavailable", TimeSpan.FromSeconds(9.999)));
            Assert(rate.ShouldWrite(101, "unavailable", TimeSpan.FromSeconds(10)));
            Assert(rate.ShouldWrite(202, "unavailable", TimeSpan.FromSeconds(10.1)));
            Assert(rate.ShouldWrite(202, "unavailable", TimeSpan.FromSeconds(1)));
        }
        private static void Deadline()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Assert(Check(w, 29.999, S(29.999)) == null); Assert(Check(w, 30, S(30)) != null); }
        private static void Axis(bool x)
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Check(w, 29, S(29, x ? 11 : 10, x ? 20 : 21)); Assert(Check(w, 58, S(58, x ? 11 : 10, x ? 20 : 21)) == null); Assert(Check(w, 59, S(59, x ? 11 : 10, x ? 20 : 21)) != null); }
        private static void Zero()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0, 0, 0)); Assert(Check(w, 29, S(29, 0, 1)) == null); Assert(Check(w, 30, S(30, 0, 1)) == null); }
        private static void Missing()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, null); Assert(Check(w, 29, null) == null); Assert(Check(w, 30, null).Contains("unavailable")); }
        private static void Invalid()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Check(w, 20, new VanillaPositionSample(101, Session, Epoch.AddSeconds(20), null, null, null, false, "read failed")); Assert(Check(w, 30, S(30)) != null); }
        private static void Unverified()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Assert(Check(w, 30, S(30, 99, 99, verified: false)) != null); }
        private static void LateBaseline()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, null); Check(w, 29, S(29)); Assert(Check(w, 30, S(30)) != null); }
        private static void Stale()
        { foreach (double stamp in new[] { 1.0, 31.0 }) { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Assert(Check(w, 30, S(stamp, 99, 99)) != null); } }
        private static void Cached()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Check(w, 1, S(0, 99, 99)); Assert(Check(w, 30, S(30)) != null); }
        private static void Intermediate()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0, moved: 0)); Check(w, 20, S(20, moved: 19)); Assert(Check(w, 30, S(30, moved: 19)) == null); Assert(Check(w, 50, S(50, moved: 19)) != null); }
        private static void FirstIntermediate()
        {
            var w = new VanillaMovementWatchdog(); Check(w, 0, S(0));
            Check(w, 1, S(1, moved: .5));
            Assert(Check(w, 30, S(30, moved: .5)) == null, "First intermediate motion was lost.");
            Assert(Check(w, 31, S(31, moved: .5)) != null, "Motion kept extending without another move.");
        }
        private static void FleetMovement()
        {
            var reader = new Reader { Pid = 101 };
            using (var fleet = new VanillaFleetMonitor(Path.GetTempPath(),
                () => new VanillaFleetMonitor.IProcessMetadata[] { new Meta { ProcessId = 101 } }, pid => reader, () => null))
            {
                fleet.Poll(); var w = new VanillaMovementWatchdog(); Check(w, 0, fleet.LatestPosition(101));
                reader.Frame = S(.5, 11); fleet.Poll();
                reader.Frame = S(1, 10); fleet.Poll();
                var returned = fleet.LatestPosition(101);
                Assert(returned.MovementAt.HasValue && returned.MovementAt.Value == Epoch.AddSeconds(1),
                    "Verified X/Y motion depended on unavailable ClientReady or was lost between polls.");
                Check(w, 1, returned);
                Assert(Check(w, 30, S(30, moved: 1)) == null, "Healthy movement-and-return caused recovery.");
            }
        }
        private static void FleetInvalidMovement()
        {
            var reader = new Reader { Pid = 101 };
            using (var fleet = new VanillaFleetMonitor(Path.GetTempPath(),
                () => new VanillaFleetMonitor.IProcessMetadata[] { new Meta { ProcessId = 101 } }, pid => reader, () => null))
            {
                fleet.Poll(); reader.Frame = S(.5, 11, verified: false); fleet.Poll();
                reader.Frame = S(1, 10); fleet.Poll();
                Assert(!fleet.LatestPosition(101).MovementAt.HasValue, "Unverified position fabricated motion.");
            }
        }
        private static void Context()
        { foreach (bool newSession in new[] { false, true }) { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Guid g = newSession ? Guid.NewGuid() : Session; string m = newSession ? "map" : "map2"; Check(w, 29, S(29, session: g, map: m)); Assert(Check(w, 30, S(30, session: g, map: m)) == null); } }
        private static void UnknownMap()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Check(w, 15, S(15, map: null)); Check(w, 29, S(29)); Assert(Check(w, 30, S(30)) != null); }
        private static void ProcessChange()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Assert(Check(w, 30, S(30, pid: 102), 102) == null); }
        private static void ClockRollback()
        { var w = new VanillaMovementWatchdog(); Check(w, 50, S(50)); Assert(Check(w, 1, S(1)) == null); }
        private static void Reset()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); w.Reset(); Assert(Check(w, 30, S(30)) == null); }
        private static void Isolation()
        { var w = new VanillaMovementWatchdog(); Check(w, 0, S(0)); Assert(Check(w, 30, S(30, 99, 99, 102)) != null); }

        private sealed class Env : IVanillaRecoveryRestartEnvironment
        {
            internal double Seconds;
            internal bool FailClose;
            internal Action BeforeClose;
            internal int StartShiftSeconds;
            internal readonly Queue<Action> Work = new Queue<Action>();
            internal readonly List<int> Closed = new List<int>();
            public DateTimeOffset UtcNow { get { return Epoch.AddSeconds(Seconds); } }
            public TimeSpan MonotonicNow { get { return TimeSpan.FromSeconds(Seconds); } }
            public DateTime GetStartTimeUtc(int pid) { return Epoch.UtcDateTime.AddSeconds(StartShiftSeconds); }
            public void Queue(Action work) { Work.Enqueue(work); }
            public void CloseClient(int pid, DateTime expected, Func<bool> cancelled, Action<Action> owned, bool immediate = false)
            { BeforeClose?.Invoke(); if (cancelled()) throw new OperationCanceledException(); if (FailClose) throw new InvalidOperationException("denied"); owned(() => { if (cancelled()) throw new OperationCanceledException(); Closed.Add(pid); }); }
        }
        private sealed class H : IDisposable
        {
            internal readonly Env E = new Env();
            internal readonly VanillaReconnectSupervisor Supervisor;
            internal readonly object A, B;
            internal readonly Dictionary<int, VanillaPositionSample> Samples = new Dictionary<int, VanillaPositionSample>();
            internal readonly List<int> Forgotten = new List<int>();
            internal readonly List<string> Wakeups = new List<string>();
            internal readonly Dictionary<int, VanillaRecoveryVisualObservation> Visuals = new Dictionary<int, VanillaRecoveryVisualObservation>();
            internal int VisualReads;
            internal Action<int> OnVisualRead;
            private readonly string root = Path.Combine(Path.GetTempPath(), "4R-watchdog-" + Guid.NewGuid().ToString("N"));
            internal H()
            {
                Supervisor = new VanillaReconnectSupervisor(root, E);
                var map = (IDictionary)Get(Supervisor, "runtimes");
                A = map[Supervisor.Settings.Accounts[0].Id]; B = map[Supervisor.Settings.Accounts[1].Id];
                foreach (var pair in new[] { Tuple.Create(A, 101), Tuple.Create(B, 102) })
                { Set(pair.Item1, "ProcessId", (int?)pair.Item2); Set(pair.Item1, "ResumeSent", true); Set(pair.Item1, "Stage", VanillaReconnectStage.Online); }
                Set(Supervisor, "running", true); // No live Start(), timer or process enumeration.
                Supervisor.SetPositionSource(pid => Samples.ContainsKey(pid) ? Samples[pid] : null, Forgotten.Add);
                Supervisor.SetAutobattleResumeTestHook((id, reason, movement) => Wakeups.Add(id + "|" + reason + "|" + movement));
            }
            internal void EnableForegroundDiagnosis()
            { Supervisor.RecoveryScreenDiagnosisFactory = (pid, created, cancelled) => new ForegroundScreen(this, pid); }
            private sealed class ForegroundScreen : IVanillaRecoveryScreenSession
            {
                private readonly H owner;
                private readonly int pid;
                internal ForegroundScreen(H owner, int pid) { this.owner = owner; this.pid = pid; }
                public bool WasMinimized { get { return true; } }
                public VanillaRecoveryScreenFrame Capture()
                {
                    owner.VisualReads++; owner.E.Seconds += .15;
                    owner.OnVisualRead?.Invoke(pid);
                    var visual = owner.Visuals.ContainsKey(pid) ? owner.Visuals[pid]
                        : new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, true, null);
                    if (visual.Error != null || !visual.WindowAvailable) throw new InvalidOperationException(visual.Error ?? "Foreground capture unavailable");
                    return new VanillaRecoveryScreenFrame { State = visual.State, At = owner.E.UtcNow,
                        Proof = new VanillaVisualInputProof(pid, new IntPtr(pid + 500), new Size(640, 480), new Point(20, 30), owner.VisualReads) };
                }
                public void RestoreMinimizedState(Action<Action> ownedStep) { ownedStep(() => { }); }
                public void Dispose() { }
            }
            internal void Sample(int pid, int x = 10) { Samples[pid] = S(E.Seconds, x, pid: pid); }
            internal bool Motion(object runtime)
            { return (bool)Call(Supervisor, "CheckMovementWatchdog", runtime, E.UtcNow, (Func<DateTime>)(() => Epoch.UtcDateTime)); }
            internal void Terminal(object runtime, int kind = 0)
            {
                var state = kind == 0 ? VanillaVisualState.LoggingOut : VanillaVisualState.Disconnected;
                int pid = ((int?)Get(runtime, "ProcessId")).Value;
                Visuals[pid] = new VanillaRecoveryVisualObservation(state, true, null);
                Call(Supervisor, "HandleTerminalVisual", runtime, state, E.UtcNow, (Func<DateTime>)(() => Epoch.UtcDateTime));
            }
            internal void Probe(object runtime) { Call(Supervisor, "Probe", runtime, E.UtcNow); }
            internal void ArmFiveMinuteStall(object runtime, int pid)
            { Sample(pid); Motion(runtime); E.Seconds = Math.Max(E.Seconds, 300); Sample(pid); Motion(runtime); }
            internal void FinishFirst()
            {
                Call(Supervisor, "Bind", A, 201, true, "fake new client");
                Set(A, "ScriptRunning", true);
                Motion(B); Assert(E.Work.Count == 0, "Second close while first logs in.");
                Set(A, "Stage", VanillaReconnectStage.VerifyingAutobattle);
                Motion(B); Assert(E.Work.Count == 0, "Second close before verified movement/minimization.");
                Set(A, "ScriptRunning", false); Set(A, "ResumeSent", true); Set(A, "HasBeenOnline", true);
                Call(Supervisor, "CompleteAutobattleRecoverySuccessLocked", A);
            }
            public void Dispose() { Supervisor.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static void OneClient()
        {
            using (var h = new H())
            {
                h.Sample(101); h.Motion(h.A);
                h.E.Seconds = 179; h.Sample(101);
                Assert(!h.Motion(h.A) && h.Wakeups.Count == 0 && h.E.Work.Count == 0,
                    "Client restarted before the 180-second default.");
                h.E.Seconds = 180; h.Sample(101);
                Assert(h.Motion(h.A) && h.Wakeups.Count == 0 && h.E.Work.Count == 1,
                    "Client did not restart at the 180-second default.");
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }));
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "ResumeSent"),
                    "Healthy sibling was disturbed by the position restart.");
                Assert(h.Forgotten.SequenceEqual(new[] { 101 }));
            }
        }

        private static void UnreadableRestart()
        {
            using (var h = new H())
            {
                h.Motion(h.A);
                h.E.Seconds = 179; Assert(!h.Motion(h.A) && h.E.Work.Count == 0);
                h.E.Seconds = 180; Assert(h.Motion(h.A));
                Assert(h.Wakeups.Count == 0 && h.E.Work.Count == 1,
                    "Unreadable coordinates did not escalate at the configured default.");
            }
        }

        private static void ConfigurableRestartThreshold()
        {
            using (var h = new H())
            {
                var settings = h.Supervisor.Settings;
                settings.MovementRestartSeconds = 240;
                Set(h.Supervisor, "settings", settings);
                h.Sample(101); h.Motion(h.A);
                h.E.Seconds = 239; h.Sample(101);
                Assert(!h.Motion(h.A) && h.E.Work.Count == 0, "Custom threshold fired early.");
                h.E.Seconds = 240; h.Sample(101);
                Assert(h.Motion(h.A) && h.E.Work.Count == 1, "Custom threshold did not fire.");
            }
        }

        private static void BothPosition()
        {
            using (var h = new H())
            {
                h.Sample(101); h.Sample(102); h.Motion(h.A); h.Motion(h.B);
                h.E.Seconds = 180; h.Sample(101); h.Sample(102);
                Assert(h.Motion(h.A)); Assert(h.Motion(h.B));
                Assert(h.E.Work.Count == 1, "Both stalled clients were allowed to close in parallel.");
                h.E.Work.Dequeue()();
                Assert((bool)Get(h.A, "RecoveryOwned") && Get(h.A, "ProcessId") == null);
                Assert(h.E.Work.Count == 0);
                h.FinishFirst();
                Assert(h.Motion(h.B) && h.E.Work.Count == 1);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101, 102 }));
            }
        }

        private static void Mixed()
        {
            using (var h = new H())
            {
                h.Sample(102); h.Motion(h.B);
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                Assert(h.E.Work.Count == 1, "Confirmed terminal dialog did not queue the first recovery.");
                h.E.Seconds = 180; h.Sample(102);
                Assert(h.Motion(h.B) && h.E.Work.Count == 1, "Second stalled client bypassed the shared recovery lease.");
                h.E.Work.Dequeue()();
                h.FinishFirst();
                Assert(h.Motion(h.B) && h.E.Work.Count == 1);
            }
        }

        private static void QueuedMovement()
        {
            using (var h = new H())
            {
                h.Sample(102); h.Motion(h.B);
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                Assert(h.E.Work.Count == 1);
                h.E.Seconds = 180; h.Sample(102);
                Assert(h.Motion(h.B) && h.E.Work.Count == 1);
                h.E.Seconds = 181; h.Sample(102, 11);
                Assert(!h.Motion(h.B), "Fresh queued movement did not cancel the stale restart decision.");
                Assert(h.Wakeups.Count == 0);
            }
        }

        private static void Stop()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                h.Supervisor.Stop();
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0 && (int?)Get(h.A, "ProcessId") == 101);
                Assert(!h.Motion(h.A));
            }
        }

        private static void StopAtBoundary()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                h.E.BeforeClose = h.Supervisor.Stop;
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0);
            }
        }

        private static void Settings()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                Set(h.Supervisor, "running", false);
                h.Supervisor.Apply(h.Supervisor.Settings, false);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0);
            }
        }

        private static void ReplacedPid()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                Set(h.A, "ProcessId", (int?)202);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0 && (int?)Get(h.A, "ProcessId") == 202);
            }
        }

        private static void ReplacedOperation()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A);
                Set(h.A, "ResumeOperationGeneration", 99);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0);
            }
        }

        private static void CloseFailure()
        {
            using (var h = new H())
            {
                h.Sample(101); Assert(!h.Motion(h.A));
                h.E.Seconds = 180; h.Sample(101);
                Assert(h.Motion(h.A) && h.E.Work.Count == 1, "Three-minute stall did not queue the close test.");
                h.E.FailClose = true;
                h.E.Work.Dequeue()();
                Assert((int?)Get(h.A, "ProcessId") == 101 && h.Forgotten.Count == 0
                    && (int)Get(h.A, "RecoveryFailures") == 1, "Close failure did not preserve PID/backoff state.");
                Assert(!(bool)Get(h.A, "FailedReplacementRetryPending"), "An ordinary movement-close failure became a never-online replacement retry.");
                Assert((DateTimeOffset?)Get(h.A, "NextRecoveryAt") > h.E.UtcNow, "Close failure did not schedule backoff.");
                h.E.Seconds = 181; h.Sample(101);
                Assert(!h.Motion(h.A) && h.E.Work.Count == 0, "Backoff allowed an immediate replacement close.");
            }
        }

        private static void RecoveryOff()
        {
            using (var h = new H())
            {
                var settings = h.Supervisor.Settings; settings.AutoRecover = false; Set(h.Supervisor, "settings", settings);
                h.Motion(h.A); h.E.Seconds = 181; h.Motion(h.A);
                Assert(h.Wakeups.Count == 0 && h.E.Work.Count == 0);
            }
        }

        private static void VisualOff()
        {
            using (var h = new H())
            {
                var settings = h.Supervisor.Settings; settings.VisualWatchdog = false; Set(h.Supervisor, "settings", settings);
                h.Motion(h.A); h.E.Seconds = 180;
                Assert(h.Motion(h.A));
                Assert(h.Wakeups.Count == 0 && h.E.Work.Count == 1);
            }
        }

        private static void StartupGrace()
        {
            using (var h = new H())
            {
                Set(h.A, "ScriptRunning", true);
                h.Motion(h.A); h.E.Seconds = 1000; h.Motion(h.A);
                Set(h.A, "ScriptRunning", false);
                Assert(!h.Motion(h.A) && h.E.Work.Count == 0);
            }
        }

        private static void MinimizeGrace()
        {
            Assert(!VanillaReconnectSupervisor.AutomaticMinimizeReady(TimeSpan.FromSeconds(59.999), TimeSpan.FromSeconds(120)),
                "Visible-window grace was shorter than 60 seconds.");
            Assert(!VanillaReconnectSupervisor.AutomaticMinimizeReady(TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(59.999)),
                "Cursor activity grace was shorter than 60 seconds.");
            Assert(VanillaReconnectSupervisor.AutomaticMinimizeReady(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)),
                "Exactly 60 seconds of visibility and cursor idle should allow minimization.");
        }

        private static void VerifiedMovementMinimize()
        {
            Assert(!VanillaReconnectSupervisor.AutomaticMinimizeReady(TimeSpan.Zero, TimeSpan.Zero, false),
                "Ordinary visible clients must still respect the user-presence grace.");
            Assert(VanillaReconnectSupervisor.AutomaticMinimizeReady(TimeSpan.Zero, TimeSpan.Zero, true),
                "Fresh verified recovery movement must allow immediate minimization.");
        }

        private static void WatchdogHotkeyFirst()
        {
            using (var h = new H())
            {
                h.Sample(101); h.Motion(h.A);
                h.E.Seconds = 179; h.Sample(101);
                Assert(!h.Motion(h.A) && h.Wakeups.Count == 0 && h.E.Work.Count == 0,
                    "Steady-state watchdog acted before the configured restart threshold.");
                h.E.Seconds = 180; h.Sample(101);
                Assert(h.Motion(h.A));
                Assert(h.Wakeups.Count == 0, "Steady-state watchdog requested an Autobattle hotkey.");
                Assert(h.E.Work.Count == 1, "Configured no-movement threshold did not queue a client restart.");
            }
        }

        private static void RestartBudget()
        {
            Assert(VanillaRecoveryPolicy.RetryDelayMs(1, 30000, 3600000) == 30000);
            Assert(VanillaRecoveryPolicy.RetryDelayMs(2, 30000, 3600000) == 60000);
            Assert(VanillaRecoveryPolicy.RetryDelayMs(3, 30000, 3600000) == 120000);
            Assert(VanillaRecoveryPolicy.RetryDelayMs(8, 30000, 3600000) == 3600000);
            Assert(VanillaRecoveryPolicy.RetryDelayMs(20, 30000, 3600000) == 3600000);
        }

        private static void RestartBudgetReset()
        {
            using (var h = new H())
            {
                Set(h.A, "RecoveryFailures", 7);
                Set(h.A, "NextRecoveryAt", (DateTimeOffset?)h.E.UtcNow.AddHours(1));
                Set(h.A, "RecoveryOwned", true);
                Call(h.Supervisor, "CompleteAutobattleRecoverySuccessLocked", h.A);
                Assert((int)Get(h.A, "RecoveryFailures") == 0 && Get(h.A, "NextRecoveryAt") == null
                    && !(bool)Get(h.A, "RecoveryOwned"));
            }
        }

        private static void WeightHoldSurvivesApply()
        {
            using (var h = new H())
            {
                var token = new VanillaWeightMaintenanceToken
                {
                    AccountId = ((VanillaReconnectAccount)Get(h.A, "Account")).Id,
                    ProcessId = 101,
                    Generation = 1,
                    Account = ((VanillaReconnectAccount)Get(h.A, "Account")).Clone()
                };
                h.Supervisor.MarkWeightMaintenanceCancelled(token, true, "synthetic cancelled cart test");
                Assert(h.Supervisor.IsWeightManualHold(token.AccountId), "Manual Cart hold was not established.");
                h.Supervisor.Apply(h.Supervisor.Settings, false);
                Assert(h.Supervisor.IsWeightManualHold(token.AccountId), "Unrelated settings Apply erased the manual Cart hold.");
            }
        }

        private static void WeightHoldSurvivesRuntimeRemoval()
        {
            using (var h = new H())
            {
                var accountA = (VanillaReconnectAccount)Get(h.A, "Account");
                var token = new VanillaWeightMaintenanceToken
                {
                    AccountId = accountA.Id, ProcessId = 101, Generation = 1, Account = accountA.Clone()
                };
                var settings = h.Supervisor.Settings;
                settings.Accounts.RemoveAll(a => a.Id == accountA.Id);
                h.Supervisor.Apply(settings, false);
                h.Supervisor.MarkWeightMaintenanceCancelled(token, true, "synthetic cancellation after runtime removal");
                Assert(h.Supervisor.IsWeightManualHold(accountA.Id),
                    "Cancelled Cart lost its stable-row hold after the runtime was removed.");
            }
        }

        private static void WeightHoldClearIsolation()
        {
            using (var h = new H())
            {
                var accountA = (VanillaReconnectAccount)Get(h.A, "Account");
                var token = new VanillaWeightMaintenanceToken
                {
                    AccountId = accountA.Id, ProcessId = 101, Generation = 1, Account = accountA.Clone()
                };
                h.Supervisor.MarkWeightMaintenanceCancelled(token, true, "synthetic cancelled cart test");
                Set(h.B, "Stage", VanillaReconnectStage.Error);
                Set(h.B, "Detail", "Unrelated recovery error");
                h.Supervisor.ClearWeightManualHolds();
                Assert(!h.Supervisor.IsWeightManualHold(accountA.Id), "Explicit clear did not remove the Cart hold.");
                Assert((VanillaReconnectStage)Get(h.B, "Stage") == VanillaReconnectStage.Error
                    && (string)Get(h.B, "Detail") == "Unrelated recovery error",
                    "Clearing a Cart hold erased an unrelated client error.");
            }
        }

        private static void ManualActionIdentityGate()
        {
            using (var h = new H())
            {
                var row = (VanillaReconnectAccount)Get(h.A, "Account");
                row.UserName = "user-a";
                row.CharacterName = "char-a";

                int pid;
                VanillaReconnectAccount resolved;
                string reason;
                Assert(!h.Supervisor.TryResolveOnlineManagedCharacter(row.Id, out pid, out resolved, out reason),
                    "Manual action was authorized without fresh positive identity evidence.");

                h.Supervisor.SetCharacterSource(() => new[]
                {
                    new VanillaCharacterIdentity(101, Guid.NewGuid(), DateTimeOffset.UtcNow, "char-a", "user-a")
                });
                Assert(h.Supervisor.TryResolveOnlineManagedCharacter(row.Id, out pid, out resolved, out reason)
                    && pid == 101 && resolved.CharacterName == "char-a",
                    "Fresh exact username + character identity did not authorize the selected manual action.");

                h.Supervisor.SetCharacterSource(() => new[]
                {
                    new VanillaCharacterIdentity(101, Guid.NewGuid(), DateTimeOffset.UtcNow, "other-char", "user-a")
                });
                Assert(!h.Supervisor.TryResolveOnlineManagedCharacter(row.Id, out pid, out resolved, out reason),
                    "Mismatched fresh character identity authorized a manual action.");
            }
        }

        private static void HealthTerminalDiagnosis()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                var settings = h.Supervisor.Settings; settings.VisualWatchdog = false; Set(h.Supervisor, "settings", settings);
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Disconnected, true, null);
                h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 0, "Missing health captured a background window.");
                h.E.Seconds = 30; h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 1, "Stalled health did not queue serialized foreground diagnosis.");
                h.E.Work.Dequeue()();
                Assert(h.VisualReads == 2 && h.E.Work.Count == 1, "Two fresh foreground terminal frames did not queue close.");
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && (int?)Get(h.B, "ProcessId") == 102);
            }
        }

        private static void HealthyVisualOff()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                foreach (bool legacyVisualEnabled in new[] { false, true })
                {
                    var settings = h.Supervisor.Settings; settings.VisualWatchdog = legacyVisualEnabled; Set(h.Supervisor, "settings", settings);
                    h.E.Seconds += 20; h.Sample(101, (int)h.E.Seconds); h.Probe(h.A);
                    Assert(h.VisualReads == 0 && h.E.Work.Count == 0, "Healthy movement or the legacy visual setting caused screen diagnosis.");
                }
                h.E.Seconds += 31; h.Sample(101, 40); h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 1, "Stationary health did not queue diagnosis without passive capture.");
                h.E.Work.Dequeue()();
                Assert(h.VisualReads == 2 && h.E.Work.Count == 0 && h.E.Closed.Count == 0, "Unknown foreground evidence authorized recovery input.");
            }
        }

        private static void FailedVisualDiagnosis()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                h.OnVisualRead = pid => { throw new System.ComponentModel.Win32Exception(5, "Synthetic capture denied"); };
                h.Probe(h.A);
                h.E.Seconds = 30; h.Probe(h.A); h.E.Work.Dequeue()();
                h.E.Seconds = 179; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert(h.VisualReads == 2 && h.E.Work.Count == 0 && h.E.Closed.Count == 0);
                h.E.Seconds = 180; h.Probe(h.A);
                Assert(h.E.Work.Count == 1, "Capture failures reset the original unavailable-health deadline.");
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && h.Wakeups.Count == 0);
            }
        }

        private static void OnlineCaptureFailure()
        {
            using (var h = new H())
            {
                Set(h.A, "HasBeenOnline", true); Set(h.A, "ResumeSent", false);
                Set(h.A, "LastLaunch", (DateTimeOffset?)Epoch.AddMinutes(-10));
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false,
                    "Background capture exceeded its bounded observation time");
                h.Sample(101); h.Probe(h.A);
                Assert((VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.Online,
                    "A failed capture demoted confirmed gameplay to startup.");
                h.E.Seconds = 15; h.Sample(101, 11);
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, true, null);
                h.Probe(h.A);
                Assert((VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.Online
                    && !(bool)Get(h.A, "ScriptRunning") && !(bool)Get(h.A, "RecoveryOwned")
                    && !(bool)Get(h.A, "ResumeSent") && h.Wakeups.Count == 0 && h.E.Work.Count == 0,
                    "A later ambiguous capture armed login/resume using the old launch timestamp.");
            }
        }
        private static void AdoptedWaitingStage()
        {
            using (var h = new H())
            {
                Set(h.A, "Stage", VanillaReconnectStage.WaitingForWindow);
                Set(h.A, "LastLaunch", (DateTimeOffset?)Epoch.AddMinutes(-10));
                h.Sample(101); h.Probe(h.A);
                Call(h.Supervisor, "QueueLogin", h.A, true, "synthetic stale startup stage");
                Assert(!(bool)Get(h.A, "ScriptRunning") && !(bool)Get(h.A, "RecoveryOwned")
                    && (VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.Online
                    && h.E.Work.Count == 0 && h.Wakeups.Count == 0,
                    "A stage label granted recovery ownership to an adopted client.");
            }
        }
        private static void ReplacementCaptureFailure()
        {
            using (var h = new H())
            {
                Set(h.A, "RecoveryOwned", true); Set(h.A, "HasBeenOnline", false); Set(h.A, "ResumeSent", false);
                Set(h.A, "LastLaunch", (DateTimeOffset?)Epoch.AddMinutes(-10));
                Set(h.A, "Stage", VanillaReconnectStage.WaitingForWindow);
                ((VanillaReconnectAccount)Get(h.A, "Account")).ProtectedPassword = null;
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false, "capture timeout");
                h.Probe(h.A);
                Assert((VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.NeedsConfiguration,
                    "Replacement remained in an unbounded capture wait instead of entering the login readiness/configuration gate.");
                Assert(h.E.Work.Count == 0 && h.Wakeups.Count == 0, "Missing credentials authorized input.");
            }
        }
        private static void ArmRetainedReplacement(H h)
        {
            Set(h.A, "RecoveryOwned", true); Set(h.A, "HasBeenOnline", false); Set(h.A, "ResumeSent", false);
            Call(h.Supervisor, "ScheduleRecoveryFailureLocked", h.A, h.E.UtcNow, "Interactive window never became ready; initial close failed");
            Assert((bool)Get(h.A, "FailedReplacementRetryPending") && !(bool)Get(h.A, "RecoveryOwned"));
        }
        private static void RetainedReplacementRetry()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h);
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false, "capture timeout");
                h.E.Seconds = 29; h.Probe(h.A); Assert(h.E.Work.Count == 0 && h.VisualReads == 0);
                h.E.Seconds = 30; h.Probe(h.A);
                Assert(h.E.Work.Count == 1 && h.VisualReads == 0 && (bool)Get(h.A, "RecoveryOwned"));
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101 }) && (int?)Get(h.B, "ProcessId") == 102
                    && Get(h.A, "ProcessId") == null && !(bool)Get(h.A, "FailedReplacementRetryPending")
                    && (bool)Get(h.A, "RecoveryOwned") && h.Wakeups.Count == 0,
                    "Failed replacement retry lost exit ordering, sibling isolation or the relaunch lease.");
            }
        }
        private static void RetainedReplacementModal()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h);
                Set(h.A, "Visual", VanillaVisualState.ModalDialog);
                h.E.Seconds = 30; h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 0 && (bool)Get(h.A, "FailedReplacementRetryPending"),
                    "A known unknown modal was bypassed by retained replacement retry.");
                Set(h.A, "LastVisualObservation", new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false, "capture unavailable"));
                h.E.Seconds = 32; h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 0, "Unavailable evidence cleared a previously observed modal.");
                Set(h.A, "Visual", VanillaVisualState.LoginShell);
                h.E.Seconds = 34; h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 1, "Explicitly cleared modal stranded the owned failed replacement.");
            }
        }

        private static void RetainedReplacementAdopted()
        {
            using (var h = new H())
            {
                Set(h.A, "ResumeSent", false); Set(h.A, "HasBeenOnline", false);
                Call(h.Supervisor, "ScheduleRecoveryFailureLocked", h.A, h.E.UtcNow, "unowned failure");
                h.E.Seconds = 30; h.Probe(h.A);
                Assert(!(bool)Get(h.A, "FailedReplacementRetryPending") && h.E.Work.Count == 0 && h.Wakeups.Count == 0);
            }
        }
        private static void RetainedReplacementStop()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h); h.E.Seconds = 30; h.Probe(h.A);
                h.Supervisor.Stop(); h.E.Work.Dequeue()();
                Assert(!(bool)Get(h.A, "FailedReplacementRetryPending") && h.E.Closed.Count == 0);
            }
        }
        private static void RetainedReplacementSettings()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h); h.E.Seconds = 30; h.Probe(h.A);
                Set(h.Supervisor, "running", false); h.Supervisor.Apply(h.Supervisor.Settings, false); h.E.Work.Dequeue()();
                Assert(!(bool)Get(h.A, "FailedReplacementRetryPending") && h.E.Closed.Count == 0);
            }
        }
        private static void RetainedReplacementIdentity()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h); h.E.StartShiftSeconds = 1; h.E.Seconds = 30; h.Probe(h.A);
                Assert(!(bool)Get(h.A, "FailedReplacementRetryPending") && h.E.Work.Count == 0 && h.E.Closed.Count == 0,
                    "A recycled PID inherited replacement close authorization.");
            }
        }
        private static void RetainedReplacementSibling()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h); Set(h.B, "RecoveryOwned", true);
                h.E.Seconds = 30; h.Probe(h.A);
                Assert(h.E.Work.Count == 0 && (bool)Get(h.A, "FailedReplacementRetryPending"));
                Set(h.B, "RecoveryOwned", false); h.E.Seconds = 32; h.Probe(h.A);
                Assert(h.E.Work.Count == 1 && (bool)Get(h.A, "RecoveryOwned") && h.Wakeups.Count == 0);
            }
        }
        private static void RetainedReplacementCloseFailure()
        {
            using (var h = new H())
            {
                ArmRetainedReplacement(h); h.E.Seconds = 30; h.E.FailClose = true; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert((bool)Get(h.A, "FailedReplacementRetryPending") && (int)Get(h.A, "RecoveryFailures") == 2);
                h.E.Seconds = 89; h.Probe(h.A); Assert(h.E.Work.Count == 0);
                h.E.Seconds = 90; h.E.FailClose = false; h.Probe(h.A);
                Assert(h.E.Work.Count == 1, "A failed retry close stranded the retained process after backoff.");
                h.E.Work.Dequeue()(); Assert(h.E.Closed.SequenceEqual(new[] { 101 }));
            }
        }
        private static void CaptureClock()
        {
            using (var h = new H())
            {
                h.Supervisor.SetPositionSource(pid =>
                {
                    h.E.Seconds += 1; h.Sample(pid, (int)h.E.Seconds);
                    return h.Samples[pid];
                }, pid => { });
                h.Probe(h.A);
                h.E.Seconds = 180; h.Probe(h.A);
                Assert(h.VisualReads == 0 && h.E.Work.Count == 0 && ((VanillaMovementWatchdog)Get(h.A, "MovementWatchdog"))
                    .StalledSeconds(h.E.MonotonicNow) == 0, "Read duration made freshly produced movement look like future/stale data.");
            }
        }

        private static void ModalBeforeRestart()
        {
            using (var h = new H())
            {
                h.Sample(101); h.Probe(h.A);
                h.E.Seconds = 180; h.Sample(101);
                Set(h.A, "Visual", VanillaVisualState.ModalDialog);
                h.Probe(h.A);
                Assert(h.E.Work.Count == 0 && h.E.Closed.Count == 0, "Movement deadline bypassed unknown-modal guard.");
                h.E.Seconds = 181; h.Sample(101);
                Set(h.A, "Visual", VanillaVisualState.Gameplay);
                h.Probe(h.A);
                Assert(h.E.Work.Count == 1 && h.VisualReads == 0, "Unknown modal reset the preceding movement deadline.");
            }
        }

        private static void ModalCaptureGap()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                h.Probe(h.A);
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.ModalDialog, true, null);
                h.E.Seconds = 30; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert((VanillaVisualState)Get(h.A, "Visual") == VanillaVisualState.ModalDialog);
                foreach (string error in new[] { "capture timeout", null })
                {
                    h.E.Seconds += 61;
                    h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, error == null, error);
                    h.Probe(h.A); h.E.Work.Dequeue()();
                    Assert((VanillaVisualState)Get(h.A, "Visual") == VanillaVisualState.ModalDialog
                        && h.E.Work.Count == 0 && h.Wakeups.Count == 0 && h.E.Closed.Count == 0,
                        "Unavailable or ambiguous foreground evidence was treated as modal dismissal.");
                }
            }
        }

        private static void FailedTerminalConfirmation()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                h.Visuals[101] = new VanillaRecoveryVisualObservation(VanillaVisualState.Disconnected, true, null);
                h.OnVisualRead = pid => { if (h.VisualReads == 2) throw new InvalidOperationException("Synthetic missing second frame"); };
                h.Probe(h.A); h.E.Seconds = 30; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert(h.VisualReads == 2 && h.E.Work.Count == 0 && h.E.Closed.Count == 0, "A failed second capture confirmed a terminal state.");
                h.E.Seconds = 91; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert(h.VisualReads == 4 && h.E.Work.Count == 1, "A fresh matching pair did not establish terminal confirmation.");
            }
        }

        private static void CancelVisualDiagnosis()
        {
            using (var h = new H())
            {
                h.EnableForegroundDiagnosis();
                h.OnVisualRead = pid => h.Supervisor.Stop();
                h.Probe(h.A); h.E.Seconds = 30; h.Probe(h.A); h.E.Work.Dequeue()();
                Assert(h.VisualReads == 1 && h.E.Work.Count == 0 && h.E.Closed.Count == 0 && h.Wakeups.Count == 0);
            }
        }

        private static void BoundedVisualDiagnosis()
        {
            var capture = new VanillaRecoveryVisualCapture();
            using (var release = new System.Threading.ManualResetEvent(false))
            using (var started = new System.Threading.ManualResetEvent(false))
            using (var completed = new System.Threading.ManualResetEvent(false))
            {
                int calls = 0, finished = 0;
                Func<VanillaRecoveryVisualObservation> blocked = () =>
                {
                    System.Threading.Interlocked.Increment(ref calls); started.Set();
                    release.WaitOne(3000); System.Threading.Interlocked.Increment(ref finished); completed.Set();
                    return new VanillaRecoveryVisualObservation(VanillaVisualState.Disconnected, true, null);
                };
                try
                {
                    Assert(capture.Observe(101, blocked, 20).State == VanillaVisualState.Unknown);
                    Assert(started.WaitOne(1000));
                    Assert(capture.Observe(101, blocked, 20).State == VanillaVisualState.Unknown && calls == 1,
                        "A timed-out capture launched another worker for the same window.");
                    Assert(capture.Observe(102, blocked, 20).State == VanillaVisualState.Unknown);
                    Assert(System.Threading.SpinWait.SpinUntil(() => calls == 2, 1000));
                    Assert(capture.Observe(103, blocked, 20).State == VanillaVisualState.Unknown && calls == 2,
                        "Blocked native captures exceeded the process-wide two-worker bound.");
                }
                finally { release.Set(); }
                Assert(completed.WaitOne(1000));
                Assert(System.Threading.SpinWait.SpinUntil(() => finished == 2, 1000));
                System.Threading.Thread.Sleep(20);
                var fresh = capture.Observe(101, () => new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, true, null), 1000);
                Assert(fresh.State == VanillaVisualState.Unknown && fresh.Error == null, "A late terminal frame was reused as fresh evidence.");
            }
        }
        private static void MinimizedTerminalCapture()
        {
            VanillaIsolatedTestDesktop.AssertCurrent();
            using (var form = new RecoveryDialogForm { ClientSize = new Size(640, 480), ShowInTaskbar = false })
            {
                form.Show(); System.Windows.Forms.Application.DoEvents();
                form.WindowState = System.Windows.Forms.FormWindowState.Minimized;
                System.Windows.Forms.Application.DoEvents();
                int printCount = form.PrintCount;
                Assert(VanillaVisualProbe.Classify(form.Handle) == VanillaVisualState.Unknown,
                    "A minimized window supplied visual evidence without owning the foreground.");
                Assert(form.PrintCount == printCount, "Passive classification asked the minimized window to render a background screenshot.");
                Assert(form.WindowState == System.Windows.Forms.FormWindowState.Minimized, "Passive diagnosis restored a minimized window.");
            }
        }

        private sealed class RecoveryDialogForm : System.Windows.Forms.Form
        {
            internal int PrintCount;
            protected override void WndProc(ref System.Windows.Forms.Message message)
            {
                if (message.Msg == 0x0317 || message.Msg == 0x0318) PrintCount++;
                base.WndProc(ref message);
            }
        }

        private static void OneTerminal()
        {
            using (var h = new H())
            {
                h.Terminal(h.A);
                Assert(h.E.Work.Count == 0, "One terminal observation closed a client.");
                h.E.Seconds = 1; h.Terminal(h.A);
                Assert(h.E.Work.Count == 1, "Two fresh matching terminal observations did not queue recovery.");
            }
        }

        private static void UnknownModal()
        {
            using (var h = new H())
            {
                Call(h.Supervisor, "HandleTerminalVisual", h.A, VanillaVisualState.ModalDialog, h.E.UtcNow,
                    (Func<DateTime>)(() => Epoch.UtcDateTime));
                Assert(h.E.Work.Count == 0 && h.E.Closed.Count == 0);
            }
        }

        private static void ChangedTerminal()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 1; h.Terminal(h.A, 1);
                Assert(h.E.Work.Count == 0);
            }
        }

        private static void StaleTerminal()
        {
            using (var h = new H())
            {
                h.Terminal(h.A); h.E.Seconds = 10; h.Terminal(h.A);
                Assert(h.E.Work.Count == 0);
            }
        }

        private static void BothTerminal(int a, int b)
        {
            using (var h = new H())
            {
                h.Terminal(h.A, a); h.Terminal(h.B, b);
                h.E.Seconds = 1; h.Terminal(h.A, a); h.Terminal(h.B, b);
                Assert(h.E.Work.Count == 1);
                h.E.Work.Dequeue()();
                h.E.Seconds = 2; h.Terminal(h.B, b);
                Assert(h.E.Work.Count == 0);
                h.FinishFirst();
                h.E.Seconds = 3; h.Terminal(h.B, b);
                Assert(h.E.Work.Count == 1);
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.SequenceEqual(new[] { 101, 102 }));
            }
        }

        private static void ColdStart()
        { using(var h=new H()){Set(h.Supervisor,"running",false);Set(h.Supervisor,"hardenedStartupRunning",true);Assert((bool)Call(h.Supervisor,"CloseTerminalBeforeStartup",h.A,101,0,h.Supervisor.Settings,(Func<VanillaVisualState>)(()=>VanillaVisualState.LoggingOut),(Func<DateTime>)(()=>Epoch.UtcDateTime),(Action<int>)(ms=>h.E.Seconds+=ms/1000.0)));Assert(h.E.Closed.SequenceEqual(new[]{101}) && (bool)Get(h.A,"RecoveryOwned"));} }
        private static void ColdStartVisualOff()
        {
            using (var h = new H())
            {
                Set(h.Supervisor, "running", false); Set(h.Supervisor, "hardenedStartupRunning", true);
                var settings = h.Supervisor.Settings; settings.VisualWatchdog = false;
                int reads = 0;
                Assert((bool)Call(h.Supervisor, "CloseTerminalBeforeStartup", h.A, 101, 0, settings,
                    (Func<VanillaVisualState>)(() => { reads++; return VanillaVisualState.Disconnected; }),
                    (Func<DateTime>)(() => Epoch.UtcDateTime), (Action<int>)(ms => h.E.Seconds += ms / 1000.0)));
                Assert(reads == 2 && h.E.Closed.SequenceEqual(new[] { 101 }) && (bool)Get(h.A, "RecoveryOwned")
                    && (int?)Get(h.B, "ProcessId") == 102, "START skipped diagnosis or disturbed its sibling.");
            }
        }
        private static void ColdStartChanged()
        { using(var h=new H()){Set(h.Supervisor,"running",false);Set(h.Supervisor,"hardenedStartupRunning",true);int reads=0;Expect<InvalidOperationException>(()=>Call(h.Supervisor,"CloseTerminalBeforeStartup",h.A,101,0,h.Supervisor.Settings,(Func<VanillaVisualState>)(()=>++reads==1?VanillaVisualState.LoggingOut:VanillaVisualState.Gameplay),(Func<DateTime>)(()=>Epoch.UtcDateTime),(Action<int>)(ms=>h.E.Seconds+=ms/1000.0)));Assert(h.E.Closed.Count==0);} }
        private sealed class Protocol
        {
            internal int Ms, Closes, Kills;
            internal bool Exited, Cancelled, NeverExit, BadRead, Denied, TerminationDenied;
            internal Action OnWait, OnExitQuery, OnTerminate;
            internal void Run() { Run(false); }
            internal void Run(bool immediate)
            {
                VanillaClientCloseProtocol.Run(
                    () => { OnExitQuery?.Invoke(); if (BadRead) throw new InvalidOperationException("Exit query denied"); return Exited; },
                    () => { Closes++; if (Denied) throw new InvalidOperationException("Close denied"); },
                    () =>
                    {
                        Kills++;
                        if (TerminationDenied) throw new InvalidOperationException("Termination denied");
                        OnTerminate?.Invoke();
                        if (!NeverExit) Exited = true;
                    },
                    () => Cancelled, () => TimeSpan.FromMilliseconds(Ms),
                    ms => { Ms += ms; OnWait?.Invoke(); }, immediate);
            }
        }
        private static void NormalClose(){var p=new Protocol();p.OnWait=()=>p.Exited=true;p.Run();Assert(p.Closes==1&&p.Kills==0);}
        private static void ForcedClose(){var p=new Protocol();p.Run();Assert(p.Closes==1&&p.Kills==1&&p.Ms==3000);}
        private static void CloseTimeout(){var p=new Protocol{NeverExit=true};Expect<TimeoutException>(p.Run);Assert(p.Ms==6000&&p.Kills==1);}
        private static void StopDuringClose(){var p=new Protocol();p.OnWait=()=>p.Cancelled=true;Expect<OperationCanceledException>(p.Run);Assert(p.Kills==0&&p.Ms==50);}
        private static void UnknownExit(){var p=new Protocol{BadRead=true};Expect<InvalidOperationException>(p.Run);Assert(p.Closes+p.Kills==0);}
        private static void CloseDenied(){var p=new Protocol{Denied=true};Expect<InvalidOperationException>(p.Run);Assert(p.Kills==0);}
        private static void EmergencyClose()
        {
            var p = new Protocol();
            p.Run(true);
            Assert(p.Closes == 0 && p.Kills == 1 && p.Ms == 0 && p.Exited,
                "Emergency close spent time on the graceful path or failed to terminate.");
        }
        private static void EmergencyAlreadyExited()
        {
            var p = new Protocol { Exited = true };
            p.Run(true);
            Assert(p.Closes == 0 && p.Kills == 0 && p.Ms == 0);
        }
        private static void EmergencyCancelled()
        {
            var p = new Protocol { Cancelled = true };
            Expect<OperationCanceledException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 0 && p.Ms == 0);
        }
        private static void EmergencyCancelledAtBoundary()
        {
            var p = new Protocol();
            p.OnExitQuery = () => p.Cancelled = true;
            Expect<OperationCanceledException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 0 && p.Ms == 0);
        }
        private static void EmergencyCancelledDuringWait()
        {
            var p = new Protocol { NeverExit = true };
            p.OnWait = () => p.Cancelled = true;
            Expect<OperationCanceledException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 1 && p.Ms == 50 && !p.Exited);
        }
        private static void EmergencyDenied()
        {
            var p = new Protocol { TerminationDenied = true };
            Expect<InvalidOperationException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 1 && p.Ms == 0 && !p.Exited);
        }
        private static void EmergencyExitTimeout()
        {
            var p = new Protocol { NeverExit = true };
            Expect<TimeoutException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 1 && p.Ms == VanillaClientCloseProtocol.TerminationWaitMs && !p.Exited);
        }
        private static void EmergencyUnknownExit()
        {
            var p = new Protocol { BadRead = true };
            Expect<InvalidOperationException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 0 && p.Ms == 0);
        }
        private static void EmergencyUnknownExitAfterTermination()
        {
            var p = new Protocol { NeverExit = true };
            p.OnTerminate = () => p.BadRead = true;
            Expect<InvalidOperationException>(() => p.Run(true));
            Assert(p.Closes == 0 && p.Kills == 1 && p.Ms == 0 && !p.Exited);
        }
        private static void CloseRights(){Assert(VanillaClientCloseHandle.RequiredAccess==0x101001 && (VanillaClientCloseHandle.RequiredAccess&0x003A)==0);}
        private sealed class Meta:VanillaFleetMonitor.IProcessMetadata{public int ProcessId{get;set;}public bool HasExited{get{throw new Exception("Not needed");}}public IntPtr MainWindowHandle{get{throw new Exception("Not needed");}}public void Dispose(){}}
        private sealed class Reader:VanillaFleetMonitor.IClientReader{internal int Pid;internal VanillaPositionSample Frame;internal bool Disposed;public bool IsStopped{get{return Disposed;}}public VanillaFleetClientInfo Poll(TimeSpan now){return new VanillaFleetClientInfo{ProcessId=Pid,Position=Frame??S(0,pid:Pid)};}public void Dispose(){Disposed=true;}}
        private static void FleetCache()
        {
            var made=new List<Reader>();
            using(var m=new VanillaFleetMonitor(Path.GetTempPath(),()=>new VanillaFleetMonitor.IProcessMetadata[]{new Meta{ProcessId=101},new Meta{ProcessId=102}},pid=>{var r=new Reader{Pid=pid};made.Add(r);return r;},()=>null))
            {m.Poll();Assert(m.LatestPosition(101).X==10);m.ConfirmClientExited(101);Assert(m.LatestPosition(101)==null&&m.LatestPosition(102)!=null&&made[0].Disposed&&!made[1].Disposed);m.Poll();Assert(made.Count==3);}
        }
        private static Bitmap Scene(string name,float scale)
        {
            var image=new Bitmap(1280,900);
            using(var stream=VanillaDisconnectPattern.OpenReference(name))using(var reference=new Bitmap(stream))using(var g=Graphics.FromImage(image))
            {g.Clear(Color.FromArgb(55,90,40));g.FillRectangle(Brushes.DarkOliveGreen,0,0,320,140);g.InterpolationMode=InterpolationMode.HighQualityBicubic;int w=(int)(reference.Width*scale),h=(int)(reference.Height*scale);g.DrawImage(reference,(1280-w)/2,(900-h)/2,w,h);}
            return image;
        }
        private static void ImageCase(string name,float scale){using(var image=Scene(name,scale)){var expected=name=="LoggingOut"?VanillaVisualState.LoggingOut:VanillaVisualState.Disconnected;Assert(VanillaVisualProbe.Classify(image)==expected,"Message image was not recognized.");}}
        private static void BlankImage(){using(var image=new Bitmap(800,600))using(var g=Graphics.FromImage(image))foreach(var c in new[]{Color.Black,Color.White,Color.Gray}){g.Clear(c);Assert(VanillaVisualProbe.Classify(image)==VanillaVisualState.Unknown);}}
        private static void WrongImage(){using(var image=Scene("LoggingOut",1)){using(var g=Graphics.FromImage(image)){g.FillRectangle(Brushes.White,512,420,240,38);using(var font=new Font("Arial",9))g.DrawString("Please select a character.",font,Brushes.Black,519,425);}Assert(!VanillaReconnectSupervisor.IsTerminalDisconnect(VanillaVisualProbe.Classify(image)));}}
        private static object Get(object o,string n){return o.GetType().GetField(n,Flags).GetValue(o);}
        private static void Set(object o,string n,object v){o.GetType().GetField(n,Flags).SetValue(o,v);}
        private static object Call(object o,string n,params object[] args){try{return o.GetType().GetMethod(n,Flags).Invoke(o,args);}catch(TargetInvocationException e){throw e.InnerException;}}
        private static void Expect<T>(Action a)where T:Exception{try{a();}catch(T){return;}throw new Exception("Expected "+typeof(T).Name);}
        private static void Assert(bool value,string message="Assertion failed"){if(!value)throw new Exception(message);}
        private static void Test(string name,Action a){try{a();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
    }
}
