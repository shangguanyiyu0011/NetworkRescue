using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace NetworkRescue
{
    internal static class NetworkWorkspaceTests
    {
        private static int checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        private static void Assert(bool passed, string name) { checks++; if (!passed) throw new Exception("Failed: " + name); }
        private static void Reject(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidDataException) { rejected = true; } catch (IOException) { rejected = true; }
            Assert(rejected, name);
        }
        private static NetworkConfigurationEntry Entry(string kind, string value)
        {
            bool proxy = kind.StartsWith("Proxy", StringComparison.Ordinal);
            return new NetworkConfigurationEntry { Key = proxy ? "proxy:" + kind : "adapter:fixture:" + kind, Kind = kind, AdapterId = proxy ? "" : "fixture", AdapterName = proxy ? "" : "Fixture", Value = value, Fingerprint = "never-persist-this-fingerprint" };
        }
        private static NetworkChangeSnapshot Snapshot(params NetworkConfigurationEntry[] entries) { return new NetworkChangeSnapshot { TimeUtc = Now, Complete = true, Entries = entries }; }
        private static MonitorTarget[] Targets()
        {
            return new[] { new MonitorTarget { Id = "gateway", Host = "192.0.2.1", Method = "icmp" }, new MonitorTarget { Id = "ipv6", Host = "[2001:db8::1]", Method = "icmp" }, new MonitorTarget { Id = "website", Host = "EXAMPLE.TEST.", Method = "https", UseSystemProxy = true } };
        }
        private static string Decrypt(string path)
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(path), Encoding.UTF8.GetBytes("NetworkRescue.Workspace.v1"), DataProtectionScope.CurrentUser));
        }
        private static void Encrypt(string path, string xml)
        {
            File.WriteAllBytes(path, ProtectedData.Protect(Encoding.UTF8.GetBytes(xml), Encoding.UTF8.GetBytes("NetworkRescue.Workspace.v1"), DataProtectionScope.CurrentUser));
        }

        private static void RoundTrip(string root)
        {
            NetworkWorkspaceStore store = new NetworkWorkspaceStore(root), other = new NetworkWorkspaceStore(root);
            Assert(store.LoadHealthProfiles().Length == 0 && other.LoadMonitorPlans().Length == 0, "new workspace is empty");
            NetworkChangeSnapshot input = Snapshot(Entry("ProxyPac", "https://user:password@pac.example/private-subscription?token=secret#private-fragment"), Entry("Dns", "2001:db8::53, 192.0.2.53"));
            HealthProfile saved = store.SaveHealthProfile("  Home  ", input);
            Assert(saved.Name == "Home" && saved.CreatedUtc.Kind == DateTimeKind.Utc && saved.Id.Length == 32, "profile metadata canonicalized");
            Assert(saved.Snapshot.Entries.All(delegate(NetworkConfigurationEntry e) { return e.Fingerprint == null; }), "save strips fingerprints in memory too");
            Assert(saved.Snapshot.Entries.Single(delegate(NetworkConfigurationEntry e) { return e.Kind == "ProxyPac"; }).Value == "https://pac.example", "PAC contains authority only");
            input.Entries[1].Value = "192.0.2.99"; saved.Name = "changed return";
            HealthProfile loaded = other.LoadHealthProfiles().Single();
            Assert(loaded.Name == "Home" && loaded.Snapshot.Entries.Single(delegate(NetworkConfigurationEntry e) { return e.Kind == "Dns"; }).Value.Contains("192.0.2.53"), "health deep copy persists across instances");
            loaded.Snapshot.Entries[0].Value = "mutated";
            Assert(other.LoadHealthProfiles().Single().Snapshot.Entries[0].Value != "mutated", "loaded profile mutations do not affect storage");
            string healthPath = Path.Combine(root, "health-profiles.nrw"), plain = Decrypt(healthPath);
            Assert(plain.Contains("<Version>1</Version>") && !plain.Contains("Fingerprint") && !plain.Contains("password") && !plain.Contains("private-subscription") && !plain.Contains("token=") && !plain.Contains("private-fragment"), "DPAPI plaintext schema contains sanitized values only");
            Assert(!Encoding.UTF8.GetString(File.ReadAllBytes(healthPath)).Contains("pac.example"), "health file is encrypted");
            Reject(delegate { other.SaveHealthProfile("home", Snapshot()); }, "duplicate health name rejected case insensitively");
            MonitorTarget[] targets = Targets();
            MonitorPlan plan = store.SaveMonitorPlan("Daily", targets, 1000, 300);
            Assert(plan.Targets[1].Host == "2001:db8::1" && plan.Targets[2].Host == "example.test" && plan.Targets[2].UseSystemProxy, "IPv4 IPv6 domain and proxy plan preserved");
            targets[0].Host = "192.0.2.99"; targets[2].UseSystemProxy = false; plan.Targets[0].Host = "192.0.2.88";
            Assert(other.LoadMonitorPlans().Single().Targets[0].Host == "192.0.2.1" && other.LoadMonitorPlans().Single().Targets[2].UseSystemProxy, "plan input and return are deep copied");
            Assert(other.LoadMonitorPlans().Single().IntervalMs == 1000 && other.LoadMonitorPlans().Single().Count == 300, "plan range round trips");
            Reject(delegate { other.SaveMonitorPlan("daily", Targets(), 1000, 1); }, "duplicate plan name rejected");
            Assert(store.DeleteHealthProfile(saved.Id) && !other.DeleteHealthProfile(saved.Id), "health delete persists and absent delete returns false");
            Assert(store.DeleteMonitorPlan(plan.Id.ToUpperInvariant()) && !other.DeleteMonitorPlan(plan.Id), "plan delete accepts canonical Guid case");
            Assert(store.LoadHealthProfiles().Length == 0 && store.LoadMonitorPlans().Length == 0, "both categories remain separately empty");
            Assert(Directory.GetFiles(root, "*.tmp").Length == 0, "atomic writes leave no temporary files");
            MonitorTarget[] bridgeTargets = new[] { new MonitorTarget { Id = "target-1", Host = "LOCALHOST", Method = "https", UseSystemProxy = true }, new MonitorTarget { Id = "target-2", Host = "::1", Method = "icmp", UseSystemProxy = false } };
            Task.Run(delegate { store.SaveMonitorPlan("bridge plan", bridgeTargets, 2000, 15); }).GetAwaiter().GetResult();
            MonitorPlan bridge = new NetworkWorkspaceStore(root).LoadMonitorPlans().Single();
            Assert(bridge.Targets[0].Host == "localhost" && bridge.Targets[1].Host == "::1" && bridge.IntervalMs == 2000 && bridge.Count == 15, "exact controller localhost and IPv6 plan round trips across async save");
        }

        private static void Validation(string root)
        {
            NetworkWorkspaceStore store = new NetworkWorkspaceStore(root);
            Reject(delegate { new NetworkWorkspaceStore("relative"); }, "relative root rejected");
            foreach (string name in new[] { "", " ", new String('x', 65), "line\nbreak", "hidden\u202ename", "nul\0name" }) Reject(delegate { store.SaveHealthProfile(name, Snapshot()); }, "unreadable or oversized name rejected");
            Assert(store.SaveHealthProfile(new String('界', 64), Snapshot()).Name.Length == 64, "64 readable character name accepted");
            Reject(delegate { store.SaveHealthProfile("incomplete", new NetworkChangeSnapshot { TimeUtc = Now, Complete = false }); }, "incomplete health capture rejected");
            Reject(delegate { store.SaveHealthProfile("no time", new NetworkChangeSnapshot { Complete = true }); }, "missing snapshot time rejected");
            Reject(delegate { store.SaveHealthProfile("unknown", Snapshot(Entry("Unknown", "secret"))); }, "unknown configuration setting rejected");
            Reject(delegate { store.SaveHealthProfile("bad address", Snapshot(Entry("Dns", "https://secret.example/private"))); }, "non IP address set rejected");
            foreach (string id in new[] { "", "../outside", "not-guid", new String('0', 31) })
            {
                Reject(delegate { store.DeleteHealthProfile(id); }, "invalid health id rejected"); Reject(delegate { store.DeleteMonitorPlan(id); }, "invalid plan id rejected");
            }
            Reject(delegate { store.SaveMonitorPlan("no targets", new MonitorTarget[0], 1000, 1); }, "empty plan rejected");
            Reject(delegate { store.SaveMonitorPlan("too many", Enumerable.Range(0, 5).Select(delegate(int i) { return new MonitorTarget { Id = "a" + i, Host = "example.test", Method = "icmp" }; }).ToArray(), 1000, 1); }, "five targets rejected");
            foreach (int interval in new[] { 999, 10001 }) Reject(delegate { store.SaveMonitorPlan("interval", Targets(), interval, 1); }, "invalid interval rejected");
            foreach (int count in new[] { 0, 3601 }) Reject(delegate { store.SaveMonitorPlan("count", Targets(), 1000, count); }, "invalid count rejected");
            MonitorTarget[] targets = Targets(); targets[1].Id = targets[0].Id;
            Reject(delegate { store.SaveMonitorPlan("duplicate id", targets, 1000, 1); }, "duplicate target id rejected");
            targets = Targets(); targets[0].Id = "bad/id";
            Reject(delegate { store.SaveMonitorPlan("bad id", targets, 1000, 1); }, "target id character validation");
            targets = Targets(); targets[0].Method = "http";
            Reject(delegate { store.SaveMonitorPlan("bad method", targets, 1000, 1); }, "method whitelist preserved");
            targets = Targets(); targets[0].Host = "https://user:pass@example.test/path";
            Reject(delegate { store.SaveMonitorPlan("bad host", targets, 1000, 1); }, "URL host rejected without starting a request");
            Assert(store.SaveMonitorPlan("max range", new[] { new MonitorTarget { Id = "max", Host = "example.test", Method = "https" } }, 10000, 3600).Count == 3600, "maximum monitor range accepted");
        }

        private static void Comparison()
        {
            HealthProfile profile = new HealthProfile { Snapshot = Snapshot(Entry("Dns", "192.0.2.53, 2001:db8::53"), Entry("Addresses", "192.0.2.1")) };
            HealthComparison same = NetworkWorkspaceStore.Compare(profile, Snapshot(Entry("Addresses", "192.0.2.1"), Entry("Dns", "2001:db8::53,192.0.2.53")));
            Assert(same.Complete && same.Changes.Length == 0 && same.ComparedUtc.Kind == DateTimeKind.Utc, "set and entry reorder produces no diff");
            HealthComparison changed = NetworkWorkspaceStore.Compare(profile, Snapshot(Entry("Dns", "192.0.2.54"), Entry("Gateways", "192.0.2.254")));
            Assert(changed.Complete && changed.Changes.Length == 3 && changed.Changes.All(delegate(NetworkChangeEvent e) { return e.Kind == "SettingChanged"; }), "added removed and changed values reported without fault judgment");
            Assert(changed.Changes.Single(delegate(NetworkChangeEvent e) { return e.Setting == "Addresses"; }).After == "", "removed value has empty after");
            Assert(changed.Changes.Single(delegate(NetworkChangeEvent e) { return e.Setting == "Gateways"; }).Before == "", "added value has empty before");
            HealthComparison missing = NetworkWorkspaceStore.Compare(profile, new NetworkChangeSnapshot { TimeUtc = Now, Complete = false, ErrorCode = "AdapterReadUnavailable" });
            Assert(!missing.Complete && missing.Changes.Length == 0 && missing.ErrorCode == "AdapterReadUnavailable", "incomplete capture cannot imply deletions");
            Assert(!NetworkWorkspaceStore.Compare(profile, null).Complete && !NetworkWorkspaceStore.Compare(null, Snapshot()).Complete, "missing comparison input is explicit unavailable");
            HealthProfile proxy = new HealthProfile { Snapshot = Snapshot(Entry("ProxyPac", "https://user:old@pac.example/old?token=old")) };
            Assert(NetworkWorkspaceStore.Compare(proxy, Snapshot(Entry("ProxyPac", "https://user:new@pac.example/new?token=new"))).Changes.Length == 0, "private-only PAC changes are outside persisted comparison scope");
        }

        private static void Corruption(string root)
        {
            NetworkWorkspaceStore store = new NetworkWorkspaceStore(root);
            HealthProfile profile = store.SaveHealthProfile("good", Snapshot());
            store.SaveMonitorPlan("independent", Targets(), 1000, 1);
            string path = Path.Combine(root, "health-profiles.nrw");
            byte[] corrupt = File.ReadAllBytes(path); corrupt[corrupt.Length / 2] ^= 128; File.WriteAllBytes(path, corrupt);
            Reject(delegate { store.LoadHealthProfiles(); }, "DPAPI corruption reports explicit read failure");
            Reject(delegate { store.SaveHealthProfile("new", Snapshot()); }, "corrupt old data cannot be overwritten by save");
            Reject(delegate { store.DeleteHealthProfile(profile.Id); }, "corrupt old data cannot be overwritten by delete");
            Assert(File.ReadAllBytes(path).SequenceEqual(corrupt), "failed read save and delete preserve corrupted bytes");
            Assert(store.LoadMonitorPlans().Single().Name == "independent", "health corruption does not hide monitor plans");
            File.Delete(path); store.SaveHealthProfile("versioned", Snapshot());
            string xml = Decrypt(path); Encrypt(path, xml.Replace("<Version>1</Version>", "<Version>99</Version>"));
            Reject(delegate { store.LoadHealthProfiles(); }, "unsupported schema rejected");
            Encrypt(path, xml.Replace("<Version>1</Version>", ""));
            Reject(delegate { store.LoadHealthProfiles(); }, "missing schema rejected");
            Encrypt(path, xml.Replace("<Kind>health</Kind>", "<Kind>health</Kind><Unexpected>secret</Unexpected>"));
            Reject(delegate { store.LoadHealthProfiles(); }, "unknown document fields rejected");
            File.Delete(path); store.SaveHealthProfile("safe", Snapshot(Entry("ProxyPac", "https://pac.example")));
            xml = Decrypt(path); Encrypt(path, xml.Replace("https://pac.example", "https://user:pass@pac.example/private-path"));
            Reject(delegate { store.LoadHealthProfiles(); }, "persisted unsanitized PAC payload rejected");
            byte[] unsafePayload = File.ReadAllBytes(path);
            Reject(delegate { store.SaveHealthProfile("new safe", Snapshot()); }, "unsafe old payload cannot be silently sanitized and overwritten");
            Assert(File.ReadAllBytes(path).SequenceEqual(unsafePayload), "unsafe persisted payload remains unchanged on failed save");
            byte[] oversized = new byte[1024 * 1024 + 1]; File.WriteAllBytes(path, oversized);
            Reject(delegate { store.LoadHealthProfiles(); }, "oversized encrypted file rejected before DPAPI");
            Reject(delegate { store.SaveHealthProfile("new", Snapshot()); }, "oversized existing file preserved");
            Assert(new FileInfo(path).Length == oversized.Length, "oversized old file unchanged");
        }

        private static void Limits(string root)
        {
            NetworkWorkspaceStore store = new NetworkWorkspaceStore(root);
            Parallel.For(0, 20, delegate(int i) { new NetworkWorkspaceStore(root).SaveHealthProfile("health " + i, Snapshot()); });
            Assert(store.LoadHealthProfiles().Length == 20, "cross instance concurrent writes retain all twenty profiles");
            Reject(delegate { store.SaveHealthProfile("twenty one", Snapshot()); }, "health profile limit enforced");
            for (int i = 0; i < 20; i++) store.SaveMonitorPlan("plan " + i, Targets(), 1000, 1);
            Reject(delegate { store.SaveMonitorPlan("twenty one", Targets(), 1000, 1); }, "monitor plan limit enforced");
            Assert(store.LoadMonitorPlans().Length == 20 && store.LoadHealthProfiles().Length == 20, "category limits are independent");
            Assert(Directory.GetFiles(root, "*.nrw").All(delegate(string p) { return new FileInfo(p).Length <= 1024 * 1024; }), "every persisted file stays within one MiB");
            string sizeRoot = Path.Combine(root, "size"); NetworkWorkspaceStore sizeStore = new NetworkWorkspaceStore(sizeRoot);
            sizeStore.SaveHealthProfile("small", Snapshot()); byte[] before = File.ReadAllBytes(Path.Combine(sizeRoot, "health-profiles.nrw"));
            NetworkConfigurationEntry[] huge = Enumerable.Range(0, 512).Select(delegate(int i) { return new NetworkConfigurationEntry { Key = "adapter:" + i + ":Dns", Kind = "Dns", AdapterId = i.ToString(), AdapterName = new String('界', 256), Value = String.Join(", ", Enumerable.Range(1, 400).Select(delegate(int a) { return "2001:db8::" + a.ToString("x"); })) }; }).ToArray();
            Reject(delegate { sizeStore.SaveHealthProfile("huge", Snapshot(huge)); }, "oversized serialized snapshot rejected");
            Assert(File.ReadAllBytes(Path.Combine(sizeRoot, "health-profiles.nrw")).SequenceEqual(before), "oversized attempted write preserves previous valid file");
            Assert(Directory.GetFiles(sizeRoot, "*.tmp").Length == 0, "failed writes leave no temporary file");
        }

        private static void SystemTemporaryDirectory()
        {
            string parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string root = Path.Combine(parent, "NetworkWorkspaceRepro-" + Guid.NewGuid().ToString("N"));
            try
            {
                NetworkWorkspaceStore store = new NetworkWorkspaceStore(root);
                Task.Run(delegate { store.SaveMonitorPlan("bridge plan", new[] { new MonitorTarget { Id = "target-1", Host = "LOCALHOST", Method = "https", UseSystemProxy = true }, new MonitorTarget { Id = "target-2", Host = "::1", Method = "icmp" } }, 2000, 15); }).GetAwaiter().GetResult();
                MonitorPlan plan = new NetworkWorkspaceStore(root).LoadMonitorPlans().Single();
                Assert(plan.Targets[0].Host == "localhost" && plan.Targets[1].Host == "::1", "system temporary location works through redirected ancestor directories");
            }
            finally
            {
                if (Path.GetDirectoryName(Path.GetFullPath(root)) == parent && Path.GetFileName(root).StartsWith("NetworkWorkspaceRepro-", StringComparison.Ordinal) && Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        public static int Main()
        {
            string testRoot = Path.GetFullPath(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
            string temporary = Path.Combine(testRoot, "temp-" + Guid.NewGuid().ToString("N"));
            try
            {
                RoundTrip(Path.Combine(temporary, "roundtrip")); Validation(Path.Combine(temporary, "validation")); Comparison(); Corruption(Path.Combine(temporary, "corrupt")); Limits(Path.Combine(temporary, "limits")); SystemTemporaryDirectory();
                Console.WriteLine("Network workspace checks passed: " + checks + " (temporary DPAPI files; no network operations)"); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine("After " + checks + " checks: " + ex.ToString()); return 1; }
            finally
            {
                if (Path.GetDirectoryName(Path.GetFullPath(temporary)) == testRoot && Path.GetFileName(temporary).StartsWith("temp-", StringComparison.Ordinal) && Directory.Exists(temporary)) Directory.Delete(temporary, true);
            }
        }
    }
}
