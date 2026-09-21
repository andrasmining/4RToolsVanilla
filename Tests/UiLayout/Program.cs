using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
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
                CheckWeightCartHotkeys(main);
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
                CheckCharacterDiscovery(main, recovery);
                CheckCharacterEditor(main, recovery);
                CheckLegacyUsernameDiscovery(main, recovery);
                CheckUsernameDiagnostics();
                CheckPrivateUpdateAccess();
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

    private static void CheckPrivateUpdateAccess()
    {
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
            foreach (string caption in new[] { "SAVE", "CLEAR SAVED", "CANCEL" })
            {
                var button = Descendants(dialog).OfType<Button>().SingleOrDefault(b => b.Text == caption);
                Check(button != null && button.Visible && FullyVisible(button, dialog), "Private update action is clipped: " + caption);
            }
            Check(boxes.Length == 1 && FullyVisible(boxes[0], dialog), "Private update token entry is clipped.");
            if (boxes.Length == 1) boxes[0].Text = "synthetic-ui-token-only";
            Pump();
            SaveScreenshot(dialog, Path.Combine(output, scale == 1F ? "24-private-update-access.png" : "25-private-update-access-scaled.png"));
            report.AppendLine("CASE private update access: masked empty entry and Save/Clear/Cancel visible at scale " + scale + "; no credentials saved or network requested.");
            dialog.Close();
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
        Type proxyType = app.GetType("_4RTools.Model.Vanilla.VanillaProxyRoute", true);
        Type dialogType = app.GetType("_4RTools.Model.Vanilla.VanillaMinimalAccountDialog", true);
        using (Form dialog = (Form)Activator.CreateInstance(dialogType, BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { Field(recovery, "supervisor"), row, Enum.ToObject(proxyType, 0) }, null))
        {
            dialog.Show(main); Pump();
            foreach (string name in new[] { "enabled", "label", "user", "slot", "character", "password", "proxy", "hotkey",
                "smartTeleport", "teleportIdle", "teleportHotkey" })
                Check(FullyVisible((Control)Field(dialog, name), dialog), "Character editor clipped field: " + name);
            Check(((TextBox)Field(dialog, "slot")).Text == "", "Character editor invented slot 1.");
            Check(((ComboBox)Field(dialog, "proxy")).SelectedIndex == -1, "Character editor invented proxy.");
            Check(!((CheckBox)Field(dialog, "smartTeleport")).Checked, "Smart Teleport must default OFF for a newly discovered character.");
            Check(((NumericUpDown)Field(dialog, "teleportIdle")).Value == 60, "Smart Teleport default idle time must be 60 seconds.");
            Check(((TextBox)Field(dialog, "teleportHotkey")).Text.Contains("press hotkey"), "Smart Teleport live hotkey capture field is missing its unset state.");
            SaveScreenshot(dialog, Path.Combine(output, "20-character-editor.png"));
            dialog.Close();
        }
        report.AppendLine("CASE 20 character editor: identity/credential + per-character Smart Teleport controls visible; 60s default; unavailable slot/proxy preserved.");
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
    private static object Call(object target, string name, params object[] args) { return target.GetType().GetMethod(name, All).Invoke(target, args); }
    private static object CallStatic(string type, string name, params object[] args) { return app.GetType(type, true).GetMethod(name, All).Invoke(null, args); }
}
