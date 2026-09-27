using System;
using System.Globalization;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    // NumericUpDown normally clamps an invalid typed value on focus loss. Settings
    // editors must instead retain the last saved value and report the invalid edit.
    internal sealed class VanillaSettingsNumber : NumericUpDown
    {
        internal static void Load(NumericUpDown control, decimal value)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            // Assigning the same cached Value does not replace an invalid draft.
            // Call while the owning editor suppresses its change handlers.
            control.Value = value;
            control.Text = value.ToString((control.ThousandsSeparator ? "N" : "F") + control.DecimalPlaces,
                CultureInfo.CurrentCulture);
        }

        internal static decimal Read(NumericUpDown control)
        {
            decimal value;
            if (!TryRead(control, out value))
                throw new ArgumentException("Enter a number from " + control.Minimum + " to " + control.Maximum
                    + " with at most " + control.DecimalPlaces + " decimal places.");
            return value;
        }

        private static bool TryRead(NumericUpDown control, out decimal value)
        {
            return decimal.TryParse(control.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out value)
                && value >= control.Minimum && value <= control.Maximum
                && decimal.Round(value, control.DecimalPlaces) == value;
        }

        protected override void ValidateEditText()
        {
            decimal value;
            if (TryRead(this, out value)) base.ValidateEditText();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            decimal value;
            if (TryRead(this, out value)) { base.OnLostFocus(e); return; }
            // NumericUpDown's focus handler also calls its private parse path,
            // bypassing ValidateEditText. Preserve the invalid draft and cached
            // value while still raising the normal focus event.
            string draft = Text;
            UserEdit = false;
            try { base.OnLostFocus(e); }
            finally { Text = draft; UserEdit = true; }
        }

        public override void UpButton()
        {
            decimal value;
            if (TryRead(this, out value)) base.UpButton();
        }

        public override void DownButton()
        {
            decimal value;
            if (TryRead(this, out value)) base.DownButton();
        }
    }
}
