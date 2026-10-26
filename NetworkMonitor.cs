using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    public class MonitorSample
    {
        public int Sequence, Sent, Received;
        public DateTime TimeUtc;
        public bool Success;
        public long? RttMs;
        public double LossPercent;
        public double? Min, Avg, Max, JitterMs;
        public string ResolvedAddress, Status, ErrorCode;
        public string Method, Route;
        public string TargetId, TargetHost;
        public int? HttpStatusCode;
        public bool ResponseReceived, HttpSuccess;
    }

    public sealed class MonitorSummary : MonitorSample
    {
        public string Host;
        public DateTime StartedUtc, EndedUtc;
    }

    public sealed class MonitorTarget
    {
        public string Id, Host, Method;
        public bool UseSystemProxy;
    }

    public sealed class MultiMonitorSummary
    {
        public DateTime StartedUtc, EndedUtc;
        public MonitorSummary[] Targets = new MonitorSummary[0];
    }

    public sealed class DnsQueryResult
    {
        public string QueryType, Status, ErrorCode;
        public int? Rcode;
        public string[] Addresses = new string[0];
        public bool Truncated, UsedTcp;
    }

    public sealed class DnsComparisonResult
    {
        public string Server, Host, Status, ErrorCode;
        public long DurationMs;
        public List<DnsQueryResult> Queries = new List<DnsQueryResult>();
    }

    public static class NetworkMonitor
    {
        private sealed class TargetProgress : IProgress<MonitorSample>
        {
            private readonly MonitorTarget target;
            private readonly IProgress<MonitorSample> progress;
            public TargetProgress(MonitorTarget target, IProgress<MonitorSample> progress) { this.target = target; this.progress = progress; }
            public void Report(MonitorSample sample)
            {
                sample.TargetId = target.Id;
                sample.TargetHost = target.Host;
                if (progress != null) progress.Report(sample);
            }
        }

        /// <summary>Reports may arrive concurrently; use a thread-safe IProgress or Progress constructed on the UI thread.</summary>
        public static Task<MultiMonitorSummary> RunTargetsAsync(MonitorTarget[] targets, int intervalMs, int count, IProgress<MonitorSample> progress, CancellationToken token)
        {
            return RunTargetsCoreAsync(targets, intervalMs, count, progress, token, delegate(MonitorTarget target, int interval, int samples, IProgress<MonitorSample> updates, CancellationToken cancellation)
            {
                return target.Method == "icmp" ? RunAsync(target.Host, interval, samples, updates, cancellation)
                    : RunHttpAsync(target.Host, interval, samples, target.UseSystemProxy, updates, cancellation);
            });
        }

        internal static async Task<MultiMonitorSummary> RunTargetsCoreAsync(MonitorTarget[] targets, int intervalMs, int count, IProgress<MonitorSample> progress, CancellationToken token,
            Func<MonitorTarget, int, int, IProgress<MonitorSample>, CancellationToken, Task<MonitorSummary>> runTarget)
        {
            if (targets == null || targets.Length < 1 || targets.Length > 4) throw new ArgumentException("InvalidMonitorTargetCount");
            if (intervalMs < 1000 || intervalMs > 10000 || count < 1 || count > 3600) throw new ArgumentOutOfRangeException("InvalidMonitorRange");
            MonitorTarget[] snapshot = new MonitorTarget[targets.Length];
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < targets.Length; i++)
            {
                MonitorTarget target = targets[i];
                if (target == null) throw new ArgumentException("InvalidMonitorTarget");
                string id = target.Id, input = target.Host, method = target.Method, host, ignored;
                bool proxy = target.UseSystemProxy;
                if (String.IsNullOrEmpty(id) || id.Length > 32 || id.Any(delegate(char c) { return !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_'; }) || !ids.Add(id)) throw new ArgumentException("InvalidMonitorTargetId");
                if (method != "icmp" && method != "https") throw new ArgumentException("InvalidMonitorMethod");
                if (!AdvancedTools.TryNormalizeHost(input, out host, out ignored)) throw new ArgumentException("InvalidHost");
                snapshot[i] = new MonitorTarget { Id = id, Host = host, Method = method, UseSystemProxy = proxy };
            }
            token.ThrowIfCancellationRequested();
            MultiMonitorSummary summary = new MultiMonitorSummary { StartedUtc = DateTime.UtcNow };
            using (CancellationTokenSource stop = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Task<MonitorSummary>[] tasks = snapshot.Select(delegate(MonitorTarget target) { return RunTargetAsync(target, intervalMs, count, progress, stop, runTarget); }).ToArray();
                summary.Targets = await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            summary.EndedUtc = DateTime.UtcNow;
            return summary;
        }

        private static async Task<MonitorSummary> RunTargetAsync(MonitorTarget target, int intervalMs, int count, IProgress<MonitorSample> progress, CancellationTokenSource stop,
            Func<MonitorTarget, int, int, IProgress<MonitorSample>, CancellationToken, Task<MonitorSummary>> runTarget)
        {
            try
            {
                MonitorSummary summary = await runTarget(target, intervalMs, count, new TargetProgress(target, progress), stop.Token).ConfigureAwait(false);
                summary.TargetId = target.Id;
                summary.TargetHost = target.Host;
                return summary;
            }
            catch { stop.Cancel(); throw; }
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static async Task<T> Bounded<T>(Task<T> task, int milliseconds, CancellationToken token)
        {
            if (await Task.WhenAny(task, Task.Delay(milliseconds, token)).ConfigureAwait(false) != task)
            {
                Observe(task);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            token.ThrowIfCancellationRequested();
            return await task.ConfigureAwait(false);
        }

        public static async Task<MonitorSummary> RunAsync(string input, int intervalMs, int count, IProgress<MonitorSample> progress, CancellationToken token)
        {
            string host, ignored;
            if (!AdvancedTools.TryNormalizeHost(input, out host, out ignored)) throw new ArgumentException("InvalidHost");
            if (intervalMs < 1000 || intervalMs > 10000 || count < 1 || count > 3600) throw new ArgumentOutOfRangeException("InvalidMonitorRange");
            token.ThrowIfCancellationRequested();
            MonitorSummary summary = new MonitorSummary { Host = host, StartedUtc = DateTime.UtcNow, Status = "Unknown", Method = "ICMP", Route = "Network" };
            IPAddress target;
            try
            {
                if (!IPAddress.TryParse(host, out target))
                    target = (await Bounded(Dns.GetHostAddressesAsync(host), 5000, token).ConfigureAwait(false)).OrderBy(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1; }).FirstOrDefault();
                if (target == null) throw new SocketException((int)SocketError.NoData);
            }
            catch (TimeoutException) { summary.ErrorCode = "ResolveTimeout"; summary.EndedUtc = DateTime.UtcNow; return summary; }
            catch (SocketException ex) { summary.ErrorCode = "Resolve" + ex.SocketErrorCode; summary.EndedUtc = DateTime.UtcNow; return summary; }
            summary.ResolvedAddress = target.ToString();
            double total = 0, jitterTotal = 0;
            long? previous = null;
            int jitterCount = 0;
            for (int i = 1; i <= count; i++)
            {
                token.ThrowIfCancellationRequested();
                Stopwatch clock = Stopwatch.StartNew();
                MonitorSample sample = new MonitorSample { Sequence = i, TimeUtc = DateTime.UtcNow, ResolvedAddress = target.ToString(), Status = "Unknown", Method = "ICMP", Route = "Network" };
                using (Ping ping = new Ping())
                using (token.Register(delegate { try { ping.SendAsyncCancel(); } catch (InvalidOperationException) { } }))
                {
                    try
                    {
                        PingReply reply = await Bounded(ping.SendPingAsync(target, 1200), 1200, token).ConfigureAwait(false);
                        sample.Success = reply.Status == IPStatus.Success;
                        sample.Status = reply.Status.ToString();
                        if (sample.Success) sample.RttMs = reply.RoundtripTime;
                    }
                    catch (PingException) { sample.ErrorCode = "PingUnavailable"; }
                    catch (TimeoutException) { sample.Status = "TimedOut"; sample.ErrorCode = "PingTimeout"; }
                    catch (SocketException ex) { sample.ErrorCode = "Ping" + ex.SocketErrorCode; }
                }
                token.ThrowIfCancellationRequested();
                summary.Sent++;
                if (sample.Success)
                {
                    long rtt = sample.RttMs.Value;
                    summary.Received++;
                    total += rtt;
                    summary.Min = summary.Min.HasValue ? Math.Min(summary.Min.Value, rtt) : rtt;
                    summary.Max = summary.Max.HasValue ? Math.Max(summary.Max.Value, rtt) : rtt;
                    summary.Avg = total / summary.Received;
                    if (previous.HasValue) { jitterTotal += Math.Abs(rtt - previous.Value); jitterCount++; }
                    previous = rtt;
                    summary.JitterMs = jitterCount == 0 ? (double?)null : jitterTotal / jitterCount;
                }
                summary.LossPercent = 100.0 * (summary.Sent - summary.Received) / summary.Sent;
                sample.Sent = summary.Sent; sample.Received = summary.Received; sample.LossPercent = summary.LossPercent;
                sample.Min = summary.Min; sample.Max = summary.Max; sample.Avg = summary.Avg; sample.JitterMs = summary.JitterMs;
                summary.Sequence = sample.Sequence; summary.TimeUtc = sample.TimeUtc; summary.RttMs = sample.RttMs;
                summary.Success = summary.Received > 0; summary.Status = summary.Received == summary.Sent ? "Responding" : summary.Received > 0 ? "PartialResponse" : "NoIcmpResponse";
                if (progress != null) progress.Report(sample);
                if (i < count) await Task.Delay(Math.Max(0, intervalMs - (int)clock.ElapsedMilliseconds), token).ConfigureAwait(false);
            }
            summary.EndedUtc = DateTime.UtcNow;
            return summary;
        }

        public static Task<MonitorSummary> RunHttpAsync(string input, int intervalMs, int count, bool useSystemProxy, IProgress<MonitorSample> progress, CancellationToken token)
        {
            string host, ignored;
            if (!AdvancedTools.TryNormalizeHost(input, out host, out ignored)) throw new ArgumentException("InvalidHost");
            Uri target = new UriBuilder(Uri.UriSchemeHttps, host, 443, "/").Uri;
            IWebProxy proxy = useSystemProxy ? WebRequest.GetSystemWebProxy() : null;
            if (proxy != null) proxy.Credentials = null;
            return RunHttpEndpointAsync(target, intervalMs, count, proxy, useSystemProxy ? "WindowsProxy" : "Direct", progress, token);
        }

        internal static async Task<MonitorSummary> RunHttpEndpointAsync(Uri target, int intervalMs, int count, IWebProxy proxy, string route, IProgress<MonitorSample> progress, CancellationToken token)
        {
            if (intervalMs < 1000 || intervalMs > 10000 || count < 1 || count > 3600) throw new ArgumentOutOfRangeException("InvalidMonitorRange");
            if (proxy != null) proxy.Credentials = null;
            token.ThrowIfCancellationRequested();
            string method = target.Scheme == Uri.UriSchemeHttps ? "HTTPS" : "HTTP";
            MonitorSummary summary = new MonitorSummary { Host = target.Host, StartedUtc = DateTime.UtcNow, Status = "Unknown", Method = method, Route = route, HttpSuccess = true };
            double total = 0, jitterTotal = 0;
            long? previous = null;
            int jitterCount = 0;
            for (int i = 1; i <= count; i++)
            {
                token.ThrowIfCancellationRequested();
                Stopwatch clock = Stopwatch.StartNew();
                MonitorSample sample = new MonitorSample { Sequence = i, TimeUtc = DateTime.UtcNow, Method = method, Route = route, Status = "HttpNoResponse" };
                using (HttpClientHandler handler = new HttpClientHandler { UseProxy = proxy != null, Proxy = proxy, UseDefaultCredentials = false, Credentials = null, PreAuthenticate = false, AllowAutoRedirect = false, UseCookies = false, MaxResponseHeadersLength = 32 })
                using (HttpClient client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan })
                using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, target))
                using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    request.Headers.ConnectionClose = true;
                    request.Headers.UserAgent.ParseAdd("NetworkRescue/1.0");
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { NoCache = true, NoStore = true };
                    deadline.CancelAfter(3000);
                    HttpResponseMessage response = null;
                    try
                    {
                        response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        client.CancelPendingRequests();
                        token.ThrowIfCancellationRequested();
                        sample.ErrorCode = "HttpTimeout";
                    }
                    catch (HttpRequestException ex)
                    {
                        WebException web = ex.InnerException as WebException;
                        using (HttpWebResponse rejected = web == null ? null : web.Response as HttpWebResponse)
                        {
                            token.ThrowIfCancellationRequested();
                            // .NET Framework reports a rejected HTTPS CONNECT through its WebException response.
                            if (rejected != null) response = new HttpResponseMessage(rejected.StatusCode);
                            else sample.ErrorCode = deadline.IsCancellationRequested || (web != null && web.Status == WebExceptionStatus.Timeout) ? "HttpTimeout" : web != null ? "Http" + web.Status : "HttpRequestFailed";
                        }
                    }
                    finally
                    {
                        if (response != null)
                        {
                            using (response)
                            {
                                int code = (int)response.StatusCode;
                                sample.HttpStatusCode = code;
                                sample.ResponseReceived = sample.Success = true;
                                sample.HttpSuccess = code >= 200 && code <= 299;
                                sample.Status = code == 407 ? "ProxyAuthenticationRequired" : code >= 500 ? "HttpServerError" : code >= 400 ? "HttpClientError" : code >= 300 ? "HttpRedirect" : "HttpResponse";
                                sample.RttMs = clock.ElapsedMilliseconds;
                            }
                        }
                    }
                }
                token.ThrowIfCancellationRequested();
                summary.Sent++;
                if (sample.ResponseReceived)
                {
                    long rtt = sample.RttMs.Value;
                    summary.Received++;
                    total += rtt;
                    summary.Min = summary.Min.HasValue ? Math.Min(summary.Min.Value, rtt) : rtt;
                    summary.Max = summary.Max.HasValue ? Math.Max(summary.Max.Value, rtt) : rtt;
                    summary.Avg = total / summary.Received;
                    if (previous.HasValue) { jitterTotal += Math.Abs(rtt - previous.Value); jitterCount++; }
                    previous = rtt;
                    summary.JitterMs = jitterCount == 0 ? (double?)null : jitterTotal / jitterCount;
                }
                summary.LossPercent = 100.0 * (summary.Sent - summary.Received) / summary.Sent;
                sample.Sent = summary.Sent; sample.Received = summary.Received; sample.LossPercent = summary.LossPercent;
                sample.Min = summary.Min; sample.Max = summary.Max; sample.Avg = summary.Avg; sample.JitterMs = summary.JitterMs;
                summary.Sequence = sample.Sequence; summary.TimeUtc = sample.TimeUtc; summary.RttMs = sample.RttMs;
                summary.HttpStatusCode = sample.HttpStatusCode; summary.ErrorCode = sample.ErrorCode;
                summary.HttpSuccess = summary.HttpSuccess && sample.HttpSuccess;
                summary.ResponseReceived = summary.Success = summary.Received > 0;
                summary.Status = summary.Received == summary.Sent ? "HttpResponding" : summary.Received > 0 ? "HttpPartialResponse" : "HttpNoResponse";
                if (progress != null) progress.Report(sample);
                if (i < count) await Task.Delay(Math.Max(0, intervalMs - (int)clock.ElapsedMilliseconds), token).ConfigureAwait(false);
            }
            summary.EndedUtc = DateTime.UtcNow;
            return summary;
        }

        public static async Task<DnsComparisonResult[]> CompareDnsAsync(string input, string[] servers, IProgress<DnsComparisonResult> progress, CancellationToken token)
        {
            string host, ignored;
            IPAddress literal;
            if (!AdvancedTools.TryNormalizeHost(input, out host, out ignored) || IPAddress.TryParse(host, out literal)) throw new ArgumentException("DnsHostRequired");
            if (servers == null || servers.Length < 1 || servers.Length > 4) throw new ArgumentException("InvalidDnsServerCount");
            IPAddress[] addresses = new IPAddress[servers.Length];
            for (int i = 0; i < servers.Length; i++)
                if (!IPAddress.TryParse(servers[i], out addresses[i]) || addresses[i].Equals(IPAddress.Any) || addresses[i].Equals(IPAddress.IPv6Any) || addresses[i].IsIPv6Multicast || (addresses[i].AddressFamily == AddressFamily.InterNetwork && addresses[i].GetAddressBytes()[0] >= 224)) throw new ArgumentException("InvalidDnsServer");
            Task<DnsComparisonResult>[] tasks = addresses.Select(delegate(IPAddress a) { return CompareServer(host, a, 53, progress, token); }).ToArray();
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }

        internal static async Task<DnsComparisonResult> CompareServer(string host, IPAddress server, int port, IProgress<DnsComparisonResult> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Stopwatch clock = Stopwatch.StartNew();
            DnsComparisonResult result = new DnsComparisonResult { Host = host, Server = server.ToString() };
            using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(2000);
                DnsQueryResult[] queries = await Task.WhenAll(Query(host, server, port, 1, timeout.Token, token), Query(host, server, port, 28, timeout.Token, token)).ConfigureAwait(false);
                result.Queries.AddRange(queries);
            }
            token.ThrowIfCancellationRequested();
            result.DurationMs = clock.ElapsedMilliseconds;
            result.Status = result.Queries.All(delegate(DnsQueryResult q) { return q.Status == "Answered"; }) ? "Answered" : "Unknown";
            result.ErrorCode = result.Status == "Answered" ? null : "IncompleteDnsComparison";
            if (progress != null) progress.Report(result);
            return result;
        }

        private static async Task<DnsQueryResult> Query(string host, IPAddress server, int port, ushort type, CancellationToken bounded, CancellationToken caller)
        {
            DnsQueryResult result = new DnsQueryResult { QueryType = type == 1 ? "A" : "AAAA", Status = "Unknown" };
            try
            {
                byte[] request = BuildQuery(host, type);
                byte[] reply;
                using (UdpClient socket = new UdpClient(server.AddressFamily))
                using (bounded.Register(socket.Close))
                {
                    socket.Connect(server, port);
                    bounded.ThrowIfCancellationRequested();
                    await socket.SendAsync(request, request.Length).ConfigureAwait(false);
                    UdpReceiveResult response = await socket.ReceiveAsync().ConfigureAwait(false);
                    if (!response.RemoteEndPoint.Address.Equals(server) || response.RemoteEndPoint.Port != port) throw new InvalidDataException();
                    reply = response.Buffer;
                }
                result = ParseResponse(reply, request, host, type);
                if (result.Truncated)
                {
                    using (TcpClient tcp = new TcpClient(server.AddressFamily))
                    using (bounded.Register(tcp.Close))
                    {
                        bounded.ThrowIfCancellationRequested();
                        await tcp.ConnectAsync(server, port).ConfigureAwait(false);
                        NetworkStream stream = tcp.GetStream();
                        byte[] frame = new byte[request.Length + 2];
                        frame[0] = (byte)(request.Length >> 8); frame[1] = (byte)request.Length;
                        Buffer.BlockCopy(request, 0, frame, 2, request.Length);
                        await stream.WriteAsync(frame, 0, frame.Length, bounded).ConfigureAwait(false);
                        byte[] length = await ReadExactly(stream, 2, bounded).ConfigureAwait(false);
                        int size = (length[0] << 8) | length[1];
                        if (size < 12) throw new InvalidDataException();
                        result = ParseResponse(await ReadExactly(stream, size, bounded).ConfigureAwait(false), request, host, type);
                        result.UsedTcp = true;
                        if (result.Truncated) { result.Status = "Unknown"; result.ErrorCode = "TruncatedTcpResponse"; }
                    }
                }
            }
            catch (Exception ex)
            {
                caller.ThrowIfCancellationRequested();
                if (!(ex is SocketException) && !(ex is IOException) && !(ex is InvalidDataException) && !(ex is ObjectDisposedException) && !(ex is OperationCanceledException)) throw;
                result.Status = "Unknown";
                result.ErrorCode = bounded.IsCancellationRequested ? "DnsTimeout" : ex is InvalidDataException ? "InvalidDnsResponse" : "DnsTransportError";
            }
            caller.ThrowIfCancellationRequested();
            return result;
        }

        private static async Task<byte[]> ReadExactly(NetworkStream stream, int count, CancellationToken token)
        {
            byte[] bytes = new byte[count];
            int read = 0;
            while (read < count)
            {
                int got = await stream.ReadAsync(bytes, read, count - read, token).ConfigureAwait(false);
                if (got == 0) throw new IOException();
                read += got;
            }
            return bytes;
        }

        internal static byte[] BuildQuery(string host, ushort type)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                byte[] id = new byte[2];
                using (RandomNumberGenerator random = RandomNumberGenerator.Create()) random.GetBytes(id);
                stream.Write(id, 0, 2);
                stream.Write(new byte[] { 1, 0, 0, 1, 0, 0, 0, 0, 0, 0 }, 0, 10);
                foreach (string label in host.Split('.')) { byte[] value = Encoding.ASCII.GetBytes(label); stream.WriteByte((byte)value.Length); stream.Write(value, 0, value.Length); }
                stream.Write(new byte[] { 0, (byte)(type >> 8), (byte)type, 0, 1 }, 0, 5);
                return stream.ToArray();
            }
        }

        private static int Word(byte[] bytes, int offset)
        {
            if (offset < 0 || offset + 2 > bytes.Length) throw new InvalidDataException();
            return (bytes[offset] << 8) | bytes[offset + 1];
        }

        private static string Name(byte[] bytes, ref int offset)
        {
            int cursor = offset, jumps = 0, wireLength = 0;
            bool jumped = false;
            List<string> labels = new List<string>();
            HashSet<int> visited = new HashSet<int>();
            while (true)
            {
                if (cursor < 0 || cursor >= bytes.Length || !visited.Add(cursor) || ++jumps > 128) throw new InvalidDataException();
                int size = bytes[cursor++];
                if (size == 0) { if (!jumped) offset = cursor; break; }
                if ((size & 192) == 192)
                {
                    if (cursor >= bytes.Length) throw new InvalidDataException();
                    int pointer = ((size & 63) << 8) | bytes[cursor++];
                    if (pointer >= cursor - 2) throw new InvalidDataException();
                    if (!jumped) offset = cursor;
                    jumped = true; cursor = pointer; continue;
                }
                if ((size & 192) != 0 || size > 63 || cursor + size > bytes.Length || (wireLength += size + 1) > 254) throw new InvalidDataException();
                labels.Add(Encoding.ASCII.GetString(bytes, cursor, size)); cursor += size;
            }
            return String.Join(".", labels).ToLowerInvariant();
        }

        internal static DnsQueryResult ParseResponse(byte[] bytes, byte[] request, string host, ushort type)
        {
            if (bytes == null || bytes.Length < 12 || bytes.Length > 65535 || Word(bytes, 0) != Word(request, 0) || (bytes[2] & 128) == 0 || (bytes[2] & 120) != 0 || Word(bytes, 4) != 1) throw new InvalidDataException();
            int offset = 12;
            if (Name(bytes, ref offset) != host || Word(bytes, offset) != type || Word(bytes, offset + 2) != 1) throw new InvalidDataException();
            offset += 4;
            DnsQueryResult result = new DnsQueryResult { QueryType = type == 1 ? "A" : "AAAA", Rcode = bytes[3] & 15, Truncated = (bytes[2] & 2) != 0, Status = "Answered" };
            if (result.Truncated) { result.Status = "Unknown"; result.ErrorCode = "TruncatedDnsResponse"; return result; }
            int answers = Word(bytes, 6), count = answers + Word(bytes, 8) + Word(bytes, 10);
            if (count > 512) throw new InvalidDataException();
            Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            List<KeyValuePair<string, string>> addresses = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < count; i++)
            {
                string owner = Name(bytes, ref offset);
                int rrType = Word(bytes, offset), rrClass = Word(bytes, offset + 2), size = Word(bytes, offset + 8);
                if (rrType == 41 && i >= answers + Word(bytes, 8)) result.Rcode = result.Rcode.Value | (bytes[offset + 4] << 4);
                offset += 10;
                if (offset + size > bytes.Length) throw new InvalidDataException();
                if (rrType == 5)
                {
                    int nameOffset = offset;
                    string alias = Name(bytes, ref nameOffset);
                    if (nameOffset != offset + size) throw new InvalidDataException();
                    if (i < answers && rrClass == 1) aliases[owner] = alias;
                }
                if (i < answers && rrClass == 1 && rrType == type)
                {
                    if (size != (type == 1 ? 4 : 16)) throw new InvalidDataException();
                    byte[] address = new byte[size]; Buffer.BlockCopy(bytes, offset, address, 0, size);
                    addresses.Add(new KeyValuePair<string, string>(owner, new IPAddress(address).ToString()));
                }
                offset += size;
            }
            if (offset != bytes.Length) throw new InvalidDataException();
            HashSet<string> allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string current = host;
            while (true)
            {
                if (!allowed.Add(current) || allowed.Count > 128) throw new InvalidDataException();
                if (!aliases.ContainsKey(current)) break;
                current = aliases[current];
            }
            result.Addresses = result.Rcode == 0 ? addresses.Where(delegate(KeyValuePair<string, string> p) { return allowed.Contains(p.Key); }).Select(delegate(KeyValuePair<string, string> p) { return p.Value; }).Distinct().ToArray() : new string[0];
            return result;
        }
    }
}
