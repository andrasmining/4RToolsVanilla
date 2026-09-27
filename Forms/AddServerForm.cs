using _4RTools.Model;
using _4RTools.Utils;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace _4RTools.Forms
{
    public partial class AddServerForm : Form
    {
        private ClientDTO dto;
        private readonly Subject subject;
        private readonly Func<ClientDTO, string, string, string, ClientDTO> persist;
        private readonly Label saveStatus = new Label { Location = new Point(6, 118), Size = new Size(236, 39) };
        private bool dirty;

        public AddServerForm(ClientDTO dto, Subject subject)
            : this(dto, subject, (original, hp, name, process) => original == null
                ? LocalServerManager.AddServer(hp, name, process) : LocalServerManager.UpdateServer(original, hp, name, process)) { }

        internal AddServerForm(ClientDTO dto, Subject subject, Func<ClientDTO, string, string, string, ClientDTO> persist)
        {
            this.subject = subject;
            this.persist = persist ?? throw new ArgumentNullException(nameof(persist));
            using (FormUtils.BeginLoading(this))
            {
                InitializeComponent();
                groupBox1.Controls.Add(saveStatus);
                SetupInputs();
                if (dto != null)
                {
                    this.dto = new ClientDTO(dto.name, dto.description, dto.hpAddress, dto.nameAddress);
                    SetAddress("txtHP", dto.hpAddress);
                    SetAddress("txtName", dto.nameAddress);
                    processCB.Text = dto.name;
                }
                UpdateMode();
            }
            processCB.TextChanged += MarkDirty;
            processCB.Validated += CompletedEdit;
            processCB.SelectionChangeCommitted += CompletedEdit;
            processCB.KeyDown += CompleteOnEnter;
            FormClosing += (sender, args) => { if (this.dto != null && !Commit(false)) args.Cancel = true; };
        }

        private void SetAddress(string prefix, string address)
        {
            string value = address ?? "";
            if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) value = value.Substring(2);
            for (int index = 0; index < 8; index++)
                Controls.Find(prefix + (index + 1), true)[0].Text = index < value.Length ? value[index].ToString() : "";
        }

        private void UpdateMode()
        {
            Text = dto == null ? "Add Server" : "Edit Server " + dto.name;
            btnAdd.Visible = dto == null;
            saveStatus.Width = dto == null ? 236 : 325;
            AcceptButton = dto == null ? btnAdd : null;
        }

        public void SetupInputs()
        {
            foreach (TextBox textBox in FormUtils.GetAll(this, typeof(TextBox)))
            {
                textBox.MaxLength = 1;
                textBox.CharacterCasing = CharacterCasing.Upper;
                textBox.KeyPress += (sender, args) =>
                {
                    if (!char.IsControl(args.KeyChar) && !LocalServerStore.IsHex(new[] { args.KeyChar })) args.Handled = true;
                };
                textBox.TextChanged += MarkDirty;
                textBox.Validated += CompletedEdit;
                textBox.KeyDown += CompleteOnEnter;
            }
        }

        private void MarkDirty(object sender, EventArgs args)
        {
            if (FormUtils.IsLoading(this)) return;
            dirty = true;
            saveStatus.ForeColor = SystemColors.ControlText;
            saveStatus.Text = dto == null ? "" : "Editing…";
        }

        private void CompletedEdit(object sender, EventArgs args) { if (dto != null) Commit(false); }
        private void CompleteOnEnter(object sender, KeyEventArgs args)
        {
            if (args.KeyCode != Keys.Enter || dto == null) return;
            args.Handled = true;
            args.SuppressKeyPress = true;
            Commit(false);
        }

        private bool Commit(bool create)
        {
            if (FormUtils.IsLoading(this) || (!create && (!dirty || dto == null))) return true;
            string hpAddress = string.Concat(Enumerable.Range(1, 8).Select(index => Controls.Find("txtHP" + index, true)[0].Text));
            string nameAddress = string.Concat(Enumerable.Range(1, 8).Select(index => Controls.Find("txtName" + index, true)[0].Text));
            try
            {
                LocalServerStore.Validate(hpAddress, nameAddress, processCB.Text);
                dto = persist(dto, hpAddress, nameAddress, processCB.Text);
                dirty = false;
                UpdateMode();
                saveStatus.ForeColor = Color.DarkGreen;
                saveStatus.Text = "Saved";
                subject?.Notify(new Utils.Message(MessageCode.SERVER_LIST_CHANGED, "Server changed"));
                return true;
            }
            catch (Exception ex)
            {
                saveStatus.ForeColor = Color.Firebrick;
                saveStatus.Text = "Not saved: " + ex.Message;
                return false;
            }
        }

        private void AddServerForm_Load(object sender, EventArgs args)
        {
            using (FormUtils.BeginLoading(this))
            {
                string selected = processCB.Text;
                processCB.Items.Clear();
                foreach (Process process in Process.GetProcesses())
                {
                    using (process)
                    {
                        try { if (process.MainWindowTitle != "" && !processCB.Items.Contains(process.ProcessName)) processCB.Items.Add(process.ProcessName); }
                        catch (InvalidOperationException) { }
                        catch (System.ComponentModel.Win32Exception) { }
                    }
                }
                processCB.Text = selected;
            }
        }

        private void btnAdd_Click(object sender, EventArgs args) { Commit(true); }
    }
}
