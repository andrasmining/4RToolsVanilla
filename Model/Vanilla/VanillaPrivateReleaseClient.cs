using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace _4RTools.Model.Vanilla
{
    /// <summary>Authenticated API access with explicit, credential-free CDN redirects.</summary>
    internal sealed class VanillaPrivateReleaseClient
    {
        internal const string Repository = "andrasmining/4RToolsVanilla";
        internal const string ReleasesUrl = "https://github.com/" + Repository + "/releases";
        internal const string LatestReleaseApi = "https://api.github.com/repos/" + Repository + "/releases/latest";
        private const string AssetsPrefix = "https://api.github.com/repos/" + Repository + "/releases/assets/";
        private readonly HttpClient http;
        private readonly Func<Task<string>> credential;

        internal VanillaPrivateReleaseClient(HttpClient http, Func<Task<string>> credential)
        { this.http = http; this.credential = credential; }

        internal static VanillaPrivateReleaseClient Create()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
            { Timeout = Timeout.InfiniteTimeSpan };
            return new VanillaPrivateReleaseClient(client, VanillaUpdateAccess.GetTokenAsync);
        }

        internal static string AssetUrl(long id)
        {
            if (id <= 0) throw new InvalidDataException("Release asset has no valid API identity.");
            return AssetsPrefix + id.ToString(System.Globalization.CultureInfo.InvariantCulture);
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

        internal async Task<string> ReadLatestAsync()
        {
            return Encoding.UTF8.GetString(await ReadAsync(new Uri(LatestReleaseApi), false, 4 * 1024 * 1024).ConfigureAwait(false));
        }

        internal Task<byte[]> ReadAssetAsync(string apiUrl, int maximumBytes)
        {
            Uri uri;
            if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out uri) || !IsAssetApi(uri))
                throw new InvalidDataException("Update asset must belong to the configured private repository API.");
            return ReadAsync(uri, true, maximumBytes);
        }

        private async Task<byte[]> ReadAsync(Uri uri, bool asset, int maximumBytes)
        {
            string token = VanillaUpdateAccess.ValidateToken(await credential().ConfigureAwait(false));
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
                            if (redirects == 0)
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
                                if (status == 401 || status == 403 || status == 404)
                                    throw new InvalidOperationException("Private update access was refused (HTTP " + status + "). Check UPDATE ACCESS and Contents: Read permission for andrasmining/4RToolsVanilla; a published release must exist.");
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
                catch (HttpRequestException) { throw new InvalidOperationException("GitHub update connection failed. Check your network connection and try again."); }
                catch (TaskCanceledException) { throw new TimeoutException("GitHub update download timed out. Try again when the connection is available."); }
            }
            throw new InvalidDataException("GitHub update redirected too many times.");
        }
    }
}
