using System;
using System.IO;
using System.Reflection;

// Test host has no administrator manifest. It loads the extracted portable payload,
// preserving its own base directory for the real production initialization/OCR code.
internal static class PortableSmokeHost
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new ArgumentException("Expected only the smoke report path.");
            Assembly app = Assembly.LoadFrom(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "4RTools-Vanilla.exe"));
            app.GetType("_4RTools.Model.Vanilla.VanillaIsolatedTestDesktop", true)
                .GetMethod("AssertCurrent", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            app.EntryPoint.Invoke(null, new object[] { new[] { "--portable-smoke-test", "--output", args[0] } });
            return Environment.ExitCode;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
