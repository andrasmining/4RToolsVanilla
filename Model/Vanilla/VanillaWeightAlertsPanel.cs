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
        private readonly CheckBox autoCart = new CheckBox { Text = "Automatically move selected inventory categories to Cart", AutoSize = true };
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

        private readonly CheckBox enabled = new CheckBox { Text = "Enable carried-weight warning e-mail", AutoSize = true };
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
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(14), ColumnCount = 1, RowCount = 6 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize)); root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 10F, FontStyle.Bold), Text = "Weight / Cart management" }, 0, 0);
            root.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(1150, 0), ForeColor = Color.DimGray, Margin = new Padding(0, 5, 0, 10),
                Text = "Weight decisions use verified read-only carried and Cart weight only. Cart maintenance uses ordinary UI hotkeys, visual slot/category detection and slow drag/drop; it never reads or writes inventory memory. A transfer gets up to 3 slow attempts; pure transfer non-progress resumes Autobattle and retries about 60s later instead of holding the character. At 95% Cart weight it switches to capacity-safe precision filling: Mastela Fruit=3 and Peco Feather=1, using a positively detected quantity dialog and verified Cart-weight progress. Exact Cart 100% remains the Cart-full mail milestone; farming is DONE at Cart >=99% plus carried weight >=50%, then Autobattle is intentionally stopped."
            }, 0, 1);

            root.Controls.Add(BuildCartGroup(), 0, 2);
            root.Controls.Add(BuildMailGroup(), 0, 3);
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 8, 0, 0) };
            buttons.Controls.Add(save); buttons.Controls.Add(test); buttons.Controls.Add(clearHold); buttons.Controls.Add(status);
            root.Controls.Add(buttons, 0, 4);

            live.Columns.Add("Client", "Client"); live.Columns.Add("Weight", "Weight"); live.Columns.Add("Percent", "%");
            live.Columns.Add("Cart", "Cart"); live.Columns.Add("CartPercent", "Cart %"); live.Columns.Add("Verification", "State");
            var liveHost = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            liveHost.RowStyles.Add(new RowStyle(SizeType.AutoSize)); liveHost.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            liveHost.Controls.Add(new Label { AutoSize = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 10, 0, 4), Text = "Live verified weight" }, 0, 0);
            liveHost.Controls.Add(live, 0, 1); root.Controls.Add(liveHost, 0, 5);
            Controls.Add(root);
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
            var hint = new Label { AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(500, 0), Text = "Equip is optional/off by default. Favorite is not processed because it can overlap the real Use/Equip/Etc categories." };
            table.Controls.Add(hint, 2, 2); table.SetColumnSpan(hint, 2);
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
            settings.Controls.Add(new Label { AutoSize = true, ForeColor = Color.DimGray, Text = "Leave password blank to keep the already saved protected password." }, 2, 6);
            var milestoneHint = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(1100, 0),
                Text = "Cart 100% and DONE milestone e-mails use these SMTP settings whenever they are configured; the checkbox above controls only carried-weight warning e-mails."
            };
            settings.Controls.Add(milestoneHint, 0, 7); settings.SetColumnSpan(milestoneHint, 4);
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
            status.Text = value.AutoCartEnabled
                ? "Saved. Automatic Cart maintenance is armed at " + value.AutoCartThresholdPercent.ToString("0.#")
                    + "%. Milestone mail " + (milestoneMail ? "is configured." : "is not configured until valid SMTP/addresses are saved.")
                : value.Enabled ? "Saved. Carried-weight warning e-mail is enabled."
                : milestoneMail ? "Saved. Cart-full/DONE milestone mail is configured."
                : "Saved. Automatic actions and e-mail notifications are disabled.";
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
        protected override void Dispose(bool disposing) { if (disposing) { disposed = true; timer.Stop(); timer.Dispose(); } base.Dispose(disposing); }
    }
}
