using System;
using System.Drawing;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace _4RTools.Forms
{
    public partial class Container
    {
        private TabPage vanillaMemoryDiscoveryPage, vanillaWeightAlertsPage;
        private VanillaMemoryDiscoveryPanel integratedMemoryDiscovery;
        private VanillaUtf8MemoryDiscoveryPanel integratedTextDiscovery;
        private TabControl integratedMemoryDiscoveryModes;
        private VanillaWeightAlertsPanel integratedWeightAlerts;
        private VanillaWeightAlertService integratedWeightAlertService;
        private bool memoryDiscoveryIntegrated, weightAlertsIntegrated;
        internal bool WeightAlertsRunning { get { return integratedWeightAlertService?.IsRunning == true; } }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyVanillaFleetSizing();
            IntegrateMemoryDiscovery();
            IntegrateWeightAlerts();
            InstallGlobalDebugUi();
            // Debug controls are inserted into the top header during OnShown. Compact only after
            // those final controls exist so Full-HD/RDP does not reserve a large empty auto-size row.
            CompactIntegratedHeader();
        }

        private void IntegrateMemoryDiscovery()
        {
            if (memoryDiscoveryIntegrated || vanillaWorkspace == null || integratedFleetMonitor == null) return;
            memoryDiscoveryIntegrated = true;
            vanillaMemoryDiscoveryPage = new TabPage("Memory finder") { Padding = new Padding(6), UseVisualStyleBackColor = true };
            int diagnosticsIndex = vanillaWorkspace.TabPages.IndexOf(vanillaDiagnosticsPage);
            if (diagnosticsIndex < 0) diagnosticsIndex = vanillaWorkspace.TabPages.Count;
            vanillaWorkspace.TabPages.Insert(diagnosticsIndex, vanillaMemoryDiscoveryPage);
            vanillaWorkspace.SelectedIndexChanged += (s, e) =>
            {
                if (vanillaWorkspace.SelectedTab == vanillaMemoryDiscoveryPage) EnsureMemoryDiscoveryEmbedded();
            };
        }

        private void IntegrateWeightAlerts()
        {
            if (weightAlertsIntegrated || vanillaWorkspace == null) return;
            weightAlertsIntegrated = true;
            integratedWeightAlertService = new VanillaWeightAlertService(AppDomain.CurrentDomain.BaseDirectory, integratedFleetMonitor, integratedReconnectSupervisor);
            if (integratedReconnectView != null)
                integratedReconnectView.WeightCartTestRequested = accountId => integratedWeightAlertService.RunCartNow(accountId);
            if (!smokeTest) integratedWeightAlertService.Start();
            vanillaWeightAlertsPage = new TabPage("Weight") { Padding = new Padding(6), UseVisualStyleBackColor = true };
            int insertAt = vanillaMemoryDiscoveryPage == null ? vanillaWorkspace.TabPages.IndexOf(vanillaDiagnosticsPage)
                : vanillaWorkspace.TabPages.IndexOf(vanillaMemoryDiscoveryPage);
            if (insertAt < 0) insertAt = vanillaWorkspace.TabPages.Count;
            vanillaWorkspace.TabPages.Insert(insertAt, vanillaWeightAlertsPage);
            vanillaWorkspace.SelectedIndexChanged += (s, e) =>
            {
                if (vanillaWorkspace.SelectedTab == vanillaWeightAlertsPage) EnsureWeightAlertsEmbedded();
            };
            FormClosing += (s, e) => integratedWeightAlerts?.FlushPendingSettings();
            FormClosed += (s, e) =>
            {
                try { integratedWeightAlerts?.Dispose(); } catch { }
                integratedWeightAlerts = null;
                try { integratedWeightAlertService?.Dispose(); } catch { }
                integratedWeightAlertService = null;
            };
        }

        private void EnsureMemoryDiscoveryEmbedded()
        {
            if (smokeTest) return;
            if (integratedMemoryDiscoveryModes != null && !integratedMemoryDiscoveryModes.IsDisposed) return;
            integratedMemoryDiscoveryModes = new TabControl { Dock = DockStyle.Fill };
            var numericPage = new TabPage("Numeric / values") { Padding = new Padding(4), UseVisualStyleBackColor = true };
            var textPage = new TabPage("Text / UTF-8") { Padding = new Padding(4), UseVisualStyleBackColor = true };
            integratedMemoryDiscovery = new VanillaMemoryDiscoveryPanel(integratedFleetMonitor) { Dock = DockStyle.Fill };
            integratedTextDiscovery = new VanillaUtf8MemoryDiscoveryPanel(integratedFleetMonitor) { Dock = DockStyle.Fill };
            numericPage.Controls.Add(integratedMemoryDiscovery);
            textPage.Controls.Add(integratedTextDiscovery);
            integratedMemoryDiscoveryModes.TabPages.Add(numericPage);
            integratedMemoryDiscoveryModes.TabPages.Add(textPage);
            vanillaMemoryDiscoveryPage.Controls.Add(integratedMemoryDiscoveryModes);
            integratedMemoryDiscoveryModes.BringToFront();
        }

        private void EnsureWeightAlertsEmbedded()
        {
            if (smokeTest) return;
            if (integratedWeightAlerts != null && !integratedWeightAlerts.IsDisposed) return;
            integratedWeightAlerts = new VanillaWeightAlertsPanel(integratedWeightAlertService) { Dock = DockStyle.Fill };
            vanillaWeightAlertsPage.Controls.Add(integratedWeightAlerts);
            integratedWeightAlerts.BringToFront();
        }
    }
}
