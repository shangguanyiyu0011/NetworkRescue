using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace NetworkRescue
{
    internal static class RepairHistoryTests
    {
        private static int assertions;
        private static void Assert(bool condition, string message) { assertions++; if (!condition) throw new Exception("FAIL: " + message); }
        private static RepairHistoryEntry Entry()
        {
            DateTime now = DateTime.UtcNow;
            return new RepairHistoryEntry { StartedUtc = now, EndedUtc = now, Operation = "repair", Success = true, BeforeOnline = false, AfterOnline = true, ActionTitles = new List<string> { "fixture-action" }, ConfigurationChanges = new List<ConfigurationChange> { new ConfigurationChange { Key = "Proxy.Enable", Before = "1", After = "0" } } };
        }
        private static void Redaction()
        {
            string source = "2026-10-25T12:34:56Z Success DnsTimeout 192.168.1.22 2001:db8::123 [fe80::123%12] ::1 ::ffff:192.0.2.1 AA-BB-CC-DD-EE-FF 0011.2233.4455 S-1-5-21-123-456 C:\\Users\\Alice\\history C:/Users/Bob/file /home/carol/file \\\\workstation\\private\\file https://alice:secret@proxy.example:8080/pac?token=secret#private http=alice:secret@proxy:8080 john@example.org 9ea3516b-9d10-48d8-b826-51694eb0c1be";
            string result = ReportRedactor.Redact(source);
            foreach (string secret in new[] { "192.168.1.22", "2001:db8", "fe80::123", "::1", "192.0.2.1", "AA-BB", "0011.2233", "S-1-5-21", "Alice", "Bob", "carol", "workstation", "secret", "proxy.example", "john@", "9ea3516b" }) Assert(result.IndexOf(secret, StringComparison.OrdinalIgnoreCase) < 0, "redacts " + secret);
            Assert(result.Contains("2026-10-25T12:34:56Z") && result.Contains("Success DnsTimeout"), "preserves time and result codes");
            Assert(ReportRedactor.StripSecrets("https://user:pass@proxy.test/pac?key=secret#secret").IndexOf("secret", StringComparison.Ordinal) < 0, "capture removes query and fragments");
            Assert(ReportRedactor.Redact(null) == "", "null export");
            string longToken = new string('x', 200000);
            Assert(ReportRedactor.StripSecrets(longToken) == longToken, "long non-credential tokens do not repeatedly backtrack from every character");
            foreach (string proxy in new[] { "user:pass@proxy.test", "http=user:pass@proxy.test;https=user:pass@proxy.test", "http://user:pass@proxy.test/path", "prefix/user:pass@proxy.test" })
                Assert(!ReportRedactor.StripSecrets(proxy).Contains("user:pass"), "token-boundary credential matching preserves supported proxy forms");
            Assert(!ReportRedactor.Redact("C:\\\\Users\\\\Alice\\\\file ::ffff:192.0.2.1 fe80::22%ethernet SSID=office-network").Contains("Alice"), "JSON escaped profile path");
            Assert(!ReportRedactor.Redact("::ffff:192.0.2.1 fe80::22%ethernet").Contains("ffff") && !ReportRedactor.Redact("fe80::22%ethernet").Contains("ethernet"), "mapped IPv6 and named scope fully masked");
            Assert(ReportRedactor.Redact("SSID=office-network\nHostName: laptop123").IndexOf("office-network", StringComparison.Ordinal) < 0, "labeled identifiers");
            Assert(!ReportRedactor.Redact("custom-adapter | Up | IPv4: 192.0.2.1").Contains("custom-adapter"), "adapter display name in diagnostic layout");
            Assert(ReportRedactor.Redact("\u793a\u4f8b.\u4e2d\u56fd") == "[host]", "international domain");
            SsidRedaction();
        }
        private static void SsidRedaction()
        {
            string metrics = " | \u4fe1\u53f7\uff1a72% | Rx=144.4 Mbps | Tx=72.2 Mbps";
            string next = "DnsTimeout 2026-10-25T12:34:56Z | HTTP 2/3";
            foreach (string ssid in new[] { "private;project", "private,project", "private|project", "\u79c1\u6709;\u9879\u76ee,\u7f51\u7edc|\u7532\u4e59", "private\r\nproject\0suffix", "private\u2028project\u2029suffix", "private | Signal: 1% | Rx=1.0 Mbps | Tx=2.0 Mbps", "https://private.example?secret=a;project#suffix" })
            {
                // Match DiagnosticInsights.Clean: controls are removed before its SSID line is composed.
                string clean = new string(ssid.Take(128).Where(delegate(char c) { return !Char.IsControl(c); }).ToArray());
                string standalone = "fixture-adapter" + Environment.NewLine + "SSID=" + clean + Environment.NewLine + metrics + Environment.NewLine + next;
                string expected = "fixture-adapter" + Environment.NewLine + "SSID=[identifier]" + Environment.NewLine + metrics + Environment.NewLine + next;
                Assert(ReportRedactor.Redact(standalone) == expected, "standalone SSID complete value and next-line technical data: " + ssid);
                string legacy = "fixture-adapter | SSID=" + clean + metrics + Environment.NewLine + next;
                Assert(ReportRedactor.Redact(legacy) == "fixture-adapter | SSID=[identifier]" + metrics + Environment.NewLine + next, "legacy DiagnosticInsights SSID and terminal metrics: " + ssid);
            }
            Assert(ReportRedactor.Redact("SSID=\r\nStatus=Passed; ErrorCode=DnsTimeout") == "SSID=[identifier]\r\nStatus=Passed; ErrorCode=DnsTimeout", "empty SSID does not consume the next line");
            Assert(ReportRedactor.Redact("ssid: private;project,team|secret\nStatus=Passed") == "ssid:[identifier]\nStatus=Passed", "case-insensitive standalone SSID label");
            Assert(ReportRedactor.Redact("SSID=private | Signal: 1% | Rx=1.0 Mbps | Tx=2.0 Mbps") == "SSID=[identifier]", "standalone SSID cannot impersonate legacy metrics");
            Assert(ReportRedactor.Redact("BSSID=AA-BB-CC-DD-EE-FF\nSSID=private;project\nSent=3; Received=2 | Loss=33.3%") == "BSSID=[identifier]\nSSID=[identifier]\nSent=3; Received=2 | Loss=33.3%", "BSSID remains separate and statistics retained");
        }
        private static void Comparison()
        {
            ConfigurationSnapshot before = new ConfigurationSnapshot { Values = new List<ConfigurationValue> { new ConfigurationValue { Key = "same", Value = "x", Readable = true }, new ConfigurationValue { Key = "changed", Value = "1", Readable = true }, new ConfigurationValue { Key = "unknown", Value = "secret", Readable = true }, new ConfigurationValue { Key = "removed", Value = "1", Readable = true } } };
            ConfigurationSnapshot after = new ConfigurationSnapshot { Values = new List<ConfigurationValue> { new ConfigurationValue { Key = "same", Value = "x", Readable = true }, new ConfigurationValue { Key = "changed", Value = "0", Readable = true }, new ConfigurationValue { Key = "unknown", Readable = false } } };
            List<ConfigurationChange> changes = RepairHistoryStore.Compare(before, after);
            Assert(changes.Count == 1 && changes[0].Key == "changed" && changes[0].Before == "1" && changes[0].After == "0", "only readable actual differences");
            Assert(RepairHistoryStore.Compare(null, after).Count == 0, "missing snapshot is unknown");
        }
        private static void Storage(string fixture)
        {
            string directory = Path.Combine(fixture, "history");
            RepairHistoryStore store = new RepairHistoryStore(directory);
            Assert(store.ReadAll().Entries.Count == 0 && store.ReadAll().ErrorCode == null, "missing history is empty");
            RepairHistoryEntry entry = Entry();
            Assert(store.Record(entry).Success && !String.IsNullOrEmpty(entry.Id), "record assigns ID");
            string path = Path.Combine(directory, "history.nrh");
            byte[] encrypted = File.ReadAllBytes(path);
            Assert(!Encoding.UTF8.GetString(encrypted).Contains("fixture-action"), "DPAPI ciphertext contains no plain action title");
            HistoryReadResult read = new RepairHistoryStore(directory).ReadAll();
            Assert(read.ErrorCode == null && read.Entries.Count == 1 && read.Entries[0].AfterOnline == true && read.Entries[0].ConfigurationChanges[0].After == "0", "encrypted roundtrip");
            Assert(!store.Record(entry).Success, "duplicate ID rejected");
            RepairHistoryEntry partial = Entry(); partial.Success = false; partial.Outcome = "Partial"; partial.RestartRequired = true;
            partial.Steps.Add(new RepairStepRecord { ActionId = "AutoDns", State = "RestartRequired", Code = "RestartNeeded", MayHaveChanged = true });
            Assert(store.Record(partial).Success, "partial and restart outcomes persist");
            RepairHistoryEntry loadedPartial = store.ReadAll().Entries.Single(e => e.Id == partial.Id);
            Assert(loadedPartial.Outcome == "Partial" && loadedPartial.RestartRequired && loadedPartial.Steps.Single().MayHaveChanged && loadedPartial.Steps[0].State == "RestartRequired", "typed outcomes survive DPAPI roundtrip");
            RepairHistoryEntry malformed = Entry(); malformed.Outcome = "invented";
            Assert(!store.Record(malformed).Success, "unknown outcome rejected");
            malformed = Entry(); malformed.Steps.Add(new RepairStepRecord { ActionId = "AutoDns", State = "invented" });
            Assert(!store.Record(malformed).Success, "unknown step state rejected");
            Assert(read.Entries[0].Outcome == null && read.Entries[0].Steps.Count == 0, "legacy entry without typed metadata remains readable");
            for (int i = 0; i < 102; i++) Assert(store.Record(Entry()).Success, "bounded history insert");
            Assert(store.ReadAll().Entries.Count == 100, "automatic 100 entry cap");
            RepairHistoryEntry invalid = Entry(); invalid.Operation = "command";
            Assert(!store.Record(invalid).Success, "invalid operation rejected");
            invalid = Entry(); invalid.ActionTitles[0] = new string('x', 513);
            Assert(!store.Record(invalid).Success, "field length bound");
            invalid = Entry(); invalid.EndedUtc = invalid.StartedUtc.AddTicks(-1);
            Assert(!store.Record(invalid).Success, "time boundary");
            string backups = Path.Combine(fixture, "backups"); Directory.CreateDirectory(backups);
            string backup = Path.Combine(backups, "fixture.nrb"); File.WriteAllBytes(backup, new byte[] { 1, 2, 3 });
            entry = Entry(); entry.BackupPath = backup;
            Assert(store.Record(entry).Success, "store backup reference");
            string resolved, error;
            Assert(store.TryGetLatestBackupPath(backups, out resolved, out error) && resolved == backup, "validated backup path in supplied engine root");
            File.Delete(backup);
            Assert(!store.TryGetLatestBackupPath(backups, out resolved, out error) && error == "HistoryBackupUnavailable", "missing backup error");
            entry = Entry(); entry.BackupPath = Path.Combine(fixture, "outside.nrb"); File.WriteAllBytes(entry.BackupPath, new byte[] { 1 }); store.Record(entry);
            Assert(!store.TryGetLatestBackupPath(backups, out resolved, out error), "external backup path rejected");
            File.WriteAllBytes(path, new byte[] { 1, 2, 3 });
            Assert(store.ReadAll().ErrorCode == "HistoryUnreadable", "corrupt ciphertext explicit error");
            Assert(!store.Record(Entry()).Success && File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3 }), "corrupt file preserved on record");
            using (FileStream stream = new FileStream(path, FileMode.Create)) stream.SetLength(5 * 1024 * 1024);
            Assert(store.ReadAll().ErrorCode == "HistoryUnreadable", "encrypted size bound");
            RepairHistoryDocument document = new RepairHistoryDocument();
            document.Version = 2;
            byte[] xml;
            using (MemoryStream stream = new MemoryStream()) { new XmlSerializer(typeof(RepairHistoryDocument)).Serialize(stream, document); xml = stream.ToArray(); }
            File.WriteAllBytes(path, ProtectedData.Protect(xml, Encoding.UTF8.GetBytes("NetworkRescue.History.v1"), DataProtectionScope.CurrentUser));
            Assert(store.ReadAll().ErrorCode == "HistoryUnreadable", "authenticated unsupported schema rejected");
            string unsafeXml = "<!DOCTYPE RepairHistoryDocument [<!ENTITY a 'secret'>]><RepairHistoryDocument><Version>1</Version><Entries /></RepairHistoryDocument>";
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(unsafeXml), Encoding.UTF8.GetBytes("NetworkRescue.History.v1"), DataProtectionScope.CurrentUser));
            Assert(store.ReadAll().ErrorCode == "HistoryUnreadable", "DTD disallowed even in authenticated fixture");
            unsafeXml = "<RepairHistoryDocument><Version>1</Version><Unexpected>data</Unexpected><Entries /></RepairHistoryDocument>";
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(unsafeXml), Encoding.UTF8.GetBytes("NetworkRescue.History.v1"), DataProtectionScope.CurrentUser));
            Assert(store.ReadAll().ErrorCode == "HistoryUnreadable", "unknown XML structure rejected");
        }
        public static int Main()
        {
            string fixture = Path.Combine(Path.GetTempPath(), "NetworkRescueHistoryTests-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(fixture); Redaction(); Comparison(); Storage(fixture); Console.WriteLine("RepairHistoryTests passed: " + assertions); return 0; }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { if (Path.GetFullPath(fixture).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) && Path.GetFileName(fixture).StartsWith("NetworkRescueHistoryTests-", StringComparison.Ordinal)) Directory.Delete(fixture, true); }
        }
    }
}
