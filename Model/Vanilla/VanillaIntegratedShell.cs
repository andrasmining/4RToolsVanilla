using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model;
using _4RTools.Model.Vanilla;

namespace _4RTools.Forms
{
    public partial class Container
    {
        private VanillaReconnectSupervisor integratedReconnectSupervisor;
        private VanillaReconnectForm integratedReconnectView;
        private TabControl vanillaWorkspace;
        private TabPage vanillaRecoveryPage, vanillaTemporaryPage, vanillaDiagnosticsPage, vanillaAboutPage;
        private TabControl primaryWorkspace;
        private TabPage primaryVanillaPage, primaryLegacyPage;
        private Panel legacySurface;
        private readonly Label integratedUpdateStatus = new Label { AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(10, 8, 4, 0) };
        private bool integratedVanillaReady, updateCheckRunning;
        private VanillaFleetMonitor integratedFleetMonitor;
        private VanillaFleetDashboardPanel integratedFleetDashboard;
        private VanillaTemporaryActionsPanel integratedTemporaryActions;
        private VanillaSmartTeleportService integratedSmartTeleport;
        internal bool FleetPollingEnabled { get { return integratedFleetDashboard?.IsPolling == true; } }
        internal int FleetPollCount { get { return integratedFleetMonitor?.PollCount ?? 0; } }
        internal bool RecoveryRunning { get { return integratedReconnectSupervisor?.IsRunning == true; } }
        internal bool UpdateCheckRunning { get { return updateCheckRunning; } }
        internal bool SmartTeleportPollingEnabled { get { return integratedSmartTeleport?.IsRunning == true; } }

        internal void AssertSmokeBackgroundServicesInactive()
        {
            if (!smokeTest || VanillaPollingEnabled || FleetPollingEnabled || FleetPollCount != 0
                || RecoveryRunning || WeightAlertsRunning || SmartTeleportPollingEnabled || UpdateCheckRunning || AutomationEnabled)
                throw new InvalidOperationException("Smoke startup must keep live polling, recovery, alerts, updates, and automation inactive.");
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (integratedVanillaReady) return;
            integratedVanillaReady = true;
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            Text = "4RTools Vanilla " + VanillaUpdater.CurrentVersionText;
            ExpandForIntegratedWorkspace();
            BuildPrimaryWorkspaceShell();
            BuildIntegratedVanillaWorkspace();
            if (!smokeTest) BeginInvoke((MethodInvoker)(() => CheckForUpdates(true)));
        }

        private void ExpandForIntegratedWorkspace()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            int width = Math.Max(1000, area.Width - 16);
            int height = Math.Max(680, area.Height - 16);
            MinimumSize = new Size(Math.Min(1000, width), Math.Min(680, height));
            Size = new Size(Math.Min(1600, width), Math.Min(1000, height));
            StartPosition = FormStartPosition.CenterScreen;
            if (!smokeTest) WindowState = FormWindowState.Maximized;
        }

        private void BuildPrimaryWorkspaceShell()
        {
            if (primaryWorkspace != null) return;
            Control[] legacyControls = Controls.Cast<Control>().Where(control => !(control is MdiClient)).ToArray();
            legacySurface = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = Color.White,
                AutoScrollMinSize = new Size(920, 650)
            };
            foreach (Control control in legacyControls)
            {
                Controls.Remove(control);
                legacySurface.Controls.Add(control);
            }

            primaryWorkspace = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(14, 6),
                Font = new Font("Segoe UI", 9.5F, FontStyle.Bold)
            };
            primaryVanillaPage = new TabPage("Vanilla") { Padding = new Padding(4), UseVisualStyleBackColor = true, AutoScroll = false };
            primaryLegacyPage = new TabPage("Original 4RTools") { Padding = new Padding(4), UseVisualStyleBackColor = true, AutoScroll = true };
            primaryLegacyPage.Controls.Add(legacySurface);
            primaryWorkspace.TabPages.Add(primaryVanillaPage);
            primaryWorkspace.TabPages.Add(primaryLegacyPage);
            primaryWorkspace.SelectedTab = primaryVanillaPage;
            Controls.Add(primaryWorkspace);
            primaryWorkspace.BringToFront();
        }

        private void BuildIntegratedVanillaWorkspace()
        {
            integratedReconnectSupervisor = new VanillaReconnectSupervisor(VanillaAppData.RootDirectory);
            integratedFleetMonitor = new VanillaFleetMonitor(AppDomain.CurrentDomain.BaseDirectory);
            integratedReconnectSupervisor.SetPositionSource(integratedFleetMonitor.LatestPosition, integratedFleetMonitor.ConfirmClientExited);
            integratedReconnectSupervisor.SetCharacterSource(integratedFleetMonitor.LatestCharacters);
            integratedSmartTeleport = new VanillaSmartTeleportService(integratedFleetMonitor, integratedReconnectSupervisor);
            if (!smokeTest) integratedSmartTeleport.Start();

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(5),
                Margin = Padding.Empty,
                RowCount = 3,
                ColumnCount = 1
            };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 145));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = new Padding(0, 0, 0, 3)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var headerActions = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = true,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            headerActions.Controls.Add(new Label
            {
                Text = "Vanilla workspace",
                Font = new Font("Segoe UI", 10.5F, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(3, 7, 12, 0)
            });
            AddIntegratedButton(headerActions, "OPEN DATA FOLDER", OpenDataFolder);

            var headerStatus = new FlowLayoutPanel
            {
                AutoSize = true,
                WrapContents = false,
                FlowDirection = FlowDirection.LeftToRight,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            AddIntegratedButton(headerStatus, "CHECK FOR UPDATES", () => CheckForUpdates(false));
            integratedUpdateStatus.Text = "Version " + VanillaUpdater.CurrentVersionText;
            headerStatus.Controls.Add(integratedUpdateStatus);

            header.Controls.Add(headerActions, 0, 0);
            header.Controls.Add(headerStatus, 1, 0);
            root.Controls.Add(header, 0, 0);

            integratedFleetDashboard = new VanillaFleetDashboardPanel(integratedFleetMonitor, observeClients: !smokeTest)
            {
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 2)
            };
            root.Controls.Add(integratedFleetDashboard, 0, 1);

            vanillaWorkspace = new TabControl
            {
                Dock = DockStyle.Fill,
                Padding = new Point(12, 5),
                Margin = Padding.Empty,
                Font = new Font("Segoe UI", 9F, FontStyle.Regular),
                Enabled = !smokeTest
            };
            vanillaRecoveryPage = WorkspacePage("Recovery & relog");
            // Recovery owns scrolling inside its responsive root. A second scrolling parent
            // can retain an old virtual width and prevent the embedded form from shrinking.
            vanillaRecoveryPage.AutoScroll = false;
            vanillaTemporaryPage = WorkspacePage("Temporary actions");
            vanillaDiagnosticsPage = WorkspacePage("Diagnostics");
            vanillaAboutPage = WorkspacePage("Data & updates");
            vanillaWorkspace.TabPages.AddRange(new[] { vanillaRecoveryPage, vanillaTemporaryPage, vanillaDiagnosticsPage, vanillaAboutPage });
            vanillaWorkspace.SelectedIndexChanged += (s, e) =>
            {
                if (vanillaWorkspace.SelectedTab == vanillaTemporaryPage) EnsureTemporaryActionsEmbedded();
                if (vanillaWorkspace.SelectedTab == vanillaDiagnosticsPage) EnsureDiagnosticsEmbedded();
            };

            integratedReconnectView = new VanillaReconnectForm(integratedReconnectSupervisor, observeClients: !smokeTest);
            integratedReconnectView.SmartTeleportTestRequested = accountId => integratedSmartTeleport.RunNow(accountId);
            integratedReconnectView.PrepareForEmbeddedHost();
            vanillaRecoveryPage.Controls.Add(integratedReconnectView);
            integratedReconnectView.Show();

            BuildAboutPage();
            root.Controls.Add(vanillaWorkspace, 0, 2);
            primaryVanillaPage.Controls.Add(root);
            vanillaWorkspace.SelectedTab = vanillaRecoveryPage;

            if (!smokeTest && integratedReconnectSupervisor.Settings.StartWith4RTools && !integratedReconnectSupervisor.IsRunning)
                integratedReconnectSupervisor.Start();
        }

        private static TabPage WorkspacePage(string text)
        {
            return new TabPage(text)
            {
                Padding = new Padding(4),
                UseVisualStyleBackColor = true,
                AutoScroll = true
            };
        }

        private void EnsureTemporaryActionsEmbedded()
        {
            if (smokeTest) return;
            if (integratedTemporaryActions != null && !integratedTemporaryActions.IsDisposed) return;
            integratedTemporaryActions = new VanillaTemporaryActionsPanel(AppDomain.CurrentDomain.BaseDirectory, integratedFleetMonitor, integratedReconnectSupervisor)
            {
                Dock = DockStyle.Fill,
                AutoScroll = true
            };
            vanillaTemporaryPage.Controls.Add(integratedTemporaryActions);
            integratedTemporaryActions.BringToFront();
        }

        private void EnsureDiagnosticsEmbedded()
        {
            if (smokeTest) return;
            if (vanillaDiagnostics != null && !vanillaDiagnostics.IsDisposed) return;
            ForceOff("Diagnostics opened");
            vanillaDiagnostics = new VanillaDiagnosticsForm(subject);
            PrepareEmbeddedForm(vanillaDiagnostics);
            vanillaDiagnosticsPage.Controls.Add(vanillaDiagnostics);
            vanillaDiagnostics.Show();
        }

        private static void PrepareEmbeddedForm(Form form)
        {
            form.TopLevel = false;
            form.FormBorderStyle = FormBorderStyle.None;
            form.MinimumSize = Size.Empty;
            // Embedded forms are controls, not independently sized desktop windows. Explicit
            // bounds prevent Framework's desktop MaxWindowTrackSize from capping their width.
            form.MaximumSize = new Size(32767, 32767);
            form.AutoSize = false;
            form.Dock = DockStyle.Fill;
            form.Margin = Padding.Empty;
            form.ShowInTaskbar = false;
            form.AutoScroll = true;
        }

        private void BuildAboutPage()
        {
            var panel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(6)
            };
            panel.Controls.Add(new Label { AutoSize = true, Font = new Font(Font, FontStyle.Bold), Text = "Persistent data" });
            panel.Controls.Add(PathLabel("Data root", VanillaAppData.RootDirectory));
            panel.Controls.Add(PathLabel("Original 4R profiles", VanillaAppData.StockProfilesDirectory));
            panel.Controls.Add(PathLabel("Vanilla rule profiles", VanillaAppData.VanillaProfilesDirectory));
            panel.Controls.Add(PathLabel("Recovery accounts/settings", VanillaAppData.ReconnectSettingsPath));
            panel.Controls.Add(PathLabel("Logs", VanillaAppData.LogsDirectory));
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(1100, 0), Margin = new Padding(3, 10, 3, 8), ForeColor = Color.DimGray,
                Text = "The Vanilla workspace is the primary product surface. Up to two running clients are observed read-only at the top at all times. Original 4RTools remains in the secondary legacy tab. Compatible data is migrated into this persistent Windows-user data location. Passwords remain Windows-DPAPI protected for this Windows user/PC."
            });
            var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
            AddIntegratedButton(buttons, "OPEN DATA FOLDER", OpenDataFolder);
            AddIntegratedButton(buttons, "CHECK FOR UPDATES", () => CheckForUpdates(false));
            AddIntegratedButton(buttons, "OPEN GITHUB RELEASES", () => Process.Start(VanillaUpdater.ReleasesUrl));
            panel.Controls.Add(buttons);
            panel.Controls.Add(new Label
            {
                AutoSize = true, MaximumSize = new Size(1100, 0), Margin = new Padding(3, 10, 3, 3),
                Text = "Updates are checked at every normal startup from the public GitHub Releases page. After you click Yes, the release is downloaded and verified immediately. If recovery or Cart currently owns input, the update waits for a safe point and then applies and restarts automatically; no second click is required."
            });
            vanillaAboutPage.Controls.Add(panel);
        }

        private static Label PathLabel(string caption, string path)
        {
            return new Label { AutoSize = true, MaximumSize = new Size(1100, 0), Text = caption + ":  " + path, Margin = new Padding(3, 5, 3, 0) };
        }

        private static void AddIntegratedButton(Control parent, string text, System.Action action)
        {
            var button = new Button { Text = text, AutoSize = true, Margin = new Padding(3) };
            button.Click += (s, e) => action();
            parent.Controls.Add(button);
        }

        private void OpenDataFolder()
        {
            VanillaAppData.InitializeAndMigrateLegacy(AppDomain.CurrentDomain.BaseDirectory);
            Process.Start(VanillaAppData.RootDirectory);
        }

        private async void CheckForUpdates(bool startup)
        {
            if (updateCheckRunning || smokeTest) return;
            updateCheckRunning = true;
            integratedUpdateStatus.Text = "Checking for updates...";
            try
            {
                VanillaUpdateInfo update = await VanillaUpdater.CheckAsync();
                if (update == null)
                {
                    integratedUpdateStatus.Text = "Version " + VanillaUpdater.CurrentVersionText + " - up to date.";
                    if (!startup) MessageBox.Show(this, "4RTools Vanilla " + VanillaUpdater.CurrentVersionText + " is the latest published release.", "Updates", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                integratedUpdateStatus.Text = "Version " + VanillaUpdater.CurrentVersionText + " - update " + update.TagName + " available";
                DialogResult answer = MessageBox.Show(this,
                    "4RTools Vanilla " + update.Version.ToString(3) + " is available. Download the verified public GitHub Release and restart automatically?\n\nYour profiles and recovery settings are stored outside the application folder and will be preserved. If recovery or Cart is currently using input, the verified update will wait for a safe point and continue automatically.",
                    "4RTools Vanilla update", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer != DialogResult.Yes) return;
                integratedUpdateStatus.Text = "Downloading and verifying public release " + update.TagName + "...";
                string payload = await VanillaUpdater.DownloadAndStageAsync(update);
                // Staging does not interrupt gameplay. Stop temporary work only after the
                // payload is verified, and never exit while Cart/recovery owns input.
                integratedTemporaryActions?.StopForApplicationUpdate();
                if (integratedReconnectSupervisor != null)
                {
                    while (!integratedReconnectSupervisor.TryPauseForApplicationUpdate())
                    {
                        integratedUpdateStatus.Text = "Update verified. Waiting for active recovery/Cart input to finish...";
                        await System.Threading.Tasks.Task.Delay(250);
                        if (IsDisposed || Disposing) return;
                    }
                }
                integratedUpdateStatus.Text = "Update verified. Restarting...";
                VanillaUpdater.BeginApplyAndRestart(payload);
                BeginInvoke((MethodInvoker)Application.Exit);
            }
            catch (Exception ex)
            {
                integratedUpdateStatus.Text = startup ? "Version " + VanillaUpdater.CurrentVersionText + " - update check unavailable." : "Version " + VanillaUpdater.CurrentVersionText + " - update check failed.";
                if (!startup) MessageBox.Show(this, ex.Message, "Update check failed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { updateCheckRunning = false; }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try { integratedTemporaryActions?.Dispose(); } catch { }
            integratedTemporaryActions = null;
            try { integratedSmartTeleport?.Dispose(); } catch { }
            integratedSmartTeleport = null;
            try { integratedFleetDashboard?.Dispose(); } catch { }
            integratedFleetDashboard = null;
            try { integratedFleetMonitor?.Dispose(); } catch { }
            integratedFleetMonitor = null;
            try { integratedReconnectSupervisor?.Dispose(); } catch { }
            integratedReconnectSupervisor = null;
            base.OnFormClosed(e);
        }
    }
}

namespace _4RTools.Model.Vanilla
{
    internal sealed partial class VanillaReconnectForm
    {
        internal void PrepareForEmbeddedHost()
        {
            TopLevel = false;
            FormBorderStyle = FormBorderStyle.None;
            MinimumSize = Size.Empty;
            MaximumSize = new Size(32767, 32767);
            AutoSize = false;
            Dock = DockStyle.Fill;
            Margin = Padding.Empty;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScroll = false;
            AutoScrollMinSize = Size.Empty;
        }
    }
}
