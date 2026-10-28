using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class DnsQualityTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        private sealed class Reports : IProgress<DnsQualityReport>
        {
            public readonly List<DnsQualityReport> Values = new List<DnsQualityReport>();
            public Action<DnsQualityReport> OnReport;
            public void Report(DnsQualityReport value) { Values.Add(value); if (OnReport != null) OnReport(value); }
        }
        private static DnsComparisonResult Result(string host, IPAddress ip, long duration, string kind)
        {
            DnsComparisonResult result = new DnsComparisonResult { Host = host, Server = ip.ToString(), DurationMs = duration, Status = kind == "timeout" ? "Unknown" : "Answered" };
            foreach (string type in new[] { "A", "AAAA" })
                result.Queries.Add(new DnsQueryResult { QueryType = type, Status = kind == "timeout" ? "Unknown" : "Answered",
                    ErrorCode = kind == "timeout" ? "DnsTimeout" : null, Rcode = kind == "timeout" ? (int?)null : kind == "nxdomain" ? 3 : kind == "servfail" ? 2 : 0,
                    Addresses = kind == "success" && type == "A" ? new[] { "192.0.2.5" } : new string[0] });
            return result;
        }
        private static Task<DnsQualityReport> Run(string[] servers, int rounds, int deadline, IProgress<DnsQualityReport> progress,
            CancellationToken token, Func<string, IPAddress, CancellationToken, Task<DnsComparisonResult>> runner)
        { return DnsQuality.RunCoreAsync("example.test", servers, rounds, 250, deadline, progress, token, runner); }

        private static async Task Validation()
        {
            int calls = 0;
            Func<string, IPAddress, CancellationToken, Task<DnsComparisonResult>> runner = delegate(string h, IPAddress a, CancellationToken t)
            { calls++; return Task.FromResult(Result(h, a, 1, "success")); };
            string[][] invalid = { null, new string[0], new[] { "127.0.0.1", "invalid" }, new[] { "0.0.0.0" }, new[] { "::" }, new[] { "224.0.0.1" }, new[] { "ff02::1" }, new[] { "::ffff:224.0.0.1" }, new[] { "1.1.1.1", "1.1.1.1", "1.1.1.1", "1.1.1.1", "1.1.1.1" } };
            foreach (string[] servers in invalid)
            {
                bool rejected = false;
                try { await Run(servers, 3, 60000, null, CancellationToken.None, runner); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && calls == 0, "all server inputs validated before first probe");
            }
            foreach (string host in new[] { "", "127.0.0.1", "https://example.test", "bad host" })
            {
                bool rejected = false;
                try { await DnsQuality.RunCoreAsync(host, new[] { "127.0.0.1" }, 3, 250, 60000, null, CancellationToken.None, runner); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && calls == 0, "invalid/non-domain host rejected before probes");
            }
            foreach (int rounds in new[] { 2, 11 })
            {
                bool rejected = false;
                try { await Run(new[] { "127.0.0.1" }, rounds, 60000, null, CancellationToken.None, runner); } catch (ArgumentException) { rejected = true; }
                Assert(rejected && calls == 0, "round bounds enforced");
            }
            bool intervalRejected = false;
            try { await DnsQuality.RunCoreAsync("example.test", new[] { "127.0.0.1" }, 3, 249, 60000, null, CancellationToken.None, runner); } catch (ArgumentException) { intervalRejected = true; }
            Assert(intervalRejected && calls == 0, "minimum interval enforced");
        }

        private static async Task Statistics()
        {
            int call = 0;
            List<long> starts = new List<long>(); Stopwatch clock = Stopwatch.StartNew();
            Reports updates = new Reports();
            DnsQualityReport report = await Run(new[] { "127.0.0.1", "::ffff:127.0.0.1", "127.0.0.1" }, 5, 60000, updates, CancellationToken.None,
                delegate(string h, IPAddress a, CancellationToken t)
                { starts.Add(clock.ElapsedMilliseconds); string kind = call == 0 ? "success" : call == 1 ? "nxdomain" : call == 2 ? "timeout" : call == 3 ? "success" : "nodata"; call++; return Task.FromResult(Result(h, a, call == 1 ? 10 : 30, kind)); });
            DnsQualityServer server = report.Servers.Single();
            Assert(report.Status == "Complete" && call == 5 && server.Server == "127.0.0.1", "normalized duplicate servers only probed once per round");
            Assert(server.AttemptedRounds == 5 && server.CompletedRounds == 5 && server.Samples.Length == 5, "requested real rounds retained");
            Assert(server.ResponseRatePercent == 80 && server.ResolutionSuccessRatePercent == 40 && server.TimeoutRatePercent == 20, "response, address resolution and timeout rates distinguish DNS errors");
            Assert(server.MinMs == 10 && server.MedianMs == 20 && server.P95Ms == 30 && server.MaxMs == 30, "successful A+AAAA round durations min median nearest-rank p95 max");
            Assert(server.Samples[0].Queries[1].Addresses.Length == 0 && server.Samples[0].ResolutionSucceeded, "AAAA NODATA does not defeat successful A record");
            Assert(server.Samples[1].ResponseReceived && !server.Samples[1].ResolutionSucceeded && server.Samples[1].Queries[0].Rcode == 3, "NXDOMAIN is a response without resolution");
            Assert(server.Samples[4].ResponseReceived && !server.Samples[4].ResolutionSucceeded && !server.Samples[4].TimedOut, "NODATA remains DNS connectivity evidence");
            Assert(server.Samples.Select(delegate(DnsQualitySample s) { return s.Round; }).SequenceEqual(new[] { 1, 2, 3, 4, 5 }), "sample round sequence retained");
            Assert(starts.Skip(1).Zip(starts, delegate(long next, long previous) { return next - previous; }).All(delegate(long delta) { return delta >= 240; }), "rounds have at least 250 ms scheduled spacing");
            Assert(updates.Values[0].Servers[0].Samples.Length == 0 && updates.Values[0].Servers[0].ResponseRatePercent == null, "initial snapshot is untested with null statistics");
            updates.Values[1].Servers[0].Samples[0].Queries[0].Addresses[0] = "mutated";
            Assert(report.Servers[0].Samples[0].Queries[0].Addresses[0] == "192.0.2.5", "progress snapshots have independent nested evidence arrays");
            report = await Run(new[] { "127.0.0.1", "127.0.0.2" }, 3, 60000, null, CancellationToken.None,
                delegate(string h, IPAddress a, CancellationToken t) { return Task.FromResult(Result(h, a, 4, a.ToString() == "127.0.0.1" ? "servfail" : "timeout")); });
            Assert(report.Servers[0].ResponseRatePercent == 100 && report.Servers[0].ResolutionSuccessRatePercent == 0 && report.Servers[0].MedianMs == null, "SERVFAIL responds but has no success latency");
            Assert(report.Servers[1].ResponseRatePercent == 0 && report.Servers[1].TimeoutRatePercent == 100 && report.Servers[1].MinMs == null, "all timeouts have measured failure rates and no success durations");
        }

        private static async Task Cancellation()
        {
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                cancel.Cancel(); int calls = 0;
                DnsQualityReport report = await Run(new[] { "127.0.0.1" }, 3, 60000, null, cancel.Token,
                    delegate(string h, IPAddress a, CancellationToken t) { calls++; return Task.FromResult(Result(h, a, 1, "success")); });
                Assert(calls == 0 && report.Status == "Cancelled" && report.Servers[0].AttemptedRounds == 0 && report.Servers[0].TimeoutRatePercent == null, "pre-cancel does not attempt or invent failures");
            }
            using (CancellationTokenSource cancel = new CancellationTokenSource())
            {
                int calls = 0; Reports progress = new Reports();
                progress.OnReport = delegate(DnsQualityReport r) { if (r.Servers[0].CompletedRounds == 1) cancel.Cancel(); };
                DnsQualityReport report = await Run(new[] { "127.0.0.1" }, 10, 60000, progress, cancel.Token,
                    delegate(string h, IPAddress a, CancellationToken t) { calls++; return Task.FromResult(Result(h, a, 7, "success")); });
                Assert(calls == 1 && report.Status == "Cancelled" && report.Servers[0].CompletedRounds == 1 && report.Servers[0].ResolutionSuccessRatePercent == 100, "cancel during interval retains only actual completed work");
            }
            Stopwatch clock = Stopwatch.StartNew();
            DnsQualityReport deadline = await Run(new[] { "127.0.0.1", "127.0.0.2" }, 10, 100, null, CancellationToken.None,
                async delegate(string h, IPAddress a, CancellationToken t) { await Task.Delay(10000, t); return Result(h, a, 1, "success"); });
            Assert(deadline.Status == "Deadline" && deadline.ErrorCode == "DnsQualityDeadline" && clock.ElapsedMilliseconds < 1500, "global deadline bounds all servers");
            Assert(deadline.Servers.All(delegate(DnsQualityServer s) { return s.AttemptedRounds == 1 && s.CompletedRounds == 0 && s.ResponseRatePercent == null && s.Samples[0].Status == "Deadline" && s.Samples[0].Queries.Length == 0; }), "interrupted rounds preserve attempt evidence without invented query outcomes");
            using (CancellationTokenSource cancel = new CancellationTokenSource(100))
            {
                DnsQualityReport report = await Run(new[] { "127.0.0.1" }, 3, 60000, null, cancel.Token,
                    async delegate(string h, IPAddress a, CancellationToken t) { await Task.Delay(10000, t); return Result(h, a, 1, "success"); });
                Assert(report.Status == "Cancelled" && report.Servers[0].Samples[0].Status == "Cancelled" && report.Servers[0].CompletedRounds == 0, "in-flight caller cancellation is distinct from deadline");
            }
        }

        private static byte[] Answer(byte[] query, bool truncated, bool nxdomain)
        {
            byte[] answer = (byte[])query.Clone(); answer[2] = (byte)(truncated ? 131 : 129); answer[3] = (byte)(nxdomain ? 131 : 128);
            if (truncated || nxdomain || query[query.Length - 3] == 28) return answer;
            answer[7] = 1;
            using (MemoryStream bytes = new MemoryStream())
            {
                bytes.Write(answer, 0, answer.Length);
                byte[] record = { 192, 12, 0, 1, 0, 1, 0, 0, 0, 30, 0, 4, 192, 0, 2, 19 };
                bytes.Write(record, 0, record.Length); return bytes.ToArray();
            }
        }
        private static async Task<byte[]> Read(NetworkStream stream, int count)
        {
            byte[] bytes = new byte[count]; int offset = 0;
            while (offset < count) { int read = await stream.ReadAsync(bytes, offset, count - offset); if (read == 0) throw new IOException(); offset += read; }
            return bytes;
        }
        private static async Task Loopback(bool tcpFallback, bool nxdomain)
        {
            using (UdpClient udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)udp.Client.LocalEndPoint).Port;
                TcpListener tcp = new TcpListener(IPAddress.Loopback, port);
                if (tcpFallback) tcp.Start();
                try
                {
                    Task udpServing = Task.Run(async delegate
                    {
                        for (int i = 0; i < 6; i++)
                        {
                            UdpReceiveResult query = await udp.ReceiveAsync(); byte[] reply = Answer(query.Buffer, tcpFallback, nxdomain);
                            await udp.SendAsync(reply, reply.Length, query.RemoteEndPoint);
                        }
                    });
                    Task tcpServing = tcpFallback ? Task.Run(async delegate
                    {
                        for (int i = 0; i < 6; i++)
                            using (TcpClient client = await tcp.AcceptTcpClientAsync())
                            {
                                NetworkStream stream = client.GetStream(); byte[] size = await Read(stream, 2);
                                byte[] query = await Read(stream, (size[0] << 8) | size[1]); byte[] reply = Answer(query, false, nxdomain);
                                await stream.WriteAsync(new[] { (byte)(reply.Length >> 8), (byte)reply.Length }, 0, 2); await stream.WriteAsync(reply, 0, reply.Length);
                            }
                    }) : Task.FromResult(true);
                    DnsQualityReport report = await Run(new[] { "127.0.0.1" }, 3, 60000, null, CancellationToken.None,
                        delegate(string h, IPAddress a, CancellationToken t) { return NetworkMonitor.CompareServer(h, a, port, null, t); });
                    await Task.WhenAll(udpServing, tcpServing);
                    Assert(report.Status == "Complete" && report.Servers[0].CompletedRounds == 3 && report.Servers[0].ResponseRatePercent == 100, "real loopback UDP rounds respond");
                    Assert(report.Servers[0].ResolutionSuccessRatePercent == (nxdomain ? 0 : 100), "real response retains address / NXDOMAIN meaning");
                    Assert(report.Servers[0].Samples.All(delegate(DnsQualitySample s) { return s.Queries.Length == 2 && s.Queries.All(delegate(DnsQueryResult q) { return q.UsedTcp == tcpFallback; }); }), "real UDP truncation uses bounded existing TCP fallback for A and AAAA");
                }
                finally { tcp.Stop(); }
            }
        }
        private static async Task RealTimeoutAndCancellation()
        {
            // A bound, silent UDP endpoint exercises real receive timeouts without relying on external DNS.
            using (UdpClient silent = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            {
                int port = ((IPEndPoint)silent.Client.LocalEndPoint).Port;
                Func<string, IPAddress, CancellationToken, Task<DnsComparisonResult>> runner = delegate(string h, IPAddress a, CancellationToken t)
                { return NetworkMonitor.CompareServer(h, a, port, null, t); };
                Stopwatch clock = Stopwatch.StartNew();
                DnsQualityReport timeout = await Run(new[] { "127.0.0.1" }, 3, 60000, null, CancellationToken.None, runner);
                Assert(timeout.Status == "Complete" && timeout.Servers[0].CompletedRounds == 3 && timeout.Servers[0].TimeoutRatePercent == 100, "real UDP receive timeouts remain completed measured rounds");
                Assert(clock.ElapsedMilliseconds < 10000 && timeout.Servers[0].Samples.All(delegate(DnsQualitySample s) { return s.Queries.Length == 2 && s.Queries.All(delegate(DnsQueryResult q) { return q.ErrorCode == "DnsTimeout"; }); }), "existing A/AAAA transport timeout is bounded and retained");
                using (CancellationTokenSource cancel = new CancellationTokenSource(100))
                {
                    clock.Restart();
                    DnsQualityReport cancelled = await Run(new[] { "127.0.0.1" }, 3, 60000, null, cancel.Token, runner);
                    Assert(cancelled.Status == "Cancelled" && cancelled.Servers[0].CompletedRounds == 0 && cancelled.Servers[0].Samples[0].Status == "Cancelled", "real pending UDP cancellation retains interruption without fabricated query results");
                    Assert(clock.ElapsedMilliseconds < 1200, "caller cancellation promptly closes real DNS sockets");
                }
                clock.Restart();
                DnsQualityReport deadline = await Run(new[] { "127.0.0.1" }, 3, 100, null, CancellationToken.None, runner);
                Assert(deadline.Status == "Deadline" && deadline.Servers[0].CompletedRounds == 0 && deadline.Servers[0].ResponseRatePercent == null, "real pending transport obeys global deadline without inventing failures");
                Assert(clock.ElapsedMilliseconds < 1200, "global deadline promptly closes real DNS sockets");
            }
        }
        public static int Main()
        {
            try { Tests().GetAwaiter().GetResult(); Console.WriteLine("DnsQualityTests passed: " + assertions); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static async Task Tests()
        { await Validation(); await Statistics(); await Cancellation(); await Loopback(false, false); await Loopback(true, false); await Loopback(false, true); await RealTimeoutAndCancellation(); }
    }
}
