using System;
using System.IO;
using System.Reflection;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaPatcherLauncherTests
    {
        private static int passed;
        private static int failed;

        internal static int Run()
        {
            Test("Patcher executable detection is exact and case-insensitive", PatcherDetection);
            Test("Patcher beside Vanilla client is preferred when present", PreferAdjacentPatcher);
            Test("GAME START visual detector is available", VisualDetectorAvailable);
            Test("Launcher actions are deliberately paced", Pacing);
            Test("GAME START visual confirmation rejects a moving candidate", StableCandidate);
            Test("Production capture has no PrintWindow import", NoBackgroundCaptureImport);
            Test("Launcher capture rejects an unavailable foreground window", MissingForeground);
            Console.WriteLine("Patcher launcher: {0} passed; {1} failed. No processes were started.", passed, failed);
            return failed;
        }

        private static void NoBackgroundCaptureImport()
        {
            foreach (Type type in typeof(VanillaDiscovery).Assembly.GetTypes())
                foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Instance
                    | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    var import = (System.Runtime.InteropServices.DllImportAttribute)Attribute.GetCustomAttribute(
                        method, typeof(System.Runtime.InteropServices.DllImportAttribute));
                    Assert(import == null || !string.Equals(import.EntryPoint, "PrintWindow", StringComparison.OrdinalIgnoreCase),
                        "Background window rendering is prohibited: " + type.FullName + "." + method.Name);
                }
        }

        private static void MissingForeground()
        {
            MethodInfo capture = LauncherType().GetMethod("CaptureLauncherForeground", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(capture != null && capture.Invoke(null, new object[] { 1, IntPtr.Zero }) == null,
                "A missing owned foreground window must not produce a launcher image.");
            MethodInfo discoveryCapture = typeof(VanillaDiscovery).GetMethod("Capture", BindingFlags.Static | BindingFlags.NonPublic);
            string path = Path.Combine(Path.GetTempPath(), "4rtools-no-background-" + Guid.NewGuid().ToString("N") + ".png");
            string detail = (string)discoveryCapture.Invoke(null, new object[] { int.MaxValue, path, false });
            Assert(detail.Contains("Screenshot skipped") && !File.Exists(path),
                "Background discovery must continue memory-only without opening/capturing the selected process.");
        }

        private static void PatcherDetection()
        {
            Type type = LauncherType();
            MethodInfo method = type.GetMethod("IsPatcher", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(method != null, "IsPatcher helper is missing.");
            Assert((bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\patcher.exe" }), "patcher.exe must use launcher mode.");
            Assert((bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\PATCHER.EXE" }), "Patcher detection must ignore case.");
            Assert((bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\Vanilla Launcher.exe" }), "Vanilla Launcher.exe must use GAME START launcher mode.");
            Assert(!(bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\Vanilla MMO.exe" }), "Direct game executable must not be treated as patcher.exe.");
        }

        private static void PreferAdjacentPatcher()
        {
            string root = Path.Combine(Path.GetTempPath(), "4rtools-patcher-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string client = Path.Combine(root, "Vanilla MMO.exe");
                string patcher = Path.Combine(root, "patcher.exe");
                File.WriteAllBytes(client, new byte[] { 1 });

                Type type = LauncherType();
                MethodInfo method = type.GetMethod("PreferPatcherBesideClient", BindingFlags.Static | BindingFlags.NonPublic);
                Assert(method != null, "PreferPatcherBesideClient helper is missing.");
                Equal(client, (string)method.Invoke(null, new object[] { client }), "Without patcher.exe, keep the client path.");

                File.WriteAllBytes(patcher, new byte[] { 1 });
                Equal(patcher, (string)method.Invoke(null, new object[] { client }), "Adjacent patcher.exe must be preferred.");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
            }
        }

        private static void Pacing()
        {
            Type type = LauncherType();
            Equal(15000, (int)type.GetField("DefaultRetryMs", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue(),
                "Launcher retry delay must leave time for Gepard/client startup.");
            Equal(2500, (int)type.GetField("LauncherSettleMs", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue(),
                "Launcher must settle before the first GAME START action.");
            Equal(3000, (int)type.GetField("LauncherActivationTimeoutMs", BindingFlags.Static | BindingFlags.NonPublic).GetRawConstantValue(),
                "Launcher foreground acquisition must be bounded and fail closed.");
        }

        private static void StableCandidate()
        {
            MethodInfo method = LauncherType().GetMethod("SameGameStartCandidate", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(method != null, "Stable GAME START confirmation helper is missing.");
            Assert((bool)method.Invoke(null, new object[] { .50d, .765d, .512d, .755d }),
                "Small detector jitter should remain one candidate.");
            Assert(!(bool)method.Invoke(null, new object[] { .50d, .765d, .56d, .70d }),
                "A moving visual candidate must not authorize a click.");
        }

        private static void VisualDetectorAvailable()
        {
            MethodInfo method = LauncherType().GetMethod("TryFindGameStart", BindingFlags.Static | BindingFlags.NonPublic);
            Assert(method != null, "TryFindGameStart helper is missing.");
            object[] args = { null, 0d, 0d, null };
            Assert(!(bool)method.Invoke(null, args), "A null launcher image must not produce a GAME START candidate.");
        }
        private static Type LauncherType()
        {
            Type type = typeof(VanillaReconnectSettings).Assembly.GetType("_4RTools.Model.Vanilla.VanillaPatcherLauncher", true);
            return type;
        }

        private static void Test(string name, Action test)
        {
            try
            {
                test();
                passed++;
                Console.WriteLine("PASS " + name);
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine("FAIL " + name + ": " + ex);
            }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }

        private static void Equal(int expected, int actual, string message)
        {
            if (expected != actual) throw new Exception(message + " Expected: " + expected + "; actual: " + actual);
        }

        private static void Equal(string expected, string actual, string message)
        {
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
                throw new Exception(message + " Expected: " + expected + "; actual: " + actual);
        }
    }
}
