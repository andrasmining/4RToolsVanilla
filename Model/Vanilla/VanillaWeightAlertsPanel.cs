using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaWeightAlertsPanel : UserControl
    {
        private readonly VanillaWeightAlertService service;
        private readonly CheckBox autoCart = new CheckBox { Text = "Cart master", AutoSize = true };
        private readonly CheckBox closeWhenComplete = new CheckBox { Text = "Close client when farming is complete (Cart >=99% AND Weight >=50%)", AutoSize = true };
        private readonly NumericUpDown autoThreshold = Number(1, 100, 50, 1);
        private readonly NumericUpDown autoRearm = Number(0, 99, 40, 1);
        private readonly CheckBox transferUse = new CheckBox { Text = "Use", AutoSize = true, Checked = true };
        private readonly CheckBox transferEquip = new CheckBox { Text = "Equip", AutoSize = true };
        private readonly CheckBox transferEtc = new CheckBox { Text = "Etc", AutoSize = true, Checked = true };
        private readonly TextBox autobattleStopHotkey = new TextBox { Width = 130, ReadOnly = true };
        private readonly TextBox inventoryHotkey = new TextBox { Width = 130, ReadOnly = true };
        private readonly TextBox cartHotkey = new TextBox { Width = 130, ReadOnly = true };
        private int autobattleStopKey, inventoryKey, cartKey;
        private bool autobattleStopCtrl, autobattleStopAlt, autobattleStopShift;
        private bool inventoryCtrl, inventoryAlt, inventoryShift, cartCtrl, cartAlt, cartShift;

        private readonly CheckBox enabled = new CheckBox { Text = "Normal e-mail", AutoSize = true };
        private readonly NumericUpDown threshold = Number(1, 100, 85, 1);
        private readonly NumericUpDown rearm = Number(0, 99, 80, 1);
        private readonly NumericUpDown pollSeconds = Number(2, 60, 5, 0);
        private readonly NumericUpDown cooldownMinutes = Number(1, 1440, 30, 0);
        private readonly TextBox smtpHost = new TextBox { Width = 260 };
        private readonly NumericUpDown smtpPort = Number(1, 65535, 587, 0);
        private readonly CheckBox useSsl = new CheckBox { Text = "TLS/SSL", AutoSize = true, Checked = true };
        private readonly TextBox smtpUser = new TextBox { Width = 300 };
        private readonly TextBox smtpPassword = new TextBox { Width = 300, UseSystemPasswordChar = true };
        private readonly TextBox fromAddress = new TextBox { Width = 300 };
        private readonly TextBox toAddress = new TextBox { Width = 300 };
        private readonly TextBox subjectPrefix = new TextBox { Width = 220 };
        private readonly Button save = new Button { Text = "SAVE WEIGHT SETTINGS", AutoSize = true };
        private readonly Button test = new Button { Text = "SEND TEST E-MAIL", AutoSize = true };
        private readonly Button clearHold = new Button { Text = "CLEAR WEIGHT/CART HOLD", AutoSize = true };
        private readonly Button clearEmergency = new Button { Text = "CLEAR EMERGENCY HOLD", AutoSize = true };
        private readonly NumericUpDown emergencyWeight = Number(0, 99.9M, 50, 1);
        private readonly NumericUpDown emergencySp = Number(0.1M, 100, 25, 1);
        private readonly NumericUpDown emergencyHp = Number(0.1M, 100, 50, 1);
        private readonly CheckBox emergencySendEmail = new CheckBox { Text = "E-mail on emergency", AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
        private readonly Label emergencySaveStatus = new Label { AutoSize = true, MaximumSize = new Size(500, 0),
            ForeColor = Color.DimGray, Margin = new Padding(8, 8, 3, 3) };
        private readonly Label emergencyStatus = new Label { AutoSize = true, MaximumSize = new Size(1100, 0), ForeColor = Color.Firebrick };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(1150, 0), ForeColor = Color.DimGray };
        private readonly DataGridView live = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly ToolTip help = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };
        private VanillaWeightAlertSettings loaded;
        private bool disposed, loadingEmergency;
        private DateTime emergencySavedUntil;

        public VanillaWeightAlertsPanel(VanillaWeightAlertService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            Dock = DockStyle.Fill; BackColor = Color.White; AutoScroll = true;
            BuildLayout(); LoadSettings(); LoadEmergencySettings();
            foreach (NumericUpDown control in new[] { emergencyWeight, emergencySp, emergencyHp })
            {
                control.Increment = 0.1M;
                control.Validated += (s, e) => SaveEmergencySettings(control);
                control.KeyDown += (s, e) =>
                {
                    if (e.KeyCode != Keys.Enter) return;
                    SaveEmergencySettings(control); e.SuppressKeyPress = e.Handled = true;
                };
            }
            emergencySendEmail.CheckedChanged += (s, e) => SaveEmergencySettings(emergencySendEmail);
            save.Click += (s, e) => Guard(SaveSettings);
            test.Click += async (s, e) => await SendTestAsync();
            clearHold.Click += (s, e) => Guard(service.ClearManualHolds);
            clearEmergency.Click += (s, e) => Guard(service.ClearEmergencyHolds);
            autobattleStopHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.AutobattleStop);
            inventoryHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.Inventory);
            cartHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.Cart);
            timer.Tick += (s, e) => RefreshStatus();
            timer.Start(); RefreshStatus();
        }

        private void BuildLayout()
        {
            // Top-docked content grows to its preferred height so AutoScroll can expose
            // every action on shorter desktops instead of squeezing the final rows.
            var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                MinimumSize = new Size(1050, 0), Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            title.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Text = "Weight / Cart management" });
            var info = new Label { AutoSize = true, Text = "ⓘ", Cursor = Cursors.Help, Margin = new Padding(8, 2, 0, 0) };
            help.SetToolTip(info,
                "Character-level Cart and Mail switches are edited on Recovery & relog. "
                + "Cart mode uses verified read-only carried/Cart weight plus ordinary UI input. "
                + "STOP must be verified stationary, HP is guarded while stopped, precision filling starts at 75%, "
                + "Mastela=3, Peco Feather=1, and transient transfer failures retry later.");
            title.Controls.Add(info);
            help.SetToolTip(clearEmergency, "Explicitly clear emergency holds after inspecting the affected characters. Recovery may then restart them. Weight/Cart hold-clear does not clear emergency holds.");
            root.Controls.Add(title, 0, 0);

            root.Controls.Add(BuildEmergencyGroup(), 0, 1);
            root.Controls.Add(BuildCartGroup(), 0, 2);
            root.Controls.Add(BuildMailGroup(), 0, 3);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(save); buttons.Controls.Add(test); buttons.Controls.Add(clearHold); buttons.Controls.Add(clearEmergency); buttons.Controls.Add(status);
            root.Controls.Add(buttons, 0, 4);

            live.Columns.Add("Client", "Client"); live.Columns.Add("Weight", "Weight"); live.Columns.Add("Percent", "%");
            live.Columns.Add("Cart", "Cart"); live.Columns.Add("CartPercent", "Cart %"); live.Columns.Add("Verification", "State");
            var liveHost = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 2 };
            liveHost.RowStyles.Add(new RowStyle(SizeType.AutoSize)); liveHost.RowStyles.Add(new RowStyle(SizeType.Absolute, 100));
            emergencyStatus.Margin = new Padding(0, 10, 0, 4);
            help.SetToolTip(emergencyStatus, "Always active for enabled character rows while 4RTools runs, including with Recovery and Cart OFF. All three configured emergency limits must be crossed in the same fresh verified observation. Only that client closes; no automatic restart until its emergency hold is explicitly cleared.");
            liveHost.Controls.Add(emergencyStatus, 0, 0);
            liveHost.Controls.Add(live, 0, 1); root.Controls.Add(liveHost, 0, 5);
            Controls.Add(root);
            // Docked children do not contribute their minimum width to WinForms'
            // automatic scroll extent; make the two-column settings width explicit.
            AutoScrollMinSize = new Size(root.MinimumSize.Width, 0);
            root.SizeChanged += (s, e) => AutoScrollMinSize = new Size(root.MinimumSize.Width, 0);

            ConfigureHelp();
        }

        private Control BuildEmergencyGroup()
        {
            var group = new GroupBox { Text = "Emergency stop — all three required (AND) • auto-save",
                Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            AddEmergencyField(row, "Weight above %", emergencyWeight);
            AddEmergencyField(row, "SP below %", emergencySp);
            AddEmergencyField(row, "HP below %", emergencyHp);
            row.Controls.Add(emergencySendEmail);
            row.Controls.Add(emergencySaveStatus); group.Controls.Add(row);
            string description = "Close only the affected client when Weight is strictly above its limit AND SP is strictly below its limit AND HP is strictly below its limit. Equality does not trigger. Saves independently when you leave a field or press Enter; pending Cart/mail edits are unchanged. Changing limits does not clear existing emergency holds.";
            help.SetToolTip(group, description);
            foreach (NumericUpDown control in new[] { emergencyWeight, emergencySp, emergencyHp }) help.SetToolTip(control, description);
            help.SetToolTip(emergencySendEmail, "Auto-save. Send one emergency notification using the saved SMTP settings, independently of Normal e-mail and character Mail switches. Save SMTP settings first. Sending never delays emergency closure.");
            return group;
        }

        private static void AddEmergencyField(FlowLayoutPanel row, string caption, NumericUpDown control)
        {
            control.Width = 90;
            var field = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 12, 0) };
            field.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 8, 6, 0) });
            field.Controls.Add(control); row.Controls.Add(field);
        }

        private void LoadEmergencySettings()
        {
            loadingEmergency = true;
            try
            {
                var value = service.EmergencySettings;
                emergencyWeight.Value = value.WeightAbovePercent;
                emergencySp.Value = value.SpBelowPercent;
                emergencyHp.Value = value.HpBelowPercent;
                emergencySendEmail.Checked = value.SendEmail;
            }
            finally { loadingEmergency = false; }
        }

        private void SaveEmergencySettings(Control edited)
        {
            if (disposed || loadingEmergency) return;
            try
            {
                // Value commits the NumericUpDown edit only at validation/Enter.
                // No ValueChanged handler persists partially typed numbers.
                var active = service.EmergencySettings;
                var value = active.Clone();
                if (ReferenceEquals(edited, emergencyWeight)) value.WeightAbovePercent = emergencyWeight.Value;
                else if (ReferenceEquals(edited, emergencySp)) value.SpBelowPercent = emergencySp.Value;
                else if (ReferenceEquals(edited, emergencyHp)) value.HpBelowPercent = emergencyHp.Value;
                else if (ReferenceEquals(edited, emergencySendEmail)) value.SendEmail = emergencySendEmail.Checked;
                else throw new ArgumentException("Unknown emergency field.", nameof(edited));
                if (value.WeightAbovePercent == active.WeightAbovePercent && value.SpBelowPercent == active.SpBelowPercent
                    && value.HpBelowPercent == active.HpBelowPercent && value.SendEmail == active.SendEmail) return;
                service.SaveEmergencySettings(value);
                LoadEmergencySettings();
                emergencySaveStatus.ForeColor = Color.DarkGreen; emergencySaveStatus.Text = "Saved";
                emergencySavedUntil = DateTime.UtcNow.AddSeconds(5);
                RefreshStatus();
            }
            catch (Exception ex)
            {
                LoadEmergencySettings();
                emergencySavedUntil = DateTime.MinValue;
                emergencySaveStatus.ForeColor = Color.Firebrick;
                emergencySaveStatus.Text = "Not saved: " + ex.Message;
            }
        }

        private void ConfigureHelp()
        {
            help.SetToolTip(autoCart,
                "Global Cart master. A character also needs its own Cart switch enabled in Recovery & relog.");
            help.SetToolTip(closeWhenComplete, "Saved with Weight settings. After verified Autobattle STOP at both Cart >=99% AND carried weight >=50%, close only that character's client completely. Its completed hold prevents automatic restart until you explicitly clear the Weight/Cart hold.");
            help.SetToolTip(autoThreshold,
                "Carried-weight percentage that triggers a Cart-maintenance pass for Cart-enabled characters.");
            help.SetToolTip(autoRearm,
                "Re-arm automatic Cart maintenance after carried weight falls below this percentage.");
            help.SetToolTip(transferUse, "Process Use. Known farming rule: Mastela Fruit weighs 3.");
            help.SetToolTip(transferEquip, "Equip has no verified unit-weight rule and is skipped once precision filling starts at 75% Cart.");
            help.SetToolTip(transferEtc, "Process Etc. Known farming rule: Peco Feather weighs 1.");
            help.SetToolTip(autobattleStopHotkey,
                "Dedicated Autobattle STOP hotkey. STOP is verified by 5 continuous stationary X/Y seconds before Cart UI is allowed.");
            help.SetToolTip(inventoryHotkey, "Inventory hotkey used only after verified Autobattle STOP.");
            help.SetToolTip(cartHotkey, "Cart hotkey used only after verified Autobattle STOP.");

            help.SetToolTip(enabled,
                "Normal weight/farming-done e-mail master. A character also needs its own Mail switch. Emergency e-mail has its own independent switch above and shares these saved SMTP settings. If Cart maintenance is inactive, mail uses the carried-weight threshold. "
                + "When Cart maintenance is active, mail waits for BOTH Cart >=99% and carried weight >=50%, after verified Autobattle STOP. No carried-only or Cart-only warning is sent.");
            help.SetToolTip(threshold,
                "Carried-weight warning threshold for Mail-enabled characters whose Cart switch is OFF.");
            help.SetToolTip(rearm, "Re-arm carried-weight warning mail below this percentage.");
            help.SetToolTip(pollSeconds, "Shared weight polling interval.");
            help.SetToolTip(cooldownMinutes, "Cooldown for repeated carried-weight warning mail.");
            help.SetToolTip(smtpPassword, "Leave blank to keep the existing protected SMTP password.");
            help.SetToolTip(test, "Send a test message using the SMTP settings below.");
            help.SetToolTip(clearHold, "Clear manual/completed Weight/Cart holds after you have inspected the character.");
        }

        private Control BuildCartGroup()
        {
            var group = new GroupBox { Text = "Automatic Cart maintenance", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 6 };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            table.Controls.Add(autoCart, 0, 0); table.SetColumnSpan(autoCart, 4);
            Add(table, 1, 0, "Start at weight %", autoThreshold); Add(table, 1, 2, "Re-arm below %", autoRearm);
            var categories = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            categories.Controls.Add(transferUse); categories.Controls.Add(transferEquip); categories.Controls.Add(transferEtc);
            table.Controls.Add(new Label { Text = "Move categories", AutoSize = true, Margin = new Padding(3, 8, 6, 0) }, 0, 2);
            table.Controls.Add(categories, 1, 2);
            var categoryHelp = new Label { AutoSize = true, Text = "ⓘ", Cursor = Cursors.Help, Margin = new Padding(6, 8, 0, 0) };
            help.SetToolTip(categoryHelp,
                "Equip is optional/off by default. Favorite is not processed because it can overlap the real Use/Equip/Etc categories.");
            table.Controls.Add(categoryHelp, 2, 2);
            Add(table, 3, 0, "Autobattle STOP", autobattleStopHotkey); Add(table, 3, 2, "Inventory hotkey", inventoryHotkey);
            Add(table, 4, 0, "Cart hotkey", cartHotkey);
            table.Controls.Add(closeWhenComplete, 0, 5); table.SetColumnSpan(closeWhenComplete, 4);
            group.Controls.Add(table); return group;
        }

        private Control BuildMailGroup()
        {
            var group = new GroupBox { Text = "E-mail notifications", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(10) };
            var settings = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 8 };
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 330));
            settings.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180)); settings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            settings.Controls.Add(enabled, 0, 0); settings.SetColumnSpan(enabled, 4);
            Add(settings, 1, 0, "Warn at weight %", threshold); Add(settings, 1, 2, "Re-arm below %", rearm);
            Add(settings, 2, 0, "Poll every (sec)", pollSeconds); Add(settings, 2, 2, "Cooldown (min)", cooldownMinutes);
            Add(settings, 3, 0, "SMTP host", smtpHost);
            settings.Controls.Add(new Label { Text = "SMTP port", AutoSize = true, Margin = new Padding(3, 8, 6, 0) }, 2, 3);
            var smtpTransport = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, Margin = Padding.Empty };
            smtpTransport.Controls.Add(smtpPort); smtpTransport.Controls.Add(useSsl); settings.Controls.Add(smtpTransport, 3, 3);
            Add(settings, 4, 0, "SMTP username", smtpUser); Add(settings, 4, 2, "SMTP password", smtpPassword);
            Add(settings, 5, 0, "From e-mail", fromAddress); Add(settings, 5, 2, "Recipient e-mail", toAddress);
            Add(settings, 6, 0, "Subject prefix", subjectPrefix);
            var mailHelp = new Label { AutoSize = true, Text = "ⓘ", Cursor = Cursors.Help, Margin = new Padding(6, 8, 0, 0) };
            help.SetToolTip(mailHelp,
                "Per-character Mail behavior: Cart OFF = carried-weight warning at the configured threshold. "
                + "Cart ON = one DONE notification only when Cart>=99% AND carried>=50%, after verified STOP. Cart-full alone does not notify.");
            settings.Controls.Add(mailHelp, 2, 6);
            group.Controls.Add(settings); return group;
        }

        private void LoadSettings()
        {
            loaded = service.Settings;
            autoCart.Checked = loaded.AutoCartEnabled;
            closeWhenComplete.Checked = loaded.CloseClientWhenFarmingComplete;
            autoThreshold.Value = Clamp(autoThreshold, loaded.AutoCartThresholdPercent); autoRearm.Value = Clamp(autoRearm, loaded.AutoCartRearmPercent);
            transferUse.Checked = loaded.TransferUseItems; transferEquip.Checked = loaded.TransferEquipItems; transferEtc.Checked = loaded.TransferEtcItems;
            autobattleStopKey = loaded.AutobattleStopKey; autobattleStopCtrl = loaded.AutobattleStopCtrl;
            autobattleStopAlt = loaded.AutobattleStopAlt; autobattleStopShift = loaded.AutobattleStopShift;
            inventoryKey = loaded.InventoryKey; inventoryCtrl = loaded.InventoryCtrl; inventoryAlt = loaded.InventoryAlt; inventoryShift = loaded.InventoryShift;
            cartKey = loaded.CartKey; cartCtrl = loaded.CartCtrl; cartAlt = loaded.CartAlt; cartShift = loaded.CartShift; UpdateHotkeys();
            enabled.Checked = loaded.Enabled; threshold.Value = Clamp(threshold, loaded.ThresholdPercent); rearm.Value = Clamp(rearm, loaded.RearmPercent);
            pollSeconds.Value = Clamp(pollSeconds, loaded.PollSeconds); cooldownMinutes.Value = Clamp(cooldownMinutes, loaded.CooldownMinutes);
            smtpHost.Text = loaded.SmtpHost ?? ""; smtpPort.Value = Clamp(smtpPort, loaded.SmtpPort);
            useSsl.Checked = loaded.UseSsl; smtpUser.Text = loaded.SmtpUser ?? ""; smtpPassword.Clear();
            fromAddress.Text = loaded.FromAddress ?? ""; toAddress.Text = loaded.ToAddress ?? ""; subjectPrefix.Text = loaded.SubjectPrefix ?? "";
        }

        private VanillaWeightAlertSettings ReadSettings()
        {
            var value = loaded == null ? new VanillaWeightAlertSettings() : loaded.Clone();
            value.AutoCartEnabled = autoCart.Checked; value.AutoCartThresholdPercent = autoThreshold.Value; value.AutoCartRearmPercent = autoRearm.Value;
            value.CloseClientWhenFarmingComplete = closeWhenComplete.Checked;
            value.TransferUseItems = transferUse.Checked; value.TransferEquipItems = transferEquip.Checked; value.TransferEtcItems = transferEtc.Checked;
            value.AutobattleStopKey = autobattleStopKey; value.AutobattleStopCtrl = autobattleStopCtrl;
            value.AutobattleStopAlt = autobattleStopAlt; value.AutobattleStopShift = autobattleStopShift;
            value.InventoryKey = inventoryKey; value.InventoryCtrl = inventoryCtrl; value.InventoryAlt = inventoryAlt; value.InventoryShift = inventoryShift;
            value.CartKey = cartKey; value.CartCtrl = cartCtrl; value.CartAlt = cartAlt; value.CartShift = cartShift;
            value.Enabled = enabled.Checked; value.ThresholdPercent = threshold.Value; value.RearmPercent = rearm.Value;
            value.PollSeconds = (int)pollSeconds.Value; value.CooldownMinutes = (int)cooldownMinutes.Value;
            value.SmtpHost = smtpHost.Text.Trim(); value.SmtpPort = (int)smtpPort.Value; value.UseSsl = useSsl.Checked;
            value.SmtpUser = smtpUser.Text.Trim(); value.FromAddress = fromAddress.Text.Trim(); value.ToAddress = toAddress.Text.Trim();
            value.SubjectPrefix = subjectPrefix.Text.Trim();
            if (!string.IsNullOrEmpty(smtpPassword.Text)) value.ProtectedSmtpPassword = service.Store.ProtectPassword(smtpPassword.Text);
            return value;
        }

        private void SaveSettings()
        {
            VanillaWeightAlertSettings value = ReadSettings(); value.Validate(false); if (value.Enabled) value.Validate(true);
            service.ApplySettings(value, true); loaded = service.Settings; smtpPassword.Clear();
            bool milestoneMail = VanillaWeightAlertService.MilestoneMailConfigured(value);
            status.Text = milestoneMail ? "Saved. SMTP ready." : "Saved. SMTP not configured.";
        }

        private enum HotkeyTarget { AutobattleStop, Inventory, Cart }

        private void CaptureHotkey(KeyEventArgs e, HotkeyTarget target)
        {
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
            if (target == HotkeyTarget.AutobattleStop)
            {
                autobattleStopKey = (int)e.KeyCode; autobattleStopCtrl = e.Control;
                autobattleStopAlt = e.Alt; autobattleStopShift = e.Shift;
            }
            else if (target == HotkeyTarget.Inventory)
            {
                inventoryKey = (int)e.KeyCode; inventoryCtrl = e.Control; inventoryAlt = e.Alt; inventoryShift = e.Shift;
            }
            else
            {
                cartKey = (int)e.KeyCode; cartCtrl = e.Control; cartAlt = e.Alt; cartShift = e.Shift;
            }
            UpdateHotkeys(); e.SuppressKeyPress = e.Handled = true;
        }
        private void UpdateHotkeys()
        {
            autobattleStopHotkey.Text = HotkeyText(autobattleStopCtrl, autobattleStopAlt, autobattleStopShift, autobattleStopKey);
            inventoryHotkey.Text = HotkeyText(inventoryCtrl, inventoryAlt, inventoryShift, inventoryKey);
            cartHotkey.Text = HotkeyText(cartCtrl, cartAlt, cartShift, cartKey);
        }
        private static string HotkeyText(bool ctrl, bool alt, bool shift, int key)
        {
            var parts = new List<string>();
            if (ctrl) parts.Add("Ctrl"); if (alt) parts.Add("Alt"); if (shift) parts.Add("Shift");
            Keys parsed = (Keys)key;
            int code = (int)parsed;
            parts.Add(code >= (int)Keys.D0 && code <= (int)Keys.D9
                ? (code - (int)Keys.D0).ToString(CultureInfo.InvariantCulture)
                : parsed.ToString());
            return string.Join("+", parts);
        }

        private async Task SendTestAsync()
        {
            if (disposed) return; test.Enabled = false; status.Text = "Sending SMTP test…";
            try { VanillaWeightAlertSettings value = ReadSettings(); value.Validate(true); await Task.Run(() => service.SendTest(value)); if (!disposed) status.Text = "Test e-mail sent successfully."; }
            catch (Exception ex) { if (!disposed) status.Text = "Test e-mail failed: " + ex.Message; }
            finally { if (!disposed) test.Enabled = true; }
        }

        private void RefreshStatus()
        {
            if (emergencySavedUntil != DateTime.MinValue && DateTime.UtcNow >= emergencySavedUntil)
            { emergencySaveStatus.Text = ""; emergencySavedUntil = DateTime.MinValue; }
            emergencyStatus.Text = service.EmergencyStatus;
            emergencyStatus.ForeColor = service.HasEmergencyHolds ? Color.Firebrick : Color.DimGray;
            clearEmergency.Enabled = service.HasEmergencyHolds;
            status.Text = service.Status; var observations = service.Latest; live.Rows.Clear();
            foreach (VanillaWeightObservation item in observations.Take(2))
            {
                string weight = item.CurrentWeight.HasValue && item.MaxWeight.HasValue ? item.CurrentWeight + " / " + item.MaxWeight : "Unavailable";
                string percent = item.Percent.HasValue ? item.Percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
                string cart = item.CurrentCartWeight.HasValue && item.MaxCartWeight.HasValue
                    ? item.CurrentCartWeight + " / " + item.MaxCartWeight : "Unavailable";
                string cartPercent = item.CartPercent.HasValue
                    ? item.CartPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "—";
                string verification = item.Verified && item.CartVerified ? "Verified"
                    : item.Error ?? (item.Verified ? "Cart unavailable" : "Weight unavailable");
                live.Rows.Add(item.CharacterName + " (PID " + item.ProcessId + ")", weight, percent, cart, cartPercent, verification);
            }
            if (observations.Count == 0) live.Rows.Add("No Vanilla clients", "—", "—", "—", "—", "Waiting");
        }

        private static void Add(TableLayoutPanel panel, int row, int column, string caption, Control control)
        { panel.Controls.Add(new Label { Text = caption, AutoSize = true, Margin = new Padding(3, 8, 6, 0) }, column, row); panel.Controls.Add(control, column + 1, row); }
        private static NumericUpDown Number(decimal min, decimal max, decimal value, int decimals)
        { return new NumericUpDown { Minimum = min, Maximum = max, Value = value, DecimalPlaces = decimals, Width = 120 }; }
        private static decimal Clamp(NumericUpDown control, decimal value) { return Math.Max(control.Minimum, Math.Min(control.Maximum, value)); }
        private void Guard(System.Action action) { try { action(); } catch (Exception ex) { status.Text = ex.Message; } }
        protected override void Dispose(bool disposing) { if (disposing) { disposed = true; timer.Stop(); timer.Dispose(); help.Dispose(); } base.Dispose(disposing); }
    }
}
