using System;
using System.Collections.Generic;
using System.Globalization;
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
    public sealed class HealthProfile
    {
        public string Id, Name;
        public DateTime CreatedUtc;
        public NetworkChangeSnapshot Snapshot;
    }

    public sealed class MonitorPlan
    {
        public string Id, Name;
        public DateTime CreatedUtc;
        public MonitorTarget[] Targets = new MonitorTarget[0];
        public int IntervalMs, Count;
    }

    public sealed class HealthComparison
    {
        public DateTime ComparedUtc;
        public bool Complete;
        public string ErrorCode;
        public NetworkChangeEvent[] Changes = new NetworkChangeEvent[0];
    }

    public sealed class NetworkWorkspaceDocument
    {
        public int Version;
        public string Kind;
        public HealthProfile[] HealthProfiles = new HealthProfile[0];
        public MonitorPlan[] MonitorPlans = new MonitorPlan[0];
    }

    public sealed class NetworkWorkspaceStore
    {
        private const int MaxBytes = 1024 * 1024, MaxItems = 20;
        private readonly string root, mutexName;
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkRescue.Workspace.v1");

        public NetworkWorkspaceStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkRescue", "Workspace")) { }
        public NetworkWorkspaceStore(string root)
        {
            if (String.IsNullOrWhiteSpace(root) || !Path.IsPathRooted(root)) throw Invalid("InvalidWorkspaceDirectory");
            this.root = Path.GetFullPath(root);
            using (SHA256 hash = SHA256.Create()) mutexName = @"Local\NetworkRescue.Workspace." + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(this.root.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()))).Replace("-", "");
        }

        private static ArgumentException Invalid(string code) { return new ArgumentException(code); }
        private static bool StoreFailure(Exception ex)
        {
            return ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is SecurityException || ex is CryptographicException || ex is XmlException || ex is InvalidOperationException || ex is ArgumentException || ex is NotSupportedException;
        }

        private T Locked<T>(Func<T> action)
        {
            try
            {
                using (Mutex mutex = new Mutex(false, mutexName))
                {
                    bool acquired = false;
                    try
                    {
                        try { acquired = mutex.WaitOne(5000); } catch (AbandonedMutexException) { acquired = true; }
                        if (!acquired) throw new IOException("WorkspaceBusy");
                        return action();
                    }
                    finally { if (acquired) mutex.ReleaseMutex(); }
                }
            }
            catch (UnauthorizedAccessException) { throw new IOException("WorkspaceAccessDenied"); }
            catch (SecurityException) { throw new IOException("WorkspaceAccessDenied"); }
        }

        private static void CheckPath(string path)
        {
            string current = Path.GetFullPath(path);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("WorkspaceUnsafePath");
        }

        private static XmlSerializer Serializer()
        {
            XmlSerializer serializer = new XmlSerializer(typeof(NetworkWorkspaceDocument));
            serializer.UnknownNode += delegate { throw new InvalidDataException(); };
            serializer.UnknownAttribute += delegate { throw new InvalidDataException(); };
            return serializer;
        }

        private string FilePath(string kind) { return Path.Combine(root, kind == "health" ? "health-profiles.nrw" : "monitor-plans.nrw"); }

        private NetworkWorkspaceDocument Read(string kind)
        {
            try
            {
                string path = FilePath(kind);
                CheckPath(root); CheckPath(path);
                if (!File.Exists(path)) return new NetworkWorkspaceDocument { Version = 1, Kind = kind };
                byte[] encrypted;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length == 0 || file.Length > MaxBytes) throw new InvalidDataException();
                    encrypted = new byte[(int)file.Length];
                    int offset = 0;
                    while (offset < encrypted.Length) { int count = file.Read(encrypted, offset, encrypted.Length - offset); if (count == 0) throw new InvalidDataException(); offset += count; }
                }
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length > MaxBytes) throw new InvalidDataException();
                using (MemoryStream stream = new MemoryStream(plain, false))
                using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes }))
                {
                    NetworkWorkspaceDocument document = (NetworkWorkspaceDocument)Serializer().Deserialize(reader);
                    Validate(document, kind);
                    return document;
                }
            }
            catch (Exception ex) { if (!StoreFailure(ex)) throw; throw new InvalidDataException("WorkspaceUnreadable"); }
        }

        private void Write(NetworkWorkspaceDocument document)
        {
            string temporary = null;
            try
            {
                Validate(document, document.Kind);
                byte[] plain;
                using (MemoryStream stream = new MemoryStream()) { Serializer().Serialize(stream, document); plain = stream.ToArray(); }
                if (plain.Length > MaxBytes) throw new InvalidDataException();
                byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                if (encrypted.Length > MaxBytes) throw new InvalidDataException();
                string path = FilePath(document.Kind);
                CheckPath(root); CheckPath(path); Directory.CreateDirectory(root); CheckPath(root); CheckPath(path);
                temporary = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
                if (File.Exists(path)) { CheckPath(path); File.Replace(temporary, path, null); }
                else File.Move(temporary, path);
            }
            catch (Exception ex) { if (!StoreFailure(ex)) throw; throw new IOException("WorkspaceWriteFailed"); }
            finally
            {
                if (temporary != null)
                {
                    try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static string Text(string value, int maximum, bool required)
        {
            if ((required && String.IsNullOrWhiteSpace(value)) || (value != null && (value.Length > maximum || value.Any(Char.IsControl)))) throw Invalid("InvalidWorkspaceValue");
            return value ?? "";
        }

        private static string Name(string value)
        {
            string name = (value ?? "").Trim().Normalize(NormalizationForm.FormC);
            Text(name, 64, true);
            for (int i = 0; i < name.Length; i++)
            {
                UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(name, i);
                if (category == UnicodeCategory.Format || category == UnicodeCategory.Surrogate || category == UnicodeCategory.OtherNotAssigned || category == UnicodeCategory.LineSeparator || category == UnicodeCategory.ParagraphSeparator) throw Invalid("InvalidWorkspaceName");
                if (Char.IsHighSurrogate(name[i])) i++;
            }
            return name;
        }

        private static void Id(string id)
        {
            Guid ignored;
            if (id == null || id.Length != 32 || !Guid.TryParseExact(id, "N", out ignored)) throw Invalid("InvalidWorkspaceId");
        }

        private static DateTime Utc(DateTime time)
        {
            if (time == default(DateTime)) throw Invalid("InvalidWorkspaceTime");
            return time.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : time.ToUniversalTime();
        }

        private static NetworkChangeSnapshot Snapshot(NetworkChangeSnapshot value)
        {
            if (value == null || !value.Complete || value.Entries == null || value.Entries.Length > 512) throw Invalid("InvalidHealthSnapshot");
            NetworkChangeSnapshot copy = new NetworkChangeSnapshot { Complete = true, TimeUtc = Utc(value.TimeUtc) };
            List<NetworkConfigurationEntry> entries = new List<NetworkConfigurationEntry>();
            HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (NetworkConfigurationEntry entry in value.Entries)
            {
                if (entry == null) throw Invalid("InvalidHealthSnapshot");
                string kind = entry.Kind, content = Text(entry.Value, 8192, false), adapterId = Text(entry.AdapterId, 256, false);
                bool proxy = kind == "ProxyEnabled" || kind == "ProxyServer" || kind == "ProxyPac";
                if (!proxy && kind != "AdapterStatus" && kind != "Addresses" && kind != "Dns" && kind != "Gateways") throw Invalid("InvalidHealthSnapshot");
                if (proxy && adapterId != "" || !proxy && String.IsNullOrEmpty(adapterId)) throw Invalid("InvalidHealthSnapshot");
                string key = proxy ? "proxy:" + kind : "adapter:" + adapterId + ":" + kind;
                if (entry.Key != key || !keys.Add(key)) throw Invalid("InvalidHealthSnapshot");
                if (kind == "ProxyEnabled" && content != "Enabled" && content != "Disabled" && content != "Invalid") throw Invalid("InvalidHealthSnapshot");
                if (kind == "ProxyServer" || kind == "ProxyPac") content = content == "Invalid" || content == "Configured" ? content : NetworkChangeReader.SanitizeProxy(content, kind == "ProxyPac");
                if (kind == "AdapterStatus" && !Enum.GetNames(typeof(OperationalStatus)).Contains(content)) throw Invalid("InvalidHealthSnapshot");
                if (kind == "Addresses" || kind == "Dns" || kind == "Gateways")
                {
                    List<string> addresses = new List<string>();
                    foreach (string item in content.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        IPAddress address;
                        if (!IPAddress.TryParse(item, out address)) throw Invalid("InvalidHealthSnapshot");
                        addresses.Add(address.ToString());
                    }
                    content = String.Join(", ", addresses.Distinct(StringComparer.Ordinal).OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal));
                }
                entries.Add(new NetworkConfigurationEntry { Key = key, Kind = kind, AdapterId = adapterId, AdapterName = Text(entry.AdapterName, 256, false), Value = content });
            }
            copy.Entries = entries.OrderBy(delegate(NetworkConfigurationEntry e) { return e.Key; }, StringComparer.Ordinal).ToArray();
            return copy;
        }

        private static MonitorTarget[] Targets(MonitorTarget[] targets, int intervalMs, int count)
        {
            if (targets == null || targets.Length < 1 || targets.Length > 4) throw Invalid("InvalidMonitorTargetCount");
            if (intervalMs < 1000 || intervalMs > 10000 || count < 1 || count > 3600) throw Invalid("InvalidMonitorRange");
            List<MonitorTarget> copy = new List<MonitorTarget>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (MonitorTarget target in targets)
            {
                if (target == null) throw Invalid("InvalidMonitorTarget");
                string id = target.Id, host, ignored;
                if (String.IsNullOrEmpty(id) || id.Length > 32 || id.Any(delegate(char c) { return !(c >= 'a' && c <= 'z') && !(c >= 'A' && c <= 'Z') && !(c >= '0' && c <= '9') && c != '-' && c != '_'; }) || !ids.Add(id)) throw Invalid("InvalidMonitorTargetId");
                if (target.Method != "icmp" && target.Method != "https") throw Invalid("InvalidMonitorMethod");
                if (!AdvancedTools.TryNormalizeHost(target.Host, out host, out ignored)) throw Invalid("InvalidHost");
                copy.Add(new MonitorTarget { Id = id, Host = host, Method = target.Method, UseSystemProxy = target.UseSystemProxy });
            }
            return copy.ToArray();
        }

        private static void Validate(NetworkWorkspaceDocument document, string kind)
        {
            if (document == null || document.Version != 1 || document.Kind != kind || (kind != "health" && kind != "plans") || document.HealthProfiles == null || document.MonitorPlans == null || document.HealthProfiles.Length > MaxItems || document.MonitorPlans.Length > MaxItems || (kind == "health" ? document.MonitorPlans.Length != 0 : document.HealthProfiles.Length != 0)) throw new InvalidDataException();
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase), names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HealthProfile profile in document.HealthProfiles)
            {
                if (profile == null) throw new InvalidDataException();
                Id(profile.Id); if (!ids.Add(profile.Id) || !names.Add(Name(profile.Name)) || profile.Name != Name(profile.Name) || profile.CreatedUtc.Kind != DateTimeKind.Utc) throw new InvalidDataException();
                Utc(profile.CreatedUtc);
                NetworkChangeSnapshot safe = Snapshot(profile.Snapshot);
                if (profile.Snapshot.ErrorCode != null || profile.Snapshot.TimeUtc.Kind != DateTimeKind.Utc || profile.Snapshot.Entries.Any(delegate(NetworkConfigurationEntry entry) { return entry.Fingerprint != null; }) || !SnapshotEqual(profile.Snapshot, safe)) throw new InvalidDataException();
            }
            foreach (MonitorPlan plan in document.MonitorPlans)
            {
                if (plan == null) throw new InvalidDataException();
                Id(plan.Id); if (!ids.Add(plan.Id) || !names.Add(Name(plan.Name)) || plan.Name != Name(plan.Name) || plan.CreatedUtc.Kind != DateTimeKind.Utc) throw new InvalidDataException();
                Utc(plan.CreatedUtc);
                MonitorTarget[] safe = Targets(plan.Targets, plan.IntervalMs, plan.Count);
                for (int i = 0; i < safe.Length; i++) if (safe[i].Host != plan.Targets[i].Host) throw new InvalidDataException();
            }
        }

        private static bool SnapshotEqual(NetworkChangeSnapshot original, NetworkChangeSnapshot safe)
        {
            for (int i = 0; i < safe.Entries.Length; i++)
            {
                NetworkConfigurationEntry a = original.Entries[i], b = safe.Entries[i];
                if (a.Key != b.Key || a.Kind != b.Kind || a.AdapterId != b.AdapterId || a.AdapterName != b.AdapterName || a.Value != b.Value) return false;
            }
            return true;
        }

        public HealthProfile[] LoadHealthProfiles() { return Locked(delegate { return Read("health").HealthProfiles; }); }
        public MonitorPlan[] LoadMonitorPlans() { return Locked(delegate { return Read("plans").MonitorPlans; }); }

        public HealthProfile SaveHealthProfile(string name, NetworkChangeSnapshot snapshot)
        {
            HealthProfile profile = new HealthProfile { Id = Guid.NewGuid().ToString("N"), Name = Name(name), CreatedUtc = DateTime.UtcNow, Snapshot = Snapshot(snapshot) };
            return Locked(delegate
            {
                NetworkWorkspaceDocument document = Read("health");
                if (document.HealthProfiles.Length >= MaxItems) throw Invalid("WorkspaceHealthLimit");
                if (document.HealthProfiles.Any(delegate(HealthProfile p) { return String.Equals(p.Name, profile.Name, StringComparison.OrdinalIgnoreCase); })) throw Invalid("WorkspaceDuplicateName");
                document.HealthProfiles = new[] { profile }.Concat(document.HealthProfiles).ToArray(); Write(document); return profile;
            });
        }

        public MonitorPlan SaveMonitorPlan(string name, MonitorTarget[] targets, int intervalMs, int count)
        {
            MonitorPlan plan = new MonitorPlan { Id = Guid.NewGuid().ToString("N"), Name = Name(name), CreatedUtc = DateTime.UtcNow, Targets = Targets(targets, intervalMs, count), IntervalMs = intervalMs, Count = count };
            return Locked(delegate
            {
                NetworkWorkspaceDocument document = Read("plans");
                if (document.MonitorPlans.Length >= MaxItems) throw Invalid("WorkspacePlanLimit");
                if (document.MonitorPlans.Any(delegate(MonitorPlan p) { return String.Equals(p.Name, plan.Name, StringComparison.OrdinalIgnoreCase); })) throw Invalid("WorkspaceDuplicateName");
                document.MonitorPlans = new[] { plan }.Concat(document.MonitorPlans).ToArray(); Write(document); return plan;
            });
        }

        public bool DeleteHealthProfile(string id) { Id(id); return Delete(id, "health"); }
        public bool DeleteMonitorPlan(string id) { Id(id); return Delete(id, "plans"); }
        private bool Delete(string id, string kind)
        {
            return Locked(delegate
            {
                NetworkWorkspaceDocument document = Read(kind);
                bool found = kind == "health" ? document.HealthProfiles.Any(delegate(HealthProfile p) { return String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase); }) : document.MonitorPlans.Any(delegate(MonitorPlan p) { return String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase); });
                if (!found) return false;
                document.HealthProfiles = document.HealthProfiles.Where(delegate(HealthProfile p) { return !String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase); }).ToArray();
                document.MonitorPlans = document.MonitorPlans.Where(delegate(MonitorPlan p) { return !String.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase); }).ToArray();
                Write(document); return true;
            });
        }

        public static HealthComparison Compare(HealthProfile profile, NetworkChangeSnapshot current)
        {
            HealthComparison result = new HealthComparison { ComparedUtc = DateTime.UtcNow };
            if (current == null || !current.Complete) { result.ErrorCode = current != null && (current.ErrorCode == "ProxyReadUnavailable" || current.ErrorCode == "AdapterReadUnavailable") ? current.ErrorCode : "SnapshotUnavailable"; return result; }
            try
            {
                if (profile == null) throw Invalid("InvalidHealthProfile");
                NetworkChangeSnapshot before = Snapshot(profile.Snapshot), after = Snapshot(current);
                after.TimeUtc = result.ComparedUtc;
                NetworkChangeTracker tracker = new NetworkChangeTracker();
                tracker.ObserveConfiguration(before);
                result.Changes = tracker.ObserveConfiguration(after);
                result.Complete = true;
            }
            catch (ArgumentException) { result.ErrorCode = "InvalidHealthSnapshot"; }
            return result;
        }
    }
}
