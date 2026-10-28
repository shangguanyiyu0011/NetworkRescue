using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class SiteDiagnosticsTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        private sealed class Progress : IProgress<SiteProbe>
        {
            public List<SiteProbe> Values = new List<SiteProbe>();
            public void Report(SiteProbe value) { lock (Values) Values.Add(value); }
        }
        private sealed class HttpFixture : IDisposable
        {
            private readonly TcpListener listener;
            private volatile bool stopped;
            public readonly int Port;
            public readonly List<string> Requests = new List<string>();
            public HttpFixture(int code) : this(code, IPAddress.Loopback) { }
            public HttpFixture(int code, IPAddress address) : this(code, address, null) { }
            public HttpFixture(int getCode, int headCode) : this(getCode, IPAddress.Loopback, headCode) { }
            private HttpFixture(int code, IPAddress address, int? headCode)
            {
                listener = new TcpListener(address, 0); listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task.Run(async delegate
                {
                    while (!stopped)
                    {
                        TcpClient client;
                        try { client = await listener.AcceptTcpClientAsync(); }
                        catch (ObjectDisposedException) { break; }
                        catch (SocketException) { if (stopped) break; throw; }
                        Handle(client, code, headCode);
                    }
                });
            }
            private async void Handle(TcpClient client, int code, int? headCode)
            {
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        string request = await Headers(stream);
                        if (request.Length == 0) return;
                        lock (Requests) Requests.Add(request);
                        if (headCode.HasValue && request.StartsWith("HEAD ", StringComparison.Ordinal)) code = headCode.Value;
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " Fixture\r\nConnection: close\r\nContent-Length: 99999999\r\nLocation: https://must-not-follow.invalid/private?secret=fixture\r\nProxy-Authenticate: Basic realm=fixture\r\n\r\n");
                        await stream.WriteAsync(response, 0, response.Length);
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                }
            }
            public void Dispose() { stopped = true; listener.Stop(); }
        }
        private static async Task<string> Headers(NetworkStream stream)
        {
            StringBuilder value = new StringBuilder(); byte[] one = new byte[1];
            while (value.Length < 16384)
            {
                if (await stream.ReadAsync(one, 0, 1) == 0) return value.ToString();
                value.Append((char)one[0]);
                if (value.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return value.ToString();
            }
            throw new IOException();
        }
        private static SiteTarget Target(string input)
        {
            SiteTarget target; string error;
            Assert(SiteDiagnostics.TryNormalizeTarget(input, out target, out error) && error == null, "valid target " + input);
            return target;
        }
        private static void Inputs()
        {
            Assert(Target("EXAMPLE.com").Origin == "https://example.com/", "bare domain defaults to HTTPS origin");
            SiteTarget target = Target("http://example.com:8080/private/file?password=secret#identity");
            Assert(target.Origin == "http://example.com:8080/" && target.Port == 8080 && target.Host == "example.com", "all sensitive path/query/fragment discarded");
            target = Target("https://[::1]:8443/path?q=secret");
            Assert(target.Port == 8443 && IPAddress.Parse(target.Host).Equals(IPAddress.IPv6Loopback) && new Uri(target.Origin).AbsolutePath == "/" && new Uri(target.Origin).Query.Length == 0, "IPv6 HTTPS origin and explicit port");
            Assert(Target("https://\u793a\u4f8b.\u4e2d\u56fd/path").Host.StartsWith("xn--", StringComparison.Ordinal), "IDN normalized");
            foreach (string input in new[] { "", "https://user:pass@example.com/", "http://@example.com/", "ftp://example.com/", "file:///private", "http://example.com:0/", "http://example.com:65536/", "http://example.com:abc/", "http://example.com:/", "https://[::1]:/", "https://example.com\r\nX: secret", "https://example.com\\@private/", "example.com:8080", "https://exa mple.com/" })
            {
                string error; Assert(!SiteDiagnostics.TryNormalizeTarget(input, out target, out error) && target == null && error != null, "invalid target rejected");
            }
        }
        private static async Task FullHttp(int code, string conclusion)
        {
            using (HttpFixture fixture = new HttpFixture(code))
            {
                Progress progress = new Progress();
                SiteDiagnosticReport report = await SiteDiagnostics.RunCoreAsync(Target("http://127.0.0.1:" + fixture.Port + "/private?secret=value"), Dns.GetHostAddressesAsync, null, progress, CancellationToken.None);
                Assert(report.Probes.Count == 7 && progress.Values.Count == 7 && report.ConclusionCode == conclusion, "complete read-only seven-probe flow " + code);
                Assert(report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "SystemDns"; }).DnsResolved == true && report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "TcpV4"; }).Connected == true, "literal address and actual IPv4 TCP handshake");
                SiteProbe v6 = report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "TcpV6"; });
                Assert(v6.Connected == null && v6.Status == "NoAddress", "no AAAA does not claim broken IPv6 or whole network");
                SiteProbe httpV6 = report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "HttpV6"; });
                Assert(httpV6.Status == "NotApplicable" && httpV6.ErrorCode == "SiteNoIpv6Address" && httpV6.HttpSuccess == null, "no AAAA explicitly inapplicable for IPv6 HTTP");
                Assert(report.Probes.Where(delegate(SiteProbe p) { return p.ResponseReceived.HasValue; }).All(delegate(SiteProbe p) { return p.ResponseReceived == true && p.HttpStatusCode == code && p.HttpSuccess == (code >= 200 && code < 300); }), "HTTP response distinct from HTTP 2xx");
                Assert(fixture.Requests.Count == 3 && fixture.Requests.Count(delegate(string request) { return request.StartsWith("HEAD / HTTP/1.1\r\nHost: 127.0.0.1:" + fixture.Port, StringComparison.Ordinal); }) == 1 && fixture.Requests.All(delegate(string request) { return (request.StartsWith("GET / HTTP/", StringComparison.Ordinal) || request.StartsWith("HEAD / HTTP/", StringComparison.Ordinal)) && request.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) < 0 && !request.Contains("secret") && !request.Contains("private"); }), "family HEAD and existing requests use origin root, no credentials, redirects or sensitive URL");
                Assert(report.EndedUtc >= report.StartedUtc && report.Target.Origin.EndsWith("/", StringComparison.Ordinal), "report timing and tested origin");
            }
        }
        private static async Task HeadResponseIsConnectivityEvidence(int headCode, string status)
        {
            using (HttpFixture fixture = new HttpFixture(200, headCode))
            {
                SiteDiagnosticReport report = await SiteDiagnostics.RunCoreAsync(Target("http://127.0.0.1:" + fixture.Port), Dns.GetHostAddressesAsync, null, null, CancellationToken.None);
                SiteProbe v4 = report.Probes.Single(delegate(SiteProbe probe) { return probe.Kind == "HttpV4"; });
                SiteProbe v6 = report.Probes.Single(delegate(SiteProbe probe) { return probe.Kind == "HttpV6"; });
                Assert(report.ConclusionCode == "BothPaths2xx" && v4.ResponseReceived == true && v4.HttpSuccess == false && v4.HttpStatusCode == headCode && v4.Status == status, "GET 200 with HEAD " + headCode + " preserves HTTP response evidence instead of reporting family disconnection");
                Assert(v6.Status == "NotApplicable" && v6.ResponseReceived == null && v6.HttpSuccess == null, "absent IPv6 does not create partial-family failure for HEAD " + headCode);
                Assert(fixture.Requests.Count == 3 && fixture.Requests.Count(delegate(string request) { return request.StartsWith("HEAD / HTTP/1.1", StringComparison.Ordinal); }) == 1, "non-2xx HEAD " + headCode + " never triggers GET fallback");
            }
        }
        private static Task<IPAddress[]> FailedDns(string host)
        {
            TaskCompletionSource<IPAddress[]> failed = new TaskCompletionSource<IPAddress[]>(); failed.SetException(new SocketException((int)SocketError.HostNotFound)); return failed.Task;
        }
        private static async Task ProxyWithoutDns(int code, string conclusion)
        {
            using (HttpFixture proxy = new HttpFixture(code))
            {
                SiteDiagnosticReport report = await SiteDiagnostics.RunCoreAsync(Target("http://remote-resolution-only.invalid/private?q=secret"), FailedDns, new WebProxy("http://127.0.0.1:" + proxy.Port) { Credentials = CredentialCache.DefaultNetworkCredentials }, null, CancellationToken.None);
                Assert(report.ConclusionCode == conclusion && report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "SystemDns"; }).DnsResolved == false, "proxy path independent of local DNS " + code);
                SiteProbe evidence = report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "HttpWindowsProxy"; });
                Assert(evidence.ResponseReceived == true && evidence.HttpStatusCode == code && evidence.HttpSuccess == (code == 200), "proxy response and authentication code preserved");
                Assert(proxy.Requests.Count == 1 && proxy.Requests[0].StartsWith("GET http://remote-resolution-only.invalid/ HTTP/", StringComparison.Ordinal) && !proxy.Requests[0].Contains("secret") && proxy.Requests[0].IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) < 0, "proxy receives clean origin and no default/proxy credentials");
            }
        }
        private static void Conclusions()
        {
            Func<string, int, SiteProbe> http = delegate(string kind, int code) { return new SiteProbe { Kind = kind, HttpStatusCode = code, ResponseReceived = true, HttpSuccess = code >= 200 && code < 300 }; };
            Assert(SiteDiagnostics.Conclude(new[] { http("HttpDirect", 200), http("HttpWindowsProxy", 407) }) == "DirectPath2xx", "direct success not overridden by unrelated proxy authentication failure");
            Assert(SiteDiagnostics.Conclude(new[] { http("HttpDirect", 401) }) == "HttpAccessRestricted", "401 restriction");
            Assert(SiteDiagnostics.Conclude(new[] { http("HttpDirect", 500) }) == "HttpServerErrorReceived", "server error received");
            Assert(SiteDiagnostics.Conclude(new[] { new SiteProbe { Kind = "HttpDirect", ErrorCode = "HttpTrustFailure" } }) == "TlsValidationOrHandshakeFailed", "TLS validation evidence distinguished");
            Assert(SiteDiagnostics.Conclude(new[] { new SiteProbe { Kind = "TcpV4", Connected = true } }) == "TcpConnectedHttpUnconfirmed", "TCP is not HTTP success");
            Assert(SiteDiagnostics.Conclude(new[] { new SiteProbe { Kind = "SystemDns", DnsResolved = false } }) == "SystemDnsUnconfirmed", "no local DNS evidence");
            Assert(SiteDiagnostics.Conclude(new[] { new SiteProbe { Kind = "SystemDns", DnsResolved = true } }) == "SiteConnectivityUnconfirmed", "DNS alone not site availability");
        }
        private static async Task CertificateValidation()
        {
            using (RSA key = RSA.Create(2048))
            {
                CertificateRequest request = new CertificateRequest("CN=untrusted-fixture.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                using (X509Certificate2 created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1)))
                using (X509Certificate2 certificate = new X509Certificate2(created.Export(X509ContentType.Pfx), (string)null, X509KeyStorageFlags.Exportable))
                {
                    TcpListener server = new TcpListener(IPAddress.Loopback, 0); server.Start();
                    int port = ((IPEndPoint)server.LocalEndpoint).Port;
                    List<string> failures = new List<string>();
                    Task serving = Task.Run(async delegate
                    {
                        List<Task> handshakes = new List<Task>();
                        for (int i = 0; i < 4; i++)
                        {
                            TcpClient client = await server.AcceptTcpClientAsync();
                            handshakes.Add(Task.Run(async delegate
                            {
                                using (client)
                                using (SslStream ssl = new SslStream(client.GetStream(), false))
                                {
                                    try { await ssl.AuthenticateAsServerAsync(certificate, false, System.Security.Authentication.SslProtocols.Tls12, false); }
                                    catch (IOException error) { lock (failures) failures.Add(error.GetType().Name); }
                                    catch (System.Security.Authentication.AuthenticationException error) { lock (failures) failures.Add(error.GetType().Name); }
                                }
                            }));
                        }
                        await Task.WhenAll(handshakes);
                    });
                    try
                    {
                        SiteDiagnosticReport report = await SiteDiagnostics.RunCoreAsync(Target("https://127.0.0.1:" + port + "/"), Dns.GetHostAddressesAsync, null, null, CancellationToken.None);
                        Assert(report.Probes.Where(delegate(SiteProbe p) { return p.Kind.StartsWith("Http", StringComparison.Ordinal) && p.Status != "NotApplicable"; }).All(delegate(SiteProbe p) { return p.ResponseReceived == false && p.HttpSuccess == false; }), "untrusted/mismatched certificate never bypassed");
                        Assert(report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "HttpV4"; }).AddressAttempts[0].ErrorCode == "SiteTlsValidationOrHandshakeFailed", "family HTTPS uses platform certificate validation");
                        Assert(report.ConclusionCode == "TlsValidationOrHandshakeFailed", "real certificate rejection is distinguished from DNS/TCP reachability");
                        await serving;
                    }
                    finally { server.Stop(); }
                }
            }
        }
        private static async Task FamilyCombinations()
        {
            IPAddress otherV4 = IPAddress.Parse("127.0.0.2"), thirdV4 = IPAddress.Parse("127.0.0.3");
            List<IPAddress> sampled = new List<IPAddress>();
            Func<SiteTarget, IPAddress, CancellationToken, Task<SiteProbe>> fake = delegate(SiteTarget ignoredTarget, IPAddress address, CancellationToken token)
            {
                lock (sampled) sampled.Add(address);
                bool success = address.Equals(otherV4);
                return Task.FromResult(new SiteProbe { Status = success ? "HttpResponse" : "HttpNoResponse", ResponseReceived = success, HttpSuccess = success, HttpStatusCode = success ? (int?)204 : null, ErrorCode = success ? null : "SiteHttpConnectionRefused" });
            };
            SiteTarget target = Target("http://family-fixture.invalid/");
            IPAddress[] addresses = new[] { IPAddress.Loopback, otherV4, thirdV4, IPAddress.IPv6Loopback };
            SiteProbe v4 = await SiteDiagnostics.ProbeFamilyAsync(target, addresses, AddressFamily.InterNetwork, true, CancellationToken.None, fake);
            Assert(v4.HttpSuccess == true && v4.AddressCount == 3 && v4.AttemptedAddressCount == 2 && v4.Address == otherV4.ToString() && sampled.Count == 2 && !sampled.Contains(thirdV4), "one failed address does not turn a successful family into failure; only two sampled");
            SiteProbe v6 = await SiteDiagnostics.ProbeFamilyAsync(target, addresses, AddressFamily.InterNetworkV6, true, CancellationToken.None, fake);
            Assert(v6.HttpSuccess == false && v6.ErrorCode == "SiteHttpAddressAttemptsUnconfirmed" && v6.AddressAttempts.Length == 1, "failed sample stays unconfirmed with per-address evidence");
            Assert(SiteDiagnostics.Conclude(new[] { v4, v6, new SiteProbe { Kind = "HttpDirect", HttpSuccess = true } }) == "AddressFamilyHttpPartialFailure", "successful generic direct path cannot hide failed IPv6 sample");
            Assert(SiteDiagnostics.Conclude(new[] { v4, new SiteProbe { Kind = "HttpV6", Status = "NotApplicable" }, new SiteProbe { Kind = "HttpDirect", HttpSuccess = true } }) == "DirectPath2xx", "missing family is not failure");
            SiteProbe unknown = await SiteDiagnostics.ProbeFamilyAsync(target, new IPAddress[0], AddressFamily.InterNetworkV6, false, CancellationToken.None, fake);
            Assert(unknown.Status == "Unknown" && unknown.ErrorCode == "SiteDnsUnconfirmed" && unknown.AttemptedAddressCount == 0, "DNS failure cannot prove no AAAA");
            using (CancellationTokenSource cancel = new CancellationTokenSource(60))
            {
                bool cancelled = false;
                try { await SiteDiagnostics.ProbeFamilyAsync(target, addresses, AddressFamily.InterNetwork, true, cancel.Token, delegate { return new TaskCompletionSource<SiteProbe>().Task; }); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled, "even uncooperative injected family attempt is cancellable");
            }
        }
        private sealed class RawFixture : IDisposable
        {
            private readonly TcpListener listener;
            private readonly CancellationTokenSource stop = new CancellationTokenSource();
            public readonly int Port;
            public string Request;
            public RawFixture(string response, bool tlsStall)
            {
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Task.Run(async delegate
                {
                    try
                    {
                        using (TcpClient client = await listener.AcceptTcpClientAsync())
                        using (stop.Token.Register(client.Close))
                        {
                            if (!tlsStall) Request = await Headers(client.GetStream());
                            if (response != null)
                            {
                                byte[] bytes = Encoding.ASCII.GetBytes(response);
                                await client.GetStream().WriteAsync(bytes, 0, bytes.Length);
                            }
                            else await Task.Delay(10000, stop.Token);
                        }
                    }
                    catch (IOException) { }
                    catch (SocketException) { }
                    catch (OperationCanceledException) { }
                    catch (ObjectDisposedException) { }
                });
            }
            public void Dispose() { stop.Cancel(); listener.Stop(); }
        }
        private static async Task FamilyWireChecks()
        {
            await OriginalTlsSni();
            using (RawFixture fixture = new RawFixture("HTTP/1.1 204 Fixture\r\nContent-Length: 999999999\r\n\r\n", false))
            {
                SiteTarget target = Target("http://original-host.invalid:" + fixture.Port + "/private?secret=yes");
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(target, IPAddress.Loopback, CancellationToken.None);
                Assert(probe.HttpSuccess == true && fixture.Request.StartsWith("HEAD / HTTP/1.1\r\nHost: original-host.invalid:" + fixture.Port + "\r\n", StringComparison.Ordinal), "forced IP transport preserves original domain Host and requests HEAD only");
            }
            using (RawFixture fixture = new RawFixture("HTTP/1.1 200 Fixture\r\nX: " + new String('x', 33000), false))
            {
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(Target("http://127.0.0.1:" + fixture.Port), IPAddress.Loopback, CancellationToken.None);
                Assert(probe.ErrorCode == "SiteHttpHeaderTooLarge" && probe.ResponseReceived == false, "32 KiB header budget enforced");
            }
            using (RawFixture fixture = new RawFixture("NOT HTTP\r\n\r\n", false))
            {
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(Target("http://127.0.0.1:" + fixture.Port), IPAddress.Loopback, CancellationToken.None);
                Assert(probe.ErrorCode == "SiteHttpInvalidResponse" && probe.HttpStatusCode == null, "invalid status is not HTTP response evidence");
            }
            using (RawFixture fixture = new RawFixture("HTTP/1.1 100 Continue\r\n\r\nHTTP/1.1 204 Fixture\r\n\r\n", false))
            {
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(Target("http://127.0.0.1:" + fixture.Port), IPAddress.Loopback, CancellationToken.None);
                Assert(probe.HttpSuccess == true && probe.HttpStatusCode == 204, "informational headers do not hide final response");
            }
            foreach (bool tls in new[] { false, true })
            {
                using (RawFixture fixture = new RawFixture(null, tls))
                using (CancellationTokenSource cancel = new CancellationTokenSource(80))
                {
                    System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew(); bool cancelled = false;
                    try { await SiteDiagnostics.ProbeFamilyAddress(Target((tls ? "https" : "http") + "://original-host.invalid:" + fixture.Port), IPAddress.Loopback, cancel.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled && clock.ElapsedMilliseconds < 1200, tls ? "TLS handshake bounded by cancellation" : "header read bounded by cancellation");
                }
            }
            using (RawFixture fixture = new RawFixture(null, false))
            {
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(Target("http://127.0.0.1:" + fixture.Port), IPAddress.Loopback, CancellationToken.None);
                Assert(probe.ErrorCode == "SiteHttpTimeout" && clock.ElapsedMilliseconds >= 2500 && clock.ElapsedMilliseconds < 4500, "complete address attempt has a three-second deadline");
            }
        }
        private static int Read16(byte[] data, int offset) { return data[offset] * 256 + data[offset + 1]; }
        private static async Task ReadExactly(NetworkStream stream, byte[] data)
        {
            int offset = 0;
            while (offset < data.Length)
            {
                int read = await stream.ReadAsync(data, offset, data.Length - offset);
                if (read == 0) throw new IOException("Truncated TLS fixture record");
                offset += read;
            }
        }
        private static async Task OriginalTlsSni()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Task<string> observed = Task.Run(async delegate
            {
                using (TcpClient client = await listener.AcceptTcpClientAsync())
                {
                    byte[] record = new byte[5]; await ReadExactly(client.GetStream(), record);
                    byte[] hello = new byte[Read16(record, 3)]; await ReadExactly(client.GetStream(), hello);
                    if (record[0] != 22 || hello[0] != 1 || hello.Length < 42) throw new IOException("Expected TLS ClientHello");
                    int offset = 38;
                    offset += 1 + hello[offset];
                    offset += 2 + Read16(hello, offset);
                    offset += 1 + hello[offset];
                    int end = offset + 2 + Read16(hello, offset); offset += 2;
                    while (offset + 4 <= end)
                    {
                        int type = Read16(hello, offset), size = Read16(hello, offset + 2); offset += 4;
                        if (type == 0 && size >= 5 && hello[offset + 2] == 0) return Encoding.ASCII.GetString(hello, offset + 5, Read16(hello, offset + 3));
                        offset += size;
                    }
                    return null;
                }
            });
            try
            {
                SiteProbe probe = await SiteDiagnostics.ProbeFamilyAddress(Target("https://original-tls-host.invalid:" + port), IPAddress.Loopback, CancellationToken.None);
                Assert(await observed == "original-tls-host.invalid" && probe.TlsServerName == "original-tls-host.invalid" && probe.ResponseReceived == false, "forced loopback IP still emits original DNS hostname in real TLS SNI");
            }
            finally { listener.Stop(); }
        }
        private static async Task Run()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Inputs(); Conclusions();
            await FamilyCombinations();
            await FamilyWireChecks();
            await FullHttp(204, "BothPaths2xx");
            await FullHttp(302, "HttpRedirectReceived");
            await FullHttp(403, "HttpAccessRestricted");
            await FullHttp(404, "HttpClientErrorReceived");
            await HeadResponseIsConnectivityEvidence(405, "HttpClientError");
            await HeadResponseIsConnectivityEvidence(403, "HttpClientError");
            await HeadResponseIsConnectivityEvidence(302, "HttpRedirect");
            await ProxyWithoutDns(200, "WindowsProxyPath2xx");
            await ProxyWithoutDns(407, "ProxyAuthenticationRequired");
            await CertificateValidation();
            if (Socket.OSSupportsIPv6)
            {
                using (HttpFixture fixture = new HttpFixture(204, IPAddress.IPv6Loopback))
                {
                    SiteDiagnosticReport report = await SiteDiagnostics.RunCoreAsync(Target("http://[::1]:" + fixture.Port + "/"), Dns.GetHostAddressesAsync, null, null, CancellationToken.None);
                    Assert(report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "TcpV6"; }).Connected == true, "actual IPv6 TCP handshake");
                    Assert(report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "HttpV6"; }).HttpSuccess == true && fixture.Requests.Any(delegate(string value) { return value.StartsWith("HEAD / HTTP/1.1\r\nHost: [::1]:" + fixture.Port, StringComparison.Ordinal); }), "actual forced IPv6 HTTP and bracketed Host");
                    Assert(report.Probes.Single(delegate(SiteProbe p) { return p.Kind == "TcpV4"; }).Connected == null && report.ConclusionCode == "BothPaths2xx", "IPv6 HTTP success does not require IPv4");
                }
            }
            using (HttpFixture proxy = new HttpFixture(200))
            using (CancellationTokenSource cancel = new CancellationTokenSource(100))
            {
                TaskCompletionSource<IPAddress[]> pending = new TaskCompletionSource<IPAddress[]>();
                System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew(); bool cancelled = false;
                try { await SiteDiagnostics.RunCoreAsync(Target("http://pending-dns.invalid/"), delegate { return pending.Task; }, new WebProxy("http://127.0.0.1:" + proxy.Port), null, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && clock.ElapsedMilliseconds < 1200, "cancel stops combined HTTP/DNS checks without awaiting system DNS completion");
            }
        }
        public static int Main()
        {
            try { Run().GetAwaiter().GetResult(); Console.WriteLine("SiteDiagnosticsTests passed: " + assertions); return 0; }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }
    }
}
