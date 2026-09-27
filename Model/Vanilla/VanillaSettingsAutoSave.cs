using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace _4RTools.Model.Vanilla
{
    // UI-thread scheduling only. Each editor owns validation, persistence and
    // inline error reporting; loading must never schedule a write.
    internal sealed class VanillaSettingsAutoSave : IDisposable
    {
        private readonly Timer timer;
        private readonly System.Action<Control> save;
        private readonly List<Control> pending = new List<Control>();
        private bool disposed;

        internal VanillaSettingsAutoSave(int delayMilliseconds, System.Action<Control> save)
        {
            if (delayMilliseconds < 1) throw new ArgumentOutOfRangeException(nameof(delayMilliseconds));
            this.save = save ?? throw new ArgumentNullException(nameof(save));
            timer = new Timer { Interval = delayMilliseconds };
            timer.Tick += (s, e) => Flush();
        }

        internal bool HasPending { get { return pending.Count != 0; } }

        internal void Schedule(Control control)
        {
            if (control == null) throw new ArgumentNullException(nameof(control));
            if (disposed || control.IsDisposed) return;
            if (!pending.Contains(control)) pending.Add(control);
            timer.Stop(); timer.Start();
        }

        internal void Flush(Control control)
        {
            if (disposed || !pending.Remove(control)) return;
            if (pending.Count == 0) timer.Stop();
            if (!control.IsDisposed) save(control);
        }

        internal void Flush()
        {
            if (disposed) return;
            timer.Stop();
            Control[] edits = pending.ToArray();
            pending.Clear();
            foreach (Control control in edits)
            {
                if (disposed) break;
                if (!control.IsDisposed) save(control);
            }
        }

        internal void Cancel(Control control)
        {
            pending.Remove(control);
            if (!disposed && pending.Count == 0) timer.Stop();
        }

        internal void Cancel()
        {
            pending.Clear();
            if (!disposed) timer.Stop();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            pending.Clear();
            timer.Stop(); timer.Dispose();
        }
    }
}
