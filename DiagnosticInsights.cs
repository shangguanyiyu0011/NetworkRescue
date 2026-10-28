using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NetworkRescue
{
    public sealed class FaultEvidenceMetric
    {
        public string Kind;
        public int Succeeded, Total;
    }

    public sealed class FaultConclusion
    {
        public string Category = "Unknown", Confidence = "Uncertain", Title = "", Summary = "";
        public List<string> Evidence = new List<string>(), NextSteps = new List<string>();
        public List<FaultEvidenceMetric> Metrics = new List<FaultEvidenceMetric>();
    }

    public sealed class SpecialDiagnostic
    {
        public string Id, Title, Summary, Detail;
        public CheckState State;
        public List<RepairAction> Actions = new List<RepairAction>();
    }

    internal sealed class ProxyInsight
    {
        internal bool DefaultRead, UserRead, AutoDetect;
        internal uint AccessType;
        internal string Server = "", Pac = "", Error;
    }

    internal sealed class RouteInsight
    {
        internal bool Readable, Truncated;
        internal string Error;
        internal int Total;
        internal List<RouteEvidence> Routes = new List<RouteEvidence>();
    }

    internal sealed class RouteEvidence
    {
        internal uint InterfaceIndex, Metric;
        internal int PrefixLength;
        internal string Destination, NextHop;
    }

    internal sealed class WifiInsight
    {
        internal bool Readable;
        internal string Error;
        internal int Total;
        internal List<WifiEvidence> Interfaces = new List<WifiEvidence>();
    }

    internal sealed class WifiEvidence
    {
        internal Guid Id;
        internal string Name, Ssid;
        internal uint State, Signal, RxKbps, TxKbps, QueryError;
        internal bool AttributesRead;
    }

    internal sealed class InsightSnapshot
    {
        internal ProxyInsight Proxy = new ProxyInsight();
        internal RouteInsight Routes = new RouteInsight();
        internal WifiInsight Wifi = new WifiInsight();
    }

    internal static class DiagnosticInsights
    {
        internal static async Task<InsightSnapshot> ReadAsync(CancellationToken token)
        {
            Task<ProxyInsight> proxy = Bounded(ReadProxy, new ProxyInsight { Error = "Timeout" }, token);
            Task<RouteInsight> routes = Bounded(ReadRoutes, new RouteInsight { Error = "Timeout" }, token);
            Task<WifiInsight> wifi = Bounded(ReadWifi, new WifiInsight { Error = "Timeout" }, token);
            await Task.WhenAll(proxy, routes, wifi).ConfigureAwait(false);
            return new InsightSnapshot { Proxy = proxy.Result, Routes = routes.Result, Wifi = wifi.Result };
        }

        private static async Task<T> Bounded<T>(Func<T> read, T fallback, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Task<T> work = Task.Run(read, token);
            Task completed = await Task.WhenAny(work, Task.Delay(2500, token)).ConfigureAwait(false);
            if (completed != work)
            {
                Observe(work);
                token.ThrowIfCancellationRequested();
                return fallback;
            }
            token.ThrowIfCancellationRequested();
            return await work.ConfigureAwait(false);
        }

        private static void Observe(Task work)
        {
            work.ContinueWith(delegate(Task failed) { var ignored = failed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        internal static void Apply(DiagnosticReport report, InsightSnapshot extra, HardwareSnapshot hardware)
        {
            Snapshot evidence = report.EvidenceSnapshot;
            report.Fault = Conclude(evidence, extra.Routes, hardware);
            report.SpecialChecks = new List<SpecialDiagnostic>
            {
                Pac(evidence, extra.Proxy), WinHttp(extra.Proxy), Dns(evidence), Routes(evidence, extra.Routes), Adapters(evidence, hardware), Wifi(evidence, extra.Wifi)
            };
            LinkSpecialActions(report);
        }

        internal static void LinkSpecialActions(DiagnosticReport report)
        {
            foreach (SpecialDiagnostic special in report.SpecialChecks)
            {
                special.Actions.Clear();
                if (special.Id != "VpnDns" || (special.State != CheckState.Warning && special.State != CheckState.Failed)) continue;
                // The specialty view may only reuse actions already offered by the current diagnosis.
                foreach (RepairAction action in report.Items.Where(x => x.Id == "Dns" || x.Id == "Connection").SelectMany(x => x.Actions).Where(x => x.Id == "FlushDns" || x.Id == "AutoDns"))
                    if (!special.Actions.Any(x => x.Id == action.Id && x.AdapterId == action.AdapterId)) special.Actions.Add(action);
            }
        }

        private static FaultConclusion Conclusion(string category, string confidence, string title, string summary, params string[] next)
        {
            return new FaultConclusion { Category = category, Confidence = confidence, Title = title, Summary = summary, NextSteps = next.ToList() };
        }

        internal static FaultConclusion Conclude(Snapshot snapshot, RouteInsight routes, HardwareSnapshot hardware)
        {
            if (snapshot == null)
                return Conclusion("Unknown", "Uncertain", I18n.T("定位证据不完整"), I18n.T("基础探测尚未完成，无法可靠区分本机、局域网或上游问题。"), I18n.T("重新诊断，并查看八项检查中的未确定项目。"));
            FaultConclusion fault;
            if (snapshot.Http.Any(delegate(Probe p) { return p.AutomaticProxySkipped; }))
                fault = Conclusion("Unknown", "Uncertain", I18n.T("系统自动代理路径未验证"), I18n.T("为了避免执行 PAC/WPAD，相关 HTTP 抽样已跳过；这不表示自动代理损坏或当前断网。") + (snapshot.DirectHttpWorks ? I18n.T("本次绕过代理的 HTTP 对照可用。") : I18n.T("直连对照尚未确认。")), I18n.T("在使用原自动代理的应用中核对连接；不要据此禁用 PAC 或重置 DNS。"));
            else if (RescueEngine.HasResidualLocalProxy(snapshot))
                fault = Conclusion("LocalConfiguration", "Confirmed", I18n.T("已定位到未运行的本地手动代理"), I18n.T("本地代理端口全部不可达，无 PAC；系统代理 HTTP 失败，而绕过代理的实际 HTTP 成功。这是当前用户代理配置阻碍这些请求的直接证据。"), I18n.T("先启动原 VPN/代理客户端；若需要恢复直连，可选择本次已有的关闭残留代理建议。"));
            else if (RescueEngine.HasResidualLoopbackDnsRisk(snapshot))
                fault = Conclusion("LocalConfiguration", "Likely", I18n.T("本地 DNS 转发路径疑似中断"), I18n.T("系统解析失败，活动 DNS 全部指向本机，TCP 53 与实际 UDP DNS 均未获得响应；客户端未启动或本地过滤仍需区分。"), I18n.T("启动原 VPN/DNS 客户端并正常断开退出后复检，不要盲目清理 DNS 或 TUN 路由。"));
            else if (snapshot.DnsFailed && (snapshot.Direct || snapshot.Online))
                fault = Conclusion("Unknown", "Uncertain", I18n.T("异常集中在系统 DNS 解析路径"), I18n.T("IP 连接或网页仍可用，但多个系统域名解析失败；尚不能区分本机配置、缓存、策略或上游 DNS 服务。"), I18n.T("核对自定义 DNS 与 VPN 的用途，依据本次已有建议逐项处理并复检。"));
            else if (snapshot.Online)
            {
                int good = snapshot.Http.Count(delegate(Probe p) { return p.Success; });
                bool partial = good >= 2 && snapshot.Http.Any(delegate(Probe p) { return !p.Success; });
                fault = partial
                    ? Conclusion("Site", "Uncertain", I18n.T("整体联网可用，部分测试端点未成功"), I18n.T("多数 HTTP 测试已成功，剩余失败只说明这些固定端点或当前路径的现象，不等于整个网站或全网故障。"), I18n.T("在高级工具中核对具体目标；留意地域限制、代理分流与端点自身策略。"))
                    : Conclusion("Healthy", snapshot.Http.Any(delegate(Probe p) { return p.Success && p.Marker; }) ? "Confirmed" : "Likely", I18n.T("当前测试已确认网页路径可用"), I18n.T("至少一个实际 HTTP 探测成功；结论只覆盖本次路径与端点，不保证所有网站和应用正常。"), I18n.T("若仅某个应用异常，检查其独立代理、DNS 或登录状态。"));
            }
            else if (snapshot.Adapters.Any(delegate(AdapterSnapshot a) { return a.Up && a.InformationReadable && a.Dhcp && a.Apipa; }) && !HasUsableAddress(snapshot, hardware))
                fault = Conclusion("LocalNetwork", "Likely", I18n.T("局域网地址获取尚未完成"), I18n.T("活动 DHCP 网卡只有 APIPA 地址，且未发现其他可用连接地址；可能涉及 Wi-Fi/网线、DHCP 服务器或本机客户端。"), I18n.T("先检查连接与路由器，再核对 DHCP 服务；只对本次提供的目标网卡考虑续租。"));
            else if (RouteConcern(snapshot, routes))
                fault = Conclusion("LocalConfiguration", "Likely", I18n.T("默认或分流路由需要进一步核对"), I18n.T("实际路由条目涉及未连接或未枚举的接口，且网页未验证；它也可能是暂未使用的配置，不能据此确定 VPN 残留。"), I18n.T("先通过原 VPN 客户端正常连接、断开并复检；不要凭网卡名称删除路由。"));
            else if (HasUsableAddress(snapshot, hardware) && snapshot.GatewayProbes.Any(delegate(Probe p) { return p.Success; }) && snapshot.Tcp.Count >= 2 && snapshot.Tcp.All(delegate(Probe p) { return !p.Success && !p.Uncertain; }) && snapshot.Http.Count >= 2 && snapshot.Http.All(delegate(Probe p) { return !p.Success && !p.Uncertain && !p.Responded; }))
                fault = Conclusion("Upstream", "Likely", I18n.T("上游或出口链路疑似受限"), I18n.T("本机有可用地址、网关至少一种协议有应答，但多个公网 IP 和 HTTP 测试失败；仍可能是 VPN、出口策略或代理上游，不能确认运营商故障。"), I18n.T("核对 VPN 与网络出口策略，并用其他设备或网络交叉验证后再联系网络提供方。"));
            else
                fault = Conclusion("Unknown", "Uncertain", I18n.T("尚不能可靠区分本地与出口问题"), I18n.T("现有地址、网关及公网证据不足；网关不回应 Ping 或未开放管理端口都不能单独证明路由器故障。"), I18n.T("结合连接状态、多个协议结果及其他设备交叉验证，保留未确定结论。"));
            fault.Evidence.Add(I18n.T("实际 HTTP 成功数：") + snapshot.Http.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.Http.Count);
            fault.Metrics.Add(new FaultEvidenceMetric { Kind = "Http", Succeeded = snapshot.Http.Count(delegate(Probe p) { return p.Success; }), Total = snapshot.Http.Count });
            fault.Evidence.Add(I18n.T("绕过代理 ") + I18n.T("实际 HTTP 成功数：") + snapshot.DirectHttp.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.DirectHttp.Count);
            fault.Metrics.Add(new FaultEvidenceMetric { Kind = "DirectHttp", Succeeded = snapshot.DirectHttp.Count(delegate(Probe p) { return p.Success; }), Total = snapshot.DirectHttp.Count });
            fault.Evidence.Add(I18n.T("公网 TCP 成功数：") + snapshot.Tcp.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.Tcp.Count);
            fault.Metrics.Add(new FaultEvidenceMetric { Kind = "Tcp", Succeeded = snapshot.Tcp.Count(delegate(Probe p) { return p.Success; }), Total = snapshot.Tcp.Count });
            fault.Evidence.Add(I18n.T("系统 DNS 成功数：") + snapshot.Dns.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.Dns.Count);
            fault.Metrics.Add(new FaultEvidenceMetric { Kind = "Dns", Succeeded = snapshot.Dns.Count(delegate(Probe p) { return p.Success; }), Total = snapshot.Dns.Count });
            fault.Evidence.Add(I18n.T("网关协议应答数：") + snapshot.GatewayProbes.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.GatewayProbes.Count);
            fault.Metrics.Add(new FaultEvidenceMetric { Kind = "Gateway", Succeeded = snapshot.GatewayProbes.Count(delegate(Probe p) { return p.Success; }), Total = snapshot.GatewayProbes.Count });
            return fault;
        }

        private static bool HasUsableAddress(Snapshot snapshot, HardwareSnapshot hardware)
        {
            return snapshot.Adapters.Any(delegate(AdapterSnapshot a) { return a.Up && a.UsableV4; }) || hardware != null && hardware.Adapters.Any(delegate(HardwareEvidence a) { return a.Up && a.UsableAddress; });
        }

        private static SpecialDiagnostic Special(string id, string title, CheckState state, string summary, string detail)
        {
            return new SpecialDiagnostic { Id = id, Title = title, State = state, Summary = summary, Detail = detail };
        }

        internal static SpecialDiagnostic Pac(Snapshot snapshot, ProxyInsight proxy)
        {
            string pac = proxy.UserRead ? proxy.Pac : snapshot == null ? "" : snapshot.Proxy.Pac;
            if (!proxy.UserRead && (snapshot == null || !snapshot.Proxy.Readable))
                return Special("VpnPac", I18n.T("VPN / PAC 专项"), CheckState.Unknown, I18n.T("当前用户自动代理配置未完整读取"), I18n.T("读取失败不等于配置异常。未执行 PAC 脚本，也未修改自动代理设置。"));
            if (String.IsNullOrWhiteSpace(pac))
                return Special("VpnPac", I18n.T("VPN / PAC 专项"), proxy.AutoDetect ? CheckState.Unknown : CheckState.Passed, proxy.AutoDetect ? I18n.T("已启用代理自动发现，未取得明确 PAC 地址") : I18n.T("未配置显式 PAC 地址"), I18n.T("只读配置与 URL 结构，不下载或执行 PAC；未配置显式地址不表示自动发现一定未使用代理。"));
            Uri uri;
            bool valid = Uri.TryCreate(pac, UriKind.Absolute, out uri) && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "file");
            return Special("VpnPac", I18n.T("VPN / PAC 专项"), valid ? CheckState.Unknown : CheckState.Warning, valid ? I18n.T("已取得 PAC 地址，脚本有效性尚未验证") : I18n.T("PAC 地址结构或协议未识别"), (valid ? SafeUri(uri) : I18n.T("原始地址已隐藏")) + Environment.NewLine + I18n.T("凭据、路径与查询参数不写入报告。结构有效不代表可达；不会执行 PAC、自动登录或删除配置。"));
        }

        private static string SafeUri(Uri uri)
        {
            return uri.Scheme == "file" ? "file://…" : uri.Scheme + "://" + uri.IdnHost + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + "/…";
        }

        private static SpecialDiagnostic WinHttp(ProxyInsight proxy)
        {
            if (!proxy.DefaultRead) return Special("VpnWinHttp", I18n.T("VPN / WinHTTP 专项"), CheckState.Unknown, I18n.T("WinHTTP 默认配置未完整读取"), I18n.T("只读 API 结果未确定：") + proxy.Error);
            List<Uri> endpoints = RescueEngine.ParseProxyEndpoints(proxy.Server);
            string detail = String.Join(Environment.NewLine, endpoints.Select(delegate(Uri uri) { return uri.DnsSafeHost + ":" + uri.Port; }));
            return Special("VpnWinHttp", I18n.T("VPN / WinHTTP 专项"), proxy.AccessType == 1 ? CheckState.Passed : CheckState.Unknown, proxy.AccessType == 1 ? I18n.T("WinHTTP 默认配置为直连") : I18n.T("WinHTTP 存在默认代理配置，未验证应用实际选路"), detail + Environment.NewLine + I18n.T("WinHTTP 默认配置与当前用户浏览器代理可以不同；应用会话也可覆盖默认值。未发送代理凭据、未执行 WinHTTP reset。"));
        }

        private static SpecialDiagnostic Dns(Snapshot snapshot)
        {
            if (snapshot == null) return Special("VpnDns", I18n.T("VPN / DNS 专项"), CheckState.Unknown, I18n.T("系统 DNS 证据尚未取得"), I18n.T("不据此更改 DNS 或 VPN 配置。"));
            bool residue = RescueEngine.HasResidualLoopbackDnsRisk(snapshot);
            string detail = String.Join(Environment.NewLine, snapshot.Adapters.Where(delegate(AdapterSnapshot a) { return a.Up; }).Take(16).Select(delegate(AdapterSnapshot a) { return Clean(a.Name) + ": " + String.Join(", ", a.AllDns.Count > 0 ? a.AllDns : a.Dns); }));
            return Special("VpnDns", I18n.T("VPN / DNS 专项"), residue ? CheckState.Warning : snapshot.DnsWorks ? CheckState.Passed : CheckState.Unknown, residue ? I18n.T("回环 DNS 与本地服务无响应的证据吻合") : snapshot.DnsWorks ? I18n.T("系统解析可用，未见可确认的 DNS 残留故障") : I18n.T("DNS 路径未确定，可能涉及客户端或上游"), detail + Environment.NewLine + I18n.T("回环或自定义 DNS 可以是有意配置；保留现有受条件约束的修复建议，不默认重置 DNS。"));
        }

        private static AdapterSnapshot RouteAdapter(Snapshot snapshot, uint index)
        {
            return snapshot == null || index == 0 ? null : snapshot.Adapters.FirstOrDefault(delegate(AdapterSnapshot a) { return a.Index == index || a.IPv6Index == index; });
        }

        internal static bool RouteConcern(Snapshot snapshot, RouteInsight routes)
        {
            if (snapshot == null || snapshot.Online || !routes.Readable || routes.Truncated || snapshot.AdapterError != null || snapshot.Adapters.Any(delegate(AdapterSnapshot a) { return !a.InformationReadable; })) return false;
            bool down = routes.Routes.Any(delegate(RouteEvidence r) { AdapterSnapshot a = RouteAdapter(snapshot, r.InterfaceIndex); return a == null || !a.Up; });
            bool usableDefault = routes.Routes.Any(delegate(RouteEvidence r) { AdapterSnapshot a = RouteAdapter(snapshot, r.InterfaceIndex); return r.PrefixLength == 0 && a != null && a.Up; });
            return down && !usableDefault && routes.Routes.Count > 0;
        }

        private static SpecialDiagnostic Routes(Snapshot snapshot, RouteInsight routes)
        {
            if (!routes.Readable) return Special("VpnRoutes", I18n.T("VPN / IP 路由专项"), CheckState.Unknown, I18n.T("IPv4 / IPv6 路由表未完整读取"), I18n.T("只读 API 结果未确定：") + routes.Error);
            string detail = String.Join(Environment.NewLine, routes.Routes.Take(24).Select(delegate(RouteEvidence r)
            {
                AdapterSnapshot a = RouteAdapter(snapshot, r.InterfaceIndex);
                return r.Destination + "/" + r.PrefixLength + " → " + r.NextHop + " | if=" + r.InterfaceIndex + " | metric=" + r.Metric + " | " + (a == null ? I18n.T("接口未确认") : Clean(a.Name) + (a.Up ? I18n.T("（已连接）") : I18n.T("（未连接）")));
            }));
            return Special("VpnRoutes", I18n.T("VPN / IP 路由专项"), RouteConcern(snapshot, routes) ? CheckState.Warning : CheckState.Unknown, RouteConcern(snapshot, routes) ? I18n.T("默认或分流路由引用的接口需要核对") : I18n.T("已读取实际默认与半网分流路由，未自动判为残留"), I18n.T("路由总数：") + routes.Total + Environment.NewLine + detail + Environment.NewLine + I18n.T("最多展示 24 条默认/半网路由；跃点仅为路由字段，未综合接口度量、策略及 WFP。接口名称、虚拟网卡存在或重复默认路由都不是删除依据；未清理路由或 VPN 断网保护。"));
        }

        private static SpecialDiagnostic Adapters(Snapshot snapshot, HardwareSnapshot hardware)
        {
            if (snapshot == null) return Special("VpnAdapters", I18n.T("VPN / 虚拟接口专项"), CheckState.Unknown, I18n.T("接口证据未取得"), I18n.T("未启停、卸载或重置任何网卡。"));
            List<AdapterSnapshot> virtuals = snapshot.Adapters.Where(delegate(AdapterSnapshot a)
            {
                return a.Type == NetworkInterfaceType.Tunnel || a.Type == NetworkInterfaceType.Ppp || hardware != null && hardware.Adapters.Any(delegate(HardwareEvidence h) { return String.Equals(h.Id, a.Id, StringComparison.OrdinalIgnoreCase) && h.Physical == false; });
            }).ToList();
            string detail = String.Join(Environment.NewLine, virtuals.Take(16).Select(delegate(AdapterSnapshot a) { return Clean(a.Name) + " | " + a.Type + " | " + (a.Up ? I18n.T("已连接") : I18n.T("未连接")) + " | " + Clean(a.Description); }));
            return Special("VpnAdapters", I18n.T("VPN / 虚拟接口专项"), CheckState.Unknown, I18n.T("识别的隧道、PPP 或非物理接口数：") + virtuals.Count, detail + Environment.NewLine + I18n.T("识别依据是接口类型或系统物理属性，未按名称判故障。已安装 VPN、虚拟网卡或接口未连接本身不是异常；未启停或卸载任何设备。"));
        }

        internal static bool PortalSuspected(Snapshot snapshot)
        {
            return snapshot != null && !snapshot.Http.Any(delegate(Probe p) { return p.Success && p.Marker; }) && snapshot.Http.Any(delegate(Probe p) { return p.Marker && p.Endpoint != null && p.Endpoint.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && (p.Redirected || p.ContentMismatch); });
        }

        internal static SpecialDiagnostic Wifi(Snapshot snapshot, WifiInsight wifi)
        {
            if (!wifi.Readable) return Special("Wifi", I18n.T("Wi-Fi 专项"), CheckState.Unknown, I18n.T("WLAN 信息未完整读取，不能判断无线故障"), I18n.T("原生 API 错误或读取界限：") + wifi.Error + Environment.NewLine + I18n.T("无 WLAN 服务、有线设备或权限限制均可能导致不可读；不自动更改服务或位置权限。"));
            List<WifiEvidence> connected = wifi.Interfaces.Where(delegate(WifiEvidence w) { return w.State == 1; }).ToList();
            if (connected.Count == 0) return Special("Wifi", I18n.T("Wi-Fi 专项"), CheckState.Unknown, wifi.Total > wifi.Interfaces.Count ? I18n.T("部分 WLAN 接口未展开，连接状态尚未确定") : wifi.Interfaces.Count == 0 ? I18n.T("没有 WLAN 接口，此专项不适用") : I18n.T("当前没有已连接的 Wi-Fi，此专项不适用或尚未关联"), I18n.T("有线或其他连接仍可正常使用；不会把未使用 Wi-Fi 当作断网。"));
            bool weak = connected.Any(delegate(WifiEvidence w) { return w.AttributesRead && w.Signal < 35; });
            bool portal = PortalSuspected(snapshot);
            bool automatic = snapshot != null && snapshot.Http.Any(delegate(Probe p) { return p.AutomaticProxySkipped; });
            bool complete = connected.All(delegate(WifiEvidence w) { return w.AttributesRead; });
            string detail = String.Join(Environment.NewLine, connected.Select(delegate(WifiEvidence w)
            {
                return Clean(w.Name) + Environment.NewLine + (w.AttributesRead ? "SSID=" + Clean(w.Ssid) + Environment.NewLine + I18n.T(" | 信号：") + w.Signal + "% | Rx=" + (w.RxKbps / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " Mbps | Tx=" + (w.TxKbps / 1000.0).ToString("F1", CultureInfo.InvariantCulture) + " Mbps" : I18n.T("连接详情不可读，错误码：") + w.QueryError);
            }));
            if (snapshot != null) detail += Environment.NewLine + I18n.T("系统 DNS 成功数：") + snapshot.Dns.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.Dns.Count + I18n.T("；实际 HTTP 成功数：") + snapshot.Http.Count(delegate(Probe p) { return p.Success; }) + "/" + snapshot.Http.Count;
            detail += Environment.NewLine + I18n.T("最多检查八个 WLAN 接口。速率为无线关联速率，不是宽带测速；信号低于 35% 仅为本工具提醒阈值。新版本 Windows 的位置/隐私权限可能限制连接详情，程序不会请求更改权限或读取已保存网络历史。" );
            if (portal) detail += Environment.NewLine + I18n.T("HTTP 联网标记发生重定向或内容不符，可能为认证门户，也可能是代理/过滤或端点变化；未跟随重定向、登录或提交任何信息。" );
            return Special("Wifi", I18n.T("Wi-Fi 专项"), automatic && !weak ? CheckState.Unknown : portal || weak || snapshot != null && !snapshot.Online ? CheckState.Warning : complete ? CheckState.Passed : CheckState.Unknown, portal ? I18n.T("已关联 Wi-Fi，存在可能的认证门户或 HTTP 截获现象") : weak ? I18n.T("已关联 Wi-Fi，信号质量偏弱") : automatic ? I18n.T("Wi-Fi 已关联，系统自动代理路径未验证") : snapshot != null && !snapshot.Online ? I18n.T("已关联 Wi-Fi，但尚未确认公网 HTTP") : !complete ? I18n.T("Wi-Fi 已关联，部分信号详情受限") : I18n.T("Wi-Fi 已关联，当前 HTTP 路径可用"), detail);
        }

        private static string Clean(string value)
        {
            return new string((value ?? "").Take(128).Where(delegate(char c) { return !Char.IsControl(c); }).ToArray());
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeProxy { public uint AccessType; public IntPtr Server, Bypass; }
        [StructLayout(LayoutKind.Sequential)]
        private struct UserProxy { [MarshalAs(UnmanagedType.Bool)] public bool AutoDetect; public IntPtr Pac, Server, Bypass; }
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpGetDefaultProxyConfiguration(out NativeProxy proxy);
        [DllImport("winhttp.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WinHttpGetIEProxyConfigForCurrentUser(out UserProxy proxy);
        [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr buffer);

        internal static ProxyInsight ReadProxy()
        {
            ProxyInsight result = new ProxyInsight();
            NativeProxy proxy = new NativeProxy();
            UserProxy user = new UserProxy();
            try
            {
                result.DefaultRead = WinHttpGetDefaultProxyConfiguration(out proxy);
                if (!result.DefaultRead) result.Error = Marshal.GetLastWin32Error().ToString(CultureInfo.InvariantCulture);
                result.AccessType = proxy.AccessType;
                result.Server = proxy.Server == IntPtr.Zero ? "" : Marshal.PtrToStringUni(proxy.Server);
                result.UserRead = WinHttpGetIEProxyConfigForCurrentUser(out user);
                result.AutoDetect = user.AutoDetect;
                result.Pac = user.Pac == IntPtr.Zero ? "" : Marshal.PtrToStringUni(user.Pac);
            }
            catch (Exception ex) { result.Error = ex.GetType().Name; }
            finally
            {
                foreach (IntPtr pointer in new[] { proxy.Server, proxy.Bypass, user.Pac, user.Server, user.Bypass }) if (pointer != IntPtr.Zero) GlobalFree(pointer);
            }
            return result;
        }

        [StructLayout(LayoutKind.Explicit, Size = 28, Pack = 4)]
        internal struct SockAddress
        {
            [FieldOffset(0)] public ushort Family;
            [FieldOffset(4)] public uint V4;
            [FieldOffset(8)] public ulong V6a;
            [FieldOffset(16)] public ulong V6b;
            [FieldOffset(24)] public uint Scope;
        }
        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        internal struct Prefix { public SockAddress Address; public byte Length; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct RouteRow
        {
            public ulong Luid; public uint Index; public Prefix Destination; public SockAddress NextHop; public byte SitePrefix;
            public uint Valid, Preferred, Metric, Protocol; public byte Loopback, Auto, Publish, Immortal; public uint Age, Origin;
        }
        [StructLayout(LayoutKind.Sequential)] private struct RouteTable { public uint Count; public RouteRow First; }
        [DllImport("iphlpapi.dll")] private static extern uint GetIpForwardTable2(ushort family, out IntPtr table);
        [DllImport("iphlpapi.dll")] private static extern void FreeMibTable(IntPtr table);

        internal static string Address(SockAddress address)
        {
            if (address.Family == 2) return new IPAddress(BitConverter.GetBytes(address.V4)).ToString();
            if (address.Family != 23) throw new InvalidDataException("Unknown address family");
            byte[] bytes = new byte[16];
            Buffer.BlockCopy(BitConverter.GetBytes(address.V6a), 0, bytes, 0, 8);
            Buffer.BlockCopy(BitConverter.GetBytes(address.V6b), 0, bytes, 8, 8);
            return new IPAddress(bytes, address.Scope).ToString();
        }

        internal static RouteEvidence ParseRoute(RouteRow row)
        {
            int bits = row.Destination.Length;
            string address = Address(row.Destination.Address);
            if (bits > (row.Destination.Address.Family == 2 ? 32 : 128) || row.NextHop.Family != row.Destination.Address.Family || row.Index == 0) throw new InvalidDataException("Invalid route fields");
            if (bits == 0 && address != "0.0.0.0" && address != "::") return null;
            if (bits != 0 && !(bits == 1 && (address == "0.0.0.0" || address == "128.0.0.0" || address == "::" || address == "8000::"))) return null;
            return new RouteEvidence { InterfaceIndex = row.Index, Destination = address, PrefixLength = bits, NextHop = Address(row.NextHop), Metric = row.Metric };
        }

        private static RouteInsight ReadRoutes()
        {
            RouteInsight result = new RouteInsight();
            IntPtr table = IntPtr.Zero;
            try
            {
                uint error = GetIpForwardTable2(0, out table);
                if (error != 0 || table == IntPtr.Zero) { result.Error = error.ToString(CultureInfo.InvariantCulture); return result; }
                int count = Marshal.ReadInt32(table);
                if (count < 0 || count > 8192) throw new InvalidDataException("Route count limit");
                int size = Marshal.SizeOf(typeof(RouteRow)), start = (int)Marshal.OffsetOf(typeof(RouteTable), "First");
                if (size != 104 || start != 8) throw new InvalidDataException("Route layout mismatch");
                result.Total = count;
                for (int i = 0; i < count; i++)
                {
                    RouteEvidence row = ParseRoute((RouteRow)Marshal.PtrToStructure(IntPtr.Add(table, start + size * i), typeof(RouteRow)));
                    if (row != null) { if (result.Routes.Count < 128) result.Routes.Add(row); else result.Truncated = true; }
                }
                result.Readable = true;
            }
            catch (Exception ex) { result.Error = ex.GetType().Name; }
            finally { if (table != IntPtr.Zero) FreeMibTable(table); }
            return result;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WlanInterface { public Guid Id; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description; public uint State; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Dot11Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct WlanAssociation
        {
            public Dot11Ssid Ssid; public uint Bss; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
            public uint Phy, PhyIndex, Signal, RxRate, TxRate;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct WlanSecurity { public int Enabled, OneX; public uint Authentication, Cipher; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WlanConnection { public uint State, Mode; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile; public WlanAssociation Association; public WlanSecurity Security; }
        [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
        [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
        [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, uint opcode, IntPtr reserved, out uint size, out IntPtr data, out uint type);
        [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
        [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr buffer);

        internal static WifiEvidence ParseWifi(WlanInterface adapter, WlanConnection connection)
        {
            if (connection.Association.Ssid.Length > 32 || connection.Association.Signal > 100) throw new InvalidDataException("Invalid WLAN fields");
            return new WifiEvidence { Id = adapter.Id, Name = adapter.Description, State = connection.State, AttributesRead = true, Ssid = Encoding.UTF8.GetString(connection.Association.Ssid.Bytes, 0, (int)connection.Association.Ssid.Length), Signal = connection.Association.Signal, RxKbps = connection.Association.RxRate, TxKbps = connection.Association.TxRate };
        }

        internal static WifiInsight ReadWifi()
        {
            WifiInsight result = new WifiInsight();
            IntPtr client = IntPtr.Zero, list = IntPtr.Zero;
            try
            {
                uint negotiated;
                uint error = WlanOpenHandle(2, IntPtr.Zero, out negotiated, out client);
                if (error != 0) { result.Error = error.ToString(CultureInfo.InvariantCulture); return result; }
                error = WlanEnumInterfaces(client, IntPtr.Zero, out list);
                if (error != 0 || list == IntPtr.Zero) { result.Error = error.ToString(CultureInfo.InvariantCulture); return result; }
                int count = Marshal.ReadInt32(list);
                if (count < 0 || count > 32 || Marshal.SizeOf(typeof(WlanInterface)) != 532 || Marshal.SizeOf(typeof(WlanConnection)) != 604) throw new InvalidDataException("WLAN layout/count limit");
                result.Total = count;
                for (int i = 0; i < Math.Min(count, 8); i++)
                {
                    WlanInterface adapter = (WlanInterface)Marshal.PtrToStructure(IntPtr.Add(list, 8 + i * 532), typeof(WlanInterface));
                    WifiEvidence evidence = new WifiEvidence { Id = adapter.Id, Name = adapter.Description, State = adapter.State };
                    if (adapter.State == 1)
                    {
                        IntPtr data = IntPtr.Zero;
                        uint size, type;
                        try
                        {
                            Guid id = adapter.Id;
                            error = WlanQueryInterface(client, ref id, 7, IntPtr.Zero, out size, out data, out type);
                            evidence.QueryError = error;
                            if (error == 0 && data != IntPtr.Zero && size >= 604 && size <= 4096)
                                evidence = ParseWifi(adapter, (WlanConnection)Marshal.PtrToStructure(data, typeof(WlanConnection)));
                            else if (error == 0) evidence.QueryError = 13;
                        }
                        finally { if (data != IntPtr.Zero) WlanFreeMemory(data); }
                    }
                    result.Interfaces.Add(evidence);
                }
                result.Readable = true;
            }
            catch (Exception ex) { result.Error = ex.GetType().Name; }
            finally
            {
                if (list != IntPtr.Zero) WlanFreeMemory(list);
                if (client != IntPtr.Zero) WlanCloseHandle(client, IntPtr.Zero);
            }
            return result;
        }
    }
}
