using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace NetworkRescue
{
    internal static class SpeedTestTests
    {
        private static int assertions;
        private static long loopbackBytes;
        private sealed class Samples : IProgress<SpeedTestResult>
        {
            private readonly List<SpeedTestResult> values = new List<SpeedTestResult>();
            public void Report(SpeedTestResult value) { lock (values) values.Add(value); }
            public SpeedTestResult[] Values { get { lock (values) return values.ToArray(); } }
        }
        private sealed class FailingSamples : IProgress<SpeedTestResult>
        {
            private bool failed;
            public void Report(SpeedTestResult value)
            {
                if (!failed && value.Phase == SpeedTestPhase.Download && value.PhaseElapsedSeconds >= 0.15)
                { failed = true; throw new InvalidOperationException("observer fixture"); }
            }
        }
        private static SpeedTestResult CompletedPhase(Samples samples, SpeedTestPhase phase, double? final, long confirmed)
        {
            SpeedTestResult terminal = null;
            double previous = -1;
            bool started = false, reachedUpload = false;
            foreach (var sample in samples.Values)
            {
                if (sample.Phase == SpeedTestPhase.Upload) reachedUpload = true;
                Assert(!(reachedUpload && sample.Phase == SpeedTestPhase.Download), "download phase never resumes after upload starts");
                if (sample.Phase != phase) continue;
                Assert(sample.PhaseElapsedSeconds >= previous, "phase time is monotonic");
                previous = sample.PhaseElapsedSeconds;
                if (!started) { Assert(!sample.PhaseAverageMbps.HasValue && !sample.PhaseCompleted, "phase starts unconfirmed"); started = true; }
                if (sample.PhaseAverageMbps.HasValue) Assert(sample.PhaseAverageMbps == SpeedTest.Mbps(confirmed, sample.PhaseElapsedSeconds), "curve uses confirmed bytes over whole phase time");
                if (sample.PhaseCompleted) terminal = sample;
            }
            Assert(terminal != null && terminal.PhaseAverageMbps == final, "successful phase endpoint exactly equals final metric");
            return terminal;
        }
        private static void Assert(bool condition, string message)
        {
            assertions++;
            if (!condition) throw new Exception("FAIL: " + message);
        }
        public static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.SystemDefault;
                if (args.Length == 1 && (args[0] == "--smoke" || args[0] == "--smoke-proxy"))
                {
                    bool proxy = args[0] == "--smoke-proxy";
                    var smoke = new SpeedTest(new Uri("https://speed.cloudflare.com/"), 1024, 1024, 1000, 20000).Run(proxy, null, CancellationToken.None);
                    Console.WriteLine("Cloudflare " + (proxy ? "system proxy" : "direct") + " smoke: " + smoke.State + "; " + smoke.Node + "; payload=" + smoke.TotalBytes + "; download-bytes=" + smoke.DownloadBytes + "; upload-bytes=" + smoke.UploadBytes + "; seconds=" + smoke.ElapsedSeconds.ToString("0.00") + "; down=" + smoke.DownloadMbps + "; up=" + smoke.UploadMbps + "; HTTP-RTT=" + smoke.LatencyMs + "; " + smoke.Detail);
                    return smoke.Completed ? 0 : 1;
                }
                Assert(Math.Abs(SpeedTest.Mbps(1000000, 1) - 8) < 0.00001, "bytes/time converts to decimal Mbps");
                Assert(Math.Abs(SpeedTest.Mbps(1048576, 2) - 4.194304) < 0.00001, "MiB is not MB");
                Assert(SpeedTest.Median(new double[] { 60, 1, 20, 5, 10 }) == 10, "odd median");
                Assert(SpeedTest.Median(new double[] { 20, 10, 40, 30 }) == 25, "even median");
                bool rejected = false;
                try { new SpeedTest(new Uri("http://example.com/"), 1, 1, 1, 1); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "public HTTP endpoints rejected");
                using (var server = new LocalServer("ok"))
                {
                    var samples = new Samples();
                    SpeedTestResult result = new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(result.Completed, "local complete HTTP test: " + result.Detail);
                    Assert(result.LatencyMs.HasValue && result.DownloadMbps > 0 && result.UploadMbps > 0, "all metrics measured");
                    Assert(result.DownloadBytes == 8192 && result.UploadBytes == 4096, "actual download/upload byte count");
                    Assert(server.Downloaded == 8192 && server.Uploaded == 4096, "server corroborates byte counts");
                    Assert(server.LatencyRequests >= 5 && result.IdleLatency.Samples.Count == 5, "idle uses five bytes=0 requests with additional loaded probes");
                    Assert(result.TotalBytes == 12288 && result.TotalBytes <= SpeedTest.MaximumPayloadBytes, "small configured traffic caps enforced");
                    Assert(result.Node.Contains("LHR"), "node derived from Cf-Ray");
                    Assert(!server.SawAuthorization, "no default credentials transmitted");
                    CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 8192);
                    CompletedPhase(samples, SpeedTestPhase.Upload, result.UploadMbps, 4096);
                    Assert(result.Phase == SpeedTestPhase.Upload && result.PhaseCompleted && result.PhaseAverageMbps == result.UploadMbps, "terminal result retains upload endpoint");
                    var copied = result.Copy();
                    copied.IdleLatency.Samples[0].Milliseconds = -1;
                    copied.IdleLatency.Samples.Clear();
                    Assert(result.IdleLatency.Samples.Count == 5 && result.IdleLatency.Samples[0].Milliseconds >= 0, "Copy deeply isolates latency collection and typed samples");
                    Assert(copied.Phase == result.Phase && copied.PhaseElapsedSeconds == result.PhaseElapsedSeconds && copied.PhaseAverageMbps == result.PhaseAverageMbps && copied.PhaseCompleted, "Copy retains additive phase fields");
                    var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
                    var json = (Dictionary<string, object>)serializer.DeserializeObject(serializer.Serialize(result));
                    Assert(Convert.ToInt32(json["Phase"]) == 2 && (bool)json["PhaseCompleted"] && json.ContainsKey("PhaseElapsedSeconds") && json.ContainsKey("PhaseAverageMbps"), "existing JSON serializer exports additive phase contract");
                }
                using (var server = new LocalServer("delayed-burst"))
                {
                    var samples = new Samples();
                    var result = new SpeedTest(server.Url, 128 * 1024, 4096, 2000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(result.Completed, "delayed burst transfer completes");
                    bool waiting = false, partial = false;
                    foreach (var sample in samples.Values)
                        if (sample.Phase == SpeedTestPhase.Download && !sample.PhaseCompleted)
                        {
                            if (sample.PhaseElapsedSeconds >= 0.15 && sample.DownloadBytes == 0) waiting = true;
                            if (sample.DownloadBytes > 0 && sample.DownloadBytes < 128 * 1024) partial = true;
                            if (sample.DownloadBytes < 128 * 1024) Assert(!sample.PhaseAverageMbps.HasValue, "first byte delay and partial burst never invent confirmed speed");
                        }
                    Assert(waiting && partial, "periodic reports cover no-byte wait and partial burst");
                    var end = CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 128 * 1024);
                    Assert(end.PhaseElapsedSeconds >= 0.55, "download endpoint includes delayed first byte and burst gap");
                }
                using (var server = new LocalServer("upload-delay"))
                {
                    var samples = new Samples();
                    var result = new SpeedTest(server.Url, 8192, 4096, 2000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(result.Completed && result.DownloadBytes == 8192 + 128 && result.UploadBytes == 4096, "nonempty upload acknowledgement stays in raw traffic accounting");
                    bool awaitingConfirmation = false, partialAck = false;
                    foreach (var sample in samples.Values)
                        if (sample.Phase == SpeedTestPhase.Upload && !sample.PhaseCompleted)
                        {
                            if (sample.PhaseElapsedSeconds >= 0.15 && sample.UploadBytes == 4096) awaitingConfirmation = true;
                            if (sample.DownloadBytes > 8192 && sample.DownloadBytes < 8192 + 128) partialAck = true;
                            if (sample.DownloadBytes < 8192 + 128) Assert(!sample.PhaseAverageMbps.HasValue, "upload is not confirmed until full acknowledgement finishes");
                            Assert(sample.DownloadMbps == result.DownloadMbps, "upload acknowledgement cannot alter completed download metric");
                        }
                    Assert(awaitingConfirmation && partialAck, "upload reports include waiting and partial nonempty acknowledgement");
                    CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 8192);
                    var end = CompletedPhase(samples, SpeedTestPhase.Upload, result.UploadMbps, 4096);
                    Assert(end.PhaseElapsedSeconds >= 0.55, "upload denominator includes server confirmation and acknowledgement tail");
                }
                using (var server = new LocalServer("fast"))
                {
                    var samples = new Samples();
                    var result = new SpeedTest(server.Url, 1, 1, 1000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(result.Completed, "single byte phases complete");
                    Assert(result.DownloadLatency.Status == LatencyMeasurementStatus.InsufficientSamples && result.UploadLatency.Status == LatencyMeasurementStatus.InsufficientSamples, "fast stages explicitly report insufficient loaded samples");
                    Assert(!result.DownloadLatency.MedianMs.HasValue && !result.DownloadLatency.P95Ms.HasValue && !result.DownloadLatencyDeltaMs.HasValue && !result.UploadLatencyDeltaMs.HasValue, "insufficient loaded samples never fabricate latency or zero delta");
                    Assert(CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 1).PhaseElapsedSeconds < 0.2, "fast download emits forced endpoint before report interval");
                    Assert(CompletedPhase(samples, SpeedTestPhase.Upload, result.UploadMbps, 1).PhaseElapsedSeconds < 0.2, "fast upload emits forced endpoint before report interval");
                }
                using (var server = new LocalServer("upload-delay"))
                using (var cancel = new CancellationTokenSource())
                {
                    var samples = new Samples();
                    var task = Task.Run(delegate { return new SpeedTest(server.Url, 8192, 4096, 2000, 4000).Run(false, samples, cancel.Token); });
                    Assert(server.SlowRequestStarted.WaitOne(1500), "server receives upload before delayed confirmation cancellation");
                    cancel.Cancel();
                    Assert(task.Wait(1500), "cancel interrupts upload confirmation wait");
                    Assert(!task.Result.Completed && task.Result.DownloadMbps.HasValue && !task.Result.UploadMbps.HasValue && !task.Result.PhaseAverageMbps.HasValue, "unconfirmed cancelled upload retains download but no upload average");
                    Assert(task.Result.Phase == SpeedTestPhase.Upload && !task.Result.PhaseCompleted, "cancelled upload has no success endpoint");
                    foreach (var sample in samples.Values) if (sample.Phase == SpeedTestPhase.Upload) Assert(!sample.PhaseCompleted, "upload wait cancellation never reports phase success");
                }
                using (var server = new LocalServer("delayed-burst"))
                {
                    var result = new SpeedTest(server.Url, 8192, 4096, 2000, 4000).Run(false, new FailingSamples(), CancellationToken.None);
                    Assert(!result.Completed && !result.PhaseCompleted && !result.DownloadMbps.HasValue && result.Detail.Contains("observer fixture"), "periodic observer failure returns incomplete result without crashing timer thread");
                }
                using (var server = new LocalServer("failure"))
                {
                    SpeedTestResult result = new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, null, CancellationToken.None);
                    Assert(!result.Completed && result.Detail.Contains("503"), "HTTP failure is explicit");
                    Assert(!result.DownloadMbps.HasValue && !result.UploadMbps.HasValue, "HTTP failure is never a throughput sample");
                }
                using (var server = new LocalServer("truncated"))
                {
                    var samples = new Samples();
                    SpeedTestResult result = new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(!result.Completed && !result.DownloadMbps.HasValue, "truncated download rejected");
                    Assert(result.DownloadBytes == 4096, "partial download remains in traffic accounting");
                    foreach (var sample in samples.Values) Assert(!sample.PhaseCompleted && !sample.PhaseAverageMbps.HasValue, "partial failed body produces no confirmed average or success endpoint");
                }
                using (var server = new LocalServer("upload-failure"))
                {
                    SpeedTestResult result = new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, null, CancellationToken.None);
                    Assert(!result.Completed && result.Detail.Contains("503") && result.DownloadMbps > 0, "upload HTTP failure preserves completed download");
                    Assert(!result.UploadMbps.HasValue && result.UploadBytes == 4096, "failed upload has traffic but no speed");
                }
                using (var server = new LocalServer("partial-last"))
                {
                    var samples = new Samples();
                    var result = new SpeedTest(server.Url, 264 * 1024, 4096, 2000, 4000).Run(false, samples, CancellationToken.None);
                    Assert(!result.Completed && !result.DownloadMbps.HasValue && result.DownloadBytes == 260 * 1024, "failed partial tail stays in usage but clears incomplete final metric");
                    Assert(result.PhaseAverageMbps == SpeedTest.Mbps(256 * 1024, result.PhaseElapsedSeconds), "failed partial tail is excluded from cumulative confirmed numerator");
                    foreach (var sample in samples.Values) Assert(!sample.PhaseCompleted, "failed partial tail never reports successful endpoint");
                }
                using (var server = new LocalServer("long-response"))
                {
                    SpeedTestResult result = new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, null, CancellationToken.None);
                    Assert(!result.Completed && !result.UploadMbps.HasValue && result.Detail.Contains("4 KiB"), "oversize upload acknowledgement rejected");
                    Assert(result.DownloadBytes <= 8192 + 4097, "upload acknowledgement read is bounded");
                }
                using (var server = new LocalServer("slow-last"))
                {
                    var samples = new Samples();
                    SpeedTestResult result = new SpeedTest(server.Url, 264 * 1024, 4096, 700, 4000).Run(false, samples, CancellationToken.None);
                    Assert(result.Completed && result.DownloadMbps > 0, "phase deadline permits conservative result from confirmed samples");
                    Assert(server.Disconnected.WaitOne(1500) && result.DownloadBytes == 256 * 1024, "last slow request is aborted and excluded from confirmed bytes");
                    Assert(result.DownloadMbps < SpeedTest.Mbps(256 * 1024, 0.3), "slow interrupted request remains in throughput denominator");
                    var end = CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 256 * 1024);
                    Assert(end.PhaseElapsedSeconds >= 0.6, "endpoint includes the whole interrupted request wait");
                    int waitSamples = 0; double previous = Double.PositiveInfinity;
                    foreach (var sample in samples.Values)
                        if (sample.Phase == SpeedTestPhase.Download && sample.PhaseAverageMbps.HasValue && !sample.PhaseCompleted)
                        {
                            Assert(sample.PhaseAverageMbps <= previous, "unchanged confirmed numerator decreases during zero-traffic tail");
                            previous = sample.PhaseAverageMbps.Value; waitSamples++;
                        }
                    Assert(waitSamples >= 2, "slow tail emits repeated no-traffic curve points");
                }
                using (var server = new LocalServer("slow-last"))
                using (var cancel = new CancellationTokenSource())
                {
                    var samples = new Samples();
                    var task = Task.Run(delegate { return new SpeedTest(server.Url, 264 * 1024, 4096, 2000, 4000).Run(false, samples, cancel.Token); });
                    Assert(server.SlowRequestStarted.WaitOne(1500), "fast sample completes before user cancels slow tail");
                    cancel.Cancel();
                    Assert(task.Wait(1500), "cancel returns while slow tail is blocked");
                    Assert(task.Result.State == "测速已停止" && !task.Result.DownloadMbps.HasValue && !task.Result.UploadMbps.HasValue, "cancel clears incomplete phase speed instead of retaining fast sample");
                    Assert(task.Result.LatencyMs.HasValue && task.Result.DownloadBytes == 256 * 1024, "cancel retains complete latency and actual traffic");
                    foreach (var sample in samples.Values) Assert(!sample.PhaseCompleted, "cancelled phase never emits success endpoint");
                    Assert(!task.Result.PhaseCompleted && task.Result.Phase == SpeedTestPhase.Download, "cancel leaves current phase explicitly incomplete");
                }
                using (var server = new LocalServer("slow-last"))
                {
                    var result = new SpeedTest(server.Url, 264 * 1024, 4096, 2000, 500).Run(false, null, CancellationToken.None);
                    Assert(!result.Completed && !result.DownloadMbps.HasValue && result.LatencyMs.HasValue, "total deadline clears incomplete phase speed and retains complete latency");
                }
                using (var server = new LocalServer("stall"))
                using (var cancel = new CancellationTokenSource())
                {
                    var watch = Stopwatch.StartNew();
                    var task = Task.Run(delegate { return new SpeedTest(server.Url, 8192, 4096, 1000, 4000).Run(false, null, cancel.Token); });
                    Assert(server.Connected.WaitOne(1500), "server received request before cancellation");
                    cancel.Cancel();
                    Assert(task.Wait(2000), "cancel actively interrupts blocked HTTP response");
                    Assert(task.Result.State == "测速已停止" && watch.ElapsedMilliseconds < 2500, "cancellation terminal state");
                    Assert(server.Disconnected.WaitOne(1500), "cancellation actually closes TCP request");
                }
                using (var server = new LocalServer("stall"))
                {
                    var watch = Stopwatch.StartNew();
                    var result = new SpeedTest(server.Url, 8192, 4096, 1000, 350).Run(false, null, CancellationToken.None);
                    Assert(!result.Completed && watch.ElapsedMilliseconds < 2000, "total deadline aborts stalled HTTP");
                    Assert(server.Disconnected.WaitOne(1500), "deadline actually closes TCP request");
                }
                rejected = false;
                try { new SpeedTest(new Uri("http://127.0.0.1/"), 49 * SpeedTest.MiB, 1, 1, 1); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected, "configured download cannot exceed production cap");
                Assert(SpeedTest.Percentile95(new double[] { 1, 2, 3, 4, 5 }) == 5, "p95 uses nearest rank of successful observations");
                using (var server = new ConcurrentLatencyServer(1700, 90))
                {
                    var point = ServicePointManager.FindServicePoint(server.Url); int oldLimit = point.ConnectionLimit;
                    point.ConnectionLimit = 1;
                    try
                    {
                        var samples = new Samples();
                        var result = new SpeedTest(server.Url, 4096, 4096, 2300, 6000).Run(false, samples, CancellationToken.None);
                        Assert(result.Completed && result.DownloadMbps > 0 && result.UploadMbps > 0, "concurrent fixture preserves throughput under per-group connection limit one: " + result.Detail);
                        Assert(result.IdleLatency.Status == LatencyMeasurementStatus.Ready && result.DownloadLatency.Status == LatencyMeasurementStatus.Ready && result.UploadLatency.Status == LatencyMeasurementStatus.Ready, "three phases have enough genuine latency observations");
                        Assert(result.DownloadLatencyDeltaMs > 40 && result.UploadLatencyDeltaMs > 40, "real server load increases both phase medians relative to idle");
                        Assert(result.DownloadLatency.P95Ms >= result.DownloadLatency.MedianMs && result.UploadLatency.P95Ms >= result.UploadLatency.MedianMs, "p95 and median use matching successful typed samples");
                        Assert(result.DownloadLatency.FailureCount == 0 && result.UploadLatency.FailureCount == 0, "phase stop cancellation does not create failure");
                        Assert(server.MaximumActive >= 2 && server.MaximumProbes == 1, "dedicated connection group overlaps transfer without queued parallel probes");
                        Assert(result.TotalBytes == 8192 && server.Uploaded == 4096 && server.Downloaded == 4096, "loaded probes add no fictional payload bytes");
                        CheckLatencySamples(result.DownloadLatency, SpeedTestPhase.Download);
                        CheckLatencySamples(result.UploadLatency, SpeedTestPhase.Upload);
                        int before = result.DownloadLatency.Samples.Count; Thread.Sleep(120);
                        Assert(before == result.DownloadLatency.Samples.Count, "no probe mutates a returned frozen phase");
                        var old = samples.Values[0];
                        Assert(old.IdleLatency.Samples.Count == 0 && old.DownloadLatency.Samples.Count == 0, "progress snapshot remains isolated from worker mutation");
                    }
                    finally { point.ConnectionLimit = oldLimit; }
                }
                using (var server = new ConcurrentLatencyServer(2100, 1800))
                {
                    var result = new SpeedTest(server.Url, 4096, 4096, 2500, 6000).Run(false, null, CancellationToken.None);
                    Assert(result.Completed && result.DownloadLatency.FailureCount >= 1 && result.UploadLatency.FailureCount >= 1, "bounded slow probes time out while throughput still completes");
                    Assert(result.DownloadLatency.Status == LatencyMeasurementStatus.InsufficientSamples && !result.DownloadLatencyDeltaMs.HasValue, "failed probes are excluded from median and delta");
                    foreach (var sample in result.DownloadLatency.Samples)
                        if (sample.State == LatencySampleState.Failed) Assert(!sample.Milliseconds.HasValue && sample.FailureCode == "Timeout", "timeout remains a typed failed observation");
                    Assert(server.MaximumProbes <= 2, "timeout does not queue multiple client probes (server may still finish an aborted request)");
                }
                using (var server = new ConcurrentLatencyServer(1500, 1000))
                using (var cancel = new CancellationTokenSource())
                {
                    var task = Task.Run(delegate { return new SpeedTest(server.Url, 4096, 4096, 2000, 6000).Run(false, null, cancel.Token); });
                    Assert(server.TransferStarted.WaitOne(1500), "loaded transfer begins before cancel");
                    Thread.Sleep(100); var watch = Stopwatch.StartNew(); cancel.Cancel();
                    Assert(task.Wait(1500) && watch.ElapsedMilliseconds < 1400, "cancel joins and actively aborts loaded probe promptly");
                    Assert(task.Result.DownloadLatency.Status == LatencyMeasurementStatus.Interrupted && task.Result.DownloadLatency.FailureCount == 0, "external cancellation marks phase interrupted without false probe failures");
                    Assert(!task.Result.DownloadLatencyDeltaMs.HasValue && !task.Result.UploadLatencyDeltaMs.HasValue, "cancelled insufficient probes have no fabricated delta");
                }
                using (var server = new ConcurrentLatencyServer(650, 5, true))
                {
                    var result = new SpeedTest(server.Url, 4096, 4096, 1000, 3000).Run(false, null, CancellationToken.None);
                    int invalid = result.DownloadLatency.FailureCount + result.UploadLatency.FailureCount;
                    Assert(result.Completed && invalid > 0, "nonempty probe response is rejected independently from successful throughput");
                    Assert(result.DownloadBytes == 4096 + invalid && result.UploadBytes == 4096, "every consumed invalid zero-byte probe body byte enters actual traffic accounting");
                    Assert(result.TotalBytes <= SpeedTest.MaximumPayloadBytes && result.DownloadBytes == server.Downloaded, "bounded invalid probe reads agree with server actual body traffic");
                    Assert(!result.DownloadLatencyDeltaMs.HasValue && !result.UploadLatencyDeltaMs.HasValue, "invalid nonempty probes cannot create a latency delta");
                }
                Assert(48 * SpeedTest.MiB + 24 * SpeedTest.MiB + 100 * 4097 + 128 < SpeedTest.MaximumPayloadBytes, "transfer caps, bounded acknowledgement tails and at most 30-second scheduled probe bodies fit hard 73 MiB ceiling");
                Assert(loopbackBytes < 2 * SpeedTest.MiB, "all loopback fixtures stay below 2 MiB actual body traffic");
                Console.WriteLine("PASS: " + assertions + " speed-test assertions; loopback traffic only; body bytes=" + loopbackBytes + ".");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
        }

        private static void CheckLatencySamples(LatencyStatistics statistics, SpeedTestPhase phase)
        {
            var values = new List<double>(); int failures = 0; double previous = -1;
            foreach (var sample in statistics.Samples)
            {
                Assert(sample.Phase == phase && sample.StartedUtc.Kind == DateTimeKind.Utc && sample.PhaseElapsedSeconds >= previous, "probe records immutable originating phase, UTC start and monotonic phase start time");
                previous = sample.PhaseElapsedSeconds;
                if (sample.State == LatencySampleState.Success) values.Add(sample.Milliseconds.Value); else failures++;
            }
            Assert(values.Count == statistics.SuccessCount && failures == statistics.FailureCount, "typed samples corroborate counts");
            Assert(statistics.MedianMs == SpeedTest.Median(values.ToArray()) && statistics.P95Ms == SpeedTest.Percentile95(values.ToArray()), "statistics are computed from exactly the exported typed observations");
        }

        private sealed class ConcurrentLatencyServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread accept;
            private readonly int transferDelay, probeDelay;
            private readonly bool extraProbeBody;
            private readonly List<Thread> workers = new List<Thread>();
            private volatile bool disposed;
            private int active, probes, transfers;
            public int MaximumActive, MaximumProbes;
            public long Downloaded, Uploaded;
            public readonly ManualResetEvent TransferStarted = new ManualResetEvent(false);
            public Uri Url;
            public ConcurrentLatencyServer(int delay, int latency, bool malformedProbeBody = false)
            {
                transferDelay = delay; probeDelay = latency;
                extraProbeBody = malformedProbeBody;
                listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                Url = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
                accept = new Thread(delegate()
                {
                    while (!disposed)
                    {
                        try
                        {
                            var client = listener.AcceptTcpClient();
                            var worker = new Thread(delegate() { Handle(client); }); worker.IsBackground = true;
                            lock (workers) workers.Add(worker); worker.Start();
                        }
                        catch (SocketException) { if (disposed) break; throw; }
                    }
                });
                accept.IsBackground = true; accept.Start();
            }
            private void Handle(TcpClient client)
            {
                bool probe = false, transfer = false;
                int current = Interlocked.Increment(ref active);
                lock (workers) MaximumActive = Math.Max(MaximumActive, current);
                try
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        client.ReceiveTimeout = 3000; client.SendTimeout = 3000;
                        var header = new StringBuilder();
                        while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                        { int b = stream.ReadByte(); if (b < 0 || header.Length > 16000) return; header.Append((char)b); }
                        string request = header.ToString(), path = request.Split(' ')[1];
                        bool post = request.StartsWith("POST ", StringComparison.Ordinal); int count = 0;
                        if (post)
                        {
                            foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) count = Int32.Parse(line.Substring(15).Trim());
                        }
                        else count = Int32.Parse(path.Substring(path.IndexOf("bytes=", StringComparison.Ordinal) + 6).Split('&')[0]);
                        probe = !post && count == 0; transfer = !probe;
                        bool loadedProbe = probe && Interlocked.CompareExchange(ref transfers, 0, 0) > 0;
                        if (probe)
                        {
                            int running = Interlocked.Increment(ref probes); lock (workers) MaximumProbes = Math.Max(MaximumProbes, running);
                            if (!Delay(client, loadedProbe ? probeDelay : 5)) return;
                        }
                        else
                        {
                            Interlocked.Increment(ref transfers); TransferStarted.Set();
                            if (post)
                            {
                                var bytes = new byte[1024];
                                for (int n = 0; n < count; )
                                { int read = stream.Read(bytes, 0, Math.Min(bytes.Length, count - n)); if (read <= 0) return; n += read; Interlocked.Add(ref Uploaded, read); Interlocked.Add(ref loopbackBytes, read); }
                            }
                            if (!Delay(client, transferDelay)) return;
                        }
                        int body = post ? 0 : count;
                        bool invalidProbe = extraProbeBody && loadedProbe;
                        if (invalidProbe) body = 1;
                        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n" + (invalidProbe ? "" : "Content-Length: " + body + "\r\n") + "Connection: close\r\n\r\n");
                        stream.Write(response, 0, response.Length);
                        if (body > 0) { byte[] bytes = new byte[body]; stream.Write(bytes, 0, body); Interlocked.Add(ref Downloaded, body); Interlocked.Add(ref loopbackBytes, body); }
                    }
                }
                catch (IOException) { }
                catch (SocketException) { }
                finally
                {
                    if (probe) Interlocked.Decrement(ref probes);
                    if (transfer) Interlocked.Decrement(ref transfers);
                    Interlocked.Decrement(ref active);
                }
            }
            private static bool Delay(TcpClient client, int milliseconds)
            {
                var watch = Stopwatch.StartNew();
                while (watch.ElapsedMilliseconds < milliseconds)
                {
                    if (client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0) return false;
                    Thread.Sleep(Math.Min(10, Math.Max(1, milliseconds - (int)watch.ElapsedMilliseconds)));
                }
                return true;
            }
            public void Dispose()
            {
                disposed = true; listener.Stop(); accept.Join(2000);
                Thread[] pending; lock (workers) pending = workers.ToArray();
                foreach (var worker in pending) worker.Join(3500);
                TransferStarted.Dispose();
            }
        }

        private sealed class LocalServer : IDisposable
        {
            private readonly TcpListener listener;
            private readonly Thread thread;
            private readonly string mode;
            private volatile bool disposed;
            public readonly ManualResetEvent Connected = new ManualResetEvent(false);
            public readonly ManualResetEvent Disconnected = new ManualResetEvent(false);
            public readonly ManualResetEvent SlowRequestStarted = new ManualResetEvent(false);
            public long Downloaded;
            public long Uploaded;
            public int LatencyRequests;
            public bool SawAuthorization;
            private int dataRequests;
            public Uri Url;
            public LocalServer(string behavior)
            {
                mode = behavior; listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                Url = new Uri("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
                thread = new Thread(Serve); thread.IsBackground = true; thread.Start();
            }
            private void Serve()
            {
                while (!disposed)
                {
                    try
                    {
                        using (var client = listener.AcceptTcpClient())
                        using (var stream = client.GetStream())
                        {
                            client.ReceiveTimeout = 2500; client.SendTimeout = 2500;
                            var header = new StringBuilder();
                            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                            {
                                int value = stream.ReadByte(); if (value < 0) throw new IOException();
                                header.Append((char)value); if (header.Length > 16384) throw new IOException();
                            }
                            Connected.Set();
                            string request = header.ToString();
                            SawAuthorization |= request.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) >= 0;
                            if (mode == "stall")
                            {
                                try { if (stream.ReadByte() < 0) Disconnected.Set(); } catch (IOException) { Disconnected.Set(); }
                                continue;
                            }
                            string path = request.Split(' ')[1];
                            bool post = request.StartsWith("POST ", StringComparison.Ordinal);
                            int count = 0;
                            if (post)
                            {
                                foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
                                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) count = Int32.Parse(line.Substring(15).Trim());
                                var buffer = new byte[1024];
                                for (int received = 0; received < count; )
                                {
                                    int read = stream.Read(buffer, 0, Math.Min(buffer.Length, count - received));
                                    if (read == 0) throw new IOException(); received += read; Uploaded += read; Interlocked.Add(ref loopbackBytes, read);
                                }
                            }
                            else
                            {
                                string number = path.Substring(path.IndexOf("bytes=", StringComparison.Ordinal) + 6).Split('&')[0];
                                count = Int32.Parse(number); if (count == 0) LatencyRequests++;
                                if (count > 0) dataRequests++;
                                if (mode == "slow-last" && count > 0 && dataRequests > 1)
                                {
                                    SlowRequestStarted.Set();
                                    try { if (stream.ReadByte() < 0) Disconnected.Set(); } catch (IOException) { Disconnected.Set(); }
                                    continue;
                                }
                            }
                            int status = mode == "failure" || (mode == "upload-failure" && post) ? 503 : 200;
                            int responseCount = post || status != 200 ? 0 : count;
                            if (mode == "long-response" && post) responseCount = 5000;
                            if (mode == "upload-delay" && post) { responseCount = 128; SlowRequestStarted.Set(); Thread.Sleep(320); }
                            byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Test\r\nContent-Length: " + responseCount + "\r\nConnection: close\r\nCf-Ray: abcdef-LHR\r\n\r\n");
                            stream.Write(headers, 0, headers.Length);
                            if (responseCount > 0)
                            {
                                if (mode == "truncated" || (mode == "partial-last" && dataRequests > 1)) responseCount /= 2;
                                var bytes = new byte[responseCount];
                                if ((mode == "delayed-burst" && !post) || (mode == "upload-delay" && post))
                                {
                                    if (!post) Thread.Sleep(320);
                                    int half = bytes.Length / 2;
                                    stream.Write(bytes, 0, half); Downloaded += half; Interlocked.Add(ref loopbackBytes, half);
                                    Thread.Sleep(320);
                                    stream.Write(bytes, half, bytes.Length - half); Downloaded += bytes.Length - half; Interlocked.Add(ref loopbackBytes, bytes.Length - half);
                                }
                                else { stream.Write(bytes, 0, bytes.Length); Downloaded += bytes.Length; Interlocked.Add(ref loopbackBytes, bytes.Length); }
                            }
                        }
                    }
                    catch (Exception) { if (disposed) return; }
                }
            }
            public void Dispose()
            {
                disposed = true; listener.Stop(); thread.Join(3000); Connected.Dispose(); Disconnected.Dispose(); SlowRequestStarted.Dispose();
            }
        }
    }
}
