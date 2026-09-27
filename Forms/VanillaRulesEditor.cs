using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;
using _4RTools.Model.Vanilla.Automation;

namespace _4RTools.Forms
{
    /// <summary>Automatically persists validated edits and retains invalid drafts locally.</summary>
    public sealed class VanillaRulesEditor : Form
    {
        private readonly int emergencyKey;
        private readonly List<AutomationRuleSettings> working;
        private readonly System.Action<List<AutomationRuleSettings>> persist;
        private readonly VanillaSettingsAutoSave autosave;
        private readonly Label saveStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(900, 0), Text = "Auto-save on" };
        private readonly ListBox rules = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false };
        private readonly Panel details = new Panel { Dock = DockStyle.Fill };
        private readonly TextBox name = new TextBox { Width = 260, MaxLength = 80 };
        private readonly CheckBox enabled = new CheckBox { Text = "Enable this rule", AutoSize = true };
        private readonly ComboBox condition = DropDown(225);
        private readonly NumericUpDown period = Seconds();
        private readonly NumericUpDown threshold = new VanillaSettingsNumber { Minimum = 0, Maximum = 100, DecimalPlaces = 2, Width = 110 };
        private readonly NumericUpDown status = new VanillaSettingsNumber { Minimum = 0, Maximum = uint.MaxValue, Width = 130 };
        private readonly NumericUpDown cooldown = Seconds();
        private readonly CheckBox preferOutOfCombat = new CheckBox { Text = "Only when out of combat", AutoSize = true };
        private readonly Label explanation = new Label { AutoSize = true, MaximumSize = new Size(710, 0), ForeColor = Color.DimGray };
        private readonly DataGridView sequence = new DataGridView
        {
            Dock = DockStyle.Fill, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Color.White, BorderStyle = BorderStyle.FixedSingle
        };
        private readonly ComboBox stepKind = DropDown(120);
        private int editing = -1;
        private bool loading, saving, disposed;

        public List<AutomationRuleSettings> Rules { get; private set; }

        public VanillaRulesEditor(List<AutomationRuleSettings> rules, int emergencyKey,
            System.Action<List<AutomationRuleSettings>> persist = null)
        {
            this.emergencyKey = emergencyKey;
            this.persist = persist;
            autosave = new VanillaSettingsAutoSave(450, control => SaveRulesAutomatically());
            working = new VanillaAutomationSettings { EmergencyKey = emergencyKey, Rules = rules ?? new List<AutomationRuleSettings>() }.Clone().Rules;
            Rules = new VanillaAutomationSettings { EmergencyKey = emergencyKey, Rules = working }.Clone().Rules;
            Text = "Additional Automation Rules";
            Font = new Font("Segoe UI", 9F);
            ClientSize = new Size(1090, 720);
            MinimumSize = new Size(960, 660);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            BuildLayout();
            RefreshList(working.Count > 0 ? 0 : -1);
            WireAutoSave();
        }

        private void BuildLayout()
        {
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 3 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 250));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var heading = new Label { Text = "Configure timed, health, status, or inactivity actions. All rules share the same stop controls, validation, and sequence scheduler.", AutoSize = true, MaximumSize = new Size(1030, 0), Margin = new Padding(0, 0, 0, 14) };
            layout.Controls.Add(heading, 0, 0);
            layout.SetColumnSpan(heading, 2);
            var listLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 12, 0) };
            listLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            listLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            listLayout.Controls.Add(rules, 0, 0);
            var listCommands = Flow();
            AddButton(listCommands, "Add rule", AddRule);
            AddButton(listCommands, "Remove rule", RemoveRule);
            listLayout.Controls.Add(listCommands, 0, 1);
            layout.Controls.Add(listLayout, 0, 1);
            BuildDetails();
            layout.Controls.Add(details, 1, 1);
            var footer = Flow();
            footer.FlowDirection = FlowDirection.RightToLeft;
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK, MinimumSize = new Size(80, 30) };
            footer.Controls.Add(close);
            footer.Controls.Add(saveStatus);
            footer.Padding = new Padding(0, 10, 0, 0);
            CancelButton = close;
            AcceptButton = close;
            layout.Controls.Add(footer, 0, 2);
            layout.SetColumnSpan(footer, 2);
            Controls.Add(layout);
            rules.SelectedIndexChanged += (s, e) =>
            {
                if (loading) return;
                int selected = rules.SelectedIndex;
                try
                {
                    autosave.Cancel();
                    if (SaveRulesAutomatically()) RefreshList(selected);
                    else
                    {
                        loading = true; rules.SelectedIndex = editing; loading = false;
                    }
                }
                catch (Exception ex)
                {
                    loading = true;
                    rules.SelectedIndex = editing;
                    loading = false;
                    ShowError(ex.Message);
                }
            };
        }

        private void BuildDetails()
        {
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 5 };
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 105));
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
            fields.Controls.Add(Caption("Name"), 0, 0);
            fields.Controls.Add(name, 1, 0);
            fields.SetColumnSpan(name, 3);
            fields.Controls.Add(enabled, 1, 1);
            fields.Controls.Add(Caption("Condition"), 0, 2);
            fields.Controls.Add(condition, 1, 2);
            fields.Controls.Add(Caption("Cooldown (s)"), 2, 2);
            fields.Controls.Add(cooldown, 3, 2);
            fields.Controls.Add(Caption("Duration (s)"), 0, 3);
            fields.Controls.Add(period, 1, 3);
            fields.Controls.Add(Caption("Below (%)"), 2, 3);
            fields.Controls.Add(threshold, 3, 3);
            fields.Controls.Add(Caption("Status ID"), 0, 4);
            fields.Controls.Add(status, 1, 4);
            fields.Controls.Add(preferOutOfCombat, 2, 4);
            fields.SetColumnSpan(preferOutOfCombat, 2);
            foreach (var entry in new[]
            {
                new Choice<RuleCondition>(RuleCondition.Timed, "Every interval"),
                new Choice<RuleCondition>(RuleCondition.HealthBelow, "HP below percentage"),
                new Choice<RuleCondition>(RuleCondition.SpBelow, "SP below percentage"),
                new Choice<RuleCondition>(RuleCondition.StatusPresent, "Status present"),
                new Choice<RuleCondition>(RuleCondition.StatusMissing, "Status missing"),
                new Choice<RuleCondition>(RuleCondition.NoTarget, "No target for duration"),
                new Choice<RuleCondition>(RuleCondition.NoCombat, "No combat for duration"),
                new Choice<RuleCondition>(RuleCondition.SamePosition, "Same position for duration")
            }) condition.Items.Add(entry);
            condition.SelectedIndexChanged += (s, e) => ExplainCondition();
            grid.Controls.Add(fields, 0, 0);
            explanation.Margin = new Padding(0, 10, 0, 10);
            grid.Controls.Add(explanation, 0, 1);

            var kinds = new List<Choice<SequenceStepKind>>
            {
                new Choice<SequenceStepKind>(SequenceStepKind.PressKey, "Press key"),
                new Choice<SequenceStepKind>(SequenceStepKind.KeyDown, "Key down"),
                new Choice<SequenceStepKind>(SequenceStepKind.KeyUp, "Key up"),
                new Choice<SequenceStepKind>(SequenceStepKind.Wait, "Wait"),
                new Choice<SequenceStepKind>(SequenceStepKind.Click, "Click")
            };
            sequence.Columns.Add(new DataGridViewComboBoxColumn { Name = "Kind", HeaderText = "Action", DataSource = kinds, DisplayMember = "Label", ValueMember = "Value", FillWeight = 110 });
            var keys = new List<Choice<int>> { new Choice<int>(0, "Choose a key") };
            for (int key = (int)Keys.F1; key <= (int)Keys.F12; key++) keys.Add(new Choice<int>(key, ((Keys)key).ToString()));
            for (int key = (int)Keys.A; key <= (int)Keys.Z; key++) keys.Add(new Choice<int>(key, ((Keys)key).ToString()));
            for (int key = (int)Keys.D0; key <= (int)Keys.D9; key++) keys.Add(new Choice<int>(key, ((char)key).ToString()));
            for (int key = 8; key <= 254; key++)
                if (!keys.Any(item => item.Value == key)) keys.Add(new Choice<int>(key, ((Keys)key).ToString()));
            sequence.Columns.Add(new DataGridViewComboBoxColumn { Name = "Key", HeaderText = "Key", DataSource = keys, DisplayMember = "Label", ValueMember = "Value", FillWeight = 110 });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "Delay", HeaderText = "Delay (ms)", ValueType = typeof(int), FillWeight = 100 });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "X", HeaderText = "Click X", ValueType = typeof(int), FillWeight = 75 });
            sequence.Columns.Add(new DataGridViewTextBoxColumn { Name = "Y", HeaderText = "Click Y", ValueType = typeof(int), FillWeight = 75 });
            sequence.CurrentCellDirtyStateChanged += (s, e) =>
            {
                if (sequence.IsCurrentCellDirty && sequence.CurrentCell is DataGridViewComboBoxCell)
                    sequence.CommitEdit(DataGridViewDataErrorContexts.Commit);
            };
            sequence.DataError += (s, e) => { e.ThrowException = false; explanation.Text = "Enter a valid number or select a key in the sequence."; };
            grid.Controls.Add(sequence, 0, 2);
            var sequenceCommands = Flow();
            foreach (var kind in kinds) stepKind.Items.Add(kind);
            stepKind.SelectedIndex = 3;
            sequenceCommands.Controls.Add(stepKind);
            AddButton(sequenceCommands, "Add step", AddStep);
            AddButton(sequenceCommands, "Remove", RemoveStep);
            AddButton(sequenceCommands, "Up", () => MoveStep(-1));
            AddButton(sequenceCommands, "Down", () => MoveStep(1));
            grid.Controls.Add(sequenceCommands, 0, 3);
            details.Controls.Add(grid);
        }

        private void ReadCurrent()
        {
            if (editing < 0 || editing >= working.Count) return;
            if (!sequence.EndEdit()) throw new ArgumentException("Finish editing the sequence before changing rules.");
            var steps = new List<SequenceStep>();
            foreach (DataGridViewRow row in sequence.Rows)
            {
                if (!(row.Cells[0].Value is SequenceStepKind)) throw new ArgumentException("Choose an action for each step.");
                var kind = (SequenceStepKind)row.Cells[0].Value;
                steps.Add(new SequenceStep
                {
                    Kind = kind,
                    Key = kind == SequenceStepKind.PressKey || kind == SequenceStepKind.KeyDown || kind == SequenceStepKind.KeyUp ? CellNumber(row, 1) : 0,
                    DelayMs = CellNumber(row, 2),
                    X = kind == SequenceStepKind.Click ? CellNumber(row, 3) : 0,
                    Y = kind == SequenceStepKind.Click ? CellNumber(row, 4) : 0
                });
            }
            working[editing] = new AutomationRuleSettings
            {
                Name = name.Text.Trim(), Enabled = enabled.Checked,
                Condition = ((Choice<RuleCondition>)condition.SelectedItem).Value,
                PeriodMs = (int)(VanillaSettingsNumber.Read(period) * 1000), ThresholdPercent = VanillaSettingsNumber.Read(threshold),
                StatusId = (uint)VanillaSettingsNumber.Read(status), CooldownMs = (int)(VanillaSettingsNumber.Read(cooldown) * 1000),
                PreferOutOfCombat = preferOutOfCombat.Checked, Sequence = steps
            };
        }

        private void LoadRule(int index)
        {
            editing = index;
            details.Enabled = index >= 0 && index < working.Count;
            if (!details.Enabled)
            {
                name.Clear();
                sequence.Rows.Clear();
                explanation.Text = "Add a rule to configure its condition and sequence.";
                return;
            }
            var rule = working[index];
            name.Text = rule.Name;
            enabled.Checked = rule.Enabled;
            condition.SelectedItem = condition.Items.Cast<Choice<RuleCondition>>().First(item => item.Value == rule.Condition);
            VanillaSettingsNumber.Load(period, rule.PeriodMs / 1000M);
            VanillaSettingsNumber.Load(threshold, rule.ThresholdPercent);
            VanillaSettingsNumber.Load(status, rule.StatusId);
            VanillaSettingsNumber.Load(cooldown, rule.CooldownMs / 1000M);
            preferOutOfCombat.Checked = rule.PreferOutOfCombat;
            sequence.Rows.Clear();
            foreach (var step in rule.Sequence) sequence.Rows.Add(step.Kind, step.Key, step.DelayMs, step.X, step.Y);
            ExplainCondition();
        }

        private void RefreshList(int selected)
        {
            loading = true;
            try
            {
                rules.Items.Clear();
                foreach (var rule in working) rules.Items.Add(RuleLabel(rule));
                rules.SelectedIndex = selected;
                LoadRule(selected);
            }
            finally { loading = false; }
        }

        private void RefreshListLabels()
        {
            loading = true;
            try { for (int i = 0; i < working.Count; i++) rules.Items[i] = RuleLabel(working[i]); }
            finally { loading = false; }
        }

        private static string RuleLabel(AutomationRuleSettings rule) => (rule.Enabled ? "ON  " : "OFF  ") + (string.IsNullOrWhiteSpace(rule.Name) ? "Unnamed rule" : rule.Name);

        private void AddRule()
        {
            ReadCurrent();
            if (working.Count >= 32) throw new ArgumentException("A profile supports at most 32 additional rules.");
            working.Add(new AutomationRuleSettings { Name = "Timed action " + (working.Count + 1) });
            RefreshList(working.Count - 1);
            SaveRulesAutomatically();
        }

        private void RemoveRule()
        {
            if (editing < 0) return;
            int next = editing;
            working.RemoveAt(editing);
            RefreshList(Math.Min(next, working.Count - 1));
            SaveRulesAutomatically();
        }

        private void AddStep()
        {
            if (sequence.Rows.Count >= 64) throw new ArgumentException("A sequence supports at most 64 steps.");
            var kind = ((Choice<SequenceStepKind>)stepKind.SelectedItem).Value;
            int index = sequence.Rows.Add(kind, 0, kind == SequenceStepKind.Wait ? 500 : 0, 0, 0);
            sequence.CurrentCell = sequence.Rows[index].Cells[kind == SequenceStepKind.Wait ? 2 : kind == SequenceStepKind.Click ? 3 : 1];
            sequence.Rows[index].Selected = true;
            QueueSave(sequence);
        }

        private void RemoveStep()
        {
            if (sequence.CurrentRow == null) return;
            sequence.Rows.RemoveAt(sequence.CurrentRow.Index); QueueSave(sequence);
        }

        private void MoveStep(int delta)
        {
            if (sequence.CurrentRow == null || !sequence.EndEdit()) return;
            int from = sequence.CurrentRow.Index;
            int to = from + delta;
            if (to < 0 || to >= sequence.Rows.Count) return;
            var values = sequence.Rows[from].Cells.Cast<DataGridViewCell>().Select(cell => cell.Value).ToArray();
            sequence.Rows.RemoveAt(from);
            sequence.Rows.Insert(to, values);
            sequence.CurrentCell = sequence.Rows[to].Cells[0];
            QueueSave(sequence);
        }

        private void WireAutoSave()
        {
            name.TextChanged += (s, e) => QueueSave(name);
            name.Leave += (s, e) => autosave.Flush();
            foreach (NumericUpDown number in new[] { period, threshold, status, cooldown })
            {
                number.ValueChanged += (s, e) => QueueSave(number);
                number.TextChanged += (s, e) => QueueSave(number);
                number.Leave += (s, e) => autosave.Flush();
            }
            foreach (CheckBox box in new[] { enabled, preferOutOfCombat })
                box.CheckedChanged += (s, e) => { QueueSave(box); autosave.Flush(); };
            condition.SelectedIndexChanged += (s, e) => { QueueSave(condition); autosave.Flush(); };
            sequence.CellValueChanged += (s, e) => { if (e.RowIndex >= 0) QueueSave(sequence); };
            sequence.CellEndEdit += (s, e) => { QueueSave(sequence); autosave.Flush(); };
        }

        private void QueueSave(Control control)
        {
            if (loading || saving || disposed) return;
            // One rule document is the validation unit, including its sequence.
            autosave.Schedule(this);
            saveStatus.Text = "Saving..."; saveStatus.ForeColor = Color.DimGray;
        }

        private bool SaveRulesAutomatically()
        {
            if (loading || saving || disposed) return true;
            saving = true;
            try
            {
                ReadCurrent();
                var settings = new VanillaAutomationSettings { EmergencyKey = emergencyKey, Rules = working };
                settings.Validate();
                var saved = settings.Clone().Rules;
                if (!Newtonsoft.Json.Linq.JToken.DeepEquals(Newtonsoft.Json.Linq.JToken.FromObject(saved),
                    Newtonsoft.Json.Linq.JToken.FromObject(Rules))) persist?.Invoke(saved);
                Rules = saved;
                RefreshListLabels();
                saveStatus.Text = "Saved"; saveStatus.ForeColor = Color.DarkGreen;
                return true;
            }
            catch (Exception ex) { saveStatus.Text = "Not saved: " + ex.Message; saveStatus.ForeColor = Color.Firebrick; return false; }
            finally { saving = false; }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (sequence.IsCurrentCellInEditMode) sequence.EndEdit();
            autosave.Cancel(); SaveRulesAutomatically();
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !disposed) { autosave.Cancel(); SaveRulesAutomatically(); disposed = true; autosave.Dispose(); }
            base.Dispose(disposing);
        }

        private void ExplainCondition()
        {
            var choice = condition.SelectedItem as Choice<RuleCondition>;
            if (choice == null) return;
            bool percentage = choice.Value == RuleCondition.HealthBelow || choice.Value == RuleCondition.SpBelow;
            bool statusCondition = choice.Value == RuleCondition.StatusPresent || choice.Value == RuleCondition.StatusMissing;
            threshold.Enabled = percentage;
            status.Enabled = statusCondition;
            period.Enabled = !percentage && !statusCondition;
            string meaning;
            switch (choice.Value)
            {
                case RuleCondition.Timed: meaning = "Execute once each duration, subject to cooldown and ready state."; break;
                case RuleCondition.HealthBelow: meaning = "Execute when validated HP is strictly below the percentage."; break;
                case RuleCondition.SpBelow: meaning = "Execute when validated SP is strictly below the percentage."; break;
                case RuleCondition.StatusPresent: meaning = "Execute when the selected status ID is present in a validated status list."; break;
                case RuleCondition.StatusMissing: meaning = "Execute when the selected status ID is absent from a validated status list. An unavailable list is never treated as missing."; break;
                case RuleCondition.NoTarget: meaning = "Execute after the configured duration without a target; a verified target signal is required."; break;
                case RuleCondition.NoCombat: meaning = "Execute after the configured duration without combat; verified combat and casting signals are required."; break;
                default: meaning = "Execute after the configured duration at the same position without combat. Verified movement and combat signals are required."; break;
            }
            explanation.Text = meaning + " Delay waits after each step, in milliseconds. Click X/Y are inside the selected game window.";
        }

        private Button AddButton(Control parent, string text, System.Action action)
        {
            var button = new Button { Text = text, AutoSize = true, MinimumSize = new Size(65, 28) };
            button.Click += (s, e) => { try { action(); } catch (Exception ex) { ShowError(ex.Message); } };
            parent.Controls.Add(button);
            return button;
        }

        private void ShowError(string message) => MessageBox.Show(this, message, "Automation rule", MessageBoxButtons.OK, MessageBoxIcon.Information);
        private static ComboBox DropDown(int width) => new ComboBox { Width = width, DropDownStyle = ComboBoxStyle.DropDownList };
        private static NumericUpDown Seconds() => new VanillaSettingsNumber { Minimum = 1, Maximum = 3600, DecimalPlaces = 1, Width = 110 };
        private static FlowLayoutPanel Flow() => new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = new Padding(0, 5, 0, 0) };
        private static Label Caption(string text) => new Label { Text = text, AutoSize = true, Padding = new Padding(0, 5, 0, 0) };
        private static int CellNumber(DataGridViewRow row, int column)
        {
            int value;
            if (!int.TryParse(Convert.ToString(row.Cells[column].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                throw new ArgumentException("Step " + (row.Index + 1) + " needs a whole number in " + row.Cells[column].OwningColumn.HeaderText + ".");
            return value;
        }

        private sealed class Choice<T>
        {
            public T Value { get; }
            public string Label { get; }
            public Choice(T value, string label) { Value = value; Label = label; }
            public override string ToString() => Label;
        }
    }
}
