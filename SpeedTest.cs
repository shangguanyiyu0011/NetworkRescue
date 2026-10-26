using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Cache;
using System.Security.Cryptography;
using System.Threading;

namespace NetworkRescue
{
    public enum SpeedTestPhase { Latency = 0, Download = 1, Upload = 2 }

    internal sealed class SpeedTestResult
    {
        public string State = I18n.T("尚未开始");
        public string Detail = "";
        public string Node = I18n.T("speed.cloudflare.com（Cloudflare 自动选路）");
        public string Route = I18n.T("直连请求（绕过系统 HTTP 代理）");
        public long DownloadBytes;
        public long UploadBytes;
        public double ElapsedSeconds;
        public double? DownloadMbps;
        public double? UploadMbps;
        public double? LatencyMs;
        public SpeedTestPhase Phase;
        public double PhaseElapsedSeconds;
        public double? PhaseAverageMbps;
        public bool PhaseCompleted;
        public bool Completed;
        public long TotalBytes { get { return DownloadBytes + UploadBytes; } }
        public SpeedTestResult Copy() { return (SpeedTestResult)MemberwiseClone(); }
    }

    internal sealed class SpeedTest
    {
        internal const long MiB = 1024 * 1024;
        internal const long MaximumPayloadBytes = 73 * MiB;
        private readonly Uri endpoint;
        private readonly long downLimit;
        private readonly long upLimit;
        private readonly int targetMilliseconds;
        private readonly int totalMilliseconds;
        private SpeedTestResult result;
        private Stopwatch elapsed;
        private IProgress<SpeedTestResult> progress;
        private long lastReport;
        private bool followProxy;
        private readonly object reportGate = new object();
        private Stopwatch phaseWatch;
        private long phaseConfirmedBytes;

        public SpeedTest() : this(new Uri("https://speed.cloudflare.com/"), 48 * MiB, 24 * MiB, 7000, 30000) { }

        // Only loopback HTTP is accepted for deterministic, small local tests.
        internal SpeedTest(Uri service, long downloadLimit, long uploadLimit, int targetMs, int totalMs)
        {
            if (service == null || (service.Scheme != "https" && !(service.Scheme == "http" && service.IsLoopback))) throw new ArgumentException(I18n.T("测速服务必须使用 HTTPS。"));
            if (downloadLimit <= 0 || downloadLimit > 48 * MiB || uploadLimit <= 0 || uploadLimit > 24 * MiB || targetMs <= 0 || targetMs > 7000 || totalMs <= 0 || totalMs > 30000) throw new ArgumentOutOfRangeException(I18n.T("测速限制"));
            endpoint = service; downLimit = downloadLimit; upLimit = uploadLimit;
            targetMilliseconds = targetMs; totalMilliseconds = totalMs;
        }

        public SpeedTestResult Run(bool useSystemProxy, IProgress<SpeedTestResult> updates, CancellationToken cancellation)
        {
            followProxy = useSystemProxy; progress = updates;
            result = new SpeedTestResult { Node = endpoint.Host + I18n.T("（Cloudflare 自动选路）"), Route = useSystemProxy ? I18n.T("跟随系统 HTTP 代理（可能直连；按每次请求的代理规则选路）") : I18n.T("直连请求（绕过系统 HTTP 代理）") };
            elapsed = Stopwatch.StartNew(); lastReport = -1000; phaseWatch = null; phaseConfirmedBytes = 0;
            int activePhase = 0;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(totalMilliseconds);
                try
                {
                    StartPhase(SpeedTestPhase.Latency, I18n.T("测量 HTTP 往返延迟"));
                    var samples = new List<double>();
                    for (int i = 0; i < 5; i++) samples.Add(Download(0, deadline.Token));
                    result.LatencyMs = Median(samples.ToArray());
                    activePhase = 1;
                    Measure(false, downLimit, deadline.Token);
                    activePhase = 0;
                    activePhase = 2;
                    Measure(true, upLimit, deadline.Token);
                    activePhase = 0;
                    deadline.Token.ThrowIfCancellationRequested();
                    result.Completed = true; result.State = I18n.T("测速完成");
                    result.Detail = I18n.T("单连接吞吐量 = 完整成功样本的正文总字节 / 整个阶段实际耗时。分母含 HTTP、服务响应，以及阶段限时中断请求的全部耗时，未完成的正文不计入分子，因此可能偏低。结果仅代表到当前服务节点的路径，不能作为运营商接入带宽的保证。");
                }
                catch (Exception ex)
                {
                    if (activePhase == 1) result.DownloadMbps = null;
                    if (activePhase == 2) result.UploadMbps = null;
                    if (cancellation.IsCancellationRequested) { result.State = I18n.T("测速已停止"); result.Detail = I18n.T("已中止当前请求；当前阶段未完成，速度保持为空，仅保留已完成阶段结果。"); }
                    else if (deadline.IsCancellationRequested) { result.State = I18n.T("测速达到 30 秒上限"); result.Detail = I18n.T("已中止当前请求；未完成的指标保持为空。"); }
                    else { result.State = I18n.T("测速未完成"); result.Detail = I18n.T("当前阶段未完成，速度保持为空。") + FriendlyError(ex); }
                }
            }
            if (phaseWatch != null) phaseWatch.Stop();
            elapsed.Stop(); Report(true); return result.Copy();
        }

        private void Measure(bool upload, long limit, CancellationToken token)
        {
            StartPhase(upload ? SpeedTestPhase.Upload : SpeedTestPhase.Download, upload ? I18n.T("测量上传速度") : I18n.T("测量下载速度"));
            int size = (int)Math.Min(256 * 1024, limit);
            using (var phase = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                Exception reportError = null;
                var reporter = progress == null ? null : new Timer(delegate
                {
                    try { Report(false); }
                    catch (Exception ex) { lock (reportGate) reportError = ex; phase.Cancel(); }
                }, null, 200, 200);
                try
                {
                    phase.CancelAfter(targetMilliseconds);
                    for (int requests = 0; requests < 100 && phaseConfirmedBytes < limit; requests++)
                    {
                        token.ThrowIfCancellationRequested();
                        int count = (int)Math.Min(size, limit - phaseConfirmedBytes);
                        double duration;
                        try { duration = upload ? Upload(count, phase.Token) : Download(count, phase.Token); }
                        catch (Exception)
                        {
                            token.ThrowIfCancellationRequested();
                            lock (reportGate) if (reportError != null) throw reportError;
                            if (phase.IsCancellationRequested && phaseConfirmedBytes > 0) break;
                            throw;
                        }
                        lock (reportGate) phaseConfirmedBytes += count;
                        Report(false);
                        if (phase.IsCancellationRequested) break;
                        // Aim at approximately one second per sample, capped to bound memory and cancellation latency.
                        size = (int)Math.Max(64 * 1024, Math.Min(upload ? 4 * MiB : 8 * MiB, count * 1000.0 / Math.Max(1, duration)));
                    }
                }
                finally
                {
                    lock (reportGate) phaseWatch.Stop();
                    if (reporter != null)
                        using (var stopped = new ManualResetEvent(false)) { reporter.Dispose(stopped); stopped.WaitOne(); }
                }
                if (reportError != null) throw reportError;
            }
            token.ThrowIfCancellationRequested();
            if (phaseConfirmedBytes == 0) throw new IOException(I18n.T("没有完成可校验的传输，无法计算速度。"));
            result.PhaseElapsedSeconds = phaseWatch.Elapsed.TotalSeconds;
            result.PhaseAverageMbps = Mbps(phaseConfirmedBytes, result.PhaseElapsedSeconds);
            if (upload) result.UploadMbps = result.PhaseAverageMbps; else result.DownloadMbps = result.PhaseAverageMbps;
            result.PhaseCompleted = true;
            Report(true);
        }

        private HttpWebRequest Request(string path, string method)
        {
            var request = (HttpWebRequest)WebRequest.Create(new Uri(endpoint, path));
            request.Method = method;
            request.Proxy = followProxy ? WebRequest.GetSystemWebProxy() : null;
            if (request.Proxy != null) request.Proxy.Credentials = null;
            request.UseDefaultCredentials = false; request.Credentials = null; request.PreAuthenticate = false;
            request.AllowAutoRedirect = false; request.AutomaticDecompression = DecompressionMethods.None;
            request.CachePolicy = new RequestCachePolicy(RequestCacheLevel.NoCacheNoStore);
            request.Headers[HttpRequestHeader.CacheControl] = "no-cache, no-store";
            request.UserAgent = "NetworkRescue/1.0";
            request.Timeout = 8000; request.ReadWriteTimeout = 8000;
            request.AllowWriteStreamBuffering = false;
            request.ServicePoint.Expect100Continue = false;
            return request;
        }

        private double Download(int bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var request = Request("__down?bytes=" + bytes + "&cache=" + Guid.NewGuid().ToString("N"), "GET");
            var watch = Stopwatch.StartNew();
            using (token.Register(delegate { request.Abort(); }))
            using (var timeout = new Timer(delegate { request.Abort(); }, null, 8000, Timeout.Infinite))
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                CheckResponse(response);
                if (response.ContentLength >= 0 && response.ContentLength != bytes) throw new IOException(I18n.T("下载响应长度与请求不一致，已拒绝此样本。"));
                long received = 0;
                var buffer = new byte[64 * 1024];
                using (var stream = response.GetResponseStream())
                {
                    int read;
                    while ((read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, bytes - received + 1))) > 0)
                    {
                        AddTraffic(read, false); received += read;
                        if (received > bytes) throw new IOException(I18n.T("下载响应超过请求上限，已停止。"));
                        token.ThrowIfCancellationRequested(); Report(false);
                    }
                }
                if (received != bytes) throw new IOException(I18n.T("下载内容不完整，已拒绝此样本。"));
            }
            return Math.Max(0.001, watch.Elapsed.TotalMilliseconds);
        }

        private double Upload(int bytes, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var buffer = new byte[Math.Min(64 * 1024, bytes)];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(buffer);
            var request = Request("__up", "POST");
            request.ContentType = "application/octet-stream"; request.ContentLength = bytes;
            var watch = Stopwatch.StartNew();
            using (token.Register(delegate { request.Abort(); }))
            using (var timeout = new Timer(delegate { request.Abort(); }, null, 8000, Timeout.Infinite))
            {
                using (var stream = request.GetRequestStream())
                    for (int sent = 0; sent < bytes; )
                    {
                        token.ThrowIfCancellationRequested();
                        int count = Math.Min(buffer.Length, bytes - sent);
                        stream.Write(buffer, 0, count); sent += count; AddTraffic(count, true); Report(false);
                    }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    CheckResponse(response);
                    using (var stream = response.GetResponseStream())
                    {
                        int read, received = 0;
                        while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, 4097 - received))) > 0)
                        {
                            AddTraffic(read, false); received += read;
                            if (received > 4096) throw new IOException(I18n.T("上传确认响应超过 4 KiB，已停止。"));
                            token.ThrowIfCancellationRequested();
                        }
                    }
                }
            }
            return Math.Max(0.001, watch.Elapsed.TotalMilliseconds);
        }

        private void CheckResponse(HttpWebResponse response)
        {
            int status = (int)response.StatusCode;
            if (status < 200 || status >= 300) throw new IOException(I18n.T("服务返回 HTTP ") + status + I18n.T("，已拒绝此样本。"));
            string ray = response.Headers["Cf-Ray"];
            if (!String.IsNullOrEmpty(ray))
            {
                int split = ray.LastIndexOf('-');
                string colo = split >= 0 ? ray.Substring(split + 1) : "";
                if (colo.Length == 3 && IsAsciiLetters(colo)) result.Node = endpoint.Host + " · Cloudflare " + colo.ToUpperInvariant();
            }
        }

        private static bool IsAsciiLetters(string value)
        {
            foreach (char c in value) if (!(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z')) return false;
            return true;
        }

        private void AddTraffic(int count, bool upload)
        {
            lock (reportGate)
            {
                if (count < 0 || count > MaximumPayloadBytes - result.TotalBytes) throw new IOException(I18n.T("已达到测速数据量上限。"));
                if (upload) result.UploadBytes += count; else result.DownloadBytes += count;
            }
        }
        private void StartPhase(SpeedTestPhase phase, string text)
        {
            result.Phase = phase; result.State = text; result.PhaseCompleted = false;
            result.PhaseAverageMbps = null; result.PhaseElapsedSeconds = 0;
            phaseConfirmedBytes = 0; phaseWatch = Stopwatch.StartNew(); Report(true);
        }
        private void Report(bool force)
        {
            lock (reportGate)
            {
                result.ElapsedSeconds = elapsed.Elapsed.TotalSeconds;
                if (phaseWatch != null)
                {
                    result.PhaseElapsedSeconds = phaseWatch.Elapsed.TotalSeconds;
                    result.PhaseAverageMbps = phaseConfirmedBytes > 0 ? (double?)Mbps(phaseConfirmedBytes, result.PhaseElapsedSeconds) : null;
                }
                if (progress != null && (force || elapsed.ElapsedMilliseconds - lastReport >= 200))
                {
                    lastReport = elapsed.ElapsedMilliseconds; progress.Report(result.Copy());
                }
            }
        }
        internal static double Mbps(long bytes, double seconds)
        {
            if (bytes < 0 || seconds <= 0 || Double.IsNaN(seconds) || Double.IsInfinity(seconds)) throw new ArgumentOutOfRangeException();
            return bytes * 8.0 / seconds / 1000000.0;
        }
        internal static double Median(double[] samples)
        {
            if (samples == null || samples.Length == 0) throw new ArgumentException(I18n.T("至少需要一个延迟样本。"));
            var sorted = (double[])samples.Clone(); Array.Sort(sorted);
            int middle = sorted.Length / 2;
            return sorted.Length % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
        }
        private static string FriendlyError(Exception ex)
        {
            var web = ex as WebException;
            if (web != null)
            {
                var response = web.Response as HttpWebResponse;
                if (response != null) { int code = (int)response.StatusCode; response.Close(); return I18n.T("测速服务返回 HTTP ") + code + I18n.T("。未使用该失败请求计算速度。"); }
                return I18n.T("连接测速服务失败（") + web.Status + I18n.T("）。请检查到 speed.cloudflare.com 的连接；直连失败不能证明接入宽带不可用。");
            }
            return ex.Message;
        }
    }
}
