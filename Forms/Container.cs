using System;
using System.Drawing;
using System.Windows.Forms;
using System.Diagnostics;
using System.IO;
using System.Linq;
using _4RTools.Model;
using _4RTools.Model.Vanilla;
using _4RTools.Model.Vanilla.Automation;
using _4RTools.Utils;

namespace _4RTools.Forms
{
    public partial class Container : Form, IObserver
    {

        private Subject subject = new Subject();
        private string currentProfile;
        private readonly bool smokeTest;
        private readonly VanillaAutomationSession vanillaSession;
        private readonly Timer vanillaTimer = new Timer { Interval = 250 };
        private ToggleApplicationStateForm toggleForm;
        private VanillaAutomationForm vanillaExtras;
        private readonly Label vanillaStatus = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
        private bool refreshingClients, profilesReady, closed, validatingClient;
        private string reportedFailure;
        internal bool AutomationEnabled { get { return toggleForm?.IsOn == true || vanillaSession.IsEnabled; } }
        internal bool GameplayAttached { get { return ClientSingleton.GetClient() != null; } }
        internal bool VanillaPollingEnabled { get { return vanillaTimer.Enabled; } }
        internal VanillaClientState VanillaSnapshot { get { return vanillaSession.Snapshot; } }
        internal bool VanillaTabIsFirst { get { return atkDefMode.TabPages.Count > 0 && object.ReferenceEquals(atkDefMode.TabPages[0], tabPageVanilla); } }
        internal string PrimaryTabOrder { get { return string.Join(" | ", atkDefMode.TabPages.Cast<TabPage>().Select(page => page.Text)); } }
        public Container(bool smokeTest = false)
        {
            this.smokeTest = smokeTest;
            vanillaSession = new VanillaAutomationSession(AppDomain.CurrentDomain.BaseDirectory);
            vanillaSession.EnableGuard = () => toggleForm?.IsOn == true
                ? "Switch the original automation OFF before starting extra rules." : null;
            vanillaSession.ConfigurationChanging += () => ForceOff("Extra rule settings changed");
            this.subject.Attach(this);

            InitializeComponent();
            this.Text = "4RTools - Vanilla extension v0.6.0";
            ConfigureVanillaFirstLayout();

            //Container Configuration
            this.IsMdiContainer = true;
            SetBackGroundColorOfMDIForm();

            //Paint Children Forms
            SetToggleApplicationStateWindow();
            SetAutopotWindow();
            SetAutopotYggWindow();
            SetSkillTimerWindow();
            SetProfileWindow();
            SetAHKWindow();
            SetAutobuffSkillWindow();
            SetAutobuffStuffWindow();
            SetDebuffRecoveryWindow();
            SetSongMacroWindow();
            SetATKDEFWindow();
            SetMacroSwitchWindow();
            SetServerWindow();
            SetVanillaWindow();

            vanillaTimer.Tick += (s, e) => PollVanilla();
            if (!smokeTest) vanillaTimer.Start();
        }

        public void addform(TabPage tp, Form f)
        {

            if (!tp.Controls.Contains(f))
            {
                tp.Controls.Add(f);
                f.Dock = DockStyle.Fill;
                f.Show();
                Refresh();
            }
            Refresh();
        }

        private void ConfigureVanillaFirstLayout()
        {
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(1050, 700);
            ClientSize = new Size(1180, 760);
            panelFooter.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            panelFooter.Location = new Point(0, ClientSize.Height - panelFooter.Height);
            panelFooter.Width = ClientSize.Width;
            lblLinkDiscord.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            panelDiscImage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            lblLinkDiscord.Left = panelFooter.Width - lblLinkDiscord.Width - 16;
            panelDiscImage.Left = lblLinkDiscord.Left - panelDiscImage.Width - 8;
            atkDefMode.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            atkDefMode.Size = new Size(ClientSize.Width - 30, panelFooter.Top - atkDefMode.Top - 6);
            vanillaStatus.MaximumSize = new Size(1080, 0);
        }

        private void SetBackGroundColorOfMDIForm()
        {
            foreach (Control ctl in this.Controls)
            {
                if ((ctl) is MdiClient)
                {
                    ctl.BackColor = Color.White;
                }

            }
        }

        private void processCB_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (refreshingClients || (smokeTest && !validatingClient) || this.processCB.SelectedItem == null) return;
            try
            {
                ForceOff("Client changed");
                ClientSingleton.GetClient()?.Dispose();
                ClientSingleton.Instance(null);
                vanillaSession.Disconnect();
                Client client = new Client(this.processCB.SelectedItem.ToString(), vanillaSession);
                ClientSingleton.Instance(client);
                reportedFailure = null;
                subject.Notify(new Utils.Message(Utils.MessageCode.PROCESS_CHANGED, null));
                if (client.IsVanilla) atkDefMode.SelectedTab = tabPageVanilla;
                if (client.IsVanilla && vanillaExtras != null && !vanillaExtras.IsDisposed) vanillaExtras.SelectClient(client.process.Id);
                PollVanilla();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Client selection stopped"); }
        }

        private void Container_Load(object sender, EventArgs e)
        {
            ProfileSingleton.Create("Default");
            this.refreshProcessList();
            this.refreshProfileList();
            this.profileCB.SelectedItem = "Default";
            profilesReady = true;
            if (!smokeTest && processCB.Items.Count == 1) processCB.SelectedIndex = 0;
        }

        public void refreshProfileList()
        {
            this.Invoke((MethodInvoker)delegate ()
            {
                string selected = ProfileSingleton.GetCurrent()?.Name;
                this.profileCB.Items.Clear();
                foreach (string p in Profile.ListAll()) this.profileCB.Items.Add(p);
                if (selected != null && profileCB.Items.Contains(selected)) profileCB.SelectedItem = selected;
                else if (profileCB.Items.Contains("Default")) profileCB.SelectedItem = "Default";
            });
        }

        private void refreshProcessList()
        {
            if (smokeTest && !validatingClient) return;
            string previous = processCB.SelectedItem as string;
            refreshingClients = true;
            try
            {
                this.processCB.Items.Clear();
                foreach (Process p in Process.GetProcesses())
                {
                    using (p)
                    {
                        try
                        {
                            if (p.MainWindowTitle != "" && (Client.IsVanillaProcessName(p.ProcessName) || ClientListSingleton.ExistsByProcessName(p.ProcessName)))
                                this.processCB.Items.Add(string.Format("{0}.exe - {1}", p.ProcessName, p.Id));
                        }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
                if (previous != null && processCB.Items.Contains(previous)) processCB.SelectedItem = previous;
            }
            finally { refreshingClients = false; }
            if (previous != null && processCB.SelectedItem == null)
            {
                ForceOff("Selected client exited");
                ClientSingleton.GetClient()?.Dispose();
                ClientSingleton.Instance(null);
                vanillaSession.Disconnect();
            }
        }

        private void btnRefresh_Click(object sender, EventArgs e)
        {
            this.refreshProcessList();
        }

        protected override void OnClosed(EventArgs e)
        {
            ShutdownApplication();
            base.OnClosed(e);
        }

        private void ShutdownApplication()
        {
            if (closed) return;
            closed = true;
            vanillaTimer.Stop();
            ForceOff("Application closed");
            vanillaExtras?.Close();
            vanillaDiagnostics?.Close();
            ClientSingleton.GetClient()?.Dispose();
            ClientSingleton.Instance(null);
            vanillaSession.Dispose();
            if (!smokeTest) KeyboardHook.Disable();
            vanillaTimer.Dispose();
        }

        private void lblLinkGithub_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(AppConfig.GithubLink);
        }

        private void lblLinkDiscord_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(AppConfig.DiscordLink);
        }

        private void websiteLinkLabel_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Process.Start(AppConfig.Website);
        }

        private void profileCB_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(this.profileCB.Text) && this.profileCB.Text != currentProfile)
            {
                try
                {
                    ForceOff("Profile changed");
                    ProfileSingleton.Load(this.profileCB.Text); //LOAD PROFILE
                    subject.Notify(new Utils.Message(MessageCode.PROFILE_CHANGED, null));
                    currentProfile = this.profileCB.Text.ToString();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[ProfileSingleton.Load] Error Message: {ex.Message}");
                    MessageBox.Show($"Error while loading the new profile. \nPlease get in touch via Discord. \nPlease send this error message to the admin: {ex.Message}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        public void Update(ISubject subject)
        {
            switch ((subject as Subject).Message.code)
            {
                case MessageCode.PROCESS_CHANGED:
                case MessageCode.PROFILE_CHANGED:
                    Client client = ClientSingleton.GetClient();
                    UpdateCharacterName(client);
                    break;
                case MessageCode.TURN_OFF:
                    ClientSingleton.GetClient()?.SetAutomationEnabled(false);
                    vanillaSession.SetEnabled(false);
                    this.profileCB.Enabled = true;
                    this.processCB.Enabled = true;

                    break;
                case MessageCode.TURN_ON:
                    this.profileCB.Enabled = false;
                    this.processCB.Enabled = false;
                    UpdateCharacterName(ClientSingleton.GetClient());
                    break;
                case MessageCode.SERVER_LIST_CHANGED:
                    this.refreshProcessList();
                    break;
                case MessageCode.CLICK_ICON_TRAY:
                    this.Show();
                    this.ShowInTaskbar = true;
                    this.WindowState = FormWindowState.Normal;
                    this.Activate();
                    break;
                case MessageCode.SHUTDOWN_APPLICATION:
                    this.ShutdownApplication();
                    Close();
                    break;
            }
        }

        private void containerResize(object sender, EventArgs e)
        {
            // Deliberately do not hide on minimize. The main product window remains a normal
            // Windows taskbar item; tray-only minimization is not used by the Vanilla product.
            if (this.WindowState == FormWindowState.Minimized) this.ShowInTaskbar = true;
        }

        #region Frames

        private VanillaDiagnosticsForm vanillaDiagnostics;

        private void SetVanillaWindow()
        {
            var page = tabPageVanilla;
            page.Controls.Clear();
            var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12) };
            panel.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(500, 0), Text = "Select Vanilla in Ragnarok Client above. Existing feature tabs use its read-only mappings. Additional rules and diagnostics are available here." });
            panel.Controls.Add(vanillaStatus);
            var extras = new Button { Text = "Smart Teleport and extra rules", AutoSize = true };
            extras.Click += (s, e) =>
            {
                var client = ClientSingleton.GetClient();
                if (client?.IsVanilla != true) { MessageBox.Show(this, "Select Vanilla in Ragnarok Client first."); return; }
                ForceOff("Extra rule settings opened");
                if (vanillaExtras == null || vanillaExtras.IsDisposed)
                {
                    vanillaExtras = new VanillaAutomationForm(vanillaSession, OpenVanillaDiagnostics, hosted: true, ownsSession: false);
                    vanillaExtras.EmergencyStopRequested = () => ForceOff("Emergency stop");
                    vanillaExtras.EmergencyKeyAllowed = key => key != (int)(Keys)Enum.Parse(typeof(Keys), ProfileSingleton.GetCurrent().UserPreferences.toggleStateKey);
                }
                vanillaExtras.SelectClient(client.process.Id);
                vanillaExtras.Show(this);
                vanillaExtras.BringToFront();
            };
            panel.Controls.Add(extras);
            var open = new Button { Text = "Open diagnostics", AutoSize = true };
            open.Click += (sender, args) => OpenVanillaDiagnostics();
            panel.Controls.Add(open);
            page.Controls.Add(panel);
        }

        private void OpenVanillaDiagnostics()
        {
            ForceOff("Diagnostics opened");
            if (vanillaDiagnostics == null || vanillaDiagnostics.IsDisposed) vanillaDiagnostics = new VanillaDiagnosticsForm(subject);
            vanillaDiagnostics.Show(this);
            vanillaDiagnostics.BringToFront();
        }

        private void ForceOff(string reason)
        {
            vanillaSession.SetEnabled(false);
            ClientSingleton.GetClient()?.SetAutomationEnabled(false);
            if (profilesReady) toggleForm.ForceOff(reason);
        }

        private void PollVanilla()
        {
            if (smokeTest && !validatingClient) return;
            vanillaSession.Tick();
            var client = ClientSingleton.GetClient();
            if (client?.IsVanilla != true)
            {
                if (vanillaStatus.Text != "Select a Vanilla client above.") vanillaStatus.Text = "Select a Vanilla client above.";
                return;
            }
            string failure = client.LastFailure;
            if (failure != null && reportedFailure != failure)
            {
                reportedFailure = failure;
                ForceOff(failure);
            }
            if (vanillaSession.Snapshot == null && toggleForm.IsOn) ForceOff("Vanilla observation stopped");
            if (toggleForm.IsOn)
            {
                string unavailable = client.GetEnableError(ProfileSingleton.GetCurrent());
                if (unavailable != null) ForceOff(unavailable);
            }
            UpdateCharacterName(client);
            var snapshot = vanillaSession.Snapshot;
            string status = snapshot == null ? vanillaSession.Status : "Connected read-only to PID " + snapshot.ProcessId
                + Environment.NewLine + "HP: " + snapshot.CurrentHP + " / " + snapshot.MaxHP
                + "    SP: " + snapshot.CurrentSP + " / " + snapshot.MaxSP
                + Environment.NewLine + "Field validation: " + snapshot.CurrentHP.Validation
                + Environment.NewLine + client.VanillaCapabilities;
            if (failure != null) status += Environment.NewLine + failure;
            if (vanillaStatus.Text != status) vanillaStatus.Text = status;
        }

        private void UpdateCharacterName(Client client)
        {
            string value;
            if (client == null || !client.TryReadCharacterName(out value)) value = "Unavailable";
            if (characterName.Text != value) characterName.Text = value;
        }

        internal void SelectClientForValidation(int processId)
        {
            // Only the explicit --original-ui-check path may attach during a smoke-mode launch.
            validatingClient = true;
            try
            {
                refreshProcessList();
                foreach (var item in processCB.Items)
                    if (item.ToString().EndsWith(".exe - " + processId, StringComparison.Ordinal))
                    { processCB.SelectedItem = item; atkDefMode.SelectedTab = tabPageVanilla; return; }
                throw new InvalidOperationException("Requested client is not in the Ragnarok Client list.");
            }
            finally { validatingClient = false; }
        }

        public void SetToggleApplicationStateWindow()
        {
            ToggleApplicationStateForm frm = new ToggleApplicationStateForm(subject, smokeTest, StockEnableError);
            toggleForm = frm;
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.MdiParent = this;
            this.OnOffPanel.Controls.Add(frm);
            frm.Show();
        }

        private string StockEnableError()
        {
            if (vanillaSession.IsEnabled) return "Stop extra rules before starting original automation.";
            if (vanillaExtras != null && !vanillaExtras.IsDisposed
                && vanillaSession.Settings.EmergencyKey == (int)(Keys)Enum.Parse(typeof(Keys), ProfileSingleton.GetCurrent().UserPreferences.toggleStateKey))
                return "Choose different keys for the original ON/OFF toggle and the extra-rules emergency stop.";
            return null;
        }

        public void SetAutopotWindow()
        {
            AutopotForm frm = new AutopotForm(subject, false);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageAutopot, frm);
        }
        public void SetAutopotYggWindow()
        {
            AutopotForm frm = new AutopotForm(subject, true);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageYggAutopot, frm);
        }

        public void SetSkillTimerWindow()
        {
            SkillTimerForm frm = new SkillTimerForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabSkillTimer, frm);
        }

        public void SetProfileWindow()
        {
            ProfileForm frm = new ProfileForm(this);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageProfiles, frm);
        }

        public void SetServerWindow()
        {
            ServersForm frm = new ServersForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageServer, frm);
        }

        public void SetAHKWindow()
        {
            AHKForm frm = new AHKForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageSpammer, frm);
        }

        public void SetAutobuffSkillWindow()
        {
            SkillAutoBuffForm frm = new SkillAutoBuffForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            addform(this.tabPageAutobuffSkill, frm);
            frm.Show();
        }

        public void SetAutobuffStuffWindow()
        {
            StuffAutoBuffForm frm = new StuffAutoBuffForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabPageAutobuffStuff, frm);
        }

        public void SetDebuffRecoveryWindow()
        {
            DebuffRecoveryForm frm = new DebuffRecoveryForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            frm.Show();
            addform(this.tabDebuffRecovery, frm);
        }

        public void SetSongMacroWindow()
        {
            MacroSongForm frm = new MacroSongForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            addform(this.tabPageMacroSongs, frm);
            frm.Show();
        }

        public void SetATKDEFWindow()
        {
            ATKDEFForm frm = new ATKDEFForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            addform(this.atkDef, frm);
            frm.Show();
        }

        public void SetMacroSwitchWindow()
        {
            MacroSwitchForm frm = new MacroSwitchForm(subject);
            frm.FormBorderStyle = FormBorderStyle.None;
            frm.Location = new Point(0, 65);
            frm.MdiParent = this;
            addform(this.tabMacroSwitch, frm);
            frm.Show();
        }
        #endregion
    }
}
