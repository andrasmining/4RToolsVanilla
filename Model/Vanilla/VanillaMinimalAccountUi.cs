using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed partial class VanillaReconnectForm
    {
        private bool minimalAccountButtonsInstalled;

        internal void InstallMinimalAccountButtons()
        {
            if (minimalAccountButtonsInstalled) return;
            minimalAccountButtonsInstalled = true;
            ReplaceAccountButton("Add", AddAccountMinimal);
            ReplaceAccountButton("Edit", EditAccountMinimal);
            ReplaceAccountButton("Remove", RemoveAccountMinimal);
            RemoveButton("Run login now (selected)");
        }

        private void ReplaceAccountButton(string text, System.Action action)
        {
            Button existing = FindButton(this, text);
            if (existing == null || existing.Parent == null) return;
            Control parent = existing.Parent;
            int index = parent.Controls.GetChildIndex(existing);
            parent.Controls.Remove(existing);
            existing.Dispose();
            var replacement = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
            replacement.Click += (s, e) => action();
            parent.Controls.Add(replacement);
            parent.Controls.SetChildIndex(replacement, index);
            help.SetToolTip(replacement, text == "Add"
                ? "Save another character, including another slot on the same username. At most two characters may be enabled."
                : text == "Edit" ? "Edit description, username, slot, character name, password, proxy and resume hotkey."
                : "Remove the selected character profile; a running client is left untouched.");
        }

        private void AddAccountMinimal()
        {
            if (accountCatalog == null || accountCatalogStore == null) return;
            EditCharacter(new VanillaReconnectAccount
            { Label = "Character " + (accountCatalog.Count + 1), Enabled = false, CharacterSlot = null, ProxyNeedsConfiguration = true }, null);
        }

        private void EditAccountMinimal()
        {
            var selected = SelectedCatalogAccount();
            if (selected != null && accountCatalogStore != null) EditCharacter(selected.Clone(), selected.Id);
        }

        private void EditCharacter(VanillaReconnectAccount copy, string replacingId)
        {
            characterEditorOpen = true;
            try
            {
                using (var dialog = new VanillaMinimalAccountDialog(supervisor, copy,
                    VanillaAccountProxyPreferences.Get(copy.Id, settings.Proxy)))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    var candidate = accountCatalog.Select(a => a.Clone()).ToList();
                    int index = candidate.FindIndex(a => a.Id == replacingId);
                    if (index < 0) candidate.Add(dialog.Account); else candidate[index] = dialog.Account;
                    VanillaCharacterRoster.Validate(candidate);
                    accountCatalogStore.Save(candidate);
                    if (!dialog.Account.ProxyNeedsConfiguration)
                        VanillaAccountProxyPreferences.Set(dialog.Account.Id, dialog.ProxyRoute);
                    accountCatalog = candidate;
                    PersistCatalogAndRefresh("Character saved");
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Character", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { characterEditorOpen = false; }
        }

        private void RemoveAccountMinimal()
        {
            var selected = SelectedCatalogAccount();
            if (selected == null || accountCatalog == null || accountCatalogStore == null) return;
            try
            {
                var candidate = accountCatalog.Where(a => a.Id != selected.Id).Select(a => a.Clone()).ToList();
                VanillaCharacterRoster.Validate(candidate);
                accountCatalogStore.Save(candidate);
                string removedKey = VanillaCharacterRoster.Key(selected);
                if (removedKey != null) ignoredDiscoveredCharacters.Add(removedKey);
                accountCatalog = candidate;
                VanillaAccountProxyPreferences.Remove(selected.Id);
                PersistCatalogAndRefresh("Character removed");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Characters", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }

    internal sealed class VanillaMinimalAccountDialog : Form
    {
        private readonly VanillaReconnectSupervisor supervisor;
        private readonly CheckBox enabled = new CheckBox { Text = "Enabled", AutoSize = true };
        private readonly CheckBox cartMaintenance = new CheckBox { Text = "Cart", AutoSize = true };
        private readonly CheckBox weightEmail = new CheckBox { Text = "E-mail", AutoSize = true };
        private readonly TextBox label = new TextBox { Dock = DockStyle.Fill, MaxLength = 80 };
        private readonly TextBox user = new TextBox { Dock = DockStyle.Fill, MaxLength = 128 };
        private readonly TextBox slot = new TextBox { Width = 80, MaxLength = 2 };
        private readonly ComboBox character = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown, MaxLength = 80 };
        private readonly TextBox password = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        private readonly ComboBox proxy = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 150 };
        private readonly TextBox hotkey = new TextBox { Width = 160, ReadOnly = true };
        private readonly CheckBox smartTeleport = new CheckBox { Text = "Smart Teleport", AutoSize = true };
        private readonly NumericUpDown teleportIdle = new NumericUpDown { Minimum = 5, Maximum = 3600, Value = 60, Width = 90 };
        private readonly TextBox teleportHotkey = new TextBox { Width = 160, ReadOnly = true };
        private readonly ToolTip help = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };
        private int key, teleportKey;
        private bool ctrl, alt, shift, teleportCtrl, teleportAlt, teleportShift, passwordEdited, passwordUnavailable;
        public VanillaReconnectAccount Account { get; private set; }
        public VanillaProxyRoute ProxyRoute { get; private set; }

        internal VanillaMinimalAccountDialog(VanillaReconnectSupervisor supervisor, VanillaReconnectAccount account, VanillaProxyRoute proxyRoute)
        {
            this.supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
            Account = account ?? throw new ArgumentNullException(nameof(account));
            ProxyRoute = proxyRoute;
            Text = "Character";
            Font = new Font("Segoe UI", 9F);
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ClientSize = new Size(520, 455);
            KeyPreview = true;
            Build();
            enabled.Checked = account.Enabled;
            cartMaintenance.Checked = account.EffectiveCartMaintenanceEnabled;
            weightEmail.Checked = account.EffectiveWeightEmailEnabled;
            label.Text = account.Label;
            user.Text = account.UserName;
            slot.Text = account.CharacterSlot?.ToString() ?? string.Empty;
            foreach (string name in supervisor.ObservedCharacters().Where(i => i != null && i.IsFresh(DateTimeOffset.UtcNow))
                .Select(i => i.CharacterName).Distinct(StringComparer.Ordinal)) character.Items.Add(name);
            character.Text = account.CharacterName;
            character.SelectionChangeCommitted += (s, e) => FillSelectedIdentity();
            proxy.DataSource = Enum.GetValues(typeof(VanillaProxyRoute));
            if (account.ProxyNeedsConfiguration) proxy.SelectedIndex = -1; else proxy.SelectedItem = proxyRoute;
            key = account.ResumeKey; ctrl = account.ResumeCtrl; alt = account.ResumeAlt; shift = account.ResumeShift;
            smartTeleport.Checked = account.SmartTeleportEnabled;
            teleportIdle.Value = Math.Max(teleportIdle.Minimum, Math.Min(teleportIdle.Maximum, account.SmartTeleportIdleSeconds));
            teleportKey = account.SmartTeleportKey; teleportCtrl = account.SmartTeleportCtrl; teleportAlt = account.SmartTeleportAlt; teleportShift = account.SmartTeleportShift;
            UpdateHotkey(); UpdateTeleportHotkey();
            try { password.Text = supervisor.GetPassword(account); }
            catch { passwordUnavailable = true; }
            password.TextChanged += (s, e) => passwordEdited = true;
            hotkey.KeyDown += CaptureHotkey;
            teleportHotkey.KeyDown += CaptureTeleportHotkey;
        }

        private void FillSelectedIdentity()
        {
            var matches = supervisor.ObservedCharacters().Where(i => i != null && i.IsFresh(DateTimeOffset.UtcNow)
                && VanillaCharacterRoster.Same(character.Text, i.CharacterName)
                && (string.IsNullOrWhiteSpace(user.Text) || VanillaCharacterRoster.Same(user.Text, i.UserName))).ToArray();
            if (matches.Length != 1) return;
            if (string.IsNullOrWhiteSpace(user.Text) && matches[0].UserName != null) user.Text = matches[0].UserName;
            if (string.IsNullOrWhiteSpace(slot.Text) && matches[0].CharacterSlot.HasValue) slot.Text = matches[0].CharacterSlot.Value.ToString();
        }

        private void Build()
        {
            var table = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 2, RowCount = 11 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 10; i++) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            var toggles = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            toggles.Controls.Add(enabled); toggles.Controls.Add(cartMaintenance); toggles.Controls.Add(weightEmail);
            AddRow(table, 0, string.Empty, toggles);
            AddRow(table, 1, "Description", label);
            AddRow(table, 2, "Username", user);
            AddRow(table, 3, "Slot", slot);
            AddRow(table, 4, "Character name", character);
            AddRow(table, 5, "Password", password);
            AddRow(table, 6, "Proxy", proxy);
            AddRow(table, 7, "Resume hotkey", hotkey);
            var teleportOptions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            teleportOptions.Controls.Add(smartTeleport);
            teleportOptions.Controls.Add(new Label { Text = "after", AutoSize = true, Margin = new Padding(10, 8, 3, 0) });
            teleportOptions.Controls.Add(teleportIdle);
            teleportOptions.Controls.Add(new Label { Text = "sec still", AutoSize = true, Margin = new Padding(3, 8, 0, 0) });
            AddRow(table, 8, "Smart Teleport", teleportOptions);
            AddRow(table, 9, "Teleport hotkey", teleportHotkey);
            var buttons = new FlowLayoutPanel { AutoSize = true, Anchor = AnchorStyles.Right };
            var save = new Button { Text = "Save", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            save.Click += Save; buttons.Controls.Add(save); buttons.Controls.Add(cancel);
            table.Controls.Add(buttons, 1, 10); Controls.Add(table); AcceptButton = save; CancelButton = cancel;
            help.SetToolTip(enabled, "Enable at most two character profiles. Multiple rows may use the same login account.");
            help.SetToolTip(cartMaintenance, "Enable UI-only Cart maintenance for this character. Shared Cart thresholds, category choices and hotkeys are configured on the Weight tab.");
            help.SetToolTip(weightEmail, "Enable e-mail for this character. If Cart maintenance is inactive, use the carried-weight threshold. If this character's Cart switch and the shared Cart master are both active, suppress carried-only warnings and mail waits for BOTH Cart >=99% and carried weight >=50%, after verified Autobattle STOP.");
            help.SetToolTip(label, "Your description; it is not used to identify the running character.");
            help.SetToolTip(character, "Saved expected character. The list contains freshly verified running character names.");
            help.SetToolTip(user, "Filled automatically only from verified memory. Without a verified username mapping, the saved username remains editable.");
            help.SetToolTip(slot, "1-based slot from 1 to 15; blank means unknown, not slot 1. Auto-filled only when verified memory provides it.");
            help.SetToolTip(proxy, "Choose this character's proxy. Discovery never guesses this setting.");
            help.SetToolTip(hotkey, "Press the key combination used to resume Vanilla Autobattle after login.");
            help.SetToolTip(smartTeleport, "Uses fresh verified X/Y for this saved username + character. When enabled, teleports after the configured stationary time; no process selection is needed.");
            help.SetToolTip(teleportIdle, "Default 60 seconds. Any verified X/Y movement resets the timer.");
            help.SetToolTip(teleportHotkey, "Click here and press the exact teleport skill/hotkey combination you use in Vanilla. It is stored on this character row.");
            help.SetToolTip(password, "Stored with Windows DPAPI for this Windows user. Discovery never reads or replaces passwords.");
        }

        private static void AddRow(TableLayoutPanel table, int row, string caption, Control control)
        {
            if (caption.Length > 0) table.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(0, 8, 8, 0) }, 0, row);
            control.Margin = new Padding(0, 3, 0, 3); table.Controls.Add(control, 1, row);
        }
        private void CaptureHotkey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
            key = (int)e.KeyCode; ctrl = e.Control; alt = e.Alt; shift = e.Shift;
            UpdateHotkey(); e.SuppressKeyPress = e.Handled = true;
        }
        private void UpdateHotkey()
        { hotkey.Text = (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + ((Keys)key); }

        private void CaptureTeleportHotkey(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
            teleportKey = (int)e.KeyCode; teleportCtrl = e.Control; teleportAlt = e.Alt; teleportShift = e.Shift;
            UpdateTeleportHotkey(); e.SuppressKeyPress = e.Handled = true;
        }
        private void UpdateTeleportHotkey()
        {
            teleportHotkey.Text = teleportKey < 8 || teleportKey > 254 ? "Click and press hotkey"
                : (teleportCtrl ? "Ctrl+" : "") + (teleportAlt ? "Alt+" : "") + (teleportShift ? "Shift+" : "") + ((Keys)teleportKey);
        }

        private void Save(object sender, EventArgs e)
        {
            try
            {
                int parsed;
                if (!string.IsNullOrWhiteSpace(slot.Text) && (!int.TryParse(slot.Text, out parsed) || parsed < 1 || parsed > 15))
                    throw new ArgumentException("Slot must be 1 to 15, or blank if unknown.");
                var candidate = Account.Clone();
                candidate.Enabled = enabled.Checked;
                candidate.CartMaintenanceEnabled = cartMaintenance.Checked;
                candidate.WeightEmailEnabled = weightEmail.Checked;
                candidate.WeightEnabled = cartMaintenance.Checked || weightEmail.Checked; // keep legacy combined field coherent
                candidate.Label = label.Text.Trim();
                candidate.UserName = user.Text.Trim(); candidate.CharacterName = character.Text.Trim();
                candidate.CharacterSlot = string.IsNullOrWhiteSpace(slot.Text) ? (int?)null : int.Parse(slot.Text);
                candidate.ProxyNeedsConfiguration = !(proxy.SelectedItem is VanillaProxyRoute);
                // Preserve an inaccessible DPAPI value on a disabled row unless explicitly replaced.
                if (!passwordUnavailable || passwordEdited) candidate.ProtectedPassword = string.IsNullOrEmpty(password.Text)
                    ? string.Empty : supervisor.ProtectPassword(password.Text);
                if (!VanillaCharacterRoster.Same(candidate.UserName, Account.UserName) && !passwordEdited
                    && !string.IsNullOrWhiteSpace(Account.ProtectedPassword))
                    throw new ArgumentException("Enter the password for the changed username; the old password will not be reused.");
                candidate.ResumeKey = key; candidate.ResumeCtrl = ctrl; candidate.ResumeAlt = alt; candidate.ResumeShift = shift;
                candidate.SmartTeleportEnabled = smartTeleport.Checked;
                candidate.SmartTeleportIdleSeconds = (int)teleportIdle.Value;
                candidate.SmartTeleportKey = teleportKey; candidate.SmartTeleportCtrl = teleportCtrl;
                candidate.SmartTeleportAlt = teleportAlt; candidate.SmartTeleportShift = teleportShift;
                if (key < 8 || key > 254) throw new ArgumentException("Choose a valid resume hotkey.");
                if (candidate.SmartTeleportEnabled && (candidate.SmartTeleportKey < 8 || candidate.SmartTeleportKey > 254))
                    throw new ArgumentException("Click Teleport hotkey and press the exact key combination before enabling Smart Teleport.");
                VanillaCharacterRoster.Validate(new[] { candidate });
                if (candidate.Enabled)
                {
                    string missing = VanillaReconnectSupervisor.MissingCharacterConfiguration(candidate);
                    if (missing != null || (passwordUnavailable && !passwordEdited))
                        throw new ArgumentException((missing ?? "Enter this Windows user's password") + ". Save as disabled until configured.");
                }
                if (!candidate.ProxyNeedsConfiguration) ProxyRoute = (VanillaProxyRoute)proxy.SelectedItem;
                Account = candidate; DialogResult = DialogResult.OK; Close();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Character", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
        protected override void Dispose(bool disposing)
        { if (disposing) help.Dispose(); base.Dispose(disposing); }
    }
}
