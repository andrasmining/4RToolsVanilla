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
            foreach (string test in new[] { "graceful", "unresponsive", "identity", "cancel" })
                try { Check(test); Console.WriteLine("PASS native test-owned child: " + test); }
                catch (Exception e) { failed++; Console.WriteLine("FAIL native test-owned child: " + test + ": " + e); }
            Console.WriteLine("Native recovery: {0} passed; {1} failed. No game processes were observed or controlled.", 4 - failed, failed);
            return failed == 0 ? 0 : 1;
        }
        private static void Check(string test)
        {
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location,
                test == "unresponsive" ? "--recovery-native-probe-ignore-close" : "--recovery-native-probe")
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
                    using (var target = new VanillaClientCloseHandle(child.Id, start))
                    {
                        int closes = 0, kills = 0;
                        var clock = Stopwatch.StartNew();
                        bool cancelled = false;
                        try
                        {
                            VanillaClientCloseProtocol.Run(target.HasExited, () => { closes++; target.CloseWindow(); },
                                () => { kills++; target.Terminate(); }, () => test == "cancel", () => clock.Elapsed, Thread.Sleep);
                        }
                        catch (OperationCanceledException) { cancelled = true; }
                        if (test == "cancel")
                        {
                            if (!cancelled || closes != 0 || kills != 0 || child.HasExited) throw new Exception("Cancelled close acted on the child.");
                        }
                        else if (!target.HasExited() || closes != 1 || kills != (test == "unresponsive" ? 1 : 0))
                            throw new Exception("Close/termination did not complete through the expected native path.");
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
