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
                    Assert(server.LatencyRequests == 5, "five requests use bytes=0");
                    Assert(result.TotalBytes == 12288 && result.TotalBytes <= SpeedTest.MaximumPayloadBytes, "small configured traffic caps enforced");
                    Assert(result.Node.Contains("LHR"), "node derived from Cf-Ray");
                    Assert(!server.SawAuthorization, "no default credentials transmitted");
                    CompletedPhase(samples, SpeedTestPhase.Download, result.DownloadMbps, 8192);
                    CompletedPhase(samples, SpeedTestPhase.Upload, result.UploadMbps, 4096);
                    Assert(result.Phase == SpeedTestPhase.Upload && result.PhaseCompleted && result.PhaseAverageMbps == result.UploadMbps, "terminal result retains upload endpoint");
                    var copied = result.Copy();
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
                Assert(loopbackBytes < 2 * SpeedTest.MiB, "all loopback fixtures stay below 2 MiB actual body traffic");
                Console.WriteLine("PASS: " + assertions + " speed-test assertions; loopback traffic only; body bytes=" + loopbackBytes + ".");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); return 1; }
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
