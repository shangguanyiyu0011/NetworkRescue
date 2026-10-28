using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace NetworkRescue
{
    internal static class MonitoringHistoryTests
    {
        private static int checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkRescue.MonitoringHistory.v1");
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.I1)] private static extern bool CreateSymbolicLink(string link, string target, int flags);
        private static void Assert(bool condition, string name) { checks++; if (!condition) throw new Exception("Failed: " + name); }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; } catch (ArgumentException) { rejected = true; }
            Assert(rejected, name);
        }
        private static string PathFor(string root, string id) { return Path.Combine(root, id + ".nrh"); }
        private static MonitoringHistoryRecord Record(int count, int targetCount, string state)
        {
            MonitorTarget[] targets = Enumerable.Range(1, targetCount).Select(delegate(int index) { return new MonitorTarget { Id = "target-" + index, Host = "example" + index + ".test", Method = "icmp" }; }).ToArray();
            MonitoringHistoryRecord record = new MonitoringHistoryRecord { Id = Guid.NewGuid().ToString("N"), State = state, StartedUtc = Now, EndedUtc = Now.AddSeconds(Math.Max(1, count)), IntervalMs = 1000, RequestedCount = Math.Max(1, count), Targets = targets };
            record.Samples = targets.SelectMany(delegate(MonitorTarget target)
            {
                return Enumerable.Range(1, count).Select(delegate(int sequence)
                {
                    bool success = sequence > 1;
                    return new MonitorSample { Sequence = sequence, Sent = sequence, Received = sequence - 1, TimeUtc = Now.AddSeconds(sequence), Success = success, RttMs = success ? (long?)5 : null, LossPercent = 100.0 / sequence, Status = success ? "Success" : "TimedOut", Method = "ICMP", Route = "Network", TargetId = target.Id, TargetHost = target.Host, ResolvedAddress = "192.0.2.1" };
                });
            }).ToArray();
            record.Events = new[] { new NetworkChangeEvent { Sequence = 1, TimeUtc = Now, Kind = "MonitoringStarted" } }; record.TotalEvents = 1;
            return record;
        }
        private static string Decrypt(string path) { return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser)); }
        private static void Encrypt(string path, string xml) { File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(xml), Entropy, DataProtectionScope.CurrentUser)); }
        private static string Xml(MonitoringHistoryRecord record)
        {
            using (MemoryStream stream = new MemoryStream()) { new XmlSerializer(typeof(MonitoringHistoryDocument)).Serialize(stream, new MonitoringHistoryDocument { Version = 1, Record = record }); return Encoding.UTF8.GetString(stream.ToArray()); }
        }
        private static void RoundTrip(string root)
        {
            MonitoringHistoryStore store = new MonitoringHistoryStore(root);
            Assert(store.List().Length == 0 && store.Clear() == 0, "new history is empty");
            MonitoringHistoryRecord record = Record(5, 2, "Completed");
            MonitoringHistoryRecord saved = store.Save(record);
            record.Samples[0].Status = "changed"; saved.Targets[0].Host = "changed.test";
            MonitoringHistoryRecord loaded = new MonitoringHistoryStore(root).Load(record.Id);
            Assert(loaded.Samples.Length == 10 && loaded.Targets.Length == 2 && loaded.Samples[0].Status == "TimedOut" && loaded.Targets[0].Host == "example1.test", "encrypted restart roundtrip and detached copies");
            Assert(loaded.Samples[0].TimeUtc.Kind == DateTimeKind.Utc && loaded.Samples[0].RttMs == null && !loaded.SamplesMissing && !loaded.EventsTruncated, "sample metadata and explicit completeness");
            MonitoringHistoryInfo info = store.List().Single();
            Assert(info.Id == record.Id && info.State == "Completed" && info.TargetCount == 2 && info.SampleCount == 10 && info.EventCount == 1 && info.TotalEvents == 1, "list metadata reflects archived samples");
            string path = PathFor(root, record.Id), plain = Decrypt(path);
            Assert(plain.Contains("<Version>1</Version>") && plain.Contains("example1.test") && !plain.Contains("Fingerprint"), "schema one has no raw configuration fingerprints");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("example1.test"), "disk bytes contain ciphertext only");
            loaded.Samples[0].Status = "local-mutation";
            Assert(store.Load(record.Id).Samples[0].Status == "TimedOut", "load does not share mutable samples");
            record = Record(0, 1, "Running"); record.EndedUtc = record.StartedUtc; store.Save(record);
            byte[] runningBytes = File.ReadAllBytes(PathFor(root, record.Id));
            Assert(new MonitoringHistoryStore(root).Load(record.Id).State == "Interrupted" && store.List().Single(delegate(MonitoringHistoryInfo item) { return item.Id == record.Id; }).State == "Interrupted", "unfinished sessions display interrupted after reopening");
            Assert(runningBytes.SequenceEqual(File.ReadAllBytes(PathFor(root, record.Id))) && Decrypt(PathFor(root, record.Id)).Contains("<State>Running</State>"), "interrupted display never rewrites persisted state");
            Assert(store.Load(record.Id).SamplesMissing, "incomplete sample collection explicitly reported");
            record.State = "Stopped"; store.Save(record); record.State = "Running";
            Reject(delegate { store.Save(record); }, "stale checkpoint cannot overwrite a terminal session");
            Assert(store.Delete(record.Id.ToUpperInvariant()) && !store.Delete(record.Id) && store.Load(record.Id) == null, "canonical id deletion and missing reads");
            Assert(store.Clear() == 1 && store.List().Length == 0 && Directory.GetFiles(root, "*.tmp").Length == 0, "clear verified records and atomic temporary cleanup");
            record = Record(1, 1, "Completed"); record.Targets[0].Method = "https"; record.Targets[0].UseSystemProxy = true;
            MonitorSample http = record.Samples[0]; http.Method = "HTTPS"; http.Route = "WindowsProxy"; http.Success = http.ResponseReceived = true; http.HttpStatusCode = 503; http.Status = "HttpServerError"; http.RttMs = 20; http.Received = 1; http.LossPercent = 0;
            store.Save(record); loaded = store.Load(record.Id);
            Assert(loaded.Samples[0].ResponseReceived && !loaded.Samples[0].HttpSuccess && loaded.Samples[0].HttpStatusCode == 503 && loaded.Targets[0].UseSystemProxy, "HTTPS server response and proxy route remain distinct from HTTP success");
        }
        private static void Validation(string root)
        {
            MonitoringHistoryStore store = new MonitoringHistoryStore(root);
            MonitoringHistoryRecord record = Record(2, 1, "Completed"); record.Id = "../../escape"; Reject(delegate { store.Save(record); }, "unsafe id rejected");
            record = Record(2, 2, "Completed"); record.Targets[1].Id = record.Targets[0].Id; Reject(delegate { store.Save(record); }, "duplicate target ids rejected");
            record = Record(2, 1, "Completed"); record.StartedUtc = DateTime.SpecifyKind(record.StartedUtc, DateTimeKind.Local); Reject(delegate { store.Save(record); }, "non UTC time rejected");
            record = Record(2, 1, "Completed"); record.EndedUtc = record.StartedUtc.AddTicks(-1); Reject(delegate { store.Save(record); }, "backward session time rejected");
            record = Record(2, 1, "Completed"); record.Samples[0].TimeUtc = record.StartedUtc.AddTicks(-1); Reject(delegate { store.Save(record); }, "out of session sample time rejected");
            record = Record(2, 1, "Completed"); record.Samples[1].Sequence = 1; Reject(delegate { store.Save(record); }, "duplicate target sequence rejected");
            record = Record(2, 1, "Completed"); record.Samples[0].TargetId = "foreign"; Reject(delegate { store.Save(record); }, "foreign sample rejected");
            record = Record(2, 1, "Completed"); record.Samples[0].TargetHost = "foreign.test"; Reject(delegate { store.Save(record); }, "mismatched target host rejected");
            record = Record(2, 1, "Completed"); record.Samples[0].LossPercent = Double.NaN; Reject(delegate { store.Save(record); }, "non finite metric rejected");
            record = Record(2, 1, "Completed"); record.RequestedCount = 3601; Reject(delegate { store.Save(record); }, "requested sample limit enforced");
            record = Record(1, 1, "Completed"); record.Samples = new MonitorSample[MonitoringHistoryStore.MaximumSamples + 1]; Reject(delegate { store.Save(record); }, "archive sample cap enforced without trimming");
            record = Record(2, 1, "Interrupted"); Reject(delegate { store.Save(record); }, "display-only interrupted state cannot be persisted");
            record = Record(2, 1, "Completed"); record.Events[0].Kind = "arbitrary"; Reject(delegate { store.Save(record); }, "unknown event kind rejected");
            record = Record(2, 1, "Completed"); record.TotalEvents = 2; Reject(delegate { store.Save(record); }, "unexplained missing events rejected");
            record = Record(2, 1, "Completed"); record.Events[0] = new NetworkChangeEvent { Sequence = 1, TimeUtc = Now, Kind = "SettingChanged", Setting = "ProxyPac", Before = "https://user:password@proxy.test/private?token=secret", After = "" }; Reject(delegate { store.Save(record); }, "raw proxy credentials rejected before serialization");
            Assert(!Directory.Exists(root), "invalid input creates no record files");
            record = Record(2, 1, "Completed"); record.TotalEvents = 501;
            record.Events = Enumerable.Range(2, 500).Select(delegate(int index) { return new NetworkChangeEvent { Sequence = index, TimeUtc = Now, Kind = "ObservationUnavailable", ErrorCode = "SnapshotUnavailable" }; }).ToArray();
            store.Save(record); Assert(store.Load(record.Id).EventsTruncated && store.List().Single().EventsTruncated, "event retention window explicitly records truncation");
            record = Record(2, 1, "Stopped"); record.Samples = new[] { record.Samples[1] }; store.Save(record);
            Assert(store.Load(record.Id).Samples.Length == 1 && store.Load(record.Id).Samples[0].Sequence == 2 && store.Load(record.Id).SamplesMissing, "missing samples are reported without fabricating an earlier sample");
        }
        private static void Corruption(string root)
        {
            MonitoringHistoryStore store = new MonitoringHistoryStore(root); MonitoringHistoryRecord good = Record(2, 1, "Completed"), bad = Record(2, 1, "Completed"); store.Save(good); store.Save(bad);
            string badPath = PathFor(root, bad.Id); byte[] damaged = Encoding.UTF8.GetBytes("damaged-not-dpapi"); File.WriteAllBytes(badPath, damaged);
            Assert(store.List().Length == 2 && store.List().Count(delegate(MonitoringHistoryInfo item) { return item.ErrorCode != null; }) == 1, "damaged entry does not hide healthy history");
            Reject(delegate { store.Load(bad.Id); }, "damaged record read rejected"); Reject(delegate { store.Save(bad); }, "damaged record cannot be silently replaced"); Reject(delegate { store.Delete(bad.Id); }, "damaged record cannot be deleted as verified history");
            Assert(damaged.SequenceEqual(File.ReadAllBytes(badPath)), "damaged bytes remain intact");
            Assert(store.Clear() == 1 && File.Exists(badPath) && store.List().Single().ErrorCode != null, "clear preserves damaged records and keeps them visible");
            Encrypt(badPath, Xml(bad).Replace("<Version>1</Version>", "<Version>2</Version>")); Reject(delegate { store.Load(bad.Id); }, "unknown schema rejected");
            Encrypt(badPath, Xml(bad).Replace("<Version>1</Version>", "")); Reject(delegate { store.Load(bad.Id); }, "missing schema rejected");
            Encrypt(badPath, Xml(bad).Replace("</Record>", "<RawFingerprint>secret</RawFingerprint></Record>")); Reject(delegate { store.Load(bad.Id); }, "unknown serialized fields rejected");
            Encrypt(badPath, Xml(bad).Replace(bad.Id, Guid.NewGuid().ToString("N"))); Reject(delegate { store.Load(bad.Id); }, "filename and record identity must match");
            Encrypt(badPath, Xml(bad) + new string(' ', MonitoringHistoryStore.MaximumRecordBytes)); Reject(delegate { store.Load(bad.Id); }, "ciphertext size limit enforced");
            byte[] oversized = File.ReadAllBytes(badPath); Reject(delegate { store.Save(bad); }, "oversized encrypted existing entry cannot be replaced"); Assert(oversized.SequenceEqual(File.ReadAllBytes(badPath)), "oversized original preserved");
        }
        private static void Retention(string root)
        {
            MonitoringHistoryStore store = new MonitoringHistoryStore(root); string oldest = null;
            for (int i = 0; i < 21; i++)
            {
                MonitoringHistoryRecord record = Record(0, 1, "Stopped"); record.StartedUtc = Now.AddMinutes(i); record.EndedUtc = record.StartedUtc; record.Events[0].TimeUtc = record.StartedUtc;
                if (i == 0) oldest = record.Id; store.Save(record);
            }
            Assert(store.List().Length == 20 && store.Load(oldest) == null, "record retention removes oldest verified history");
            string blockedRoot = Path.Combine(root, "blocked"); Directory.CreateDirectory(blockedRoot);
            for (int i = 0; i < 20; i++) File.WriteAllText(PathFor(blockedRoot, Guid.NewGuid().ToString("N")), "damage");
            Reject(delegate { new MonitoringHistoryStore(blockedRoot).Save(Record(0, 1, "Stopped")); }, "damaged entries cannot be discarded to make record capacity");
            Assert(Directory.GetFiles(blockedRoot, "*.nrh").Length == 20, "retention leaves all damaged files untouched");
            string sizeRoot = Path.Combine(root, "size"); Directory.CreateDirectory(sizeRoot); string first = null;
            // Valid comment padding exercises the real 64 MiB budget without invented samples.
            for (int i = 0; i < 5; i++)
            {
                MonitoringHistoryRecord record = Record(0, 1, "Stopped"); record.StartedUtc = Now.AddMinutes(i); record.EndedUtc = record.StartedUtc; record.Events[0].TimeUtc = record.StartedUtc;
                if (i == 0) first = record.Id;
                string xml = Xml(record).Replace("</MonitoringHistoryDocument>", "<!--" + new string('p', 14 * 1024 * 1024) + "--></MonitoringHistoryDocument>"); Encrypt(PathFor(sizeRoot, record.Id), xml);
            }
            MonitoringHistoryStore sizeStore = new MonitoringHistoryStore(sizeRoot); sizeStore.Save(Record(0, 1, "Stopped"));
            Assert(sizeStore.Load(first) == null && Directory.GetFiles(sizeRoot, "*.nrh").Sum(delegate(string path) { return new FileInfo(path).Length; }) <= MonitoringHistoryStore.MaximumTotalBytes, "total byte retention deletes oldest verified records");
        }
        private static void FullArchive(string root)
        {
            MonitoringHistoryRecord record = Record(3600, 4, "Completed"); MonitoringHistoryStore store = new MonitoringHistoryStore(root); store.Save(record);
            MonitoringHistoryRecord loaded = new MonitoringHistoryStore(root).Load(record.Id);
            Assert(loaded.Samples.Length == 14400 && loaded.Samples.Count(delegate(MonitorSample sample) { return sample.Sequence == 1 && !sample.Success; }) == 4, "all 14400 real samples and early outage samples survive restart");
            Assert(loaded.Samples.Last().Sequence == 3600 && !loaded.SamplesMissing, "full session retains final samples too");
        }
        private static void Reparse(string root)
        {
            Directory.CreateDirectory(root); string destination = Path.Combine(root, "destination"), link = Path.Combine(root, "junction"); Directory.CreateDirectory(destination);
            using (Process process = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + destination + "\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })) { process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit(); Assert(process.ExitCode == 0, "test directory junction created"); }
            try
            {
                Reject(delegate { new MonitoringHistoryStore(link).Save(Record(0, 1, "Stopped")); }, "reparse root cannot be saved through");
                Reject(delegate { new MonitoringHistoryStore(Path.Combine(link, "child")).List(); }, "reparse ancestor cannot be read through");
                Assert(Directory.GetFiles(destination).Length == 0 && !Directory.Exists(Path.Combine(destination, "child")), "junction target remains unchanged");
            }
            finally { Directory.Delete(link); }
            MonitoringHistoryRecord record = Record(1, 1, "Completed"); string fileLink = PathFor(root, record.Id), target = Path.Combine(destination, "original.nrh"); Encrypt(target, Xml(record)); byte[] original = File.ReadAllBytes(target);
            if (CreateSymbolicLink(fileLink, target, 2) || CreateSymbolicLink(fileLink, target, 0))
            {
                try
                {
                    MonitoringHistoryStore store = new MonitoringHistoryStore(root); Assert(store.List().Single().ErrorCode == "MonitoringHistoryUnsafePath", "reparse file is reported without reading target");
                    Reject(delegate { store.Save(record); }, "reparse file cannot be replaced"); Reject(delegate { store.Delete(record.Id); }, "reparse file cannot be deleted");
                    Assert(store.Clear() == 0 && original.SequenceEqual(File.ReadAllBytes(target)), "clear leaves symlink and target intact");
                }
                finally { File.Delete(fileLink); }
            }
            else Console.WriteLine("File symlink test unavailable without Windows developer mode or symlink privilege; directory junction checks passed.");
        }
        public static int Main()
        {
            string parent = Path.GetFullPath(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
            string temporary = Path.Combine(parent, "monitor-history-checks-" + Guid.NewGuid().ToString("N"));
            try
            {
                RoundTrip(Path.Combine(temporary, "roundtrip")); Validation(Path.Combine(temporary, "validation")); Corruption(Path.Combine(temporary, "corrupt")); Retention(Path.Combine(temporary, "retention")); FullArchive(Path.Combine(temporary, "full")); Reparse(Path.Combine(temporary, "reparse"));
                Console.WriteLine("Monitoring history checks passed: " + checks + " (DPAPI fixtures only; no network operations)"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("After " + checks + " checks: " + ex); return 1; }
            finally { if (Path.GetDirectoryName(Path.GetFullPath(temporary)) == parent && Path.GetFileName(temporary).StartsWith("monitor-history-checks-", StringComparison.Ordinal) && Directory.Exists(temporary)) Directory.Delete(temporary, true); }
        }
    }
}
