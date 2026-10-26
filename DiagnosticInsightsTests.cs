using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    internal static class DiagnosticInsightsTests
    {
        private static int count;
        private static void Check(bool condition, string message) { count++; if (!condition) throw new Exception(message); }
        private static Snapshot Sample(bool online)
        {
            Snapshot s = new Snapshot { Proxy = new ProxySnapshot { Readable = true } };
            s.Adapters.Add(new AdapterSnapshot { Id = Guid.NewGuid().ToString("D"), Index = 7, IPv6Index = 7, Up = true, InformationReadable = true, Dhcp = true, DnsConfigurationReadable = true, DnsAutomatic = false, Name = "fixture", Addresses = new List<string> { "192.168.1.8" }, Dns = new List<string> { "192.168.1.1" }, ConfiguredDns = new List<string> { "192.168.1.1" } });
            for (int i = 0; i < 3; i++)
            {
                s.Http.Add(new Probe { Endpoint = i == 0 ? "http://www.msftconnecttest.com/connecttest.txt" : "https://example.test/", Marker = i < 2, Success = online });
                s.Dns.Add(new Probe { Success = true });
                s.Tcp.Add(new Probe { Success = online });
            }
            return s;
        }
        private static WifiInsight Connected(uint signal)
        {
            return new WifiInsight { Readable = true, Total = 1, Interfaces = new List<WifiEvidence> { new WifiEvidence { State = 1, AttributesRead = true, Signal = signal, Name = "fixture", Ssid = "test-only", RxKbps = 144000, TxKbps = 144000 } } };
        }
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--native-readonly")
                {
                    InsightSnapshot native = DiagnosticInsights.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
                    Console.WriteLine("Native read-only: WinHTTP=" + native.Proxy.DefaultRead + "; userProxy=" + native.Proxy.UserRead + "; routes=" + native.Routes.Readable + "/" + native.Routes.Total + "; WLAN=" + native.Wifi.Readable + "/" + native.Wifi.Interfaces.Count + "; queried=" + native.Wifi.Interfaces.Count(x => x.AttributesRead) + "; errors=" + native.Routes.Error + "/" + native.Wifi.Error + "; process=" + (Environment.Is64BitProcess ? "x64" : "x86"));
                    return 0;
                }
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.SockAddress)) == 28, "SOCKADDR_INET layout");
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.Prefix)) == 32, "IP_ADDRESS_PREFIX layout");
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.RouteRow)) == 104, "MIB_IPFORWARD_ROW2 layout");
                Check((int)Marshal.OffsetOf(typeof(DiagnosticInsights.RouteRow), "Destination") == 12 && (int)Marshal.OffsetOf(typeof(DiagnosticInsights.RouteRow), "NextHop") == 44 && (int)Marshal.OffsetOf(typeof(DiagnosticInsights.RouteRow), "Metric") == 84, "Route offsets match native x86/x64 ABI");
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.WlanInterface)) == 532, "WLAN_INTERFACE_INFO layout");
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.WlanAssociation)) == 68, "WLAN_ASSOCIATION_ATTRIBUTES layout");
                Check(Marshal.SizeOf(typeof(DiagnosticInsights.WlanConnection)) == 604, "WLAN_CONNECTION_ATTRIBUTES layout");
                Check((int)Marshal.OffsetOf(typeof(DiagnosticInsights.WlanConnection), "Association") == 520, "WLAN connection association offset");
                Snapshot s = Sample(true);
                FaultConclusion healthy = DiagnosticInsights.Conclude(s, new RouteInsight(), null);
                Check(healthy.Category == "Healthy" && healthy.Confidence == "Confirmed", "Actual marker evidence can confirm current tested path");
                s.Http[2].Success = false;
                FaultConclusion site = DiagnosticInsights.Conclude(s, new RouteInsight(), null);
                Check(site.Category == "Site" && site.Confidence == "Uncertain" && site.Summary.Contains("固定端点"), "One failed endpoint among multiple successes does not mean whole site or Internet failure");
                s = Sample(false);
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category == "Unknown", "No gateway response does not prove broken router or ISP");
                s.GatewayProbes.Add(new Probe { Success = true });
                FaultConclusion upstream = DiagnosticInsights.Conclude(s, new RouteInsight(), null);
                Check(upstream.Category == "Upstream" && upstream.Confidence == "Likely" && upstream.Summary.Contains("不能确认运营商"), "Gateway response plus multi-public failures only suggests upstream path");
                s.Tcp[0].Uncertain = true;
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category != "Upstream", "Uncertain TCP probes do not establish an upstream failure");
                s.Tcp[0].Uncertain = false; s.Http[0].Responded = true; s.Http[0].HttpStatus = 407;
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category != "Upstream", "A proxy HTTP error response is not evidence of a disconnected upstream");
                s.Http[0].Responded = false;
                s.Tcp.RemoveRange(1, 2); s.Http.RemoveRange(1, 2);
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Confidence == "Uncertain", "Sparse public evidence remains uncertain");
                s = Sample(false); s.Tcp[0].Success = true; s.Dns.ForEach(x => x.Success = false);
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category == "Unknown", "IP works but DNS fails does not prove local configuration is the cause");
                s = Sample(false); s.Adapters[0].Addresses = new List<string> { "169.254.9.2" };
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category == "LocalNetwork", "APIPA with no alternate usable address identifies local address acquisition issue");
                s = Sample(false); s.Proxy.Enabled = true; s.Proxy.Server = "127.0.0.1:10808"; s.Proxy.Endpoints.Add(new Probe()); s.DirectHttp.Add(new Probe { Success = true });
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Category == "LocalConfiguration" && DiagnosticInsights.Conclude(s, new RouteInsight(), null).Confidence == "Confirmed", "Only established stale-proxy chain is confirmed local configuration");
                s.Proxy.Endpoints[0].Success = true;
                Check(DiagnosticInsights.Conclude(s, new RouteInsight(), null).Confidence != "Confirmed", "Running VPN proxy is not confirmed faulty");
                s = Sample(false); s.Proxy.Pac = "https://user:secret@pac.example/private?token=secret"; s.DirectHttp.Add(new Probe { Success = true });
                s.Http.ForEach(x => { x.AutomaticProxySkipped = true; x.Uncertain = true; }); s.Dns.ForEach(x => x.Success = false); s.Tcp[0].Success = true;
                FaultConclusion pac = DiagnosticInsights.Conclude(s, new RouteInsight(), null);
                Check(pac.Category == "Unknown" && pac.Confidence == "Uncertain" && pac.Summary.Contains("不表示"), "Working or unknown PAC is unverified, not offline or broken");
                DiagnosticReport pacReport = RescueEngine.BuildReport(s);
                Check(!pacReport.Items.SelectMany(x => x.Actions).Any(x => x.Id == "DisableProxy" || x.Id == "AutoDns"), "PAC uncertainty never proposes disabling proxy or auto-DNS");
                Check(pacReport.Items.Single(x => x.Id == "Http").State == CheckState.Unknown && pacReport.Summary.Contains("不能据此判断断网"), "Skipped automatic HTTP is Unknown rather than Failed");
                SpecialDiagnostic pacSpecial = DiagnosticInsights.Pac(s, new ProxyInsight());
                Check(!pacSpecial.Detail.Contains("secret") && !pacSpecial.Detail.Contains("private") && pacSpecial.State == CheckState.Unknown, "PAC credential/path redaction and non-execution limitation");
                s.Proxy.Pac = "javascript:alert(1)";
                Check(DiagnosticInsights.Pac(s, new ProxyInsight()).State == CheckState.Warning, "Unsupported PAC scheme is warned about without executing it");
                Check(DiagnosticInsights.Wifi(Sample(true), new WifiInsight { Readable = true }).State == CheckState.Unknown, "No WLAN is not an Internet failure");
                Check(DiagnosticInsights.Wifi(Sample(true), new WifiInsight { Readable = true, Interfaces = new List<WifiEvidence> { new WifiEvidence { State = 4 } } }).State == CheckState.Unknown, "Disconnected Wi-Fi with another connection is not a fault");
                Check(DiagnosticInsights.Wifi(Sample(true), Connected(20)).State == CheckState.Warning, "Low measured Wi-Fi quality produces a bounded heuristic warning");
                Check(DiagnosticInsights.Wifi(Sample(true), Connected(80)).State == CheckState.Passed, "Connected Wi-Fi plus successful HTTP is supported by evidence");
                foreach (string ssid in new[] { "private,project", "private;project", "private|project" })
                {
                    WifiInsight isolated = Connected(80);
                    isolated.Interfaces[0].Ssid = ssid;
                    string wifiDetail = DiagnosticInsights.Wifi(Sample(true), isolated).Detail;
                    Check(wifiDetail.Split(new[] { Environment.NewLine }, StringSplitOptions.None).Contains("SSID=" + ssid), "SSID occupies an isolated line without treating punctuation as delimiters");
                    Check(wifiDetail.Contains(Environment.NewLine + I18n.T(" | 信号：") + "80%"), "signal and rate evidence is outside the sensitive SSID line");
                }
                Check(DiagnosticInsights.Wifi(Sample(false), Connected(80)).Summary.Contains("尚未确认"), "Associated Wi-Fi alone does not claim public connectivity");
                Check(DiagnosticInsights.Wifi(s, Connected(80)).State == CheckState.Unknown, "PAC skip does not classify Wi-Fi as broken");
                Snapshot portal = Sample(false); portal.Http[0].Redirected = true;
                Check(DiagnosticInsights.PortalSuspected(portal), "Observable HTTP marker redirect is a possible portal signal");
                Check(DiagnosticInsights.Wifi(portal, Connected(80)).Summary.Contains("可能"), "Portal finding is qualified rather than asserted");
                portal.Http[0].Redirected = false; portal.Http[0].ContentMismatch = true;
                Check(DiagnosticInsights.PortalSuspected(portal), "Observed marker content interception is a possible portal signal");
                portal.Http[1].Success = true;
                Check(!DiagnosticInsights.PortalSuspected(portal), "Another successful marker prevents overcalling a portal");
                var row = new DiagnosticInsights.RouteRow { Index = 7, Destination = new DiagnosticInsights.Prefix { Address = new DiagnosticInsights.SockAddress { Family = 2 }, Length = 0 }, NextHop = new DiagnosticInsights.SockAddress { Family = 2, V4 = BitConverter.ToUInt32(IPAddress.Parse("192.168.1.1").GetAddressBytes(), 0) }, Metric = 25 };
                RouteEvidence parsed = DiagnosticInsights.ParseRoute(row);
                Check(parsed.Destination == "0.0.0.0" && parsed.NextHop == "192.168.1.1" && parsed.Metric == 25, "Real route fields retain IPv4 byte order and interface evidence");
                row.Destination.Length = 24;
                Check(DiagnosticInsights.ParseRoute(row) == null, "Non-default non-split routes are not needlessly reported");
                row.Destination.Length = 0; row.Destination.Address = new DiagnosticInsights.SockAddress { Family = 23 }; row.NextHop = new DiagnosticInsights.SockAddress { Family = 23 };
                Check(DiagnosticInsights.ParseRoute(row).Destination == "::", "IPv6 default route parsing");
                RouteInsight routes = new RouteInsight { Readable = true, Routes = new List<RouteEvidence> { new RouteEvidence { InterfaceIndex = 9, PrefixLength = 0 } } };
                Check(DiagnosticInsights.RouteConcern(Sample(false), routes), "Actual default route referring to an unenumerated interface is a qualified concern");
                Check(!DiagnosticInsights.RouteConcern(Sample(true), routes), "A dormant route does not label a working VPN/network faulty");
                routes.Routes.Add(new RouteEvidence { InterfaceIndex = 7, PrefixLength = 0 });
                Check(!DiagnosticInsights.RouteConcern(Sample(false), routes), "Usable alternate default route prevents blaming inactive route");
                routes.Routes.RemoveAt(1); routes.Truncated = true;
                Check(!DiagnosticInsights.RouteConcern(Sample(false), routes), "Truncated route evidence cannot conclude residue");
                var connection = new DiagnosticInsights.WlanConnection { State = 1, Association = new DiagnosticInsights.WlanAssociation { Ssid = new DiagnosticInsights.Dot11Ssid { Bytes = new byte[32], Length = 4 }, Bssid = new byte[6], Signal = 74, RxRate = 866000, TxRate = 433000 } };
                Buffer.BlockCopy(Encoding.UTF8.GetBytes("test"), 0, connection.Association.Ssid.Bytes, 0, 4);
                WifiEvidence wifi = DiagnosticInsights.ParseWifi(new DiagnosticInsights.WlanInterface { Description = "fixture", State = 1 }, connection);
                Check(wifi.Ssid == "test" && wifi.Signal == 74 && wifi.RxKbps == 866000, "WLAN native connection data is parsed into actual link metrics");
                var proxy = new ProxySnapshot { Readable = true, Enabled = true, Server = "http=user:secret@127.0.0.1:10808;https=127.0.0.1:10809", Override = "*.corp.example;<LOCAL>" };
                Check(RescueEngine.ManualProxyFor(new Uri("https://service.corp.example/"), proxy) == null, "Explicit Windows-style bypass rules are respected without PAC");
                Check(RescueEngine.ManualProxyFor(new Uri("http://router/"), proxy) == null, "Local-name bypass is case insensitive");
                WebProxy selected = (WebProxy)RescueEngine.ManualProxyFor(new Uri("https://www.example.test/"), proxy);
                Check(selected.Address.Port == 10809 && selected.Credentials == null, "Correct protocol proxy is selected without default credentials");
                selected = (WebProxy)RescueEngine.ManualProxyFor(new Uri("http://www.example.test/"), proxy);
                Check(selected.Address.Port == 10808 && selected.Address.UserInfo == "" && selected.Credentials == null, "Stored proxy credentials are not forwarded or reported");
                MethodInfo httpProbe = typeof(RescueEngine).GetMethod("HttpProbe", BindingFlags.Static | BindingFlags.NonPublic);
                proxy.Pac = "https://pac.example/autoconfig.js";
                Probe skipped = (Probe)httpProbe.Invoke(null, new object[] { "http://127.0.0.1:9/", "marker", true, true, proxy });
                Check(skipped.AutomaticProxySkipped && skipped.Uncertain && !skipped.Responded && skipped.HttpStatus == 0, "PAC-configured HTTP is skipped before any local request or script evaluation");
                proxy.Pac = ""; proxy.AutoDetect = true;
                skipped = (Probe)httpProbe.Invoke(null, new object[] { "http://127.0.0.1:9/", "marker", true, true, proxy });
                Check(skipped.AutomaticProxySkipped && !proxy.DefinitelyUnreachable, "WPAD mode is not evaluated or eligible for proxy-disable inference");
                DiagnosticReport report = RescueEngine.BuildReport(Sample(true));
                DiagnosticReport composed = DiagnosticService.Compose(report, new DiagnosticItem { Id = "Hardware" }, new DiagnosticItem { Id = "DhcpService" }, new DiagnosticItem { Id = "Hosts" }, new DiagnosticItem { Id = "Winsock" }, null);
                DiagnosticInsights.Apply(composed, new InsightSnapshot { Wifi = Connected(80), Routes = new RouteInsight { Readable = true } }, null);
                Check(composed.Items.Count == 8 && composed.SpecialChecks.Count == 6 && composed.Fault != null, "Typed sidecar insights preserve exactly eight core items");
                Check(composed.ToPlainText().Contains(composed.Fault.Title) && composed.ToPlainText().Contains("Wi-Fi"), "Export includes conclusions and special evidence");
                SpecialActionChecks();
                TypedFaultMetricChecks();
                ManualProxyWireCheck();
                Console.WriteLine("PASS: " + count + " diagnostic insight assertions; no public network probes or repairs.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }

        private static void TypedFaultMetricChecks()
        {
            Snapshot snapshot = Sample(false);
            snapshot.Http[0].Success = snapshot.Http[1].Success = true;
            snapshot.DirectHttp.AddRange(new[] { new Probe { Success = true }, new Probe() });
            snapshot.Tcp.Add(new Probe { Success = true });
            snapshot.Dns.Clear(); snapshot.Dns.AddRange(new[] { new Probe { Success = true }, new Probe() });
            snapshot.GatewayProbes.AddRange(new[] { new Probe { Success = true }, new Probe { Success = true }, new Probe() });
            FaultConclusion fault = DiagnosticInsights.Conclude(snapshot, new RouteInsight(), null);
            Check(fault.Metrics.Count == 5 && fault.Metrics.Select(x => x.Kind).SequenceEqual(new[] { "Http", "DirectHttp", "Tcp", "Dns", "Gateway" }), "Fault metrics use exactly five stable ordered protocol kinds");
            Check(fault.Metrics.Select(x => x.Succeeded).SequenceEqual(new[] { 2, 1, 1, 1, 2 }) && fault.Metrics.Select(x => x.Total).SequenceEqual(new[] { 3, 2, 4, 2, 3 }), "Typed counts preserve each protocol's raw successes and totals including failures");
            Check(fault.Category == "Site" && fault.Confidence == "Uncertain", "Failed probes retain the original partial endpoint conclusion");
            string[] evidence = {
                I18n.T("实际 HTTP 成功数：") + "2/3", I18n.T("绕过代理 ") + I18n.T("实际 HTTP 成功数：") + "1/2",
                I18n.T("公网 TCP 成功数：") + "1/4", I18n.T("系统 DNS 成功数：") + "1/2", I18n.T("网关协议应答数：") + "2/3"
            };
            Check(fault.Evidence.SequenceEqual(evidence), "Original translated evidence remains byte for byte unchanged");
            Check(fault.NextSteps.SequenceEqual(new[] { I18n.T("在高级工具中核对具体目标；留意地域限制、代理分流与端点自身策略。") }), "Typed metrics do not alter next steps");
            var serializer = new System.Web.Script.Serialization.JavaScriptSerializer();
            var packet = (Dictionary<string, object>)serializer.DeserializeObject(serializer.Serialize(fault));
            var metrics = (object[])packet["Metrics"];
            var http = (Dictionary<string, object>)metrics[0];
            Check(metrics.Length == 5 && (string)http["Kind"] == "Http" && (int)http["Succeeded"] == 2 && (int)http["Total"] == 3, "Public metric DTO serializes directly for the UI without parsing evidence text");
            DiagnosticReport report = RescueEngine.BuildReport(snapshot); report.Fault = fault;
            string plain = report.ToPlainText(); fault.Metrics.Clear();
            Check(report.ToPlainText() == plain, "Typed metrics do not change the plain text report");
            FaultConclusion untested = DiagnosticInsights.Conclude(new Snapshot(), new RouteInsight(), null);
            Check(untested.Metrics.Count == 5 && untested.Metrics.All(x => x.Succeeded == 0 && x.Total == 0) && untested.Category == "Unknown", "Untested protocols retain zero totals and an uncertain original conclusion");
            Check(DiagnosticInsights.Conclude(null, new RouteInsight(), null).Metrics.Count == 0, "Missing snapshots do not fabricate protocol measurements");
        }

        private static void SpecialActionChecks()
        {
            var flush = new RepairAction { Id = "FlushDns", Suggested = true };
            var automatic = new RepairAction { Id = "AutoDns", AdapterId = "fixture" };
            var report = new DiagnosticReport { Items = new System.Collections.Generic.List<DiagnosticItem> {
                new DiagnosticItem { Id = "Dns", Actions = new System.Collections.Generic.List<RepairAction> { flush, flush } },
                new DiagnosticItem { Id = "Connection", Actions = new System.Collections.Generic.List<RepairAction> { automatic, new RepairAction { Id = "RenewDhcp" } } },
                new DiagnosticItem { Id = "Proxy", Actions = new System.Collections.Generic.List<RepairAction> { new RepairAction { Id = "DisableProxy" } } }
            } };
            foreach (CheckState state in new[] { CheckState.Passed, CheckState.Unknown, CheckState.Warning, CheckState.Failed })
            {
                var special = new SpecialDiagnostic { Id = "VpnDns", State = state };
                report.SpecialChecks.Clear(); report.SpecialChecks.Add(special);
                DiagnosticInsights.LinkSpecialActions(report);
                Check(special.Actions.Count == (state == CheckState.Warning || state == CheckState.Failed ? 2 : 0), "Specialty repairs require warning/failure evidence and reuse only offered DNS actions");
                if (special.Actions.Count > 0) Check(special.Actions[0] == flush && special.Actions[1] == automatic, "Specialty actions preserve original native action identity and adapter target");
            }
            foreach (string id in new[] { "VpnPac", "VpnWinHttp", "VpnRoutes", "VpnAdapters", "Wifi" })
            {
                var special = new SpecialDiagnostic { Id = id, State = CheckState.Failed };
                special.Actions.Add(flush); report.SpecialChecks.Clear(); report.SpecialChecks.Add(special);
                DiagnosticInsights.LinkSpecialActions(report);
                Check(special.Actions.Count == 0, "Unsupported specialty cannot acquire a repair from another check: " + id);
            }
            Check(report.Items[0].Actions.Count == 2 && report.Items[1].Actions.Count == 2 && report.Items[2].Actions.Count == 1, "Specialty presentation cannot change main-check repair policy");
        }

        private static void ManualProxyWireCheck()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            string request = "";
            Task server = Task.Run(delegate
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 3000;
                    using (StreamReader reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null && line.Length > 0 && request.Length < 8192) request += line + "\r\n";
                    }
                    byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 7\r\nConnection: close\r\n\r\nmarker!");
                    stream.Write(response, 0, response.Length);
                }
            });
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                ProxySnapshot proxy = new ProxySnapshot { Readable = true, Enabled = true, Server = "http://user:secret@127.0.0.1:" + port };
                MethodInfo probe = typeof(RescueEngine).GetMethod("HttpProbe", BindingFlags.Static | BindingFlags.NonPublic);
                // The unspecified origin cannot send a public request, even if proxy selection regresses.
                Probe result = (Probe)probe.Invoke(null, new object[] { "http://0.0.0.0/proxy-fixture", "marker!", true, true, proxy });
                if (!server.Wait(4000)) throw new Exception("Local proxy fixture timed out");
                Check(result.Success && result.HttpStatus == 200, "Actual loopback proxy response is verified by marker and status");
                Check(request.StartsWith("GET http://0.0.0.0/proxy-fixture", StringComparison.Ordinal), "Explicit manual proxy is actually used without invoking PAC");
                Check(request.IndexOf("Proxy-Authorization", StringComparison.OrdinalIgnoreCase) < 0 && !request.Contains("secret"), "Wire request does not send stored/default proxy credentials");
            }
            finally { listener.Stop(); }
        }
    }
}
