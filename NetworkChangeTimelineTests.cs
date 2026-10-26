using System;
using System.Linq;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class NetworkChangeTimelineTests
    {
        private static int checks;
        private static readonly DateTime Now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        private static void Assert(bool passed, string name)
        {
            checks++;
            if (!passed) throw new Exception("Failed: " + name);
        }

        private static NetworkConfigurationEntry Entry(string kind, string value, string fingerprint)
        {
            bool proxy = kind.StartsWith("Proxy", StringComparison.Ordinal);
            return new NetworkConfigurationEntry { Key = (proxy ? "proxy:" : "adapter:fixture:") + kind, Kind = kind, AdapterId = proxy ? "" : "fixture", AdapterName = proxy ? "" : "Fixture", Value = value, Fingerprint = fingerprint };
        }

        private static NetworkChangeSnapshot Snapshot(params NetworkConfigurationEntry[] entries)
        {
            return new NetworkChangeSnapshot { TimeUtc = Now, Complete = true, Entries = entries };
        }

        private static MonitorSample Sample(string id, bool response, string method)
        {
            return new MonitorSample { TargetId = id, TargetHost = id + ".test", TimeUtc = Now, Method = method, Success = method == "ICMP" && response, ResponseReceived = response };
        }

        private static void Configuration()
        {
            NetworkChangeTracker tracker = new NetworkChangeTracker();
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Addresses", "192.0.2.1, 2001:db8::1", null), Entry("Dns", "192.0.2.53", null))).Length == 0, "first complete baseline is quiet");
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Dns", "192.0.2.53", null), Entry("Addresses", "2001:db8::1, 192.0.2.1, 192.0.2.1", null))).Length == 0, "set order and duplicates are irrelevant");
            NetworkChangeEvent[] changed = tracker.ObserveConfiguration(Snapshot(Entry("Addresses", "192.0.2.2", null), Entry("Gateways", "192.0.2.254", null)));
            Assert(changed.Length == 3 && changed.All(delegate(NetworkChangeEvent e) { return e.Kind == "SettingChanged" && e.TargetId == "fixture"; }), "only added removed and changed fields emit");
            Assert(changed.Single(delegate(NetworkChangeEvent e) { return e.Setting == "Dns"; }).After == "", "removed entry records empty after");
            Assert(changed.Single(delegate(NetworkChangeEvent e) { return e.Setting == "Gateways"; }).Before == "", "added entry records empty before");
            Assert(tracker.ObserveConfiguration(new NetworkChangeSnapshot { TimeUtc = Now, Complete = false, ErrorCode = "AdapterReadUnavailable" }).Single().Kind == "ObservationUnavailable", "incomplete capture reports observation failure");
            Assert(tracker.ObserveConfiguration(new NetworkChangeSnapshot { TimeUtc = Now, Complete = false, ErrorCode = "AdapterReadUnavailable" }).Length == 0, "repeated observation failure is coalesced");
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Addresses", "192.0.2.2", null), Entry("Gateways", "192.0.2.254", null))).Length == 0, "capture failure does not delete baseline");
            NetworkConfigurationEntry mutable = Entry("Dns", "192.0.2.53", null);
            tracker = new NetworkChangeTracker(); tracker.ObserveConfiguration(Snapshot(mutable)); mutable.Value = "192.0.2.54";
            Assert(tracker.ObserveConfiguration(Snapshot(mutable)).Length == 1, "baseline is a defensive copy");
            tracker = new NetworkChangeTracker();
            Assert(tracker.ObserveConfiguration(new NetworkChangeSnapshot { TimeUtc = Now, Complete = false, ErrorCode = "private error detail" }).Single().ErrorCode == "SnapshotUnavailable", "unknown error text is never exposed");
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Dns", "192.0.2.53", null))).Length == 0, "first complete baseline after failure is quiet");
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Dns", "192.0.2.54", null), Entry("Dns", "192.0.2.55", null))).Single().ErrorCode == "InvalidSnapshot", "duplicate keys rejected without changing baseline");
            Assert(tracker.ObserveConfiguration(Snapshot(Entry("Dns", "192.0.2.53", null))).Length == 0, "invalid snapshot preserves baseline");
        }

        private static void Privacy()
        {
            string first = "https://alice:password@proxy.example:8443/private/subscription?token=secret#fragment";
            string second = "https://bob:other@proxy.example:8443/another?token=changed";
            string safe = NetworkChangeReader.SanitizeProxy(first, true);
            Assert(safe == "https://proxy.example:8443", "PAC displays authority only");
            Assert(NetworkChangeReader.SanitizeProxy("http=" + first + ";https=127.0.0.1:7890", false) == "Configured", "ambiguous mapped URLs reveal no endpoints");
            Assert(NetworkChangeReader.SanitizeProxy("http=proxy.example:8443;https=127.0.0.1:7890", false) == "http=http://proxy.example:8443; https=http://127.0.0.1:7890", "plain mapped endpoints remain readable");
            Assert(NetworkChangeReader.SanitizeProxy("http://proxy.example/path;private-token", false) == "http://proxy.example", "semicolon in URL path never becomes a host");
            Assert(NetworkChangeReader.SanitizeProxy("http=proxy.example/path;https=private-token", false) == "Configured", "mapped URL path cannot masquerade as another endpoint");
            Assert(NetworkChangeReader.SanitizeProxy("secret invalid @:// password", false) == "Invalid", "invalid proxy never echoes raw text");
            Assert(NetworkChangeReader.SanitizeProxy("file:///C:/private/pac.js", true) == "Invalid", "local PAC paths are not exposed");
            string ipv6 = NetworkChangeReader.SanitizeProxy("https://u:p@[2001:db8::1]:8443/p?q=secret", true);
            Assert(ipv6 == "https://[2001:db8::1]:8443", "IPv6 authority is sanitized: " + ipv6);
            NetworkChangeTracker tracker = new NetworkChangeTracker();
            tracker.ObserveConfiguration(Snapshot(Entry("ProxyPac", safe, NetworkChangeReader.Hash(first))));
            NetworkChangeEvent change = tracker.ObserveConfiguration(Snapshot(Entry("ProxyPac", safe, NetworkChangeReader.Hash(second)))).Single();
            Assert(change.Before == safe && change.After == safe && change.Setting == "ProxyPac", "same display with different fingerprint still emits");
            tracker = new NetworkChangeTracker();
            tracker.ObserveConfiguration(Snapshot(Entry("ProxyServer", first, null)));
            change = tracker.ObserveConfiguration(Snapshot(Entry("ProxyServer", second, null))).Single();
            Assert(change.Before == safe && change.After == safe, "tracker also sanitizes external proxy entries");
            Assert(typeof(NetworkConfigurationEntry).GetField("Fingerprint") == null, "raw comparison fingerprint is not public");
        }

        private static void Samples()
        {
            NetworkChangeTracker tracker = new NetworkChangeTracker();
            Assert(tracker.ObserveSample(Sample("a", true, "ICMP")).Length == 0, "first successful sample establishes baseline");
            NetworkChangeEvent failure = tracker.ObserveSample(Sample("a", false, "ICMP")).Single();
            Assert(failure.Kind == "TargetNoResponse" && failure.ErrorCode == "NoIcmpResponse", "ICMP failure describes no response only");
            Assert(tracker.ObserveSample(Sample("a", false, "ICMP")).Length == 0, "same target failure is quiet");
            Assert(tracker.ObserveSample(Sample("b", true, "ICMP")).Length == 0, "second target has separate state");
            Assert(tracker.ObserveSample(Sample("a", true, "ICMP")).Single().Kind == "TargetRecovered", "ICMP recovery follows target failure");
            Assert(tracker.ObserveSample(Sample("b", true, "ICMP")).Length == 0, "other target does not recover spuriously");
            Assert(tracker.ObserveSample(Sample("c", false, "ICMP")).Single().Kind == "TargetNoResponse", "first failed sample is observable");
            foreach (int status in new[] { 301, 403 })
            {
                string id = "http" + status;
                tracker.ObserveSample(Sample(id, false, "HTTPS"));
                MonitorSample sample = Sample(id, true, "HTTPS"); sample.HttpStatusCode = status; sample.HttpSuccess = false;
                Assert(tracker.ObserveSample(sample).Single().Kind == "TargetRecovered", "HTTP " + status + " is a received response");
                Assert(tracker.ObserveSample(sample).Length == 0, "HTTP " + status + " stays responding");
            }
        }

        private static void Retention()
        {
            NetworkChangeTracker tracker = new NetworkChangeTracker();
            foreach (string kind in new[] { "MonitoringStarted", "MonitoringCompleted", "MonitoringStopped" }) Assert(tracker.Record(kind, Now).Kind == kind, "allowed lifecycle " + kind);
            foreach (string kind in new[] { "SettingChanged", "TargetNoResponse", "ObservationUnavailable", "anything", "", null })
            {
                bool rejected = false;
                try { tracker.Record(kind, Now); } catch (ArgumentException) { rejected = true; }
                Assert(rejected, "lifecycle whitelist");
            }
            Assert(tracker.TotalEvents == 3, "invalid lifecycle does not append");
            Parallel.For(0, 600, delegate(int i) { tracker.Record("MonitoringStarted", DateTime.SpecifyKind(Now, DateTimeKind.Unspecified)); NetworkChangeEvent[] ignored = tracker.RecentEvents; });
            NetworkChangeEvent[] recent = tracker.RecentEvents;
            Assert(tracker.TotalEvents == 603 && recent.Length == 500 && recent[0].Sequence == 104 && recent[499].Sequence == 603, "thread safe latest 500 with full count");
            Assert(recent.All(delegate(NetworkChangeEvent e) { return e.TimeUtc.Kind == DateTimeKind.Utc; }), "all event timestamps are UTC");
            recent[0].Kind = "mutated";
            Assert(tracker.RecentEvents[0].Kind != "mutated", "event reads are defensive copies");
            NetworkChangeEvent recorded = tracker.Record("MonitoringStopped", Now); recorded.Kind = "mutated";
            Assert(tracker.RecentEvents.Last().Kind == "MonitoringStopped", "append return is a defensive copy");
        }

        private static void ReadOnlySmoke()
        {
            NetworkChangeSnapshot snapshot = NetworkChangeReader.Capture();
            Assert(snapshot.TimeUtc.Kind == DateTimeKind.Utc && snapshot.Entries != null, "host capture returns safe snapshot shape");
            Assert(snapshot.Entries.All(delegate(NetworkConfigurationEntry e) { return !String.IsNullOrEmpty(e.Key); }), "host capture keys exist");
            string gateway = NetworkChangeReader.FindGateway();
            System.Net.IPAddress address;
            Assert(gateway == "" || System.Net.IPAddress.TryParse(gateway, out address), "gateway is empty or valid address");
            Console.WriteLine("Read-only host smoke: completed (private values omitted).");
        }

        private static void LongValues()
        {
            string addresses = String.Join(", ", Enumerable.Range(1, 600).Select(delegate(int i) { return "2001:db8::" + i.ToString("x"); }));
            NetworkConfigurationEntry first = Entry("Dns", addresses + ", 2001:db8::ffff", null);
            first.AdapterName = new String('n', 300);
            NetworkChangeTracker tracker = new NetworkChangeTracker();
            tracker.ObserveConfiguration(Snapshot(first));
            NetworkConfigurationEntry next = Entry("Dns", addresses + ", 2001:db8::fffe", null);
            next.AdapterName = first.AdapterName;
            NetworkChangeEvent change = tracker.ObserveConfiguration(Snapshot(next)).Single();
            Assert(change.Before.Length == 2048 && change.After.Length == 2048 && change.Before.EndsWith("…") && change.After.EndsWith("…"), "long configuration event values are visibly bounded");
            Assert(change.AdapterName.Length == 256 && change.AdapterName.EndsWith("…"), "long adapter names are visibly bounded");
            Assert(change.Before == change.After, "fixture differs beyond the display boundary");
            Assert(tracker.ObserveConfiguration(Snapshot(next)).Length == 0, "full baseline remains unchanged by event truncation");
            Assert(tracker.ObserveConfiguration(Snapshot(first)).Length == 1, "full baseline detects another change beyond display boundary");
            MonitorSample sample = Sample(new String('t', 400), false, "ICMP");
            change = tracker.ObserveSample(sample).Single();
            Assert(change.TargetId.Length == 256 && change.TargetId.EndsWith("…") && change.TargetHost.Length == 253 && change.TargetHost.EndsWith("…"), "external target event fields are visibly bounded");
        }

        public static int Main()
        {
            try
            {
                Configuration(); Privacy(); Samples(); Retention(); LongValues(); ReadOnlySmoke();
                Console.WriteLine("Network change timeline checks passed: " + checks);
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
        }
    }
}
