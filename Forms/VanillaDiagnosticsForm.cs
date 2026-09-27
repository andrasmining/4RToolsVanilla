using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Newtonsoft.Json;
using _4RTools.Model;
using _4RTools.Model.Vanilla;
using _4RTools.Utils;

namespace _4RTools.Forms
{
    public sealed class VanillaDiagnosticsForm : Form, IObserver
    {
        private readonly Subject subject;
        private readonly Timer timer = new Timer();
        private readonly VanillaSettingsAutoSave autosave;
        private readonly Label saveStatus = new Label { AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Color.DimGray };
        private readonly Dictionary<Control, string> saveErrors = new Dictionary<Control, string>();
        private Profile settingsProfile;
        private bool loadingSettings, disposed;
        private readonly ComboBox processes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 240 };
        private readonly NumericUpDown interval = new VanillaSettingsNumber { Minimum = 250, Maximum = 10000, Increment = 250, Width = 80 };
        private readonly TextBox mapEditor = new TextBox { Multiline = true, AcceptsReturn = true, AcceptsTab = true, ScrollBars = ScrollBars.Both, WordWrap = false, Dock = DockStyle.Fill, Font = new Font(FontFamily.GenericMonospace, 10) };
        private readonly Label connection = new Label { AutoSize = true, MaximumSize = new Size(1050, 0) };
        private readonly Label identity = new Label { AutoSize = true, MaximumSize = new Size(1050, 0) };
        private readonly TextBox note = new TextBox { Width = 300 };
        private readonly StableDataGridView values = new StableDataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        private readonly TextBox events = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        private IStateSource source;
        private VanillaClientState latest;
        private VanillaExecutableIdentity executable;
        private VanillaStateAdapter adapter;
        private string executablePath;
        private string buildProfile;

        public VanillaDiagnosticsForm(Subject subject = null) : this(subject, true) { }

        // Native UI regression harness can render snapshots without enumerating real clients.
        internal VanillaDiagnosticsForm(Subject subject, bool enumerateProcesses)
        {
            this.subject = subject;
            autosave = new VanillaSettingsAutoSave(450, SaveEditedSetting);
            subject?.Attach(this);
            Text = "Vanilla Automation — read-only diagnostics (local extension)";
            ClientSize = new Size(1100, 740);
            MinimumSize = new Size(820, 540);
            StartPosition = FormStartPosition.CenterParent;
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 110));
            var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
            controls.Controls.Add(processes);
            AddButton(controls, "Refresh processes", RefreshProcesses);
            AddButton(controls, "Connect read-only", Connect);
            AddButton(controls, "Offline demo", StartDemo);
            AddButton(controls, "Disconnect", () => Stop("Disconnected; observations cleared."));
            AddButton(controls, "Pause / resume", () =>
            {
                if (source == null || source.IsStopped) return;
                if (timer.Enabled) { timer.Stop(); SetLabelText(connection, "PAUSED — displayed observations are stale; no automation is attached."); Log("Polling paused."); }
                else { BeginPolling(); Log("Polling resumed."); }
            });
            controls.Controls.Add(new Label { Text = "Poll (ms)", AutoSize = true, Padding = new Padding(0, 6, 0, 0) });
            controls.Controls.Add(interval);
            interval.ValueChanged += (s, e) => QueueSettings(interval);
            interval.TextChanged += (s, e) => QueueSettings(interval);
            interval.Leave += (s, e) => autosave.Flush(interval);
            interval.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                QueueSettings(interval); autosave.Flush(interval); e.SuppressKeyPress = true;
            };
            mapEditor.TextChanged += (s, e) => QueueSettings(mapEditor);
            mapEditor.Leave += (s, e) => autosave.Flush(mapEditor);
            layout.Controls.Add(controls, 0, 0);
            layout.Controls.Add(connection, 0, 1);
            layout.Controls.Add(identity, 0, 2);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var stateTab = new TabPage("Observed state");
            foreach (string name in new[] { "Field", "Value", "Validation", "Address", "Last observed (UTC)", "Last changed (UTC)", "Evidence / error" })
                values.Columns.Add(name, name);
            stateTab.Controls.Add(values);
            tabs.TabPages.Add(stateTab);
            var mapTab = new TabPage("Advanced diagnostics");
            var mapLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1 };
            mapLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mapLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            mapLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mapLayout.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(1000, 0), Text = "An empty map uses the matching build's available mappings. Field validation is shown separately from observed values. Changes apply on the next connection. No scanning or input is performed." }, 0, 0);
            mapLayout.Controls.Add(mapEditor, 0, 1);
            var mapButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            AddButton(mapButtons, "Load map…", LoadMap);
            mapButtons.Controls.Add(saveStatus);
            mapLayout.Controls.Add(mapButtons, 0, 2);
            mapTab.Controls.Add(mapLayout);
            tabs.TabPages.Add(mapTab);
            layout.Controls.Add(tabs, 0, 3);
            var logLayout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1 };
            logLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            logLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var logButtons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
            logButtons.Controls.Add(note);
            AddButton(logButtons, "Mark controlled action", () => { Log("Action note: " + note.Text); note.Clear(); });
            AddButton(logButtons, "Export current snapshot…", ExportSnapshot);
            AddButton(logButtons, "Export log…", () =>
            {
                using (var dialog = new SaveFileDialog { Filter = "Text log (*.txt)|*.txt", FileName = "vanilla-diagnostics.txt" })
                    if (dialog.ShowDialog(this) == DialogResult.OK) File.WriteAllText(dialog.FileName, events.Text);
            });
            AddButton(logButtons, "Clear log", () => events.Clear());
            logLayout.Controls.Add(logButtons, 0, 0);
            logLayout.Controls.Add(events, 0, 1);
            layout.Controls.Add(logLayout, 0, 4);
            Controls.Add(layout);
            timer.Tick += (s, e) => Poll();
            LoadSettings();
            if (enumerateProcesses) RefreshProcesses();
            Stop("Disconnected. Use Offline demo without a game, or explicitly connect read-only. Automation is not enabled.");
        }

        private void AddButton(Control parent, string text, System.Action action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += (s, e) =>
            {
                try { action(); }
                catch (Exception ex) { SetLabelText(connection, ex.Message); Log(ex.ToString()); }
            };
            parent.Controls.Add(button);
        }

        private void RefreshProcesses()
        {
            processes.Items.Clear();
            foreach (Process process in Process.GetProcessesByName("Vanilla MMO"))
            {
                using (process) processes.Items.Add(new ProcessChoice(process.Id, process.ProcessName));
            }
            if (processes.Items.Count > 0) processes.SelectedIndex = 0;
        }

        public void StartDemo()
        {
            Stop("Starting offline demo.");
            source = new DemoStateSource();
            BeginPolling();
        }

        private void Connect()
        {
            autosave.Flush();
            if (saveErrors.Count != 0) throw new InvalidOperationException("Correct the unsaved diagnostics settings before connecting.");
            Stop("Connecting read-only.");
            var choice = processes.SelectedItem as ProcessChoice;
            if (choice == null) throw new InvalidOperationException("No Vanilla MMO process selected. Start the game and refresh the list.");
            var configured = VanillaMemoryMap.Parse(settingsProfile.VanillaDiagnostics.MemoryMapJson);
            var memory = new ReadOnlyProcessMemory(choice.Id);
            try
            {
                executablePath = memory.ExecutablePath;
                executable = VanillaExecutableIdentity.Read(executablePath);
                var profile = VanillaBuildProfile.Find(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "VanillaBuilds"), executable, Log);
                buildProfile = profile?.Label ?? "Unknown build";
                if (configured.Fields.Count == 0 && profile != null) configured = profile.MemoryMap;
                adapter = new VanillaStateAdapter(profile, executable);
                source = new MemoryStateSource(memory, configured);
            }
            catch { memory.Dispose(); throw; }
            BeginPolling();
        }

        private void BeginPolling()
        {
            timer.Interval = settingsProfile.VanillaDiagnostics.PollIntervalMilliseconds;
            Poll();
            if (source != null && !source.IsStopped) timer.Start();
        }

        private void Poll()
        {
            if (source == null) return;
            try
            {
                latest = source.Poll(DateTimeOffset.UtcNow);
                latest.ExecutablePath = executablePath;
                latest.Fingerprint = executable?.Sha256;
                latest.BuildProfile = buildProfile;
                adapter?.Observe(latest, TimeSpan.Zero);
                SetLabelText(connection, (latest.IsDemo ? "OFFLINE DEMO — simulated values. " : "LIVE READ-ONLY — see each field's validation. ") + source.Status);
                string identityText = string.Format(CultureInfo.InvariantCulture, "{0} | PID: {1} | module: {2} | target pointer bytes: {3} | profile: {4}",
                    latest.ProcessName, latest.ProcessId, latest.ModuleBaseAddress.HasValue ? "0x" + latest.ModuleBaseAddress.Value.ToString("X8") : "—", latest.TargetPointerSize, ProfileSingleton.GetCurrent().Name);
                if (executable != null) identityText += " | SHA256: " + executable.Sha256 + " | " + executablePath;
                if (buildProfile != null) identityText += " | Build: " + buildProfile;
                SetLabelText(identity, identityText);
                UpdateValuesGrid();
                if (source.IsStopped)
                {
                    timer.Stop();
                    Log(source.Status + " Polling stopped. No retry or alternate access is attempted.");
                }
            }
            catch (Exception ex)
            {
                Stop("Observation stopped: " + ex.Message);
                Log(ex.ToString());
            }
        }

        private void UpdateValuesGrid()
        {
            if (latest == null) return;
            values.SuspendLayout();
            try
            {
                foreach (var field in latest.Fields)
                {
                    StateValue observed = field.Value;
                    object[] cells =
                    {
                        field.Key,
                        observed.IsAvailable ? FormatValue(observed.UntypedValue) : "Unavailable",
                        observed.Validation,
                        observed.Address.HasValue ? "0x" + observed.Address.Value.ToString("X", CultureInfo.InvariantCulture) : "—",
                        Time(observed.LastObservedAtUtc),
                        Time(observed.LastChangedAtUtc),
                        observed.Error ?? observed.Evidence
                    };
                    DataGridViewRow row = values.Rows.Cast<DataGridViewRow>()
                        .FirstOrDefault(candidate => Equals(candidate.Cells[0].Value, field.Key));
                    if (row == null)
                    {
                        int index = values.Rows.Add(cells);
                        values.Rows[index].Tag = field.Key;
                        continue;
                    }
                    for (int column = 0; column < cells.Length; column++)
                    {
                        object current = row.Cells[column].Value;
                        object next = cells[column];
                        if (!Equals(current, next)) row.Cells[column].Value = next;
                    }
                }
            }
            finally { values.ResumeLayout(false); }
        }

        private static void SetLabelText(Label label, string text)
        {
            if (!string.Equals(label.Text, text, StringComparison.Ordinal)) label.Text = text;
        }

        private static string FormatValue(object value)
        {
            var statuses = value as uint[];
            return statuses == null ? Convert.ToString(value, CultureInfo.InvariantCulture) : string.Join(", ", statuses);
        }

        private static string Time(DateTimeOffset? value) => value?.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) ?? "—";

        private void Stop(string status)
        {
            timer.Stop();
            source?.Dispose();
            source = null;
            latest = null;
            executable = null;
            adapter = null;
            executablePath = null;
            buildProfile = null;
            values.Rows.Clear();
            SetLabelText(identity, "No current observation.");
            SetLabelText(connection, status);
            Log(status);
        }

        private void LoadSettings()
        {
            autosave.Cancel();
            loadingSettings = true;
            try
            {
                settingsProfile = ProfileSingleton.GetCurrent();
                var settings = settingsProfile.VanillaDiagnostics;
                settings.Validate();
                VanillaSettingsNumber.Load(interval, settings.PollIntervalMilliseconds);
                timer.Interval = settings.PollIntervalMilliseconds;
                mapEditor.Text = JsonConvert.SerializeObject(VanillaMemoryMap.Parse(settings.MemoryMapJson), Formatting.Indented);
                saveErrors.Clear(); saveStatus.Text = "Auto-save on"; saveStatus.ForeColor = Color.DimGray;
            }
            finally { loadingSettings = false; }
        }

        private void QueueSettings(Control edited)
        {
            if (loadingSettings || disposed) return;
            autosave.Schedule(edited);
            if (saveErrors.Count == 0) { saveStatus.Text = "Saving..."; saveStatus.ForeColor = Color.DimGray; }
        }

        private void SaveEditedSetting(Control edited)
        {
            if (loadingSettings || disposed || settingsProfile == null) return;
            try
            {
                var current = settingsProfile.VanillaDiagnostics;
                var value = new VanillaDiagnosticsSettings { PollIntervalMilliseconds = current.PollIntervalMilliseconds,
                    MemoryMapJson = current.MemoryMapJson };
                if (ReferenceEquals(edited, interval)) value.PollIntervalMilliseconds = (int)VanillaSettingsNumber.Read(interval);
                else if (ReferenceEquals(edited, mapEditor)) value.MemoryMapJson = mapEditor.Text;
                else return;
                value.Validate();
                if (value.PollIntervalMilliseconds != current.PollIntervalMilliseconds || value.MemoryMapJson != current.MemoryMapJson)
                    ProfileSingleton.SetVanillaDiagnostics(settingsProfile, value);
                timer.Interval = value.PollIntervalMilliseconds;
                saveErrors.Remove(edited);
            }
            catch (Exception ex) { saveErrors[edited] = ex.Message; }
            saveStatus.Text = saveErrors.Count == 0 ? "Saved" : "Not saved: " + saveErrors.Values.First();
            saveStatus.ForeColor = saveErrors.Count == 0 ? Color.DarkGreen : Color.Firebrick;
        }

        private void LoadMap()
        {
            using (var dialog = new OpenFileDialog { Filter = "JSON map (*.json)|*.json", CheckFileExists = true })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                var map = VanillaMemoryMap.Load(dialog.FileName);
                mapEditor.Text = JsonConvert.SerializeObject(map, Formatting.Indented);
                autosave.Flush(mapEditor);
                Log("Loaded map " + dialog.FileName + ". Reconnect to apply.");
            }
        }

        private void ExportSnapshot()
        {
            if (latest == null) throw new InvalidOperationException("There is no current snapshot to export.");
            using (var dialog = new SaveFileDialog { Filter = "JSON (*.json)|*.json", FileName = "vanilla-observation.json" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                File.WriteAllText(dialog.FileName, JsonConvert.SerializeObject(new { Executable = executable, ExecutablePath = executablePath, Paused = !timer.Enabled, Snapshot = latest, Notes = events.Text }, Formatting.Indented));
            }
        }

        private void Log(string message)
        {
            // Keep the live UI bounded; exports explicitly include only the retained notes.
            if (events.TextLength > 32000) events.Text = events.Text.Substring(events.TextLength - 16000);
            events.AppendText(DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
        }

        public void Update(ISubject sender)
        {
            var message = (sender as Subject)?.Message;
            if (message?.code == MessageCode.PROFILE_CHANGED)
            {
                // Flush to the profile that owned the edit, even though the global
                // selector has already switched to its replacement.
                autosave.Flush();
                Stop("Profile changed; observations cleared.");
                LoadSettings();
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            FlushPendingSettings();
            base.OnFormClosing(e);
        }

        private void FlushPendingSettings()
        {
            foreach (Control control in saveErrors.Keys.ToArray()) autosave.Schedule(control);
            autosave.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed)
            {
                FlushPendingSettings(); disposed = true; autosave.Dispose();
                timer.Stop();
                timer.Dispose();
                source?.Dispose();
                subject?.Detach(this);
            }
            base.Dispose(disposing);
        }

        private sealed class StableDataGridView : DataGridView
        {
            public StableDataGridView()
            {
                DoubleBuffered = true;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
                UpdateStyles();
            }
        }

        private sealed class ProcessChoice
        {
            public int Id { get; }
            private readonly string name;
            public ProcessChoice(int id, string name) { Id = id; this.name = name; }
            public override string ToString() => name + ".exe — " + Id;
        }
    }
}
