using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    public enum AdvancedToolKind { Ping, Dns, Trace, Tcp }

    public sealed class AdvancedToolSample
    {
        public string Kind, Address, RecordType, Status, ErrorCode;
        public int? Sequence, Hop, Port;
        public bool Responded;
        public long? RttMs;
        public double DurationMs;
    }

    public sealed class AdvancedToolResult
    {
        public bool Success;
        public string Summary;
        public string Tool, Host;
        public double DurationMs;
        public List<AdvancedToolSample> Rows = new List<AdvancedToolSample>();
        public int TotalRows;
        public bool Truncated;
        public int Sent, Received;
        public double? AverageRttMs;
    }

    public static class AdvancedTools
    {
        public static bool TryNormalizeHost(string input, out string host, out string error)
        {
            host = null;
            error = I18n.T("请输入 IP 地址或合法域名，例如 192.168.1.1、::1、www.baidu.com；不要填写网址或端口。");
            string value = (input ?? "").Trim();
            if (value.Length == 0 || value.Length > 1024) return false;
            bool bracketed = value[0] == '[' && value.EndsWith("]", StringComparison.Ordinal);
            if (bracketed) value = value.Substring(1, value.Length - 2);
            IPAddress address;
            if (IPAddress.TryParse(value, out address)) { host = address.ToString(); error = null; return true; }
            if (bracketed) return false;
            try
            {
                if (value.EndsWith(".", StringComparison.Ordinal)) value = value.Substring(0, value.Length - 1);
                value = new IdnMapping().GetAscii(value).ToLowerInvariant();
                if (value.Length == 0 || value.Length > 253) return false;
                foreach (string label in value.Split('.'))
                {
                    if (label.Length == 0 || label.Length > 63 || label[0] == '-' || label[label.Length - 1] == '-') return false;
                    foreach (char c in label)
                        if (!(c >= 'a' && c <= 'z') && !(c >= '0' && c <= '9') && c != '-') return false;
                }
                host = value;
                error = null;
                return true;
            }
            catch (ArgumentException) { return false; }
        }

        public static bool TryParsePort(string value, out int port)
        {
            return Int32.TryParse((value ?? "").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) && port >= 1 && port <= 65535;
        }

        public static async Task<AdvancedToolResult> RunAsync(AdvancedToolKind tool, string input, int port, IProgress<string> progress, CancellationToken token, IProgress<AdvancedToolSample> samples = null)
        {
            token.ThrowIfCancellationRequested();
            string host, error;
            if (!TryNormalizeHost(input, out host, out error)) throw new ArgumentException(error);
            if (tool < AdvancedToolKind.Ping || tool > AdvancedToolKind.Tcp) throw new ArgumentException(I18n.T("未识别的检查工具。"));
            if (tool == AdvancedToolKind.Tcp && (port < 1 || port > 65535)) throw new ArgumentException(I18n.T("TCP 端口必须为 1–65535。"));
            Stopwatch watch = Stopwatch.StartNew();
            AdvancedToolResult result = new AdvancedToolResult { Tool = tool.ToString(), Host = host };
            Report(progress, I18n.T("目标：") + host + (tool == AdvancedToolKind.Tcp ? I18n.T("  端口：") + port.ToString(CultureInfo.InvariantCulture) : ""));
            try
            {
                if (tool == AdvancedToolKind.Dns) await QueryDnsAsync(host, progress, token, result, samples).ConfigureAwait(false);
                else if (tool == AdvancedToolKind.Tcp) await TestTcpAsync(host, port, progress, token, result, samples).ConfigureAwait(false);
                else await PingOrTraceAsync(host, tool == AdvancedToolKind.Trace, progress, token, result, samples).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                result.Summary = tool == AdvancedToolKind.Tcp ? I18n.T("TCP 测试超过 3 秒期限（含解析）。未确认端口可达；超时不能单独说明整机断网。") : I18n.T("系统 DNS 解析在 5 秒内未完成。未确认此域名的地址；系统后台解析可能稍后自行结束。");
                AddSample(result, samples, new AdvancedToolSample { Kind = result.Tool, Address = host, Port = tool == AdvancedToolKind.Tcp ? (int?)port : null, Status = "TimedOut", ErrorCode = "TimedOut", DurationMs = watch.Elapsed.TotalMilliseconds });
            }
            catch (SocketException ex)
            {
                result.Summary = I18n.T("系统解析或连接未完成（") + ex.SocketErrorCode.ToString() + I18n.T("）。请结合目标与其他检查判断。");
                AddSample(result, samples, new AdvancedToolSample { Kind = result.Tool, Address = host, Port = tool == AdvancedToolKind.Tcp ? (int?)port : null, Status = "Error", ErrorCode = ex.SocketErrorCode.ToString(), DurationMs = watch.Elapsed.TotalMilliseconds });
            }
            result.DurationMs = watch.Elapsed.TotalMilliseconds;
            return result;
        }

        internal static void AddSample(AdvancedToolResult result, IProgress<AdvancedToolSample> samples, AdvancedToolSample sample)
        {
            lock (result.Rows)
            {
                result.TotalRows++;
                if (result.Rows.Count >= 512)
                {
                    result.Truncated = true;
                    return;
                }
                result.Rows.Add(sample);
                if (sample.Sequence.HasValue || sample.Hop.HasValue)
                {
                    result.Sent++;
                    if (sample.Responded) result.Received++;
                    var rtts = result.Rows.Where(delegate(AdvancedToolSample row) { return row.RttMs.HasValue; }).ToArray();
                    result.AverageRttMs = rtts.Length == 0 ? (double?)null : rtts.Average(delegate(AdvancedToolSample row) { return (double)row.RttMs.Value; });
                }
                if (samples != null) samples.Report(sample);
            }
        }

        private static void Report(IProgress<string> progress, string text)
        {
            if (progress != null) progress.Report(text);
        }

        private static void Observe(Task task)
        {
            task.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private static async Task<IPAddress[]> ResolveAsync(string host, int milliseconds, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IPAddress literal;
            if (IPAddress.TryParse(host, out literal)) return new[] { literal };
            Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);
            Task elapsed = Task.Delay(milliseconds, token);
            if (await Task.WhenAny(lookup, elapsed).ConfigureAwait(false) != lookup)
            {
                Observe(lookup);
                token.ThrowIfCancellationRequested();
                throw new TimeoutException();
            }
            token.ThrowIfCancellationRequested();
            IPAddress[] addresses = await lookup.ConfigureAwait(false);
            if (addresses.Length == 0) throw new SocketException((int)SocketError.NoData);
            return addresses.Distinct().ToArray();
        }

        private static async Task QueryDnsAsync(string host, IProgress<string> progress, CancellationToken token, AdvancedToolResult result, IProgress<AdvancedToolSample> samples)
        {
            Report(progress, I18n.T("通过 Windows 系统解析器查询 A/AAAA，最长等待 5 秒；结果可能包含缓存或 hosts 记录。"));
            Stopwatch watch = Stopwatch.StartNew();
            IPAddress[] addresses = await ResolveAsync(host, 5000, token).ConfigureAwait(false);
            double duration = watch.Elapsed.TotalMilliseconds;
            foreach (IPAddress address in addresses)
            {
                Report(progress, (address.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA  " : "A     ") + address.ToString());
                AddSample(result, samples, new AdvancedToolSample { Kind = "Dns", Address = address.ToString(), RecordType = address.AddressFamily == AddressFamily.InterNetworkV6 ? "AAAA" : "A", Status = "Success", Responded = true, DurationMs = duration });
            }
            result.Success = true;
            result.Summary = I18n.T("返回 ") + addresses.Length.ToString(CultureInfo.InvariantCulture) + I18n.T(" 个实际地址。此工具不区分来自 DNS 服务器、系统缓存或 hosts 的结果。");
        }

        private static async Task<PingReply> PingOnceAsync(IPAddress address, int timeout, int ttl, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (Ping ping = new Ping())
            {
                Task<PingReply> send = ping.SendPingAsync(address, timeout, new byte[32], new PingOptions(ttl, false));
                TaskCompletionSource<bool> cancelled = new TaskCompletionSource<bool>();
                using (token.Register(delegate
                {
                    cancelled.TrySetResult(true);
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        try { ping.SendAsyncCancel(); }
                        catch (InvalidOperationException) { }
                    });
                }))
                {
                    if (await Task.WhenAny(send, cancelled.Task).ConfigureAwait(false) != send)
                    {
                        Observe(send);
                        token.ThrowIfCancellationRequested();
                    }
                    token.ThrowIfCancellationRequested();
                    return await send.ConfigureAwait(false);
                }
            }
        }

        private static async Task PingOrTraceAsync(string host, bool trace, IProgress<string> progress, CancellationToken token, AdvancedToolResult result, IProgress<AdvancedToolSample> samples)
        {
            Stopwatch overall = Stopwatch.StartNew();
            IPAddress[] addresses = await ResolveAsync(host, 5000, token).ConfigureAwait(false);
            IPAddress target = addresses.OrderBy(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1; }).First();
            Report(progress, I18n.T("实际目标 IP：") + target.ToString());
            Report(progress, trace ? I18n.T("逐跳发送 ICMP，TTL 1–20，总时限 30 秒；每跳最多等待 1.2 秒。") : I18n.T("发送 4 次 ICMP，每次最多等待 1.2 秒。ICMP 无响应不能据此断言断网。"));
            int successes = 0, sent = 0;
            long totalRtt = 0;
            for (int i = 1; i <= (trace ? 20 : 4); i++)
            {
                token.ThrowIfCancellationRequested();
                int remaining = trace ? 30000 - (int)overall.ElapsedMilliseconds : 1200;
                if (remaining <= 0) break;
                Stopwatch hopTime = Stopwatch.StartNew();
                sent++;
                try
                {
                    PingReply reply = await PingOnceAsync(target, Math.Min(1200, remaining), trace ? i : 128, token).ConfigureAwait(false);
                    AddSample(result, samples, new AdvancedToolSample { Kind = result.Tool, Address = reply.Address == null ? target.ToString() : reply.Address.ToString(), Sequence = trace ? null : (int?)i, Hop = trace ? (int?)i : null, Status = reply.Status.ToString(), ErrorCode = reply.Status == IPStatus.Success ? null : reply.Status.ToString(), Responded = reply.Status == IPStatus.Success || (trace && reply.Address != null && reply.Status != IPStatus.TimedOut), RttMs = reply.Status == IPStatus.Success ? (long?)reply.RoundtripTime : null, DurationMs = hopTime.Elapsed.TotalMilliseconds });
                    if (reply.Status == IPStatus.Success)
                    {
                        successes++;
                        totalRtt += reply.RoundtripTime;
                        Report(progress, (trace ? I18n.T("跳 ") : I18n.T("第 ")) + i.ToString(CultureInfo.InvariantCulture) + (trace ? "  " : I18n.T(" 次  ")) + (reply.Address == null ? target.ToString() : reply.Address.ToString()) + "  RTT " + reply.RoundtripTime.ToString(CultureInfo.InvariantCulture) + " ms");
                        if (trace)
                        {
                            result.Success = true;
                            result.Summary = I18n.T("在第 ") + i.ToString(CultureInfo.InvariantCulture) + I18n.T(" 跳收到目标 ICMP 应答。路径只反映本次探测，可能随网络策略变化。");
                            return;
                        }
                    }
                    else if (trace && reply.Address != null && reply.Status != IPStatus.TimedOut)
                        Report(progress, I18n.T("跳 ") + i.ToString(CultureInfo.InvariantCulture) + "  " + reply.Address.ToString() + "  " + reply.Status.ToString() + I18n.T("  探测耗时 ") + hopTime.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                    else
                        Report(progress, (trace ? I18n.T("跳 ") : I18n.T("第 ")) + i.ToString(CultureInfo.InvariantCulture) + (trace ? "  *  " : I18n.T(" 次  ")) + reply.Status.ToString());
                }
                catch (PingException ex)
                {
                    Report(progress, (trace ? I18n.T("跳 ") : I18n.T("第 ")) + i.ToString(CultureInfo.InvariantCulture) + I18n.T("  ICMP 探测未完成（可能为权限、协议支持或网络策略）。"));
                    SocketException socket = ex.InnerException as SocketException;
                    AddSample(result, samples, new AdvancedToolSample { Kind = result.Tool, Address = target.ToString(), Sequence = trace ? null : (int?)i, Hop = trace ? (int?)i : null, Status = "Error", ErrorCode = socket == null ? "PingException" : socket.SocketErrorCode.ToString(), DurationMs = hopTime.Elapsed.TotalMilliseconds });
                }
                if (!trace && i < 4) await Task.Delay(250, token).ConfigureAwait(false);
            }
            if (trace)
            {
                result.Summary = I18n.T("已结束逐跳探测：发送 ") + sent.ToString(CultureInfo.InvariantCulture) + I18n.T(" 跳，未收到目标成功应答。路由器、VPN 或防火墙可能不返回 ICMP；星号不等于该跳故障，也不能据此断言断网。");
                return;
            }
            result.Success = successes > 0;
            result.Summary = I18n.T("发送 ") + sent.ToString(CultureInfo.InvariantCulture) + I18n.T("，收到 ") + successes.ToString(CultureInfo.InvariantCulture) + I18n.T("，丢包 ") + ((sent - successes) * 100.0 / sent).ToString("F0", CultureInfo.InvariantCulture) + "%" + (successes > 0 ? I18n.T("，平均 RTT ") + (totalRtt * 1.0 / successes).ToString("F1", CultureInfo.InvariantCulture) + I18n.T(" ms。") : I18n.T("；未收到 ICMP 应答，不能据此断言断网。"));
        }

        private static async Task TestTcpAsync(string host, int port, IProgress<string> progress, CancellationToken token, AdvancedToolResult result, IProgress<AdvancedToolSample> samples)
        {
            Stopwatch watch = Stopwatch.StartNew();
            IPAddress[] addresses = await ResolveAsync(host, 3000, token).ConfigureAwait(false);
            int remaining = 3000 - (int)watch.ElapsedMilliseconds;
            if (remaining <= 0) throw new TimeoutException();
            Report(progress, I18n.T("总时限 3 秒（含解析），直接建立 TCP 连接，不发送应用数据；最多验证同一目标的前 8 个地址。"));
            Task[] probes = addresses.Take(8).Select(delegate(IPAddress address) { return ConnectAsync(address, port, remaining, progress, token, result, samples); }).ToArray();
            await Task.WhenAll(probes).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            result.Sent = result.Rows.Count;
            result.Received = result.Rows.Count(delegate(AdvancedToolSample row) { return row.Responded; });
            result.Success = result.Received > 0;
            result.Summary = result.Success ? I18n.T("至少一个目标地址的 TCP 端口可达。仅证明 TCP 握手成功，不代表服务内容、TLS 或登录正常。") : I18n.T("未确认目标 TCP 端口可达。目标可能未监听或连接被过滤，不代表整机断网。");
        }

        private static async Task ConnectAsync(IPAddress address, int port, int timeout, IProgress<string> progress, CancellationToken token, AdvancedToolResult result, IProgress<AdvancedToolSample> samples)
        {
            string label = (address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address.ToString() + "]" : address.ToString()) + ":" + port.ToString(CultureInfo.InvariantCulture);
            using (TcpClient client = new TcpClient(address.AddressFamily))
            using (CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                deadline.CancelAfter(timeout);
                using (deadline.Token.Register(delegate { client.Close(); }))
                {
                    Stopwatch watch = Stopwatch.StartNew();
                    AdvancedToolSample sample = new AdvancedToolSample { Kind = "Tcp", Address = address.ToString(), Port = port };
                    try
                    {
                        await client.ConnectAsync(address, port).ConfigureAwait(false);
                        watch.Stop();
                        token.ThrowIfCancellationRequested();
                        if (deadline.IsCancellationRequested)
                        {
                            Report(progress, label + I18n.T("  连接超时"));
                            sample.Status = "TimedOut";
                            sample.ErrorCode = "TimedOut";
                        }
                        else
                        {
                            Report(progress, label + I18n.T("  已连接，耗时 ") + watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture) + " ms");
                            sample.Status = "Success";
                            sample.Responded = true;
                        }
                    }
                    catch (SocketException ex)
                    {
                        watch.Stop();
                        token.ThrowIfCancellationRequested();
                        Report(progress, label + "  " + (deadline.IsCancellationRequested ? I18n.T("连接超时") : ex.SocketErrorCode.ToString()));
                        sample.Status = deadline.IsCancellationRequested ? "TimedOut" : "Error";
                        sample.ErrorCode = deadline.IsCancellationRequested ? "TimedOut" : ex.SocketErrorCode.ToString();
                    }
                    catch (ObjectDisposedException)
                    {
                        watch.Stop();
                        token.ThrowIfCancellationRequested();
                        Report(progress, label + I18n.T("  连接超时"));
                        sample.Status = "TimedOut";
                        sample.ErrorCode = "TimedOut";
                    }
                    sample.DurationMs = watch.Elapsed.TotalMilliseconds;
                    AddSample(result, samples, sample);
                }
            }
        }
    }
}
