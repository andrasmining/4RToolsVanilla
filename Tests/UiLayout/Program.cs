using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

// Loads the production assembly in its inert smoke mode. Only fictional account/fleet
// observations are supplied. No game process, credentials, email or input is used.
internal static class UiLayoutHarness
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static Assembly app;
    private static string output;
    private static int caseNumber;
    private static readonly List<string> failures = new List<string>();
    private static readonly StringBuilder report = new StringBuilder();
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        output = Path.GetFullPath(args[1]);
        Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("FOURRTOOLS_DATA_ROOT", Path.Combine(output, "isolated-data"));
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            CallStatic("_4RTools.Model.Vanilla.VanillaIsolatedTestDesktop", "AssertCurrent");
            report.AppendLine("Isolated non-input desktop: verified");
            report.AppendLine("Assembly: " + app.GetName().Version + "; platform=" + Environment.OSVersion + "; pointerBytes=" + IntPtr.Size);
            CallStatic("_4RTools.Model.Vanilla.VanillaAppData", "InitializeAndMigrateLegacy", Path.GetDirectoryName(Path.GetFullPath(args[0])));
            CallStatic("_4RTools.Model.ProfileSingleton", "Create", "Default");
            CallStatic("_4RTools.Program", "LoadStockClients");
            using (Form main = (Form)Activator.CreateInstance(app.GetType("_4RTools.Forms.Container", true), new object[] { true }))
            {
                main.MaximumSize = new Size(4096, 4096);
                main.StartPosition = FormStartPosition.Manual;
                main.Location = Point.Empty;
                main.Show();
                Pump();
                ((Control)Field(main, "vanillaWorkspace")).Enabled = true;
                object recovery = Field(main, "integratedReconnectView");
                Check(recovery != null, "Production Recovery form was not embedded by Container startup.");
                CheckWorkspacePolicy(main, recovery);
                CheckRecoveryHostCloseFlush(main, recovery);
                CheckWeightCartHotkeys(main);
                CheckEmergencySettings(main);
                CheckFarmingOptions(main);
                SeedFleet(main);
                RunCase(main, recovery, 1920, 1020, 2, 1F, false);
                CheckRecoverySplitter(main, recovery);
                RunCase(main, recovery, 1920, 1020, 4, 1F, false);
                // Realistic Full-HD work area, allowing space for borders/title/taskbar.
                RunCase(main, recovery, 1904, 981, 12, 1F, false);
                RunCase(main, recovery, 1980, 1020, 4, 1F, false);
                RunCase(main, recovery, 1600, 900, 12, 1F, false);
                RunCase(main, recovery, 1366, 768, 4, 1F, false);
                RunCase(main, recovery, 1050, 700, 4, 1F, false);
                RunCase(main, recovery, 1920, 1020, 40, 1F, false);
                RunCase(main, recovery, 1366, 768, 40, 1F, false);
                RunCase(main, recovery, 1920, 1020, 4, 1.25F, false);
                RunCase(main, recovery, 1920, 1020, 12, 1.50F, false);
                RunCase(main, recovery, 1366, 768, 4, 1.50F, false);
                RunCase(main, recovery, 1050, 700, 4, 1F, true);
                RunCase(main, recovery, 1920, 1020, 4, 1F, true);
                // Return from enlarged text/small windows to the initial size on the same instance.
                RunCase(main, recovery, 1920, 1020, 4, 1F, false);
                // Vertical scrolling must not consume the last column or create horizontal overflow,
                // including enlarged text, a stacked narrow workspace and a return to a short list.
                RunCase(main, recovery, 1920, 1020, 40, 1.50F, false);
                RunCase(main, recovery, 1050, 700, 40, 1F, false);
                RunCase(main, recovery, 1920, 1020, 4, 1F, false);
                CheckFarmingStopCards(main, recovery);
                CheckCharacterDiscovery(main, recovery);
                CheckCharacterEditor(main, recovery);
                CheckLegacyUsernameDiscovery(main, recovery);
                CheckUsernameDiagnostics();
                CheckRulesEditor();
                CheckPrivateUpdateAccess();
                CheckNoSaveButtons(main, "Integrated workspace");
                Call(main, "AssertSmokeBackgroundServicesInactive");
                report.AppendLine("Background services: inactive; fleet polls=0; no game input or email enabled.");
            }
        }
        catch (Exception ex) { failures.Add("Harness exception: " + ex); }
        report.AppendLine("Cases: " + caseNumber);
        report.AppendLine("Failures: " + failures.Count);
        foreach (string failure in failures) report.AppendLine("FAIL " + failure);
        File.WriteAllText(Path.Combine(output, "layout-report.txt"), report.ToString());
        Console.WriteLine(report.ToString());
        return failures.Count == 0 ? 0 : 1;
    }

    private static void CheckWorkspacePolicy(Form main, object recovery)
    {
        caseNumber++;
        TabControl workspace = (TabControl)Field(main, "vanillaWorkspace");
        string[] tabs = workspace.TabPages.Cast<TabPage>().Select(page => page.Text).ToArray();
        Check(tabs.Length > 0 && tabs[0] == "Recovery & relog", "Recovery & relog is not the first Vanilla workspace tab.");
        Check(!tabs.Contains("Automation"), "Redundant Vanilla Automation tab is still present.");
        NumericUpDown restart = (NumericUpDown)Field(recovery, "movementRestartSeconds");
        Check(restart.Value == 180, "No-movement restart threshold does not default to 180 seconds.");
        ContextMenuStrip tests = (ContextMenuStrip)Field(recovery, "responsiveTestsMenu");
        string[] items = tests.Items.Cast<ToolStripItem>().Select(item => item.Text).ToArray();
        Check(items.Contains("Smart Teleport now (selected)"), "TESTS menu is missing manual Smart Teleport.");
        Check(items.Contains("Weight/Cart clean now (selected)"), "TESTS menu is missing manual Weight/Cart cleaning.");
        report.AppendLine("CASE workspace policy: Automation tab removed; 180s restart default; manual Smart Teleport and Weight/Cart TESTS actions present.");
    }

    private static void CheckRecoveryHostCloseFlush(Form main, object recovery)
    {
        caseNumber++;
        object supervisor = Field(recovery, "supervisor");
        var restart = (NumericUpDown)Field(recovery, "movementRestartSeconds");
        var pending = (System.Windows.Forms.Timer)Field(recovery, "autosaveTimer");
        int original = (int)ReadProperty(ReadProperty(supervisor, "Settings"), "MovementRestartSeconds");
        restart.Text = (original + 30).ToString();
        Check(pending.Enabled && (int)ReadProperty(ReadProperty(supervisor, "Settings"), "MovementRestartSeconds") == original,
            "Embedded Recovery fixture must have an uncommitted debounce edit before host closing.");
        // Raise only the real host's closing event. Do not close/dispose this
        // harness, start recovery, enumerate clients or send any input.
        Call(main, "OnFormClosing", new FormClosingEventArgs(CloseReason.UserClosing, false));
        object reloaded = Call(Field(supervisor, "store"), "Load");
        Check(!pending.Enabled && (int)ReadProperty(ReadProperty(supervisor, "Settings"), "MovementRestartSeconds") == original + 30
            && (int)ReadProperty(reloaded, "MovementRestartSeconds") == original + 30,
            "Main-window closing did not flush embedded Recovery's pending edit to active and durable settings.");
        Check(ReferenceEquals(supervisor, Field(main, "integratedReconnectSupervisor")) && !(bool)Field(supervisor, "disposed"),
            "Recovery must remain alive while its pending settings are flushed.");
        object unchanged = Field(supervisor, "settings");
        Call(main, "OnFormClosing", new FormClosingEventArgs(CloseReason.UserClosing, false));
        Check(ReferenceEquals(unchanged, Field(supervisor, "settings")), "Closing without pending Recovery edits unnecessarily reapplied settings.");
        restart.Text = original.ToString();
        Call(main, "OnFormClosing", new FormClosingEventArgs(CloseReason.UserClosing, false));
        Call(main, "AssertSmokeBackgroundServicesInactive");
        report.AppendLine("CASE embedded Recovery host close: pending debounce flushed before disposal; durable reload verified; no-pending close is a no-op; no live services.");
    }

    private static void CheckPrivateUpdateAccess()
    {
        Type access = app.GetType("_4RTools.Model.Vanilla.VanillaUpdateAccess", true);
        string path = (string)access.GetProperty("TokenPath", All).GetValue(null, null);
        Check(!File.Exists(path), "Isolated update-token fixture unexpectedly exists before opening.");
        foreach (float scale in new[] { 1F, 1.5F })
        using (var dialog = (Form)Activator.CreateInstance(app.GetType("_4RTools.Model.Vanilla.VanillaUpdateAccessDialog", true), true))
        {
            caseNumber++;
            dialog.StartPosition = FormStartPosition.Manual; dialog.Location = Point.Empty;
            if (scale != 1F)
            {
                dialog.Scale(new SizeF(scale, scale));
                dialog.Font = new Font(dialog.Font.FontFamily, dialog.Font.Size * scale);
            }
            dialog.Show(); Pump();
            var boxes = Descendants(dialog).OfType<TextBox>().ToArray();
            Check(boxes.Length == 1 && boxes[0].UseSystemPasswordChar && boxes[0].Text.Length == 0,
                "Private update token must start empty and use password masking.");
            Check(!File.Exists(path), "Opening update access must not write defaults.");
            CheckNoSaveButtons(dialog, "Private update access");
            foreach (string caption in new[] { "CLEAR ACCESS", "CLOSE" })
            {
                var button = Descendants(dialog).OfType<Button>().SingleOrDefault(b => b.Text == caption);
                Check(button != null && button.Visible && FullyVisible(button, dialog), "Private update action is clipped: " + caption);
            }
            Check(boxes.Length == 1 && FullyVisible(boxes[0], dialog), "Private update token entry is clipped.");
            if (boxes.Length == 1)
            {
                boxes[0].Text = "synthetic-ui-token-only";
                Check(!File.Exists(path), "Token entry was persisted mid-keystroke.");
                Call(boxes[0], "OnValidated", EventArgs.Empty);
                Check(File.Exists(path) && boxes[0].Text.Length == 0
                    && (string)access.GetMethod("ReadSavedToken", All).Invoke(null, null) == "synthetic-ui-token-only",
                    "Completed masked token entry did not persist through DPAPI.");
                byte[] previous = File.ReadAllBytes(path);
                boxes[0].Text = "bad"; Call(boxes[0], "OnValidated", EventArgs.Empty);
                Check(previous.SequenceEqual(File.ReadAllBytes(path)) && ((Label)Field(dialog, "status")).Text.StartsWith("Not saved"),
                    "Invalid replacement must retain existing token and show a visible error.");
                dialog.Close(); Pump();
                Check(!dialog.IsDisposed && dialog.Visible && boxes[0].Text == "bad" && ((Button)Field(dialog, "discard")).Visible,
                    "Failed token save on Close must keep the error and retryable entry visible.");
                boxes[0].Clear();
                Descendants(dialog).OfType<Button>().Single(b => b.Text == "CLEAR ACCESS").PerformClick();
                Check(!File.Exists(path), "Clear access did not remove the test-owned token.");
            }
            Pump();
            SaveScreenshot(dialog, Path.Combine(output, scale == 1F ? "24-private-update-access.png" : "25-private-update-access-scaled.png"));
            report.AppendLine("CASE private update access: masked auto-save, malformed replacement retains token, Clear/Close visible at scale " + scale + "; synthetic token only, no network.");
            boxes[0].Text = "bad"; Call(boxes[0], "OnValidated", EventArgs.Empty);
            ((Button)Field(dialog, "discard")).PerformClick();
            Check(dialog.IsDisposed && !File.Exists(path), "Explicit Discard entry failed to close without saving.");
        }
    }

    private static void CheckRecoverySplitter(Form main, object recovery)
    {
        caseNumber++;
        ResizeNativeViewport(main, 1920, 1020);
        Pump();
        SplitContainer split = (SplitContainer)Field(recovery, "responsiveSplit");
        Control left = (Control)Field(recovery, "responsiveLeft");
        Control logBox = (Control)Field(recovery, "responsiveLogBox");
        Check(split != null && !split.IsSplitterFixed, "Recovery Characters/Log divider is not draggable.");
        Check(split.Orientation == Orientation.Vertical, "Full-HD Recovery splitter is not vertical.");

        int original = split.SplitterDistance;
        int leftBefore = left.Width, logBefore = logBox.Width;
        int target = Math.Max(split.Panel1MinSize, original - Math.Max(100, split.Width / 10));
        if (target >= original) target = Math.Max(1, original - 40);
        split.SplitterDistance = target;
        Pump();
        Check(left.Width < leftBefore, "Dragging Recovery divider toward Characters did not shrink the character pane.");
        Check(logBox.Width > logBefore, "Dragging Recovery divider did not enlarge the Log pane.");
        Check(FullyVisible(logBox, main), "Log pane became clipped after divider resize.");

        split.SplitterDistance = original;
        Pump();
        report.AppendLine("CASE draggable recovery splitter: Characters/Log divider resizes both panes and restores cleanly.");
    }

    private static void CheckWeightCartHotkeys(Form main)
    {
        caseNumber++;
        object service = Field(main, "integratedWeightAlertService");
        Type panelType = app.GetType("_4RTools.Model.Vanilla.VanillaWeightAlertsPanel", true);
        using (var host = new Form { ClientSize = new Size(1180, 720), StartPosition = FormStartPosition.Manual, Location = Point.Empty })
        using (var panel = (Control)Activator.CreateInstance(panelType, new[] { service }))
        {
            panel.Dock = DockStyle.Fill;
            host.Controls.Add(panel);
            host.Show(); Pump();
            TextBox stop = (TextBox)Field(panel, "autobattleStopHotkey");
            TextBox inventory = (TextBox)Field(panel, "inventoryHotkey");
            TextBox cart = (TextBox)Field(panel, "cartHotkey");
            Check(stop.Text == "Alt+3", "Weight Autobattle STOP hotkey must default to Alt+3.");
            Check(inventory.Text == "Alt+E" && cart.Text == "Alt+W", "Weight Inventory/Cart defaults changed.");
            Check(FullyVisible(stop, host), "Weight Autobattle STOP hotkey field is clipped.");
            SaveScreenshot(host, Path.Combine(output, "23-weight-cart-hotkeys.png"));
            host.Close();
        }
        report.AppendLine("CASE 23 Weight/Cart hotkeys: dedicated Autobattle STOP visible as Alt+3; Inventory Alt+E; Cart Alt+W.");
    }

    private static void CheckEmergencySettings(Form main)
    {
        caseNumber++;
        object service = Field(main, "integratedWeightAlertService");
        object supervisor = Field(service, "supervisor");
        Type panelType = app.GetType("_4RTools.Model.Vanilla.VanillaWeightAlertsPanel", true);
        object reconnectStore = Field(supervisor, "store");
        string settingsPath = (string)Field(reconnectStore, "path");
        string before = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;
        using (var host = new Form { ClientSize = new Size(1180, 720), StartPosition = FormStartPosition.Manual, Location = Point.Empty })
        using (var panel = (ScrollableControl)Activator.CreateInstance(panelType, new[] { service }))
        {
            panel.Dock = DockStyle.Fill; host.Controls.Add(panel); host.Show(); Pump();
            NumericUpDown weight = (NumericUpDown)Field(panel, "emergencyWeight");
            NumericUpDown sp = (NumericUpDown)Field(panel, "emergencySp");
            NumericUpDown hp = (NumericUpDown)Field(panel, "emergencyHp");
            Label saved = (Label)Field(panel, "emergencySaveStatus");
            CheckNoSaveButtons(panel, "Weight settings");
            Check(weight.Value == 50M && sp.Value == 25M && hp.Value == 50M, "Emergency defaults must be Weight >50, SP <25, HP <50.");
            Check(weight.Minimum == 0M && weight.Maximum == 99.9M && sp.Minimum == .1M && sp.Maximum == 100M
                && hp.Minimum == .1M && hp.Maximum == 100M
                && new[] { weight, sp, hp }.All(c => c.DecimalPlaces == 1 && c.Increment == .1M), "Emergency editor limits/precision are wrong.");
            Check(Descendants(panel).OfType<GroupBox>().Any(g => g.Text.Contains("AND") && g.Text.Contains("auto-save")),
                "Emergency editor must show all-three AND and auto-save.");
            Check(before == (File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null),
                "Constructing the emergency editor must not save defaults.");

            NumericUpDown cart = (NumericUpDown)Field(panel, "autoThreshold");
            TextBox smtp = (TextBox)Field(panel, "fromAddress");
            CheckBox mail = (CheckBox)Field(panel, "enabled");
            object originalWeightSettings = service.GetType().GetProperty("Settings").GetValue(service, null);
            cart.Value = 61M; smtp.Text = "pending-invalid-address";
            weight.Text = 60.5M.ToString();
            Check(EmergencyValue(service, "WeightAbovePercent") == 50M, "Partially typed emergency limits saved before validation.");
            Call(weight, "OnValidated", EventArgs.Empty);
            sp.Text = 30.2M.ToString(); Call(sp, "OnValidated", EventArgs.Empty);
            hp.Text = 45.8M.ToString(); Call(hp, "OnKeyDown", new KeyEventArgs(Keys.Enter));
            Check(EmergencyValue(service, "WeightAbovePercent") == 60.5M && EmergencyValue(service, "SpBelowPercent") == 30.2M
                && EmergencyValue(service, "HpBelowPercent") == 45.8M && saved.Text == "Saved", "Completed emergency edits did not auto-save.");
            Check(cart.Value == 61M && smtp.Text == "pending-invalid-address" && !mail.Checked,
                "Emergency auto-save overwrote pending Cart/mail edits.");
            object stillWeightSettings = service.GetType().GetProperty("Settings").GetValue(service, null);
            Check(Equals(originalWeightSettings.GetType().GetProperty("AutoCartThresholdPercent").GetValue(originalWeightSettings, null),
                    stillWeightSettings.GetType().GetProperty("AutoCartThresholdPercent").GetValue(stillWeightSettings, null))
                && Equals(originalWeightSettings.GetType().GetProperty("FromAddress").GetValue(originalWeightSettings, null),
                    stillWeightSettings.GetType().GetProperty("FromAddress").GetValue(stillWeightSettings, null)),
                "Emergency auto-save committed pending Cart/mail settings.");

            // Reopen the durable state with an inert supervisor, without Start or timers.
            using (var reloaded = (IDisposable)Activator.CreateInstance(supervisor.GetType(), new[] { Field(supervisor, "baseDirectory") }))
            {
                object settings = reloaded.GetType().GetProperty("FarmingEmergencySettings").GetValue(reloaded, null);
                Check((decimal)settings.GetType().GetProperty("WeightAbovePercent").GetValue(settings, null) == 60.5M
                    && (decimal)settings.GetType().GetProperty("SpBelowPercent").GetValue(settings, null) == 30.2M
                    && (decimal)settings.GetType().GetProperty("HpBelowPercent").GetValue(settings, null) == 45.8M,
                    "Emergency edits were not restored from durable settings.");
            }
            Call(panel, "FlushPendingSettings");
            Check((decimal)ReadProperty(ReadProperty(service, "Settings"), "AutoCartThresholdPercent") == 61M
                && Equals(ReadProperty(originalWeightSettings, "FromAddress"), ReadProperty(ReadProperty(service, "Settings"), "FromAddress"))
                && ((Label)Field(panel, "saveStatus")).Text.StartsWith("Not saved:"),
                "Weight auto-save must persist valid Cart edits independently of an invalid e-mail draft.");
            smtp.Text = ""; Call(panel, "FlushPendingSettings");
            Check(EmergencyValue(service, "WeightAbovePercent") == 60.5M && EmergencyValue(service, "SpBelowPercent") == 30.2M
                && EmergencyValue(service, "HpBelowPercent") == 45.8M, "Weight auto-save reverted emergency limits.");

            string blocker = Path.Combine(output, "emergency-save-blocker");
            File.WriteAllText(blocker, "test-owned file prevents creation of a child directory");
            try
            {
                SetField(reconnectStore, "path", Path.Combine(blocker, "reconnect.json"));
                weight.Text = 70M.ToString(); Call(weight, "OnValidated", EventArgs.Empty);
                Check(weight.Value == 60.5M && EmergencyValue(service, "WeightAbovePercent") == 60.5M
                    && saved.Text.StartsWith("Not saved:") && saved.ForeColor == Color.Firebrick,
                    "Failed emergency save must restore active limits and display an error.");
                Call(panel, "RefreshStatus");
                Check(saved.Text.StartsWith("Not saved:"), "Polling erased the emergency save error.");
            }
            finally { SetField(reconnectStore, "path", settingsPath); File.Delete(blocker); }

            foreach (var viewport in new[] { new Size(1904, 850), new Size(1180, 650), new Size(760, 560) })
            {
                host.ClientSize = viewport; Pump();
                CheckWeightControlsReachable(host, panel, viewport.ToString());
                panel.AutoScrollPosition = Point.Empty; Pump();
                SaveScreenshot(host, Path.Combine(output, "emergency-" + viewport.Width + ".png"));
            }
            panel.Scale(new SizeF(1.5F, 1.5F)); panel.Font = new Font(panel.Font.FontFamily, panel.Font.Size * 1.5F);
            Pump(); CheckWeightControlsReachable(host, panel, "760x560 enlarged text");
            panel.AutoScrollPosition = Point.Empty; Pump();
            SaveScreenshot(host, Path.Combine(output, "emergency-scaled.png"));
            host.Close();
        }
        Call(main, "AssertSmokeBackgroundServicesInactive");
        report.AppendLine("CASE emergency settings: defaults, independent auto-save, durable reload, failure rollback, Cart/mail isolation, narrow and enlarged-text scrolling; no live actions.");
    }

    private static void CheckNoSaveButtons(Control root, string context)
    {
        Check(!Descendants(root).OfType<Button>().Any(b => b.Text.Trim().StartsWith("Save", StringComparison.OrdinalIgnoreCase)),
            context + ": a manual Save button remains.");
    }

    private static void CheckRulesEditor()
    {
        Type ruleType = app.GetType("_4RTools.Model.Vanilla.Automation.AutomationRuleSettings", true);
        Type listType = typeof(List<>).MakeGenericType(ruleType);
        foreach (float scale in new[] { 1F, 1.25F })
        {
            caseNumber++;
            int writes = 0;
            object durable = null;
            Action<object> persist = value => { durable = value; writes++; };
            var valueParameter = Expression.Parameter(listType, "rules");
            Delegate callback = Expression.Lambda(typeof(Action<>).MakeGenericType(listType),
                Expression.Invoke(Expression.Constant(persist), Expression.Convert(valueParameter, typeof(object))), valueParameter).Compile();
            using (Form dialog = (Form)Activator.CreateInstance(app.GetType("_4RTools.Forms.VanillaRulesEditor", true),
                new[] { Activator.CreateInstance(listType), (object)(int)Keys.F12, callback }))
            {
                if (scale != 1F) { dialog.Scale(new SizeF(scale, scale)); dialog.Font = new Font(dialog.Font.FontFamily, dialog.Font.Size * scale); }
                dialog.Show(); Pump();
                Check(writes == 0, "Opening rule editor wrote defaults.");
                CheckNoSaveButtons(dialog, "Rules editor");
                Call(dialog, "AddRule");
                Check(writes == 1 && ((IList)durable).Count == 1, "Explicit Add rule did not persist its valid disabled rule.");
                var name = (TextBox)Field(dialog, "name");
                name.Text = "Synthetic autosaved rule"; Call(name, "OnLeave", EventArgs.Empty);
                Check(writes == 2 && (string)ReadProperty(((IList)durable)[0], "Name") == name.Text,
                    "Completed rule edit did not auto-save.");
                Check(FullyVisible(name, dialog) && FullyVisible((Control)Field(dialog, "saveStatus"), dialog),
                    "Rule editor fields/status clipped at scale " + scale);
                var close = Descendants(dialog).OfType<Button>().SingleOrDefault(b => b.Text == "Close");
                Check(close != null && FullyVisible(close, dialog), "Rules Close action clipped at scale " + scale);
                SaveScreenshot(dialog, Path.Combine(output, "rules-autosave-" + (int)(scale * 100) + ".png"));
                dialog.Close();
            }
        }
        report.AppendLine("CASE rules editor: no Save; explicit creation, automatic committed edit, no initialization write, normal/enlarged layout; mock persistence only.");
    }

    private static decimal EmergencyValue(object service, string property)
    {
        object value = service.GetType().GetProperty("EmergencySettings").GetValue(service, null);
        return (decimal)value.GetType().GetProperty(property).GetValue(value, null);
    }

    private static void CheckFarmingOptions(Form main)
    {
        caseNumber++;
        object service = Field(main, "integratedWeightAlertService");
        Type panelType = app.GetType("_4RTools.Model.Vanilla.VanillaWeightAlertsPanel", true);
        using (var host = new Form { ClientSize = new Size(1180, 720), StartPosition = FormStartPosition.Manual, Location = Point.Empty })
        using (var panel = (ScrollableControl)Activator.CreateInstance(panelType, new[] { service }))
        {
            host.Controls.Add(panel); host.Show(); Pump();
            CheckBox emergencyMail = (CheckBox)Field(panel, "emergencySendEmail");
            CheckBox completedClose = (CheckBox)Field(panel, "closeWhenComplete");
            CheckBox normalMail = (CheckBox)Field(panel, "enabled");
            Check(!emergencyMail.Checked && !completedClose.Checked, "New emergency-mail/completion-close switches must default off.");
            completedClose.Checked = true;
            emergencyMail.Checked = true;
            Check(!emergencyMail.Checked && ((Label)Field(panel, "emergencySaveStatus")).Text.StartsWith("Not saved:"),
                "Emergency mail without saved SMTP must restore its unchecked state and show the save error.");
            Check(completedClose.Checked && (bool)ReadProperty(ReadProperty(service, "Settings"), "CloseClientWhenFarmingComplete"),
                "Completion-close checkbox must save immediately and survive the unrelated emergency error.");
            ((TextBox)Field(panel, "smtpHost")).Text = "smtp.example.invalid";
            ((TextBox)Field(panel, "fromAddress")).Text = "sender@example.invalid";
            ((TextBox)Field(panel, "toAddress")).Text = "recipient@example.invalid";
            normalMail.Checked = false; Call(panel, "FlushPendingSettings");
            emergencyMail.Checked = true; Pump();
            Check(emergencyMail.Checked && (bool)ReadProperty(ReadProperty(service, "EmergencySettings"), "SendEmail")
                && !(bool)ReadProperty(ReadProperty(service, "Settings"), "Enabled"),
                "Emergency e-mail must save independently with normal mail off.");
            Check((bool)ReadProperty(ReadProperty(service, "Settings"), "CloseClientWhenFarmingComplete"),
                "Completion-close checkbox did not auto-save.");
            using (var reopened = (Control)Activator.CreateInstance(panelType, new[] { service }))
                Check(((CheckBox)Field(reopened, "emergencySendEmail")).Checked
                    && ((CheckBox)Field(reopened, "closeWhenComplete")).Checked, "Farming checkboxes did not reload their saved values.");
            completedClose.Checked = false;
            emergencyMail.Checked = false;
            Check(!(bool)ReadProperty(ReadProperty(service, "Settings"), "CloseClientWhenFarmingComplete"),
                "Completion-close opt-out did not auto-save.");
            CheckWeightSaveFailureAndPassword(panel, service);
            host.ClientSize = new Size(760, 560); panel.Scale(new SizeF(1.5F, 1.5F));
            panel.Font = new Font(panel.Font.FontFamily, panel.Font.Size * 1.5F);
            Pump(); CheckWeightControlsReachable(host, panel, "farming flags narrow/enlarged");
            panel.AutoScrollPosition = Point.Empty; Pump();
            SaveScreenshot(host, Path.Combine(output, "farming-options.png"));
        }
        Call(main, "AssertSmokeBackgroundServicesInactive");
        report.AppendLine("CASE farming options: defaults off; independent checkbox auto-save, durable reload, failed-write preservation and SMTP password protection; no SMTP/network call.");
    }

    private static void CheckWeightSaveFailureAndPassword(Control panel, object service)
    {
        var host = (TextBox)Field(panel, "smtpHost");
        var port = (NumericUpDown)Field(panel, "smtpPort");
        int portBefore = (int)ReadProperty(ReadProperty(service, "Settings"), "SmtpPort");
        port.Focus(); port.Text = "70000"; host.Focus(); Pump(); Pump(); Pump(); Pump();
        Call(panel, "FlushPendingSettings");
        Check((int)ReadProperty(ReadProperty(service, "Settings"), "SmtpPort") == portBefore
            && ((Label)Field(panel, "saveStatus")).Text.StartsWith("Not saved:"),
            "Invalid typed SMTP port must remain rejected after focus validation and debounce; type=" + port.GetType().Name
                + "; raw=" + port.Text + "; active=" + ReadProperty(ReadProperty(service, "Settings"), "SmtpPort")
                + "; status=" + ((Label)Field(panel, "saveStatus")).Text);
        port.Text = portBefore.ToString(); Call(panel, "FlushPendingSettings");
        object store = ReadProperty(service, "Store");
        string path = (string)Field(store, "path");
        string previous = (string)ReadProperty(ReadProperty(service, "Settings"), "SmtpHost");
        string blocker = Path.Combine(output, "weight-autosave-blocker");
        File.WriteAllText(blocker, "synthetic file blocker");
        try
        {
            SetField(store, "path", Path.Combine(blocker, "settings.json"));
            host.Text = "new-host.example.invalid"; Call(panel, "FlushPendingSettings");
            Check((string)ReadProperty(ReadProperty(service, "Settings"), "SmtpHost") == previous
                && ((Label)Field(panel, "saveStatus")).Text.StartsWith("Not saved:"),
                "Failed Weight auto-save changed active settings or hid its error.");
            Call(panel, "RefreshStatus");
            Check(((Label)Field(panel, "saveStatus")).Text.StartsWith("Not saved:"), "Live polling erased auto-save error.");
        }
        finally { SetField(store, "path", path); File.Delete(blocker); }
        host.Text = previous; Call(panel, "FlushPendingSettings");
        var password = (TextBox)Field(panel, "smtpPassword");
        Check(password.UseSystemPasswordChar && password.Text.Length == 0, "SMTP replacement must start empty and masked.");
        password.Text = "synthetic-smtp-password"; Call(panel, "FlushPendingSettings");
        string protectedValue = (string)ReadProperty(ReadProperty(service, "Settings"), "ProtectedSmtpPassword");
        Check(password.Text.Length == 0 && !string.IsNullOrEmpty(protectedValue) && protectedValue != "synthetic-smtp-password",
            "SMTP password must be encrypted and cleared from the editor after saving.");
        Call(panel, "SaveField", password);
        Check((string)ReadProperty(ReadProperty(service, "Settings"), "ProtectedSmtpPassword") == protectedValue,
            "Blank SMTP replacement erased stored password.");
        ((Button)Field(panel, "clearPassword")).PerformClick();
        Check(string.IsNullOrEmpty((string)ReadProperty(ReadProperty(service, "Settings"), "ProtectedSmtpPassword")),
            "Explicit Clear password action did not persist.");
    }

    private static void CheckWeightControlsReachable(Form host, ScrollableControl panel, string context)
    {
        // An earlier edit must not make WinForms' focus scrolling pull the panel
        // back while this check navigates to a different control.
        var container = panel as ContainerControl;
        if (container != null) container.ActiveControl = null;
        host.ActiveControl = null;
        foreach (string field in new[] { "emergencyWeight", "emergencySp", "emergencyHp", "emergencySendEmail", "closeWhenComplete", "autoThreshold", "autoRearm",
            "autobattleStopHotkey", "inventoryHotkey", "cartHotkey", "smtpHost", "smtpPassword", "toAddress",
            "clearPassword", "test", "clearHold", "clearEmergency" })
        {
            Control control = (Control)Field(panel, field);
            panel.ScrollControlIntoView(control); Pump();
            Check(FullyVisible(control, host), context + ": Weight control is not reachable by scrolling: " + field
                + "; bounds=" + BoundsIn(control, host) + "; scroll=" + panel.AutoScrollPosition + "; extent=" + panel.AutoScrollMinSize);
        }
    }

    private static void CheckFarmingStopCards(Form main, object recovery)
    {
        caseNumber++;
        SeedAccounts(recovery, 2);
        Control fleet = (Control)Field(main, "integratedFleetDashboard");
        Array cards = (Array)Field(fleet, "cards");
        IList catalog = (IList)Field(recovery, "accountCatalog");
        Type accountType = app.GetType("_4RTools.Model.Vanilla.VanillaReconnectAccount", true);
        Type infoType = app.GetType("_4RTools.Model.Vanilla.VanillaFleetClientInfo", true);
        Type identityType = app.GetType("_4RTools.Model.Vanilla.VanillaCharacterIdentity", true);
        Type stopType = app.GetType("_4RTools.Model.Vanilla.VanillaFarmingStopStatus", true);
        Type runtimeType = app.GetType("_4RTools.Model.Vanilla.VanillaReconnectStatus", true);
        Array profiles = Array.CreateInstance(accountType, 2);
        Array clients = Array.CreateInstance(infoType, 2);
        Array stops = Array.CreateInstance(stopType, 2);
        Array runtimes = Array.CreateInstance(runtimeType, 0);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (int i = 0; i < 2; i++)
        {
            profiles.SetValue(catalog[i], i);
            object info = Activator.CreateInstance(infoType);
            string character = (string)ReadProperty(catalog[i], "CharacterName");
            string user = (string)ReadProperty(catalog[i], "UserName");
            Property(info, "ProcessId", 12064 + i); Property(info, "CharacterName", character);
            Property(info, "NameVerified", true); Property(info, "HpVerified", true); Property(info, "SpVerified", true);
            Property(info, "CurrentHP", (uint?)(2100 + i)); Property(info, "MaxHP", (uint?)4000);
            Property(info, "CurrentSP", (uint?)300); Property(info, "MaxSP", (uint?)500);
            Property(info, "Location", "mock_field (10, 20)");
            Property(info, "Identity", Activator.CreateInstance(identityType, All, null,
                new object[] { 12064 + i, Guid.NewGuid(), now, character, user, i + 1 }, null));
            // Reversed polling order must not exchange the characters' cards.
            clients.SetValue(info, 1 - i);
            object stop = Activator.CreateInstance(stopType);
            Property(stop, "AccountId", ReadProperty(catalog[i], "Id")); Property(stop, "UserName", user);
            Property(stop, "CharacterName", character); Property(stop, "Status", i == 0 ? "Emergency" : "Completed");
            Property(stop, "Detail", i == 0
                ? "2026-09-27 12:05:03: Weight 56.0% (2800/5000) >50.0%, SP 20.0% (100/500) <25.0%, HP 40.0% (400/1000) <50.0%. Close: client exited; exit confirmed for the observed process. Email: failed (SMTP connection timed out while contacting the saved mail host); retry scheduled in 5 minutes."
                : "2026-09-27 12:06:08: Farming complete: Cart 99.0% (9900/10000) AND Weight 50.0% (2500/5000); Autobattle STOP verified. Close: client exited. Email: retry scheduled.");
            stops.SetValue(stop, i);
        }
        Array firstStop = Array.CreateInstance(stopType, 1); firstStop.SetValue(stops.GetValue(0), 0);
        Call(fleet, "RenderSnapshot", clients, profiles, runtimes, firstStop, now, null); Pump();
        Check(((Label)Field(cards.GetValue(0), "title")).Text == "Mock character 1"
            && ((Label)Field(cards.GetValue(0), "state")).Text == "Emergency"
            && !((Label)Field(cards.GetValue(0), "hp")).Text.Contains("2100"),
            "A closed emergency card disappeared, changed identity or retained stale HP.");
        Check(((Label)Field(cards.GetValue(1), "title")).Text == "Mock character 2"
            && ((Label)Field(cards.GetValue(1), "hp")).Text.Contains("2101/4000"),
            "Retaining the offline emergency card hid or exchanged its healthy sibling.");

        Array none = Array.CreateInstance(infoType, 0);
        Font originalFont = fleet.Font;
        Action restoreRoster = SeedFarmingStopRoster(recovery, catalog);
        try
        {
            foreach (var viewport in new[] { new Size(1904, 981), new Size(1050, 700) })
            {
                ResizeNativeViewport(main, viewport.Width, viewport.Height);
                fleet.Font = new Font(originalFont.FontFamily, originalFont.Size * (viewport.Width < 1200 ? 1.5F : 1F));
                Call(fleet, "RenderSnapshot", none, profiles, runtimes, stops, now, null); Pump();
                Call(main, "UpdateVanillaFleetHeight"); Pump();
                for (int i = 0; i < 2; i++)
                {
                    Label detail = (Label)Field(cards.GetValue(i), "stopDetail");
                    Check(detail.Visible && detail.Text == (string)ReadProperty(stops.GetValue(i), "Detail")
                        && FullyVisible(detail, main), viewport + ": exact stopped-character reason is hidden or clipped.");
                    Check(((Label)Field(cards.GetValue(i), "state")).Text == (i == 0 ? "Emergency" : "Completed"),
                        viewport + ": typed farming status was replaced with generic Error.");
                    Check(!((Label)Field(cards.GetValue(i), "hp")).Text.Contains("/")
                        && !((Label)Field(cards.GetValue(i), "sp")).Text.Contains("/"),
                        viewport + ": a closed card presents historical vitals as live.");
                    foreach (Label label in Descendants((Control)cards.GetValue(i)).OfType<Label>().Where(l => l.Visible))
                        Check(label.Height >= label.Font.Height && FullyVisible(label, main), viewport + ": stopped card label clipped: " + label.Text);
                }
                SaveScreenshot(main, Path.Combine(output, "farming-stops-" + viewport.Width + ".png"));
            }
        }
        finally { restoreRoster(); }

        // Reusing the row ID or PID for a different identity cannot transplant history or vitals.
        string oldCharacter = (string)ReadProperty(profiles.GetValue(0), "CharacterName");
        Property(profiles.GetValue(0), "CharacterName", "Different character");
        Call(fleet, "RenderSnapshot", clients, profiles, runtimes, stops, now, null); Pump();
        Check(!((Label)Field(cards.GetValue(0), "stopDetail")).Visible
            && !((Label)Field(cards.GetValue(0), "hp")).Text.Contains("/"),
            "An edited/reused account row inherited another character's stop or HP.");
        Property(profiles.GetValue(0), "CharacterName", oldCharacter);
        Array noStops = Array.CreateInstance(stopType, 0);
        Call(fleet, "RenderSnapshot", clients, profiles, runtimes, noStops, now.AddSeconds(4), null); Pump();
        Check(!((Label)Field(cards.GetValue(0), "hp")).Text.Contains("/")
            && !((Label)Field(cards.GetValue(1), "hp")).Text.Contains("/"),
            "Stale identity samples supplied current vitals to configured cards.");
        object ordinaryFailure = Activator.CreateInstance(runtimeType);
        Property(ordinaryFailure, "AccountId", ReadProperty(profiles.GetValue(0), "Id"));
        Array failedRuntime = Array.CreateInstance(runtimeType, 1); failedRuntime.SetValue(ordinaryFailure, 0);
        foreach (string stage in new[] { "Error", "Stopped" })
        {
            Property(ordinaryFailure, "Stage", Enum.Parse(app.GetType("_4RTools.Model.Vanilla.VanillaReconnectStage", true), stage));
            Property(ordinaryFailure, "Detail", stage == "Error"
                ? "Recovery stopped: the expected login controls were unavailable after the bounded wait."
                : "Stopped by user; automatic recovery is paused.");
            Call(fleet, "RenderSnapshot", none, profiles, failedRuntime, noStops, now, null); Pump();
            Label detail = (Label)Field(cards.GetValue(0), "stopDetail");
            Check(((Label)Field(cards.GetValue(0), "state")).Text == stage && detail.Visible
                && detail.Text == (string)ReadProperty(ordinaryFailure, "Detail") && FullyVisible(detail, main),
                "Offline runtime " + stage + " lost its visible reason without a farming ledger event.");
        }
        Property(ordinaryFailure, "Stage", Enum.Parse(app.GetType("_4RTools.Model.Vanilla.VanillaReconnectStage", true), "Online"));
        Property(ordinaryFailure, "ProcessId", (int?)12064);
        Property(ordinaryFailure, "Detail", "");
        object unreadable = Activator.CreateInstance(infoType);
        Property(unreadable, "ProcessId", 12064);
        Property(unreadable, "Error", "Synthetic read unavailable; native error 5.");
        Property(unreadable, "CurrentHP", (uint?)999); Property(unreadable, "MaxHP", (uint?)1000);
        Array readErrors = Array.CreateInstance(infoType, 1); readErrors.SetValue(unreadable, 0);
        Call(fleet, "RenderSnapshot", readErrors, profiles, failedRuntime, noStops, now, null); Pump();
        Check(((Label)Field(cards.GetValue(0), "stopDetail")).Text.Contains("Synthetic read unavailable")
            && ((Label)Field(cards.GetValue(0), "stopDetail")).Visible
            && !((Label)Field(cards.GetValue(0), "hp")).Text.Contains("/"),
            "Assigned-PID read error lost its reason or authorized unverified vitals.");
        fleet.Font = originalFont; SeedFleet(main); ResizeNativeViewport(main, 1920, 1020);
        Call(main, "UpdateVanillaFleetHeight"); Pump();
        Call(main, "AssertSmokeBackgroundServicesInactive");
        report.AppendLine("CASE stopped fleet cards: stable exact identities; healthy sibling retained; both offline statuses/reasons visible; stale or edited identity rejected; Full HD and enlarged narrow rendering.");
    }

    private static Action SeedFarmingStopRoster(object recovery, IList catalog)
    {
        object supervisor = Field(recovery, "supervisor");
        IDictionary holds = (IDictionary)Field(supervisor, "farmingEmergencyHolds");
        IDictionary notices = (IDictionary)Field(supervisor, "farmingStops");
        IDictionary runtimes = (IDictionary)Field(supervisor, "runtimes");
        object hold = Activator.CreateInstance(supervisor.GetType().GetNestedType("FarmingEmergencyHold", All), true);
        string user = (string)ReadProperty(catalog[0], "UserName"), name = (string)ReadProperty(catalog[0], "CharacterName");
        string key = user.Length + ":" + user + name.Length + ":" + name;
        Property(hold, "UserName", user); Property(hold, "CharacterName", name);
        Property(hold, "ObservedAt", DateTimeOffset.UtcNow); Property(hold, "Detail", "Synthetic emergency reason");
        holds.Add(key, hold);
        object notice = Activator.CreateInstance(app.GetType("_4RTools.Model.Vanilla.VanillaFarmingStopNotice", true));
        string eventId = Guid.NewGuid().ToString("N");
        Property(notice, "EventId", eventId);
        Property(notice, "Kind", Enum.Parse(app.GetType("_4RTools.Model.Vanilla.VanillaFarmingStopKind", true), "Completed"));
        Property(notice, "AccountId", ReadProperty(catalog[1], "Id"));
        Property(notice, "UserName", ReadProperty(catalog[1], "UserName"));
        Property(notice, "CharacterName", ReadProperty(catalog[1], "CharacterName"));
        Property(notice, "ObservedAt", DateTimeOffset.UtcNow); Property(notice, "Detail", "Synthetic farming completion reason");
        notices.Add(eventId, notice);
        object[] owners = { runtimes[ReadProperty(catalog[0], "Id")], runtimes[ReadProperty(catalog[1], "Id")] };
        object[] pids = owners.Select(o => Field(o, "ProcessId")).ToArray();
        foreach (object owner in owners) SetField(owner, "ProcessId", null);
        Call(recovery, "RefreshAccountSupplementalColumnsCore");
        DataGridView grid = (DataGridView)Field(recovery, "accounts");
        for (int i = 0; i < 2; i++)
        {
            DataGridViewRow row = grid.Rows.Cast<DataGridViewRow>().Single(r => Equals(r.Tag, ReadProperty(catalog[i], "Id")));
            Check(Convert.ToString(row.Cells["RuntimeStatus"].Value) == (i == 0 ? "Emergency" : "Completed"),
                "Recovery roster did not use the real typed stop projection.");
            Check(row.Cells["RuntimeStatus"].ToolTipText.Contains("Synthetic"), "Recovery roster lost the exact stop reason.");
        }
        return () =>
        {
            holds.Remove(key); notices.Remove(eventId);
            for (int i = 0; i < owners.Length; i++) SetField(owners[i], "ProcessId", pids[i]);
            Call(recovery, "RefreshAccountSupplementalColumnsCore");
        };
    }

    private static void CheckCharacterDiscovery(Form main, object recovery)
    {
        caseNumber++;
        object fleet = Field(main, "integratedFleetMonitor");
        Type type = app.GetType("_4RTools.Model.Vanilla.VanillaCharacterIdentity", true);
        Array observed = Array.CreateInstance(type, 1);
        object identity = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { 99101, Guid.NewGuid(), DateTimeOffset.UtcNow, "Auto discovered mock", "mock-login", null }, null);
        observed.SetValue(identity, 0);
        fleet.GetType().GetField("characterCache", All).SetValue(fleet, observed);
        Call(recovery, "DiscoverCharacters", false);
        Call(recovery, "DiscoverCharacters", false);
        DataGridView grid = (DataGridView)Field(recovery, "accounts");
        var found = grid.Rows.Cast<DataGridViewRow>().Where(r => Convert.ToString(r.Cells["CharacterName"].Value) == "Auto discovered mock").ToArray();
        Check(found.Length == 1, "Discovery added duplicate or missing character rows.");
        if (found.Length == 1)
        {
            Check(Convert.ToString(found[0].Cells["Enabled"].Value) == "No", "Discovery enabled a new client.");
            Check(Convert.ToString(found[0].Cells["User"].Value) == "mock-login", "Username was not populated in the table.");
            Check(Convert.ToString(found[0].Cells["Slot"].Value) == "—", "Unknown discovered slot silently became slot 1.");
            Check(Convert.ToString(found[0].Cells["Secret"].Value) == "Not set", "Discovery populated a password.");
            Check(Convert.ToString(found[0].Cells["AccountProxy"].Value) == "Not set", "Discovery guessed a proxy.");
        }
        Check(Field(recovery, "characterDiscoveryTimer") == null, "Explicit inert discovery started a background timer.");
        SaveScreenshot(main, Path.Combine(output, "19-character-discovery.png"));
        report.AppendLine("CASE 19 character discovery: one disabled row; verified username and unknown slot; no duplicate; no background observer.");
    }

    private static void CheckLegacyUsernameDiscovery(Form main, object recovery)
    {
        caseNumber++;
        SeedAccounts(recovery, 2);
        IList catalog = (IList)Field(recovery, "accountCatalog");
        Type rowType = catalog[0].GetType();
        for (int i = 0; i < 2; i++)
        {
            Property(catalog[i], "CharacterName", "");
            Property(catalog[i], "UserName", "mock-login-" + i);
            Property(catalog[i], "ProtectedPassword", "inert-encrypted-placeholder-" + i);
            Property(catalog[i], "CharacterSlot", 2);
        }
        object orphan = Activator.CreateInstance(rowType);
        Property(orphan, "Enabled", false); Property(orphan, "Label", "Discovered Alpha");
        Property(orphan, "CharacterName", "Discovered Alpha"); Property(orphan, "CharacterSlot", null);
        Property(orphan, "ProxyNeedsConfiguration", true); catalog.Add(orphan);
        Call(recovery, "SynchronizeSupervisorAccountsFromCatalog");
        object supervisor = Field(recovery, "supervisor");
        Call(supervisor, "Apply", Field(recovery, "settings"), false);
        Type identityType = app.GetType("_4RTools.Model.Vanilla.VanillaCharacterIdentity", true);
        Array observed = Array.CreateInstance(identityType, 2);
        for (int i = 0; i < 2; i++) observed.SetValue(Activator.CreateInstance(identityType, All, null,
            new object[] { 99101 + i, Guid.NewGuid(), DateTimeOffset.UtcNow, i == 0 ? "Discovered Alpha" : "Discovered Beta", "mock-login-" + i, null }, null), i);
        object fleet = Field(main, "integratedFleetMonitor");
        SetField(fleet, "characterCache", observed);
        Call(recovery, "DiscoverCharacters", false);
        Call(recovery, "DiscoverCharacters", false);
        DataGridView grid = (DataGridView)Field(recovery, "accounts");
        Check(grid.Rows.Count == 2, "Username migration left or created duplicate character rows.");
        if (grid.Rows.Count == 2)
        {
            Check(Convert.ToString(grid.Rows[0].Tag) == "layout-account-0", "Migration replaced configured row identity.");
            Check(Convert.ToString(grid.Rows[0].Cells["CharacterName"].Value) == "Discovered Alpha", "Legacy character name stayed blank.");
            Check(Convert.ToString(grid.Rows[1].Cells["CharacterName"].Value) == "Discovered Beta", "Second legacy character name stayed blank.");
            Check(grid.Rows.Cast<DataGridViewRow>().All(r => Convert.ToString(r.Cells["Enabled"].Value) == "Yes"
                && Convert.ToString(r.Cells["Slot"].Value) == "2" && Convert.ToString(r.Cells["Secret"].Value) == "Encrypted"),
                "Migration lost enabled flags, slots or encrypted credentials.");
        }
        Check(Field(recovery, "characterDiscoveryTimer") == null, "Migration test activated background discovery.");
        SaveScreenshot(main, Path.Combine(output, "21-legacy-username-reconciliation.png"));
        report.AppendLine("CASE 21 username reconciliation: configured IDs, slots, passwords and enable flags retained; two rows, no duplicates.");
    }

    private static void CheckUsernameDiagnostics()
    {
        caseNumber++;
        Type formType = app.GetType("_4RTools.Forms.VanillaDiagnosticsForm", true);
        Type fieldType = app.GetType("_4RTools.Model.Vanilla.VanillaField", true);
        Type validationType = app.GetType("_4RTools.Model.Vanilla.StateValidation", true);
        Type valueType = app.GetType("_4RTools.Model.Vanilla.StateValue", true);
        Type textType = app.GetType("_4RTools.Model.Vanilla.StateValue`1", true).MakeGenericType(typeof(string));
        IDictionary fields = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(fieldType, valueType));
        string[] names = { "UserName", "UserNameMirror" };
        ulong[] addresses = { 0x011343F8, 0x01139159 };
        for (int i = 0; i < 2; i++)
        {
            object value = Activator.CreateInstance(textType, All, null, new object[] { "mock-login" }, null);
            Property(value, "IsAvailable", true); Property(value, "Validation", Enum.Parse(validationType, "Valid"));
            Property(value, "Address", (ulong?)addresses[i]); Property(value, "LastObservedAtUtc", (DateTimeOffset?)DateTimeOffset.UtcNow);
            Property(value, "Evidence", "Synthetic diagnostic sample at the supplied module-relative username mapping.");
            fields.Add(Enum.Parse(fieldType, names[i]), value);
        }
        object snapshot = Activator.CreateInstance(app.GetType("_4RTools.Model.Vanilla.VanillaClientState", true));
        Property(snapshot, "Fields", fields);
        using (Form dialog = (Form)Activator.CreateInstance(formType, All, null, new object[] { null, false }, null))
        {
            SetField(dialog, "latest", snapshot); Call(dialog, "UpdateValuesGrid"); dialog.Show(); Pump();
            var grid = (DataGridView)Field(dialog, "values");
            Check(grid.Rows.Count == 2, "Diagnostics omits a username source.");
            for (int i = 0; i < grid.Rows.Count; i++)
            {
                Check(Convert.ToString(grid.Rows[i].Cells[0].Value) == names[i]
                    && Convert.ToString(grid.Rows[i].Cells[1].Value) == "mock-login"
                    && Convert.ToString(grid.Rows[i].Cells[3].Value) == "0x" + addresses[i].ToString("X"), "Diagnostics lost a username value/address.");
            }
            Check(!((System.Windows.Forms.Timer)Field(dialog, "timer")).Enabled && Field(dialog, "source") == null,
                "Diagnostics test opened a real reader or started polling.");
            CheckNoSaveButtons(dialog, "Diagnostics");
            object profile = Field(dialog, "settingsProfile");
            object settings = ReadProperty(profile, "VanillaDiagnostics");
            string mapBefore = (string)ReadProperty(settings, "MemoryMapJson");
            object pending = Field(dialog, "autosave");
            Check(!(bool)ReadProperty(pending, "HasPending"), "Loading diagnostics scheduled a save.");
            var map = (TextBox)Field(dialog, "mapEditor");
            var poll = (NumericUpDown)Field(dialog, "interval");
            map.Text = "{ invalid"; Call(map, "OnLeave", EventArgs.Empty);
            Check((string)ReadProperty(ReadProperty(profile, "VanillaDiagnostics"), "MemoryMapJson") == mapBefore
                && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                "Invalid memory map changed active diagnostics settings or lost its error.");
            poll.Value = 750; Call(poll, "OnLeave", EventArgs.Empty);
            Check((int)ReadProperty(ReadProperty(profile, "VanillaDiagnostics"), "PollIntervalMilliseconds") == 750
                && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                "Valid diagnostics field must save independently while another field's error remains visible.");
            const string validMap = "{\"SchemaVersion\":1,\"ProcessName\":\"Vanilla MMO\",\"Fields\":{}}";
            map.Text = validMap; Call(map, "OnLeave", EventArgs.Empty);
            Check((string)ReadProperty(ReadProperty(profile, "VanillaDiagnostics"), "MemoryMapJson") == validMap
                && ((Label)Field(dialog, "saveStatus")).Text == "Saved",
                "Valid completed memory map did not auto-save.");
            Check(!((System.Windows.Forms.Timer)Field(dialog, "timer")).Enabled && Field(dialog, "source") == null,
                "Diagnostics auto-save activated polling or connected a reader.");
            SaveScreenshot(dialog, Path.Combine(output, "22-username-diagnostics.png"));
        }
        report.AppendLine("CASE 22 native diagnostics: both username values and addresses visible; no reader, process enumeration or polling.");
    }

    private static void CheckCharacterEditor(Form main, object recovery)
    {
        caseNumber++;
        Type rowType = app.GetType("_4RTools.Model.Vanilla.VanillaReconnectAccount", true);
        object row = Activator.CreateInstance(rowType);
        Property(row, "Enabled", false); Property(row, "CharacterName", "Auto discovered mock");
        Property(row, "CharacterSlot", null); Property(row, "ProxyNeedsConfiguration", true);
        int writes = 0;
        bool reject = false;
        object durable = null;
        Action<object> persist = value =>
        {
            if (reject) throw new IOException("Synthetic write failure");
            durable = value; writes++;
        };
        using (Form dialog = NewCharacterDialog(recovery, row, false, persist))
        {
            dialog.Show(main); Pump();
            Check(writes == 0, "Opening character editor must not persist defaults.");
            CheckNoSaveButtons(dialog, "Character editor");
            foreach (string name in new[] { "enabled", "label", "user", "slot", "character", "password", "proxy", "hotkey",
                "smartTeleport", "teleportIdle", "teleportHotkey" })
                Check(FullyVisible((Control)Field(dialog, name), dialog), "Character editor clipped field: " + name);
            Check(((TextBox)Field(dialog, "slot")).Text == "", "Character editor invented slot 1.");
            Check(((ComboBox)Field(dialog, "proxy")).SelectedIndex == -1, "Character editor invented proxy.");
            Check(!((CheckBox)Field(dialog, "smartTeleport")).Checked, "Smart Teleport must default OFF for a newly discovered character.");
            Check(((NumericUpDown)Field(dialog, "teleportIdle")).Value == 60, "Smart Teleport default idle time must be 60 seconds.");
            Check(((TextBox)Field(dialog, "teleportHotkey")).Text.Contains("press hotkey"), "Smart Teleport live hotkey capture field is missing its unset state.");
            TextBox description = (TextBox)Field(dialog, "label");
            description.Text = "Edited synthetic character";
            Check(writes == 0, "Character text saved before its edit was committed.");
            Call(description, "OnValidated", EventArgs.Empty);
            Check(writes == 1 && (string)ReadProperty(durable, "Label") == description.Text,
                "Existing character edit did not auto-save.");
            var idle = (NumericUpDown)Field(dialog, "teleportIdle");
            Check(!(bool)Field(dialog, "dirty"), "Accepted account edit did not reset dirty state before numeric rejection test.");
            idle.Text = "70000"; Call(idle, "OnValidated", EventArgs.Empty);
            Check(writes == 1 && (int)ReadProperty(ReadProperty(dialog, "Account"), "SmartTeleportIdleSeconds") == 60
                && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                "Invalid account numeric text must not clamp into a saved value.");
            idle.Text = "60"; Call(idle, "OnValidated", EventArgs.Empty);
            int beforeToggle = writes;
            ((CheckBox)Field(dialog, "weightEmail")).Checked = !((CheckBox)Field(dialog, "weightEmail")).Checked;
            Check(writes == beforeToggle + 1, "Character checkbox did not save immediately.");
            int beforeFailure = writes;
            reject = true; description.Text = "Unsaved synthetic failure"; Call(description, "OnValidated", EventArgs.Empty);
            Check(writes == beforeFailure && (string)ReadProperty(ReadProperty(dialog, "Account"), "Label") == "Edited synthetic character"
                && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                "Failed character save must preserve the accepted account and show its error.");
            dialog.Close(); Pump();
            Check(!dialog.IsDisposed && dialog.Visible && ((Button)Field(dialog, "discard")).Visible,
                "Failed account persistence on Close must keep the editor and retryable draft visible.");
            reject = false; description.Text = "Edited synthetic character"; Call(description, "OnValidated", EventArgs.Empty);
            SaveScreenshot(dialog, Path.Combine(output, "20-character-editor.png"));
            dialog.Close();
        }
        using (Form reopened = NewCharacterDialog(recovery, durable, false, persist))
        {
            Check(((TextBox)Field(reopened, "label")).Text == "Edited synthetic character", "Saved character was not restored in a reopened editor.");
            int previousWrites = writes; reopened.Show(main); Pump();
            Check(previousWrites == writes, "Reopening saved character wrote unchanged values.");
            reopened.Scale(new SizeF(1.5F, 1.5F)); reopened.Font = new Font(reopened.Font.FontFamily, reopened.Font.Size * 1.5F);
            reopened.ClientSize = new Size(820, 740); Pump();
            foreach (string name in new[] { "label", "password", "teleportHotkey", "saveStatus" })
            {
                var control = (Control)Field(reopened, name); reopened.ScrollControlIntoView(control); Pump();
                Check(FullyVisible(control, reopened), "Scaled character editor clips " + name);
            }
            reopened.Close();
        }
        using (Form failure = NewCharacterDialog(recovery, durable, false, value => { throw new IOException("Synthetic close failure"); }))
        {
            failure.Show(main); Pump();
            ((TextBox)Field(failure, "label")).Text = "Unsaved discard fixture";
            failure.Close(); Pump();
            Check(!failure.IsDisposed && failure.Visible, "First failed save on close must keep the editor open.");
            ((Button)Field(failure, "discard")).PerformClick();
            Check(failure.IsDisposed && (string)ReadProperty(durable, "Label") == "Edited synthetic character",
                "Explicit Discard unsaved failed to retain earlier saved account settings.");
        }
        object newRow = Activator.CreateInstance(rowType);
        Property(newRow, "Enabled", false);
        int beforeCreate = writes;
        using (Form added = NewCharacterDialog(recovery, newRow, true, persist))
        {
            added.Show(main); Pump();
            ((TextBox)Field(added, "label")).Text = "New synthetic";
            Call((Control)Field(added, "label"), "OnValidated", EventArgs.Empty);
            ((Button)Field(added, "create")).PerformClick();
            Check(writes == beforeCreate && ((Label)Field(added, "saveStatus")).Text.StartsWith("Not saved:"),
                "An incomplete new identity must not be persisted.");
            ((TextBox)Field(added, "user")).Text = "synthetic-user";
            ((ComboBox)Field(added, "character")).Text = "Synthetic character";
            ((Button)Field(added, "create")).PerformClick();
            Check(writes == beforeCreate + 1 && !((Button)Field(added, "create")).Visible,
                "Creating a valid disabled identity must persist once and switch to auto-save.");
            added.Close();
        }
        CheckCharacterPasswordPreservation(recovery, rowType);
        CheckCharacterDurableAutoSave(recovery);
        report.AppendLine("CASE 20 character editor: no Save; automatic committed edits, write-failure feedback, valid-only Create, password preservation and enlarged controls; mock callbacks only.");
    }

    private static void CheckCharacterDurableAutoSave(object recovery)
    {
        var restart = (NumericUpDown)Field(recovery, "movementRestartSeconds");
        object recoverySupervisor = Field(recovery, "supervisor");
        int previousRestart = (int)ReadProperty(ReadProperty(recoverySupervisor, "Settings"), "MovementRestartSeconds");
        restart.Text = "70000"; Call(restart, "OnLeave", EventArgs.Empty);
        Check((int)ReadProperty(ReadProperty(recoverySupervisor, "Settings"), "MovementRestartSeconds") == previousRestart
            && ((Label)Field(recovery, "testState")).Text.Contains("failed"),
            "Invalid typed recovery threshold must not clamp into an active setting.");
        Check(!((System.Windows.Forms.Timer)Field(recovery, "saveToastTimer")).Enabled,
            "Recovery save errors must remain visible until corrected.");
        Call(recovery, "LoadFromSupervisor");
        Check(restart.Text == previousRestart.ToString() && !((System.Windows.Forms.Timer)Field(recovery, "autosaveTimer")).Enabled,
            "Reloading Recovery must replace invalid raw numeric text without scheduling a save.");
        object original = ((IList)Field(recovery, "accountCatalog")).Cast<object>()
            .Single(value => (string)ReadProperty(value, "CharacterName") == "Auto discovered mock");
        object supervisor = Field(recovery, "supervisor"), store = Field(supervisor, "store");
        string path = (string)Field(store, "path");
        object catalogStore = Field(recovery, "accountCatalogStore");
        string catalogPath = (string)ReadProperty(catalogStore, "FilePath");
        Type proxyType = app.GetType("_4RTools.Model.Vanilla.VanillaProxyRoute", true);
        using (Form dialog = NewCharacterDialog(recovery, original, false,
            value => Call(recovery, "PersistEditedCharacter", value, Enum.ToObject(proxyType, 0))))
        {
            var label = (TextBox)Field(dialog, "label");
            label.Text = "Durably edited mock"; Call(label, "OnValidated", EventArgs.Empty);
            Check(File.ReadAllText(catalogPath).Contains("Durably edited mock")
                && (string)ReadProperty(ReadProperty(dialog, "Account"), "Label") == label.Text,
                "Character callback did not persist the real catalog.");
            string catalogBefore = File.ReadAllText(catalogPath);
            object settingsBefore = Field(supervisor, "settings");
            string blocker = Path.Combine(output, "character-autosave-blocker");
            File.WriteAllText(blocker, "synthetic failure");
            try
            {
                SetField(store, "path", Path.Combine(blocker, "settings.json"));
                label.Text = "Rejected mock"; Call(label, "OnValidated", EventArgs.Empty);
                Check(File.ReadAllText(catalogPath) == catalogBefore && ReferenceEquals(settingsBefore, Field(supervisor, "settings"))
                    && (string)ReadProperty(ReadProperty(dialog, "Account"), "Label") == "Durably edited mock"
                    && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                    "Failed account persistence did not roll back catalog or preserve active settings.");
            }
            finally { SetField(store, "path", path); File.Delete(blocker); }
        }
    }

    private static Form NewCharacterDialog(object recovery, object row, bool isNew, Action<object> persist)
    {
        Type proxyType = app.GetType("_4RTools.Model.Vanilla.VanillaProxyRoute", true);
        var account = Expression.Parameter(row.GetType(), "account");
        var route = Expression.Parameter(proxyType, "route");
        Type callback = typeof(Action<,>).MakeGenericType(row.GetType(), proxyType);
        Delegate save = Expression.Lambda(callback, Expression.Invoke(Expression.Constant(persist),
            Expression.Convert(account, typeof(object))), account, route).Compile();
        return (Form)Activator.CreateInstance(app.GetType("_4RTools.Model.Vanilla.VanillaMinimalAccountDialog", true),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Field(recovery, "supervisor"), row, Enum.ToObject(proxyType, 0), save, isNew }, null);
    }

    private static void CheckCharacterPasswordPreservation(object recovery, Type rowType)
    {
        object row = Activator.CreateInstance(rowType);
        Property(row, "Enabled", false); Property(row, "UserName", "old-synthetic");
        Property(row, "CharacterName", "Synthetic password fixture");
        Property(row, "ProtectedPassword", "inaccessible-synthetic-protected-value");
        object durable = row; int writes = 0;
        using (Form dialog = NewCharacterDialog(recovery, row, false, value => { durable = value; writes++; }))
        {
            ((TextBox)Field(dialog, "label")).Text = "Password fixture edited";
            Call((Control)Field(dialog, "label"), "OnValidated", EventArgs.Empty);
            Check(writes == 1 && (string)ReadProperty(durable, "ProtectedPassword") == "inaccessible-synthetic-protected-value",
                "Unrelated edit erased an inaccessible protected password.");
            ((TextBox)Field(dialog, "user")).Text = "new-synthetic";
            Call((Control)Field(dialog, "user"), "OnValidated", EventArgs.Empty);
            Check(writes == 1 && ((Label)Field(dialog, "saveStatus")).Text.StartsWith("Not saved:"),
                "Changing username reused the old password.");
            ((TextBox)Field(dialog, "password")).Text = "synthetic-password";
            Call((Control)Field(dialog, "password"), "OnValidated", EventArgs.Empty);
            Check(writes == 2 && (string)Call(Field(recovery, "supervisor"), "GetPassword", durable) == "synthetic-password",
                "Explicit replacement password did not persist using protected storage.");
        }
    }

    private static void ResizeNativeViewport(Form main, int width, int height)
    {
        // Standard hosted desktops may be 1024x768. Size only this test application's
        // top-level viewport natively; all child layout is still the production code.
        RECT client, outer;
        if (!GetClientRect(main.Handle, out client) || !GetWindowRect(main.Handle, out outer))
            throw new InvalidOperationException("Could not measure the native test window.");
        int borderX = outer.Right - outer.Left - (client.Right - client.Left);
        int borderY = outer.Bottom - outer.Top - (client.Bottom - client.Top);
        if (!SetWindowPos(main.Handle, IntPtr.Zero, 0, 0, width + borderX, height + borderY, 0x0014))
            throw new InvalidOperationException("Could not size native test viewport: " + Marshal.GetLastWin32Error());
        Pump();
        GetClientRect(main.Handle, out client);
        if (client.Right - client.Left != width || client.Bottom - client.Top != height)
            throw new InvalidOperationException("Native viewport was not resized to " + width + "x" + height + ".");
    }

    private static void RunCase(Form main, object recovery, int width, int height, int rows, float textScale, bool notifications)
    {
        caseNumber++;
        string name = caseNumber.ToString("00") + "-" + width + "x" + height + "-" + rows + "accounts-text" + (int)(textScale * 100)
            + (notifications ? "-notifications" : "");
        try
        {
            ResizeNativeViewport(main, width, height);
            Control view = (Control)recovery;
            view.Font = new Font("Segoe UI", 9F * textScale);
            SeedAccounts(recovery, rows);
            ((Label)Field(recovery, "testState")).Text = notifications ? "Save failed: sample error; details in debug log" : string.Empty;
            ((Label)Field(recovery, "runState")).Text = notifications ? "STARTING" : "STOPPED";
            string version = app.GetName().Version.ToString(3);
            ((Label)Field(main, "integratedUpdateStatus")).Text = "Version " + version + (notifications
                ? " - update check unavailable. Sample long status." : " - up to date.");
            Pump();
            Call(main, "AssertSmokeBackgroundServicesInactive");
            DataGridView grid = (DataGridView)Field(recovery, "accounts");
            string[] expectedColumns =
            {
                "Enabled", "CartMaintenanceEnabled", "WeightEmailEnabled", "SmartTeleportEnabled", "SmartTeleportSeconds", "SmartTeleportHotkey",
                "Label", "User", "Slot", "CharacterName", "Hotkey", "Secret", "AccountProxy", "RuntimePid", "RuntimeStatus"
            };
            Check(grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Visible).OrderBy(c => c.DisplayIndex)
                .Select(c => c.Name).SequenceEqual(expectedColumns), name + ": character column order is wrong.");
            Check(grid.Columns["Enabled"].HeaderText == "Enabled"
                && grid.Columns["CartMaintenanceEnabled"].HeaderText == "Cart"
                && grid.Columns["WeightEmailEnabled"].HeaderText == "Mail"
                && grid.Columns["SmartTeleportEnabled"].HeaderText == "Smart TP",
                name + ": first character policy columns must be Enabled, Cart, Mail, Smart TP.");
            Check(grid.Columns["SmartTeleportSeconds"].HeaderText == "TP sec"
                && grid.Columns["SmartTeleportHotkey"].HeaderText == "TP hotkey",
                name + ": Smart Teleport seconds/hotkey columns are missing.");
            Check(grid.Columns["Label"].HeaderText == "Description" && grid.Columns["CharacterName"].HeaderText == "Character name", name + ": character headers missing.");
            Check(!Descendants(view).OfType<CheckBox>().Any(c => c.Text == "Visual watchdog"
                || c.Text == "Detect login screens/popups visually"), name + ": obsolete background visual monitoring switch remains.");
            Check(grid.Rows.Cast<DataGridViewRow>().All(r =>
                    !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells["CartMaintenanceEnabled"].Value))
                    && !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells["WeightEmailEnabled"].Value))),
                name + ": per-character Cart/Mail policies were not rendered.");
            Check(Convert.ToString(grid.Rows[0].Cells["CartMaintenanceEnabled"].Value) == "Yes"
                && Convert.ToString(grid.Rows[0].Cells["WeightEmailEnabled"].Value) == "No",
                name + ": first character split Cart/Mail policy did not render.");
            if (grid.Rows.Count > 1)
                Check(Convert.ToString(grid.Rows[1].Cells["CartMaintenanceEnabled"].Value) == "No"
                    && Convert.ToString(grid.Rows[1].Cells["WeightEmailEnabled"].Value) == "Yes",
                    name + ": second character split Cart/Mail policy did not render.");
            Check(Convert.ToString(grid.Rows[0].Cells["SmartTeleportEnabled"].Value) == "Yes"
                && Convert.ToString(grid.Rows[0].Cells["SmartTeleportSeconds"].Value) == "75"
                && Convert.ToString(grid.Rows[0].Cells["SmartTeleportHotkey"].Value) == "Ctrl+F5",
                name + ": enabled Smart Teleport details were not rendered in the character list.");
            if (grid.Rows.Count > 1)
                Check(Convert.ToString(grid.Rows[1].Cells["SmartTeleportEnabled"].Value) == "No"
                    && Convert.ToString(grid.Rows[1].Cells["SmartTeleportSeconds"].Value) == "60",
                    name + ": disabled Smart Teleport state/default seconds were not rendered.");
            Check(grid.Rows.Cast<DataGridViewRow>().All(r => !string.IsNullOrWhiteSpace(Convert.ToString(r.Cells["CharacterName"].Value))), name + ": saved character names missing from table.");
            Check(Field(recovery, "characterDiscoveryTimer") == null, name + ": smoke mode started character discovery.");
            Control launcher = (Control)Field(recovery, "launchPath");
            Control box = grid.Parent;
            while (box != null && !(box is GroupBox)) box = box.Parent;
            Control log = (Control)Field(recovery, "log");
            Control left = (Control)Field(recovery, "responsiveLeft");
            Control logBox = (Control)Field(recovery, "responsiveLogBox");
            report.AppendLine("CASE " + name + " actualClient=" + main.ClientSize + " outer=" + main.Size
                + " recovery=" + view.Bounds + " parentClient=" + view.Parent.ClientSize
                + " grid=" + BoundsIn(grid, main) + " launcher=" + BoundsIn(launcher, main) + " log=" + BoundsIn(log, main));
            Check(main.ClientSize == new Size(width, height), name + ": managed viewport differs from native size.");
            Check(main.Width >= width && main.Height >= height, name + ": screenshot is smaller than requested viewport.");
            Check(Math.Abs(view.Width - (view.Parent.ClientSize.Width - view.Parent.Padding.Horizontal)) <= 2,
                name + ": embedded recovery form is not filling the parent width.");
            Check(grid.Rows.Count == rows, name + ": not all mock account profiles are rendered.");
            Check(FullyVisible(grid, main), name + ": account grid is clipped by an ancestor viewport.");
            Check(FullyVisible(log, main), name + ": log is clipped by an ancestor viewport.");
            Check(grid.Columns.Contains("RuntimePid") && grid.Columns.Contains("RuntimeStatus"), name + ": runtime columns missing.");
            if (width >= 1600 && textScale <= 1.25F)
            {
                Check(BoundsIn(logBox, main).Left >= BoundsIn(left, main).Right,
                    name + ": Full-HD recovery/log panes are stacked instead of side-by-side.");
                double ratio = left.Width / (double)(left.Width + logBox.Width);
                Check(ratio >= 0.64 && ratio <= 0.69, name + ": accounts/log split is not approximately 2:1: " + ratio);
            }
            int totalWidth = CheckAccountViewport(grid, name, true);
            Check(grid.Height >= grid.ColumnHeadersHeight + grid.RowTemplate.Height * 5,
                name + ": fewer than four account rows plus one spare row can fit.");
            if (rows <= 4) Check(grid.DisplayedRowCount(false) == rows, name + ": an account row is not fully visible.");

            Control strip = (Control)Field(recovery, "responsiveHeader");
            int lastBottom = Descendants(strip).Where(c => c.Visible && (c is Button || c is CheckBox || c is TextBox || c is Label))
                .Select(c => BoundsIn(c, main).Bottom).DefaultIfEmpty(BoundsIn(launcher, main).Bottom).Max();
            int gap = BoundsIn(box, main).Top - lastBottom;
            report.AppendLine("  HEADER gap=" + gap + " totalColumnWidth=" + totalWidth + " displayedRows=" + grid.DisplayedRowCount(false));
            Check(gap >= 0 && gap <= 16, name + ": dead space below launcher/actions: " + gap + "px.");
            foreach (Control control in Descendants(strip).Where(c => c.Visible
                && (c is Button || c is CheckBox || c is TextBox || c is NumericUpDown)))
                Check(FullyVisible(control, main), name + ": action clipped: " + control.Text);
            foreach (string caption in new[] { "Add", "Edit", "Remove" })
            {
                Button button = Descendants(view).OfType<Button>().FirstOrDefault(b => b.Visible && b.Text == caption);
                Check(button != null && FullyVisible(button, main), name + ": account action clipped/missing: " + caption);
            }
            foreach (string field in new[] { "globalDebugEnabled", "globalCopyDebug", "integratedUpdateStatus" })
            {
                Control control = (Control)Field(main, field);
                Check(control != null && control.Visible && FullyVisible(control, main), name + ": global header control clipped/missing: " + field);
            }
            foreach (Button button in Descendants(main).OfType<Button>().Where(b => b.Visible && b.Text == "CHECK FOR UPDATES"))
                Check(FullyVisible(button, main), name + ": update button is clipped.");
            Control fleet = (Control)Field(main, "integratedFleetDashboard");
            foreach (Label label in Descendants(fleet).OfType<Label>().Where(c => c.Visible))
            {
                Check(label.Height >= label.Font.Height && FullyVisible(label, main),
                    name + ": live-card text is clipped: " + label.Text + " bounds=" + BoundsIn(label, main));
            }
            Check(Descendants(fleet).OfType<Label>().Count(c => c.Visible && c.Text.StartsWith("Activity:")) == 0,
                name + ": unverified Activity fields must not be shown.");
            Check(Descendants(fleet).OfType<Label>().Count(c => c.Visible && c.Text.StartsWith("Weight ")) == 2,
                name + ": both live-card carried-weight fields must remain visible.");
            Check(Descendants(fleet).OfType<Label>().Count(c => c.Visible && c.Text.StartsWith("Cart ")) == 2,
                name + ": both live-card Cart-weight fields must remain visible.");
            if (rows > grid.DisplayedRowCount(false))
            {
                grid.FirstDisplayedScrollingRowIndex = rows - 1;
                Pump();
                Check(grid.GetRowDisplayRectangle(rows - 1, true).Height >= grid.Rows[rows - 1].Height,
                    name + ": final account cannot be scrolled fully into view.");
                CheckAccountViewport(grid, name + " at final account", false);
                grid.FirstDisplayedScrollingRowIndex = 0;
            }
            grid.CurrentCell = grid.Rows[Math.Min(1, rows - 1)].Cells[1];
            object selected = grid.CurrentRow.Tag;
            TabControl tabs = (TabControl)Field(main, "primaryWorkspace");
            tabs.SelectedIndex = 1; Pump(); tabs.SelectedIndex = 0; Pump();
            Check(object.Equals(grid.CurrentRow.Tag, selected), name + ": account selection lost when returning to Vanilla.");
            CheckAccountViewport(grid, name + " after tab return", false);
            SaveScreenshot(main, Path.Combine(output, name + ".png"));
            Rectangle stable = BoundsIn(grid, main);
            Pump();
            Check(stable == BoundsIn(grid, main), name + ": layout moves after settling.");
            Call(main, "AssertSmokeBackgroundServicesInactive");
        }
        catch (Exception ex)
        {
            failures.Add(name + ": " + ex);
            try { SaveScreenshot(main, Path.Combine(output, name + "-error.png")); } catch { }
        }
    }

    private static int CheckAccountViewport(DataGridView grid, string context, bool writeReport)
    {
        // ClientSize includes DataGridView's managed scrollbar children. Comparing column bounds
        // only to ClientSize would pass even when the rightmost column sits under the scrollbar.
        VScrollBar vertical = grid.Controls.OfType<VScrollBar>().FirstOrDefault(b => b.Visible);
        HScrollBar horizontal = grid.Controls.OfType<HScrollBar>().FirstOrDefault(b => b.Visible);
        int dataRight = vertical == null ? grid.ClientSize.Width - 2 : vertical.Left;
        int totalWidth = 0;
        foreach (DataGridViewColumn column in grid.Columns)
        {
            if (!column.Visible) continue;
            Rectangle cell = grid.GetColumnDisplayRectangle(column.Index, false);
            totalWidth += column.Width;
            if (writeReport) report.AppendLine("  COLUMN " + column.Name + " width=" + column.Width + " rect=" + cell);
            Check(cell.Width == column.Width && cell.Left >= 0 && cell.Right <= dataRight,
                context + ": column " + column.Name + " is clipped or obscured by a scrollbar (right=" + cell.Right + ", dataRight=" + dataRight + ").");
        }
        Check(totalWidth <= grid.ClientSize.Width, context + ": column widths exceed grid viewport.");
        Check(horizontal == null, context + ": unnecessary horizontal scrollbar despite a fitting account-pane width.");
        if (writeReport) report.AppendLine("  SCROLLBARS vertical=" + (vertical != null) + ", horizontal=" + (horizontal != null) + ", dataRight=" + dataRight);
        return totalWidth;
    }

    private static void SeedAccounts(object recovery, int count)
    {
        Type type = app.GetType("_4RTools.Model.Vanilla.VanillaReconnectAccount", true);
        IList catalog = (IList)Field(recovery, "accountCatalog");
        catalog.Clear();
        for (int i = 0; i < count; i++)
        {
            object account = Activator.CreateInstance(type);
            Property(account, "Id", "layout-account-" + i);
            Property(account, "Enabled", i < 2);
            Property(account, "Label", i == 0 ? "Priest - long description to test fitting" : "Character " + (i + 1));
            Property(account, "CharacterName", "Mock character " + (i + 1));
            Property(account, "UserName", i == 1 ? "long_username_for_layout_test" : "mock-user-" + (i + 1));
            Property(account, "CharacterSlot", i % 15 + 1);
            Property(account, "ResumeCtrl", false); Property(account, "ResumeAlt", true);
            Property(account, "WeightEnabled", true);
            Property(account, "CartMaintenanceEnabled", (bool?)(i == 0));
            Property(account, "WeightEmailEnabled", (bool?)(i != 0));
            Property(account, "SmartTeleportEnabled", i == 0);
            Property(account, "SmartTeleportIdleSeconds", i == 0 ? 75 : 60);
            if (i == 0)
            {
                Property(account, "SmartTeleportKey", (int)Keys.F5);
                Property(account, "SmartTeleportCtrl", true);
            }
            catalog.Add(account);
        }
        Call(recovery, "SynchronizeSupervisorAccountsFromCatalog");
        object supervisor = Field(recovery, "supervisor");
        Call(supervisor, "Apply", Field(recovery, "settings"), false);
        IDictionary runtimes = (IDictionary)Field(supervisor, "runtimes");
        int n = 0;
        foreach (DictionaryEntry entry in runtimes)
        {
            SetField(entry.Value, "ProcessId", (int?)(12064 + n));
            FieldInfo stage = entry.Value.GetType().GetField("Stage", All);
            stage.SetValue(entry.Value, Enum.Parse(stage.FieldType, n++ == 0 ? "Online" : "Backoff"));
            SetField(entry.Value, "Detail", "Mock recovery detail: waiting for the other client. No real process is controlled.");
        }
        Call(recovery, "RefreshAccountGridFromCatalog");
        TextBox log = (TextBox)Field(recovery, "log");
        log.Text = string.Join(Environment.NewLine, Enumerable.Range(0, 60).Select(i =>
            "18:00:" + (i % 60).ToString("00") + " [MOCK] Account " + (i % 2 + 1) + ": observed gameplay; client remains minimized. Detailed diagnostic entry " + i));
        log.SelectionStart = 0; log.ScrollToCaret();
    }

    private static void SeedFleet(object main)
    {
        Array cards = (Array)Field(Field(main, "integratedFleetDashboard"), "cards");
        Type type = app.GetType("_4RTools.Model.Vanilla.VanillaFleetClientInfo", true);
        for (int i = 0; i < cards.Length; i++)
        {
            object info = Activator.CreateInstance(type);
            Property(info, "ProcessId", 12064 + i);
            Property(info, "CharacterName", i == 0 ? "Mock Novicer" : "Mock Nordina");
            Property(info, "NameVerified", true); Property(info, "HpVerified", true); Property(info, "SpVerified", true);
            Property(info, "WeightVerified", true); Property(info, "CartWeightVerified", true);
            Property(info, "CurrentHP", (uint?)3465); Property(info, "MaxHP", (uint?)4187);
            Property(info, "CurrentSP", (uint?)303); Property(info, "MaxSP", (uint?)367);
            Property(info, "CurrentWeight", (uint?)2630); Property(info, "MaxWeight", (uint?)5490);
            Property(info, "CurrentCartWeight", (uint?)(9600 + i * 100)); Property(info, "MaxCartWeight", (uint?)10000);
            Property(info, "Location", "yuno_fild08 (283, 233)");
            Call(cards.GetValue(i), "ShowClient", info);
        }
    }

    private static Rectangle BoundsIn(Control control, Control ancestor) { return ancestor.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)); }
    private static bool FullyVisible(Control control, Control root)
    {
        Rectangle bounds = control.RectangleToScreen(control.ClientRectangle);
        for (Control parent = control.Parent; parent != null; parent = parent.Parent)
        {
            if (!parent.RectangleToScreen(parent.ClientRectangle).Contains(bounds)) return false;
            if (object.ReferenceEquals(parent, root)) return true;
        }
        return false;
    }
    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }
    private static void SaveScreenshot(Form form, string path)
    {
        using (Bitmap bitmap = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            Point origin = form.PointToScreen(Point.Empty);
            foreach (Control control in form.Controls.Cast<Control>().Reverse())
            {
                if (!control.Visible || control is MdiClient) continue;
                Rectangle bounds = new Rectangle(origin.X - form.Left + control.Left, origin.Y - form.Top + control.Top, control.Width, control.Height);
                bounds.Intersect(new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                if (bounds.Width > 0 && bounds.Height > 0) control.DrawToBitmap(bitmap, bounds);
            }
            bitmap.Save(path, ImageFormat.Png);
        }
    }
    private static void Pump() { for (int i = 0; i < 15; i++) { Application.DoEvents(); Thread.Sleep(10); } }
    private static void Check(bool condition, string message) { if (!condition) failures.Add(message); }
    private static object Field(object target, string name) { return target.GetType().GetField(name, All).GetValue(target); }
    private static void SetField(object target, string name, object value) { target.GetType().GetField(name, All).SetValue(target, value); }
    private static void Property(object target, string name, object value) { target.GetType().GetProperty(name, All).SetValue(target, value, null); }
    private static object ReadProperty(object target, string name) { return target.GetType().GetProperty(name, All).GetValue(target, null); }
    private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static object CallStatic(string type, string name, params object[] args) { return app.GetType(type, true).GetMethod(name, All).Invoke(null, args); }
}
