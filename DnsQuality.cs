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
    public sealed class DnsQualitySample
    {
        public int Round;
        public DateTime TimeUtc;
        // This is the elapsed duration of the complete concurrent A + AAAA round, not DNS RTT.
        public long DurationMs;
        public string Status, ErrorCode;
        public bool ResponseReceived, ResolutionSucceeded, TimedOut;
        public DnsQueryResult[] Queries = new DnsQueryResult[0];
    }

    public sealed class DnsQualityServer
    {
        public string Server;
        public int AttemptedRounds, CompletedRounds;
        public DnsQualitySample[] Samples = new DnsQualitySample[0];
        // Percentages use completed rounds only. A cancelled in-flight round has no inferred query outcome.
        // Response: any valid A/AAAA response. Resolution: any NOERROR address. Timeout: any timed-out query.
        public double? ResponseRatePercent, ResolutionSuccessRatePercent, TimeoutRatePercent;
        // Successful rounds only; each value measures the complete A + AAAA round duration.
        public double? MinMs, MedianMs, P95Ms, MaxMs;
    }

    public sealed class DnsQualityReport
    {
        public string Host, Status, ErrorCode;
        public DateTime StartedUtc, EndedUtc;
        public int RequestedRounds;
        public DnsQualityServer[] Servers = new DnsQualityServer[0];
    }

    public static class DnsQuality
    {
        public const int DefaultRounds = 5;
        public const int MinimumIntervalMs = 250;
        public const int DeadlineMs = 60000;

        public static Task<DnsQualityReport> RunAsync(string input, string[] servers, int rounds, IProgress<DnsQualityReport> progress, CancellationToken token)
        {
            return RunCoreAsync(input, servers, rounds, MinimumIntervalMs, DeadlineMs, progress, token,
                delegate(string host, IPAddress server, CancellationToken cancellation)
                { return NetworkMonitor.CompareServer(host, server, 53, null, cancellation); });
        }

        // The injectable runner reuses the same transport in tests with a loopback endpoint and arbitrary port.
        internal static async Task<DnsQualityReport> RunCoreAsync(string input, string[] servers, int rounds, int intervalMs, int deadlineMs,
            IProgress<DnsQualityReport> progress, CancellationToken token, Func<string, IPAddress, CancellationToken, Task<DnsComparisonResult>> runner)
        {
            string host, ignored;
            IPAddress literal;
            if (!AdvancedTools.TryNormalizeHost(input, out host, out ignored) || IPAddress.TryParse(host, out literal)) throw new ArgumentException("DnsHostRequired");
            if (servers == null || servers.Length < 1 || servers.Length > 4) throw new ArgumentException("InvalidDnsServerCount");
            if (rounds < 3 || rounds > 10) throw new ArgumentException("InvalidDnsQualityRounds");
            if (intervalMs < MinimumIntervalMs) throw new ArgumentException("InvalidDnsQualityInterval");
            if (deadlineMs < 1 || deadlineMs > DeadlineMs) throw new ArgumentException("InvalidDnsQualityDeadline");
            if (runner == null) throw new ArgumentNullException("runner");
            List<IPAddress> addresses = new List<IPAddress>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string value in servers)
            {
                IPAddress address;
                if (!IPAddress.TryParse((value ?? "").Trim(), out address)) throw new ArgumentException("InvalidDnsServer");
                if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.IsIPv6Multicast ||
                    (address.AddressFamily == AddressFamily.InterNetwork && address.GetAddressBytes()[0] >= 224)) throw new ArgumentException("InvalidDnsServer");
                if (unique.Add(address.ToString())) addresses.Add(address);
            }
            DnsQualityReport state = new DnsQualityReport { Host = host, StartedUtc = DateTime.UtcNow, RequestedRounds = rounds, Status = "Running",
                Servers = addresses.Select(delegate(IPAddress a) { return new DnsQualityServer { Server = a.ToString() }; }).ToArray() };
            object sync = new object();
            using (CancellationTokenSource deadline = new CancellationTokenSource())
            using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token))
            {
                deadline.CancelAfter(deadlineMs);
                if (progress != null) progress.Report(Snapshot(state));
                Task[] tasks = new Task[addresses.Count];
                for (int i = 0; i < addresses.Count; i++)
                    tasks[i] = RunServerAsync(host, addresses[i], state.Servers[i], rounds, intervalMs, runner, state, sync, progress, linked.Token, token);
                await Task.WhenAll(tasks).ConfigureAwait(false);
                lock (sync)
                {
                    state.Status = token.IsCancellationRequested ? "Cancelled" : deadline.IsCancellationRequested ? "Deadline" : "Complete";
                    state.ErrorCode = state.Status == "Cancelled" ? "DnsQualityCancelled" : state.Status == "Deadline" ? "DnsQualityDeadline" : null;
                    state.EndedUtc = DateTime.UtcNow;
                    DnsQualityReport result = Snapshot(state);
                    if (progress != null) progress.Report(Snapshot(state));
                    return result;
                }
            }
        }

        private static async Task RunServerAsync(string host, IPAddress address, DnsQualityServer server, int rounds, int intervalMs,
            Func<string, IPAddress, CancellationToken, Task<DnsComparisonResult>> runner, DnsQualityReport state, object sync,
            IProgress<DnsQualityReport> progress, CancellationToken linked, CancellationToken caller)
        {
            for (int round = 1; round <= rounds; round++)
            {
                if (linked.IsCancellationRequested) break;
                DnsQualitySample sample = new DnsQualitySample { Round = round, TimeUtc = DateTime.UtcNow };
                Stopwatch clock = Stopwatch.StartNew();
                try
                {
                    DnsComparisonResult result = await runner(host, address, linked).ConfigureAwait(false);
                    sample.DurationMs = result.DurationMs;
                    sample.Status = result.Status;
                    sample.ErrorCode = result.ErrorCode;
                    sample.Queries = result.Queries.Select(CloneQuery).ToArray();
                    sample.ResponseReceived = sample.Queries.Any(delegate(DnsQueryResult q) { return q.Status == "Answered" && q.Rcode.HasValue; });
                    sample.ResolutionSucceeded = sample.Queries.Any(delegate(DnsQueryResult q) { return q.Status == "Answered" && q.Rcode == 0 && q.Addresses.Length > 0; });
                    sample.TimedOut = sample.Queries.Any(delegate(DnsQueryResult q) { return q.ErrorCode == "DnsTimeout"; });
                    lock (sync) { server.CompletedRounds++; AddSample(server, sample); if (progress != null) progress.Report(Snapshot(state)); }
                }
                catch (OperationCanceledException)
                {
                    if (!linked.IsCancellationRequested) throw;
                    sample.DurationMs = clock.ElapsedMilliseconds;
                    sample.Status = caller.IsCancellationRequested ? "Cancelled" : "Deadline";
                    sample.ErrorCode = caller.IsCancellationRequested ? "DnsQualityCancelled" : "DnsQualityDeadline";
                    // CompareServer cannot return partial query evidence when interrupted. Leave Queries empty.
                    lock (sync) { AddSample(server, sample); if (progress != null) progress.Report(Snapshot(state)); }
                    break;
                }
                if (round == rounds) break;
                try { await Task.Delay(intervalMs, linked).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        private static void AddSample(DnsQualityServer server, DnsQualitySample sample)
        {
            server.Samples = server.Samples.Concat(new[] { sample }).ToArray();
            server.AttemptedRounds = server.Samples.Length;
        }

        private static DnsQueryResult CloneQuery(DnsQueryResult q)
        {
            return new DnsQueryResult { QueryType = q.QueryType, Status = q.Status, ErrorCode = q.ErrorCode, Rcode = q.Rcode,
                Addresses = (string[])q.Addresses.Clone(), Truncated = q.Truncated, UsedTcp = q.UsedTcp };
        }

        private static DnsQualitySample CloneSample(DnsQualitySample s)
        {
            return new DnsQualitySample { Round = s.Round, TimeUtc = s.TimeUtc, DurationMs = s.DurationMs, Status = s.Status, ErrorCode = s.ErrorCode,
                ResponseReceived = s.ResponseReceived, ResolutionSucceeded = s.ResolutionSucceeded, TimedOut = s.TimedOut, Queries = s.Queries.Select(CloneQuery).ToArray() };
        }

        private static DnsQualityReport Snapshot(DnsQualityReport state)
        {
            DnsQualityReport copy = new DnsQualityReport { Host = state.Host, Status = state.Status, ErrorCode = state.ErrorCode,
                StartedUtc = state.StartedUtc, EndedUtc = state.EndedUtc, RequestedRounds = state.RequestedRounds };
            copy.Servers = state.Servers.Select(delegate(DnsQualityServer server)
            {
                DnsQualityServer value = new DnsQualityServer { Server = server.Server, AttemptedRounds = server.AttemptedRounds,
                    CompletedRounds = server.CompletedRounds, Samples = server.Samples.Select(CloneSample).ToArray() };
                DnsQualitySample[] completed = value.Samples.Where(delegate(DnsQualitySample s) { return s.Status != "Cancelled" && s.Status != "Deadline"; }).ToArray();
                if (completed.Length > 0)
                {
                    value.ResponseRatePercent = 100.0 * completed.Count(delegate(DnsQualitySample s) { return s.ResponseReceived; }) / completed.Length;
                    value.ResolutionSuccessRatePercent = 100.0 * completed.Count(delegate(DnsQualitySample s) { return s.ResolutionSucceeded; }) / completed.Length;
                    value.TimeoutRatePercent = 100.0 * completed.Count(delegate(DnsQualitySample s) { return s.TimedOut; }) / completed.Length;
                }
                long[] durations = completed.Where(delegate(DnsQualitySample s) { return s.ResolutionSucceeded; }).Select(delegate(DnsQualitySample s) { return s.DurationMs; }).OrderBy(delegate(long n) { return n; }).ToArray();
                if (durations.Length > 0)
                {
                    value.MinMs = durations[0]; value.MaxMs = durations[durations.Length - 1];
                    value.MedianMs = durations.Length % 2 == 0 ? (durations[durations.Length / 2 - 1] / 2.0 + durations[durations.Length / 2] / 2.0) : durations[durations.Length / 2];
                    // Nearest-rank p95; no unobserved values are synthesized.
                    value.P95Ms = durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
                }
                return value;
            }).ToArray();
            return copy;
        }
    }
}
