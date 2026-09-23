using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaNativeRecoveryTests
    {
        internal static int Child(bool ignoreClose)
        {
            using (var form = new Form { Text = "4RTools inert recovery test child", Width = 300, Height = 100, ShowInTaskbar = true })
            using (var timeout = new System.Windows.Forms.Timer { Interval = 30000 })
            {
                if (ignoreClose) form.FormClosing += (s, e) => e.Cancel = true;
                timeout.Tick += (s, e) => Environment.Exit(0);
                timeout.Start();
                Application.Run(form);
            }
            return 0;
        }
        internal static int Run()
        {
            int failed = 0;
            string[] cases = { "graceful", "unresponsive", "identity", "cancel", "immediate", "immediate-environment" };
            foreach (string test in cases)
                try { Check(test); Console.WriteLine("PASS native test-owned child: " + test); }
                catch (Exception e) { failed++; Console.WriteLine("FAIL native test-owned child: " + test + ": " + e); }
            Console.WriteLine("Native recovery: {0} passed; {1} failed. No game processes were observed or controlled.", cases.Length - failed, failed);
            return failed == 0 ? 0 : 1;
        }
        private static void Check(string test)
        {
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                test == "unresponsive" || test.StartsWith("immediate", StringComparison.Ordinal)
                    ? "--recovery-native-probe-ignore-close" : "--recovery-native-probe")
            { UseShellExecute = false, CreateNoWindow = true }))
            {
                if (child == null) throw new Exception("Inert child did not start.");
                try
                {
                    var ready = Stopwatch.StartNew();
                    while (child.MainWindowHandle == IntPtr.Zero && !child.HasExited && ready.ElapsedMilliseconds < 10000)
                    { Thread.Sleep(50); child.Refresh(); }
                    if (child.MainWindowHandle == IntPtr.Zero) throw new Exception("Inert child window did not appear.");
                    DateTime start = child.StartTime.ToUniversalTime();
                    var updateIdentity = VanillaLauncherUpdateProcess.Read(child.Id);
                    if (updateIdentity.Pid != child.Id || updateIdentity.StartedUtc != start
                        || !string.Equals(updateIdentity.Executable, Assembly.GetExecutingAssembly().Location, StringComparison.OrdinalIgnoreCase)
                        || VanillaLauncherUpdateProcess.MetadataAccess != 0x1000)
                        throw new Exception("Limited-query update identity did not match the inert child.");
                    if (test == "identity")
                    {
                        bool rejected = false;
                        try { using (var wrong = new VanillaClientCloseHandle(child.Id, start.AddSeconds(-1))) { } }
                        catch (InvalidOperationException) { rejected = true; }
                        if (!rejected || child.HasExited) throw new Exception("Wrong identity was not rejected safely.");
                        return;
                    }
                    if (test == "immediate-environment")
                    {
                        int ownedSteps = 0;
                        long terminateAtMs = -1;
                        var clock = Stopwatch.StartNew();
                        new VanillaRecoveryRestartEnvironment().CloseClient(child.Id, start, () => false,
                            action => { ownedSteps++; terminateAtMs = clock.ElapsedMilliseconds; action(); }, immediate: true);
                        if (!child.HasExited || ownedSteps != 1 || terminateAtMs < 0 || terminateAtMs >= VanillaClientCloseProtocol.GracefulWaitMs)
                            throw new Exception("Emergency environment did not terminate its owned child before the graceful deadline.");
                        return;
                    }
                    bool immediate = test == "immediate";
                    using (var target = new VanillaClientCloseHandle(child.Id, start, resolveWindow: !immediate))
                    {
                        int closes = 0, kills = 0;
                        long terminateAtMs = -1;
                        var clock = Stopwatch.StartNew();
                        bool cancelled = false;
                        try
                        {
                            VanillaClientCloseProtocol.Run(target.HasExited, () => { closes++; target.CloseWindow(); },
                                () => { kills++; terminateAtMs = clock.ElapsedMilliseconds; target.Terminate(); }, () => test == "cancel", () => clock.Elapsed, Thread.Sleep, immediate);
                        }
                        catch (OperationCanceledException) { cancelled = true; }
                        if (test == "cancel")
                        {
                            if (!cancelled || closes != 0 || kills != 0 || child.HasExited) throw new Exception("Cancelled close acted on the child.");
                        }
                        else if (immediate)
                        {
                            if (!target.HasExited() || closes != 0 || kills != 1 || terminateAtMs < 0 || terminateAtMs >= VanillaClientCloseProtocol.GracefulWaitMs)
                                throw new Exception("Emergency close used the graceful path instead of immediate pinned termination.");
                        }
                        else if (!target.HasExited() || closes != 1 || kills != (test == "unresponsive" ? 1 : 0))
                            throw new Exception("Close/termination did not complete through the expected native path: exited="
                                + target.HasExited() + ", closes=" + closes + ", terminations=" + kills
                                + ", elapsedMs=" + clock.ElapsedMilliseconds + ".");
                    }
                }
                finally
                {
                    // Cleanup is restricted to this test's own directly created Process.
                    if (!child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                }
            }
        }
    }
}
