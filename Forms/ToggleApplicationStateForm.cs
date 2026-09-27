using System;
using System.Drawing;
using System.Windows.Forms;
using _4RTools.Utils;
using _4RTools.Model;
using System.Media;
using _4RTools.Properties;

namespace _4RTools.Forms
{
    public partial class ToggleApplicationStateForm : Form, IObserver
    {
        private Subject subject;
        private ContextMenu contextMenu;
        private MenuItem menuItem;

        //Store key used for last profile - necessarly to clean when change profile
        private Keys lastKey;
        private readonly bool smokeTest;
        private readonly Func<string> enableGuard;
        private readonly ToolTip statusTip = new ToolTip();
        public bool IsOn { get { return this.btnStatusToggle.Text == "ON"; } }

        public ToggleApplicationStateForm(Subject subject, bool smokeTest = false, Func<string> enableGuard = null)
        {
            this.smokeTest = smokeTest;
            this.enableGuard = enableGuard;
            InitializeComponent();

            subject.Attach(this);
            this.subject = subject;
            if (!smokeTest) KeyboardHook.Enable();
            this.txtStatusToggleKey.Text = ProfileSingleton.GetCurrent().UserPreferences.toggleStateKey;
            this.txtStatusToggleKey.KeyDown += new KeyEventHandler(FormUtils.OnKeyDown);
            this.txtStatusToggleKey.KeyPress += new KeyPressEventHandler(FormUtils.OnKeyPress);
            this.txtStatusToggleKey.TextChanged += new EventHandler(this.onStatusToggleKeyChange);
            this.cbAudio.Checked = ProfileSingleton.GetCurrent().UserPreferences.audioEnabled;
            this.cbAudio.CheckedChanged += (sender, args) =>
            {
                if (FormUtils.IsLoading(this)) return;
                ProfileSingleton.GetCurrent().UserPreferences.audioEnabled = cbAudio.Checked;
                ProfileSingleton.SetConfiguration(ProfileSingleton.GetCurrent().UserPreferences);
            };

            InitializeContextualMenu();
            if (smokeTest) this.notifyIconTray.Visible = false;
        }

        private void InitializeContextualMenu()
        {
            this.contextMenu = new ContextMenu();
            this.menuItem = new MenuItem();

            this.contextMenu.MenuItems.AddRange(
                    new MenuItem[] { this.menuItem });

            this.menuItem.Index = 0;
            this.menuItem.Text = "Close";
            this.menuItem.Click += new EventHandler(this.notifyShutdownApplication);

            this.notifyIconTray.ContextMenu = this.contextMenu;
        }

        public void Update(ISubject subject)
        {
            if ((subject as Subject).Message.code == MessageCode.TURN_OFF) ForceOff(null, false);
            if ((subject as Subject).Message.code == MessageCode.PROFILE_CHANGED)
            {
                Keys currentToggleKey = (Keys)Enum.Parse(typeof(Keys), ProfileSingleton.GetCurrent().UserPreferences.toggleStateKey);
                if (!smokeTest) KeyboardHook.Remove(lastKey);

                using (FormUtils.BeginLoading(this))
                {
                    this.txtStatusToggleKey.Text = currentToggleKey.ToString();
                    this.cbAudio.Checked = ProfileSingleton.GetCurrent().UserPreferences.audioEnabled;
                }
                if (!smokeTest) KeyboardHook.Add(currentToggleKey, new KeyboardHook.KeyPressed(this.toggleStatus));
                lastKey = currentToggleKey;
            }
        }

        private void btnToggleStatusHandler(object sender, EventArgs e) { this.toggleStatus(); }

        private void onStatusToggleKeyChange(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this)) return;
            if (IsOn) ForceOff("Toggle key changed");
            //Get last key from profile before update it in json
            Keys currentToggleKey = (Keys)Enum.Parse(typeof(Keys), this.txtStatusToggleKey.Text);
            if (!smokeTest) KeyboardHook.Remove(lastKey);
            if (!smokeTest) KeyboardHook.Add(currentToggleKey, new KeyboardHook.KeyPressed(this.toggleStatus));
            ProfileSingleton.GetCurrent().UserPreferences.toggleStateKey = currentToggleKey.ToString(); //Update profile key
            ProfileSingleton.SetConfiguration(ProfileSingleton.GetCurrent().UserPreferences);

            lastKey = currentToggleKey; //Refresh lastKey to update 
        }

        private bool toggleStatus()
        {
            if (smokeTest) return true;
            bool isOn = this.btnStatusToggle.Text == "ON";
            if (isOn)
            {
                ForceOff(null);

                if (this.cbAudio.Checked) { new SoundPlayer(Resources._4RTools.ETCResource.Speech_Off).Play(); }
            }
            else
            {
                Client client = ClientSingleton.GetClient();
                if (client != null)
                {
                    try
                    {
                    string error = enableGuard?.Invoke() ?? client.GetEnableError(ProfileSingleton.GetCurrent());
                    if (error != null) { ForceOff(error); MessageBox.Show(this, error, "Feature unavailable"); return true; }
                    client.SetAutomationEnabled(true);
                    this.subject.Notify(new Utils.Message(MessageCode.TURN_ON, null));
                    this.btnStatusToggle.BackColor = Color.Green;
                    this.btnStatusToggle.Text = "ON";
                    this.notifyIconTray.Icon = Resources._4RTools.ETCResource.logo_4rtools_on;
                    this.lblStatusToggle.Text = "Press the key to stop!";
                    this.lblStatusToggle.ForeColor = Color.Black;

                    if (this.cbAudio.Checked) { new SoundPlayer(Resources._4RTools.ETCResource.Speech_On).Play(); }
                    }
                    catch (Exception ex) { ForceOff(ex.Message); MessageBox.Show(this, ex.Message, "Automation stopped"); }
                }
                else
                {
                    this.lblStatusToggle.Text = "Please select the Ragnarok Client!!";
                    this.lblStatusToggle.ForeColor = Color.Red;
                }
            }

            return true;
        }

        public void ForceOff(string reason, bool notify = true)
        {
            ClientSingleton.GetClient()?.SetAutomationEnabled(false);
            this.btnStatusToggle.BackColor = Color.Red;
            this.btnStatusToggle.Text = "OFF";
            this.notifyIconTray.Icon = Resources._4RTools.ETCResource.logo_4rtools_off;
            if (notify) this.subject.Notify(new Utils.Message(MessageCode.TURN_OFF, null));
            this.lblStatusToggle.Text = string.IsNullOrWhiteSpace(reason) ? "Press the key to start!" : "OFF — see Vanilla status";
            statusTip.SetToolTip(this.lblStatusToggle, reason ?? "");
        }

        private void notifyIconDoubleClick(object sender, MouseEventArgs e)
        {
            this.subject.Notify(new Utils.Message(MessageCode.CLICK_ICON_TRAY, null));
        }

        private void notifyShutdownApplication(object Sender, EventArgs e)
        {
            // Close the form, which closes the application.
            this.subject.Notify(new Utils.Message(MessageCode.SHUTDOWN_APPLICATION, null));
        }
    }
}
