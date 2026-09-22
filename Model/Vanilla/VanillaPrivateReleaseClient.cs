using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Public-first release access with optional API authentication and credential-free CDN redirects.</summary>
    internal sealed class VanillaPrivateReleaseClient
    {
        internal const string Repository = "andrasmining/4RToolsVanilla";
        internal const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
        internal const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const string LatestReleaseWeb = ReleasesUrl + "/latest";
        private const string AssetsPrefix = "https://api.github.com/repos/" + Repository + "/releases/assets/";
        private const string PublicDownloadPrefix = "https://github.com/" + Repository + "/releases/download/";
        private const string PublicTagPathPrefix = "/" + Repository + "/releases/tag/";
        private readonly HttpClient http;
        private readonly Func<Task<string>> credential;
        private readonly bool publicFirst;

        // Explicit private mode preserves the authenticated transport for compatibility.
        // Normal application creation always uses public-first mode below.
        internal VanillaPrivateReleaseClient(HttpClient http, Func<Task<string>> credential, bool publicFirst = false)
        { this.http = http; this.credential = credential; this.publicFirst = publicFirst; }

        internal static VanillaPrivateReleaseClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan };
            return new VanillaPrivateReleaseClient(client, VanillaUpdateAccess.GetTokenAsync, publicFirst: true);
        }

        internal static string AssetUrl(long id)
        {
            if (id <= 0) throw new InvalidDataException("Release asset has no valid API identity.");
            return AssetsPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        internal static string PublicAssetUrl(string tag, string name)
        {
            if (!IsVersionTag(tag)) throw new InvalidDataException("Release tag is not a supported version tag.");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name == "." || name == ".."
                || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0
                || name.IndexOf('?') >= 0 || name.IndexOf('#') >= 0)
                throw new InvalidDataException("Release asset name is unsafe.");
            return PublicDownloadPrefix + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(name);
        }

        internal static bool IsAssetApi(Uri uri)
        {
            if (!IsSecure(uri) || !string.IsNullOrEmpty(uri.Query)) return false;
            string value = uri.AbsoluteUri;
            long id;
            return value.StartsWith(AssetsPrefix, StringComparison.Ordinal) &&
                long.TryParse(value.Substring(AssetsPrefix.Length), System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out id) && id > 0;
        }

        internal static bool IsPublicReleaseAsset(Uri uri)
        {
            if (!IsSecure(uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                || !string.IsNullOrEmpty(uri.Query)) return false;
            string pathPrefix = "/" + Repository + "/releases/download/";
            string path = uri.AbsolutePath;
            if (!path.StartsWith(pathPrefix, StringComparison.Ordinal)) return false;
            string remaining = path.Substring(pathPrefix.Length);
            int slash = remaining.IndexOf('/');
            if (slash <= 0 || slash == remaining.Length - 1 || remaining.IndexOf('/', slash + 1) >= 0) return false;
            string tag = Uri.UnescapeDataString(remaining.Substring(0, slash));
            string name = Uri.UnescapeDataString(remaining.Substring(slash + 1));
            if (!IsVersionTag(tag) || string.IsNullOrWhiteSpace(name) || name == "." || name == ".."
                || name.IndexOf('/') >= 0 || name.IndexOf('\\') >= 0 || name.IndexOf(':') >= 0) return false;
            return true;
        }

        private static bool IsVersionTag(string tag)
        {
            return !string.IsNullOrWhiteSpace(tag) && Regex.IsMatch(tag, "^v[0-9]+\\.[0-9]+\\.[0-9]+$",
                RegexOptions.CultureInvariant);
        }

        private static bool IsSecure(Uri uri)
        {
            return uri != null && uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 &&
                string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment);
        }

        internal static bool IsReleaseCdn(Uri uri)
        {
            if (!IsSecure(uri)) return false;
            return string.Equals(uri.Host, "release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(uri.Host, "github-releases.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }

        internal async Task<string> ReadLatestPublicTagAsync()
        {
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
            using (var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseWeb))
            {
                request.Headers.UserAgent.ParseAdd("4RTools-Vanilla-Updater/" + VanillaUpdater.CurrentVersionText);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
                try
                {
                    using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                    {
                        int status = (int)response.StatusCode;
                        if (status != 301 && status != 302 && status != 303 && status != 307 && status != 308)
                            throw new InvalidOperationException("Public GitHub latest-release redirect was unavailable (HTTP " + status + ").");
                        Uri next = response.Headers.Location;
                        if (next != null && !next.IsAbsoluteUri) next = new Uri(new Uri(LatestReleaseWeb), next);
                        if (!IsSecure(next) || !string.Equals(next.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                            || !string.IsNullOrEmpty(next.Query) || !next.AbsolutePath.StartsWith(PublicTagPathPrefix, StringComparison.Ordinal))
                            throw new InvalidDataException("GitHub latest-release redirect did not identify the canonical repository tag.");
                        string tag = Uri.UnescapeDataString(next.AbsolutePath.Substring(PublicTagPathPrefix.Length));
                        if (tag.IndexOf('/') >= 0 || !IsVersionTag(tag))
                            throw new InvalidDataException("GitHub latest-release redirect contains an unsupported tag.");
                        return tag;
                    }
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("Public GitHub release connection failed. Check your network connection and try again.");
                }
                catch (TaskCanceledException)
                {
                    throw new TimeoutException("Public GitHub latest-release lookup timed out.");
                }
            }
        }

        internal async Task<string> ReadLatestAsync()
        {
            return Encoding.UTF8.GetString(await ReadAsync(new Uri(LatestReleaseApi), false, 4 * 1024 * 1024, true).ConfigureAwait(false));
        }

        internal Task<byte[]> ReadAssetAsync(string url, int maximumBytes)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
                throw new InvalidDataException("Update asset URL is invalid.");
            if (IsPublicReleaseAsset(uri))
                return ReadAsync(uri, true, maximumBytes, false);
            if (IsAssetApi(uri))
                return ReadAsync(uri, true, maximumBytes, true);
            throw new InvalidDataException("Update asset must belong to the configured repository.");
        }

        private async Task<byte[]> ReadAsync(Uri uri, bool asset, int maximumBytes, bool allowAuthentication)
        {
            // A stale/missing DPAPI/CLI credential must not block a public release.
            string token = (!allowAuthentication || publicFirst)
                ? null
                : VanillaUpdateAccess.ValidateToken(await credential().ConfigureAwait(false));
            bool authenticationAttempted = !allowAuthentication || !publicFirst;
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(asset ? 300 : 30)))
            {
                try
                {
                    for (int redirects = 0; redirects <= 4; redirects++)
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Get, uri))
                        {
                            request.Headers.UserAgent.ParseAdd("4RTools-Vanilla-Updater/" + VanillaUpdater.CurrentVersionText);
                            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(asset ? "application/octet-stream" : "application/vnd.github+json"));
                            if (redirects == 0 && token != null)
                            {
                                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                                request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
                            }
                            using (var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
                            {
                                int status = (int)response.StatusCode;
                                if (status == 301 || status == 302 || status == 303 || status == 307 || status == 308)
                                {
                                    Uri next = response.Headers.Location;
                                    if (next != null && !next.IsAbsoluteUri) next = new Uri(uri, next);
                                    if (!asset || redirects == 4 || !IsReleaseCdn(next))
                                        throw new InvalidDataException("GitHub update returned an unsupported download redirect.");
                                    uri = next;
                                    continue;
                                }
                                if ((status == 401 || status == 403 || status == 404) && redirects == 0
                                    && allowAuthentication && !authenticationAttempted)
                                {
                                    authenticationAttempted = true;
                                    try { token = VanillaUpdateAccess.ValidateToken(await credential().ConfigureAwait(false)); }
                                    catch
                                    {
                                        throw new InvalidOperationException("Public GitHub update request failed (HTTP " + status
                                            + "). The repository must have a published release; optional UPDATE ACCESS may help with API rate limits. The installed application was not changed.");
                                    }
                                    redirects--; // One authenticated retry of the original API request, never a CDN.
                                    continue;
                                }
                                if (status == 401 || status == 403 || status == 404)
                                    throw new InvalidOperationException("GitHub update access was refused (HTTP " + status
                                        + "). Check the published release or optional UPDATE ACCESS. The installed application was not changed.");
                                if (status != 200) throw new InvalidOperationException("GitHub update request failed (HTTP " + status + "). Try again later.");
                                if (response.Content.Headers.ContentLength > maximumBytes)
                                    throw new InvalidDataException("GitHub update response exceeds the supported size.");
                                using (var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                using (var destination = new MemoryStream())
                                {
                                    byte[] buffer = new byte[81920];
                                    int count;
                                    while ((count = await source.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false)) != 0)
                                    {
                                        if (destination.Length + count > maximumBytes) throw new InvalidDataException("GitHub update response exceeds the supported size.");
                                        destination.Write(buffer, 0, count);
                                    }
                                    return destination.ToArray();
                                }
                            }
                        }
                    }
                }
                catch (HttpRequestException)
                {
                    throw new InvalidOperationException("GitHub update connection failed. Check your network connection and try again.");
                }
                catch (TaskCanceledException)
                {
                    throw new TimeoutException("GitHub update download timed out. Try again when the connection is available.");
                }
            }
            throw new InvalidDataException("GitHub update redirected too many times.");
        }
    }
}
