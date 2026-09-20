using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaLauncherUpdateTests
    {
        private static int passed, failed;
        private static readonly DateTime Epoch = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc);
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        internal static int Run()
        {
            foreach (float scale in new[] { .5f, .8f, 1f, 1.25f, 1.5f, 2f })
            { float s = scale; Test("Launcher update screen scale " + s, () => { using (var b = Image(s)) Assert(VanillaLauncherPatchFrame.Read(b) != null); }); }
            Test("Blank/missing captures do not mean update", UnknownImages);
            Test("Visible GAME START is not an update stall", () => { using (var b = Image(1, .94f, true)) Assert(VanillaLauncherPatchFrame.Read(b) == null); });
            Test("Progress and status changes are observable", ProgressImage);
            Test("Stall requires 60 continuous seconds", () => { var w = new VanillaLauncherPatchWatch(); Warm(w); Assert(Sample(w, 60)); });
            Test("Progress resets the full stall interval", Progress);
            Test("Unknown frame resets stall evidence", UnknownFrame);
            Test("Gaps, rollback and cached timestamps reset evidence", Gaps);
            Test("PID, HWND and creation-time changes reset evidence", Identity);
            Test("One blocker closes before launcher restart", () => ResetCase(1));
            Test("Both blockers and old patchers close before restart", () => ResetCase(2));
            Test("No blockers: no reset", () => { var h = new H(0); Assert(!h.Run() && h.Closed.Count == 0); });
            Test("Progress during preflight: no reset", () => { var h = new H { Blocked = false }; Assert(!h.Run() && h.Closed.Count == 0); });
            Test("Replaced process during preflight: no close", ChangedPreflight);
            Test("Wrong install, excess clients and unknown identity rejected", InvalidTargets);
            Test("STOP at each reset phase prevents further closes/restart", Cancellation);
            Test("Close denial never authorizes restart", () => { var h = new H(); h.BeforeClose = () => { throw new InvalidOperationException("denied"); }; Throws<InvalidOperationException>(() => h.Run()); Assert(h.Closed.Count == 0); });
            Test("New process during reset prevents restart", NewProcess);
            Test("Direct-game fallback is refused", RequireLauncher);
            foreach (string mode in new[] { "success", "stop", "generation", "session", "busy", "cooldown" })
            { string m = mode; Test("Supervisor update lease: " + m, () => SupervisorCase(m)); }
            Console.WriteLine("Launcher update: {0} passed; {1} failed. Synthetic images and fake process lifecycle only.", passed, failed);
            return failed;
        }
        private static Bitmap Image(float scale = 1, float fill = .94f, bool ready = false, bool text = false)
        {
            var b = new Bitmap((int)(780 * scale), (int)(327 * scale));
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.FromArgb(100, 190, 240));
                g.FillRectangle(Brushes.White, 0, b.Height * .79f, b.Width, b.Height * .21f);
                using (var yellow = new SolidBrush(Color.FromArgb(255, 210, 40)))
                {
                    g.FillRectangle(yellow, b.Width * .03f, b.Height * .925f, b.Width * fill, b.Height * .035f);
                    if (ready) g.FillRectangle(yellow, b.Width * .4f, b.Height * .70f, b.Width * .2f, b.Height * .11f);
                }
                if (text) g.FillRectangle(Brushes.Gray, b.Width * .1f, b.Height * .85f, b.Width * .13f, b.Height * .035f);
            }
            return b;
        }
        private static void UnknownImages()
        {
            Assert(VanillaLauncherPatchFrame.Read(null) == null);
            foreach (var color in new[] { Color.Black, Color.White, Color.Yellow, Color.LightBlue })
                using (var b = new Bitmap(780, 327)) { using (var g = Graphics.FromImage(b)) g.Clear(color); Assert(VanillaLauncherPatchFrame.Read(b) == null); }
        }
        private static void ProgressImage()
        {
            using (var a = Image()) using (var b = Image(1, .45f)) using (var c = Image(1, .94f, false, true))
            {
                var aa = VanillaLauncherPatchFrame.Read(a); var bb = VanillaLauncherPatchFrame.Read(b); var cc = VanillaLauncherPatchFrame.Read(c);
                Assert(aa != null && bb != null && cc != null && aa.Signature != bb.Signature && aa.Signature != cc.Signature);
            }
        }
        private static bool Sample(VanillaLauncherPatchWatch w, int second, string signature = "same", int pid = 1, int hwnd = 2, long birth = 3)
        { return w.Observe(pid, new IntPtr(hwnd), signature == null ? null : new VanillaLauncherPatchFrame(signature), TimeSpan.FromSeconds(second), birth); }
        private static void Warm(VanillaLauncherPatchWatch w) { for (int s = 0; s < 60; s++) Assert(!Sample(w, s)); }
        private static void Progress()
        { var w = new VanillaLauncherPatchWatch(); Warm(w); for (int s = 60; s < 120; s++) Assert(!Sample(w, s, "new")); Assert(Sample(w, 120, "new")); }
        private static void UnknownFrame()
        { var w = new VanillaLauncherPatchWatch(); Warm(w); Assert(!Sample(w, 60, null)); for (int s = 61; s < 121; s++) Assert(!Sample(w, s)); Assert(Sample(w, 121)); }
        private static void Gaps()
        { foreach (int t in new[] { 59, 2, 66 }) { var w = new VanillaLauncherPatchWatch(); Warm(w); Assert(!Sample(w, t)); } }
        private static void Identity()
        { for (int i = 0; i < 3; i++) { var w = new VanillaLauncherPatchWatch(); Warm(w); Assert(!Sample(w, 60, pid: i == 0 ? 4 : 1, hwnd: i == 1 ? 4 : 2, birth: i == 2 ? 4 : 3)); } }
        private static VanillaLauncherUpdateProcess P(int pid, bool game = false, string dir = "4R-reset-test", int seconds = 0)
        { return new VanillaLauncherUpdateProcess(pid, Epoch.AddSeconds(seconds), Path.Combine(Path.GetTempPath(), dir, game ? "Vanilla MMO.exe" : "Vanilla Launcher.exe"), game); }
        private sealed class H
        {
            internal readonly List<VanillaLauncherUpdateProcess> Alive = new List<VanillaLauncherUpdateProcess>();
            internal readonly List<int> Closed = new List<int>();
            internal bool Cancelled, Blocked = true;
            internal int Snapshots;
            internal Action OnBegin, BeforeClose, OnSnapshot;
            internal H(int games = 2) { Alive.Add(P(10)); for (int i = 1; i <= games; i++) Alive.Add(P(i, true)); }
            internal VanillaLauncherUpdateProcess[] Snapshot() { Snapshots++; OnSnapshot?.Invoke(); return Alive.ToArray(); }
            internal bool Run()
            {
                return VanillaLauncherUpdateReset.Run(P(10), () => Blocked, Snapshot,
                    p => { BeforeClose?.Invoke(); if (Cancelled) throw new OperationCanceledException(); Assert(Alive.Any(p.Same)); Closed.Add(p.Pid); Alive.RemoveAll(p.Same); },
                    p => { }, () => Cancelled, () => OnBegin?.Invoke());
            }
        }
        private static void ResetCase(int games)
        { var h = new H(games); h.Alive.Add(P(11)); Assert(h.Run() && h.Alive.Count == 0); Assert(h.Closed.Take(2).SequenceEqual(new[] { 10, 11 }) && h.Closed.Count == games + 2); }
        private static void ChangedPreflight()
        { var h = new H(); h.OnSnapshot = () => { if (h.Snapshots == 2) h.Alive[1] = P(1, true, seconds: 1); }; Throws<InvalidOperationException>(() => h.Run()); Assert(h.Closed.Count == 0); }
        private static void InvalidTargets()
        {
            for (int i = 0; i < 4; i++)
            {
                var h = new H();
                if (i == 0) h.Alive.Add(P(3, true));
                if (i == 1) h.Alive[1] = P(1, true, "another-installation");
                if (i == 2) h.Alive[1] = new VanillaLauncherUpdateProcess(1, Epoch, null, true);
                if (i == 3) h.Alive[0] = P(10, seconds: 1);
                Throws<InvalidOperationException>(() => h.Run()); Assert(h.Closed.Count == 0);
            }
        }
        private static void Cancellation()
        {
            for (int phase = 0; phase < 4; phase++)
            {
                var h = new H(); int p = phase;
                if (p == 0) h.Cancelled = true;
                if (p == 1) h.OnBegin = () => h.Cancelled = true;
                if (p >= 2) h.BeforeClose = () => { if (h.Closed.Count == p - 1) h.Cancelled = true; };
                Throws<OperationCanceledException>(() => h.Run()); Assert(h.Closed.Count == Math.Max(0, p - 1));
            }
        }
        private static void NewProcess()
        { var h = new H(); h.OnSnapshot = () => { if (h.Snapshots == 3) h.Alive.Add(P(3, true)); }; Throws<InvalidOperationException>(() => h.Run()); Assert(h.Alive.Count == 1 && h.Alive[0].Pid == 3); }
        private static void RequireLauncher()
        {
            string root = Path.Combine(Path.GetTempPath(), "4R-launcher-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string client = Path.Combine(root, "Vanilla MMO.exe"), launcher = Path.Combine(root, "Vanilla Launcher.exe");
                Throws<InvalidOperationException>(() => VanillaPatcherLauncher.RequireLauncher(client));
                File.WriteAllBytes(launcher, new byte[] { 0 });
                Assert(VanillaPatcherLauncher.RequireLauncher(client) == launcher && VanillaPatcherLauncher.RequireLauncher(launcher) == launcher);
            }
            finally { Directory.Delete(root, true); }
        }
        private sealed class Env : IVanillaRecoveryRestartEnvironment
        {
            internal readonly H H = new H(1);
            internal Action Before;
            public DateTimeOffset UtcNow { get { return Epoch; } }
            public TimeSpan MonotonicNow { get { return TimeSpan.FromSeconds(1); } }
            public DateTime GetStartTimeUtc(int pid) { return Epoch; }
            public void Queue(Action work) { throw new Exception("Unexpected queue"); }
            public void CloseClient(int pid, DateTime birth, Func<bool> cancelled, Action<Action> owned)
            {
                Before?.Invoke(); if (cancelled()) throw new OperationCanceledException();
                owned(() => { Assert(H.Alive.Any(p => p.Pid == pid && p.StartedUtc == birth)); H.Closed.Add(pid); H.Alive.RemoveAll(p => p.Pid == pid); });
            }
        }
        private static void SupervisorCase(string mode)
        {
            string root = Path.Combine(Path.GetTempPath(), "4R-update-test-" + Guid.NewGuid().ToString("N")); var env = new Env();
            try
            {
                using (var supervisor = new VanillaReconnectSupervisor(root, env))
                {
                    var rows = (IDictionary)Get(supervisor, "runtimes");
                    object owner = rows[supervisor.Settings.Accounts[0].Id], sibling = rows[supervisor.Settings.Accounts[1].Id];
                    Set(supervisor, "running", true); Set(supervisor, "resumeVerificationGeneration", 7);
                    Set(owner, "ScriptRunning", true); Set(owner, "RecoveryOwned", true); Set(owner, "ResumeOperationGeneration", 7);
                    Set(sibling, "ProcessId", (int?)1); Set(sibling, "CharacterSession", (Guid?)Guid.NewGuid());
                    Set(sibling, "HasBeenOnline", true); Set(sibling, "Stage", VanillaReconnectStage.Online);
                    if (mode == "busy") Set(sibling, "ScriptRunning", true);
                    if (mode == "cooldown") Set(supervisor, "nextLauncherUpdateReset", (TimeSpan?)TimeSpan.FromMinutes(10));
                    env.Before = () =>
                    {
                        if (mode == "stop") supervisor.Stop();
                        if (mode == "generation") Set(supervisor, "resumeVerificationGeneration", 8);
                        if (mode == "session") Set(sibling, "CharacterSession", (Guid?)Guid.NewGuid());
                        if (mode == "success")
                        { Assert((bool)Get(supervisor, "launcherUpdateResetRunning") && (bool)Get(owner, "ScriptRunning") && (bool)Get(owner, "RecoveryOwned")); Call(supervisor, "TickLocked"); }
                    };
                    Func<bool> run = () => (bool)Call(supervisor, "RecoverLauncherUpdate", owner, 7, (Func<bool>)(() => !supervisor.IsRunning),
                        P(10), (Func<bool>)(() => true), (Func<VanillaLauncherUpdateProcess[]>)(() => env.H.Snapshot()));
                    if (mode == "stop" || mode == "generation" || mode == "session")
                    { Throws<OperationCanceledException>(() => run()); Assert(env.H.Closed.Count == 0); }
                    else if (mode == "busy" || mode == "cooldown") Assert(!run() && env.H.Closed.Count == 0);
                    else
                    {
                        Assert(run() && env.H.Closed.SequenceEqual(new[] { 10, 1 }));
                        Assert(Get(sibling, "ProcessId") == null && !(bool)Get(sibling, "RecoveryOwned"));
                        Assert((bool)Get(owner, "ScriptRunning") && (bool)Get(owner, "RecoveryOwned"));
                        Assert((int)Get(supervisor, "launcherUpdateResetSerial") == 1);
                        env.H.Alive.Add(P(10)); env.H.Alive.Add(P(1, true)); Assert(!run() && env.H.Closed.Count == 2);
                    }
                    Assert(!(bool)Get(supervisor, "launcherUpdateResetRunning"));
                }
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
        private static object Get(object o, string name) { return o.GetType().GetField(name, Flags).GetValue(o); }
        private static void Set(object o, string name, object value) { o.GetType().GetField(name, Flags).SetValue(o, value); }
        private static object Call(object o, string name, params object[] args)
        { try { return o.GetType().GetMethod(name, Flags).Invoke(o, args); } catch (TargetInvocationException ex) { throw ex.InnerException; } }
        private static void Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T) { return; } throw new Exception("Expected " + typeof(T).Name); }
        private static void Assert(bool value) { if (!value) throw new Exception("Launcher update assertion failed."); }
        private static void Test(string name, Action test)
        { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); } }
    }
}
