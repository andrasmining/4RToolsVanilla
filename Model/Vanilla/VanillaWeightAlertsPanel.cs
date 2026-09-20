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

        private readonly CheckBox enabled = new CheckBox { Text = "E-mail master", AutoSize = true };
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
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(1150, 0), ForeColor = Color.DimGray };
        private readonly DataGridView live = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
            RowHeadersVisible = false, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        };
        private readonly Timer timer = new Timer { Interval = 1000 };
        private readonly ToolTip help = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };
        private VanillaWeightAlertSettings loaded;
        private bool disposed;

        public VanillaWeightAlertsPanel(VanillaWeightAlertService service)
        {
            this.service = service ?? throw new ArgumentNullException(nameof(service));
            Dock = DockStyle.Fill; BackColor = Color.White; AutoScroll = true;
            BuildLayout(); LoadSettings();
            save.Click += (s, e) => Guard(SaveSettings);
            test.Click += async (s, e) => await SendTestAsync();
            clearHold.Click += (s, e) => service.ClearManualHolds();
            autobattleStopHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.AutobattleStop);
            inventoryHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.Inventory);
            cartHotkey.KeyDown += (s, e) => CaptureHotkey(e, HotkeyTarget.Cart);
            timer.Tick += (s, e) => RefreshStatus();
            timer.Start(); RefreshStatus();
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 5 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var title = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            title.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Text = "Weight / Cart management" });
            var info = new Label { AutoSize = true, Text = "ⓘ", Cursor = Cursors.Help, Margin = new Padding(8, 2, 0, 0) };
            help.SetToolTip(info,
                "Character-level Cart and Mail switches are edited on Recovery & relog. "
                + "Cart mode uses verified read-only carried/Cart weight plus ordinary UI input. "
                + "STOP must be verified stationary, HP is guarded while stopped, precision filling starts at 75%, "
                + "Mastela=3, Peco Feather=1, and transient transfer failures retry later.");
            title.Controls.Add(info);
            root.Controls.Add(title, 0, 0);

            root.Controls.Add(BuildCartGroup(), 0, 1);
            root.Controls.Add(BuildMailGroup(), 0, 2);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(save); buttons.Controls.Add(test); buttons.Controls.Add(clearHold); buttons.Controls.Add(status);
            root.Controls.Add(buttons, 0, 3);

            live.Columns.Add("Client", "Client"); live.Columns.Add("Weight", "Weight"); live.Columns.Add("Percent", "%");
            live.Columns.Add("Cart", "Cart"); live.Columns.Add("CartPercent", "Cart %"); live.Columns.Add("Verification", "State");
            var liveHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            liveHost.RowStyles.Add(new RowStyle(SizeType.AutoSize)); liveHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            liveHost.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4), Text = "Live verified weight" }, 0, 0);
            liveHost.Controls.Add(live, 0, 1); root.Controls.Add(liveHost, 0, 4);
            Controls.Add(root);

            ConfigureHelp();
        }

        private void ConfigureHelp()
        {
            help.SetToolTip(autoCart,
                "Global Cart master. A character also needs its own Cart switch enabled in Recovery & relog.");
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
                "Global e-mail master. A character also needs its own Mail switch. If Cart maintenance is inactive, mail uses the carried-weight threshold. "
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
            var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 4, RowCount = 5 };
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
