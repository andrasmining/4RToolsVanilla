using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using _4RTools.Model.Vanilla;

namespace _4RTools.Forms
{
    public partial class Container
    {
        private bool vanillaFleetSizingApplied;
        private bool arrangingVanillaHeader;
        private System.Windows.Forms.Timer vanillaMemoryAccessDiagnosticTimer;
        private readonly ToolTip vanillaFleetHelp = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };

        private void ApplyVanillaFleetSizing()
        {
            if (vanillaFleetSizingApplied || integratedFleetDashboard == null) return;
            vanillaFleetSizingApplied = true;
            ConfigureFleetPresentation(integratedFleetDashboard);
            UpdateVanillaFleetHeight();
            integratedFleetDashboard.PresentationChanged += (s, e) => UpdateVanillaFleetHeight();
            SizeChanged += (s, e) => { UpdateVanillaFleetHeight(); CompactIntegratedHeader(); };
            FontChanged += (s, e) => { UpdateVanillaFleetHeight(); CompactIntegratedHeader(); };
            integratedUpdateStatus.TextChanged += (s, e) => CompactIntegratedHeader();
            Control host = integratedFleetDashboard.Parent;
            if (host != null) host.Layout += (s, e) => CompactIntegratedHeader();
            CompactIntegratedHeader();
            StartMemoryAccessDiagnostics();
        }

        internal static int PreferredFleetDashboardHeight(int availableClientHeight)
        {
            if (availableClientHeight < 760) return 96;
            if (availableClientHeight < 900) return 104;
            if (availableClientHeight < 1050) return 112;
            return 120;
        }

        private void UpdateVanillaFleetHeight()
        {
            if (integratedFleetDashboard == null || integratedFleetDashboard.IsDisposed) return;
            // A compact strip must still contain its text. Font metrics, not just screen
            // height, determine the minimum; the final location/Cart-left row must never disappear.
            int contentHeight = 0;
            foreach (TableLayoutPanel outer in integratedFleetDashboard.Controls.OfType<TableLayoutPanel>())
            foreach (GroupBox card in outer.Controls.OfType<GroupBox>())
            {
                TableLayoutPanel layout = card.Controls.OfType<TableLayoutPanel>().FirstOrDefault();
                if (layout == null) continue;
                // GDI label autosizing can round below Font.Height after text scaling.
                // Keep a full line box even for short metrics and status captions.
                foreach (Label label in layout.Controls.OfType<Label>())
                    label.MinimumSize = new Size(label.MinimumSize.Width, label.Font.Height);
                if (layout.RowStyles.Count > 3)
                    layout.RowStyles[3].Height = layout.Controls.Cast<Control>().Where(c => layout.GetRow(c) == 3)
                        .Select(c => c.Font.Height + c.Margin.Vertical + 2).DefaultIfEmpty(layout.Font.Height + 4).Max();
                int rowsHeight = 0;
                for (int row = 0; row < layout.RowCount; row++)
                {
                    int r = row;
                    int measured = layout.Controls.Cast<Control>().Where(c => c.Visible && layout.GetRow(c) == r)
                        .Select(c => (r == 2 ? 4 : r == 5
                            ? c.GetPreferredSize(new Size(Math.Max(1, layout.ClientSize.Width - c.Margin.Horizontal), 0)).Height
                            : c.Font.Height) + c.Margin.Vertical).DefaultIfEmpty(0).Max();
                    rowsHeight += measured + (r == 2 ? 2 : 0);
                }
                // GroupBox borders/title and TableLayout rounding can consume a few pixels
                // beyond the summed row metrics on compact RDP heights. Keep a small explicit
                // containment allowance so the final Location / Cart-left row never touches
                // the dashboard viewport edge.
                contentHeight = Math.Max(contentHeight, rowsHeight + card.Font.Height + card.Padding.Vertical
                    + card.Margin.Vertical + outer.Padding.Vertical + 16);
            }
            int dashboardHeight = Math.Max(PreferredFleetDashboardHeight(ClientSize.Height), contentHeight);
            integratedFleetDashboard.MinimumSize = new Size(0, dashboardHeight);
            integratedFleetDashboard.Height = dashboardHeight;
            var host = integratedFleetDashboard.Parent as TableLayoutPanel;
            if (host != null && host.RowStyles.Count > 1)
            {
                host.RowStyles[1].SizeType = SizeType.Absolute;
                host.RowStyles[1].Height = dashboardHeight;
            }
        }

        private void CompactIntegratedHeader()
        {
            if (arrangingVanillaHeader || integratedFleetDashboard == null || integratedFleetDashboard.IsDisposed) return;
            var host = integratedFleetDashboard.Parent as TableLayoutPanel;
            var header = host == null ? null : host.GetControlFromPosition(0, 0) as TableLayoutPanel;
            if (header == null || host.RowStyles.Count == 0) return;
            FlowLayoutPanel statusGroup = header.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(c => c.Controls.Contains(integratedUpdateStatus));
            FlowLayoutPanel actions = header.Controls.OfType<FlowLayoutPanel>().FirstOrDefault(c => c != statusGroup);
            if (statusGroup == null || actions == null) return;
            arrangingVanillaHeader = true;
            header.SuspendLayout();
            try
            {
                int width = Math.Max(1, host.ClientSize.Width - host.Padding.Horizontal);
                header.AutoSize = false;
                header.MinimumSize = Size.Empty;
                header.Dock = DockStyle.Fill;
                actions.AutoSize = false;
                actions.Dock = DockStyle.Fill;
                statusGroup.AutoSize = false;
                statusGroup.Dock = DockStyle.Fill;
                statusGroup.WrapContents = false;
                statusGroup.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                int buttonWidth = statusGroup.Controls.OfType<Button>().Sum(c => c.GetPreferredSize(Size.Empty).Width + c.Margin.Horizontal);
                integratedUpdateStatus.MaximumSize = new Size(Math.Max(100, Math.Min(360, width - buttonWidth - 24)), 0);
                vanillaFleetHelp.SetToolTip(integratedUpdateStatus, integratedUpdateStatus.Text);

                int statusWidth = Math.Min(width, NaturalFlowWidth(statusGroup));
                bool stacked = NaturalFlowWidth(actions) + statusWidth + 8 > width;
                int actionWidth = stacked ? width : Math.Max(1, width - statusWidth);
                int actionHeight = MeasuredFlowHeight(actions, actionWidth);
                // The label may have just changed from one to multiple lines. Its old Bounds
                // still describe the previous text until layout completes; measure the new
                // preferred text height too, otherwise the first long status is clipped.
                int statusHeight = Math.Max(MeasuredFlowHeight(statusGroup, statusWidth),
                    statusGroup.Controls.Cast<Control>().Where(c => c.Visible)
                        .Select(c => c.GetPreferredSize(Size.Empty).Height + c.Margin.Vertical).DefaultIfEmpty(0).Max());
                int firstHeight = stacked ? actionHeight : Math.Max(actionHeight, statusHeight);
                int secondHeight = stacked ? statusHeight : 0;

                header.ColumnCount = 2;
                header.RowCount = 2;
                while (header.ColumnStyles.Count < 2) header.ColumnStyles.Add(new ColumnStyle());
                while (header.RowStyles.Count < 2) header.RowStyles.Add(new RowStyle());
                header.ColumnStyles[0].SizeType = SizeType.Absolute;
                header.ColumnStyles[0].Width = Math.Max(0, width - statusWidth);
                header.ColumnStyles[1].SizeType = SizeType.Absolute;
                header.ColumnStyles[1].Width = statusWidth;
                header.RowStyles[0].SizeType = SizeType.Absolute;
                header.RowStyles[0].Height = firstHeight;
                header.RowStyles[1].SizeType = SizeType.Absolute;
                header.RowStyles[1].Height = secondHeight;
                header.SetCellPosition(actions, new TableLayoutPanelCellPosition(0, 0));
                header.SetColumnSpan(actions, stacked ? 2 : 1);
                header.SetCellPosition(statusGroup, new TableLayoutPanelCellPosition(1, stacked ? 1 : 0));
                int height = firstHeight + secondHeight + header.Padding.Vertical;
                host.RowStyles[0].SizeType = SizeType.Absolute;
                if (host.RowStyles[0].Height != height) host.RowStyles[0].Height = height;
            }
            finally
            {
                header.ResumeLayout(true);
                arrangingVanillaHeader = false;
            }
        }

        private static int NaturalFlowWidth(FlowLayoutPanel flow)
        {
            return flow.Controls.Cast<Control>().Where(c => c.Visible)
                .Sum(c => c.GetPreferredSize(Size.Empty).Width + c.Margin.Horizontal) + flow.Padding.Horizontal + 2;
        }

        private static int MeasuredFlowHeight(FlowLayoutPanel flow, int width)
        {
            flow.Width = Math.Max(1, width);
            flow.PerformLayout();
            return Math.Max(28, flow.Controls.Cast<Control>().Where(c => c.Visible)
                .Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max() + flow.Padding.Bottom);
        }

        private void StartMemoryAccessDiagnostics()
        {
            if (smokeTest || vanillaMemoryAccessDiagnosticTimer != null) return;
            vanillaMemoryAccessDiagnosticTimer = new System.Windows.Forms.Timer { Interval = 2000 };
            vanillaMemoryAccessDiagnosticTimer.Tick += (s, e) => QueueMemoryAccessDiagnostics();
            vanillaMemoryAccessDiagnosticTimer.Start();
            QueueMemoryAccessDiagnostics();
            FormClosed += (s, e) =>
            {
                try { vanillaMemoryAccessDiagnosticTimer?.Stop(); } catch { }
                try { vanillaMemoryAccessDiagnosticTimer?.Dispose(); } catch { }
                vanillaMemoryAccessDiagnosticTimer = null;
                try { vanillaFleetHelp.Dispose(); } catch { }
            };
        }

        private static void QueueMemoryAccessDiagnostics()
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                VanillaMemoryAccessDiagnostics.CaptureCurrentClients();
                VanillaTargetSecurityDiagnostics.CaptureCurrentClients();
            });
        }

        private void ConfigureFleetPresentation(Control root)
        {
            foreach (Control child in root.Controls)
            {
                var layout = child as TableLayoutPanel;
                if (layout != null && layout.RowCount == 2 && layout.ColumnCount == 2)
                {
                    foreach (Control item in layout.Controls)
                    {
                        if (layout.GetRow(item) != 1) continue;
                        Label helper = item as Label;
                        if (helper == null) continue;
                        helper.Visible = false;
                        helper.Margin = Padding.Empty;
                        vanillaFleetHelp.SetToolTip(root, "Read-only values from the verified Vanilla build profile. Hover a client card for observation details.");
                    }
                    while (layout.RowStyles.Count < 2) layout.RowStyles.Add(new RowStyle());
                    layout.RowStyles[1].SizeType = SizeType.Absolute;
                    layout.RowStyles[1].Height = 0;
                }
                if (layout != null && layout.Parent is GroupBox && layout.RowCount >= 4 && layout.ColumnCount == 4)
                {
                    // Current fleet cards are deliberately one compact line of four metrics
                    // (HP/SP/carried/Cart), short bars, then one location/error line. Do not
                    // reserve any legacy Activity row or large default GroupBox padding.
                    while (layout.RowStyles.Count < 4) layout.RowStyles.Add(new RowStyle());
                    layout.RowStyles[0].SizeType = SizeType.AutoSize;
                    layout.RowStyles[1].SizeType = SizeType.AutoSize;
                    layout.RowStyles[2].SizeType = SizeType.Absolute;
                    layout.RowStyles[2].Height = 6;
                    layout.RowStyles[3].SizeType = SizeType.Absolute;
                    layout.RowStyles[3].Height = layout.Font.Height + 4;
                    for (int row = 4; row < layout.RowCount; row++) layout.RowStyles[row].SizeType = SizeType.AutoSize;

                    GroupBox card = (GroupBox)layout.Parent;
                    card.Padding = new Padding(6, 3, 6, 3);
                    card.Margin = new Padding(3, 2, 3, 2);
                    vanillaFleetHelp.SetToolTip(card,
                        "Read-only Vanilla client status: HP, SP, carried weight, Cart weight and location.");

                    foreach (Control item in layout.Controls)
                    {
                        int row = layout.GetRow(item);
                        item.Margin = new Padding(3, 1, 3, 1);
                        if (row == 2) item.Height = 4;
                        Label label = item as Label;
                        if (label == null || row != 3) continue;
                        label.AutoSize = false;
                        label.Dock = DockStyle.Fill;
                        label.AutoEllipsis = true;
                        label.TextAlign = ContentAlignment.TopLeft;
                    }
                }
                if (child.HasChildren) ConfigureFleetPresentation(child);
            }
        }
    }
}
