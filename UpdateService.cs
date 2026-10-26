using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NetworkRescue
{
    public sealed class UpdateCheckResult
    {
        public string Status, CurrentVersion, LatestVersion, ReleaseUrl, ErrorCode, Repository;
    }

    public static class UpdateService
    {
        internal const string DefaultRepository = "shangguanyiyu0011/NetworkRescue";
        private const int MaximumBodyBytes = 512 * 1024;
        private const int TimeoutMilliseconds = 8000;

        internal static string RepositoryOrDefault(string repository) { return String.IsNullOrWhiteSpace(repository) ? DefaultRepository : repository.Trim(); }

        public static bool TryNormalizeRepository(string input, out string repository)
        {
            repository = null;
            if (String.IsNullOrWhiteSpace(input)) return false;
            string value = input.Trim();
            string[] parts = value.Split('/');
            if (parts.Length != 2 || parts[0].Length > 39 || parts[1].Length > 100 || value.Contains("..")) return false;
            if (!Regex.IsMatch(parts[0], @"\A[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?\z") || parts[0].Contains("--")) return false;
            if (!Regex.IsMatch(parts[1], @"\A[A-Za-z0-9_.-]+\z") || parts[1] == ".") return false;
            repository = value;
            return true;
        }

        public static Task<UpdateCheckResult> CheckAsync(string repository, string currentVersion, CancellationToken token)
        {
            return CheckCoreAsync(repository, currentVersion, token, null);
        }

        public static bool IsAllowedReleaseUrl(string repository, string releaseUrl)
        {
            string normalized;
            if (!TryNormalizeRepository(repository, out normalized) || String.IsNullOrEmpty(releaseUrl)) return false;
            string prefix = "https://github.com/" + normalized + "/releases/tag/";
            if (!releaseUrl.StartsWith(prefix, StringComparison.Ordinal)) return false;
            string tag;
            try { tag = Uri.UnescapeDataString(releaseUrl.Substring(prefix.Length)); }
            catch (UriFormatException) { return false; }
            SemanticVersion version;
            return SemanticVersion.TryParse(tag, out version) && version.Prerelease == null && releaseUrl == ReleaseUrl(normalized, tag);
        }

        // The public entry point always constructs the fixed GitHub API URL; tests use loopback only.
        internal static Task<UpdateCheckResult> CheckLoopbackAsync(string repository, string currentVersion, CancellationToken token, Uri endpoint)
        {
            if (endpoint == null || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ArgumentException("Loopback fixture required.", "endpoint");
            return CheckCoreAsync(repository, currentVersion, token, endpoint);
        }

        private static async Task<UpdateCheckResult> CheckCoreAsync(string input, string currentVersion, CancellationToken token, Uri fixture)
        {
            token.ThrowIfCancellationRequested();
            UpdateCheckResult result = new UpdateCheckResult { Status = "CheckFailed", CurrentVersion = currentVersion };
            if (String.IsNullOrWhiteSpace(input)) { result.Status = "NotConfigured"; return result; }
            string repository;
            if (!TryNormalizeRepository(input, out repository)) { result.ErrorCode = "InvalidRepository"; return result; }
            result.Repository = repository;
            SemanticVersion current;
            if (!SemanticVersion.TryParse(currentVersion, out current)) { result.ErrorCode = "InvalidCurrentVersion"; return result; }
            Uri endpoint = fixture ?? new Uri("https://api.github.com/repos/" + repository + "/releases/latest");
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "GET";
            request.Accept = "application/vnd.github+json";
            request.UserAgent = "NetworkRescue-UpdateCheck/1.0";
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            request.AllowAutoRedirect = false;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.PreAuthenticate = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            request.Timeout = TimeoutMilliseconds;
            request.ReadWriteTimeout = TimeoutMilliseconds;
            request.Proxy = fixture == null ? WebRequest.GetSystemWebProxy() : null;
            if (request.Proxy != null) request.Proxy.Credentials = null;
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (deadline.Token.Register(request.Abort))
            {
                deadline.CancelAfter(TimeoutMilliseconds);
                Task<UpdateCheckResult> operation = ReadReleaseAsync(request, result, current, deadline.Token);
                if (await Task.WhenAny(operation, Task.Delay(Timeout.Infinite, deadline.Token)).ConfigureAwait(false) != operation)
                {
                    Observe(operation);
                    token.ThrowIfCancellationRequested();
                    return new UpdateCheckResult { Status = "CheckFailed", CurrentVersion = currentVersion, Repository = repository, ErrorCode = "Timeout" };
                }
                try { result = await operation.ConfigureAwait(false); }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); result.ErrorCode = "Timeout"; }
                catch (WebException ex) { result.ErrorCode = deadline.IsCancellationRequested ? "Timeout" : "Network" + ex.Status; }
                catch (IOException) { result.ErrorCode = deadline.IsCancellationRequested ? "Timeout" : "NetworkError"; }
                catch (XmlException) { result.ErrorCode = "InvalidRelease"; }
                catch (ArgumentException) { result.ErrorCode = "InvalidRelease"; }
                catch (InvalidDataException ex) { result.ErrorCode = ex.Message == "ResponseTooLarge" ? "ResponseTooLarge" : "InvalidRelease"; }
                token.ThrowIfCancellationRequested();
                return result;
            }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static async Task<UpdateCheckResult> ReadReleaseAsync(HttpWebRequest request, UpdateCheckResult result, SemanticVersion current, CancellationToken token)
        {
            HttpWebResponse response;
            try { response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false); }
            catch (WebException ex)
            {
                response = ex.Response as HttpWebResponse;
                if (response == null) throw;
            }
            using (response)
            {
                int status = (int)response.StatusCode;
                if (status == 404) { result.Status = "NoPublishedRelease"; return result; }
                if (status == 429 || (status == 403 && (response.Headers["X-RateLimit-Remaining"] == "0" || response.Headers["Retry-After"] != null)))
                { result.Status = "RateLimited"; result.ErrorCode = "RateLimitExceeded"; return result; }
                if (status != 200) { result.ErrorCode = status == 403 ? "Forbidden" : "Http" + status.ToString(CultureInfo.InvariantCulture); return result; }
                if (response.ContentLength > MaximumBodyBytes) throw new InvalidDataException("ResponseTooLarge");
                using (Stream stream = response.GetResponseStream())
                using (MemoryStream body = new MemoryStream())
                {
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                    {
                        if (body.Length + read > MaximumBodyBytes) throw new InvalidDataException("ResponseTooLarge");
                        body.Write(buffer, 0, read);
                    }
                    token.ThrowIfCancellationRequested();
                    string tag = ParseReleaseTag(body.ToArray());
                    SemanticVersion latest;
                    if (!SemanticVersion.TryParse(tag, out latest) || latest.Prerelease != null) throw new InvalidDataException();
                    result.LatestVersion = tag;
                    result.Status = latest.CompareTo(current) > 0 ? "UpdateAvailable" : "UpToDate";
                    result.ReleaseUrl = ReleaseUrl(result.Repository, tag);
                    return result;
                }
            }
        }

        private static string ReleaseUrl(string repository, string tag)
        {
            return "https://github.com/" + repository + "/releases/tag/" + Uri.EscapeDataString(tag);
        }

        internal static string ParseReleaseTag(byte[] body)
        {
            if (body == null || body.Length > MaximumBodyBytes) throw new InvalidDataException("ResponseTooLarge");
            // shortcut: the Framework reader tolerates trailing commas; use a stricter reader if exact JSON syntax becomes a requirement.
            XmlDictionaryReaderQuotas quotas = new XmlDictionaryReaderQuotas { MaxDepth = 16, MaxStringContentLength = MaximumBodyBytes, MaxArrayLength = MaximumBodyBytes, MaxBytesPerRead = 4096, MaxNameTableCharCount = 16384 };
            XmlDocument document = new XmlDocument { XmlResolver = null };
            using (XmlDictionaryReader reader = JsonReaderWriterFactory.CreateJsonReader(body, 0, body.Length, new UTF8Encoding(false, true), quotas, null)) document.Load(reader);
            XmlElement root = document.DocumentElement;
            if (root == null || root.GetAttribute("type") != "object") throw new InvalidDataException();
            if (Field(root, "draft", "boolean") != "false" || Field(root, "prerelease", "boolean") != "false") throw new InvalidDataException();
            return Field(root, "tag_name", "string");
        }

        private static string Field(XmlElement root, string name, string type)
        {
            XmlNodeList nodes = root.SelectNodes(name);
            if (nodes.Count != 1 || ((XmlElement)nodes[0]).GetAttribute("type") != type) throw new InvalidDataException();
            return nodes[0].InnerText;
        }

        private sealed class SemanticVersion
        {
            private int major, minor, patch;
            internal string Prerelease;

            internal static bool TryParse(string text, out SemanticVersion version)
            {
                version = null;
                if (String.IsNullOrEmpty(text) || text.Length > 256) return false;
                Match match = Regex.Match(text, @"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z");
                if (!match.Success) return false;
                SemanticVersion parsed = new SemanticVersion();
                if (!Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed.major) || !Int32.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed.minor) || !Int32.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out parsed.patch)) return false;
                if (match.Groups[4].Success)
                {
                    parsed.Prerelease = match.Groups[4].Value;
                    foreach (string part in parsed.Prerelease.Split('.')) if (IsNumeric(part) && part.Length > 1 && part[0] == '0') return false;
                }
                version = parsed;
                return true;
            }

            private static bool IsNumeric(string part)
            {
                foreach (char value in part) if (value < '0' || value > '9') return false;
                return true;
            }

            internal int CompareTo(SemanticVersion other)
            {
                int comparison = major.CompareTo(other.major);
                if (comparison == 0) comparison = minor.CompareTo(other.minor);
                if (comparison == 0) comparison = patch.CompareTo(other.patch);
                if (comparison != 0) return comparison;
                // This service compares only a validated stable release against the installed version.
                return other.Prerelease == null ? 0 : 1;
            }
        }
    }
}
