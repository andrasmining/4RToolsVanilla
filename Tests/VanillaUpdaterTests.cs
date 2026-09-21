using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using _4RTools.Model.Vanilla;

namespace Vanilla.Diagnostics.Tests
{
    internal static class VanillaUpdaterTests
    {
        private static int passed, failed;
        private const string Token = "test_update_token_not_a_credential";
        internal static int Run()
        {
            Test("Private release selects exact completed assets by API identity", ReleaseAssets);
            Test("Draft, prerelease, missing and duplicate assets are refused", InvalidReleases);
            Test("Private metadata is authenticated without Actions", Metadata);
            Test("Private assets accept direct binary responses", DirectAsset);
            Test("Signed CDN redirect receives no GitHub credential", CdnRedirect);
            Test("Foreign, insecure and credential-bearing redirects are refused", RejectedRedirects);
            Test("Metadata redirects and redirect loops are refused", RedirectBounds);
            Test("Asset origin is checked before resolving credentials", AssetOrigins);
            Test("Denied access and network errors cannot echo credentials", SafeErrors);
            Test("Oversized release responses are refused", ResponseBounds);
            Test("Credentials use explicit, saved, then CLI access", CredentialPriority);
            Test("Malformed credentials never reach HTTP", MalformedCredentials);
            Test("Saved update token is DPAPI encrypted and replaceable", SavedToken);
            Test("ZIP extraction refuses traversal, alternate streams and expansion bombs", ZipSafety);
            Console.WriteLine("Private updater: {0} passed; {1} failed. Fake HTTP and isolated DPAPI storage only.", passed, failed);
            return failed;
        }

        private static JObject Release()
        {
            return new JObject
            {
                ["tag_name"] = "v0.6.68", ["draft"] = false, ["prerelease"] = false,
                ["html_url"] = "https://untrusted.example/release",
                ["assets"] = new JArray(
                    new JObject { ["name"] = "4RTools-Vanilla-v0.6.68-portable.zip", ["id"] = 123, ["state"] = "uploaded", ["browser_download_url"] = "https://untrusted.example/zip" },
                    new JObject { ["name"] = "4RTools-Vanilla-v0.6.68-portable.zip.sha256", ["id"] = 124, ["state"] = "uploaded" })
            };
        }

        private static void ReleaseAssets()
        {
            var info = VanillaUpdater.ParseRelease(Release().ToString());
            Assert(info.Version == new Version(0, 6, 68) && info.ZipName == "4RTools-Vanilla-v0.6.68-portable.zip");
            Assert(info.ZipUrl == VanillaPrivateReleaseClient.AssetUrl(123) && info.ChecksumUrl == VanillaPrivateReleaseClient.AssetUrl(124));
            Assert(info.ReleaseUrl == VanillaUpdater.ReleasesUrl + "/tag/v0.6.68");
        }

        private static void InvalidReleases()
        {
            foreach (string field in new[] { "draft", "prerelease" })
            { var r = Release(); r[field] = true; Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(r.ToString())); }
            var missing = Release(); ((JArray)missing["assets"]).RemoveAt(0);
            Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(missing.ToString()));
            var duplicate = Release(); ((JArray)duplicate["assets"]).Add(duplicate["assets"][0].DeepClone());
            Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(duplicate.ToString()));
            var unfinished = Release(); unfinished["assets"][0]["state"] = "new";
            Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(unfinished.ToString()));
            var invalidId = Release(); invalidId["assets"][0]["id"] = 0;
            Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(invalidId.ToString()));
            foreach (string tag in new[] { "v0.6", "v0.6.68.0", "v0.6.68.1", "vv0.6.68", "v0.6.68-beta" })
            { var r = Release(); r["tag_name"] = tag; Throws<InvalidDataException>(() => VanillaUpdater.ParseRelease(r.ToString())); }
        }

        private static void Metadata()
        {
            using (var h = new Harness((request, index) =>
            {
                Assert(index == 0 && request.RequestUri.AbsoluteUri == VanillaPrivateReleaseClient.LatestReleaseApi);
                Assert(request.Headers.Authorization.Scheme == "Bearer" && request.Headers.Authorization.Parameter == Token);
                Assert(request.Headers.Accept.Single().MediaType == "application/vnd.github+json");
                return Ok(Encoding.UTF8.GetBytes(Release().ToString()));
            }))
            { Assert(h.Client.ReadLatestAsync().GetAwaiter().GetResult().Contains("v0.6.68")); Assert(h.Calls == 1); }
        }

        private static void DirectAsset()
        {
            using (var h = new Harness((request, index) =>
            {
                Assert(request.Headers.Authorization.Parameter == Token);
                Assert(request.Headers.Accept.Single().MediaType == "application/octet-stream");
                return Ok(new byte[] { 1, 2, 3 });
            })) Assert(h.Read().SequenceEqual(new byte[] { 1, 2, 3 }));
        }

        private static void CdnRedirect()
        {
            using (var h = new Harness((request, index) =>
            {
                if (index == 0) { Assert(request.Headers.Authorization.Parameter == Token); return Redirect("https://release-assets.githubusercontent.com/private.zip?signature=synthetic"); }
                Assert(request.Headers.Authorization == null && !request.Headers.Contains("X-GitHub-Api-Version"));
                Assert(request.RequestUri.Host == "release-assets.githubusercontent.com");
                return Ok(new byte[] { 3, 4 });
            })) { Assert(h.Read().Length == 2 && h.Calls == 2); }
        }

        private static void RejectedRedirects()
        {
            foreach (string target in new[]
            {
                "https://evil.example/a", "http://release-assets.githubusercontent.com/a", "https://release-assets.githubusercontent.com.evil.example/a",
                "https://user:secret@release-assets.githubusercontent.com/a", "https://release-assets.githubusercontent.com:444/a",
                "https://api.github.com/repos/other/repo/releases/assets/1", "https://github.com/login", "/unexpected"
            })
                using (var h = new Harness((request, index) => Redirect(target)))
                { Throws<InvalidDataException>(() => h.Read()); Assert(h.Calls == 1); }
        }

        private static void RedirectBounds()
        {
            using (var h = new Harness((request, index) => Redirect("https://release-assets.githubusercontent.com/a")))
            { Throws<InvalidDataException>(() => h.Client.ReadLatestAsync().GetAwaiter().GetResult()); Assert(h.Calls == 1); }
            using (var h = new Harness((request, index) => Redirect("https://release-assets.githubusercontent.com/a")))
            { Throws<InvalidDataException>(() => h.Read()); Assert(h.Calls == 5); }
        }

        private static void AssetOrigins()
        {
            foreach (string address in new[]
            {
                "https://api.github.com/repos/andrasmining/4RTools/releases/assets/1", "https://api.github.com/repos/andrasmining/4RToolsVanilla/releases/assets/1?x=1",
                "https://api.github.com.evil.example/repos/andrasmining/4RToolsVanilla/releases/assets/1", "http://api.github.com/repos/andrasmining/4RToolsVanilla/releases/assets/1",
                "https://user@api.github.com/repos/andrasmining/4RToolsVanilla/releases/assets/1", "https://api.github.com/repos/andrasmining/4RToolsVanilla/releases/assets/-1"
            })
                using (var h = new Harness((request, index) => Ok(new byte[0])))
                { Throws<InvalidDataException>(() => h.Client.ReadAssetAsync(address, 100).GetAwaiter().GetResult()); Assert(h.Calls == 0 && h.CredentialCalls == 0); }
        }

        private static void SafeErrors()
        {
            foreach (int status in new[] { 401, 403, 404, 500 })
                using (var h = new Harness((request, index) => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(Token) }))
                {
                    var error = Throws<InvalidOperationException>(() => h.Read());
                    Assert(!error.ToString().Contains(Token) && error.Message.Contains(status.ToString()));
                }
            using (var h = new Harness((request, index) => { throw new HttpRequestException(Token); }))
            { Assert(!Throws<InvalidOperationException>(() => h.Read()).ToString().Contains(Token)); }
        }

        private static void ResponseBounds()
        {
            using (var h = new Harness((request, index) => Ok(new byte[1025])))
                Throws<InvalidDataException>(() => h.Client.ReadAssetAsync(VanillaPrivateReleaseClient.AssetUrl(123), 1024).GetAwaiter().GetResult());
        }

        private static void CredentialPriority()
        {
            Func<string> forbidden = () => { throw new Exception("Unexpected credential fallback."); };
            Assert(VanillaUpdateAccess.ResolveToken(n => n == "GH_TOKEN" ? Token : null, forbidden, forbidden) == Token);
            Assert(VanillaUpdateAccess.ResolveToken(n => n == "GITHUB_TOKEN" ? Token : null, forbidden, forbidden) == Token);
            Assert(VanillaUpdateAccess.ResolveToken(n => null, () => Token, forbidden) == Token);
            Assert(VanillaUpdateAccess.ResolveToken(n => null, () => null, () => Token) == Token);
            Assert(Throws<InvalidOperationException>(() => VanillaUpdateAccess.ResolveToken(n => null, () => null, () => null)).Message.Contains("UPDATE ACCESS"));
        }

        private static void MalformedCredentials()
        {
            foreach (string token in new[] { "", "short", "token\r\nAuthorization: bad", new string('x', 4097) })
            {
                using (var http = new HttpClient(new Handler((r, i) => { throw new Exception("Malformed credential reached HTTP."); })))
                {
                    var client = new VanillaPrivateReleaseClient(http, () => Task.FromResult(token));
                    Throws<InvalidOperationException>(() => client.ReadLatestAsync().GetAwaiter().GetResult());
                }
            }
        }

        private static void SavedToken()
        {
            string previous = Environment.GetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable);
            string directory = Path.Combine(Path.GetTempPath(), "4RToolsUpdaterTests-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, directory);
            try
            {
                VanillaUpdateAccess.SaveToken(Token);
                Assert(VanillaUpdateAccess.HasSavedToken && VanillaUpdateAccess.ReadSavedToken() == Token);
                Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(VanillaUpdateAccess.TokenPath)).Contains(Token));
                VanillaUpdateAccess.SaveToken(Token + "_replacement");
                Assert(VanillaUpdateAccess.ReadSavedToken() == Token + "_replacement");
                File.WriteAllBytes(VanillaUpdateAccess.TokenPath, new byte[] { 1, 2, 3 });
                Assert(Throws<InvalidOperationException>(() => VanillaUpdateAccess.ReadSavedToken()).Message.Contains("UPDATE ACCESS"));
                VanillaUpdateAccess.ClearToken(); Assert(!VanillaUpdateAccess.HasSavedToken);
            }
            finally { Environment.SetEnvironmentVariable(VanillaAppData.DataRootEnvironmentVariable, previous); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        private static void ZipSafety()
        {
            string directory = Path.Combine(Path.GetTempPath(), "4RToolsUpdaterZipTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                foreach (string name in new[] { "../escape.txt", "inside/file.txt:stream", "/rooted.txt" })
                {
                    string archive = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".zip");
                    using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
                    using (var writer = new StreamWriter(zip.CreateEntry(name).Open())) writer.Write("test");
                    Throws<InvalidDataException>(() => VanillaUpdater.SafeExtract(archive, Path.Combine(directory, "payload")));
                }
                string bomb = Path.Combine(directory, "declared-size.zip");
                using (var zip = ZipFile.Open(bomb, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("file.txt").Open())) writer.Write("x");
                // Mutate only the central-directory expanded size. The reader must reject
                // it before opening/decompressing any content or allocating its declared size.
                byte[] bytes = File.ReadAllBytes(bomb);
                for (int i = 0; i <= bytes.Length - 46; i++)
                    if (bytes[i] == 0x50 && bytes[i + 1] == 0x4b && bytes[i + 2] == 1 && bytes[i + 3] == 2)
                    { Array.Copy(BitConverter.GetBytes(600 * 1024 * 1024), 0, bytes, i + 24, 4); break; }
                File.WriteAllBytes(bomb, bytes);
                Throws<InvalidDataException>(() => VanillaUpdater.SafeExtract(bomb, Path.Combine(directory, "payload")));
                string valid = Path.Combine(directory, "valid.zip");
                using (var zip = ZipFile.Open(valid, ZipArchiveMode.Create))
                using (var writer = new StreamWriter(zip.CreateEntry("payload/readme.txt").Open())) writer.Write("verified");
                VanillaUpdater.SafeExtract(valid, Path.Combine(directory, "output"));
                Assert(File.ReadAllText(Path.Combine(directory, "output", "payload", "readme.txt")) == "verified");
            }
            finally { Directory.Delete(directory, true); }
        }

        private static HttpResponseMessage Ok(byte[] bytes) { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; }
        private static HttpResponseMessage Redirect(string uri)
        { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(uri, UriKind.RelativeOrAbsolute); return response; }
        private sealed class Handler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, int, HttpResponseMessage> respond;
            internal int Calls;
            internal Handler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) { this.respond = respond; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            { return Task.FromResult(respond(request, Calls++)); }
        }
        private sealed class Harness : IDisposable
        {
            private readonly Handler handler;
            private readonly HttpClient http;
            internal readonly VanillaPrivateReleaseClient Client;
            internal int CredentialCalls;
            internal int Calls { get { return handler.Calls; } }
            internal Harness(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
            {
                handler = new Handler(respond); http = new HttpClient(handler);
                Client = new VanillaPrivateReleaseClient(http, () => { CredentialCalls++; return Task.FromResult(Token); });
            }
            internal byte[] Read() { return Client.ReadAssetAsync(VanillaPrivateReleaseClient.AssetUrl(123), 1024).GetAwaiter().GetResult(); }
            public void Dispose() { http.Dispose(); }
        }
        private static T Throws<T>(Action action) where T : Exception
        { try { action(); } catch (T ex) { return ex; } throw new Exception("Expected " + typeof(T).Name); }
        private static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
        private static void Test(string name, Action action)
        { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
    }
}
