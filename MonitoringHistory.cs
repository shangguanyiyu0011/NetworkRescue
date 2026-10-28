using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;

namespace NetworkRescue
{
    public sealed class MonitoringHistoryRecord
    {
        public string Id, State;
        public DateTime StartedUtc, EndedUtc;
        public int IntervalMs, RequestedCount, TotalEvents;
        public MonitorTarget[] Targets = new MonitorTarget[0];
        public MonitorSample[] Samples = new MonitorSample[0];
        public NetworkChangeEvent[] Events = new NetworkChangeEvent[0];
        public bool WifiCollectionEnabled;
        public int WifiTotalSamples;
        public WifiStabilitySample[] WifiSamples = new WifiStabilitySample[0];
        [XmlIgnore] public bool WifiTruncated { get { return WifiSamples != null && WifiTotalSamples > WifiSamples.Length; } }
        [XmlIgnore] public bool SamplesMissing { get { return Targets != null && Samples != null && Samples.Length < (long)Targets.Length * RequestedCount; } }
        [XmlIgnore] public bool EventsTruncated { get { return Events != null && TotalEvents > Events.Length; } }
    }

    public sealed class MonitoringHistoryInfo
    {
        public string Id, State, ErrorCode;
        public DateTime StartedUtc, EndedUtc;
        public int IntervalMs, RequestedCount, TargetCount, SampleCount, EventCount, TotalEvents;
        public bool WifiCollectionEnabled;
        public int WifiSampleCount, WifiTotalSamples;
        public bool WifiTruncated { get { return WifiTotalSamples > WifiSampleCount; } }
        public bool SamplesMissing { get { return SampleCount < (long)TargetCount * RequestedCount; } }
        public bool EventsTruncated { get { return TotalEvents > EventCount; } }
    }

    public sealed class MonitoringHistoryDocument
    {
        public int Version;
        public MonitoringHistoryRecord Record;
    }

    /// <summary>Per-user encrypted history. No network configuration snapshots or fingerprints are stored.</summary>
    public sealed class MonitoringHistoryStore
    {
        public const int MaximumRecords = 20, MaximumSamples = 14400, MaximumEvents = 500;
        public const int MaximumRecordBytes = 16 * 1024 * 1024;
        public const long MaximumTotalBytes = 64L * 1024 * 1024;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkRescue.MonitoringHistory.v1");
        private readonly string root, mutexName;

        public MonitoringHistoryStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkRescue", "Monitoring")) { }
        public MonitoringHistoryStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw Invalid("InvalidMonitoringHistoryDirectory");
            string fullRoot = Path.GetFullPath(root);
            this.root = fullRoot.Length == Path.GetPathRoot(fullRoot).Length ? fullRoot : fullRoot.TrimEnd(Path.DirectorySeparatorChar);
            using (SHA256 hash = SHA256.Create()) mutexName = @"Local\NetworkRescue.MonitoringHistory." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(this.root.ToUpperInvariant()))).Replace("-", "");
        }

        private static InvalidDataException Invalid(string code) { return new InvalidDataException(code); }
        private static bool StoreFailure(Exception ex)
        {
            return ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is SecurityException || ex is CryptographicException || ex is XmlException || ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException;
        }
        private T Locked<T>(Func<T> action)
        {
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool acquired = false;
                try
                {
                    try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                    if (!acquired) throw new IOException("MonitoringHistoryBusy");
                    return action();
                }
                finally { if (acquired) mutex.ReleaseMutex(); }
            }
        }

        // Inspect every existing ancestor as well as the final entry, including dangling links.
        private static void CheckPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!String.IsNullOrEmpty(current))
            {
                try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw Invalid("MonitoringHistoryUnsafePath"); }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                string parent = Path.GetDirectoryName(current);
                if (parent == current) break;
                current = parent;
            }
        }
        private static string CanonicalId(string id)
        {
            Guid value;
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out value) || value == Guid.Empty) throw Invalid("InvalidMonitoringHistoryId");
            return value.ToString("N");
        }
        private string FilePath(string id) { return Path.Combine(root, CanonicalId(id) + ".nrh"); }
        private static XmlSerializer Serializer()
        {
            XmlSerializer serializer = new XmlSerializer(typeof(MonitoringHistoryDocument));
            serializer.UnknownNode += delegate { throw Invalid("MonitoringHistoryUnknownField"); };
            serializer.UnknownAttribute += delegate { throw Invalid("MonitoringHistoryUnknownField"); };
            return serializer;
        }
        private static void Text(string value, int maximum, bool required)
        {
            if ((required && String.IsNullOrWhiteSpace(value)) || (value != null && (value.Length > maximum || value.Any(Char.IsControl)))) throw Invalid("InvalidMonitoringHistoryText");
        }
        private static void Token(string value, int maximum, bool required)
        {
            Text(value, maximum, required);
            if (value != null && value.Any(delegate(char c) { return !Char.IsLetterOrDigit(c) && c != '_' && c != '-'; })) throw Invalid("InvalidMonitoringHistoryToken");
        }
        private static void Time(DateTime value)
        {
            if (value.Kind != DateTimeKind.Utc || value < new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc) || value > DateTime.UtcNow.AddDays(1)) throw Invalid("InvalidMonitoringHistoryTime");
        }
        private static void Metric(double? value)
        {
            if (value.HasValue && (Double.IsNaN(value.Value) || Double.IsInfinity(value.Value) || value.Value < 0 || value.Value > 86400000)) throw Invalid("InvalidMonitoringHistoryMetric");
        }
        private static string SettingValue(string setting, string value)
        {
            Text(value, 2048, false); value = value ?? "";
            if (value == "") return value;
            if (setting == "ProxyServer" || setting == "ProxyPac") return value == "Invalid" || value == "Configured" ? value : NetworkChangeReader.SanitizeProxy(value, setting == "ProxyPac");
            if (setting == "ProxyEnabled") { if (value != "Enabled" && value != "Disabled" && value != "Invalid") throw Invalid("InvalidMonitoringHistorySetting"); }
            else if (setting == "AdapterStatus") { if (!Enum.GetNames(typeof(OperationalStatus)).Contains(value)) throw Invalid("InvalidMonitoringHistorySetting"); }
            else if (setting == "Addresses" || setting == "Dns" || setting == "Gateways")
            {
                foreach (string item in value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)) { IPAddress ignored; if (!IPAddress.TryParse(item, out ignored)) throw Invalid("InvalidMonitoringHistorySetting"); }
            }
            else throw Invalid("InvalidMonitoringHistorySetting");
            return value;
        }

        private static void Validate(MonitoringHistoryRecord record, bool saving)
        {
            if (record == null || record.Id != CanonicalId(record.Id) || !new[] { "Running", "Completed", "Stopped", "Failed" }.Contains(record.State)) throw Invalid("InvalidMonitoringHistoryRecord");
            Time(record.StartedUtc); Time(record.EndedUtc);
            if (record.WifiSamples == null || record.WifiTotalSamples < record.WifiSamples.Length || record.WifiSamples.Length != Math.Min(record.WifiTotalSamples, 720) || (!record.WifiCollectionEnabled && record.WifiTotalSamples != 0)) throw Invalid("InvalidMonitoringHistoryWifi");
            WifiStabilityReader.Validate(record.WifiSamples);
            DateTime previousWifi = record.StartedUtc;
            foreach (WifiStabilitySample wifi in record.WifiSamples)
            {
                Time(wifi.TimeUtc);
                if (wifi.TimeUtc < previousWifi || wifi.TimeUtc > record.EndedUtc) throw Invalid("InvalidMonitoringHistoryWifiTime");
                previousWifi = wifi.TimeUtc;
            }
            if (record.EndedUtc < record.StartedUtc || record.IntervalMs < 1000 || record.IntervalMs > 10000 || record.RequestedCount < 1 || record.RequestedCount > 3600 || record.Targets == null || record.Targets.Length < 1 || record.Targets.Length > 4 || record.Samples == null || record.Samples.Length > MaximumSamples || record.Samples.Length > record.Targets.Length * record.RequestedCount || record.Events == null || record.Events.Length > MaximumEvents || record.TotalEvents < record.Events.Length || record.Events.Length != Math.Min(record.TotalEvents, MaximumEvents)) throw Invalid("InvalidMonitoringHistoryRange");
            Dictionary<string, MonitorTarget> targets = new Dictionary<string, MonitorTarget>(StringComparer.Ordinal);
            foreach (MonitorTarget target in record.Targets)
            {
                if (target == null) throw Invalid("InvalidMonitoringHistoryTarget");
                Token(target.Id, 32, true);
                string host, ignored;
                if (targets.ContainsKey(target.Id) || (target.Method != "icmp" && target.Method != "https") || !AdvancedTools.TryNormalizeHost(target.Host, out host, out ignored) || host != target.Host) throw Invalid("InvalidMonitoringHistoryTarget");
                targets.Add(target.Id, target);
            }
            Dictionary<string, int> sequences = new Dictionary<string, int>(StringComparer.Ordinal);
            Dictionary<string, DateTime> times = new Dictionary<string, DateTime>(StringComparer.Ordinal);
            foreach (MonitorSample sample in record.Samples)
            {
                MonitorTarget target;
                if (sample == null || sample.GetType() != typeof(MonitorSample) || sample.TargetId == null || !targets.TryGetValue(sample.TargetId, out target) || sample.TargetHost != target.Host || sample.Method != (target.Method == "icmp" ? "ICMP" : "HTTPS") || sample.Route != (target.Method == "icmp" ? "Network" : target.UseSystemProxy ? "WindowsProxy" : "Direct")) throw Invalid("InvalidMonitoringHistorySample");
                Time(sample.TimeUtc); int previous; DateTime previousTime;
                if (sample.TimeUtc < record.StartedUtc || sample.TimeUtc > record.EndedUtc || sample.Sequence < 1 || sample.Sequence > record.RequestedCount || (sequences.TryGetValue(sample.TargetId, out previous) && sample.Sequence <= previous) || (times.TryGetValue(sample.TargetId, out previousTime) && sample.TimeUtc < previousTime) || sample.Sent != sample.Sequence || sample.Received < 0 || sample.Received > sample.Sent || Double.IsNaN(sample.LossPercent) || Double.IsInfinity(sample.LossPercent) || Math.Abs(sample.LossPercent - 100.0 * (sample.Sent - sample.Received) / sample.Sent) > 0.000001 || (sample.RttMs.HasValue && (sample.RttMs.Value < 0 || sample.RttMs.Value > 86400000))) throw Invalid("InvalidMonitoringHistorySample");
                sequences[sample.TargetId] = sample.Sequence; times[sample.TargetId] = sample.TimeUtc;
                Metric(sample.Min); Metric(sample.Avg); Metric(sample.Max); Metric(sample.JitterMs);
                if (sample.Min.HasValue && sample.Max.HasValue && sample.Min > sample.Max || sample.Avg.HasValue && sample.Min.HasValue && sample.Avg < sample.Min || sample.Avg.HasValue && sample.Max.HasValue && sample.Avg > sample.Max) throw Invalid("InvalidMonitoringHistoryMetric");
                Token(sample.Status, 96, true); Token(sample.ErrorCode, 96, false); Text(sample.ResolvedAddress, 128, false);
                IPAddress address; if (!String.IsNullOrEmpty(sample.ResolvedAddress) && !IPAddress.TryParse(sample.ResolvedAddress, out address)) throw Invalid("InvalidMonitoringHistoryAddress");
                if (sample.HttpStatusCode.HasValue && (sample.HttpStatusCode.Value < 100 || sample.HttpStatusCode.Value > 599) || target.Method == "icmp" && (sample.HttpStatusCode.HasValue || sample.ResponseReceived || sample.HttpSuccess) || target.Method == "https" && (sample.Success != sample.ResponseReceived || sample.HttpStatusCode.HasValue != sample.ResponseReceived || sample.HttpSuccess != (sample.HttpStatusCode.HasValue && sample.HttpStatusCode.Value >= 200 && sample.HttpStatusCode.Value <= 299)) || sample.Success != sample.RttMs.HasValue) throw Invalid("InvalidMonitoringHistoryResponse");
            }
            for (int i = 0; i < record.Events.Length; i++)
            {
                NetworkChangeEvent item = record.Events[i];
                if (item == null || item.Sequence != record.TotalEvents - record.Events.Length + i + 1 || !new[] { "MonitoringStarted", "MonitoringCompleted", "MonitoringStopped", "ObservationUnavailable", "SettingChanged", "TargetRecovered", "TargetNoResponse" }.Contains(item.Kind)) throw Invalid("InvalidMonitoringHistoryEvent");
                Time(item.TimeUtc); if (item.TimeUtc < record.StartedUtc || item.TimeUtc > record.EndedUtc) throw Invalid("InvalidMonitoringHistoryEventTime");
                Text(item.TargetId, 256, false); Text(item.TargetHost, 253, false); Text(item.AdapterName, 256, false); Token(item.ErrorCode, 96, false);
                if (item.Kind == "TargetRecovered" || item.Kind == "TargetNoResponse")
                {
                    MonitorTarget target;
                    if (item.TargetId == null || !targets.TryGetValue(item.TargetId, out target) || item.TargetHost != target.Host || !String.IsNullOrEmpty(item.Setting) || !String.IsNullOrEmpty(item.Before) || !String.IsNullOrEmpty(item.After)) throw Invalid("InvalidMonitoringHistoryEventTarget");
                }
                else if (item.Kind == "SettingChanged")
                {
                    if (!new[] { "ProxyEnabled", "ProxyServer", "ProxyPac", "AdapterStatus", "Addresses", "Dns", "Gateways" }.Contains(item.Setting) || !String.IsNullOrEmpty(item.TargetHost)) throw Invalid("InvalidMonitoringHistorySetting");
                    string before = SettingValue(item.Setting, item.Before), after = SettingValue(item.Setting, item.After);
                    if (saving) { item.Before = before; item.After = after; }
                    else if (before != (item.Before ?? "") || after != (item.After ?? "")) throw Invalid("UnsafeMonitoringHistorySetting");
                }
                else if (!String.IsNullOrEmpty(item.TargetId) || !String.IsNullOrEmpty(item.TargetHost) || !String.IsNullOrEmpty(item.AdapterName) || !String.IsNullOrEmpty(item.Setting) || !String.IsNullOrEmpty(item.Before) || !String.IsNullOrEmpty(item.After)) throw Invalid("InvalidMonitoringHistoryLifecycle");
            }
        }

        private static byte[] Serialize(MonitoringHistoryRecord record)
        {
            using (MemoryStream stream = new MemoryStream()) { Serializer().Serialize(stream, new MonitoringHistoryDocument { Version = 2, Record = record }); if (stream.Length > MaximumRecordBytes) throw Invalid("MonitoringHistoryRecordTooLarge"); return stream.ToArray(); }
        }
        private static MonitoringHistoryRecord Deserialize(byte[] plain)
        {
            if (plain.Length == 0 || plain.Length > MaximumRecordBytes) throw Invalid("MonitoringHistoryRecordTooLarge");
            using (MemoryStream stream = new MemoryStream(plain, false))
            using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumRecordBytes }))
            {
                MonitoringHistoryDocument document = (MonitoringHistoryDocument)Serializer().Deserialize(reader);
                if (document == null || (document.Version != 1 && document.Version != 2)) throw Invalid("MonitoringHistorySchema");
                if (document.Record != null && document.Version == 1 && (document.Record.WifiCollectionEnabled || document.Record.WifiTotalSamples != 0 || document.Record.WifiSamples == null || document.Record.WifiSamples.Length != 0)) throw Invalid("MonitoringHistorySchema");
                return document.Record;
            }
        }
        private MonitoringHistoryRecord ReadFile(string path, string id)
        {
            try
            {
                CheckPath(path);
                byte[] encrypted;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length == 0 || stream.Length > MaximumRecordBytes) throw Invalid("MonitoringHistoryRecordTooLarge");
                    encrypted = new byte[(int)stream.Length]; int offset = 0;
                    while (offset < encrypted.Length) { int count = stream.Read(encrypted, offset, encrypted.Length - offset); if (count == 0) throw Invalid("MonitoringHistoryUnreadable"); offset += count; }
                }
                MonitoringHistoryRecord record = Deserialize(ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
                Validate(record, false); if (record.Id != id) throw Invalid("MonitoringHistoryIdentityMismatch");
                return record;
            }
            catch (Exception ex) { if (!StoreFailure(ex)) throw; throw Invalid(ex.Message == "MonitoringHistoryUnsafePath" ? ex.Message : "MonitoringHistoryUnreadable"); }
        }
        private sealed class Stored
        {
            public string Path, Id, Error;
            public long Bytes;
            public MonitoringHistoryRecord Record;
        }
        private List<Stored> Scan()
        {
            CheckPath(root); List<Stored> files = new List<Stored>();
            if (!Directory.Exists(root)) return files;
            foreach (string path in Directory.GetFiles(root, "*.nrh", SearchOption.TopDirectoryOnly))
            {
                Stored item = new Stored { Path = path }; files.Add(item);
                try
                {
                    item.Id = CanonicalId(Path.GetFileNameWithoutExtension(path)); CheckPath(path);
                    item.Bytes = new FileInfo(path).Length; item.Record = ReadFile(path, item.Id);
                }
                catch (Exception ex) { if (!StoreFailure(ex)) throw; item.Error = ex.Message == "MonitoringHistoryUnsafePath" ? ex.Message : "MonitoringHistoryUnreadable"; if (item.Bytes == 0) { try { CheckPath(path); item.Bytes = new FileInfo(path).Length; } catch (Exception sizeError) { if (!StoreFailure(sizeError)) throw; item.Bytes = MaximumTotalBytes; } } }
            }
            return files;
        }
        private static string DisplayState(string state) { return state == "Running" ? "Interrupted" : state; }
        public MonitoringHistoryInfo[] List()
        {
            return Locked(delegate
            {
                return Scan().Select(delegate(Stored file)
                {
                    MonitoringHistoryRecord r = file.Record;
                    return r == null ? new MonitoringHistoryInfo { Id = file.Id, ErrorCode = file.Error } : new MonitoringHistoryInfo { Id = r.Id, StartedUtc = r.StartedUtc, EndedUtc = r.EndedUtc, State = DisplayState(r.State), IntervalMs = r.IntervalMs, RequestedCount = r.RequestedCount, TargetCount = r.Targets.Length, SampleCount = r.Samples.Length, EventCount = r.Events.Length, TotalEvents = r.TotalEvents, WifiCollectionEnabled = r.WifiCollectionEnabled, WifiSampleCount = r.WifiSamples.Length, WifiTotalSamples = r.WifiTotalSamples };
                }).OrderByDescending(delegate(MonitoringHistoryInfo info) { return info.StartedUtc; }).ToArray();
            });
        }
        public MonitoringHistoryRecord Load(string id)
        {
            id = CanonicalId(id);
            return Locked(delegate { string path = FilePath(id); CheckPath(path); if (!File.Exists(path)) return null; MonitoringHistoryRecord record = ReadFile(path, id); record.State = DisplayState(record.State); return record; });
        }
        public MonitoringHistoryRecord Save(MonitoringHistoryRecord record)
        {
            // Validate first, so arbitrarily large caller-owned fields never enter the serializer.
            // The tracker already sanitizes configuration events; reject raw credentials here too.
            Validate(record, false);
            MonitoringHistoryRecord copy = Deserialize(Serialize(record)); Validate(copy, true);
            byte[] plain = Serialize(copy), encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            if (encrypted.Length > MaximumRecordBytes) throw Invalid("MonitoringHistoryRecordTooLarge");
            return Locked(delegate
            {
                string path = FilePath(copy.Id), temporary = null;
                CheckPath(path); List<Stored> files = Scan();
                Stored existing = files.FirstOrDefault(delegate(Stored item) { return item.Id == copy.Id; });
                if (existing != null && existing.Record == null) throw Invalid("MonitoringHistoryUnreadable");
                if (existing != null && existing.Record.State != "Running" && copy.State == "Running") throw Invalid("MonitoringHistoryAlreadyFinished");
                long bytes = files.Sum(delegate(Stored item) { return item.Bytes; }) - (existing == null ? 0 : existing.Bytes) + encrypted.Length;
                int count = files.Count + (existing == null ? 1 : 0);
                List<Stored> remove = new List<Stored>();
                foreach (Stored oldest in files.Where(delegate(Stored item) { return item.Record != null && item.Id != copy.Id; }).OrderBy(delegate(Stored item) { return item.Record.StartedUtc; }).ThenBy(delegate(Stored item) { return item.Id; }, StringComparer.Ordinal))
                {
                    if (count <= MaximumRecords && bytes <= MaximumTotalBytes) break;
                    remove.Add(oldest); count--; bytes -= oldest.Bytes;
                }
                if (count > MaximumRecords || bytes > MaximumTotalBytes) throw Invalid("MonitoringHistoryCapacityBlocked");
                try
                {
                    CheckPath(root); Directory.CreateDirectory(root); CheckPath(path);
                    temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
                    // Only files read and validated under this lock are eligible for retention cleanup.
                    foreach (Stored oldest in remove) { CheckPath(oldest.Path); ReadFile(oldest.Path, oldest.Id); File.Delete(oldest.Path); }
                    CheckPath(path);
                    if (File.Exists(path)) { ReadFile(path, copy.Id); File.Replace(temporary, path, null); }
                    else File.Move(temporary, path);
                    return copy;
                }
                finally { if (temporary != null) { try { CheckPath(temporary); if (File.Exists(temporary)) File.Delete(temporary); } catch (Exception ex) { if (!StoreFailure(ex)) throw; } } }
            });
        }
        public bool Delete(string id)
        {
            id = CanonicalId(id);
            return Locked(delegate { string path = FilePath(id); CheckPath(path); if (!File.Exists(path)) return false; ReadFile(path, id); File.Delete(path); return true; });
        }
        /// <summary>Removes verified records only. Unreadable files remain visible through List().</summary>
        public int Clear()
        {
            return Locked(delegate { int deleted = 0; foreach (Stored file in Scan()) { if (file.Record == null) continue; CheckPath(file.Path); ReadFile(file.Path, file.Id); File.Delete(file.Path); deleted++; } return deleted; });
        }
    }
}
