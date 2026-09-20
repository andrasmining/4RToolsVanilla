using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using _4RTools.Forms;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaReconnectRegressionTests
    {
        private static int passed, failed;

        internal static int Run()
        {
            Test("Fresh reconnect settings contain exactly two defaults", FreshDefaults);
            Test("No-movement restart threshold defaults to three minutes and validates", MovementRestartSetting);
            Test("Reconnect settings clone never appends defaults", CloneDoesNotDuplicate);
            Test("Legacy duplicated settings keep the two configured accounts", LegacyDuplicateMigration);
            Test("Runtime status contains only current configured accounts", StatusTracksCurrentSettings);
            Test("Vanilla Launcher.exe uses GAME START launcher mode", VanillaLauncherName);
            Test("Legacy version folders migrate into one persistent Profiles root", PersistentDataMigration);
            Test("Updater only accepts strictly newer semantic versions", UpdaterVersionComparison);
            Test("Legacy login anchors migrate to verified field centers", LoginAnchorMigration);
            Test("Reconnect backoff doubles and caps at one hour", ExponentialBackoff);
            Test("Recovery ownership blocks parallel client workflows", SequentialRecoveryGate);
            Test("Supervisor minimizes only healthy gameplay clients", ManagedMinimizePolicy);
            Test("Launcher PID binding closes the duplicate-launch race", SequentialLaunchBinding);
            Test("Sequential startup advances only after gameplay resume and minimize", HardenedStartupAdvanceGate);
            Test("Existing client startup accepts verified memory when visual is Unknown", ExistingClientMemoryGate);
            Test("Host diagnostics include session display and window context", HostDiagnosticsBundle);
            Test("Gepard splash and GDI hook helpers are never interactive targets", BootstrapHelpersAreTransient);
            Test("Real Vanilla game window outranks generic windows", GameWindowCandidateRanking);
            Test("Minimized Vanilla game window stays eligible for restore", MinimizedGameWindowCandidate);
            Test("Recovery workspace uses split layout on ordinary Full-HD widths", ResponsiveRecoveryBreakpoint);
            Test("Fleet strip stays minimal across common desktop heights", ResponsiveFleetHeight);
            Test("Account table reserves four rows plus one blank-row worth of breathing room", ResponsiveAccountRows);
            Test("Saved account catalog keeps extra profiles but limits enabled clients", SavedAccountCatalog);
            Console.WriteLine("Reconnect regressions: {0} passed; {1} failed. No live process was controlled.", passed, failed);
            return failed;
        }

        private static void FreshDefaults()
        {
            string root = Temp();
            try
            {
                var value = new VanillaReconnectStore(root).Load();
                Assert(value.Accounts.Count == 2, "Fresh settings need two rows.");
            }
            finally { Delete(root); }
        }

        private static void MovementRestartSetting()
        {
            var value = VanillaReconnectSettings.CreateDefault();
            Assert(value.MovementRestartSeconds == 180, "Default no-movement restart threshold must be 180 seconds.");
            value.MovementRestartSeconds = 60; value.Validate();
            value.MovementRestartSeconds = 3600; value.Validate();
            value.MovementRestartSeconds = 59;
            bool lowRejected = false;
            try { value.Validate(); } catch (ArgumentException) { lowRejected = true; }
            Assert(lowRejected, "Restart threshold below 60 seconds was accepted.");
            value.MovementRestartSeconds = 3601;
            bool highRejected = false;
            try { value.Validate(); } catch (ArgumentException) { highRejected = true; }
            Assert(highRejected, "Restart threshold above one hour was accepted.");
        }

        private static void CloneDoesNotDuplicate()
        {
            var value = VanillaReconnectSettings.CreateDefault();
            value.Accounts[0].Label = "first";
            value.Accounts[0].UserName = "user-a";
            value.Accounts[1].Label = "second";
            value.Accounts[1].UserName = "user-b";
            var clone = value.Clone();
            Assert(clone.Accounts.Count == 2 && clone.Accounts[0].Label == "first" && clone.Accounts[1].Label == "second",
                "Clone changed or duplicated rows.");
        }

        private static void LegacyDuplicateMigration()
        {
            string root = Temp();
            try
            {
                var legacy = VanillaReconnectSettings.CreateDefault();
                legacy.Accounts.Add(new VanillaReconnectAccount { Label = "first", UserName = "user-a", CharacterSlot = 4 });
                legacy.Accounts.Add(new VanillaReconnectAccount { Label = "second", UserName = "user-b", CharacterSlot = 1 });
                string dir = Path.Combine(root, "VanillaReconnect");
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "reconnect.json"), JsonConvert.SerializeObject(legacy, Formatting.Indented));
                var loaded = new VanillaReconnectStore(root).Load();
                Assert(loaded.Accounts.Count == 2 && loaded.Accounts[0].Label == "first" && loaded.Accounts[1].Label == "second",
                    "Configured rows did not win over synthetic defaults.");
            }
            finally { Delete(root); }
        }

        private static void StatusTracksCurrentSettings()
        {
            string root = Temp();
            try
            {
                using (var supervisor = new VanillaReconnectSupervisor(root))
                {
                    var value = supervisor.Settings;
                    value.Accounts.RemoveAt(1);
                    supervisor.Apply(value, false);
                    var rows = supervisor.Statuses();
                    Assert(rows.Count == 1 && rows[0].Label == "Client 1", "Removed row lingered in status.");
                }
            }
            finally { Delete(root); }
        }

        private static void VanillaLauncherName()
        {
            Type type = typeof(VanillaReconnectSettings).Assembly.GetType("_4RTools.Model.Vanilla.VanillaPatcherLauncher", true);
            MethodInfo method = type.GetMethod("IsPatcher", BindingFlags.Static | BindingFlags.NonPublic);
            Assert((bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\Vanilla Launcher.exe" }),
                "Vanilla Launcher.exe was not recognized.");
            Assert((bool)method.Invoke(null, new object[] { @"C:\Games\Vanilla RO\patcher.exe" }), "patcher.exe regressed.");
        }

        private static void LoginAnchorMigration()
        {
            var legacy = VanillaReconnectSettings.CreateDefault();
            legacy.Anchors.UserNameY = 0.66;
            legacy.Anchors.PasswordY = 0.685;
            var migrated = legacy.Clone();
            Assert(Math.Abs(migrated.Anchors.UserNameY - 0.677) < 0.0001
                && Math.Abs(migrated.Anchors.PasswordY - 0.697) < 0.0001,
                "Legacy login-field coordinates were not migrated.");
        }

        private static void ExponentialBackoff()
        {
            Assert(VanillaRecoveryPolicy.RetryDelayMs(1, 30000, 3600000) == 30000, "First failure should wait 30 seconds.");
            Assert(VanillaRecoveryPolicy.RetryDelayMs(2, 30000, 3600000) == 60000, "Second failure should wait 60 seconds.");
            Assert(VanillaRecoveryPolicy.RetryDelayMs(3, 30000, 3600000) == 120000, "Third failure should wait 120 seconds.");
            Assert(VanillaRecoveryPolicy.RetryDelayMs(8, 30000, 3600000) == 3600000, "Retry interval must cap at one hour.");
            Assert(VanillaRecoveryPolicy.RetryDelayMs(20, 30000, 3600000) == 3600000, "Retry interval exceeded one-hour cap.");
        }

        private static void SequentialRecoveryGate()
        {
            Assert(!VanillaRecoveryPolicy.BlocksParallelRecovery(false, false), "Idle client unexpectedly blocks recovery.");
            Assert(VanillaRecoveryPolicy.BlocksParallelRecovery(true, false), "Recovery owner did not block parallel recovery.");
            Assert(VanillaRecoveryPolicy.BlocksParallelRecovery(false, true), "Active script did not block parallel recovery.");
        }

        private static void ManagedMinimizePolicy()
        {
            Assert(VanillaReconnectSupervisor.ShouldKeepClientMinimized(VanillaReconnectStage.Online, VanillaVisualState.Unknown),
                "Online supervised clients must be minimized.");
            Assert(VanillaReconnectSupervisor.ShouldKeepClientMinimized(VanillaReconnectStage.WaitingForGameplay, VanillaVisualState.Gameplay),
                "Confirmed gameplay must be minimized while Online settles.");
            Assert(!VanillaReconnectSupervisor.ShouldKeepClientMinimized(VanillaReconnectStage.LoggingIn, VanillaVisualState.LoginShell),
                "The client receiving login input must stay available.");
        }

        private static void SequentialLaunchBinding()
        {
            int pid;
            Assert(VanillaReconnectSupervisor.TryParseLaunchedVanillaPid(
                    "Monk: Patcher started Vanilla MMO (PID 26220).", true, out pid) && pid == 26220,
                "Patcher-reported game PID was not parsed.");
            Assert(!VanillaReconnectSupervisor.TryParseLaunchedVanillaPid(
                    "Monk: Launcher start requested: exe='Vanilla Launcher.exe', startedPID=29128, preExistingVanillaPIDs=[]", true, out pid),
                "Patcher mode incorrectly treated the launcher PID as the game PID.");
            Assert(VanillaReconnectSupervisor.TryParseLaunchedVanillaPid(
                    "Monk: Launcher start requested: exe='Vanilla MMO.exe', startedPID=31415, preExistingVanillaPIDs=[]", false, out pid)
                    && pid == 31415,
                "Direct game PID was not parsed.");
            Assert(VanillaReconnectSupervisor.IsPendingSequentialLaunch(true, true, false, VanillaReconnectStage.Launching),
                "Active launcher owner must remain pending before binding.");
            Assert(VanillaReconnectSupervisor.IsPendingSequentialLaunch(true, true, false, VanillaReconnectStage.WaitingForWindow),
                "Waiting-for-window launcher owner must remain pending before binding.");
            Assert(!VanillaReconnectSupervisor.IsPendingSequentialLaunch(true, true, true, VanillaReconnectStage.Launching),
                "An already-bound process must not be rebound.");
            Assert(!VanillaReconnectSupervisor.IsPendingSequentialLaunch(false, true, false, VanillaReconnectStage.Launching),
                "A runtime without the recovery lease must not claim a PID.");
        }

        private static void HardenedStartupAdvanceGate()
        {
            Assert(!VanillaReconnectSupervisor.SequentialStartupMayAdvance(false, true, true, false),
                "Startup advanced without confirmed gameplay.");
            Assert(!VanillaReconnectSupervisor.SequentialStartupMayAdvance(true, false, true, false),
                "Startup advanced before the resume hotkey was sent.");
            Assert(!VanillaReconnectSupervisor.SequentialStartupMayAdvance(true, true, false, false),
                "Startup advanced before the current client was minimized.");
            Assert(!VanillaReconnectSupervisor.SequentialStartupMayAdvance(true, true, true, true),
                "Startup advanced after a failed current-client sequence.");
            Assert(VanillaReconnectSupervisor.SequentialStartupMayAdvance(true, true, true, false),
                "Startup did not advance after gameplay + one resume + minimize completed successfully.");
        }

        private static void ExistingClientMemoryGate()
        {
            Assert(!VanillaReconnectSupervisor.ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState.Unknown),
                "Unknown visual state must not override fresh verified gameplay memory.");
            Assert(!VanillaReconnectSupervisor.ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState.Gameplay),
                "Gameplay unexpectedly blocked memory-backed adoption.");
            Assert(VanillaReconnectSupervisor.ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState.LoginShell),
                "A verified login shell must block gameplay adoption.");
            Assert(VanillaReconnectSupervisor.ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState.LoggingOut),
                "Logging-out state must block gameplay adoption.");
            Assert(VanillaReconnectSupervisor.ExistingClientVisualBlocksMemoryAdoption(VanillaVisualState.Disconnected),
                "Disconnected state must block gameplay adoption.");
        }

        private static void HostDiagnosticsBundle()
        {
            string value = VanillaHostDiagnostics.Build();
            Assert(value.Contains("HOST / DISPLAY / SESSION DIAGNOSTICS")
                && value.Contains("Session=") && value.Contains("Screens=")
                && value.Contains("Vanilla processes="),
                "Host diagnostics are missing required machine/session/display/process context.");
        }

        private static void BootstrapHelpersAreTransient()
        {
            Assert(VanillaForegroundInput.IsTransientBootstrapWindow("Gepard_Splash_Class", "GepardSplash"),
                "Known Gepard splash must never receive automated input.");
            Assert(VanillaForegroundInput.IsTransientBootstrapWindow("GDI+ Hook Window Class", "GDI+ Window (Vanilla MMO.exe)"),
                "The hidden 1x1 GDI+ hook helper must never be restored, focused, or receive input.");
            Assert(!VanillaForegroundInput.IsKnownVanillaGameWindow("GDI+ Hook Window Class", "GDI+ Window (Vanilla MMO.exe)"),
                "GDI+ helper was still recognized as a real game window because its caption contains Vanilla MMO.");
            Assert(!VanillaForegroundInput.IsTransientBootstrapWindow(
                    "Vanilla MMO | Gepard Shield 3.0 (^-_-^)", "Vanilla MMO | Gepard Shield 3.0 (^-_-^)") ,
                "Actual Vanilla game window was incorrectly classified as a helper.");
            Assert(VanillaForegroundInput.WindowCandidateScore(true, true, 1, 1, true, false,
                    "GDI+ Hook Window Class", "GDI+ Window (Vanilla MMO.exe)") == int.MinValue,
                "GDI+ helper remained eligible for ShowWindow/focus automation.");
            Assert(VanillaForegroundInput.WindowCandidateScore(true, true, 780, 327, true, false,
                    "Gepard_Splash_Class", "GepardSplash") == int.MinValue,
                "Transient splash remained an eligible input candidate.");
        }

        private static void GameWindowCandidateRanking()
        {
            int game = VanillaForegroundInput.WindowCandidateScore(true, true, 1280, 720, true, false,
                "Vanilla MMO | Gepard Shield 3.0 (^-_-^)", "Vanilla MMO | Gepard Shield 3.0 (^-_-^)");
            int generic = VanillaForegroundInput.WindowCandidateScore(true, true, 1280, 720, false, false,
                "SomeWindowClass", "SomeWindow");
            int preferredLauncher = VanillaForegroundInput.WindowCandidateScore(true, true, 780, 327, true, true,
                "TThorForm", "Vanilla MMO Launcher");
            Assert(game > generic, "Actual game window must outrank a generic same-process top-level window.");
            Assert(preferredLauncher > generic, "Explicit launcher window must remain usable for GAME START input.");
        }

        private static void MinimizedGameWindowCandidate()
        {
            int minimizedGame = VanillaForegroundInput.WindowCandidateScore(true, false, 0, 0, false, false,
                "Vanilla MMO | Gepard Shield 3.0 (^-_-^)", "Vanilla MMO | Gepard Shield 3.0 (^-_-^)", true);
            int hiddenGeneric = VanillaForegroundInput.WindowCandidateScore(true, false, 0, 0, false, false,
                "SomeWindowClass", "SomeWindow", true);
            Assert(minimizedGame != int.MinValue,
                "A known minimized Vanilla gameplay window must remain eligible so it can be restored for verification/input.");
            Assert(hiddenGeneric == int.MinValue,
                "An arbitrary hidden/minimized same-process window must not become an automation target.");

            int width, height; string source;
            Assert(VanillaBackgroundWindowInput.TryResolveCaptureSize(1024, 768, 0, 0, 0, 0,
                out width, out height, out source) && width == 1024 && height == 768 && source == "client-rect",
                "Visible background capture must prefer the actual client rectangle.");
            Assert(VanillaBackgroundWindowInput.TryResolveCaptureSize(0, 0, 1040, 807, 16, 39,
                out width, out height, out source) && width == 1024 && height == 768
                && source == "normal-placement-minus-frame",
                "A minimized 0x0 Vanilla client must recover its normal capture size without restoring/foregrounding the window.");
            Assert(!VanillaBackgroundWindowInput.TryResolveCaptureSize(0, 0, 100, 80, 0, 0,
                out width, out height, out source),
                "Tiny/helper window geometry must never become a background teleport capture surface.");
        }

        private static void ResponsiveRecoveryBreakpoint()
        {
            Assert(!VanillaReconnectForm.UseWideRecoveryLayout(1000),
                "Genuinely narrow recovery layouts should stack left workspace and log.");
            Assert(VanillaReconnectForm.UseWideRecoveryLayout(1100),
                "Ordinary desktop widths should use the 2:1 workspace/log split.");
            Assert(VanillaReconnectForm.UseWideRecoveryLayout(1800),
                "Full-HD recovery workspace unexpectedly fell back to stacked layout.");
        }

        private static void ResponsiveFleetHeight()
        {
            Assert(Container.PreferredFleetDashboardHeight(700) == 96,
                "Small desktop should use the most compact fleet strip.");
            Assert(Container.PreferredFleetDashboardHeight(820) == 104,
                "Constrained RDP height should keep the fleet strip compact.");
            Assert(Container.PreferredFleetDashboardHeight(980) == 112,
                "Full-HD class workspace should not waste vertical space on fleet cards.");
            Assert(Container.PreferredFleetDashboardHeight(1100) == 120,
                "Large desktop should still keep the fleet strip minimal.");
        }

        private static void ResponsiveAccountRows()
        {
            Assert(VanillaReconnectForm.MinimumVisibleAccountRows(1) == 5,
                "One configured account should still reserve four row slots plus one blank-row worth of breathing room.");
            Assert(VanillaReconnectForm.MinimumVisibleAccountRows(4) == 5,
                "Four configured accounts should retain one blank-row worth of breathing room.");
            Assert(VanillaReconnectForm.MinimumVisibleAccountRows(7) == 8,
                "The visible-row target should grow with the number of saved profiles.");
            Assert(VanillaReconnectForm.PreferredAccountsPanelHeight(700) == 190,
                "Constrained layouts should still reserve a useful minimum account area.");
            Assert(VanillaReconnectForm.PreferredAccountsPanelHeight(900) == 210,
                "Normal Full-HD layouts should reserve a larger minimum account area.");
            Assert(VanillaReconnectForm.MinimumRecoveryLogHeight(15) == 84,
                "Compact Recovery layouts should keep four readable log lines without forcing outer scrolling.");
            Assert(VanillaReconnectForm.MinimumRecoveryLogHeight(24) == 96,
                "Enlarged text should grow the compact log minimum by font metrics rather than a six-line hard floor.");
            Assert(!VanillaReconnectForm.RecoverySplitCanRotate(1, 700, 5)
                && !VanillaReconnectForm.RecoverySplitCanRotate(1000, 1, 5),
                "Recovery splitter must defer orientation changes while either axis is only a transient layout sliver.");
            Assert(VanillaReconnectForm.RecoverySplitCanRotate(1000, 700, 5),
                "Recovery splitter should rotate normally once both axes have real viewport size.");
        }

        private static void SavedAccountCatalog()
        {
            string root = Temp();
            string previous = Environment.GetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable);
            try
            {
                Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, root);
                VanillaAppData.InitializeAndMigrateLegacy(root);
                var seed = new[]
                {
                    new VanillaReconnectAccount { Label = "A", Enabled = true },
                    new VanillaReconnectAccount { Label = "B", Enabled = true }
                };
                var store = new VanillaAccountCatalogStore();
                var loaded = store.Load(seed);
                loaded.Add(new VanillaReconnectAccount { Label = "C", Enabled = false });
                loaded.Add(new VanillaReconnectAccount { Label = "D", Enabled = false });
                store.Save(loaded);
                var roundTrip = store.Load(seed);
                Assert(roundTrip.Count == 4, "Saved catalog dropped extra account profiles.");
                Assert(roundTrip.Count(a => a.Enabled) == 2, "Saved catalog changed the two-enabled-client limit.");

                roundTrip[2].Enabled = true;
                VanillaAccountCatalogStore.NormalizeEnabledLimit(roundTrip);
                Assert(roundTrip.Count(a => a.Enabled) == 2, "Catalog normalization allowed more than two enabled profiles.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, previous);
                Delete(root);
            }
        }

        private static void PersistentDataMigration()
        {
            string root = Temp();
            string previous = Environment.GetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable);
            try
            {
                string oldInstall = Path.Combine(root, "4RTools-Vanilla-v0.6.0");
                string newInstall = Path.Combine(root, "4RTools-Vanilla-v0.6.1");
                string data = Path.Combine(root, "user-data");
                Directory.CreateDirectory(Path.Combine(oldInstall, "Profile"));
                Directory.CreateDirectory(Path.Combine(oldInstall, "Profiles", "Vanilla"));
                Directory.CreateDirectory(Path.Combine(oldInstall, "VanillaReconnect"));
                Directory.CreateDirectory(newInstall);
                File.WriteAllText(Path.Combine(oldInstall, "Profile", "Hunter.json"), "{}");
                File.WriteAllText(Path.Combine(oldInstall, "Profiles", "Vanilla", "Farm.json"), "{}");
                File.WriteAllText(Path.Combine(oldInstall, "VanillaReconnect", "reconnect.json"), "{}");
                File.WriteAllText(Path.Combine(oldInstall, "supported_servers.json"), "[]");
                Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, data);
                VanillaAppData.InitializeAndMigrateLegacy(newInstall);
                Assert(File.Exists(Path.Combine(data, "Profiles", "Stock", "Hunter.json")), "Stock profile did not migrate.");
                Assert(File.Exists(Path.Combine(data, "Profiles", "Vanilla", "Farm.json")), "Vanilla profile did not migrate.");
                Assert(File.Exists(Path.Combine(data, "VanillaReconnect", "reconnect.json")), "Reconnect settings did not migrate.");
                Assert(File.Exists(Path.Combine(data, "supported_servers.json")), "Local server settings did not migrate.");
            }
            finally
            {
                Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, previous);
                Delete(root);
            }
        }

        private static void UpdaterVersionComparison()
        {
            Assert(VanillaUpdater.IsNewerVersion(new Version(0, 6, 2), new Version(0, 6, 1)), "Newer version was rejected.");
            Assert(!VanillaUpdater.IsNewerVersion(new Version(0, 6, 1), new Version(0, 6, 1)), "Equal version was treated as newer.");
            Assert(!VanillaUpdater.IsNewerVersion(new Version(0, 5, 9), new Version(0, 6, 1)), "Older version was treated as newer.");
        }

        private static string Temp()
        {
            string path = Path.Combine(Path.GetTempPath(), "4rtools-reconnect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void Delete(string path)
        {
            try { Directory.Delete(path, true); } catch { }
        }

        private static void Test(string name, Action action)
        {
            try { action(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        }

        private static void Assert(bool value, string message)
        {
            if (!value) throw new Exception(message);
        }
    }
}
