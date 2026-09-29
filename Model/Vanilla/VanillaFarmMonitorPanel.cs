using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    public sealed class VanillaFarmMonitorPanel : UserControl
    {
        private readonly VanillaWeightAlertService weightService;
        private readonly VanillaFarmMonitorService monitor;
        private readonly TableLayoutPanel cards;
        private readonly Timer timer;
        private readonly ToolTip help;
        private string accountSignature = "";

        public VanillaFarmMonitorPanel(VanillaWeightAlertService service)
        {
            weightService = service ?? throw new ArgumentNullException(nameof(service));
            monitor = service.FarmMonitor;
            Dock = DockStyle.Top;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            BackColor = Color.White;

            help = new ToolTip { ShowAlways = true, AutoPopDelay = 30000 };
            cards = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                RowCount = 1,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
            Controls.Add(cards);

            timer = new Timer { Interval = 1000 };
            timer.Tick += (s, e) => RefreshCards();
            RebuildCards();
            timer.Start();
        }

        private IReadOnlyList<VanillaReconnectAccount> Accounts()
        {
            return weightService.FarmMonitorAccounts
                .Where(account => account != null && !string.IsNullOrWhiteSpace(account.Id))
                .OrderByDescending(account => account.Enabled)
                .ThenBy(account => account.Label, StringComparer.CurrentCultureIgnoreCase)
                .Take(2)
                .ToArray();
        }

        private static string Signature(IEnumerable<VanillaReconnectAccount> accounts)
        {
            return string.Join("|", accounts.Select(account =>
                (account.Id ?? "") + ":" + (account.Label ?? "") + ":" + (account.CharacterName ?? "")));
        }

        private void RebuildCards()
        {
            IReadOnlyList<VanillaReconnectAccount> accounts = Accounts();
            accountSignature = Signature(accounts);
            cards.SuspendLayout();
            try
            {
                foreach (Control oldCard in cards.Controls.Cast<Control>().ToArray()) oldCard.Dispose();
                cards.Controls.Clear();
                cards.ColumnCount = Math.Max(1, accounts.Count);
                cards.ColumnStyles.Clear();
                if (accounts.Count <= 1)
                    cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
                else
                {
                    cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                    cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50F));
                }

                if (accounts.Count == 0)
                {
                    cards.Controls.Add(new Label
                    {
                        AutoSize = true,
                        ForeColor = Color.DimGray,
                        Text = "No configured character rows are available for farming monitoring."
                    }, 0, 0);
                    return;
                }

                for (int i = 0; i < accounts.Count; i++)
                {
                    var card = new FarmMonitorCard(monitor, accounts[i], help)
                    {
                        Dock = DockStyle.Fill,
                        Margin = new Padding(i == 0 ? 0 : 6, 0, i == accounts.Count - 1 ? 0 : 6, 0)
                    };
                    cards.Controls.Add(card, i, 0);
                }
            }
            finally { cards.ResumeLayout(true); }
        }

        private void RefreshCards()
        {
            IReadOnlyList<VanillaReconnectAccount> accounts = Accounts();
            if (!string.Equals(accountSignature, Signature(accounts), StringComparison.Ordinal))
            {
                RebuildCards();
                return;
            }

            foreach (FarmMonitorCard card in cards.Controls.OfType<FarmMonitorCard>())
                card.RefreshSnapshot(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (timer != null) timer.Dispose();
                if (help != null) help.Dispose();
            }
            base.Dispose(disposing);
        }

        private sealed class FarmMonitorCard : GroupBox
        {
            private readonly VanillaFarmMonitorService monitor;
            private readonly VanillaReconnectAccount account;
            private readonly ToolTip help;
            private readonly Label summary;
            private readonly Label unassigned;
            private readonly Label status;
            private readonly Button start;
            private readonly Button pause;
            private readonly Button reset;
            private readonly Button add;
            private readonly Button delete;
            private readonly DataGridView grid;
            private bool loading;

            internal FarmMonitorCard(VanillaFarmMonitorService monitor, VanillaReconnectAccount account, ToolTip help)
            {
                this.monitor = monitor;
                this.account = account.Clone();
                this.help = help;

                Text = DisplayName(account);
                AutoSize = true;
                AutoSizeMode = AutoSizeMode.GrowAndShrink;
                Padding = new Padding(8);
                MinimumSize = new Size(470, 0);

                summary = new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    Font = new Font("Segoe UI", 9F, FontStyle.Bold),
                    Margin = new Padding(0, 2, 0, 5)
                };
                unassigned = new Label
                {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    ForeColor = Color.DarkOrange,
                    Margin = new Padding(0, 2, 0, 4)
                };
                status = new Label
                {
                    AutoSize = true,
                    ForeColor = Color.DimGray,
                    Margin = new Padding(8, 7, 0, 0)
                };

                start = new Button { Text = "START / RESUME", AutoSize = true };
                pause = new Button { Text = "PAUSE", AutoSize = true };
                reset = new Button { Text = "RESET", AutoSize = true };
                add = new Button { Text = "ADD ITEM", AutoSize = true };
                delete = new Button { Text = "DELETE ITEM", AutoSize = true };

                grid = BuildGrid();
                var root = new TableLayoutPanel
                {
                    Dock = DockStyle.Top,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    ColumnCount = 1,
                    RowCount = 5,
                    Margin = Padding.Empty
                };
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 138));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                root.Controls.Add(summary, 0, 0);
                root.Controls.Add(unassigned, 0, 1);
                root.Controls.Add(grid, 0, 2);

                var buttons = new FlowLayoutPanel
                {
                    Dock = DockStyle.Fill,
                    AutoSize = true,
                    WrapContents = true,
                    Margin = new Padding(0, 5, 0, 0)
                };
                buttons.Controls.Add(start);
                buttons.Controls.Add(pause);
                buttons.Controls.Add(reset);
                buttons.Controls.Add(add);
                buttons.Controls.Add(delete);
                buttons.Controls.Add(status);
                root.Controls.Add(buttons, 0, 3);

                var info = new Label { AutoSize = true, Text = "ⓘ", Cursor = Cursors.Help, Margin = new Padding(2, 4, 0, 0) };
                help.SetToolTip(info,
                    "RESET clears counts, elapsed time and unassigned Cart weight, keeps item definitions, and immediately starts a new run. "
                    + "For the cleanest comparison, empty carried farming loot before reset. "
                    + "Automatic rows count only verified Cart-weight increases. Choose one automatic row per Use/Equip/Etc category and enter that item's unit weight. "
                    + "If a transfer cannot be mapped safely, its Cart-weight delta remains Unassigned instead of being guessed. Pause to edit prices/counts or add manual loot. Automatic source mappings are YOUR assumptions, not item recognition. Mixed items in one category require a combined average price or manual counts.");
                root.Controls.Add(info, 0, 4);
                Controls.Add(root);

                help.SetToolTip(summary, "Total value is sum(count × zeny/item). Zeny/hour uses only the monitor's active elapsed time.");
                help.SetToolTip(grid,
                    "Item and zeny/item are generic. Count is editable while paused. Auto=Manual never changes automatically. "
                    + "Use/Equip/Etc maps verified Cart transfers from that category using Weight/item. Any is a fallback for a category without its own mapping. Enter decimal prices without thousands separators; your local decimal separator and a decimal point are accepted.");
                help.SetToolTip(reset, "Clear this character's counts/time/unassigned deltas, keep item definitions, and start immediately.");
                help.SetToolTip(pause, "Stop elapsed time and automatic Cart-transfer counting so item rows can be edited.");
                help.SetToolTip(start, "Continue elapsed time and automatic counting without clearing the current run.");

                start.Click += (s, e) => Guard(() => monitor.SetRunning(account.Id, DisplayName(account), true));
                pause.Click += (s, e) => Guard(() => monitor.SetRunning(account.Id, DisplayName(account), false));
                reset.Click += (s, e) => ResetRun();
                add.Click += (s, e) => AddItem();
                delete.Click += (s, e) => DeleteItem();
                grid.CellEndEdit += (s, e) => SaveGrid();
                grid.DataError += (s, e) =>
                {
                    e.ThrowException = false;
                    status.ForeColor = Color.Firebrick;
                    status.Text = "Invalid item value.";
                };

                SizeChanged += (s, e) => status.MaximumSize = new Size(Math.Max(180, ClientSize.Width - 30), 0);
                RefreshSnapshot(true);
            }

            private DataGridView BuildGrid()
            {
                var value = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    AllowUserToAddRows = false,
                    AllowUserToDeleteRows = false,
                    AllowUserToResizeRows = false,
                    RowHeadersVisible = false,
                    SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                    MultiSelect = false,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                    BackgroundColor = Color.White,
                    BorderStyle = BorderStyle.FixedSingle
                };
                value.Columns.Add(new DataGridViewTextBoxColumn { Name = "Id", Visible = false });
                value.Columns.Add(new DataGridViewTextBoxColumn { Name = "Item", HeaderText = "Item", FillWeight = 125 });
                value.Columns.Add(new DataGridViewTextBoxColumn { Name = "Zeny", HeaderText = "Zeny/item", FillWeight = 82 });
                value.Columns.Add(new DataGridViewTextBoxColumn { Name = "Count", HeaderText = "Count", FillWeight = 70 });
                value.Columns.Add(new DataGridViewComboBoxColumn
                {
                    Name = "Auto",
                    HeaderText = "Auto",
                    FillWeight = 70,
                    DataSource = new[] { "Manual", "Use", "Equip", "Etc", "Any" }
                });
                value.Columns.Add(new DataGridViewTextBoxColumn { Name = "Weight", HeaderText = "Wt/item", FillWeight = 62 });
                return value;
            }

            internal void RefreshSnapshot(bool forceGrid)
            {
                VanillaFarmMonitorSnapshot snapshot;
                try { snapshot = monitor.Snapshot(account.Id, DisplayName(account)); }
                catch (Exception ex)
                {
                    status.ForeColor = Color.Firebrick;
                    status.Text = ex.Message;
                    return;
                }

                string state = snapshot.Running ? "RUNNING" : "PAUSED";
                summary.Text = state
                    + "  •  " + FormatElapsed(snapshot.Elapsed)
                    + "  •  " + FormatZeny(snapshot.TotalZeny)
                    + "  •  " + FormatZeny(snapshot.ZenyPerHour) + "/h";

                if (!string.IsNullOrWhiteSpace(snapshot.Warning))
                {
                    status.ForeColor = Color.Firebrick;
                    status.Text = snapshot.Warning;
                }

                if (snapshot.UnassignedWeight > 0)
                {
                    unassigned.Visible = true;
                    unassigned.Text = "Unassigned Cart wt  Use " + snapshot.UnassignedUseWeight
                        + "  Equip " + snapshot.UnassignedEquipWeight
                        + "  Etc " + snapshot.UnassignedEtcWeight;
                }
                else
                {
                    unassigned.Visible = false;
                    unassigned.Text = "";
                }

                start.Enabled = !snapshot.Running;
                pause.Enabled = snapshot.Running;
                add.Enabled = !snapshot.Running;
                delete.Enabled = !snapshot.Running && grid.SelectedRows.Count > 0;
                grid.ReadOnly = snapshot.Running;

                if (forceGrid)
                    LoadGrid(snapshot);
                else if (snapshot.Running)
                    RefreshCounts(snapshot);
            }

            private void LoadGrid(VanillaFarmMonitorSnapshot snapshot)
            {
                if (loading) return;
                loading = true;
                try
                {
                    string selectedId = grid.SelectedRows.Count > 0 ? grid.SelectedRows[0].Cells["Id"].Value as string : null;
                    grid.Rows.Clear();
                    foreach (VanillaFarmMonitorItem item in snapshot.Items)
                    {
                        int row = grid.Rows.Add(item.Id, item.Name,
                            item.ZenyPerItem.ToString("0.############################", CultureInfo.CurrentCulture),
                            item.Count.ToString(CultureInfo.InvariantCulture),
                            item.AutoSource.ToString(),
                            item.UnitWeight.ToString(CultureInfo.InvariantCulture));
                        if (string.Equals(item.Id, selectedId, StringComparison.OrdinalIgnoreCase))
                            grid.Rows[row].Selected = true;
                    }
                    delete.Enabled = !snapshot.Running && grid.SelectedRows.Count > 0;
                }
                finally { loading = false; }
            }

            private void RefreshCounts(VanillaFarmMonitorSnapshot snapshot)
            {
                var byId = snapshot.Items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                foreach (DataGridViewRow row in grid.Rows)
                {
                    string id = row.Cells["Id"].Value as string;
                    VanillaFarmMonitorItem item;
                    if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out item)) continue;
                    row.Cells["Count"].Value = item.Count.ToString(CultureInfo.InvariantCulture);
                }
            }

            private void SaveGrid()
            {
                if (loading) return;
                try
                {
                    VanillaFarmMonitorSnapshot snapshot = monitor.Snapshot(account.Id, DisplayName(account));
                    if (snapshot.Running)
                    {
                        status.ForeColor = Color.Firebrick;
                        status.Text = "Pause before editing.";
                        RefreshSnapshot(true);
                        return;
                    }

                    var items = new List<VanillaFarmMonitorItem>();
                    foreach (DataGridViewRow row in grid.Rows)
                    {
                        string id = Convert.ToString(row.Cells["Id"].Value, CultureInfo.InvariantCulture);
                        string name = Convert.ToString(row.Cells["Item"].Value, CultureInfo.CurrentCulture) ?? "";
                        decimal zeny = ParseDecimal(row.Cells["Zeny"].Value, "Zeny/item");
                        long count = ParseLong(row.Cells["Count"].Value, "Count");
                        uint unitWeight = ParseUInt(row.Cells["Weight"].Value, "Weight/item");
                        VanillaFarmAutoSource source;
                        string sourceText = Convert.ToString(row.Cells["Auto"].Value, CultureInfo.InvariantCulture);
                        if (!Enum.TryParse(sourceText, true, out source))
                            throw new ArgumentException("Auto source must be Manual, Use, Equip, Etc or Any.");
                        items.Add(new VanillaFarmMonitorItem
                        {
                            Id = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString("N") : id,
                            Name = name,
                            ZenyPerItem = zeny,
                            Count = count,
                            AutoSource = source,
                            UnitWeight = unitWeight
                        });
                    }
                    monitor.ReplaceItems(account.Id, DisplayName(account), items);
                    status.ForeColor = Color.DarkGreen;
                    status.Text = "Saved";
                    RefreshSnapshot(true);
                }
                catch (Exception ex)
                {
                    status.ForeColor = Color.Firebrick;
                    status.Text = ex.Message;
                    RefreshSnapshot(true);
                }
            }

            private void AddItem()
            {
                Guard(() =>
                {
                    VanillaFarmMonitorSnapshot snapshot = monitor.Snapshot(account.Id, DisplayName(account));
                    if (snapshot.Running) throw new InvalidOperationException("Pause before adding an item.");
                    var items = snapshot.Items.Select(item => item.Clone()).ToList();
                    items.Add(new VanillaFarmMonitorItem
                    {
                        Name = "Item " + (items.Count + 1).ToString(CultureInfo.InvariantCulture),
                        ZenyPerItem = 0m,
                        Count = 0,
                        AutoSource = VanillaFarmAutoSource.Manual,
                        UnitWeight = 1
                    });
                    monitor.ReplaceItems(account.Id, DisplayName(account), items);
                    status.ForeColor = Color.DarkGreen;
                    status.Text = "Item added";
                    RefreshSnapshot(true);
                    if (grid.Rows.Count > 0)
                    {
                        grid.ClearSelection();
                        grid.Rows[grid.Rows.Count - 1].Selected = true;
                        grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells["Item"];
                        grid.BeginEdit(true);
                    }
                });
            }

            private void DeleteItem()
            {
                Guard(() =>
                {
                    VanillaFarmMonitorSnapshot snapshot = monitor.Snapshot(account.Id, DisplayName(account));
                    if (snapshot.Running) throw new InvalidOperationException("Pause before deleting an item.");
                    if (grid.SelectedRows.Count == 0) return;
                    string id = grid.SelectedRows[0].Cells["Id"].Value as string;
                    monitor.ReplaceItems(account.Id, DisplayName(account),
                        snapshot.Items.Where(item => !string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)));
                    status.ForeColor = Color.DarkGreen;
                    status.Text = "Item deleted";
                    RefreshSnapshot(true);
                });
            }

            private void ResetRun()
            {
                if (MessageBox.Show(this,
                    "Reset this farming run? Counts, elapsed time and unassigned Cart weight will be cleared. "
                    + "Item names/prices/mappings stay saved and the timer starts immediately. Empty carried farming loot first for the cleanest result.",
                    "Reset farming monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
                Guard(() =>
                {
                    monitor.Reset(account.Id, DisplayName(account));
                    status.ForeColor = Color.DarkGreen;
                    status.Text = "New run started";
                    RefreshSnapshot(true);
                });
            }

            private void Guard(System.Action action)
            {
                try { action(); }
                catch (Exception ex)
                {
                    status.ForeColor = Color.Firebrick;
                    status.Text = ex.Message;
                    RefreshSnapshot(true);
                }
            }

            private static decimal ParseDecimal(object raw, string caption)
            {
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                decimal value;
                if (!decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.CurrentCulture, out value)
                    && !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(caption + " is not a valid number.");
                return value;
            }

            private static long ParseLong(object raw, string caption)
            {
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                long value;
                if (!long.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
                    && !long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(caption + " must be a whole number.");
                return value;
            }

            private static uint ParseUInt(object raw, string caption)
            {
                string text = Convert.ToString(raw, CultureInfo.CurrentCulture);
                uint value;
                if (!uint.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
                    && !uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
                    throw new ArgumentException(caption + " must be a positive whole number.");
                return value;
            }

            private static string DisplayName(VanillaReconnectAccount value)
            {
                string label = string.IsNullOrWhiteSpace(value.Label) ? "Character" : value.Label.Trim();
                return string.IsNullOrWhiteSpace(value.CharacterName)
                    ? label
                    : label + " · " + value.CharacterName.Trim();
            }

            private static string FormatElapsed(TimeSpan elapsed)
            {
                long hours = (long)Math.Floor(elapsed.TotalHours);
                return hours.ToString("0", CultureInfo.InvariantCulture) + ":"
                    + elapsed.Minutes.ToString("00", CultureInfo.InvariantCulture) + ":"
                    + elapsed.Seconds.ToString("00", CultureInfo.InvariantCulture);
            }

            private static string FormatZeny(decimal value)
            {
                return value.ToString("#,##0.##", CultureInfo.CurrentCulture) + " z";
            }
        }
    }
}
