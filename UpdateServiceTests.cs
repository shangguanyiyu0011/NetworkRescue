using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class UpdateServiceTests
    {
        private static int assertions;
        private const string Repository = "example-owner/network-rescue";
        private const string Stable = "{\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false,\"html_url\":\"https://evil.test/run.exe\"}";
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }

        private static void InputAndParser()
        {
            string normalized;
            Assert(UpdateService.TryNormalizeRepository(" example-owner/network-rescue ", out normalized) && normalized == Repository, "trim owner/repo");
            foreach (string value in new[] { null, "", "owner", "https://github.com/owner/repo", "owner/repo?token=secret", "owner/repo#fragment", "user:password/repo", "owner//repo", "../repo", "owner/..", "owner/a..b", "-owner/repo", "owner-/repo", "owner--name/repo", "owner/repo/", "owner/repo\\file", "owner/re po", "owner/%72epo", "owner/.", "é/repo", new string('a', 40) + "/repo", "owner/" + new string('a', 101) })
                Assert(!UpdateService.TryNormalizeRepository(value, out normalized), "reject repository " + value);
            Assert(UpdateService.ParseReleaseTag(Encoding.UTF8.GetBytes(Stable)) == "v1.0.0", "strict release fields");
            Assert(UpdateService.ParseReleaseTag(Encoding.UTF8.GetBytes(Stable.Substring(0, Stable.Length - 1) + ",}")) == "v1.0.0", "Framework trailing-comma tolerance retains typed fields");
            foreach (string value in new[] { "", "null", "[]", "{}", "{\"tag_name\":1,\"draft\":false,\"prerelease\":false}", "{\"tag_name\":\"1.0.0\",\"draft\":\"false\",\"prerelease\":false}", "{\"tag_name\":\"1.0.0\",\"draft\":true,\"prerelease\":false}", "{\"tag_name\":\"1.0.0\",\"draft\":false,\"prerelease\":true}", Stable + "garbage", Stable + Stable, Stable.Replace("\"tag_name\":", "tag_name:"), Stable.Replace("\"tag_name\"", "'tag_name'"), Stable.Replace("false,\"prerelease\"", "False,\"prerelease\""), Stable.Replace("\"draft\":false", "\"draft\":false,\"draft\":false"), "{\"extra\":" + new string('[', 20) + "0" + new string(']', 20) + ",\"tag_name\":\"1.0.0\",\"draft\":false,\"prerelease\":false}" })
            {
                bool rejected = false;
                try { UpdateService.ParseReleaseTag(Encoding.UTF8.GetBytes(value)); } catch (Exception) { rejected = true; }
                Assert(rejected, "reject malformed release " + value);
            }
            string good = "https://github.com/" + Repository + "/releases/tag/v1.0.0%2Bbuild.42";
            Assert(UpdateService.IsAllowedReleaseUrl(Repository, good), "release URL build metadata escaped");
            foreach (string url in new[] { "http://github.com/" + Repository + "/releases/tag/1.0.0", good + "?token=x", good + "#x", good.Replace("github.com", "github.com.evil.test"), good.Replace("github.com/", "user@github.com/"), good.Replace("example-owner", "other-owner"), good.Replace("v1.0.0%2Bbuild.42", "../download"), good.Replace("v1.0.0%2Bbuild.42", "1.0.0-rc.1"), good.Replace("v1.0.0%2Bbuild.42", "%31.0.0"), good.Replace("v1.0.0%2Bbuild.42", "01.0.0") })
                Assert(!UpdateService.IsAllowedReleaseUrl(Repository, url), "reject external or malformed release URL");
        }

        private sealed class Fixture : IDisposable
        {
            private readonly HttpListener listener = new HttpListener();
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            internal readonly Uri Endpoint;
            internal readonly Task Server;
            internal Fixture(int status, byte[] body, Dictionary<string, string> headers, int delay, bool chunked, bool delayBody)
            {
                TcpListener portPicker = new TcpListener(IPAddress.Loopback, 0);
                portPicker.Start(); int port = ((IPEndPoint)portPicker.LocalEndpoint).Port; portPicker.Stop();
                string prefix = "http://127.0.0.1:" + port + "/";
                Endpoint = new Uri(prefix + "repos/" + Repository + "/releases/latest");
                listener.Prefixes.Add(prefix); listener.Start();
                Server = Task.Run(async delegate
                {
                    try
                    {
                        HttpListenerContext context = await listener.GetContextAsync();
                        Assert(context.Request.HttpMethod == "GET" && context.Request.RawUrl == Endpoint.AbsolutePath, "fixed GET API path");
                        Assert(context.Request.Headers["Authorization"] == null && context.Request.Headers["Proxy-Authorization"] == null && context.Request.Headers["Cookie"] == null, "no credentials or cookies");
                        Assert(context.Request.UserAgent == "NetworkRescue-UpdateCheck/1.0" && context.Request.Headers["Accept"] == "application/vnd.github+json", "GitHub headers");
                        if (!delayBody && delay > 0) await Task.Delay(delay, stop.Token);
                        context.Response.StatusCode = status;
                        context.Response.ContentType = "application/json";
                        if (headers != null) foreach (KeyValuePair<string, string> header in headers) context.Response.Headers[header.Key] = header.Value;
                        if (chunked) context.Response.SendChunked = true; else context.Response.ContentLength64 = body.Length;
                        if (delayBody)
                        {
                            await context.Response.OutputStream.WriteAsync(body, 0, 1);
                            await context.Response.OutputStream.FlushAsync();
                            await Task.Delay(delay, stop.Token);
                            await context.Response.OutputStream.WriteAsync(body, 1, body.Length - 1);
                        }
                        else if (body.Length > 0) await context.Response.OutputStream.WriteAsync(body, 0, body.Length);
                        context.Response.Close();
                    }
                    catch (OperationCanceledException) { }
                    catch (HttpListenerException) { } // The client closes rejected responses without consuming their body.
                    catch (IOException) { }
                    catch (ObjectDisposedException) { if (!stop.IsCancellationRequested) throw; }
                });
            }
            public void Dispose() { stop.Cancel(); listener.Close(); }
        }

        private static async Task<UpdateCheckResult> Response(int status, string body, string current, Dictionary<string, string> headers)
        {
            using (Fixture fixture = new Fixture(status, Encoding.UTF8.GetBytes(body), headers, 0, false, false))
            {
                UpdateCheckResult result = await UpdateService.CheckLoopbackAsync(Repository, current, CancellationToken.None, fixture.Endpoint);
                await fixture.Server;
                return result;
            }
        }

        private static async Task Checks()
        {
            Assert((await UpdateService.CheckAsync("", "1.0.0", CancellationToken.None)).Status == "NotConfigured", "empty configuration does not request network");
            Assert((await UpdateService.CheckAsync("https://evil.test/x", "1.0.0", CancellationToken.None)).ErrorCode == "InvalidRepository", "invalid repo does not request network");
            Assert((await UpdateService.CheckAsync(Repository, "not-a-version", CancellationToken.None)).ErrorCode == "InvalidCurrentVersion", "invalid current version does not request network");
            UpdateCheckResult result = await Response(200, Stable, "1.0.0-rc.1", null);
            Assert(result.Status == "UpdateAvailable" && result.LatestVersion == "v1.0.0", "stable release outranks current RC");
            Assert(result.Repository == Repository && result.CurrentVersion == "1.0.0-rc.1" && result.ErrorCode == null, "result context");
            Assert(result.ReleaseUrl == "https://github.com/" + Repository + "/releases/tag/v1.0.0" && UpdateService.IsAllowedReleaseUrl(Repository, result.ReleaseUrl), "ignore attacker html_url");
            Assert((await Response(200, Stable, "1.0.0+local", null)).Status == "UpToDate", "build metadata does not change version precedence");
            Assert((await Response(200, Stable, "2.0.0", null)).Status == "UpToDate", "never suggest downgrade");
            Assert((await Response(200, Stable.Replace("v1.0.0", "v1.1.0+build.5"), "1.0.99", null)).Status == "UpdateAvailable", "minor version and build metadata");
            foreach (string tag in new[] { "release-2.0.0", "1.0", "1.0.0.0", "01.0.0", "1.00.0", "2147483648.0.0", "1.0.0-rc.1", "1.0.0+", "1.0.0/../../x", "1.0.0\n", "V1.0.0", "1.0.0-01" })
                Assert((await Response(200, Stable.Replace("v1.0.0", tag.Replace("\n", "\\n")), "0.0.1", null)).ErrorCode == "InvalidRelease", "reject invalid or prerelease tag " + tag);
            foreach (string current in new[] { "1.0.0-01", "1.0.0-alpha..1", "9999999999.0.0", " 1.0.0", "1.0.0\n" })
                Assert((await UpdateService.CheckAsync(Repository, current, CancellationToken.None)).ErrorCode == "InvalidCurrentVersion", "reject invalid current version");
            Assert((await Response(404, "not JSON", "1.0.0", null)).Status == "NoPublishedRelease", "404 unpublished release");
            Assert((await Response(429, "not JSON", "1.0.0", null)).Status == "RateLimited", "429 rate limit");
            Assert((await Response(403, "not JSON", "1.0.0", new Dictionary<string, string> { { "X-RateLimit-Remaining", "0" } })).Status == "RateLimited", "403 primary limit");
            Assert((await Response(403, "not JSON", "1.0.0", new Dictionary<string, string> { { "Retry-After", "60" } })).Status == "RateLimited", "403 secondary limit");
            result = await Response(403, "not JSON", "1.0.0", null);
            Assert(result.Status == "CheckFailed" && result.ErrorCode == "Forbidden", "ordinary 403 is forbidden");
            Assert((await Response(500, "not JSON", "1.0.0", null)).ErrorCode == "Http500", "HTTP error");
            Assert((await Response(302, "", "1.0.0", new Dictionary<string, string> { { "Location", "https://evil.test/" } })).ErrorCode == "Http302", "do not follow redirect");
            Assert((await Response(200, "{invalid}", "1.0.0", null)).ErrorCode == "InvalidRelease", "invalid JSON maps to safe error");
            Assert((await Response(200, new string(' ', 524289), "1.0.0", null)).ErrorCode == "ResponseTooLarge", "content length body bound");
            using (Fixture fixture = new Fixture(200, Encoding.UTF8.GetBytes(new string(' ', 524289)), null, 0, true, false))
            {
                Assert((await UpdateService.CheckLoopbackAsync(Repository, "1.0.0", CancellationToken.None, fixture.Endpoint)).ErrorCode == "ResponseTooLarge", "chunked body bound");
                await fixture.Server;
            }
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (Fixture fixture = new Fixture(200, Encoding.UTF8.GetBytes(Stable), null, 30000, false, false))
            {
                cancellation.CancelAfter(150);
                bool cancelled = false;
                try { await UpdateService.CheckLoopbackAsync(Repository, "1.0.0", cancellation.Token, fixture.Endpoint); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "abort cancelled request");
                fixture.Dispose(); await fixture.Server;
            }
            using (Fixture fixture = new Fixture(200, Encoding.UTF8.GetBytes(Stable), null, 30000, true, true))
            {
                Stopwatch clock = Stopwatch.StartNew();
                result = await UpdateService.CheckLoopbackAsync(Repository, "1.0.0", CancellationToken.None, fixture.Endpoint);
                Assert(result.ErrorCode == "Timeout" && clock.ElapsedMilliseconds >= 7500 && clock.ElapsedMilliseconds < 11000, "8 second deadline covers stalled body");
                fixture.Dispose(); await fixture.Server;
            }
            bool externalRejected = false;
            try { await UpdateService.CheckLoopbackAsync(Repository, "1.0.0", CancellationToken.None, new Uri("https://evil.test/")); } catch (ArgumentException) { externalRejected = true; }
            Assert(externalRejected, "fixture cannot reach external service");
        }

        public static int Main()
        {
            try { InputAndParser(); Checks().GetAwaiter().GetResult(); Console.WriteLine("UpdateService: " + assertions + " assertions passed (loopback only)."); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
