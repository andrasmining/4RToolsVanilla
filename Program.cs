using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using _4RTools.Model;
using _4RTools.Model.Vanilla;
using _4RTools.Model.Vanilla.Automation;
using _4RTools.Utils;

namespace _4RTools
{
    internal static class Program
    {
        /// <summary>
        /// Ponto de entrada principal para o aplicativo.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            if (VanillaUpdater.TryHandleApplyCommand(args)) return;
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);

            // Normal application launches own a debug session from process startup, not from
            // whichever UI surface happens to initialize first. This guarantees one distinct
            // timestamp/PID log file for every app restart. UI/smoke rendering stays side-effect
            // free and therefore deliberately skips runtime debug initialization.
            bool uiSmoke = args.Contains("--portable-smoke-test") || args.Contains("--original-ui-check");
            if (!uiSmoke) VanillaDebugLog.Initialize();

            if (args.Length > 0 && args[0] == "--vanilla-discover")
            {
                Discover(args);
                return;
            }
            // This developer entry point creates no stock workers or updater.
            if (args.Length > 0 && args[0] == "--vanilla-session-check")
            {
                CheckSession(args);
                return;
            }
            if (args.Length > 0 && args[0] == "--vanilla-snapshot")
            {
                CaptureSnapshot(args);
                return;
            }
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length > 0 && (args[0] == "--portable-smoke-test" || args[0] == "--original-ui-check"))
            {
                PortableSmokeTest(args);
                return;
            }
            if (args.Length > 0 && args[0] == "--vanilla-diagnostics")
            {
                try
                {
                    ProfileSingleton.Create("Default");
                    var diagnostics = new Forms.VanillaDiagnosticsForm();
                    if (args.Contains("--demo")) diagnostics.StartDemo();
                    System.Windows.Forms.Application.Run(diagnostics);
                }
                catch (Exception ex)
                {
                    System.Windows.Forms.MessageBox.Show(ex.Message, "Vanilla diagnostics");
                    Environment.ExitCode = 1;
                }
                return;
            }
            try
            {
                if (!args.Contains("--vanilla-tools"))
                {
                    ProfileSingleton.Create("Default");
                    LoadStockClients();
                    System.Windows.Forms.Application.Run(new Forms.Container());
                    return;
                }
                using (var session = new VanillaAutomationSession(AppDomain.CurrentDomain.BaseDirectory))
                {
                    var app = new Forms.VanillaAutomationForm(session, () =>
                    {
                        session.SetEnabled(false);
                        ProfileSingleton.Create("Default");
                        new Forms.VanillaDiagnosticsForm().Show();
                    });
                    System.Windows.Forms.Application.Run(app);
                }
            }
            catch (Exception ex)
            {
                System.Windows.Forms.MessageBox.Show(ex.Message, "4RTools Vanilla stopped");
                Environment.ExitCode = 1;
            }
        }

        internal static void OpenStockTools()
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = System.Windows.Forms.Application.ExecutablePath,
                Arguments = "--stock-ui",
                WorkingDirectory = Environment.CurrentDirectory,
                UseShellExecute = true
            });
        }

        private static void LoadStockClients()
        {
            var clients = LocalServerManager.GetLocalClients();
            clients.AddRange(JsonConvert.DeserializeObject<System.Collections.Generic.List<ClientDTO>>(Resources._4RTools.ETCResource.supported_servers));
            foreach (var client in clients)
            {
                // Vanilla uses the shared read-only backend instead of legacy address definitions.
                if (Client.IsVanillaProcessName(client.name)) continue;
                ClientListSingleton.AddClient(new Client(client));
            }
        }

        private static void PortableSmokeTest(string[] args)
        {
            string output = null;
            try
            {
                int outputIndex = Array.IndexOf(args, "--output");
                if (outputIndex < 0 || outputIndex + 1 >= args.Length) throw new ArgumentException("Smoke test requires --output <new-json-path>.");
                output = args[outputIndex + 1];
                if (File.Exists(output)) throw new IOException("Smoke report already exists.");
                ProfileSingleton.Create("Default");
                LoadStockClients();
                using (var form = new Forms.Container(smokeTest: true))
                {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    form.Show();
                    // Exercise startup callbacks for more than two dashboard timer periods.
                    // The offline path must remain inert even when live clients and SMTP settings exist.
                    var startupClock = System.Diagnostics.Stopwatch.StartNew();
                    do
                    {
                        System.Windows.Forms.Application.DoEvents();
                        form.AssertSmokeBackgroundServicesInactive();
                        System.Threading.Thread.Sleep(10);
                    }
                    while (startupClock.ElapsedMilliseconds < 1100);
                    if (form.AutomationEnabled || form.GameplayAttached || IntPtr.Size != 4)
                        throw new InvalidOperationException("Unexpected startup automation, game attachment, or process architecture.");
                    var mainTabs = Descendants(form).OfType<System.Windows.Forms.TabControl>()
                        .OrderByDescending(tabs => tabs.TabPages.Count).FirstOrDefault();
                    if (!form.VanillaTabIsFirst)
                        throw new InvalidOperationException("Vanilla must be the first primary feature tab. Actual order: " + form.PrimaryTabOrder);
                    if (form.FormBorderStyle != System.Windows.Forms.FormBorderStyle.Sizable || !form.MaximizeBox || form.MinimumSize.Width < 1000 || form.MinimumSize.Height < 650)
                        throw new InvalidOperationException("Main window is too small for the no-scroll Vanilla layout.");
                    // Upstream reparents its child forms into tab pages, so MdiChildren
                    // does not contain them all after the window is constructed.
                    string[] originalForms = Descendants(form).OfType<System.Windows.Forms.Form>()
                        .Select(child => child.GetType().Name).ToArray();
                    int featureForms = originalForms.Length;
                    if (featureForms < 10) throw new InvalidOperationException("Original feature forms are missing.");
                    var stock = JsonConvert.DeserializeObject<System.Collections.Generic.List<ClientDTO>>(Resources._4RTools.ETCResource.supported_servers);
                    if (stock == null || stock.Count == 0) throw new InvalidOperationException("Bundled stock resources missing.");
                    bool liveCheck = args[0] == "--original-ui-check";
                    VanillaClientState snapshot = null;
                    if (liveCheck)
                    {
                        int pid;
                        if (args.Length < 2 || !int.TryParse(args[1], out pid) || pid <= 0)
                            throw new ArgumentException("Original UI check requires a positive Vanilla process ID.");
                        form.SelectClientForValidation(pid);
                        snapshot = form.VanillaSnapshot;
                        if (snapshot == null || snapshot.ProcessId != pid || form.AutomationEnabled)
                            throw new InvalidOperationException("Original UI did not attach read-only to the requested client.");
                    }
                    int imageIndex = Array.IndexOf(args, "--screenshot");
                    if (imageIndex >= 0 && imageIndex + 1 < args.Length)
                    {
                        form.Refresh();
                        System.Windows.Forms.Application.DoEvents();
                        using (var bitmap = new System.Drawing.Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, new System.Drawing.Rectangle(0, 0, form.Width, form.Height));
                            // MdiClient paints over reparented stock controls in DrawToBitmap.
                            // Render those controls directly, without capturing other windows.
                            var origin = form.PointToScreen(System.Drawing.Point.Empty);
                            foreach (var control in form.Controls.Cast<System.Windows.Forms.Control>().Reverse())
                            {
                                if (!control.Visible || control is System.Windows.Forms.MdiClient) continue;
                                var bounds = new System.Drawing.Rectangle(origin.X - form.Left + control.Left,
                                    origin.Y - form.Top + control.Top, control.Width, control.Height);
                                bounds = System.Drawing.Rectangle.Intersect(bounds, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height));
                                if (bounds.Width > 0 && bounds.Height > 0)
                                    control.DrawToBitmap(bitmap, bounds);
                            }
                            bitmap.Save(args[imageIndex + 1]);
                        }
                    }
                    form.AssertSmokeBackgroundServicesInactive();
                    VerifyPackagedOcr();
                    // Capture actual state before Close() disposes the services.
                    var report = new
                    {
                        Success = true, Version = VanillaUpdater.CurrentVersionText, PointerBytes = IntPtr.Size,
                        ObserverContext = ProcessObservationContext.Current.ToString(),
                        MainUi = "Container", OriginalFeatureForms = featureForms, PackagedOcr = true,
                        FeatureForms = originalForms,
                        AutomationEnabled = form.AutomationEnabled,
                        VanillaPollingEnabled = form.VanillaPollingEnabled,
                        FleetPollingEnabled = form.FleetPollingEnabled,
                        FleetPollCount = form.FleetPollCount,
                        RecoveryRunning = form.RecoveryRunning,
                        WeightAlertsRunning = form.WeightAlertsRunning,
                        UpdateCheckRunning = form.UpdateCheckRunning,
                        ExecutableDirectory = AppDomain.CurrentDomain.BaseDirectory,
                        WorkingDirectory = Environment.CurrentDirectory,
                        ProfileRoundTrip = Profile.ListAll().Count > 0,
                        StockClientDefinitions = stock.Count,
                        GameplayAttached = form.GameplayAttached, InputSent = false, Snapshot = snapshot,
                        Checked = liveCheck ? "Original client selector and shared read-only snapshot; automation OFF without input or keyboard hook"
                            : "Original 4RTools window and feature forms, embedded dependencies, portable profiles, UI show/close; live polling, recovery, weight alerts, updates, and automation inactive without game attachment or keyboard hook"
                    };
                    form.Close();
                    File.WriteAllText(output, JsonConvert.SerializeObject(report, Formatting.Indented));
                }
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                if (output != null && !File.Exists(output))
                    File.WriteAllText(output, JsonConvert.SerializeObject(new { Success = false, Error = ex.ToString() }, Formatting.Indented));
            }
        }

        private static System.Collections.Generic.IEnumerable<System.Windows.Forms.Control> Descendants(System.Windows.Forms.Control parent)
        {
            foreach (System.Windows.Forms.Control child in parent.Controls)
            {
                yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }

        private static void CaptureSnapshot(string[] args)
        {
            string output = null;
            try
            {
                int outputIndex = Array.IndexOf(args, "--output");
                if (outputIndex < 0 || outputIndex + 1 >= args.Length)
                    throw new ArgumentException("Use --vanilla-snapshot <pid> --output <new-json-path> [--map <json-path>] [--probe].");
                output = args[outputIndex + 1];
                if (File.Exists(output)) throw new IOException("Snapshot output already exists; choose a new path.");
                int pid;
                if (args.Length < 2 || !int.TryParse(args[1], out pid) || pid <= 0)
                    throw new ArgumentException("A positive process ID is required.");
                int mapIndex = Array.IndexOf(args, "--map");
                if (mapIndex >= 0 && mapIndex + 1 >= args.Length) throw new ArgumentException("Missing map path.");
                VanillaMemoryMap map = mapIndex < 0
                    ? VanillaMemoryMap.Parse(new VanillaDiagnosticsSettings().MemoryMapJson)
                    : VanillaMemoryMap.Load(args[mapIndex + 1]);
                using (var memory = new ReadOnlyProcessMemory(pid))
                {
                    string probe = null;
                    if (args.Contains("--probe"))
                    {
                        // Explicit bounded read of the known module header, not a game-state field.
                        // A failure ends this attempt; the source never gets constructed afterward.
                        probe = BitConverter.ToString(memory.ReadBytes(memory.MainModuleBaseAddress, 2));
                    }
                    using (var source = new MemoryStateSource(memory, map))
                    {
                        VanillaClientState snapshot = source.Poll(DateTimeOffset.UtcNow);
                        object result = probe == null ? (object)snapshot : new { HeaderProbe = probe, Snapshot = snapshot };
                        File.WriteAllText(output, JsonConvert.SerializeObject(result, Formatting.Indented));
                        if (source.IsStopped) Environment.ExitCode = 1;
                    }
                }
            }
            catch (Exception ex)
            {
                Environment.ExitCode = 1;
                Console.Error.WriteLine(ex);
                if (output != null && !File.Exists(output))
                {
                    try { File.WriteAllText(output, JsonConvert.SerializeObject(new { Error = ex.ToString(), SampledAtUtc = DateTimeOffset.UtcNow }, Formatting.Indented)); }
                    catch (Exception writeError) { Console.Error.WriteLine(writeError); }
                }
            }
        }

        private static void CheckSession(string[] args)
        {
            try
            {
                int pid, outputIndex = Array.IndexOf(args, "--output");
                if (args.Length < 2 || !int.TryParse(args[1], out pid) || outputIndex < 0 || outputIndex + 1 >= args.Length)
                    throw new ArgumentException("Use --vanilla-session-check <pid> --output <new-json-path>.");
                string output = args[outputIndex + 1];
                if (File.Exists(output)) throw new IOException("Output already exists.");
                using (var session = new VanillaAutomationSession(AppDomain.CurrentDomain.BaseDirectory))
                {
                    // No input is possible, regardless of saved user preferences.
                    session.ApplySettings(new VanillaAutomationSettings { DryRun = true });
                    session.Connect(pid);
                    if (session.Snapshot == null) throw new InvalidOperationException(session.Status);
                    session.SetEnabled(true);
                    File.WriteAllText(output, JsonConvert.SerializeObject(new
                    {
                        session.ExecutablePath, session.Fingerprint, session.BuildProfile,
                        session.Status, session.IsEnabled, session.Snapshot,
                        InputSent = false, DryRun = true
                    }, Formatting.Indented));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }

        private static void VerifyPackagedOcr()
        {
            using (var bitmap = new System.Drawing.Bitmap(320, 64))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Arial", 24, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel))
            {
                graphics.Clear(System.Drawing.Color.White);
                graphics.DrawString("Vanilla MMO", font, System.Drawing.Brushes.Black, 12, 12);
                VanillaTextLine[] lines;
                string evidence;
                if (!VanillaTextRecognition.TryRead(bitmap, new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height),
                    true, out lines, out evidence) || !lines.Any(line => line.Text == "Vanilla MMO" && line.Confidence >= 70))
                    throw new InvalidOperationException("Packaged OCR runtime/model check failed: " + evidence);
            }
        }

        private static void Discover(string[] args)
        {
            try
            {
                int pid;
                int outputIndex = Array.IndexOf(args, "--output"), captureIndex = Array.IndexOf(args, "--capture");
                if (args.Length < 2 || !int.TryParse(args[1], out pid) || outputIndex < 0 || outputIndex + 1 >= args.Length)
                    throw new ArgumentException("Use --vanilla-discover <pid> --output <new-json-path> [--scan] [--capture <new-png-path>].");
                string output = args[outputIndex + 1];
                if (File.Exists(output)) throw new IOException("Output already exists.");
                string capture = captureIndex >= 0 && captureIndex + 1 < args.Length ? args[captureIndex + 1] : null;
                if (capture != null && File.Exists(capture)) throw new IOException("Capture output already exists.");
                int statsIndex = Array.IndexOf(args, "--stats");
                uint[] expected = statsIndex >= 0 && statsIndex + 1 < args.Length
                    ? args[statsIndex + 1].Split(',').Select(value => uint.Parse(value, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
                var result = VanillaDiscovery.Inspect(pid, args.Contains("--scan"), capture, expected, args.Contains("--restore-window"));
                File.WriteAllText(output, JsonConvert.SerializeObject(result, Formatting.Indented));
                if (result.Error != null) Environment.ExitCode = 1;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Environment.ExitCode = 1; }
        }
    }
}
