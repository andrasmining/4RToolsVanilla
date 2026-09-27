using System.Windows.Forms;
using System;
using System.Windows.Input;
using _4RTools.Model;
using _4RTools.Utils;

namespace _4RTools.Forms
{
    public partial class AutopotForm : Form, IObserver
    {
        private Autopot autopot;
        private bool isYgg;

        public AutopotForm(Subject subject, bool isYgg)
        {
            using (FormUtils.BeginLoading(this)) { InitializeComponent(); }
            if (isYgg)
            {
                this.picBoxHP.Image = Resources._4RTools.ETCResource.Yggdrasil;
                this.picBoxSP.Image = Resources._4RTools.ETCResource.Yggdrasil;
            }
            subject.Attach(this);
            this.isYgg = isYgg;
            txtHpKey.KeyDown += FormUtils.OnKeyDown;
            txtHpKey.KeyPress += FormUtils.OnKeyPress;
            txtHpKey.TextChanged += onHpTextChange;
            txtSPKey.KeyDown += FormUtils.OnKeyDown;
            txtSPKey.KeyPress += FormUtils.OnKeyPress;
            txtSPKey.TextChanged += onSpTextChange;
            FormUtils.CommitNumericEditsOnClose(this);
        }

        public void Update(ISubject subject)
        {
            switch ((subject as Subject).Message.code)
            {
                case MessageCode.PROFILE_CHANGED:
                    this.autopot = this.isYgg ? ProfileSingleton.GetCurrent().AutopotYgg : ProfileSingleton.GetCurrent().Autopot;
                    using (FormUtils.BeginLoading(this)) { InitializeApplicationForm(); }
                    break;
                case MessageCode.TURN_OFF:
                    this.autopot.Stop();
                    break;
                case MessageCode.TURN_ON:
                    this.autopot.Start();
                    break;
            }
        }

        private void InitializeApplicationForm()
        {
            this.txtHpKey.Text = this.autopot.hpKey.ToString();
            this.txtSPKey.Text = this.autopot.spKey.ToString();
            this.txtHPpct.Text = this.autopot.hpPercent.ToString();
            this.txtSPpct.Text = this.autopot.spPercent.ToString();
            this.txtAutopotDelay.Text = this.autopot.delay.ToString();




        }

        private void onHpTextChange(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this) || autopot == null) return;
            Key key = (Key)Enum.Parse(typeof(Key), txtHpKey.Text.ToString());
            this.autopot.hpKey = key;
            ProfileSingleton.SetConfiguration(this.autopot);
        }

        private void onSpTextChange(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this) || autopot == null) return;
            Key key = (Key)Enum.Parse(typeof(Key), txtSPKey.Text.ToString());
            this.autopot.spKey = key;
            ProfileSingleton.SetConfiguration(this.autopot);
        }

        private void txtAutopotDelayTextChanged(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this) || autopot == null) return;
            try
            {
                this.autopot.delay = decimal.ToInt32(this.txtAutopotDelay.Value);
                ProfileSingleton.SetConfiguration(this.autopot);
            }
            catch (Exception) { }
        }

        private void txtHPpctTextChanged(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this) || autopot == null) return;
            try
            {
                this.autopot.hpPercent = decimal.ToInt32(this.txtHPpct.Value);
                ProfileSingleton.SetConfiguration(this.autopot);
            }
            catch (Exception) { }

        }

        private void txtSPpctTextChanged(object sender, EventArgs e)
        {
            if (FormUtils.IsLoading(this) || autopot == null) return;
            try
            {
                this.autopot.spPercent = decimal.ToInt32(this.txtSPpct.Value);
                ProfileSingleton.SetConfiguration(this.autopot);
            }
            catch (Exception) { }
        }
    }
}
