using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace NetworkRescue
{
    public sealed class NetworkConfigurationEntry
    {
        public string Key, Kind, AdapterId, AdapterName, Value;
        internal string Fingerprint;
    }

    public sealed class NetworkChangeSnapshot
    {
        public DateTime TimeUtc;
        public bool Complete;
        public string ErrorCode;
        public NetworkConfigurationEntry[] Entries = new NetworkConfigurationEntry[0];
    }

    public sealed class NetworkChangeEvent
    {
        public int Sequence;
        public DateTime TimeUtc;
        public string Kind, TargetId, TargetHost, AdapterName, Setting, Before, After, ErrorCode;
    }

    public static class NetworkChangeReader
    {
        internal static string Hash(string value)
        {
            using (SHA256 hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(value ?? "")));
        }

        private static string Authority(string value, bool pac)
        {
            Uri uri;
            string candidate = value.Trim();
            if (candidate.Length == 0) return "";
            if (!pac && candidate.IndexOf("://", StringComparison.Ordinal) < 0) candidate = "http://" + candidate;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) ||
                (uri.Scheme != "http" && uri.Scheme != "https" && (pac || (uri.Scheme != "socks" && uri.Scheme != "socks4" && uri.Scheme != "socks5"))) ||
                String.IsNullOrEmpty(uri.Host) || Uri.CheckHostName(uri.Host.Trim('[', ']')) == UriHostNameType.Unknown)
                return "Invalid";
            IPAddress address;
            string host = uri.GetComponents(UriComponents.Host, UriFormat.UriEscaped);
            if (IPAddress.TryParse(host.Trim('[', ']'), out address)) host = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address + "]" : address.ToString();
            return uri.Scheme + "://" + host + (uri.IsDefaultPort || uri.Port < 0 ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture));
        }

        internal static string SanitizeProxy(string value, bool pac)
        {
            if (String.IsNullOrWhiteSpace(value)) return "";
            if (pac) return Authority(value, true);
            string trimmed = value.Trim();
            if (trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("socks://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("socks4://", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("socks5://", StringComparison.OrdinalIgnoreCase)) return Authority(trimmed, false);
            string[] parts = value.Split(';');
            List<string> safe = new List<string>();
            foreach (string part in parts)
            {
                if (String.IsNullOrWhiteSpace(part)) continue;
                int equal = part.IndexOf('=');
                string protocol = equal < 0 ? "" : part.Substring(0, equal).Trim().ToLowerInvariant();
                if (equal >= 0 && protocol != "http" && protocol != "https" && protocol != "ftp" && protocol != "socks") equal = -1;
                if (parts.Length > 1 && equal < 0) return "Invalid";
                string rawEndpoint = equal < 0 ? part : part.Substring(equal + 1);
                int scheme = rawEndpoint.IndexOf("://", StringComparison.Ordinal);
                string authority = scheme < 0 ? rawEndpoint : rawEndpoint.Substring(scheme + 3);
                // A semicolon may belong to credentials or a URL path, so ambiguous mappings reveal no endpoints.
                if (parts.Length > 1 && authority.IndexOfAny(new[] { '/', '?', '#', '@' }) >= 0) return "Configured";
                string endpoint = Authority(rawEndpoint, false);
                if (endpoint == "Invalid" || endpoint.Length == 0) return "Invalid";
                safe.Add((equal < 0 ? "" : protocol + "=") + endpoint);
            }
            return safe.Count == 0 ? "Invalid" : String.Join("; ", safe.OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal));
        }

        private static bool ValidGateway(IPAddress address)
        {
            if (address == null || (address.AddressFamily != AddressFamily.InterNetwork && address.AddressFamily != AddressFamily.InterNetworkV6) ||
                IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) ||
                address.Equals(IPAddress.None) || address.Equals(IPAddress.IPv6None) || address.IsIPv6Multicast) return false;
            return address.AddressFamily != AddressFamily.InterNetwork || address.GetAddressBytes()[0] < 224;
        }

        private static string Addresses(IEnumerable<IPAddress> addresses)
        {
            return String.Join(", ", addresses.Select(delegate(IPAddress a) { return a.ToString(); }).Distinct(StringComparer.Ordinal).OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal));
        }

        private static void Add(List<NetworkConfigurationEntry> entries, string kind, string value, string raw, string adapterId, string adapterName)
        {
            entries.Add(new NetworkConfigurationEntry { Key = String.IsNullOrEmpty(adapterId) ? "proxy:" + kind : "adapter:" + adapterId + ":" + kind,
                Kind = kind, Value = value, Fingerprint = raw == null ? null : Hash(raw), AdapterId = adapterId ?? "", AdapterName = adapterName ?? "" });
        }

        public static NetworkChangeSnapshot Capture()
        {
            NetworkChangeSnapshot snapshot = new NetworkChangeSnapshot { TimeUtc = DateTime.UtcNow, Complete = true };
            List<NetworkConfigurationEntry> entries = new List<NetworkConfigurationEntry>();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings", false))
                {
                    object enabled = key == null ? null : key.GetValue("ProxyEnable", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    string enableValue = enabled == null ? "Disabled" : enabled is int && (int)enabled == 0 ? "Disabled" : enabled is int && (int)enabled == 1 ? "Enabled" : "Invalid";
                    Add(entries, "ProxyEnabled", enableValue, null, null, null);
                    foreach (string name in new[] { "ProxyServer", "AutoConfigURL" })
                    {
                        object value = key == null ? null : key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                        string raw = value as string;
                        Add(entries, name == "ProxyServer" ? "ProxyServer" : "ProxyPac", value == null ? "" : raw == null ? "Invalid" : SanitizeProxy(raw, name == "AutoConfigURL"), raw ?? (value == null ? "" : "InvalidType"), null, null);
                    }
                }
            }
            catch (Exception) { snapshot.Complete = false; snapshot.ErrorCode = "ProxyReadUnavailable"; }
            try
            {
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(delegate(NetworkInterface a) { return a.Id; }, StringComparer.Ordinal))
                {
                    try
                    {
                        string id = adapter.Id, name = adapter.Name;
                        Add(entries, "AdapterStatus", adapter.OperationalStatus.ToString(), null, id, name);
                        IPInterfaceProperties properties = adapter.GetIPProperties();
                        Add(entries, "Addresses", Addresses(properties.UnicastAddresses.Select(delegate(UnicastIPAddressInformation a) { return a.Address; })), null, id, name);
                        Add(entries, "Dns", Addresses(properties.DnsAddresses), null, id, name);
                        Add(entries, "Gateways", Addresses(properties.GatewayAddresses.Select(delegate(GatewayIPAddressInformation a) { return a.Address; }).Where(ValidGateway)), null, id, name);
                    }
                    catch (Exception) { snapshot.Complete = false; snapshot.ErrorCode = "AdapterReadUnavailable"; }
                }
            }
            catch (Exception) { snapshot.Complete = false; snapshot.ErrorCode = "AdapterReadUnavailable"; }
            snapshot.Entries = entries.ToArray();
            return snapshot;
        }

        public static string FindGateway()
        {
            try
            {
                // The first configured gateway is deterministic; it is not a route-table lookup.
                foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces().OrderBy(delegate(NetworkInterface a) { return a.Id; }, StringComparer.Ordinal))
                {
                    try
                    {
                        if (adapter.OperationalStatus != OperationalStatus.Up || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        string gateway = adapter.GetIPProperties().GatewayAddresses.Select(delegate(GatewayIPAddressInformation a) { return a.Address; }).Where(ValidGateway)
                            .OrderBy(delegate(IPAddress a) { return a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1; }).ThenBy(delegate(IPAddress a) { return a.ToString(); }, StringComparer.Ordinal).Select(delegate(IPAddress a) { return a.ToString(); }).FirstOrDefault();
                        if (gateway != null) return gateway;
                    }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            return "";
        }
    }

    public sealed class NetworkChangeTracker
    {
        public const int MaximumEvents = 500;
        private readonly object gate = new object();
        private readonly Queue<NetworkChangeEvent> events = new Queue<NetworkChangeEvent>();
        private readonly Dictionary<string, bool> targets = new Dictionary<string, bool>(StringComparer.Ordinal);
        private Dictionary<string, NetworkConfigurationEntry> baseline;
        private string unavailable;
        private int totalEvents;

        public int TotalEvents { get { lock (gate) return totalEvents; } }
        public NetworkChangeEvent[] RecentEvents { get { lock (gate) return events.Select(Clone).ToArray(); } }
        public NetworkChangeEvent[] Snapshot(out int total)
        {
            lock (gate) { total = totalEvents; return events.Select(Clone).ToArray(); }
        }

        private static NetworkChangeEvent Clone(NetworkChangeEvent value)
        {
            return new NetworkChangeEvent { Sequence = value.Sequence, TimeUtc = value.TimeUtc, Kind = value.Kind, TargetId = value.TargetId, TargetHost = value.TargetHost,
                AdapterName = value.AdapterName, Setting = value.Setting, Before = value.Before, After = value.After, ErrorCode = value.ErrorCode };
        }

        private static DateTime Utc(DateTime value)
        {
            return value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
        }

        private NetworkChangeEvent Append(NetworkChangeEvent value)
        {
            value.Sequence = ++totalEvents;
            value.TimeUtc = Utc(value.TimeUtc);
            value.Before = Bounded(value.Before, 2048);
            value.After = Bounded(value.After, 2048);
            value.AdapterName = Bounded(value.AdapterName, 256);
            value.TargetId = Bounded(value.TargetId, 256);
            value.TargetHost = Bounded(value.TargetHost, 253);
            events.Enqueue(value);
            if (events.Count > MaximumEvents) events.Dequeue();
            return Clone(value);
        }

        private static string Bounded(string value, int maximum)
        {
            if (value == null || value.Length <= maximum) return value;
            int length = maximum - 1;
            if (Char.IsHighSurrogate(value[length - 1])) length--;
            return value.Substring(0, length) + "…";
        }

        public NetworkChangeEvent Record(string kind, DateTime timeUtc)
        {
            if (kind != "MonitoringStarted" && kind != "MonitoringCompleted" && kind != "MonitoringStopped") throw new ArgumentException("InvalidLifecycleKind", "kind");
            lock (gate) return Append(new NetworkChangeEvent { Kind = kind, TimeUtc = timeUtc });
        }

        private static bool IsSetting(string kind)
        {
            return kind == "ProxyEnabled" || kind == "ProxyServer" || kind == "ProxyPac" || kind == "AdapterStatus" || kind == "Addresses" || kind == "Dns" || kind == "Gateways";
        }

        private static string Normalize(string kind, string value)
        {
            if (kind != "Addresses" && kind != "Dns" && kind != "Gateways") return value ?? "";
            return String.Join(", ", (value ?? "").Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal));
        }

        private NetworkChangeEvent[] Unavailable(DateTime timeUtc, string error)
        {
            if (error != "ProxyReadUnavailable" && error != "AdapterReadUnavailable" && error != "InvalidSnapshot") error = "SnapshotUnavailable";
            if (unavailable == error) return new NetworkChangeEvent[0];
            unavailable = error;
            return new[] { Append(new NetworkChangeEvent { Kind = "ObservationUnavailable", TimeUtc = timeUtc, ErrorCode = error }) };
        }

        public NetworkChangeEvent[] ObserveConfiguration(NetworkChangeSnapshot snapshot)
        {
            lock (gate)
            {
                if (snapshot == null) return Unavailable(DateTime.UtcNow, "SnapshotUnavailable");
                if (!snapshot.Complete) return Unavailable(snapshot.TimeUtc, snapshot.ErrorCode);
                Dictionary<string, NetworkConfigurationEntry> current = new Dictionary<string, NetworkConfigurationEntry>(StringComparer.Ordinal);
                if (snapshot.Entries == null) return Unavailable(snapshot.TimeUtc, "InvalidSnapshot");
                foreach (NetworkConfigurationEntry entry in snapshot.Entries)
                {
                    if (entry == null || String.IsNullOrEmpty(entry.Key) || !IsSetting(entry.Kind) || current.ContainsKey(entry.Key)) return Unavailable(snapshot.TimeUtc, "InvalidSnapshot");
                    string value = Normalize(entry.Kind, entry.Value);
                    string fingerprint = entry.Fingerprint;
                    if (entry.Kind == "ProxyServer" || entry.Kind == "ProxyPac")
                    {
                        if (fingerprint == null) fingerprint = NetworkChangeReader.Hash(value);
                        value = value == "Invalid" || value == "Configured" ? value : NetworkChangeReader.SanitizeProxy(value, entry.Kind == "ProxyPac");
                    }
                    current.Add(entry.Key, new NetworkConfigurationEntry { Key = entry.Key, Kind = entry.Kind, AdapterId = entry.AdapterId, AdapterName = entry.AdapterName, Value = value, Fingerprint = fingerprint });
                }
                unavailable = null;
                List<NetworkChangeEvent> changes = new List<NetworkChangeEvent>();
                if (baseline != null)
                {
                    foreach (string key in baseline.Keys.Union(current.Keys).OrderBy(delegate(string s) { return s; }, StringComparer.Ordinal))
                    {
                        NetworkConfigurationEntry before, after;
                        baseline.TryGetValue(key, out before); current.TryGetValue(key, out after);
                        if (before != null && after != null && before.Kind == after.Kind && before.Value == after.Value && before.Fingerprint == after.Fingerprint) continue;
                        NetworkConfigurationEntry setting = after ?? before;
                        changes.Add(Append(new NetworkChangeEvent { Kind = "SettingChanged", TimeUtc = snapshot.TimeUtc, TargetId = setting.AdapterId, AdapterName = setting.AdapterName, Setting = setting.Kind,
                            Before = before == null ? "" : before.Value, After = after == null ? "" : after.Value }));
                    }
                }
                baseline = current;
                return changes.ToArray();
            }
        }

        public NetworkChangeEvent[] ObserveSample(MonitorSample sample)
        {
            if (sample == null) throw new ArgumentNullException("sample");
            lock (gate)
            {
                string key = (sample.TargetId ?? "") + "\0" + (sample.TargetHost ?? "");
                bool responded = String.Equals(sample.Method, "HTTP", StringComparison.OrdinalIgnoreCase) || String.Equals(sample.Method, "HTTPS", StringComparison.OrdinalIgnoreCase) ? sample.ResponseReceived || sample.Success : sample.Success;
                bool previous;
                bool known = targets.TryGetValue(key, out previous);
                targets[key] = responded;
                if ((known && previous == responded) || (!known && responded)) return new NetworkChangeEvent[0];
                return new[] { Append(new NetworkChangeEvent { Kind = responded ? "TargetRecovered" : "TargetNoResponse", TimeUtc = sample.TimeUtc, TargetId = sample.TargetId, TargetHost = sample.TargetHost,
                    ErrorCode = responded ? null : String.Equals(sample.Method, "ICMP", StringComparison.OrdinalIgnoreCase) ? "NoIcmpResponse" : "NoResponse" }) };
            }
        }
    }
}
