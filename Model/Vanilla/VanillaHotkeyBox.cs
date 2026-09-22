using System;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Capture a key chord by pressing it; no restricted function-key dropdown.</summary>
    internal sealed class VanillaHotkeyBox : TextBox
    {
        internal int Key { get; private set; }
        internal bool Ctrl { get; private set; }
        internal bool Alt { get; private set; }
        internal bool Shift { get; private set; }
        internal event EventHandler ChordChanged;
        internal VanillaHotkeyBox()
        {
            ReadOnly = true; ShortcutsEnabled = false; Width = 210;
            var menu = new ContextMenuStrip();
            menu.Items.Add("Clear", null, (s, e) => Set(0, false, false, false, true));
            ContextMenuStrip = menu;
            Set(0, false, false, false);
        }
        internal void Set(int key, bool ctrl, bool alt, bool shift, bool notify = false)
        {
            Key = key; Ctrl = ctrl; Alt = alt; Shift = shift;
            Text = key == 0 ? "Press a hotkey here" : (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "")
                + (shift ? "Shift+" : "") + ((Keys)key).ToString();
            if (notify) ChordChanged?.Invoke(this, EventArgs.Empty);
        }
        internal static bool IsMainKey(Keys key)
        {
            return (int)key >= 8 && (int)key <= 254 && key != Keys.ShiftKey && key != Keys.ControlKey && key != Keys.Menu
                && key != Keys.LShiftKey && key != Keys.RShiftKey && key != Keys.LControlKey && key != Keys.RControlKey
                && key != Keys.LMenu && key != Keys.RMenu && key != Keys.LWin && key != Keys.RWin;
        }
        protected override bool IsInputKey(Keys keyData) { return true; }
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Capture(keyData); return true;
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            Capture(e.KeyData); e.Handled = e.SuppressKeyPress = true;
        }
        private void Capture(Keys data)
        {
            Keys key = data & Keys.KeyCode;
            if (IsMainKey(key)) Set((int)key, (data & Keys.Control) != 0, (data & Keys.Alt) != 0, (data & Keys.Shift) != 0, true);
        }
    }
}
