using System;
using System.IO;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaSessionLogTests
    {
        private static int passed, failed;
        internal static int Run()
        {
            Test("Session log uses a fresh named file", FreshPath);
            Test("Session log rotates at configured size", Rotation);
            Test("Single oversized session payload is split below the cap", OversizedSessionPayload);
            Test("Legacy reconnect.log is archived", LegacyArchive);
            Test("Shared fixed log starts a fresh timestamped session", SharedFreshSession);
            Test("Shared fixed log rotates before the configured cap", SharedBoundedRotation);
            Test("Oversized legacy fixed logs are split below the cap", SharedOversizedArchive);
            Test("Every app start owns a distinct timestamped debug file", DebugSessionIsolation);
            Test("Per-start debug parts never exceed the configured cap", DebugSessionHardCap);
            Console.WriteLine("Session logging: {0} passed; {1} failed.", passed, failed);
            return failed;
        }

        private static void FreshPath()
        {
            string root = Temp();
            try
            {
                object log = Create(root, 2048, 8192, "session-a");
                string path = Current(log);
                Assert(Path.GetFileName(path).StartsWith("reconnect-session-a", StringComparison.Ordinal), "Unexpected log name: " + path);
                Assert(File.Exists(path), "Session log must exist.");
            }
            finally { Cleanup(root); }
        }

        private static void Rotation()
        {
            string root = Temp();
            try
            {
                object log = Create(root, 1024, 8192, "rotation");
                MethodInfo write = LogType().GetMethod("WriteLine", BindingFlags.Instance | BindingFlags.NonPublic);
                string first = Current(log);
                for (int i = 0; i < 20; i++) write.Invoke(log, new object[] { new string('x', 180) });
                string current = Current(log);
                Assert(!string.Equals(first, current, StringComparison.OrdinalIgnoreCase), "Log should rotate to another part.");
                Assert(new FileInfo(current).Length < 1400, "Rotated part should remain bounded.");
            }
            finally { Cleanup(root); }
        }

        private static void OversizedSessionPayload()
        {
            string root = Temp();
            try
            {
                object log = Create(root, 1024, 16384, "oversized-line");
                MethodInfo write = LogType().GetMethod("WriteLine", BindingFlags.Instance | BindingFlags.NonPublic);
                write.Invoke(log, new object[] { new string('q', 5000) });
                string dir = Path.Combine(root, "Logs");
                string[] files = Directory.GetFiles(dir, "reconnect-oversized-line*.log");
                Assert(files.Length >= 5, "Oversized reconnect payload did not split across parts.");
                foreach (string file in files)
                    Assert(new FileInfo(file).Length <= 1024, "Reconnect part exceeded configured max: " + file);
            }
            finally { Cleanup(root); }
        }

        private static void LegacyArchive()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs"); Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "reconnect.log"), "legacy");
                Create(root, 2048, 8192, "archive");
                Assert(!File.Exists(Path.Combine(dir, "reconnect.log")), "Legacy fixed log should be moved.");
                Assert(Directory.GetFiles(dir, "reconnect-legacy-*.log").Length == 1, "Legacy archive is missing.");
            }
            finally { Cleanup(root); }
        }

        private static void SharedFreshSession()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs"); Directory.CreateDirectory(dir);
                string current = Path.Combine(dir, "debug.log");
                File.WriteAllText(current, "old-session");
                VanillaLogRotation.StartNewSession(current, "debug", 1024, 8192, 20);
                Assert(!File.Exists(current), "A new application session must not append to the previous debug.log.");
                string[] archived = Directory.GetFiles(dir, "debug-*.log");
                Assert(archived.Length == 1, "Previous debug session was not timestamp-archived.");
                Assert(File.ReadAllText(archived[0]) == "old-session", "Archived debug session content changed.");

                VanillaLogRotation.Append(current, "debug", "new-session\r\n", 1024, 8192, 20);
                Assert(File.Exists(current) && File.ReadAllText(current).Contains("new-session"),
                    "Fresh current debug.log was not created for the new session.");
            }
            finally { Cleanup(root); }
        }

        private static void SharedBoundedRotation()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs"); Directory.CreateDirectory(dir);
                string current = Path.Combine(dir, "memory-access.log");
                for (int i = 0; i < 30; i++)
                    VanillaLogRotation.Append(current, "memory-access", new string('x', 180) + "\r\n", 1024, 8192, 20);

                string[] files = Directory.GetFiles(dir, "memory-access*.log");
                Assert(files.Length > 1, "Shared log never rotated.");
                foreach (string file in files)
                    Assert(new FileInfo(file).Length <= 1024, "Rotated log exceeded configured max: " + file);
            }
            finally { Cleanup(root); }
        }

        private static void SharedOversizedArchive()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs"); Directory.CreateDirectory(dir);
                string current = Path.Combine(dir, "debug.log");
                File.WriteAllText(current, new string('z', 5000));
                VanillaLogRotation.StartNewSession(current, "debug", 1024, 16384, 20);
                Assert(!File.Exists(current), "Oversized previous debug.log remained active.");
                string[] files = Directory.GetFiles(dir, "debug-*.log");
                Assert(files.Length >= 5, "Oversized previous log was not split into bounded archive parts.");
                foreach (string file in files)
                    Assert(new FileInfo(file).Length <= 1024, "Oversized archive part exceeded configured max: " + file);
            }
            finally { Cleanup(root); }
        }

        private static void DebugSessionIsolation()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs");
                var first = new VanillaDebugSessionLog(dir, "20260919-223000-000-p1234", 1024, 8192, 20);
                first.WriteText("first-session\r\n");
                var second = new VanillaDebugSessionLog(dir, "20260919-223000-000-p1234", 1024, 8192, 20);
                second.WriteText("second-session\r\n");

                Assert(!string.Equals(first.CurrentPath, second.CurrentPath, StringComparison.OrdinalIgnoreCase),
                    "Two app starts must never share one live debug file.");
                Assert(Path.GetFileName(first.CurrentPath).StartsWith("debug-20260919-223000-000-p1234", StringComparison.Ordinal),
                    "Debug session filename must carry the start timestamp/PID token.");
                Assert(!File.Exists(Path.Combine(dir, "debug.log")),
                    "Per-start debug logging must not recreate a shared live debug.log.");
                Assert(File.ReadAllText(first.CurrentPath).Contains("first-session")
                    && !File.ReadAllText(first.CurrentPath).Contains("second-session"),
                    "Second app session appended into the first app's debug file.");
            }
            finally { Cleanup(root); }
        }

        private static void DebugSessionHardCap()
        {
            string root = Temp();
            try
            {
                string dir = Path.Combine(root, "Logs");
                var log = new VanillaDebugSessionLog(dir, "20260919-223100-000-p5678", 1024, 16384, 20);
                log.WriteText(new string('d', 5000));
                string[] files = Directory.GetFiles(dir, "debug-20260919-223100-000-p5678*.log");
                Assert(files.Length >= 5, "Large debug session did not rotate into bounded parts.");
                foreach (string file in files)
                    Assert(new FileInfo(file).Length <= 1024, "Debug part exceeded configured hard cap: " + file);
            }
            finally { Cleanup(root); }
        }

        private static Type LogType() { return typeof(VanillaReconnectSettings).Assembly.GetType("_4RTools.Model.Vanilla.VanillaSessionLog", true); }
        private static object Create(string root, long maxFile, long maxDir, string token)
        {
            return Activator.CreateInstance(LogType(), BindingFlags.Instance | BindingFlags.NonPublic, null,
                new object[] { root, maxFile, maxDir, token }, null);
        }
        private static string Current(object log)
        {
            return (string)LogType().GetProperty("CurrentPath", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(log, null);
        }
        private static string Temp() { string p = Path.Combine(Path.GetTempPath(), "4rtools-log-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(p); return p; }
        private static void Cleanup(string root) { try { Directory.Delete(root, true); } catch { } }
        private static void Test(string name, Action test)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }
        private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    }
}
