using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

// Render the real temporary panel on an isolated non-input desktop. Disable its
// timer before the first message pump; no game enumeration, keyboard or mouse input.
internal static class TemporaryUiHarness
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 2) return 2;
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        Environment.SetEnvironmentVariable("FOURRTOOLS_DATA_ROOT", Path.Combine(output, "isolated-data"));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            Assembly app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
            Type isolated = app.GetType("_4RTools.Model.Vanilla.VanillaIsolatedTestDesktop", true);
            isolated.GetMethod("AssertCurrent", All).Invoke(null, null);
            app.GetType("_4RTools.Model.Vanilla.VanillaAppData", true).GetMethod("InitializeAndMigrateLegacy", All)
                .Invoke(null, new object[] { Path.GetDirectoryName(args[0]) });
            Type profile = app.GetType("_4RTools.Model.ProfileSingleton", true);
            profile.GetMethod("Create", All).Invoke(null, new object[] { "Default" });
            app.GetType("_4RTools.Program", true).GetMethod("LoadStockClients", All).Invoke(null, null);
            using (var main = (Form)Activator.CreateInstance(app.GetType("_4RTools.Forms.Container", true), new object[] { true }))
            {
                main.Show(); Application.DoEvents();
                var panelType = app.GetType("_4RTools.Model.Vanilla.VanillaTemporaryActionsPanel", true);
                using (var panel = (Control)Activator.CreateInstance(panelType, All, null,
                    new object[] { Path.GetDirectoryName(args[0]), Field(main, "integratedFleetMonitor"), Field(main, "integratedReconnectSupervisor") }, null))
                {
                    ((Timer)Field(panel, "timer")).Stop(); // No message pump has run since construction.
                    var workspace = (TabControl)Field(main, "vanillaWorkspace");
                    workspace.Enabled = true;
                    TabPage page = workspace.TabPages.Cast<TabPage>().Single(p => p.Text == "Temporary actions");
                    page.Controls.Add(panel); workspace.SelectedTab = page;
                    foreach (int width in new[] { 1600, 1050 })
                    {
                        main.WindowState = FormWindowState.Normal; main.ClientSize = new Size(width, 900);
                        main.PerformLayout(); Application.DoEvents();
                        foreach (string name in new[] { "actionKey", "sitKey" })
                        {
                            var box = (TextBox)Field(panel, name);
                            box.GetType().GetMethod("Set", All).Invoke(box, new object[] { (int)Keys.D3, false, true, true, false });
                            Require(box.Text == "Alt+Shift+D3" && box.Visible, "Recorded chord was not retained: " + name);
                            Require(main.RectangleToScreen(main.ClientRectangle).Contains(box.RectangleToScreen(box.ClientRectangle)), "Hotkey box is clipped.");
                        }
                        foreach (string caption in new[] { "CAPTURE TARGET (3s)", "CAPTURE REST GROUND (3s)", "START TEMPORARY ACTION", "STOP" })
                        {
                            Button button = Descendants(panel).OfType<Button>().SingleOrDefault(b => b.Text == caption);
                            Require(button != null && button.Visible && main.RectangleToScreen(main.ClientRectangle).Contains(button.RectangleToScreen(button.ClientRectangle)), "Clipped control: " + caption);
                        }
                        Require(ReferenceEquals(Field(panel, "supervisor"), Field(main, "integratedReconnectSupervisor")), "Input ownership is not shared.");
                        Require(ReferenceEquals(Field(panel, "fleet"), Field(main, "integratedFleetMonitor")), "Fleet identity is not shared.");
                        main.GetType().GetMethod("AssertSmokeBackgroundServicesInactive", All).Invoke(main, null);
                        using (var bitmap = new Bitmap(main.Width, main.Height))
                        { main.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(Path.Combine(output, "temporary-" + width + ".png"), ImageFormat.Png); }
                    }
                }
            }
            File.WriteAllText(Path.Combine(output, "temporary-report.txt"), "Failures: 0\nTwo viewport sizes; recorded chords; captures; shared ownership; no live polling/input.\n");
            return 0;
        }
        catch (Exception failure) { File.WriteAllText(Path.Combine(output, "temporary-report.txt"), failure.ToString()); return 1; }
    }
    private static object Field(object obj, string name) { return obj.GetType().GetField(name, All).GetValue(obj); }
    private static IEnumerable<Control> Descendants(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
