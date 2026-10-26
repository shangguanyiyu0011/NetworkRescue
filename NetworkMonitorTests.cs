using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class NetworkMonitorTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        private sealed class Samples : IProgress<MonitorSample>
        {
            public List<MonitorSample> Values = new List<MonitorSample>();
            public readonly TaskCompletionSource<bool> FirstTwo = new TaskCompletionSource<bool>();
            public void Report(MonitorSample sample) { lock (Values) { Values.Add(sample); if (Values.Count == 2) FirstTwo.TrySetResult(true); } }
        }
        private static byte[] Answer(byte[] query, bool truncated)
        {
            byte[] answer = (byte[])query.Clone();
            answer[2] = (byte)(truncated ? 131 : 129); answer[3] = 128;
            if (truncated) return answer;
            answer[7] = 1;
            int type = query[query.Length - 3];
            byte[] address = type == 1 ? IPAddress.Parse("192.0.2.19").GetAddressBytes() : IPAddress.Parse("2001:db8::19").GetAddressBytes();
            using (MemoryStream stream = new MemoryStream())
            {
                stream.Write(answer, 0, answer.Length);
                byte[] header = { 192, 12, 0, (byte)type, 0, 1, 0, 0, 0, 30, 0, (byte)address.Length };
                stream.Write(header, 0, header.Length); stream.Write(address, 0, address.Length);
                return stream.ToArray();
            }
        }
        private static void Invalid(byte[] response, byte[] query, ushort type, string message)
        {
            bool rejected = false;
            try { NetworkMonitor.ParseResponse(response, query, "example.test", type); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, message);
        }
        private static void ProtocolFixtures()
        {
            byte[] query = NetworkMonitor.BuildQuery("example.test", 1);
            byte[] valid = Answer(query, false);
            DnsQueryResult result = NetworkMonitor.ParseResponse(valid, query, "example.test", 1);
            Assert(result.Addresses.SequenceEqual(new[] { "192.0.2.19" }) && result.Rcode == 0 && result.Status == "Answered", "compressed A answer");
            byte[] aaaa = NetworkMonitor.BuildQuery("example.test", 28);
            Assert(NetworkMonitor.ParseResponse(Answer(aaaa, false), aaaa, "example.test", 28).Addresses[0] == "2001:db8::19", "AAAA answer");
            byte[] wrong = (byte[])valid.Clone(); wrong[0] ^= 1; Invalid(wrong, query, 1, "ID mismatch");
            wrong = (byte[])valid.Clone(); wrong[13] ^= 1; Invalid(wrong, query, 1, "question mismatch");
            wrong = (byte[])valid.Clone(); wrong[2] &= 127; Invalid(wrong, query, 1, "query is not response");
            wrong = (byte[])valid.Clone(); wrong[query.Length + 11] = 5; Invalid(wrong, query, 1, "RDATA length boundary");
            wrong = (byte[])valid.Clone(); wrong[query.Length] = (byte)(192 | (query.Length >> 8)); wrong[query.Length + 1] = (byte)query.Length; Invalid(wrong, query, 1, "self compression pointer");
            wrong = (byte[])valid.Clone(); wrong[6] = 3; wrong[7] = 255; Invalid(wrong, query, 1, "record count bound");
            wrong = valid.Take(valid.Length - 1).ToArray(); Invalid(wrong, query, 1, "truncated RDATA rejected");
            wrong = (byte[])query.Clone(); wrong[2] = 129; wrong[3] = 131;
            result = NetworkMonitor.ParseResponse(wrong, query, "example.test", 1);
            Assert(result.Rcode == 3 && result.Addresses.Length == 0 && result.Status == "Answered", "NXDOMAIN explicit RCODE");
            Assert(NetworkMonitor.ParseResponse(Answer(query, true), query, "example.test", 1).Truncated, "TC requests TCP fallback");
            for (int size = 0; size < valid.Length; size++) Invalid(valid.Take(size).ToArray(), query, 1, "packet prefix " + size);
        }
        private static async Task<byte[]> Read(NetworkStream stream, int count)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int got = await stream.ReadAsync(bytes, offset, count - offset); if (got == 0) throw new IOException(); offset += got; }
            return bytes;
        }
        private static async Task DnsTransport(bool tcpFallback)
        {
            using (UdpClient server = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)server.Client.LocalEndPoint).Port;
                TcpListener tcp = new TcpListener(IPAddress.Loopback, port);
                if (tcpFallback) tcp.Start();
                Task udpTask = Task.Run(async delegate
                {
                    for (int i = 0; i < 2; i++) { UdpReceiveResult request = await server.ReceiveAsync(); byte[] response = Answer(request.Buffer, tcpFallback); await server.SendAsync(response, response.Length, request.RemoteEndPoint); }
                });
                Task tcpTask = tcpFallback ? Task.Run(async delegate
                {
                    for (int i = 0; i < 2; i++)
                    {
                        using (TcpClient client = await tcp.AcceptTcpClientAsync())
                        {
                            NetworkStream stream = client.GetStream(); byte[] length = await Read(stream, 2);
                            byte[] response = Answer(await Read(stream, (length[0] << 8) | length[1]), false);
                            await stream.WriteAsync(new[] { (byte)(response.Length >> 8), (byte)response.Length }, 0, 2);
                            await stream.WriteAsync(response, 0, response.Length);
                        }
                    }
                }) : Task.FromResult(true);
                try
                {
                    DnsComparisonResult result = await NetworkMonitor.CompareServer("example.test", IPAddress.Loopback, port, null, CancellationToken.None);
                    Assert(result.Status == "Answered" && result.Queries.Count == 2, "real local DNS transport " + tcpFallback);
                    Assert(result.Queries.All(delegate(DnsQueryResult q) { return q.Addresses.Length == 1 && q.UsedTcp == tcpFallback; }), "real A/AAAA addresses and TCP flag");
                    await Task.WhenAll(udpTask, tcpTask);
                }
                finally { tcp.Stop(); }
            }
        }
        private static async Task Run()
        {
            ProtocolFixtures();
            await HttpFixtures();
            await MultiMonitorFixtures();
            Samples samples = new Samples();
            MonitorSummary summary = await NetworkMonitor.RunAsync("127.0.0.1", 1000, 2, samples, CancellationToken.None);
            Assert(summary.Sent == 2 && summary.Received == 2 && samples.Values.Count == 2 && summary.LossPercent == 0 && summary.Avg.HasValue && summary.JitterMs.HasValue, "actual loopback ICMP and cumulative statistics");
            Assert(samples.Values[0].Sent == 1 && samples.Values[1].Sent == 2 && samples.Values[0] != samples.Values[1], "independent typed samples");
            using (CancellationTokenSource cancel = new CancellationTokenSource(100))
            {
                Stopwatch clock = Stopwatch.StartNew(); bool cancelled = false;
                try { await NetworkMonitor.RunAsync("127.0.0.1", 10000, 100, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && clock.ElapsedMilliseconds < 1200, "cancel monitoring delay promptly");
            }
            await DnsTransport(false); await DnsTransport(true);
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)silent.Client.LocalEndPoint).Port;
                Stopwatch clock = Stopwatch.StartNew();
                DnsComparisonResult timeout = await NetworkMonitor.CompareServer("example.test", IPAddress.Loopback, port, null, CancellationToken.None);
                Assert(timeout.Status == "Unknown" && timeout.Queries.All(delegate(DnsQueryResult q) { return q.ErrorCode == "DnsTimeout"; }) && clock.ElapsedMilliseconds < 2800, "resolver total deadline includes both query types");
                using (CancellationTokenSource cancel = new CancellationTokenSource(100))
                {
                    clock.Restart(); bool cancelled = false;
                    try { await NetworkMonitor.CompareServer("example.test", IPAddress.Loopback, port, null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled && clock.ElapsedMilliseconds < 1200, "cancel pending DNS sockets");
                }
            }
            bool invalid = false;
            try { await NetworkMonitor.CompareDnsAsync("example.test", new[] { "resolver.example" }, null, CancellationToken.None); } catch (ArgumentException) { invalid = true; }
            Assert(invalid, "resolver requires explicit IP");
        }

        private static async Task<string> ReadHeaders(NetworkStream stream)
        {
            StringBuilder text = new StringBuilder();
            byte[] one = new byte[1];
            while (text.Length < 16384)
            {
                if (await stream.ReadAsync(one, 0, 1) == 0) throw new IOException();
                text.Append((char)one[0]);
                if (text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) return text.ToString();
            }
            throw new IOException();
        }

        private static async Task HttpResponseFixture(int status, bool proxy, bool https, bool keepBodyOpen = false)
        {
            TcpListener server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            string requestHeaders = null;
            int port = ((IPEndPoint)server.LocalEndpoint).Port;
            Task serving = Task.Run(async delegate
            {
                using (TcpClient client = await server.AcceptTcpClientAsync())
                {
                    NetworkStream stream = client.GetStream();
                    requestHeaders = await ReadHeaders(stream);
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nConnection: close\r\nContent-Length: 100000000\r\nLocation: https://must-not-follow.invalid/\r\nProxy-Authenticate: Basic realm=fixture\r\n\r\n");
                    await stream.WriteAsync(response, 0, response.Length);
                    if (keepBodyOpen)
                    {
                        Task<int> closed = stream.ReadAsync(new byte[1], 0, 1);
                        Assert(await Task.WhenAny(closed, Task.Delay(1500)) == closed && await closed == 0, "headers-only monitor closes the unread stalled error body " + status);
                    }
                }
            });
            try
            {
                Uri target = new Uri(proxy ? (https ? "https://" : "http://") + "remote-resolution-only.invalid/" : "http://127.0.0.1:" + port + "/");
                WebProxy webProxy = proxy ? new WebProxy("http://127.0.0.1:" + port) { Credentials = CredentialCache.DefaultNetworkCredentials } : null;
                Samples samples = new Samples();
                Stopwatch clock = Stopwatch.StartNew();
                MonitorSummary result = await NetworkMonitor.RunHttpEndpointAsync(target, 1000, 1, webProxy, proxy ? "WindowsProxy" : "Direct", samples, CancellationToken.None);
                await serving;
                Assert(result.Received == 1 && result.Sent == 1 && result.LossPercent == 0 && result.HttpStatusCode == status && result.ResponseReceived, "HTTP response counts including non-2xx " + status + " https=" + https);
                Assert(result.HttpSuccess == (status >= 200 && status <= 299) && samples.Values[0].HttpSuccess == result.HttpSuccess, "2xx availability separate from response statistics");
                Assert(samples.Values[0].Status == (status == 407 ? "ProxyAuthenticationRequired" : status >= 500 ? "HttpServerError" : status >= 400 ? "HttpClientError" : status >= 300 ? "HttpRedirect" : "HttpResponse"), "typed HTTP status " + status);
                Assert(clock.ElapsedMilliseconds < 2000 && result.RttMs.HasValue, "headers suffice without downloading declared large body");
                Assert(requestHeaders.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) < 0 && requestHeaders.Contains("NetworkRescue/"), "no origin/proxy credentials and app user agent");
                Assert(!proxy || requestHeaders.StartsWith(https ? "CONNECT remote-resolution-only.invalid:443 " : "GET http://remote-resolution-only.invalid/ ", StringComparison.Ordinal), "proxy receives unresolved origin rather than local DNS prerequisite");
                Assert(result.Method == (https ? "HTTPS" : "HTTP") && result.ResolvedAddress == null, "HTTP evidence never claims ICMP address or RTT");
            }
            finally { server.Stop(); }
        }

        private static async Task HttpFixtures()
        {
            await HttpResponseFixture(204, false, false);
            await HttpResponseFixture(302, false, false);
            await HttpResponseFixture(404, false, false);
            await HttpResponseFixture(200, true, false);
            await HttpResponseFixture(407, true, false);
            await HttpResponseFixture(407, true, true);
            await HttpResponseFixture(407, true, true, true);
            await HttpResponseFixture(404, false, false, true);
            await HttpResponseFixture(500, false, false, true);
            TcpListener silent = new TcpListener(IPAddress.Loopback, 0);
            silent.Start();
            try
            {
                Uri target = new Uri("http://127.0.0.1:" + ((IPEndPoint)silent.LocalEndpoint).Port + "/");
                Task<TcpClient> accept = silent.AcceptTcpClientAsync();
                using (CancellationTokenSource cancel = new CancellationTokenSource(100))
                {
                    Stopwatch clock = Stopwatch.StartNew(); bool cancelled = false;
                    try { await NetworkMonitor.RunHttpEndpointAsync(target, 1000, 3, null, "Direct", null, cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
                    using (TcpClient client = await accept) { }
                    Assert(cancelled && clock.ElapsedMilliseconds < 1200, "HTTP pending-header cancellation aborts socket");
                }
                accept = silent.AcceptTcpClientAsync();
                Stopwatch timeoutClock = Stopwatch.StartNew();
                MonitorSummary timeout = await NetworkMonitor.RunHttpEndpointAsync(target, 1000, 1, null, "Direct", null, CancellationToken.None);
                using (TcpClient client = await accept) { }
                Assert(timeout.Received == 0 && timeout.LossPercent == 100 && timeout.ErrorCode == "HttpTimeout" && timeoutClock.ElapsedMilliseconds < 3800, "HTTP no-response timeout and statistics bounded to 3 seconds");
            }
            finally { silent.Stop(); }
            bool invalid = false;
            try { await NetworkMonitor.RunHttpAsync("https://example.com/private?token=secret", 1000, 1, false, null, CancellationToken.None); } catch (ArgumentException) { invalid = true; }
            Assert(invalid, "public HTTPS API rejects arbitrary URL path, scheme, query and port");
        }

        private static MonitorTarget Target(string id) { return new MonitorTarget { Id = id, Host = "127.0.0.1", Method = "icmp" }; }

        private static async Task MultiInvalid(MonitorTarget[] targets, int interval, int count)
        {
            int probes = 0;
            bool rejected = false;
            try
            {
                await NetworkMonitor.RunTargetsCoreAsync(targets, interval, count, null, CancellationToken.None, delegate(MonitorTarget target, int delay, int samples, IProgress<MonitorSample> progress, CancellationToken token)
                {
                    probes++;
                    return Task.FromResult(new MonitorSummary());
                });
            }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && probes == 0, "all target validation precedes every probe");
        }

        private static async Task MultiMonitorFixtures()
        {
            await MultiInvalid(null, 1000, 1);
            await MultiInvalid(new MonitorTarget[0], 1000, 1);
            await MultiInvalid(new[] { Target("a"), Target("b"), Target("c"), Target("d"), Target("e") }, 1000, 1);
            await MultiInvalid(new[] { Target("a"), null }, 1000, 1);
            foreach (string id in new[] { "a", "", null, new string('a', 33), "a b", "a.b", "目标", "a\n" })
                await MultiInvalid(new[] { Target("a"), Target(id) }, 1000, 1);
            foreach (string method in new[] { "ICMP", "HTTPS", "http", "dns", "", null })
            {
                MonitorTarget bad = Target("b"); bad.Method = method;
                await MultiInvalid(new[] { Target("a"), bad }, 1000, 1);
            }
            foreach (string host in new[] { "", null, "https://127.0.0.1/", "127.0.0.1:443", "a..test" })
            {
                MonitorTarget bad = Target("b"); bad.Host = host;
                await MultiInvalid(new[] { Target("a"), bad }, 1000, 1);
            }
            await MultiInvalid(new[] { Target("a") }, 999, 1);
            await MultiInvalid(new[] { Target("a") }, 10001, 1);
            await MultiInvalid(new[] { Target("a") }, 1000, 0);
            await MultiInvalid(new[] { Target("a") }, 1000, 3601);

            int probes = 0;
            MonitorTarget[] maximum = { Target(new string('a', 32)), Target("A_1"), Target("a-1"), Target("fourth") };
            MultiMonitorSummary maximumResult = await NetworkMonitor.RunTargetsCoreAsync(maximum, 1000, 1, null, CancellationToken.None,
                delegate(MonitorTarget target, int interval, int count, IProgress<MonitorSample> progress, CancellationToken token) { probes++; return Task.FromResult(new MonitorSummary()); });
            Assert(probes == 4 && maximumResult.Targets.Length == 4, "accept four targets and boundary-length ASCII IDs");
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); probes = 0; bool stopped = false;
                try
                {
                    await NetworkMonitor.RunTargetsCoreAsync(maximum, 1000, 1, null, cancelled.Token,
                        delegate(MonitorTarget target, int interval, int count, IProgress<MonitorSample> progress, CancellationToken token) { probes++; return Task.FromResult(new MonitorSummary()); });
                }
                catch (OperationCanceledException) { stopped = true; }
                Assert(stopped && probes == 0, "pre-cancelled multi-monitor never starts any probe");
            }

            MonitorTarget[] input = { Target("z-last-name"), Target("A_first") };
            input[0].Host = " 127.0.0.1 ";
            Samples samples = new Samples();
            Task<MultiMonitorSummary> running = NetworkMonitor.RunTargetsAsync(input, 1000, 2, samples, CancellationToken.None);
            input[0].Id = "changed"; input[0].Host = "invalid host"; input[0].Method = "https"; input[0].UseSystemProxy = true;
            input[1] = Target("replacement");
            MultiMonitorSummary result = await running;
            Assert(result.Targets.Select(delegate(MonitorSummary s) { return s.TargetId; }).SequenceEqual(new[] { "z-last-name", "A_first" }), "summary order follows original input and copied IDs");
            Assert(result.StartedUtc <= result.EndedUtc && result.Targets.All(delegate(MonitorSummary s) { return s.StartedUtc >= result.StartedUtc && s.EndedUtc <= result.EndedUtc; }), "multi-target timing encloses all probes");
            Assert(result.Targets.All(delegate(MonitorSummary s) { return s.Sent == 2 && s.Received == 2 && s.TargetHost == "127.0.0.1" && s.Host == "127.0.0.1" && s.Method == "ICMP" && s.LossPercent == 0 && s.JitterMs.HasValue; }), "each loopback target retains independent count and statistics after input mutation");
            Assert(samples.Values.Count == 4 && samples.Values.All(delegate(MonitorSample s) { return s.TargetHost == "127.0.0.1"; }), "every concurrent sample has normalized target host");
            foreach (MonitorSummary target in result.Targets)
                Assert(samples.Values.Where(delegate(MonitorSample s) { return s.TargetId == target.TargetId; }).Select(delegate(MonitorSample s) { return s.Sent; }).SequenceEqual(new[] { 1, 2 }), "each stable target ID owns its sample sequence");

            result = await NetworkMonitor.RunTargetsCoreAsync(new[] { Target("slow"), Target("resolve-failure"), Target("fast") }, 1000, 1, null, CancellationToken.None,
                async delegate(MonitorTarget target, int interval, int count, IProgress<MonitorSample> progress, CancellationToken token)
                {
                    if (target.Id == "resolve-failure") return new MonitorSummary { Host = target.Host, Status = "Unknown", ErrorCode = "ResolveHostNotFound" };
                    if (target.Id == "slow") await Task.Delay(80, token);
                    return await NetworkMonitor.RunAsync(target.Host, interval, count, progress, token);
                });
            Assert(result.Targets.Select(delegate(MonitorSummary s) { return s.TargetId; }).SequenceEqual(new[] { "slow", "resolve-failure", "fast" }), "out-of-order completion preserves input summary order");
            Assert(result.Targets[1].ErrorCode == "ResolveHostNotFound" && result.Targets[1].Sent == 0 && result.Targets[1].TargetHost == "127.0.0.1" && result.Targets[0].Received == 1 && result.Targets[2].Received == 1, "resolver failure remains target-local and other real probes finish");

            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                Samples updates = new Samples();
                running = NetworkMonitor.RunTargetsAsync(new[] { Target("left"), Target("right") }, 10000, 100, updates, cancel.Token);
                Assert(await Task.WhenAny(updates.FirstTwo.Task, Task.Delay(2000)) == updates.FirstTwo.Task, "both targets reach monitor delay");
                Stopwatch clock = Stopwatch.StartNew(); cancel.Cancel(); bool cancelled = false;
                try { await running; } catch (OperationCanceledException) { cancelled = true; }
                await Task.Delay(100);
                Assert(cancelled && clock.ElapsedMilliseconds < 1200 && updates.Values.Count == 2, "cancel all target delays promptly without later samples");
            }

            int settled = 0;
            Stopwatch failureClock = Stopwatch.StartNew(); bool faulted = false;
            try
            {
                await NetworkMonitor.RunTargetsCoreAsync(new[] { Target("fault"), Target("sibling") }, 1000, 1, null, CancellationToken.None,
                    async delegate(MonitorTarget target, int interval, int count, IProgress<MonitorSample> progress, CancellationToken token)
                    {
                        try
                        {
                            await Task.Delay(target.Id == "fault" ? 50 : 10000, token);
                            throw new InvalidOperationException("fixture");
                        }
                        finally { Interlocked.Increment(ref settled); }
                    });
            }
            catch (InvalidOperationException) { faulted = true; }
            Assert(faulted && settled == 2 && failureClock.ElapsedMilliseconds < 1200, "unexpected fault cancels and awaits all sibling tasks");
            await MultiHttpCancellation();
        }

        private static async Task MultiHttpCancellation()
        {
            TcpListener server = new TcpListener(IPAddress.Loopback, 0);
            server.Start();
            List<TcpClient> clients = new List<TcpClient>();
            try
            {
                Uri endpoint = new Uri("http://127.0.0.1:" + ((IPEndPoint)server.LocalEndpoint).Port + "/");
                Task accepting = Task.Run(async delegate
                {
                    for (int i = 0; i < 2; i++)
                    {
                        TcpClient client = await server.AcceptTcpClientAsync(); clients.Add(client);
                        await ReadHeaders(client.GetStream());
                    }
                });
                using (CancellationTokenSource cancel = new CancellationTokenSource())
                {
                    MonitorTarget[] targets = { Target("https-a"), Target("https-b") };
                    foreach (MonitorTarget target in targets) target.Method = "https";
                    Task<MultiMonitorSummary> running = NetworkMonitor.RunTargetsCoreAsync(targets, 1000, 3, null, cancel.Token,
                        delegate(MonitorTarget target, int interval, int count, IProgress<MonitorSample> progress, CancellationToken token)
                        {
                            return NetworkMonitor.RunHttpEndpointAsync(endpoint, interval, count, null, "Direct", progress, token);
                        });
                    Assert(await Task.WhenAny(accepting, Task.Delay(2500)) == accepting, "two HTTP transports are concurrently pending on loopback");
                    await accepting;
                    Stopwatch clock = Stopwatch.StartNew(); cancel.Cancel(); bool cancelled = false;
                    try { await running; } catch (OperationCanceledException) { cancelled = true; }
                    Assert(cancelled && clock.ElapsedMilliseconds < 1200, "cancel all pending HTTP transports promptly");
                    foreach (TcpClient client in clients)
                    {
                        Task<int> closed = client.GetStream().ReadAsync(new byte[1], 0, 1);
                        Assert(await Task.WhenAny(closed, Task.Delay(1000)) == closed && await closed == 0, "HTTP cancellation closes every target socket");
                    }
                }
            }
            finally { server.Stop(); foreach (TcpClient client in clients) client.Close(); }
        }
        public static int Main()
        {
            int previousErrorLimit = HttpWebRequest.DefaultMaximumErrorResponseLength;
            try
            {
                // Match the process-entry policy: probes never need buffered error bodies, including CONNECT failures.
                HttpWebRequest.DefaultMaximumErrorResponseLength = 0;
                Run().GetAwaiter().GetResult(); Console.WriteLine("NetworkMonitorTests passed: " + assertions); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { HttpWebRequest.DefaultMaximumErrorResponseLength = previousErrorLimit; }
        }
    }
}
