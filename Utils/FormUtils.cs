using System;
using System.Collections.Generic;
using System.Data.Linq;
using System.Linq;
using System.Windows.Forms;
using System.Windows.Input;
using static System.Windows.Forms.Control;

namespace _4RTools.Utils
{
    public class FormUtils
    {
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Control, LoadingState> loading =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Control, LoadingState>();

        // Profile rendering must never be mistaken for a user edit by TextChanged/ValueChanged.
        public static IDisposable BeginLoading(Control control)
        {
            var state = loading.GetValue(control, owner => new LoadingState());
            state.Depth++;
            return new LoadingScope(state);
        }

        public static bool IsLoading(Control control)
        {
            for (Control current = control; current != null; current = current.Parent)
            {
                LoadingState state;
                if (loading.TryGetValue(current, out state) && state.Depth > 0) return true;
            }
            return false;
        }

        private sealed class LoadingState { public int Depth; }
        private sealed class LoadingScope : IDisposable
        {
            private LoadingState state;
            public LoadingScope(LoadingState state) { this.state = state; }
            public void Dispose()
            {
                if (state == null) return;
                state.Depth--;
                state = null;
            }
        }

        // Embedded stock forms are disposed directly when their host closes. WinForms
        // does not validate the active NumericUpDown editor first in that path.
        public static void CommitNumericEditsOnClose(Form owner)
        {
            Form connected = null;
            FormClosingEventHandler flush = (sender, args) =>
            {
                if (owner.IsDisposed || IsLoading(owner)) return;
                foreach (NumericUpDown number in GetAll(owner, typeof(NumericUpDown)))
                {
                    decimal value;
                    if (decimal.TryParse(number.Text, System.Globalization.NumberStyles.Number,
                            System.Globalization.CultureInfo.CurrentCulture, out value)
                        && value >= number.Minimum && value <= number.Maximum
                        && decimal.Round(value, number.DecimalPlaces) == value)
                        number.Value = value;
                }
            };
            EventHandler connect = (sender, args) =>
            {
                if (owner.IsDisposed) return;
                Form host = owner.TopLevelControl as Form ?? owner;
                if (ReferenceEquals(host, connected)) return;
                if (connected != null) connected.FormClosing -= flush;
                connected = host;
                connected.FormClosing += flush;
            };
            owner.ParentChanged += connect;
            owner.HandleCreated += connect;
            owner.Disposed += (sender, args) => { if (connected != null) connected.FormClosing -= flush; };
            connect(owner, EventArgs.Empty);
        }

        public static void OnKeyDown(object sender, System.Windows.Forms.KeyEventArgs e)
        {
            try
            {
                TextBox textBox = (TextBox)sender;
                Key thisk = (Key)Enum.Parse(typeof(Key), e.KeyCode.ToString());

                switch (thisk)
                {
                    case Key.Escape: case Key.Back:
                        textBox.Text = Key.None.ToString();
                        break;
                    default:
                        textBox.Text = e.KeyCode.ToString();
                        break;
                }
                textBox.Parent.Focus();
                e.Handled = true;
            }
            catch { }
        }

        public static bool IsValidKey(Key key)
        {
            return (key != Key.Back && key != Key.Escape && key != Key.None);
        }

        public static void OnKeyPress(object sender, KeyPressEventArgs e)
        {
            e.Handled = true;
        }

        public static IEnumerable<Control> GetAll(Control control, Type type)
        {
            var controls = control.Controls.Cast<Control>();

            return controls.SelectMany(ctrl => GetAll(ctrl, type))
                                      .Concat(controls)
                                      .Where(c => c.GetType() == type);
        }

        private static void resetForm(Control control)
        {

            IEnumerable<Control> texts = GetAll(control, typeof(TextBox));
            IEnumerable<Control> checks = GetAll(control, typeof(CheckBox));
            IEnumerable<Control> combos = GetAll(control, typeof(ComboBox));

            foreach(Control c in texts)
            {
                TextBox textBox = (TextBox)c;
                textBox.Text = Key.None.ToString();
            }

            foreach (Control c in checks)
            {
                CheckBox checkBox = (CheckBox)c;
                checkBox.Checked = false;
            }

            foreach (Control c in combos)
            {
                ComboBox comboBox = (ComboBox)c;
                if (comboBox.Items.Count > 0)
                    comboBox.SelectedIndex = 0;
            }
        }

        public static void ResetForm(Form form)
        {
            resetForm(form);
        }

        public static void ResetForm(Control form)
        {
            resetForm(form);
        }

        public static void ResetForm(Panel panel)
        {
            resetForm(panel);
        }

        public static void ResetForm(GroupBox group)
        {
            resetForm(group);
        }
    }
}
