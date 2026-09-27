using System;
using System.Drawing;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    internal sealed class VanillaUpdateAccessDialog : Form
    {
        private readonly TextBox token = new TextBox { UseSystemPasswordChar = true, MaxLength = 4096, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 8) };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(460, 0) };
        private readonly Button discard = new Button { Text = "DISCARD ENTRY", AutoSize = true, Visible = false, CausesValidation = false };
        private bool edited;
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
            panel.Controls.Add(token);
            status.Text = VanillaUpdateAccess.HasSavedToken ? "A token is saved. Replacements save automatically on leaving the field or pressing Enter."
                : "No token saved. Entry saves automatically on leaving the field or pressing Enter. Existing CLI sign-in or GH_TOKEN also works.";
            panel.Controls.Add(status);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "CLOSE", AutoSize = true, DialogResult = DialogResult.Cancel };
            var clear = new Button { Text = "CLEAR ACCESS", AutoSize = true, CausesValidation = false };
            buttons.Controls.Add(close); buttons.Controls.Add(discard); buttons.Controls.Add(clear);
            panel.Controls.Add(buttons);
            Controls.Add(panel);
            CancelButton = close;
            token.TextChanged += (s, e) => edited = true;
            token.Validated += (s, e) => SaveAutomatically();
            token.KeyDown += (s, e) =>
            {
                if (e.KeyCode != Keys.Enter) return;
                SaveAutomatically(); e.Handled = e.SuppressKeyPress = true;
            };
            clear.Click += (s, e) =>
            {
                try { VanillaUpdateAccess.ClearToken(); token.Clear(); edited = false; discard.Visible = false; status.ForeColor = Color.DarkGreen; status.Text = "Saved token cleared. Existing GitHub CLI sign-in or GH_TOKEN may still provide access."; }
                catch (Exception) { status.ForeColor = Color.Firebrick; status.Text = "Saved token could not be cleared. Check access to the data folder."; }
            };
            discard.Click += (s, e) => { token.Clear(); edited = false; Close(); };
            FormClosing += (s, e) => { if (!SaveAutomatically()) e.Cancel = true; };
            FormClosed += (s, e) => token.Clear();
        }

        private bool SaveAutomatically()
        {
            if (!edited) return true;
            // An empty replacement is not an instruction to erase existing access.
            if (token.Text.Length == 0) { edited = false; return true; }
            try
            {
                VanillaUpdateAccess.SaveToken(token.Text);
                token.Clear(); edited = false;
                discard.Visible = false;
                status.ForeColor = Color.DarkGreen; status.Text = "Saved for this Windows user and PC.";
                return true;
            }
            catch (Exception)
            {
                status.ForeColor = Color.Firebrick;
                status.Text = "Not saved. Check the token and access to the data folder. Previous access is unchanged.";
                discard.Visible = true;
                return false;
            }
        }
    }
}
