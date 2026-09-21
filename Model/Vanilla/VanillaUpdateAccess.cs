using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Private-release access, separate from game profiles and diagnostic logs.</summary>
    internal static class VanillaUpdateAccess
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("4RToolsVanilla private updates v1");
        internal static string TokenPath { get { return Path.Combine(VanillaAppData.RootDirectory, "UpdateAccess", "github-token.bin"); } }
        internal static bool HasSavedToken { get { return File.Exists(TokenPath); } }

        internal static string ValidateToken(string value)
        {
            string token = (value ?? string.Empty).Trim();
            if (token.Length < 8 || token.Length > 4096 || token.Any(c => c < 33 || c > 126))
                throw new InvalidOperationException("GitHub update access token is missing or malformed. Use Data & updates > UPDATE ACCESS.");
            return token;
        }

        internal static void SaveToken(string value)
        {
            byte[] plain = Encoding.UTF8.GetBytes(ValidateToken(value));
            byte[] encrypted;
            try { encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); }
            finally { Array.Clear(plain, 0, plain.Length); }
            string path = TokenPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, encrypted);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static void ClearToken() { if (File.Exists(TokenPath)) File.Delete(TokenPath); }

        internal static Task<string> GetTokenAsync()
        {
            // Resolving gh may involve its credential manager. Keep it off the UI thread;
            // never put a token in command-line arguments, persisted JSON, or exceptions.
            return Task.Run(() => ResolveToken(Environment.GetEnvironmentVariable, ReadSavedToken, ReadCliToken));
        }

        internal static string ResolveToken(Func<string, string> environment, Func<string> saved, Func<string> cli)
        {
            foreach (string name in new[] { "GH_TOKEN", "GITHUB_TOKEN" })
            {
                string configured = environment(name);
                if (!string.IsNullOrWhiteSpace(configured)) return ValidateToken(configured);
            }
            string local = saved();
            if (!string.IsNullOrWhiteSpace(local)) return ValidateToken(local);
            string existing = cli();
            if (!string.IsNullOrWhiteSpace(existing)) return ValidateToken(existing);
            throw new InvalidOperationException("Private updates need GitHub access. Use Data & updates > UPDATE ACCESS to save a token with Contents: Read access to andrasmining/4RToolsVanilla, or sign in with GitHub CLI.");
        }

        internal static string ReadSavedToken()
        {
            string path = TokenPath;
            if (!File.Exists(path)) return null;
            try
            {
                if (new FileInfo(path).Length > 16384) throw new CryptographicException();
                byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
                try { return ValidateToken(Encoding.UTF8.GetString(plain)); }
                finally { Array.Clear(plain, 0, plain.Length); }
            }
            catch (CryptographicException)
            {
                throw new InvalidOperationException("Saved update access belongs to another Windows user/PC or is damaged. Save a new token in Data & updates > UPDATE ACCESS.");
            }
        }

        private static string ReadCliToken()
        {
            var start = new ProcessStartInfo
            {
                FileName = "gh.exe", Arguments = "auth token --hostname github.com",
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            try
            {
                using (var process = Process.Start(start))
                {
                    if (process == null) return null;
                    Task<string> output = process.StandardOutput.ReadToEndAsync();
                    Task<string> error = process.StandardError.ReadToEndAsync();
                    if (!process.WaitForExit(5000))
                    {
                        process.Kill();
                        throw new InvalidOperationException("GitHub credential lookup timed out. Configure UPDATE ACCESS instead.");
                    }
                    Task.WaitAll(output, error);
                    return process.ExitCode == 0 ? output.Result.Trim() : null;
                }
            }
            catch (Win32Exception) { return null; } // CLI is optional for portable installations.
        }
    }
}
