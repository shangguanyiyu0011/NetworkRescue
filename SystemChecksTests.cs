using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Threading;

namespace NetworkRescue
{
    internal static class SystemChecksTests
    {
        private static int assertions;
        private static void Assert(bool value, string message)
        {
            assertions++;
            if (!value) throw new Exception("FAIL: " + message);
        }

        private static CatalogEntry Entry(uint id, int length, params uint[] chain)
        {
            return new CatalogEntry { Id = id, ChainLength = length, Chain = chain };
        }

        private static string Value(DiagnosticItem item, string key)
        {
            return item.EvidenceGroups.SelectMany(delegate(DiagnosticEvidenceGroup group) { return group.Fields; }).First(delegate(DiagnosticEvidenceField field) { return field.LabelKey == key; }).Value;
        }

        private static DiagnosticItem Unreadable(string id)
        {
            return (DiagnosticItem)typeof(DiagnosticService).GetMethod("Unknown", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { id, id, new System.TimeoutException() });
        }

        private static void StructuredEvidenceChecks()
        {
            Assert(DiagnosticEvidence.Group(null, "网络硬件配置", null).Title == "网络硬件配置", "literal title does not become a localization lookup");
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                Probe loopback = RescueEngine.TcpProbe("127.0.0.1", port);
                Assert(loopback.Success && loopback.Method == "TCP" && loopback.Endpoint == "127.0.0.1:" + port, "real loopback probe records typed endpoint and method");
            }
            finally { listener.Stop(); }
            const string raw = "translated log must remain raw";
            Snapshot snapshot = new Snapshot { Proxy = new ProxySnapshot { Readable = true } };
            snapshot.Adapters.Add(new AdapterSnapshot { Name = "literal <adapter>", Up = true, Dhcp = true, InformationReadable = true, Addresses = new List<string> { "192.0.2.2" }, Gateways = new List<string> { "192.0.2.1" }, AllDns = new List<string> { "192.0.2.53" } });
            snapshot.GatewayProbes.Add(new Probe { Label = "192.0.2.1 ICMP", Success = true, Detail = raw });
            snapshot.Tcp.Add(new Probe { Label = "192.0.2.3:443", Success = true, Detail = raw });
            snapshot.Dns.Add(new Probe { Label = "fixture.invalid", Success = true, ResolvedAddressCount = 2, Detail = raw });
            snapshot.Http.Add(new Probe { Label = "HTTP fixture", Endpoint = "https://fixture.invalid/", Success = true, Responded = true, HttpStatus = 204, Marker = true, Detail = raw });
            HardwareSnapshot hardware = new HardwareSnapshot();
            hardware.Adapters.Add(new HardwareEvidence { Name = "hardware fixture", Type = "Ethernet", Up = true, UsableAddress = true, Dhcp = true, DeviceError = 0, Physical = true, Enabled = true });
            hardware.Adapters.Add(new HardwareEvidence { Name = "disconnected TAP", Up = false, Physical = false, DeviceError = 31 });
            DiagnosticItem catalog = DiagnosticService.BuildCatalogItem("catalog fixture", false, 7, new List<string>(), 3, 0, 0);
            DiagnosticItem winsock = DiagnosticService.BuildWinsock(new List<DiagnosticItem> { catalog });
            DiagnosticReport report = DiagnosticService.Compose(RescueEngine.BuildReport(snapshot), DiagnosticService.BuildHardware(hardware), DiagnosticService.BuildDhcp(hardware, ServiceControllerStatus.Running), DiagnosticService.BuildHosts(DiagnosticService.ParseHosts(new[] { "192.0.2.7 private.invalid alias.invalid # secret comment" })), winsock, hardware);
            Assert(report.Items.Count == 8 && report.Items.All(delegate(DiagnosticItem item) { return item.State == CheckState.Passed; }), "all eight normal checks keep their judgments");
            foreach (DiagnosticItem item in report.Items)
            {
                Assert(item.EvidenceGroups.Count > 0 && item.EvidenceGroups.All(delegate(DiagnosticEvidenceGroup group) { return group.Fields.Count > 0; }), item.Id + " has grouped fields");
                Assert(item.EvidenceGroups.SelectMany(delegate(DiagnosticEvidenceGroup group) { return group.Fields; }).All(delegate(DiagnosticEvidenceField field) { return field.Value != raw; }), item.Id + " does not parse translated raw log");
            }
            DiagnosticItem connection = report.Items[1];
            Assert(connection.EvidenceGroups.Count == 3, "connection merges adapter, gateway and direct TCP groups");
            Assert(connection.EvidenceGroups[0].TitleKey == null && connection.EvidenceGroups[0].Title == "literal <adapter>", "adapter identity is literal, never a translation key");
            Assert(Value(connection, "adapterIpv4") == "192.0.2.2" && Value(connection, "adapterGateways") == "192.0.2.1", "connection fields come from adapter snapshot");
            Assert(Value(report.Items[3], "adapterDns") == "192.0.2.53" && Value(report.Items[3], "evidenceResolvedAddresses") == "2", "DNS uses configured servers and typed resolver count");
            Assert(Value(report.Items[4], "evidenceCustomLines") == "1" && Value(report.Items[4], "evidenceCustomMappings") == "2", "hosts fields use parsed counters");
            Assert(!String.Join(" ", report.Items[4].EvidenceGroups.SelectMany(delegate(DiagnosticEvidenceGroup group) { return group.Fields; }).Select(delegate(DiagnosticEvidenceField field) { return field.Value; })).Contains("private.invalid"), "structured hosts does not reveal arbitrary mappings");
            Assert(Value(report.Items[2], "statusColumn") == "Running" && Value(report.Items[2], "evidenceDhcpAdapters") == "1", "DHCP fields use actual service status and dependency count");
            Assert(Value(winsock, "evidenceProtocolEntries") == "7" && Value(winsock, "evidenceDllPresent") == "3", "winsock keeps catalog statistics through aggregation");
            Assert(winsock.EvidenceGroups[0] == catalog.EvidenceGroups[0], "winsock retains original catalog evidence group");
            Assert(Value(report.Items[7], "evidenceHttpStatus") == "204" && Value(report.Items[7], "hostLabel") == "https://fixture.invalid/", "HTTP fields use typed status and endpoint");
            Assert(report.Items[0].EvidenceGroups[1].State == CheckState.Unknown, "unconnected virtual adapter is not a fault");
            Assert(report.ToPlainText().Contains(raw) && !report.ToPlainText().Contains("evidenceProtocolEntries"), "plain text retains original logs without DTO keys");

            snapshot.Proxy.Enabled = true;
            snapshot.Proxy.Pac = "https://user:secret@fixture.invalid/private-token?secret=yes";
            snapshot.Proxy.Server = "http://user:secret@127.0.0.1:12345";
            snapshot.Proxy.Endpoints.Add(new Probe { Label = "127.0.0.1:12345", Uncertain = true, Detail = raw });
            DiagnosticItem proxy = RescueEngine.BuildReport(snapshot).Items.First(delegate(DiagnosticItem item) { return item.Id == "Proxy"; });
            string proxyValues = String.Join(" ", proxy.EvidenceGroups.SelectMany(delegate(DiagnosticEvidenceGroup group) { return group.Fields; }).Select(delegate(DiagnosticEvidenceField field) { return field.Value; }));
            Assert(proxyValues.Contains("https://fixture.invalid/") && !proxyValues.Contains("secret") && !proxyValues.Contains("private-token"), "structured proxy redacts credentials, PAC path and query");
            Assert(proxy.EvidenceGroups.Last().State == CheckState.Unknown, "uncertain proxy endpoint does not pass");

            Snapshot unknown = new Snapshot { AdapterError = "FixtureReadError" };
            unknown.Http.Add(new Probe { Label = "skipped HTTP", AutomaticProxySkipped = true, Uncertain = true, Detail = raw });
            DiagnosticReport unknownReport = DiagnosticService.Compose(RescueEngine.BuildReport(unknown), DiagnosticService.BuildHardware(new HardwareSnapshot()), DiagnosticService.BuildDhcp(null, ServiceControllerStatus.Running), Unreadable("Hosts"), DiagnosticService.BuildWinsock(new List<DiagnosticItem> { Unreadable("Catalog") }), null);
            foreach (DiagnosticItem item in unknownReport.Items)
            {
                Assert(item.State == CheckState.Unknown, item.Id + " unknown check does not invent success");
                Assert(item.EvidenceGroups.Count > 0 && item.EvidenceGroups.All(delegate(DiagnosticEvidenceGroup group) { return group.State != CheckState.Passed; }), item.Id + " unknown evidence does not invent success");
            }
            Assert(Value(unknownReport.Items[2], "evidenceDhcpAdapters") == I18n.T("未知"), "missing DHCP dependency snapshot is unknown, not zero");
            Assert(DiagnosticService.BuildCatalogItem("unreadable DLL", false, 3, new List<string>(), 1, 0, 2).State == CheckState.Unknown, "unreadable provider path remains unknown");
            Assert(DiagnosticService.BuildCatalogItem("missing DLL", false, 3, new List<string>(), 1, 1, 0).State == CheckState.Warning, "missing DLL preserves original warning");
        }

        public static int Main(string[] args)
        {
            try
            {
                StructuredEvidenceChecks();
                HostsEvidence normal = DiagnosticService.ParseHosts(new[] { "# comment", "127.0.0.1 localhost", "::1 localhost localhost.localdomain" });
                Assert(normal.CustomMappings == 0 && normal.InvalidLines == 0, "standard localhost ignored");
                Assert(DiagnosticService.BuildHosts(normal).State == CheckState.Passed, "normal hosts passes");
                HostsEvidence custom = DiagnosticService.ParseHosts(new[] { "192.168.1.2 dev.local alias.local # private comment", "127.0.0.1 ads.example", "::1 local-test" });
                Assert(custom.CustomLines == 3 && custom.CustomMappings == 4, "count custom lines and aliases");
                DiagnosticItem customItem = DiagnosticService.BuildHosts(custom);
                Assert(customItem.State == CheckState.Passed, "custom hosts is not evidence of malware");
                Assert(!customItem.Detail.Contains("dev.local") && !customItem.Detail.Contains("private comment"), "hosts report hides arbitrary domains and comments");
                HostsEvidence covered = DiagnosticService.ParseHosts(new[] { "0.0.0.0 WWW.BAIDU.COM.", "::1 www.microsoft.com www.cloudflare.com" });
                Assert(covered.Covered.Count == 3 && DiagnosticService.BuildHosts(covered).State == CheckState.Warning, "test domain coverage recorded case insensitively");
                HostsEvidence invalid = DiagnosticService.ParseHosts(new[] { "invalid", "999.1.1.1 bad.example", "1.2.3.4 https://user:pass@example.com", "1.2.3.4 valid.local bad/name", "127.1 localhost" });
                Assert(invalid.InvalidLines == 5 && invalid.CustomMappings == 1, "invalid address/host/line counts and valid alias retained");
                Assert(!DiagnosticService.BuildHosts(invalid).Detail.Contains("pass"), "invalid input never echoed");
                HostsEvidence remap = DiagnosticService.ParseHosts(new[] { "192.168.1.5 localhost" });
                Assert(remap.CustomMappings == 1, "non-loopback localhost not silently ignored");

                Func<string, DriveType> localDrive = delegate(string root) { return DriveType.Fixed; };
                Func<string, FileAttributes> normalPath = delegate(string path) { return path.EndsWith("hosts", StringComparison.Ordinal) ? FileAttributes.Normal : FileAttributes.Directory; };
                string localHosts = @"C:\Windows\System32\drivers\etc\hosts";
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, normalPath) == localHosts, "local absolute hosts accepted");
                foreach (string path in new[] { @"\\server\share\hosts", @"\\?\UNC\server\share\hosts", @"\\?\C:\Windows\hosts", @"\\.\C:\Windows\hosts", @"Windows\hosts", @"C:hosts", @"\Windows\hosts", @"C:\", @"C:\Windows\hosts:secret" })
                    Assert(DiagnosticService.LocalHostsPath(path, delegate(string root) { throw new Exception("must not inspect drive"); }, delegate(string file) { throw new Exception("must not inspect file"); }) == null, "nonlocal/ambiguous syntax rejected before file access");
                Assert(DiagnosticService.LocalHostsPath(localHosts, delegate(string root) { return DriveType.Network; }, delegate(string file) { throw new Exception("must not touch network drive"); }) == null, "mapped network drive rejected without file access");
                List<string> visited = new List<string>();
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, delegate(string path) { visited.Add(path); return path == @"C:\Windows" ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory; }) == null, "reparse parent rejected");
                Assert(visited.Count == 2 && !visited.Contains(localHosts), "stop before traversing reparse parent");
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, delegate(string path) { return path == localHosts ? FileAttributes.ReparsePoint : FileAttributes.Directory; }) == null, "reparse hosts file rejected");
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, delegate(string path) { return path == @"C:\" ? FileAttributes.Directory | FileAttributes.ReparsePoint : FileAttributes.Directory; }) == null, "reparse drive root rejected");
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, delegate(string path) { if (path == localHosts) throw new FileNotFoundException(); return FileAttributes.Directory; }) == localHosts, "missing local hosts remains eligible for normal missing-file result");
                Assert(DiagnosticService.LocalHostsPath(localHosts, localDrive, delegate(string path) { throw new UnauthorizedAccessException(); }) == null, "unreadable path cannot be trusted local");

                Assert(DiagnosticService.IsUsableAddress(IPAddress.Parse("2001:db8::42")), "global IPv6 considered");
                Assert(DiagnosticService.IsUsableAddress(IPAddress.Parse("fd00::42")), "IPv6 local network considered without claiming internet");
                Assert(!DiagnosticService.IsUsableAddress(IPAddress.Parse("fe80::1")), "link-local IPv6 is not usable routed address evidence");
                Assert(!DiagnosticService.IsUsableAddress(IPAddress.Parse("::1")) && !DiagnosticService.IsUsableAddress(IPAddress.Parse("169.254.1.2")), "loopback and APIPA excluded");
                HardwareSnapshot hardware = new HardwareSnapshot();
                hardware.Adapters.Add(new HardwareEvidence { Name = "IPv6 active", Up = true, UsableAddress = true, Dhcp = false, Physical = true, DeviceError = 0 });
                hardware.Adapters.Add(new HardwareEvidence { Name = "TAP disconnected", Up = false, Physical = false, DeviceError = 31 });
                hardware.Adapters.Add(new HardwareEvidence { Name = "Spare disabled", Up = false, Physical = true, DeviceError = 22 });
                Assert(DiagnosticService.BuildHardware(hardware).State == CheckState.Passed, "active IPv6 plus disconnected virtual/disabled spare passes");
                Assert(DiagnosticService.BuildDhcp(hardware, ServiceControllerStatus.Stopped).State == CheckState.Passed, "static IP does not fail stopped DHCP");
                hardware.Adapters[0].Dhcp = true;
                Assert(DiagnosticService.BuildDhcp(hardware, ServiceControllerStatus.Stopped).State == CheckState.Warning, "active DHCP and stopped service warns");
                Assert(DiagnosticService.BuildDhcp(hardware, ServiceControllerStatus.Running).State == CheckState.Passed, "running DHCP passes");
                Assert(DiagnosticService.BuildDhcp(null, ServiceControllerStatus.Running).State == CheckState.Unknown, "unknown adapters remain unknown");
                hardware.Adapters[0].Up = false;
                hardware.Adapters[0].DeviceError = 10;
                Assert(DiagnosticService.BuildHardware(hardware).State == CheckState.Failed, "physical device error without working link reported");

                Assert(Marshal.SizeOf(typeof(DiagnosticService.ProtocolInfo)) == 628, "WinAPI WSAPROTOCOL_INFOW structure size");
                List<CatalogEntry> catalog = new List<CatalogEntry> { Entry(1, 0), Entry(2, 1), Entry(3, 2, 1, 2) };
                Assert(DiagnosticService.ValidateChains(catalog).Count == 0, "valid LSP/base chain accepted");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(2, 1) }).Count == 0, "base does not need explicit chain self-reference");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(1, 0), Entry(3, 2, 1, 99) }).Count > 0, "dangling chain detected");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(1, 0), Entry(3, 2, 1, 1) }).Count > 0, "duplicate layer/non-base endpoint detected");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(1, 0), Entry(2, 1), Entry(3, 2, 2, 1) }).Count > 0, "reversed layer/base detected");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(3, 8) }).Count > 0, "oversized chain rejected");
                Assert(DiagnosticService.ValidateChains(new[] { Entry(1, 1), Entry(1, 1) }).Count > 0, "duplicate catalog ids rejected");
                Assert(DiagnosticService.ResolveProviderPath(@"\\server\share\provider.dll", false) == null, "no network file probing");
                Assert(DiagnosticService.ResolveProviderPath("provider.dll", false) == null, "relative provider path remains unknown");
                if (Environment.Is64BitProcess)
                {
                    string path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "mswsock.dll");
                    Assert(DiagnosticService.ResolveProviderPath(path, true).IndexOf("SysWOW64", StringComparison.OrdinalIgnoreCase) >= 0, "32-bit provider checks redirected to 32-bit file");
                    Assert(DiagnosticService.ResolveProviderPath(path, false) == path, "native provider keeps native path");
                }

                DiagnosticReport original = new DiagnosticReport { Summary = "preserved", InternetAvailable = true, Timestamp = DateTime.Now };
                RepairAction renew = new RepairAction { Id = "RenewDhcp", AdapterId = "test" };
                original.Items.Add(new DiagnosticItem { Id = "Adapters", Title = "old", Detail = "IPv4 evidence", State = CheckState.Warning, Actions = new List<RepairAction> { renew } });
                original.Items.Add(new DiagnosticItem { Id = "Gateway", Title = "gateway", Detail = "gateway evidence", State = CheckState.Unknown });
                original.Items.Add(new DiagnosticItem { Id = "DirectTcp", Title = "direct", Detail = "direct evidence", State = CheckState.Passed });
                original.Items.Add(new DiagnosticItem { Id = "Dns", Detail = "DNS evidence" });
                original.Items.Add(new DiagnosticItem { Id = "Proxy", Detail = "proxy evidence" });
                original.Items.Add(new DiagnosticItem { Id = "Http", Detail = "HTTP evidence" });
                DiagnosticEvidenceGroup originalGroup = DiagnosticEvidence.Group("connection", "source group", CheckState.Warning, DiagnosticEvidence.Field("adapterIpv4", "IPv4 地址", "192.0.2.5"));
                original.Items[0].EvidenceGroups.Add(originalGroup);
                DiagnosticReport composed = DiagnosticService.Compose(original, new DiagnosticItem { Id = "Hardware" }, new DiagnosticItem { Id = "DhcpService" }, new DiagnosticItem { Id = "Hosts" }, new DiagnosticItem { Id = "Winsock" }, hardware);
                Assert(composed.Items.Count == 8, "exactly eight checks");
                Assert(String.Join(",", composed.Items.Select(delegate(DiagnosticItem item) { return item.Id; })) == "Hardware,Connection,DhcpService,Dns,Hosts,Proxy,Winsock,Http", "required order");
                Assert(composed.Items[1].Actions.Contains(renew), "merged connection retains original repair action");
                Assert(composed.Items[1].EvidenceGroups.Contains(originalGroup) && composed.Items[1].State == CheckState.Passed && originalGroup.State == CheckState.Warning, "compose keeps original grouped evidence and its own source judgment");
                Assert(composed.Items[1].Detail.Contains("IPv4 evidence") && composed.Items[1].Detail.Contains("gateway evidence") && composed.Items[1].Detail.Contains("direct evidence"), "connection preserves all source evidence");
                Assert(composed.Summary == "preserved" && composed.InternetAvailable, "summary and online preserved");
                Assert(composed.Items[5].Detail.Contains("未检查所有浏览器"), "browser scope explicit");
                CancellationTokenSource cancel = new CancellationTokenSource();
                cancel.Cancel();
                bool canceled = false;
                try { DiagnosticService.DiagnoseAsync(new RescueEngine(), null, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { canceled = true; }
                Assert(canceled, "pre-cancelled diagnostic exits without probing");
                Console.WriteLine("System checks: " + assertions + " assertions passed.");
                if (args.Contains("--live"))
                {
                    DiagnosticReport live = DiagnosticService.DiagnoseAsync(new RescueEngine(), null, CancellationToken.None).GetAwaiter().GetResult();
                    Assert(live.Items.Count == 8, "live eight checks");
                    Console.WriteLine(live.ToPlainText());
                }
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
    }
}
