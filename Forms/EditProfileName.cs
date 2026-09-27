using _4RTools.Model;
using _4RTools.Utils;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace _4RTools.Forms
{
    public partial class EditProfileName : Form
    {
        private string savedName;
        private readonly Label saveStatus = new Label { AutoSize = false, Location = new Point(16, 82), Size = new Size(312, 35) };
        public bool Changed { get; private set; }
        public string ProfileName { get { return savedName; } }
        public EditProfileName()
        {
            InitializeComponent();
            Controls.Add(saveStatus);
            ClientSize = new Size(ClientSize.Width, 124);
            txtProfileName.Validated += (sender, args) => CommitName();
            txtProfileName.KeyDown += (sender, args) =>
            {
                if (args.KeyCode != Keys.Enter) return;
                args.Handled = true;
                args.SuppressKeyPress = true;
                CommitName();
            };
            FormClosing += (sender, args) => { if (!CommitName()) args.Cancel = true; };
        }

        public void SetProfileName(string name)
        {
            using (FormUtils.BeginLoading(this))
            {
                savedName = name;
                txtProfileName.Text = name;
                Changed = false;
                saveStatus.Text = "";
            }
        }

        private void onTextChange(object sender, EventArgs e)
        {
            if (!FormUtils.IsLoading(this)) saveStatus.Text = txtProfileName.Text == savedName ? "" : "Editing…";
        }

        private bool CommitName()
        {
            if (FormUtils.IsLoading(this) || savedName == null || txtProfileName.Text == savedName) return true;
            try
            {
                ProfileSingleton.Rename(savedName, txtProfileName.Text);
                savedName = txtProfileName.Text;
                Changed = true;
                saveStatus.ForeColor = Color.DarkGreen;
                saveStatus.Text = "Saved";
                return true;
            }
            catch (Exception ex)
            {
                saveStatus.ForeColor = Color.Firebrick;
                saveStatus.Text = "Not saved: " + ex.Message;
                return false;
            }
        }

        private void btnClose_Click(object sender, EventArgs e) { Close(); }
    }
}
