using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;
using _4RTools.Model.Vanilla.Automation;

namespace _4RTools.Forms
{
    // The form observes a session; process memory and action execution remain in the session.
    public interface IAutomationSession : IDisposable
    {
        VanillaAutomationSettings Settings { get; }
        VanillaClientState Snapshot { get; }
        string Status { get; }
        string Fingerprint { get; }
        string BuildProfile { get; }
        string ExecutablePath { get; }
        string CurrentProfileName { get; }
        string ActivitySummary { get; }
        bool IsEnabled { get; }
        bool FarmingEnabled { get; }
        event System.Action<string> Logged;
        void Connect(int processId);
        void Disconnect();
        void Tick();
        void SetEnabled(bool enabled);
        void SetFarmingEnabled(bool enabled);
        void TestOnce();
        void ApplySettings(VanillaAutomationSettings settings);
        IReadOnlyList<string> GetProfileNames();
        void LoadProfile(string name);
        void SaveProfile(string name, VanillaAutomationSettings settings);
        void ImportProfile(string path, string name);
        void ExportProfile(string path);
    }

    public sealed class VanillaAutomationForm : Form
    {
        private const int EmergencyHotkeyId = 0x4F56;
        private const int WmHotkey = 0x0312;
        private readonly IAutomationSession session;
        private readonly System.Action openDiagnostics;
        private readonly bool hosted;
        private readonly bool ownsSession;
        private readonly Timer timer = new Timer { Interval = 250 };
        private readonly ToolTip tips = new ToolTip { AutoPopDelay = 15000 };
        private readonly ComboBox profiles = DropDown(210);
        private readonly ComboBox processes = DropDown(360);
        private readonly Button toggle = new Button { Text = "AUTOMATION OFF", Width = 175, Height = 38, FlatStyle = FlatStyle.Flat };
        private readonly CheckBox dryRun = new CheckBox { Text = "Dry run (log actions only)", AutoSize = true };
        private readonly CheckBox farming = new CheckBox { Text = "Farming active", AutoSize = true };
        private readonly ComboBox emergency = KeyDropDown(false);
        private readonly Label connection = StatusLabel();
        private readonly Label character = StatusLabel();
        private readonly Label build = StatusLabel();
        private readonly Label validation = StatusLabel();
        private readonly Label saveStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(850, 0) };
        private readonly Control activity = new BufferedReadout { Dock = DockStyle.Fill, Margin = new Padding(0, 16, 0, 0) };
        private readonly ListBox ruleSummary = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly CheckBox teleportEnabled = new CheckBox { Text = "Enable teleport", AutoSize = true };
        private readonly ComboBox teleportMode = DropDown(180);
        private readonly ComboBox teleportKey = KeyDropDown(true);
        private readonly NumericUpDown noTarget = Seconds();
        private readonly NumericUpDown noCombat = Seconds();
        private readonly NumericUpDown cooldown = Seconds();
        private readonly NumericUpDown grace = Seconds();
        private readonly NumericUpDown fixedInterval = Seconds();
        private readonly CheckBox stuckEnabled = new CheckBox { Text = "Recover from being stuck", AutoSize = true };
        private readonly NumericUpDown stuckTimeout = Seconds();
        private readonly CheckBox recoveryEnabled = new CheckBox { Text = "Enable SP recovery", AutoSize = true };
        private readonly NumericUpDown spThreshold = new VanillaSettingsNumber { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Width = 100 };
        private readonly NumericUpDown spCooldown = Seconds();
        private readonly CheckBox outOfCombat = new CheckBox { Text = "Only when out of combat", AutoSize = true };
        private readonly DataGridView sequence = new BufferedDataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        private readonly ComboBox newStepKind = DropDown(140);
        private readonly DataGridView state = new BufferedDataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        private readonly TextBox log = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill,
            BackColor = Color.White, Font = new Font(FontFamily.GenericMonospace, 9)
        };
        private bool loading;
        private bool dirty;
        private bool disposed;
        private DateTimeOffset lastEdit;
        private int registeredEmergencyKey;
        private VanillaClientState displayedSnapshot;

        public System.Action EmergencyStopRequested { get; set; }
        public Func<int, bool> EmergencyKeyAllowed { get; set; }

        public VanillaAutomationForm(IAutomationSession session, System.Action openDiagnostics = null, bool hosted = false, bool ownsSession = true)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.openDiagnostics = openDiagnostics;
            this.hosted = hosted;
            this.ownsSession = ownsSession;
            Text = hosted ? "4RTools — Vanilla extra features" : "4RTools Vanilla Companion";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1120, 840);
            MinimumSize = new Size(980, 740);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(245, 247, 250);
            AutoScaleMode = AutoScaleMode.Dpi;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            noTarget.Minimum = noCombat.Minimum = grace.Minimum = 0.1M;
            cooldown.Minimum = fixedInterval.Minimum = stuckTimeout.Minimum = spCooldown.Minimum = 1;
            grace.Maximum = 600;
            BuildLayout();
            WireEvents();
            session.Logged += AppendLog;
            LoadControls();
            RefreshProfiles();
            RefreshProcesses();
            UpdateStatus();
            Load += (s, e) => LoadControls();
            Shown += (s, e) =>
            {
                Guard(() => RegisterEmergency(session.Settings.EmergencyKey));
                timer.Start();
            };
        }

        private void BuildLayout()
        {
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
            for (int i = 0; i < 4; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { Text = "Vanilla Automation", Font = new Font(Font, FontStyle.Bold), AutoSize = true, Margin = new Padding(0, 0, 0, 12) }, 0, 0);

            var selection = new BufferedTableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, RowCount = 2 };
            var profileRow = Flow();
            profileRow.Controls.Add(Caption("Profile"));
            profileRow.Controls.Add(profiles);
            AddButton(profileRow, "Copy profile…", CopyProfile);
            AddButton(profileRow, "Import…", ImportProfile);
            AddButton(profileRow, "Export…", ExportProfile);
            if (!hosted) AddButton(profileRow, "Original 4RTools", () => { session.SetEnabled(false); Program.OpenStockTools(); });
            selection.Controls.Add(profileRow, 0, 0);
            var clientRow = Flow();
            clientRow.Controls.Add(Caption("Client"));
            clientRow.Controls.Add(processes);
            if (hosted && !ownsSession)
            {
                processes.Enabled = false;
                clientRow.Controls.Add(Caption("Client selected in the main 4RTools window."));
            }
            else
            {
                AddButton(clientRow, "Refresh", RefreshProcesses);
                AddButton(clientRow, "Connect", Connect);
                AddButton(clientRow, "Disconnect", () => { session.Disconnect(); UpdateStatus(); });
            }
            selection.Controls.Add(clientRow, 0, 1);
            layout.Controls.Add(selection, 0, 1);

            var statusBox = new GroupBox { Text = "Selected client", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var statusLayout = new BufferedTableLayoutPanel { ColumnCount = 2, RowCount = 4, AutoSize = true, Dock = DockStyle.Top };
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
            statusLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            AddStatusRow(statusLayout, 0, "Connection", connection);
            AddStatusRow(statusLayout, 1, "Character", character);
            AddStatusRow(statusLayout, 2, "Build", build);
            AddStatusRow(statusLayout, 3, "State / rules", validation);
            statusBox.Controls.Add(statusLayout);
            layout.Controls.Add(statusBox, 0, 2);

            var actions = Flow();
            actions.Padding = new Padding(0, 8, 0, 8);
            toggle.ForeColor = Color.White;
            toggle.BackColor = Color.FromArgb(154, 47, 47);
            actions.Controls.Add(toggle);
            actions.Controls.Add(dryRun);
            actions.Controls.Add(farming);
            actions.Controls.Add(Caption("Emergency stop"));
            actions.Controls.Add(emergency);
            tips.SetToolTip(farming, "Confirm you are farming with Vanilla's own Autobattle. This companion does not start combat or movement.");
            tips.SetToolTip(dryRun, "Rules and cooldowns run normally. All configured actions are written to the log without sending input.");
            tips.SetToolTip(emergency, "This global hotkey always switches Vanilla automation OFF, even when another window is active.");
            layout.Controls.Add(actions, 0, 3);

            var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
            tabs.TabPages.Add(BuildTeleportPage());
            tabs.TabPages.Add(BuildRecoveryPage());
            tabs.TabPages.Add(BuildRulesPage());
            tabs.TabPages.Add(BuildStatePage());
            tabs.TabPages.Add(BuildLogPage());
            layout.Controls.Add(tabs, 0, 4);
            saveStatus.Margin = new Padding(0, 10, 0, 0);
            layout.Controls.Add(saveStatus, 0, 5);
            Controls.Add(layout);
        }

        private TabPage BuildRulesPage()
        {
            var page = new TabPage("Timed & status rules") { Padding = new Padding(14) };
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var buttons = Flow();
            AddButton(buttons, "Edit rules…", () =>
            {
                session.SetEnabled(false);
                if (dirty) SaveCurrent(false);
                var settings = session.Settings;
                string editingProfile = session.CurrentProfileName;
                using (var editor = new VanillaRulesEditor(settings.Rules, settings.EmergencyKey, rules =>
                {
                    if (session.CurrentProfileName != editingProfile) throw new InvalidOperationException("The selected profile changed; reopen its rule editor.");
                    var current = session.Settings.Clone(); current.Rules = rules;
                    session.SaveProfile(editingProfile, current);
                })) editor.ShowDialog(this);
                LoadControls();
            });
            buttons.Controls.Add(Hint("Timed, HP/SP, status, target, combat and position conditions share the guarded action scheduler."));
            layout.Controls.Add(buttons, 0, 0);
            layout.Controls.Add(ruleSummary, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        internal void VerifyDisplayedSettings()
        {
            if (ReadControls().ToJson() != session.Settings.ToJson())
                throw new InvalidOperationException("Displayed controls do not match the selected profile.");
        }

        private TabPage BuildTeleportPage()
        {
            var page = new TabPage("Smart Teleport") { Padding = new Padding(14), BackColor = Color.White, AutoScroll = true };
            teleportMode.Items.Add(new ModeChoice(TeleportMode.SmartIdle, "Smart idle"));
            teleportMode.Items.Add(new ModeChoice(TeleportMode.FixedInterval, "Fixed interval"));
            if (hosted)
            {
                page.Controls.Add(new Label
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    MaximumSize = new Size(950, 0),
                    Padding = new Padding(8),
                    Text = "Smart Teleport is now process-free and configured per character in Recovery & relog. "
                        + "Edit the username + character row, enable Smart Teleport, press the exact teleport hotkey into the live hotkey box, "
                        + "and choose the idle X/Y timeout (60 seconds by default). It runs automatically while supervision is ON and does not use target/combat state."
                });
                return page;
            }
            var grid = new BufferedTableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, RowCount = 10 };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 215));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 195));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 9; row++) grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            // A stable readout row prevents live status text from resizing the surrounding table.
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 135));
            grid.Controls.Add(teleportEnabled, 0, 0);
            grid.SetColumnSpan(teleportEnabled, 3);
            AddSetting(grid, 1, "Mode", teleportMode, "Smart mode requires verified target and combat signals.");
            AddSetting(grid, 2, "Teleport / Fly Wing key", teleportKey, "Choose the hotkey already assigned in Vanilla.");
            AddSetting(grid, 3, "No target for (seconds)", noTarget, "Starts over when a target is acquired.");
            AddSetting(grid, 4, "No combat for (seconds)", noCombat, "Combat or casting prevents an idle teleport.");
            AddSetting(grid, 5, "Cooldown (seconds)", cooldown, "Minimum time between teleport actions.");
            AddSetting(grid, 6, "Grace period (seconds)", grace, "Wait after connecting, changing maps, or teleporting.");
            AddSetting(grid, 7, "Fixed interval (seconds)", fixedInterval, "Used only when Fixed interval is selected.");
            grid.Controls.Add(stuckEnabled, 0, 8);
            grid.Controls.Add(stuckTimeout, 1, 8);
            grid.Controls.Add(Hint("Seconds at the same position without combat; verified X/Y required."), 2, 8);
            grid.Controls.Add(activity, 0, 9);
            grid.SetColumnSpan(activity, 3);
            page.Controls.Add(grid);
            return page;
        }

        private TabPage BuildRecoveryPage()
        {
            var page = new TabPage("SP Recovery") { Padding = new Padding(14), BackColor = Color.White };
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var options = Flow();
            options.Controls.Add(recoveryEnabled);
            options.Controls.Add(Caption("SP below (%)"));
            options.Controls.Add(spThreshold);
            options.Controls.Add(Caption("Cooldown (seconds)"));
            options.Controls.Add(spCooldown);
            options.Controls.Add(outOfCombat);
            layout.Controls.Add(options, 0, 0);
            var hint = Hint("Steps run in order, one sequence at a time. Delay is the wait after each action, in milliseconds. Click coordinates are inside the selected game window. Test Once respects Dry run and requires automation ON.");
            hint.Margin = new Padding(0, 7, 0, 10);
            layout.Controls.Add(hint, 0, 1);
            sequence.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "Kind", HeaderText = "Action", DataSource = Enum.GetValues(typeof(SequenceStepKind)), FillWeight = 130
            });
            sequence.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "Key", HeaderText = "Key", DataSource = KeyChoices(true), DisplayMember = "Label", ValueMember = "Key", FillWeight = 110
            });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "Delay", HeaderText = "Delay (ms)", ValueType = typeof(int), FillWeight = 90 });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "X", HeaderText = "Click X", ValueType = typeof(int), FillWeight = 75 });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "Y", HeaderText = "Click Y", ValueType = typeof(int), FillWeight = 75 });
            sequence.RowTemplate.Height = 28;
            layout.Controls.Add(sequence, 0, 2);
            var commands = Flow();
            commands.Padding = new Padding(0, 7, 0, 0);
            newStepKind.DataSource = Enum.GetValues(typeof(SequenceStepKind));
            newStepKind.SelectedItem = SequenceStepKind.Wait;
            commands.Controls.Add(newStepKind);
            AddButton(commands, "Add step", AddStep);
            AddButton(commands, "Remove", RemoveStep);
            AddButton(commands, "Move up", () => MoveStep(-1));
            AddButton(commands, "Move down", () => MoveStep(1));
            AddButton(commands, "Test Once", () => { SaveCurrent(true); session.TestOnce(); UpdateStatus(); });
            layout.Controls.Add(commands, 0, 3);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildStatePage()
        {
            var page = new TabPage("Current State") { Padding = new Padding(14), BackColor = Color.White };
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var buttons = Flow();
            AddButton(buttons, "Open diagnostics", () =>
            {
                if (openDiagnostics != null) openDiagnostics();
                else new VanillaDiagnosticsForm().Show(this);
            });
            buttons.Controls.Add(Hint("Unavailable fields never count as zero or idle."));
            layout.Controls.Add(buttons, 0, 0);
            foreach (string name in new[] { "Field", "Value", "Availability", "Last observed (UTC)", "Last changed (UTC)", "Evidence / error" })
                state.Columns.Add(name, name);
            state.Columns[5].FillWeight = 220;
            foreach (VanillaField field in Enum.GetValues(typeof(VanillaField))) state.Rows.Add(field, "Unavailable", "Unavailable", "—", "—", "No observation.");
            layout.Controls.Add(state, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        private TabPage BuildLogPage()
        {
            var page = new TabPage("Activity Log") { Padding = new Padding(14), BackColor = Color.White };
            var layout = new BufferedTableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var buttons = Flow();
            AddButton(buttons, "Export log…", () =>
            {
                using (var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = "vanilla-activity.txt", OverwritePrompt = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, log.Text);
            });
            AddButton(buttons, "Clear view", () => log.Clear());
            layout.Controls.Add(buttons, 0, 0);
            layout.Controls.Add(log, 0, 1);
            page.Controls.Add(layout);
            return page;
        }

        private void WireEvents()
        {
            foreach (CheckBox box in new[] { dryRun, teleportEnabled, stuckEnabled, recoveryEnabled, outOfCombat })
                box.CheckedChanged += (s, e) => { MarkDirty(); SavePendingAutomatically(); };
            foreach (ComboBox box in new[] { teleportMode, teleportKey, emergency })
                box.SelectedIndexChanged += (s, e) => { MarkDirty(); SavePendingAutomatically(); };
            foreach (NumericUpDown number in new[] { noTarget, noCombat, cooldown, grace, fixedInterval, stuckTimeout, spThreshold, spCooldown })
            {
                number.ValueChanged += (s, e) => MarkDirty();
                number.TextChanged += (s, e) => MarkDirty();
                number.Leave += (s, e) => SavePendingAutomatically();
            }
            farming.CheckedChanged += (s, e) => { if (!loading) Guard(() => session.SetFarmingEnabled(farming.Checked)); };
            profiles.SelectedIndexChanged += (s, e) => { if (!loading) Guard(SwitchProfile); };
            processes.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                session.Disconnect();
                UpdateStatus();
            };
            toggle.Click += (s, e) => Guard(() =>
            {
                if (session.IsEnabled) session.SetEnabled(false);
                else { SaveCurrent(true); RegisterEmergency(session.Settings.EmergencyKey); session.SetEnabled(true); }
                UpdateStatus();
            });
            sequence.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (sequence.IsCurrentCellDirty && sequence.CurrentCell is DataGridViewComboBoxCell)
                    sequence.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            sequence.CellValueChanged += (s, e) => { if (e.RowIndex >= 0) MarkDirty(); };
            sequence.CellEndEdit += (s, e) => MarkDirty();
            sequence.DataError += (s, e) =>
            {
                e.ThrowException = false;
                saveStatus.Text = "Please enter a valid sequence value. Settings have not been saved.";
                saveStatus.ForeColor = Color.Firebrick;
                if (!loading) session.SetEnabled(false);
            };
            sequence.CellFormatting += (s, e) =>
            {
                if (e.RowIndex < 0 || e.ColumnIndex < 1) return;
                object kindValue = sequence.Rows[e.RowIndex].Cells[0].Value;
                if (!(kindValue is SequenceStepKind)) return;
                var kind = (SequenceStepKind)kindValue;
                bool used = e.ColumnIndex == 1 ? kind == SequenceStepKind.PressKey || kind == SequenceStepKind.KeyDown || kind == SequenceStepKind.KeyUp
                    : e.ColumnIndex == 2 || kind == SequenceStepKind.Click;
                e.CellStyle.ForeColor = used ? Color.Black : Color.Silver;
                e.CellStyle.BackColor = used ? Color.White : Color.FromArgb(247, 247, 247);
            };
            timer.Tick += (s, e) => TickSession();
        }

        private void TickSession()
        {
            try
            {
                // A hosted window observes the Container's shared session; it must not poll it twice.
                if (ownsSession) session.Tick();
                if (dirty && !sequence.IsCurrentCellInEditMode && DateTimeOffset.UtcNow - lastEdit > TimeSpan.FromSeconds(1))
                {
                    try { SaveCurrent(false); }
                    catch (Exception ex) { ShowValidationError(ex.Message); }
                }
                UpdateStatus();
            }
            catch (Exception ex)
            {
                session.SetEnabled(false);
                timer.Stop();
                AppendLog("Session stopped: " + ex.Message);
                ShowValidationError(ex.Message);
                UpdateStatus();
            }
        }

        private void MarkDirty()
        {
            if (loading) return;
            if (!dirty) session.SetEnabled(false);
            dirty = true;
            lastEdit = DateTimeOffset.UtcNow;
            saveStatus.Text = "Saving settings… Automation remains OFF after changes.";
            saveStatus.ForeColor = Color.DimGray;
            UpdateModeControls();
        }

        private void SavePendingAutomatically()
        {
            if (loading || disposed || !dirty) return;
            try { SaveCurrent(false); }
            catch (Exception ex) { ShowValidationError(ex.Message); }
        }

        private VanillaAutomationSettings ReadControls()
        {
            if (!sequence.EndEdit()) throw new ArgumentException("Finish editing the sequence before continuing.");
            var settings = session.Settings.Clone();
            settings.DryRun = dryRun.Checked;
            settings.EmergencyKey = SelectedKey(emergency);
            settings.Teleport.Enabled = !hosted && teleportEnabled.Checked;
            settings.Teleport.Mode = ((ModeChoice)teleportMode.SelectedItem).Mode;
            settings.Teleport.Key = SelectedKey(teleportKey);
            settings.Teleport.NoTargetTimeoutMs = Milliseconds(noTarget);
            settings.Teleport.NoCombatTimeoutMs = Milliseconds(noCombat);
            settings.Teleport.CooldownMs = Milliseconds(cooldown);
            settings.Teleport.GraceMs = Milliseconds(grace);
            settings.Teleport.FixedIntervalMs = Milliseconds(fixedInterval);
            settings.Teleport.StuckEnabled = stuckEnabled.Checked;
            settings.Teleport.StuckTimeoutMs = Milliseconds(stuckTimeout);
            settings.SpRecovery.Enabled = recoveryEnabled.Checked;
            settings.SpRecovery.ThresholdPercent = VanillaSettingsNumber.Read(spThreshold);
            settings.SpRecovery.CooldownMs = Milliseconds(spCooldown);
            settings.SpRecovery.PreferOutOfCombat = outOfCombat.Checked;
            settings.SpRecovery.Sequence.Clear();
            foreach (DataGridViewRow row in sequence.Rows)
            {
                if (!(row.Cells[0].Value is SequenceStepKind)) throw new ArgumentException("Choose an action for every sequence step.");
                var kind = (SequenceStepKind)row.Cells[0].Value;
                settings.SpRecovery.Sequence.Add(new SequenceStep
                {
                    Kind = kind,
                    Key = kind == SequenceStepKind.PressKey || kind == SequenceStepKind.KeyDown || kind == SequenceStepKind.KeyUp ? ReadCell(row, 1) : 0,
                    DelayMs = ReadCell(row, 2),
                    X = kind == SequenceStepKind.Click ? ReadCell(row, 3) : 0,
                    Y = kind == SequenceStepKind.Click ? ReadCell(row, 4) : 0
                });
            }
            settings.Validate();
            return settings;
        }

        private void LoadControls()
        {
            loading = true;
            try
            {
                var settings = session.Settings;
                dryRun.Checked = settings.DryRun;
                ruleSummary.Items.Clear();
                foreach (var rule in settings.Rules)
                    ruleSummary.Items.Add((rule.Enabled ? "ON: " : "OFF: ") + rule.Name + " — " + rule.Condition);
                farming.Checked = session.FarmingEnabled;
                SelectKey(emergency, settings.EmergencyKey);
                teleportEnabled.Checked = !hosted && settings.Teleport.Enabled;
                teleportMode.SelectedItem = teleportMode.Items.Cast<ModeChoice>().First(item => item.Mode == settings.Teleport.Mode);
                SelectKey(teleportKey, settings.Teleport.Key);
                SetSeconds(noTarget, settings.Teleport.NoTargetTimeoutMs);
                SetSeconds(noCombat, settings.Teleport.NoCombatTimeoutMs);
                SetSeconds(cooldown, settings.Teleport.CooldownMs);
                SetSeconds(grace, settings.Teleport.GraceMs);
                SetSeconds(fixedInterval, settings.Teleport.FixedIntervalMs);
                stuckEnabled.Checked = settings.Teleport.StuckEnabled;
                SetSeconds(stuckTimeout, settings.Teleport.StuckTimeoutMs);
                recoveryEnabled.Checked = settings.SpRecovery.Enabled;
                VanillaSettingsNumber.Load(spThreshold, settings.SpRecovery.ThresholdPercent);
                SetSeconds(spCooldown, settings.SpRecovery.CooldownMs);
                outOfCombat.Checked = settings.SpRecovery.PreferOutOfCombat;
                sequence.Rows.Clear();
                foreach (var step in settings.SpRecovery.Sequence)
                    sequence.Rows.Add(step.Kind, step.Key, step.DelayMs, step.X, step.Y);
                dirty = false;
                saveStatus.Text = "Profile: " + session.CurrentProfileName + ". Settings save automatically; automation starts OFF.";
                saveStatus.ForeColor = Color.DimGray;
                UpdateModeControls();
            }
            finally { loading = false; }
        }

        private void SaveCurrent(bool explicitSave)
        {
            if (!dirty && !explicitSave) return;
            var settings = ReadControls();
            RegisterEmergency(settings.EmergencyKey);
            session.SaveProfile(session.CurrentProfileName, settings);
            dirty = false;
            saveStatus.Text = "Saved profile: " + session.CurrentProfileName + ".";
            saveStatus.ForeColor = Color.DimGray;
        }

        private void CopyProfile()
        {
            SaveCurrent(false);
            var settings = ReadControls();
            string name = AskProfileName("Copy profile", session.CurrentProfileName + " Copy", "Create copy");
            if (name == null) return;
            if (!ConfirmOverwrite(name)) return;
            session.SetEnabled(false);
            RegisterEmergency(settings.EmergencyKey);
            session.SaveProfile(name, settings);
            LoadControls();
            RefreshProfiles();
        }

        private void SwitchProfile()
        {
            string selected = profiles.SelectedItem as string;
            if (selected == null || selected == session.CurrentProfileName) return;
            session.SetEnabled(false);
            try
            {
                if (dirty) SaveCurrent(false);
                session.LoadProfile(selected);
                LoadControls();
                RegisterEmergency(session.Settings.EmergencyKey);
            }
            finally { RefreshProfiles(); UpdateStatus(); }
        }

        private void ImportProfile()
        {
            using (var dialog = new OpenFileDialog { Filter = "Vanilla profile (*.json)|*.json", CheckFileExists = true, Multiselect = false })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                string name = AskProfileName("Import profile", Path.GetFileNameWithoutExtension(dialog.FileName), "Import");
                if (name == null || !ConfirmOverwrite(name)) return;
                if (dirty) SaveCurrent(false);
                session.SetEnabled(false);
                session.ImportProfile(dialog.FileName, name);
                LoadControls();
                RefreshProfiles();
                RegisterEmergency(session.Settings.EmergencyKey);
            }
        }

        private void ExportProfile()
        {
            SaveCurrent(true);
            using (var dialog = new SaveFileDialog { Filter = "Vanilla profile (*.json)|*.json", FileName = session.CurrentProfileName + ".json", OverwritePrompt = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) session.ExportProfile(dialog.FileName);
        }

        private bool ConfirmOverwrite(string name)
        {
            return !session.GetProfileNames().Contains(name, StringComparer.OrdinalIgnoreCase)
                || MessageBox.Show(this, "Replace the saved profile '" + name + "'?", "Replace profile", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        private string AskProfileName(string title, string suggested, string action)
        {
            using (var dialog = new Form { Text = title, ClientSize = new Size(390, 125), FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent, MinimizeBox = false, MaximizeBox = false, Font = Font })
            {
                var input = new TextBox { Left = 15, Top = 32, Width = 355, Text = suggested, MaxLength = 64 };
                var ok = new Button { Text = action, Left = 185, Top = 78, Width = 100, DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "Cancel", Left = 290, Top = 78, Width = 80, DialogResult = DialogResult.Cancel };
                dialog.Controls.AddRange(new Control[] { new Label { Left = 15, Top = 10, Text = "Profile name", AutoSize = true }, input, ok, cancel });
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;
                dialog.Shown += (s, e) => { input.SelectAll(); input.Focus(); };
                if (dialog.ShowDialog(this) != DialogResult.OK) return null;
                string name = input.Text.Trim();
                if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name == "." || name == ".." || name.EndsWith(".", StringComparison.Ordinal))
                    throw new ArgumentException("Use a profile name without file path characters or a trailing period.");
                return name;
            }
        }

        private void RefreshProfiles()
        {
            loading = true;
            try
            {
                profiles.Items.Clear();
                foreach (string name in session.GetProfileNames()) profiles.Items.Add(name);
                profiles.SelectedItem = session.CurrentProfileName;
            }
            finally { loading = false; }
        }

        private void RefreshProcesses()
        {
            int? previous = (processes.SelectedItem as ProcessChoice)?.Id ?? session.Snapshot?.ProcessId;
            loading = true;
            try
            {
                processes.Items.Clear();
                foreach (Process process in Process.GetProcessesByName("Vanilla MMO").OrderBy(item => item.Id))
                {
                    using (process)
                    {
                        try { processes.Items.Add(new ProcessChoice(process.Id, process.MainWindowTitle)); }
                        catch (InvalidOperationException) { /* A process can exit during enumeration. */ }
                        catch (Win32Exception ex) { AppendLog("Process listing: " + ex.Message); }
                    }
                }
                processes.SelectedItem = processes.Items.Cast<ProcessChoice>().FirstOrDefault(item => item.Id == previous);
                if (processes.SelectedIndex < 0 && processes.Items.Count > 0) processes.SelectedIndex = 0;
            }
            finally { loading = false; }
        }

        private void Connect()
        {
            var selected = processes.SelectedItem as ProcessChoice;
            if (selected == null) throw new InvalidOperationException("Start Vanilla, then refresh the client list and select its window.");
            if (dirty) SaveCurrent(false);
            session.Connect(selected.Id);
            timer.Start();
            UpdateStatus();
        }

        /// <summary>Synchronizes the selected Vanilla client from the original 4RTools window.</summary>
        public void SelectClient(int processId)
        {
            if (processId <= 0) throw new ArgumentOutOfRangeException(nameof(processId));
            session.SetEnabled(false);
            if (dirty) SaveCurrent(false);
            RefreshProcesses();
            loading = true;
            try
            {
                var selected = processes.Items.Cast<ProcessChoice>().FirstOrDefault(item => item.Id == processId);
                if (selected == null)
                {
                    // The host owns selection. Connect below still validates the actual executable.
                    selected = new ProcessChoice(processId, "Selected in 4RTools");
                    processes.Items.Add(selected);
                }
                processes.SelectedItem = selected;
            }
            finally { loading = false; }
            if (session.Snapshot?.ProcessId != processId) session.Connect(processId);
            UpdateStatus();
        }

        private void AddStep()
        {
            if (sequence.Rows.Count >= 64) throw new ArgumentException("A sequence supports at most 64 steps.");
            var kind = (SequenceStepKind)newStepKind.SelectedItem;
            int index = sequence.Rows.Add(kind, 0, kind == SequenceStepKind.Wait ? 500 : 0, 0, 0);
            sequence.ClearSelection();
            sequence.Rows[index].Selected = true;
            sequence.CurrentCell = sequence.Rows[index].Cells[kind == SequenceStepKind.Wait ? 2 : kind == SequenceStepKind.Click ? 3 : 1];
            MarkDirty();
        }

        private void RemoveStep()
        {
            if (sequence.CurrentRow == null) return;
            sequence.Rows.RemoveAt(sequence.CurrentRow.Index);
            MarkDirty();
        }

        private void MoveStep(int direction)
        {
            if (sequence.CurrentRow == null || !sequence.EndEdit()) return;
            int from = sequence.CurrentRow.Index;
            int to = from + direction;
            if (to < 0 || to >= sequence.Rows.Count) return;
            object[] content = sequence.Rows[from].Cells.Cast<DataGridViewCell>().Select(cell => cell.Value).ToArray();
            sequence.Rows.RemoveAt(from);
            sequence.Rows.Insert(to, content);
            sequence.ClearSelection();
            sequence.CurrentCell = sequence.Rows[to].Cells[0];
            sequence.Rows[to].Selected = true;
            MarkDirty();
        }

        private void UpdateModeControls()
        {
            if (hosted)
            {
                teleportEnabled.Enabled = teleportMode.Enabled = teleportKey.Enabled = false;
                noTarget.Enabled = noCombat.Enabled = cooldown.Enabled = grace.Enabled = fixedInterval.Enabled = false;
                stuckEnabled.Enabled = stuckTimeout.Enabled = false;
                return;
            }
            bool smart = (teleportMode.SelectedItem as ModeChoice)?.Mode == TeleportMode.SmartIdle;
            noTarget.Enabled = noCombat.Enabled = smart;
            stuckEnabled.Enabled = smart;
            stuckTimeout.Enabled = smart && stuckEnabled.Checked;
            fixedInterval.Enabled = !smart;
        }

        private void UpdateStatus()
        {
            var snapshot = session.Snapshot;
            string sessionStatus = session.Status ?? "Waiting for a client.";
            SetTextIfChanged(connection, snapshot?.ProcessId.HasValue == true
                ? "Vanilla MMO.exe — PID " + snapshot.ProcessId + " | " + (snapshot.ConnectionStatus ?? session.Status)
                : "Disconnected — select a client and connect.");
            SetToolTipIfChanged(connection, session.ExecutablePath ?? "");
            SetTextIfChanged(character, snapshot?.CharacterName.IsAvailable == true ? snapshot.CharacterName.Value : "Unavailable");
            string fingerprint = session.Fingerprint;
            SetTextIfChanged(build, (string.IsNullOrWhiteSpace(session.BuildProfile) ? "No recognized build profile" : session.BuildProfile)
                + (string.IsNullOrWhiteSpace(fingerprint) ? "" : " | " + (fingerprint.Length > 24 ? fingerprint.Substring(0, 24) + "…" : fingerprint)));
            SetToolTipIfChanged(build, fingerprint ?? "");
            SetTextIfChanged(validation, sessionStatus);
            bool isEnabled = session.IsEnabled;
            bool isDryRun = isEnabled && session.Settings.DryRun;
            SetTextIfChanged(toggle, isEnabled ? (isDryRun ? "DRY RUN ON" : "AUTOMATION ON") : "AUTOMATION OFF");
            Color toggleColor = isEnabled ? (isDryRun ? Color.FromArgb(39, 87, 145) : Color.FromArgb(38, 116, 74)) : Color.FromArgb(154, 47, 47);
            if (toggle.BackColor != toggleColor) toggle.BackColor = toggleColor;
            if (farming.Checked != session.FarmingEnabled)
            {
                loading = true;
                farming.Checked = session.FarmingEnabled;
                loading = false;
            }
            string target = snapshot?.CurrentTargetId.IsAvailable == true ? snapshot.CurrentTargetId.Value.ToString(CultureInfo.InvariantCulture) + " (" + snapshot.CurrentTargetId.Validation + ")" : "Unavailable";
            string position = snapshot?.X.IsAvailable == true && snapshot.Y.IsAvailable ? snapshot.X.Value + ", " + snapshot.Y.Value : "Unavailable";
            string activityText = "Target: " + target + "     Position: " + position + Environment.NewLine + sessionStatus;
            if (snapshot != null) activityText += Environment.NewLine + "Last movement: " + FormatTime(snapshot.LastMovementAtUtc)
                + " | target activity: " + FormatTime(snapshot.LastTargetActivityAtUtc) + " | combat: " + FormatTime(snapshot.LastCombatActivityAtUtc);
            activityText += Environment.NewLine + session.ActivitySummary;
            // Assign the complete text once. Intermediate AutoSize labels caused the visible blink.
            SetTextIfChanged(activity, activityText);
            if (!state.Visible || ReferenceEquals(displayedSnapshot, snapshot)) return;
            displayedSnapshot = snapshot;
            foreach (DataGridViewRow row in state.Rows)
            {
                StateValue value = null;
                snapshot?.Fields.TryGetValue((VanillaField)row.Cells[0].Value, out value);
                SetCellIfChanged(row.Cells[1], value?.ToString() ?? "Unavailable");
                SetCellIfChanged(row.Cells[2], value?.Validation.ToString() ?? "Unavailable");
                SetCellIfChanged(row.Cells[3], FormatTime(value?.LastObservedAtUtc));
                SetCellIfChanged(row.Cells[4], FormatTime(value?.LastChangedAtUtc));
                SetCellIfChanged(row.Cells[5], value?.Error ?? value?.Evidence ?? "No observation.");
            }
        }

        private void RegisterEmergency(int key)
        {
            if (EmergencyKeyAllowed != null && !EmergencyKeyAllowed(key))
                throw new InvalidOperationException("The emergency stop key conflicts with the original 4RTools ON/OFF key. Choose a different emergency key.");
            if (registeredEmergencyKey == key && key != 0) return;
            if (!IsHandleCreated) return;
            int old = registeredEmergencyKey;
            if (old != 0 && !Native.UnregisterHotKey(Handle, EmergencyHotkeyId))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not update the emergency stop hotkey.");
            registeredEmergencyKey = 0;
            if (!Native.RegisterHotKey(Handle, EmergencyHotkeyId, 0x4000, (uint)key))
            {
                int error = Marshal.GetLastWin32Error();
                if (old != 0 && Native.RegisterHotKey(Handle, EmergencyHotkeyId, 0x4000, (uint)old)) registeredEmergencyKey = old;
                session.SetEnabled(false);
                throw new Win32Exception(error, "Emergency stop key is unavailable. Choose another key before turning automation ON.");
            }
            registeredEmergencyKey = key;
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == WmHotkey && message.WParam.ToInt32() == EmergencyHotkeyId)
            {
                try { EmergencyStopRequested?.Invoke(); }
                finally { session.SetEnabled(false); }
                AppendLog("EMERGENCY STOP — Vanilla automation OFF.");
                UpdateStatus();
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            session.SetEnabled(false);
            if (dirty)
            {
                try { SaveCurrent(false); }
                catch (Exception ex)
                {
                    if (e.CloseReason == CloseReason.UserClosing)
                    {
                        e.Cancel = MessageBox.Show(this, ex.Message + Environment.NewLine + "Close and keep the previously saved profile?", "Unsaved settings", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes;
                    }
                    AppendLog("Settings were not saved: " + ex.Message);
                }
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                if (dirty)
                {
                    try { SaveCurrent(false); }
                    catch (Exception ex) { AppendLog("Settings were not saved: " + ex.Message); }
                }
                disposed = true;
                timer.Stop();
                timer.Dispose();
                if (registeredEmergencyKey != 0 && IsHandleCreated) Native.UnregisterHotKey(Handle, EmergencyHotkeyId);
                registeredEmergencyKey = 0;
                session.Logged -= AppendLog;
                if (ownsSession) session.Dispose();
                else session.SetEnabled(false);
                tips.Dispose();
            }
            base.Dispose(disposing);
        }

        private void AppendLog(string message)
        {
            if (disposed || IsDisposed) return;
            if (InvokeRequired) { BeginInvoke(new System.Action<string>(AppendLog), message); return; }
            if (log.TextLength > 160000) log.Text = log.Text.Substring(log.TextLength - 100000);
            log.AppendText(DateTimeOffset.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + message + Environment.NewLine);
        }

        private void Guard(System.Action action)
        {
            try { action(); }
            catch (Exception ex)
            {
                session.SetEnabled(false);
                AppendLog(ex.Message);
                ShowValidationError(ex.Message);
                MessageBox.Show(this, ex.Message, "Vanilla Companion", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ShowValidationError(string message)
        {
            saveStatus.Text = message;
            saveStatus.ForeColor = Color.Firebrick;
        }

        private void AddButton(Control parent, string text, System.Action action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(75, 28), Padding = new Padding(5, 0, 5, 0) };
            button.Click += (s, e) => Guard(action);
            parent.Controls.Add(button);
        }

        private static ComboBox DropDown(int width) => new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = width };
        private static FlowLayoutPanel Flow() => new BufferedFlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 2, 0, 3) };
        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(0, 6, 4, 0) };
        private static Label StatusLabel() => new BufferedLabel { AutoSize = true, MaximumSize = new Size(890, 0), Margin = new Padding(0, 3, 0, 3) };
        private static Label Hint(string text) => new Label { Text = text, AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(950, 0), Padding = new Padding(0, 5, 0, 0) };
        private static NumericUpDown Seconds() => new VanillaSettingsNumber { Minimum = 0, Maximum = 3600, DecimalPlaces = 1, Increment = 1, Width = 100 };
        private static int Milliseconds(NumericUpDown number) => checked((int)(VanillaSettingsNumber.Read(number) * 1000));
        private static void SetSeconds(NumericUpDown number, int milliseconds) { VanillaSettingsNumber.Load(number, milliseconds / 1000M); }
        private static string FormatTime(DateTimeOffset? time) => time?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";
        private static void SetTextIfChanged(Control control, string value) { if (!string.Equals(control.Text, value, StringComparison.Ordinal)) control.Text = value; }
        private void SetToolTipIfChanged(Control control, string value) { if (!string.Equals(tips.GetToolTip(control), value, StringComparison.Ordinal)) tips.SetToolTip(control, value); }
        private static void SetCellIfChanged(DataGridViewCell cell, object value) { if (!Equals(cell.Value, value)) cell.Value = value; }
        private static void AddStatusRow(TableLayoutPanel grid, int row, string text, Control value) { grid.Controls.Add(Caption(text), 0, row); grid.Controls.Add(value, 1, row); }
        private static void AddSetting(TableLayoutPanel grid, int row, string text, Control value, string hint)
        {
            grid.Controls.Add(Caption(text), 0, row);
            grid.Controls.Add(value, 1, row);
            grid.Controls.Add(Hint(hint), 2, row);
        }

        private static int ReadCell(DataGridViewRow row, int column)
        {
            int value;
            if (!int.TryParse(Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("Step " + (row.Index + 1) + ": enter a whole number for " + row.Cells[column].OwningColumn.HeaderText + ".");
            return value;
        }

        private static ComboBox KeyDropDown(bool includeNone)
        {
            var control = DropDown(140);
            control.DisplayMember = "Label";
            control.ValueMember = "Key";
            control.DataSource = KeyChoices(includeNone);
            return control;
        }

        private static List<KeyChoice> KeyChoices(bool includeNone)
        {
            var keys = new List<KeyChoice>();
            if (includeNone) keys.Add(new KeyChoice(0, "Choose a key"));
            for (int key = (int)Keys.F1; key <= (int)Keys.F12; key++) keys.Add(new KeyChoice(key, ((Keys)key).ToString()));
            foreach (Keys key in new[] { Keys.Pause, Keys.End, Keys.Home, Keys.Insert, Keys.Delete, Keys.PageUp, Keys.PageDown, Keys.Space, Keys.Tab, Keys.Escape, Keys.Enter, Keys.Left, Keys.Right, Keys.Up, Keys.Down })
                keys.Add(new KeyChoice((int)key, key.ToString()));
            for (int key = (int)Keys.D0; key <= (int)Keys.D9; key++) keys.Add(new KeyChoice(key, ((char)key).ToString()));
            for (int key = (int)Keys.A; key <= (int)Keys.Z; key++) keys.Add(new KeyChoice(key, ((Keys)key).ToString()));
            for (int key = (int)Keys.NumPad0; key <= (int)Keys.NumPad9; key++) keys.Add(new KeyChoice(key, ((Keys)key).ToString()));
            foreach (Keys key in new[] { Keys.LShiftKey, Keys.RShiftKey, Keys.LControlKey, Keys.RControlKey, Keys.LMenu, Keys.RMenu, Keys.OemMinus, Keys.Oemplus, Keys.OemOpenBrackets, Keys.OemCloseBrackets, Keys.OemSemicolon, Keys.OemQuotes, Keys.Oemcomma, Keys.OemPeriod, Keys.OemQuestion, Keys.OemPipe, Keys.Oemtilde })
                keys.Add(new KeyChoice((int)key, key.ToString()));
            // Keep profiles created on another keyboard layout editable without losing keys.
            for (int key = 8; key <= 254; key++)
                if (!keys.Any(item => item.Key == key)) keys.Add(new KeyChoice(key, ((Keys)key).ToString()));
            return keys;
        }

        private static int SelectedKey(ComboBox control) => (control.SelectedItem as KeyChoice)?.Key ?? 0;
        private static void SelectKey(ComboBox control, int key)
        {
            var choices = (List<KeyChoice>)control.DataSource;
            if (!choices.Any(item => item.Key == key))
            {
                choices = new List<KeyChoice>(choices) { new KeyChoice(key, ((Keys)key).ToString()) };
                control.DataSource = choices;
            }
            control.SelectedItem = choices.First(item => item.Key == key);
        }

        private sealed class KeyChoice
        {
            public int Key { get; }
            public string Label { get; }
            public KeyChoice(int key, string label) { Key = key; Label = label; }
        }

        private sealed class ModeChoice
        {
            public TeleportMode Mode { get; }
            private readonly string label;
            public ModeChoice(TeleportMode mode, string label) { Mode = mode; this.label = label; }
            public override string ToString() => label;
        }

        private sealed class ProcessChoice
        {
            public int Id { get; }
            private readonly string title;
            public ProcessChoice(int id, string title) { Id = id; this.title = title; }
            public override string ToString() => "Vanilla MMO.exe — " + Id + (string.IsNullOrWhiteSpace(title) ? "" : " — " + title);
        }

        private sealed class BufferedLabel : Label
        {
            public BufferedLabel() { DoubleBuffered = true; }
        }

        // Label.Text performs parent layout even with AutoSize disabled. This fixed readout
        // only invalidates its own buffered surface when a complete observation changes.
        private sealed class BufferedReadout : Control
        {
            private string content = string.Empty;
            public BufferedReadout()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                AccessibleRole = AccessibleRole.StaticText;
                TabStop = false;
            }
            public override string Text
            {
                get { return content; }
                set
                {
                    if (string.Equals(content, value, StringComparison.Ordinal)) return;
                    content = value ?? string.Empty;
                    OnTextChanged(EventArgs.Empty);
                    Invalidate();
                }
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                TextRenderer.DrawText(e.Graphics, content, Font, ClientRectangle, ForeColor,
                    TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            }
        }

        private sealed class BufferedTableLayoutPanel : TableLayoutPanel
        {
            public BufferedTableLayoutPanel() { DoubleBuffered = true; }
        }

        private sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
        {
            public BufferedFlowLayoutPanel() { DoubleBuffered = true; }
        }

        private sealed class BufferedDataGridView : DataGridView
        {
            public BufferedDataGridView() { DoubleBuffered = true; }
        }

        private static class Native
        {
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool UnregisterHotKey(IntPtr window, int id);
        }
    }
}
