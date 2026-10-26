using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class AdvancedToolsTests
    {
        private static int assertions;
        private sealed class Lines : IProgress<string>
        {
            public readonly List<string> Values = new List<string>();
            public void Report(string value) { lock (Values) Values.Add(value); }
        }

        private sealed class Samples : IProgress<AdvancedToolSample>
        {
            public readonly List<AdvancedToolSample> Values = new List<AdvancedToolSample>();
            public void Report(AdvancedToolSample value) { lock (Values) Values.Add(value); }
        }

        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }

        private static void SampleLimitChecks()
        {
            foreach (int count in new[] { 512, 513, 3000 })
            {
                AdvancedToolResult result = new AdvancedToolResult { Success = true, Summary = "lookup complete" };
                Samples samples = new Samples();
                for (int i = 0; i < count; i++)
                    AdvancedTools.AddSample(result, samples, new AdvancedToolSample { Kind = "Dns", Address = "127.0.0.1", RecordType = "A", Status = "Success", Responded = true });
                Assert(result.Rows.Count == 512 && samples.Values.Count == 512, "typed DNS rows and callbacks stop at 512 for " + count);
                Assert(result.TotalRows == count && result.Truncated == (count > 512), "typed DNS total and truncation remain explicit for " + count);
                Assert(Object.ReferenceEquals(result.Rows[511], samples.Values[511]) && result.Success && result.Summary == "lookup complete", "row identity and overall result survive limiting for " + count);
            }
        }

        private static async Task NetworkChecksAsync()
        {
            Lines messages = new Lines();
            Samples samples = new Samples();
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            int port;
            listener.Start();
            try
            {
                port = ((IPEndPoint)listener.LocalEndpoint).Port;
                AdvancedToolResult result = await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "127.0.0.1", port, messages, CancellationToken.None, samples);
                Assert(result.Success, "actual IPv4 loopback TCP handshake");
                Assert(messages.Values.Exists(delegate(string line) { return line.Contains("已连接"); }), "TCP evidence includes actual connection");
                Assert(result.Tool == "Tcp" && result.Host == "127.0.0.1" && result.Rows.Count == 1 && result.Sent == 1 && result.Received == 1, "TCP result carries target and handshake counts");
                AdvancedToolSample row = result.Rows[0];
                Assert(row.Kind == "Tcp" && row.Address == "127.0.0.1" && row.Port == port && row.Status == "Success" && row.Responded && row.ErrorCode == null, "TCP accept row reflects actual endpoint");
                Assert(row.DurationMs >= 0 && result.DurationMs >= row.DurationMs && row.RttMs == null && result.AverageRttMs == null, "TCP duration is separate from ICMP RTT");
                Assert(samples.Values.Count == 1 && Object.ReferenceEquals(row, samples.Values[0]), "TCP progress and result retain the same row");
            }
            finally { listener.Stop(); }
            AdvancedToolResult closed = await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "127.0.0.1", port, null, CancellationToken.None);
            Assert(!closed.Success, "closed loopback port returns a result rather than claiming Internet failure");
            Assert(closed.Tool == "Tcp" && closed.Host == "127.0.0.1" && closed.Rows.Count == 1 && closed.Sent == 1 && closed.Received == 0, "TCP rejection preserves typed context and counts");
            Assert(closed.Rows[0].Status == "Error" && closed.Rows[0].ErrorCode == SocketError.ConnectionRefused.ToString() && !closed.Rows[0].Responded && closed.Rows[0].DurationMs >= 0 && closed.DurationMs >= closed.Rows[0].DurationMs, "closed port records socket rejection and elapsed time");

            if (Socket.OSSupportsIPv6)
            {
                TcpListener v6 = new TcpListener(IPAddress.IPv6Loopback, 0);
                v6.Start();
                try
                {
                    int v6Port = ((IPEndPoint)v6.LocalEndpoint).Port;
                    AdvancedToolResult result = await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "[::1]", v6Port, null, CancellationToken.None);
                    Assert(result.Success, "actual IPv6 loopback TCP handshake");
                    Assert(result.Host == "::1" && result.Rows.Count == 1 && result.Rows[0].Address == "::1" && result.Rows[0].Port == v6Port, "TCP normalizes bracketed IPv6 in typed result");
                }
                finally { v6.Stop(); }
            }
            using (CancellationTokenSource cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                samples = new Samples();
                bool stopped = false;
                try { await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "127.0.0.1", port, null, cancelled.Token, samples); }
                catch (OperationCanceledException) { stopped = true; }
                Assert(stopped, "cancelled TCP does not create a new test");
                Assert(samples.Values.Count == 0, "pre-cancelled run emits no fabricated sample");
            }
            using (CancellationTokenSource cancelPing = new CancellationTokenSource())
            {
                Stopwatch watch = Stopwatch.StartNew();
                cancelPing.CancelAfter(100);
                bool stopped = false;
                try { await AdvancedTools.RunAsync(AdvancedToolKind.Ping, "127.0.0.1", 443, null, cancelPing.Token); }
                catch (OperationCanceledException) { stopped = true; }
                Assert(stopped && watch.ElapsedMilliseconds < 2000, "in-flight Ping series cancellation completes promptly");
            }
            messages = new Lines();
            samples = new Samples();
            AdvancedToolResult ping = await AdvancedTools.RunAsync(AdvancedToolKind.Ping, "127.0.0.1", 443, messages, CancellationToken.None, samples);
            Assert(ping.Summary.Contains("发送 4") && ping.Summary.Contains("丢包"), "Ping reports four probes and loss");
            Assert(messages.Values.FindAll(delegate(string line) { return line.StartsWith("第 ", StringComparison.Ordinal); }).Count == 4, "each Ping has its own evidence line");
            Assert(ping.Tool == "Ping" && ping.Host == "127.0.0.1" && ping.Rows.Count == 4 && samples.Values.Count == 4 && ping.Sent == 4, "Ping exposes exactly four actual samples");
            Assert(ping.Received == ping.Rows.Count(delegate(AdvancedToolSample row) { return row.Responded; }) && ping.Received == 4, "loopback Ping receives four successful replies");
            Assert(ping.AverageRttMs.HasValue && ping.AverageRttMs.Value == ping.Rows.Average(delegate(AdvancedToolSample row) { return (double)row.RttMs.Value; }), "Ping average matches actual RTT rows");
            for (int i = 0; i < 4; i++)
            {
                AdvancedToolSample row = ping.Rows[i];
                Assert(Object.ReferenceEquals(row, samples.Values[i]) && row.Kind == "Ping" && row.Sequence == i + 1 && row.Hop == null && row.Port == null && row.Address == "127.0.0.1" && row.Status == "Success", "Ping sample identity, order and status");
                Assert(row.RttMs.HasValue && row.DurationMs >= 0 && ping.DurationMs >= row.DurationMs, "Ping preserves real RTT and elapsed duration");
            }
            AdvancedToolResult trace = await AdvancedTools.RunAsync(AdvancedToolKind.Trace, "127.0.0.1", 443, null, CancellationToken.None);
            Assert(trace.Success && trace.Summary.Contains("第 1 跳"), "actual loopback route trace stops at target");
            Assert(trace.Tool == "Trace" && trace.Rows.Count == 1 && trace.Sent == 1 && trace.Received == 1 && trace.Rows[0].Hop == 1 && trace.Rows[0].Sequence == null && trace.Rows[0].Status == "Success" && trace.Rows[0].Responded, "trace reports the actual reached hop");
            messages = new Lines();
            samples = new Samples();
            AdvancedToolResult dns = await AdvancedTools.RunAsync(AdvancedToolKind.Dns, "::1", 443, messages, CancellationToken.None, samples);
            Assert(dns.Success && messages.Values.Exists(delegate(string line) { return line.StartsWith("AAAA", StringComparison.Ordinal) && line.Contains("::1"); }), "DNS address result preserves IPv6");
            Assert(dns.Tool == "Dns" && dns.Host == "::1" && dns.Rows.Count == 1 && dns.Rows[0].Kind == "Dns" && dns.Rows[0].RecordType == "AAAA" && dns.Rows[0].Address == "::1" && dns.Rows[0].Status == "Success" && dns.Rows[0].Responded, "DNS has an actual IPv6 address row");
            Assert(Object.ReferenceEquals(dns.Rows[0], samples.Values[0]) && dns.Rows[0].RttMs == null && dns.Rows[0].DurationMs >= 0 && dns.DurationMs >= dns.Rows[0].DurationMs, "DNS progress preserves row and lookup timing without inventing RTT");
            dns = await AdvancedTools.RunAsync(AdvancedToolKind.Dns, "127.0.0.1", 443, null, CancellationToken.None);
            Assert(dns.Rows.Count == 1 && dns.Rows[0].RecordType == "A" && dns.Rows[0].Address == "127.0.0.1", "DNS classifies IPv4 as A");
            samples = new Samples();
            bool rejected = false;
            try { await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "http://127.0.0.1", port, null, CancellationToken.None, samples); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && samples.Values.Count == 0, "invalid target throws before any sample");
            rejected = false;
            try { await AdvancedTools.RunAsync(AdvancedToolKind.Tcp, "127.0.0.1", 0, null, CancellationToken.None, samples); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && samples.Values.Count == 0, "invalid TCP port throws before any sample");
            rejected = false;
            try { await AdvancedTools.RunAsync((AdvancedToolKind)99, "127.0.0.1", port, null, CancellationToken.None, samples); }
            catch (ArgumentException) { rejected = true; }
            Assert(rejected && samples.Values.Count == 0, "invalid tool throws before any sample");
        }

        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                SampleLimitChecks();
                if (args.Length == 1 && args[0] == "--sample-limits-only")
                {
                    Console.WriteLine("PASS: " + assertions + " sample limit assertions. No network probes.");
                    return 0;
                }
                string host, error;
                foreach (string valid in new[] { "127.0.0.1", "::1", "[::1]", "2001:db8::1", "example.com", "example.com.", "router", "  localhost  ", "例子.中国" })
                    Assert(AdvancedTools.TryNormalizeHost(valid, out host, out error), "valid host: " + valid);
                Assert(AdvancedTools.TryNormalizeHost("例子.中国", out host, out error) && host.Contains("xn--"), "IDN becomes actual ASCII DNS name");
                foreach (string invalid in new[] { "", "http://example.com", "https://example.com/path", "example.com:443", "host & calc", "host;calc", "host\r\n-a", "-host", "host-", "host_name", "a..b", ".host", "[example.com]", "host/path", "user@host", new string('a', 64) + ".com", new string('a', 1025) })
                    Assert(!AdvancedTools.TryNormalizeHost(invalid, out host, out error), "invalid host rejected");
                int port;
                foreach (string valid in new[] { "1", "443", "65535", " 80 " }) Assert(AdvancedTools.TryParsePort(valid, out port), "valid port");
                foreach (string invalid in new[] { "0", "65536", "-1", "+80", "80x", "80 & calc", "", "99999999999" }) Assert(!AdvancedTools.TryParsePort(invalid, out port), "invalid port rejected");
                NetworkChecksAsync().GetAwaiter().GetResult();
                Console.WriteLine("PASS: " + assertions + " advanced tool assertions. Only loopback probes; no system configuration changed.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }
    }
}
