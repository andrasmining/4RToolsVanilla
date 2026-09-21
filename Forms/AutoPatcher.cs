using System;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace _4RTools.Forms
{
    // Retained for legacy callers; package discovery and verification are shared
    // with the Vanilla workspace instead of the obsolete first-asset RAR updater.
    public partial class AutoPatcher : Form
    {
        public AutoPatcher()
        {
            InitializeComponent();
            Shown += (s, e) => StartAutopatcher();
        }

        private async void StartAutopatcher()
        {
            bool restarting = false;
            try
            {
                VanillaUpdateInfo update = await VanillaUpdater.CheckAsync();
                if (update != null && MessageBox.Show(this, "Download verified update " + update.TagName + " and restart?",
                    "4RTools Vanilla update", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    string payload = await VanillaUpdater.DownloadAndStageAsync(update);
                    VanillaUpdater.BeginApplyAndRestart(payload);
                    restarting = true;
                    Application.Exit();
                }
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Update unavailable", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally
            {
                if (!restarting) { new ClientUpdaterForm().Show(); Hide(); }
            }
        }
    }
}
