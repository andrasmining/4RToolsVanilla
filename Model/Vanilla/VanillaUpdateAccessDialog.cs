using System;
using System.Drawing;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaUpdateAccessDialog : Form
    {
        internal VanillaUpdateAccessDialog()
        {
            Text = "Private update access";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(490, 220);
            var panel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4,
                AutoScroll = true
            };
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(460, 0), Margin = new Padding(0, 0, 0, 9),
                Text = "GitHub token for andrasmining/4RToolsVanilla with Contents: Read permission. Stored for this Windows user and PC."
            });
            var token = new TextBox { UseSystemPasswordChar = true, MaxLength = 4096, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
            panel.Controls.Add(token);
            var status = new Label
            {
                AutoSize = true, MaximumSize = new Size(460, 0),
                Text = VanillaUpdateAccess.HasSavedToken ? "A token is saved. Enter a replacement or clear it." : "No token saved. Existing GitHub CLI sign-in or GH_TOKEN can also provide access."
            };
            panel.Controls.Add(status);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "CANCEL", AutoSize = true, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "SAVE", AutoSize = true };
            var clear = new Button { Text = "CLEAR SAVED", AutoSize = true };
            buttons.Controls.Add(cancel); buttons.Controls.Add(save); buttons.Controls.Add(clear);
            panel.Controls.Add(buttons);
            Controls.Add(panel);
            AcceptButton = save; CancelButton = cancel;
            save.Click += (s, e) =>
            {
                try { VanillaUpdateAccess.SaveToken(token.Text); token.Clear(); DialogResult = DialogResult.OK; Close(); }
                catch (Exception) { status.Text = "Token could not be saved. Check the token and access to the data folder."; }
            };
            clear.Click += (s, e) =>
            {
                try { VanillaUpdateAccess.ClearToken(); token.Clear(); status.Text = "Saved token cleared. Existing GitHub CLI sign-in or GH_TOKEN may still provide access."; }
                catch (Exception) { status.Text = "Saved token could not be cleared. Check access to the data folder."; }
            };
            FormClosed += (s, e) => token.Clear();
        }
    }
}
