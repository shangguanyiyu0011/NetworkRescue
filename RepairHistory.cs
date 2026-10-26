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
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace NetworkRescue
{
    public sealed class ConfigurationValue { public string Key, Value; public bool Readable; }
    public sealed class ConfigurationSnapshot { public List<ConfigurationValue> Values = new List<ConfigurationValue>(); }
    public sealed class ConfigurationChange { public string Key, Before, After; }
    public sealed class RepairStepRecord { public string ActionId, State, Code; public bool MayHaveChanged; }
    public sealed class RepairHistoryEntry
    {
        public string Id, Operation, BackupPath;
        public DateTime StartedUtc, EndedUtc;
        public bool Success;
        public string Outcome;
        public bool RestartRequired;
        public List<RepairStepRecord> Steps = new List<RepairStepRecord>();
        public bool? BeforeOnline, AfterOnline;
        public List<string> ActionTitles = new List<string>();
        public List<ConfigurationChange> ConfigurationChanges = new List<ConfigurationChange>();
    }
    public sealed class HistoryReadResult { public List<RepairHistoryEntry> Entries = new List<RepairHistoryEntry>(); public string ErrorCode; }
    public sealed class HistoryWriteResult { public bool Success; public string ErrorCode; }
    public sealed class RepairHistoryDocument { public int Version = 1; public List<RepairHistoryEntry> Entries = new List<RepairHistoryEntry>(); }

    public sealed class RepairHistoryStore
    {
        private const int MaxBytes = 4 * 1024 * 1024;
        private readonly string directory, path;
        private readonly object sync = new object();
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkRescue.History.v1");
        public RepairHistoryStore() : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkRescue", "History")) { }
        public RepairHistoryStore(string directory)
        {
            if (String.IsNullOrWhiteSpace(directory) || !Path.IsPathRooted(directory)) throw new ArgumentException("InvalidHistoryDirectory");
            this.directory = Path.GetFullPath(directory);
            path = Path.Combine(this.directory, "history.nrh");
        }

        public static ConfigurationSnapshot CaptureConfiguration()
        {
            ConfigurationSnapshot snapshot = new ConfigurationSnapshot();
            try
            {
                using (RegistryKey proxy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
                {
                    if (proxy == null) throw new IOException();
                    object enabled = proxy.GetValue("ProxyEnable", 0);
                    bool enabledReadable = enabled is int && ((int)enabled == 0 || (int)enabled == 1);
                    Add(snapshot, "Proxy.Enable", enabledReadable ? Convert.ToString(enabled, CultureInfo.InvariantCulture) : null, enabledReadable);
                    object server = proxy.GetValue("ProxyServer", ""), pac = proxy.GetValue("AutoConfigURL", "");
                    Add(snapshot, "Proxy.Server", server is string ? ReportRedactor.StripSecrets((string)server) : null, server is string);
                    Add(snapshot, "Proxy.Pac", pac is string ? ReportRedactor.StripSecrets((string)pac) : null, pac is string);
                }
            }
            catch (Exception ex) { if (!ReadFailure(ex)) throw; Add(snapshot, "Proxy.Enable", null, false); Add(snapshot, "Proxy.Server", null, false); Add(snapshot, "Proxy.Pac", null, false); }
            try
            {
                NetworkInterface[] adapters = NetworkInterface.GetAllNetworkInterfaces().Where(delegate(NetworkInterface n) { return n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback; }).OrderBy(delegate(NetworkInterface n) { return n.Id; }, StringComparer.OrdinalIgnoreCase).ToArray();
                Add(snapshot, "Adapters.Active", String.Join(",", adapters.Select(delegate(NetworkInterface n) { return n.Id; })), true);
                foreach (NetworkInterface adapter in adapters)
                {
                    string prefix = "Adapter." + adapter.Id + ".";
                    try
                    {
                        IPInterfaceProperties props = adapter.GetIPProperties();
                        Add(snapshot, prefix + "Addresses", String.Join(",", props.UnicastAddresses.Select(delegate(UnicastIPAddressInformation a) { return a.Address.ToString(); }).OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal)), true);
                        Add(snapshot, prefix + "DnsAddresses", String.Join(",", props.DnsAddresses.Select(delegate(IPAddress a) { return a.ToString(); })), true);
                    }
                    catch (Exception ex) { if (!ReadFailure(ex)) throw; Add(snapshot, prefix + "Addresses", null, false); Add(snapshot, prefix + "DnsAddresses", null, false); }
                    ReadDnsMode(snapshot, prefix, adapter.Id, "Tcpip", "V4");
                    ReadDnsMode(snapshot, prefix, adapter.Id, "Tcpip6", "V6");
                }
            }
            catch (Exception ex) { if (!ReadFailure(ex)) throw; Add(snapshot, "Adapters.Active", null, false); }
            return snapshot;
        }

        private static bool ReadFailure(Exception ex) { return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException || ex is NetworkInformationException || ex is InvalidOperationException || ex is ArgumentException || ex is RegexMatchTimeoutException; }
        private static void Add(ConfigurationSnapshot snapshot, string key, string value, bool readable) { snapshot.Values.Add(new ConfigurationValue { Key = key, Value = value, Readable = readable }); }
        private static void ReadDnsMode(ConfigurationSnapshot snapshot, string prefix, string id, string protocol, string family)
        {
            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + protocol + @"\Parameters\Interfaces\" + id))
                {
                    if (key == null) { Add(snapshot, prefix + family + "DnsMode", null, false); return; }
                    object raw = key.GetValue("NameServer", "");
                    string configured = raw is string[] ? String.Join(",", (string[])raw) : raw as string;
                    if (configured == null) { Add(snapshot, prefix + family + "DnsMode", null, false); return; }
                    foreach (string item in configured.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        IPAddress address;
                        if (!IPAddress.TryParse(item, out address) || (family == "V4" ? address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork : address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)) { Add(snapshot, prefix + family + "DnsMode", null, false); Add(snapshot, prefix + family + "ConfiguredDns", null, false); return; }
                    }
                    Add(snapshot, prefix + family + "DnsMode", String.IsNullOrWhiteSpace(configured) ? "Automatic" : "Manual", true);
                    Add(snapshot, prefix + family + "ConfiguredDns", configured.Trim(), true);
                }
            }
            catch (Exception ex) { if (!ReadFailure(ex)) throw; Add(snapshot, prefix + family + "DnsMode", null, false); }
        }

        public static List<ConfigurationChange> Compare(ConfigurationSnapshot before, ConfigurationSnapshot after)
        {
            List<ConfigurationChange> changes = new List<ConfigurationChange>();
            if (before == null || after == null || before.Values == null || after.Values == null) return changes;
            foreach (ConfigurationValue old in before.Values)
            {
                if (old == null || !old.Readable || String.IsNullOrEmpty(old.Key)) continue;
                ConfigurationValue current = after.Values.FirstOrDefault(delegate(ConfigurationValue v) { return v != null && v.Key == old.Key; });
                if (current != null && current.Readable && !String.Equals(old.Value, current.Value, StringComparison.Ordinal)) changes.Add(new ConfigurationChange { Key = old.Key, Before = old.Value ?? "", After = current.Value ?? "" });
            }
            return changes;
        }

        public HistoryReadResult ReadAll() { lock (sync) return ReadCore(); }
        private HistoryReadResult ReadCore()
        {
            HistoryReadResult result = new HistoryReadResult();
            try
            {
                CheckPath(directory);
                if (!File.Exists(path)) return result;
                CheckPath(path);
                if (new FileInfo(path).Length > MaxBytes + 65536) throw new InvalidDataException();
                byte[] encrypted;
                using (FileStream file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (file.Length > MaxBytes + 65536) throw new InvalidDataException();
                    encrypted = new byte[(int)file.Length];
                    int offset = 0;
                    while (offset < encrypted.Length) { int count = file.Read(encrypted, offset, encrypted.Length - offset); if (count == 0) throw new InvalidDataException(); offset += count; }
                }
                byte[] plain = ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser);
                if (plain.Length > MaxBytes) throw new InvalidDataException();
                using (MemoryStream stream = new MemoryStream(plain, false))
                using (XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxBytes }))
                {
                    XmlSerializer serializer = Serializer();
                    RepairHistoryDocument document = (RepairHistoryDocument)serializer.Deserialize(reader);
                    Validate(document);
                    result.Entries = document.Entries;
                }
            }
            catch (Exception ex) { if (!StoreFailure(ex)) throw; result.ErrorCode = ex is UnauthorizedAccessException || ex is SecurityException ? "HistoryAccessDenied" : "HistoryUnreadable"; }
            return result;
        }

        public HistoryWriteResult Record(RepairHistoryEntry entry)
        {
            lock (sync)
            {
                string temporary = null;
                try
                {
                    HistoryReadResult existing = ReadCore();
                    if (existing.ErrorCode != null) return new HistoryWriteResult { ErrorCode = existing.ErrorCode };
                    if (entry == null) throw new InvalidDataException();
                    if (String.IsNullOrEmpty(entry.Id)) entry.Id = Guid.NewGuid().ToString("N");
                    ValidateEntry(entry);
                    if (existing.Entries.Any(delegate(RepairHistoryEntry e) { return e.Id == entry.Id; })) throw new InvalidDataException();
                    existing.Entries.Insert(0, entry);
                    if (existing.Entries.Count > 100) existing.Entries.RemoveRange(100, existing.Entries.Count - 100);
                    byte[] plain;
                    using (MemoryStream stream = new MemoryStream()) { Serializer().Serialize(stream, new RepairHistoryDocument { Entries = existing.Entries }); plain = stream.ToArray(); }
                    if (plain.Length > MaxBytes) throw new InvalidDataException();
                    byte[] encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
                    CheckPath(directory);
                    Directory.CreateDirectory(directory);
                    CheckPath(directory);
                    temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
                    using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(encrypted, 0, encrypted.Length); stream.Flush(true); }
                    if (File.Exists(path)) { CheckPath(path); File.Replace(temporary, path, null); }
                    else File.Move(temporary, path);
                    return new HistoryWriteResult { Success = true };
                }
                catch (Exception ex) { if (!StoreFailure(ex)) throw; return new HistoryWriteResult { ErrorCode = ex is InvalidDataException || ex is ArgumentException ? "InvalidHistoryEntry" : "HistoryWriteFailed" }; }
                finally { if (temporary != null) { try { if (File.Exists(temporary)) File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { } } }
            }
        }

        public bool TryGetLatestBackupPath(string backupRoot, out string backupPath, out string errorCode)
        {
            backupPath = null;
            HistoryReadResult history = ReadAll();
            errorCode = history.ErrorCode;
            if (errorCode != null) return false;
            RepairHistoryEntry latest = history.Entries.Where(delegate(RepairHistoryEntry e) { return !String.IsNullOrWhiteSpace(e.BackupPath); }).OrderByDescending(delegate(RepairHistoryEntry e) { return e.EndedUtc; }).FirstOrDefault();
            if (latest == null) { errorCode = "NoHistoryBackup"; return false; }
            try
            {
                if (!Path.IsPathRooted(backupRoot) || !Path.IsPathRooted(latest.BackupPath)) throw new InvalidDataException();
                string root = Path.GetFullPath(backupRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                string full = Path.GetFullPath(latest.BackupPath);
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetExtension(full), ".nrb", StringComparison.OrdinalIgnoreCase) || !String.Equals(Path.GetDirectoryName(full).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                CheckPath(root); CheckPath(full);
                using (FileStream stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read)) { if (stream.Length == 0 || stream.Length > 4 * 1024 * 1024) throw new InvalidDataException(); }
                backupPath = full;
                return true;
            }
            catch (Exception ex) { if (!StoreFailure(ex)) throw; errorCode = "HistoryBackupUnavailable"; return false; }
        }

        private static XmlSerializer Serializer()
        {
            XmlSerializer serializer = new XmlSerializer(typeof(RepairHistoryDocument));
            serializer.UnknownNode += delegate { throw new InvalidDataException(); };
            serializer.UnknownAttribute += delegate { throw new InvalidDataException(); };
            return serializer;
        }
        private static bool StoreFailure(Exception ex) { return ReadFailure(ex) || ex is InvalidDataException || ex is CryptographicException || ex is XmlException || ex is NotSupportedException; }
        private static void CheckPath(string value)
        {
            string current = Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar);
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        }
        private static void Text(string text, int maximum, bool required) { if ((required && String.IsNullOrEmpty(text)) || (text != null && text.Length > maximum) || (text != null && text.IndexOf('\0') >= 0)) throw new InvalidDataException(); }
        private static void Validate(RepairHistoryDocument document)
        {
            if (document == null || document.Version != 1 || document.Entries == null || document.Entries.Count > 100) throw new InvalidDataException();
            HashSet<string> ids = new HashSet<string>();
            foreach (RepairHistoryEntry entry in document.Entries) { ValidateEntry(entry); if (!ids.Add(entry.Id)) throw new InvalidDataException(); }
        }
        private static void ValidateEntry(RepairHistoryEntry entry)
        {
            if (entry == null || (entry.Operation != "repair" && entry.Operation != "restore") || entry.StartedUtc == default(DateTime) || entry.EndedUtc < entry.StartedUtc || entry.ActionTitles == null || entry.ActionTitles.Count > 100 || entry.ConfigurationChanges == null || entry.ConfigurationChanges.Count > 256) throw new InvalidDataException();
            Guid id; if (!Guid.TryParse(entry.Id, out id)) throw new InvalidDataException();
            Text(entry.Id, 64, true); Text(entry.BackupPath, 1024, false);
            Text(entry.Outcome, 32, false);
            if (!String.IsNullOrEmpty(entry.Outcome) && !new[] { "NotStarted", "Completed", "RestartRequired", "Partial", "Rejected", "Failed", "Cancelled", "NoChange", "NeedsReview" }.Contains(entry.Outcome)) throw new InvalidDataException();
            if (entry.Steps == null || entry.Steps.Count > 258) throw new InvalidDataException();
            foreach (RepairStepRecord step in entry.Steps)
            {
                if (step == null || !new[] { "Prepared", "Writing", "Completed", "RestartRequired", "Rejected", "Failed", "Cancelled", "Unconfirmed", "NotRestorable" }.Contains(step.State)) throw new InvalidDataException();
                Text(step.ActionId, 32, true); Text(step.Code, 96, false);
            }
            foreach (string title in entry.ActionTitles) Text(title, 512, true);
            foreach (ConfigurationChange change in entry.ConfigurationChanges) { if (change == null) throw new InvalidDataException(); Text(change.Key, 512, true); Text(change.Before, 4096, false); Text(change.After, 4096, false); }
        }
    }

    public static class ReportRedactor
    {
        private static string Replace(string text, string pattern, string replacement) { return Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
        internal static string StripSecrets(string input)
        {
            string text = input ?? "";
            text = Replace(text, @"(?<=(?:https?|socks[45]?)://)[^\s/@]+@", "[credentials]@");
            text = Replace(text, @"(?<![^\s;=:/@])[^\s;=:/@]+:[^\s;@]+@", "[credentials]@");
            text = Replace(text, @"(?<=[^\s])\?[^\s;\""<>]*", "?[query]");
            return Replace(text, @"#[^\s;\""<>]*", "#[fragment]");
        }
        public static string Redact(string input)
        {
            try { return RedactCore(input); }
            catch (RegexMatchTimeoutException) { return "[redaction-unavailable]"; }
        }
        private static string RedactCore(string input)
        {
            // SSIDs may contain delimiters and URL-like text; redact the field before generic substitutions.
            string text = Regex.Replace(input ?? "", @"(?im)^(?<prefix>[^\r\n]*?)(?<label>\bSSID[ \t]*[:=])[ \t]*(?<value>[^\r\n]*)", delegate(Match match)
            {
                string prefix = match.Groups["prefix"].Value;
                string metrics = "";
                if (prefix.EndsWith(" | ", StringComparison.Ordinal))
                {
                    Match suffix = Regex.Match(match.Groups["value"].Value, @"[ \t]+\|[ \t]*[^|\r\n:：]+[:：][ \t]*[0-9]+%[ \t]+\|[ \t]*Rx=[0-9]+(?:\.[0-9]+)?[ \t]+Mbps[ \t]+\|[ \t]*Tx=[0-9]+(?:\.[0-9]+)?[ \t]+Mbps[ \t]*$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                    if (suffix.Success) metrics = suffix.Value;
                }
                return prefix + match.Groups["label"].Value + "[identifier]" + metrics;
            }, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            text = StripSecrets(text);
            text = Replace(text, @"\bS-1-\d+(?:-\d+){1,15}\b", "[sid]");
            text = Replace(text, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[id]");
            text = Replace(text, @"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b", "[mac]");
            text = Regex.Replace(text, @"(?<![\w:])\[?[0-9a-f:.]*:[0-9a-f:.]+(?:%[0-9a-z_.-]+)?\]?(?![\w:])", delegate(Match match)
            {
                IPAddress ip; string candidate = match.Value.Trim('[', ']');
                int zone = candidate.IndexOf('%'); if (zone >= 0) candidate = candidate.Substring(0, zone);
                return IPAddress.TryParse(candidate, out ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? "[ipv6]" : match.Value;
            }, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            text = Replace(text, @"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?![\w.])", "[ipv4]");
            text = Replace(text, @"[a-z]:[\\/]+(?:Users|Documents and Settings)[\\/]+[^\\/\s\""<>]+", @"[user-profile]");
            text = Replace(text, @"(?<!:)//(?:home|Users)/[^/\s\""<>]+|/(?:home|Users)/[^/\s\""<>]+", "[user-profile]");
            text = Replace(text, @"\\\\[^\s\\]+\\[^\s\""<>]+", "[network-path]");
            text = Replace(text, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,63}\b", "[email]");
            text = Replace(text, @"\b(?:https?|socks[45]?)://[^\s\""<>]+", "[url]");
            text = Replace(text, @"(?<![\p{L}\p{N}-])(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+[\p{L}][\p{L}\p{N}-]{1,62}(?![\p{L}\p{N}-])", "[host]");
            text = Regex.Replace(text, @"(?im)\b(BSSID|HostName|ComputerName|UserName|UserDomainName)[ \t]*[:=][ \t]*[^\r\n,;]+", "$1=[identifier]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            text = Regex.Replace(text, @"(?m)^[^\r\n|]{1,256}(?=\s+\|[^\r\n|]+\|\s*IPv[46]\s*:)", "[adapter]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            return text;
        }
    }
}
