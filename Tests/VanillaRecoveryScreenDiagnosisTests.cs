using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaRecoveryScreenDiagnosisTests
    {
        private static int passed, failed;
        private static readonly DateTimeOffset Epoch = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private static readonly Guid Session = Guid.NewGuid();
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static int Run()
        {
            Test("Healthy memory supervision takes no screenshots and stall diagnosis ignores legacy visual switch", BackgroundPolicy);
            Test("Usable Unknown frames authorize memory-backed recovery only at the configured idle", StationaryScheduling);
            Test("Teleport without auto relog repeats at idle while preserving the restart deadline", TeleportWithoutRelog);
            Test("Failed foreground capture cannot authorize stationary input", FailedCapture);
            Test("Original restart deadline does not cancel its foreground diagnosis", DeadlineDiagnosis);
            Test("Configured teleport idle five through sixty seconds retains exact scheduling", ConfiguredIdle);
            Test("New movement does not inherit the previous stall diagnosis cooldown", NewStallCooldown);
            Test("Two fresh diagnosis frames restore only an originally minimized window", FreshPair);
            Test("Changed or stale diagnosis frames authorize no terminal result", FrameGuards);
            Test("STOP suppresses cleanup while movement-only cancellation restores original state", Cancellation);
            Test("Capture and cleanup failures always dispose the diagnosis session", SessionFailures);
            Test("Confirmed foreground disconnects use the existing serialized close flow", TerminalIntegration);
            Test("Confirmed foreground Server Closed uses the shared outage schedule", ServerClosedIntegration);
            Test("Mixed foreground dialog frames cannot authorize close or outage", MixedDialogPair);
            Test("Foreground diagnosis cancels before capture when movement or deadline arrives", QueuedCancellation);
            Test("Diagnosis cannot clear a replacement operation's lease", ReplacedOperation);
            Test("Diagnosis disposal failure releases its own lease", DisposalLease);
            Test("Diagnosis is throttled without resetting the movement deadline", Throttle);
            Test("Temporary ownership and explicit holds block foreground diagnosis", Holds);
            Test("An existing unknown modal survives two Unknown foreground frames", ModalLatch);
            Test("An initial modal arms passive health timing for later diagnosis", InitialModal);
            Test("Stale, future and foreign-PID frames cannot authorize recovery", PublicationFreshness);
            Test("Login-shell diagnosis respects its configured settle and capture budget", LoginSettle);
            Test("A stable foreground login shell recovers an established client", LoginRecovery);
            Console.WriteLine("Recovery screen diagnosis: {0} passed; {1} failed. Fake native boundaries; no desktop or game input.", passed, failed);
            return failed;
        }
        private static void BackgroundPolicy()
        {
            foreach (bool legacy in new[] { false, true })
            using (var h = new Harness())
            {
                ((VanillaReconnectSettings)Get(h.Supervisor, "settings")).VisualWatchdog = legacy;
                h.Sample(); h.Probe();
                h.E.Seconds = 29; h.Sample(); h.Probe();
                Assert(h.Opens == 0 && h.E.Work.Count == 0 && h.Resumes == 0);
                h.E.Seconds = 30; h.Sample(); h.Probe();
                Assert(h.Opens == 0 && h.E.Work.Count == 1 && h.Resumes == 0);
            }
        }
        private static void StationaryScheduling()
        {
            using (var h = new Harness())
            {
                var account = (VanillaReconnectAccount)Get(h.A, "Account");
                account.SmartTeleportEnabled = true; account.SmartTeleportKey = 116; account.SmartTeleportIdleSeconds = 60;
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.Resumes == 0 && (long)Get(h.A, "ForegroundGameplayVersion") == h.Watchdog.ProgressVersion);
                h.E.Seconds = 60; h.Sample();
                Assert((bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A) && h.Resumes == 1);
                Assert(!(bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A) && h.Resumes == 1);
                h.E.Seconds = 61; h.Sample(11); h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow);
                h.E.Seconds = 121; h.Sample(11);
                Assert(!(bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A), "Old foreground evidence authorized a new stall.");
                Assert(h.Queue()); h.E.Work.Dequeue()(); Assert(h.Resumes == 2);
            }
        }
        private static void TeleportWithoutRelog()
        {
            using (var h = new Harness())
            {
                ((VanillaReconnectSettings)Get(h.Supervisor, "settings")).AutoRecover = false;
                var account = (VanillaReconnectAccount)Get(h.A, "Account");
                account.SmartTeleportEnabled = true; account.SmartTeleportKey = 116; account.SmartTeleportIdleSeconds = 60;
                h.Arm(); h.E.Seconds = 60; h.Sample(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.Resumes == 1);
                h.E.Seconds = 90; h.Sample(); Assert(!(bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A));
                h.E.Seconds = 121; h.Sample(); Assert((bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A));
                Assert(h.Resumes == 2 && h.E.Closed.Count == 0 && h.Watchdog.StalledSeconds(h.E.MonotonicNow) == 121);
            }
            using (var h = new Harness())
            {
                ((VanillaReconnectSettings)Get(h.Supervisor, "settings")).AutoRecover = false;
                ((VanillaReconnectAccount)Get(h.A, "Account")).SmartTeleportEnabled = true;
                h.Arm(); h.Screen.Frame = n => h.Frame(n, VanillaVisualState.ServerClosed);
                Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 0 && h.E.Work.Count == 0 && h.Resumes == 0);
            }
        }
        private static void FailedCapture()
        {
            using (var h = new Harness())
            {
                h.Arm(); h.Screen.FailCapture = true; Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.Resumes == 0 && (long)Get(h.A, "ForegroundGameplayVersion") == -1);
            }
        }
        private static void DeadlineDiagnosis()
        {
            using (var h = new Harness())
            {
                h.Arm(); Assert(h.Queue()); h.E.Seconds = 180; h.Sample(); h.E.Work.Dequeue()();
                Assert(h.Opens == 1 && h.Screen.Captures == 2 && h.Resumes == 0 && h.Watchdog.StalledSeconds(h.E.MonotonicNow) >= 180);
            }
        }
        private static void ConfiguredIdle()
        {
            foreach (int idle in new[] { 5, 29, 30, 60 })
            using (var h = new Harness())
            {
                var account = (VanillaReconnectAccount)Get(h.A, "Account");
                account.SmartTeleportEnabled = true; account.SmartTeleportKey = 116; account.SmartTeleportIdleSeconds = idle;
                h.Sample(); h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow);
                h.E.Seconds = Math.Min(30, idle) - .1; h.Sample();
                h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow); Assert(!h.Queue());
                h.E.Seconds += .1; h.Sample(); h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow);
                Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.Resumes == (idle <= 30 ? 1 : 0), "Configured idle changed for " + idle + "s.");
                if (idle > 30)
                { h.E.Seconds = idle; h.Sample(); Assert((bool)Call(h.Supervisor, "TryQueueStalledAutobattleRecovery", h.A)); }
            }
            using (var h = new Harness())
            {
                var account = (VanillaReconnectAccount)Get(h.A, "Account");
                account.SmartTeleportEnabled = false; account.SmartTeleportKey = 116; account.SmartTeleportIdleSeconds = 60;
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.Resumes == 1, "Disabled teleport with retained key delayed the ordinary stall-recovery fallback.");
            }
        }
        private static void NewStallCooldown()
        {
            using (var h = new Harness())
            {
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                h.E.Seconds = 31; h.Sample(11); h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow);
                h.E.Seconds = 61; h.Sample(11); h.Watchdog.Observe(101, h.Position, h.E.MonotonicNow, h.E.UtcNow);
                Assert(h.Queue(), "Prior stall's cooldown suppressed a new stationary episode.");
                h.E.Work.Dequeue()(); Assert(h.Resumes == 2);
            }
        }
        private sealed class Screen : IVanillaRecoveryScreenSession
        {
            internal int Captures, Restores, Disposals;
            internal bool Cancelled, Owned = true, FailCapture, FailRestore, FailDispose;
            internal Func<int, VanillaRecoveryScreenFrame> Frame = n => NewFrame(n);
            internal Action<int> OnCapture;
            public bool WasMinimized { get; set; } = true;
            public VanillaRecoveryScreenFrame Capture()
            {
                Captures++; OnCapture?.Invoke(Captures);
                if (FailCapture) throw new InvalidOperationException("capture unavailable");
                return Frame(Captures);
            }
            public void RestoreMinimizedState(Action<Action> owned)
            {
                if (Captures == 0) return;
                owned(() => { Restores++; if (FailRestore) throw new InvalidOperationException("restore failed"); });
            }
            public void Dispose() { Disposals++; if (FailDispose) throw new InvalidOperationException("dispose failed"); }
            internal VanillaRecoveryScreenFrame[] Run()
            { return VanillaRecoveryScreenDiagnosis.Observe(this, () => Cancelled, action => { if (Owned) action(); }, ms => { }); }
        }
        private static VanillaRecoveryScreenFrame NewFrame(int n, VanillaVisualState state = VanillaVisualState.Unknown)
        {
            return new VanillaRecoveryScreenFrame { State = state, At = Epoch.AddMilliseconds(n * 150),
                Proof = new VanillaVisualInputProof(101, new IntPtr(501), new Size(640, 480), new Point(20, 30), n) };
        }
        private static void FreshPair()
        {
            foreach (bool minimized in new[] { false, true })
            {
                var screen = new Screen { WasMinimized = minimized };
                Assert(screen.Run().Length == 2 && screen.Captures == 2 && screen.Disposals == 1
                    && screen.Restores == (minimized ? 1 : 0));
            }
        }
        private static void FrameGuards()
        {
            foreach (string change in new[] { "missing", "proof", "time", "stamp", "pid", "window", "origin", "size" })
            {
                var screen = new Screen { Frame = n =>
                {
                    var frame = NewFrame(n); if (n == 1) return frame;
                    if (change == "missing") return null;
                    if (change == "proof") frame.Proof = null;
                    else if (change == "time") frame.At = Epoch;
                    else frame.Proof = new VanillaVisualInputProof(change == "pid" ? 102 : 101,
                        new IntPtr(change == "window" ? 502 : 501), new Size(change == "size" ? 800 : 640, 480),
                        new Point(change == "origin" ? 21 : 20, 30), change == "stamp" ? 1 : n);
                    return frame;
                } };
                Reject(screen.Run); Assert(screen.Disposals == 1 && screen.Restores == 1);
            }
        }
        private static void Cancellation()
        {
            var before = new Screen { Cancelled = true };
            Reject(before.Run); Assert(before.Captures == 0 && before.Restores == 0 && before.Disposals == 1);
            foreach (bool stillOwned in new[] { false, true })
            {
                var screen = new Screen { Owned = stillOwned };
                screen.OnCapture = n => screen.Cancelled = true;
                Reject(screen.Run);
                Assert(screen.Captures == 1 && screen.Restores == (stillOwned ? 1 : 0) && screen.Disposals == 1);
            }
        }
        private static void SessionFailures()
        {
            foreach (string fail in new[] { "capture", "restore", "dispose" })
            {
                var screen = new Screen { FailCapture = fail == "capture", FailRestore = fail == "restore", FailDispose = fail == "dispose" };
                Reject(screen.Run); Assert(screen.Disposals == 1);
            }
        }
        private sealed class Environment : IVanillaRecoveryRestartEnvironment
        {
            internal double Seconds;
            internal int CreationShift;
            internal readonly Queue<Action> Work = new Queue<Action>();
            internal readonly List<int> Closed = new List<int>();
            public DateTimeOffset UtcNow => Epoch.AddSeconds(Seconds);
            public TimeSpan MonotonicNow => TimeSpan.FromSeconds(Seconds);
            public DateTime GetStartTimeUtc(int pid) => Epoch.UtcDateTime.AddSeconds(CreationShift);
            public void Queue(Action action) { Work.Enqueue(action); }
            public void CloseClient(int pid, DateTime created, Func<bool> cancelled, Action<Action> owned, bool immediate = false)
            { if (cancelled()) throw new OperationCanceledException(); owned(() => Closed.Add(pid)); }
        }
        private sealed class Harness : IDisposable
        {
            internal readonly Environment E = new Environment();
            internal readonly VanillaReconnectSupervisor Supervisor;
            internal readonly object A, B;
            internal Screen Screen = new Screen();
            internal VanillaPositionSample Position;
            internal int Opens, Resumes;
            internal VanillaRecoveryVisualObservation Background = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false, "nativeError=18");
            private readonly string directory = Path.Combine(Path.GetTempPath(), "4R-screen-" + Guid.NewGuid().ToString("N"));
            internal Harness()
            {
                Supervisor = new VanillaReconnectSupervisor(directory, E);
                var runtimes = (IDictionary)Get(Supervisor, "runtimes");
                A = runtimes[Supervisor.Settings.Accounts[0].Id]; B = runtimes[Supervisor.Settings.Accounts[1].Id];
                foreach (var pair in new[] { Tuple.Create(A, 101), Tuple.Create(B, 102) })
                { Set(pair.Item1, "ProcessId", (int?)pair.Item2); Set(pair.Item1, "ResumeSent", true); Set(pair.Item1, "HasBeenOnline", true); Set(pair.Item1, "Stage", VanillaReconnectStage.Online); }
                Set(Supervisor, "running", true);
                Supervisor.SetPositionSource(pid => pid == 101 ? Position : null, pid => { });
                Supervisor.SetAutobattleResumeTestHook((id, trigger, recovery) => Resumes++);
                Supervisor.RecoveryScreenDiagnosisFactory = (pid, created, cancelled) => { Opens++; return Screen; };
                Screen.Frame = n => Frame(n);
                Set(A, "LastVisualObservation", Background);
            }
            internal VanillaRecoveryScreenFrame Frame(int n, VanillaVisualState state = VanillaVisualState.Unknown)
            { E.Seconds += .15; var frame = NewFrame(n, state); frame.At = E.UtcNow; return frame; }
            internal void Sample(int x = 10) { Position = new VanillaPositionSample(101, Session, E.UtcNow, x, 20, "field", true); }
            internal void Arm()
            {
                Sample(); Watchdog.Observe(101, Position, E.MonotonicNow, E.UtcNow);
                E.Seconds = 30; Sample(); Watchdog.Observe(101, Position, E.MonotonicNow, E.UtcNow);
            }
            internal VanillaMovementWatchdog Watchdog => (VanillaMovementWatchdog)Get(A, "MovementWatchdog");
            internal bool Queue() => (bool)Call(Supervisor, "TryQueueRecoveryScreenDiagnosis", A, E.UtcNow,
                (Func<DateTime>)(() => Epoch.UtcDateTime), Watchdog.StalledSeconds(E.MonotonicNow));
            internal void Probe() { Call(Supervisor, "Probe", A, E.UtcNow); }
            public void Dispose() { Supervisor.Dispose(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        private static void TerminalIntegration()
        {
            foreach (var state in new[] { VanillaVisualState.Disconnected, VanillaVisualState.LoggingOut })
            using (var h = new Harness())
            {
                h.Screen.Frame = n => h.Frame(n, state); h.Arm(); Assert(h.Queue());
                Assert((bool)Get(h.A, "ScriptRunning") && !(bool)Get(h.B, "ScriptRunning"));
                h.E.Work.Dequeue()();
                Assert(h.E.Work.Count == 1 && (bool)Get(h.A, "ClosingForRecovery"));
                h.E.Work.Dequeue()();
                Assert(h.E.Closed.Count == 1 && h.E.Closed[0] == 101 && (int?)Get(h.B, "ProcessId") == 102);
            }
        }
        private static void QueuedCancellation()
        {
            foreach (string change in new[] { "movement", "stop", "creation" })
            using (var h = new Harness())
            {
                h.Arm(); Assert(h.Queue());
                if (change == "movement") { h.E.Seconds = 31; h.Sample(11); }
                else if (change == "creation") h.E.CreationShift = 1;
                else h.Supervisor.Stop();
                h.E.Work.Dequeue()();
                Assert(h.Opens == 0 && h.E.Closed.Count == 0 && !(bool)Get(h.A, "ScriptRunning"));
            }
        }
        private static void ServerClosedIntegration()
        {
            using (var h = new Harness())
            {
                var settings = (VanillaReconnectSettings)Get(h.Supervisor, "settings");
                // Path and credentials are inert sentinels; this test never launches or logs in.
                settings.LaunchExecutable = typeof(VanillaRecoveryScreenDiagnosisTests).Assembly.Location;
                foreach (var account in settings.Accounts)
                {
                    account.UserName = "synthetic-" + account.Id;
                    account.ProtectedPassword = "inert-placeholder-never-decrypted";
                    account.CharacterSlot = 1;
                    account.ProxyNeedsConfiguration = false;
                    foreach (var runtime in new[] { h.A, h.B })
                        if (((VanillaReconnectAccount)Get(runtime, "Account")).Id == account.Id)
                            Set(runtime, "Account", account);
                }
                h.Screen.Frame = n => h.Frame(n, VanillaVisualState.ServerClosed);
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                var outage = (VanillaServerOutagePolicy)Get(h.Supervisor, "serverOutage");
                Assert(outage.Active && outage.Remaining(h.E.MonotonicNow) == TimeSpan.FromMinutes(15));
                Assert((bool)Get(h.A, "ServerOutagePending") && Get(h.A, "NextRecoveryAt") == null
                    && (VanillaReconnectStage)Get(h.A, "Stage") == VanillaReconnectStage.WaitingForServer);
                Assert(!(bool)Get(h.A, "ScriptRunning") && !(bool)Get(h.A, "RecoveryOwned")
                    && !(bool)Get(h.A, "ClosingForRecovery") && h.E.Work.Count == 0 && h.E.Closed.Count == 0);
                Assert((int?)Get(h.B, "ProcessId") == 102 && (bool)Get(h.B, "HasBeenOnline")
                    && (bool)Get(h.B, "ResumeSent") && !(bool)Get(h.B, "ServerOutagePending")
                    && (VanillaReconnectStage)Get(h.B, "Stage") == VanillaReconnectStage.Online);
            }
        }
        private static void MixedDialogPair()
        {
            foreach (var pair in new[]
            {
                Tuple.Create(VanillaVisualState.Disconnected, VanillaVisualState.LoggingOut),
                Tuple.Create(VanillaVisualState.LoggingOut, VanillaVisualState.Disconnected),
                Tuple.Create(VanillaVisualState.Disconnected, VanillaVisualState.Unknown),
                Tuple.Create(VanillaVisualState.ServerClosed, VanillaVisualState.Disconnected)
            })
            using (var h = new Harness())
            {
                h.Screen.Frame = n => h.Frame(n, n == 1 ? pair.Item1 : pair.Item2);
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.E.Work.Count == 0 && h.E.Closed.Count == 0
                    && !(bool)Get(h.A, "ClosingForRecovery") && !(bool)Get(h.A, "RecoveryOwned")
                    && !(bool)Get(h.A, "ScriptRunning")
                    && !((VanillaServerOutagePolicy)Get(h.Supervisor, "serverOutage")).Active);
            }
        }
        private static void ReplacedOperation()
        {
            using (var h = new Harness())
            {
                h.Arm(); Assert(h.Queue());
                Set(h.A, "ResumeOperationGeneration", (int)Get(h.A, "ResumeOperationGeneration") + 1);
                h.E.Work.Dequeue()(); Assert(h.Opens == 0 && (bool)Get(h.A, "ScriptRunning"));
            }
        }
        private static void DisposalLease()
        {
            using (var h = new Harness())
            {
                h.Screen.FailDispose = true; h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(!(bool)Get(h.A, "ScriptRunning") && h.E.Work.Count == 0 && (bool)Get(h.A, "ResumeSent"));
            }
        }
        private static void Throttle()
        {
            using (var h = new Harness())
            {
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                h.E.Seconds = 89; h.Sample(); Assert(!h.Queue());
                h.E.Seconds = 90; h.Sample(); Assert(h.Queue());
                Assert(h.Watchdog.StalledSeconds(h.E.MonotonicNow) == 90 && (bool)Get(h.A, "ResumeSent"));
            }
        }
        private static void Holds()
        {
            foreach (string hold in new[] { "weightManualHolds", "weightCompletedHolds", "sibling" })
            using (var h = new Harness())
            {
                h.Arm();
                if (hold == "sibling") Set(h.B, "ScriptRunning", true);
                else ((HashSet<string>)Get(h.Supervisor, hold)).Add(((VanillaReconnectAccount)Get(h.A, "Account")).Id);
                Assert(!h.Queue() && h.Opens == 0);
            }
            using (var h = new Harness())
            {
                h.Arm(); var owner = h.Supervisor.RegisterTemporaryAction(101);
                Assert(!h.Queue()); h.Supervisor.UnregisterTemporaryAction(owner);
            }
        }
        private static void ModalLatch()
        {
            using (var h = new Harness())
            {
                h.Arm(); Set(h.A, "Visual", VanillaVisualState.ModalDialog);
                Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert((VanillaVisualState)Get(h.A, "Visual") == VanillaVisualState.ModalDialog && h.E.Work.Count == 0);
            }
        }
        private static void InitialModal()
        {
            using (var h = new Harness())
            {
                Set(h.A, "Visual", VanillaVisualState.ModalDialog);
                h.Sample(); h.Probe(); Assert(h.Watchdog.IsArmed && h.E.Work.Count == 0);
                h.E.Seconds = 30; h.Sample();
                h.Background = new VanillaRecoveryVisualObservation(VanillaVisualState.Unknown, false, "nativeError=18");
                h.Probe(); Assert(h.E.Work.Count == 1 && h.E.Closed.Count == 0);
            }
        }
        private static void PublicationFreshness()
        {
            foreach (string change in new[] { "stale", "future", "pid", "creation" })
            using (var h = new Harness())
            {
                h.Screen.Frame = n =>
                {
                    var frame = h.Frame(n, VanillaVisualState.Disconnected);
                    if (change == "stale") frame.At = Epoch.AddMilliseconds(n * 150);
                    else if (change == "future") frame.At = frame.At.AddMinutes(1);
                    else if (change == "pid") frame.Proof = new VanillaVisualInputProof(102, new IntPtr(502), new Size(640, 480), new Point(20, 30), n);
                    else if (n == 2) h.E.CreationShift = 1;
                    return frame;
                };
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.E.Work.Count == 0 && h.E.Closed.Count == 0 && !(bool)Get(h.A, "ScriptRunning"));
            }
        }
        private static void LoginSettle()
        {
            foreach (int settle in new[] { 150, 1500, 6000 })
            {
                int waited = 0;
                var screen = new Screen { Frame = n => NewFrame(n, VanillaVisualState.LoginShell) };
                VanillaRecoveryScreenDiagnosis.Observe(screen, () => false, action => action(), ms => waited += ms, settle);
                Assert(waited == (settle <= VanillaRecoveryScreenDiagnosis.MaximumDurationMs - 1000 ? settle : 150));
            }
        }
        private static void LoginRecovery()
        {
            using (var h = new Harness())
            {
                ((VanillaReconnectSettings)Get(h.Supervisor, "settings")).LoginStableMs = 1500;
                h.Screen.Frame = n =>
                {
                    h.E.Seconds += n == 1 ? .15 : 1.5;
                    var frame = NewFrame(n, VanillaVisualState.LoginShell); frame.At = h.E.UtcNow; return frame;
                };
                h.Arm(); Assert(h.Queue()); h.E.Work.Dequeue()();
                Assert(h.E.Work.Count == 1 && (bool)Get(h.A, "ClosingForRecovery"));
            }
        }
        private static object Get(object instance, string name) => instance.GetType().GetField(name, Flags).GetValue(instance);
        private static void Set(object instance, string name, object value) => instance.GetType().GetField(name, Flags).SetValue(instance, value);
        private static object Call(object instance, string name, params object[] arguments)
        {
            try { return instance.GetType().GetMethod(name, Flags).Invoke(instance, arguments); }
            catch (TargetInvocationException ex) { throw ex.InnerException; }
        }
        private static void Reject(Func<VanillaRecoveryScreenFrame[]> action)
        { try { action(); } catch (InvalidOperationException) { return; } catch (OperationCanceledException) { return; } throw new Exception("Unsafe diagnosis was accepted."); }
        private static void Assert(bool condition, string message = "Diagnosis assertion failed") { if (!condition) throw new Exception(message); }
        private static void Test(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); } }
    }
}
