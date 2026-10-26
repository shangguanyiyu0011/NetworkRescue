using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Serialization;
using Microsoft.Win32;

namespace NetworkRescue
{
    public enum CheckState { Passed, Warning, Failed, Unknown }

    public class RepairAction
    {
        public string Id, Title, Description, AdapterId, Impact;
        public bool Suggested;
    }

    public class DiagnosticItem
    {
        public string Id, Title, Summary, Detail;
        public CheckState State;
        public List<RepairAction> Actions = new List<RepairAction>();
        public List<DiagnosticEvidenceGroup> EvidenceGroups = new List<DiagnosticEvidenceGroup>();
    }

    public class DiagnosticEvidenceGroup
    {
        public string TitleKey, Title, Note;
        public CheckState? State;
        public List<DiagnosticEvidenceField> Fields = new List<DiagnosticEvidenceField>();
    }

    public class DiagnosticEvidenceField
    {
        public string LabelKey, Label, Value;
    }

    internal static class DiagnosticEvidence
    {
        internal static DiagnosticEvidenceField Field(string key, string label, object value)
        {
            return new DiagnosticEvidenceField { LabelKey = key, Label = I18n.T(label), Value = Convert.ToString(value, CultureInfo.InvariantCulture) };
        }

        internal static DiagnosticEvidenceGroup Group(string key, string title, CheckState? state, params DiagnosticEvidenceField[] fields)
        {
            return new DiagnosticEvidenceGroup { TitleKey = key, Title = String.IsNullOrEmpty(key) ? title : I18n.T(title), State = state, Fields = fields.ToList() };
        }

        internal static string Boolean(bool? value)
        {
            return !value.HasValue ? I18n.T("未知") : value.Value ? I18n.T("是") : I18n.T("否");
        }

        internal static string State(CheckState state)
        {
            return state == CheckState.Passed ? I18n.T("通过") : state == CheckState.Warning ? I18n.T("注意") : state == CheckState.Failed ? I18n.T("异常") : I18n.T("未确定");
        }

        internal static List<DiagnosticEvidenceGroup> Summary(CheckState state, string summary)
        {
            return new List<DiagnosticEvidenceGroup> { Group("evidence", "检测依据", state, Field("statusColumn", "状态", State(state)), Field("testDetails", "测试详情", summary)) };
        }

        internal static void AddProbes(DiagnosticItem item, string key, string title, IEnumerable<Probe> probes, bool http)
        {
            List<Probe> readings = probes.ToList();
            if (readings.Count == 0)
            {
                item.EvidenceGroups.Add(Group(key, title, CheckState.Unknown, Field("statusColumn", "状态", I18n.T("未读取"))));
                return;
            }
            foreach (Probe probe in readings)
            {
                CheckState state = probe.Uncertain || probe.AutomaticProxySkipped ? CheckState.Unknown : probe.Success ? CheckState.Passed : probe.Responded ? CheckState.Warning : CheckState.Unknown;
                DiagnosticEvidenceGroup group = Group(null, probe.Label, state, Field("statusColumn", "状态", State(state)));
                if (!String.IsNullOrEmpty(probe.Method)) group.Fields.Add(Field("monitorMethod", "检查方式", probe.Method));
                if (!String.IsNullOrEmpty(probe.Endpoint)) group.Fields.Add(Field("hostLabel", "域名或 IP 地址", probe.Endpoint));
                if (probe.ResolvedAddressCount.HasValue) group.Fields.Add(Field("evidenceResolvedAddresses", "解析地址数", probe.ResolvedAddressCount.Value));
                if (http)
                {
                    group.Fields.Add(Field("siteResponded", "已收到应答", Boolean(probe.Responded)));
                    group.Fields.Add(Field("evidenceHttpStatus", "HTTP 状态码", probe.HttpStatus > 0 ? probe.HttpStatus.ToString(CultureInfo.InvariantCulture) : I18n.T("未确定")));
                    if (probe.AutomaticProxySkipped) group.Note = I18n.T("系统自动代理路径未验证：为避免执行 PAC/WPAD，跳过此 HTTP 抽样；直连对照不代表 PAC 故障。");
                    else if (probe.ContentMismatch || probe.Redirected) group.Note = I18n.T("，未获得预期响应，可能为登录页、过滤或端点差异").TrimStart('，', ',');
                    else if (probe.Success) group.Note = probe.Marker ? I18n.T("，联网标记匹配").TrimStart('，', ',') : I18n.T("国内站点 TLS 及 HTTP 响应成功；未验证固定联网标记");
                }
                item.EvidenceGroups.Add(group);
            }
        }
    }

    public class DiagnosticReport
    {
        public DateTime Timestamp;
        public List<DiagnosticItem> Items = new List<DiagnosticItem>();
        public string Summary;
        public bool InternetAvailable;
        public FaultConclusion Fault;
        public List<SpecialDiagnostic> SpecialChecks = new List<SpecialDiagnostic>();
        internal Snapshot EvidenceSnapshot;

        public string ToPlainText()
        {
            StringBuilder text = new StringBuilder();
            text.AppendLine(I18n.T("断网急救箱 · 诊断报告"));
            text.AppendLine(Timestamp.ToString("yyyy-MM-dd HH:mm:ss"));
            text.AppendLine(Summary);
            if (Fault != null)
            {
                text.AppendLine(Fault.Title + " [" + Fault.Category + "/" + Fault.Confidence + "]");
                text.AppendLine(Fault.Summary);
                foreach (string evidence in Fault.Evidence) text.AppendLine("- " + evidence);
                foreach (string step in Fault.NextSteps) text.AppendLine("→ " + step);
            }
            text.AppendLine(I18n.T("说明：这是固定端点的连通性抽样；未探测的站点和应用可能有不同结果。报告不包含代理凭据。"));
            foreach (DiagnosticItem item in Items)
            {
                string state = item.State == CheckState.Passed ? I18n.T("通过") : item.State == CheckState.Warning ? I18n.T("注意") : item.State == CheckState.Failed ? I18n.T("异常") : I18n.T("未确定");
                text.AppendLine();
                text.AppendLine("[" + state + "] " + item.Title + I18n.T("：") + item.Summary);
                text.AppendLine(item.Detail);
                foreach (RepairAction action in item.Actions)
                    text.AppendLine(I18n.T("可选操作：") + action.Title + I18n.T("；") + action.Impact);
            }
            foreach (SpecialDiagnostic special in SpecialChecks)
            {
                text.AppendLine();
                text.AppendLine(special.Title + I18n.T("：") + special.Summary);
                text.AppendLine(special.Detail);
            }
            return text.ToString();
        }
    }

    public class RepairResult
    {
        public bool Success;
        public string Summary, BackupPath, Code;
        public List<string> Details = new List<string>();
        public bool RestartRequired;
        public bool? BeforeInternetAvailable;
        public DateTime? BeforeSampledUtc;
        public RepairOutcome Outcome;
        public List<RepairItemResult> Items = new List<RepairItemResult>();
        public string OperationId;
        public bool JournalSaved, PartialSuccess;
    }

    public enum RepairOutcome { NotStarted, Completed, RestartRequired, Partial, Rejected, Failed, Cancelled, NoChange, NeedsReview }
    public enum RepairItemState { Prepared, Writing, Completed, RestartRequired, Rejected, Failed, Cancelled, Unconfirmed, NotRestorable }

    public class RepairItemResult
    {
        public string ActionId, AdapterId, Code, Message;
        public RepairItemState State;
        public bool WriteStarted, MayHaveChanged, RestartRequired;
        public long? NativeCode;
    }

    public class RepairOperationRecord
    {
        public int Schema = 1;
        public string OperationId, Kind;
        public DateTime StartedUtc, UpdatedUtc;
        public bool Finished;
        public RepairOutcome Outcome;
        public List<RepairItemResult> Items = new List<RepairItemResult>();
    }

    public class BackupOperationInfo
    {
        public string BackupPath, OperationId, Kind;
        public DateTime StartedUtc;
        public RepairOutcome Outcome;
        public bool Finished;
        public List<RepairItemResult> Items = new List<RepairItemResult>();
    }

    internal class AdapterSnapshot
    {
        public string Id, Name;
        public int Index, IPv6Index;
        public NetworkInterfaceType Type;
        public string Description;
        public bool Up, Dhcp, DnsAutomatic, DnsConfigurationReadable, InformationReadable;
        public List<string> Addresses = new List<string>();
        public List<string> Gateways = new List<string>();
        public List<string> Dns = new List<string>();
        public List<string> AllDns = new List<string>();
        public List<string> ConfiguredDns = new List<string>();
        public bool Apipa { get { return Addresses.Any(delegate(string a) { return a.StartsWith("169.254.", StringComparison.Ordinal); }); } }
        public bool UsableV4 { get { return Addresses.Any(delegate(string a) { return !a.StartsWith("169.254.", StringComparison.Ordinal) && a != "0.0.0.0"; }); } }
    }

    internal class Probe
    {
        public string Label, Detail;
        public bool Success, Responded, Marker, Uncertain;
        public string Endpoint, Method;
        public int HttpStatus;
        public bool Redirected, ContentMismatch;
        public bool AutomaticProxySkipped;
        public int? ResolvedAddressCount;
    }

    internal class ProxySnapshot
    {
        public bool Readable, Enabled, AutoDetect;
        public bool AutoConfigKnown = true;
        public string Server = "", Pac = "", Override = "";
        public bool EnableExists;
        public int EnableValue;
        public List<Probe> Endpoints = new List<Probe>();
        public bool DefinitelyUnreachable { get { return Readable && Enabled && AutoConfigKnown && !AutoDetect && String.IsNullOrWhiteSpace(Pac) && Endpoints.Count > 0 && Endpoints.All(delegate(Probe p) { return !p.Success && !p.Uncertain; }); } }
    }

    internal class Snapshot
    {
        public List<AdapterSnapshot> Adapters = new List<AdapterSnapshot>();
        public List<Probe> Tcp = new List<Probe>(), Dns = new List<Probe>(), Http = new List<Probe>();
        public List<Probe> GatewayProbes = new List<Probe>();
        public List<Probe> DirectHttp = new List<Probe>(), LocalDns = new List<Probe>();
        public ProxySnapshot Proxy = new ProxySnapshot();
        public string AdapterError;
        public bool Direct { get { return Tcp.Any(delegate(Probe p) { return p.Success; }); } }
        public bool DnsWorks { get { return Dns.Any(delegate(Probe p) { return p.Success; }); } }
        public bool DnsFailed { get { return !DnsWorks && Dns.Count(delegate(Probe p) { return !p.Success && !p.Uncertain; }) >= 2; } }
        public bool Online { get { return Http.Any(delegate(Probe p) { return p.Success; }); } }
        public bool DirectHttpWorks { get { return DirectHttp.Any(delegate(Probe p) { return p.Success; }); } }
    }

    [Serializable]
    public class AdapterBackup
    {
        public string Id, Name;
        public bool Dhcp, DnsAutomatic, DnsStarted, RenewStarted;
        public string[] Addresses = new string[0], Gateways = new string[0], Dns = new string[0];
    }

    [Serializable]
    public class RescueBackup
    {
        public int Schema = 1;
        public DateTime CreatedUtc = DateTime.UtcNow;
        public bool ProxyStarted, FlushStarted, ProxyEnableExists;
        public int ProxyEnableValue;
        public string ProxyServer = "", ProxyPac = "", ProxyOverride = "";
        public List<AdapterBackup> Adapters = new List<AdapterBackup>();
        public RepairOperationRecord Operation;
    }

    public class RescueEngine
    {
        private const string InternetKey = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NetworkRescue.Backup.v1");
        private readonly SemaphoreSlim operation = new SemaphoreSlim(1, 1);
        public string BackupRoot { get; private set; }

        public RescueEngine()
        {
            BackupRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NetworkRescue", "Backups");
        }

        public async Task<DiagnosticReport> DiagnoseAsync(IProgress<string> progress, CancellationToken cancellationToken)
        {
            await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return BuildReport(await CaptureAsync(progress, cancellationToken).ConfigureAwait(false)); }
            finally { operation.Release(); }
        }

        private static void Notify(IProgress<string> progress, string text)
        {
            if (progress != null) progress.Report(text);
        }

        private static async Task<Snapshot> CaptureAsync(IProgress<string> progress, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Notify(progress, I18n.T("正在读取网卡与当前用户代理配置……"));
            Snapshot snapshot = new Snapshot();
            await Task.Run(delegate
            {
                try { snapshot.Adapters = ReadAdapters(); }
                catch (Exception ex) { snapshot.AdapterError = ex.GetType().Name; }
                snapshot.Proxy = ReadProxy();
            }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            Notify(progress, I18n.T("正在交叉验证直连、DNS、系统代理和 HTTP 联网（约 5–15 秒）……"));
            Task<Probe>[] tcp = new[] { Task.Run(delegate { return TcpProbe("1.1.1.1", 443); }), Task.Run(delegate { return TcpProbe("8.8.8.8", 443); }), Task.Run(delegate { return TcpProbe("223.5.5.5", 443); }) };
            Task<Probe>[] dns = new[] { Task.Run(delegate { return DnsProbe("www.microsoft.com"); }), Task.Run(delegate { return DnsProbe("www.cloudflare.com"); }), Task.Run(delegate { return DnsProbe("www.baidu.com"); }) };
            Task<Probe>[] http = new[] { Task.Run(delegate { return HttpProbe("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test", true, true, snapshot.Proxy); }), Task.Run(delegate { return HttpProbe("https://www.gstatic.com/generate_204", null, true, true, snapshot.Proxy); }), Task.Run(delegate { return HttpProbe("https://www.baidu.com/", null, false, true, snapshot.Proxy); }) };
            Task<Probe>[] directHttp = snapshot.Proxy.Enabled || snapshot.Proxy.AutoDetect || !snapshot.Proxy.AutoConfigKnown || !String.IsNullOrEmpty(snapshot.Proxy.Pac) ? new[] { Task.Run(delegate { return HttpProbe("http://www.msftconnecttest.com/connecttest.txt", "Microsoft Connect Test", true, false); }), Task.Run(delegate { return HttpProbe("https://www.gstatic.com/generate_204", null, true, false); }), Task.Run(delegate { return HttpProbe("https://www.baidu.com/", null, false, false); }) } : new Task<Probe>[0];
            List<Task<Probe>> localDns = new List<Task<Probe>>();
            if (HasOnlyLoopbackDns(snapshot))
                foreach (string dnsAddress in ActiveDns(snapshot).Take(4))
                {
                    string address = dnsAddress;
                    localDns.Add(Task.Run(delegate { return TcpProbe(address, 53); }));
                    localDns.Add(Task.Run(delegate { return LocalDnsProbe(address); }));
                }
            List<Task<Probe>> gateways = new List<Task<Probe>>();
            foreach (string gateway in snapshot.Adapters.Where(delegate(AdapterSnapshot a) { return a.Up; }).SelectMany(delegate(AdapterSnapshot a) { return a.Gateways; }).Distinct().Take(4))
            {
                string address = gateway;
                gateways.Add(Task.Run(delegate { return PingProbe(address); }));
                gateways.Add(Task.Run(delegate { return TcpProbe(address, 80); }));
                gateways.Add(Task.Run(delegate { return TcpProbe(address, 443); }));
            }
            Task proxy = Task.Run(delegate { ProbeProxy(snapshot.Proxy); });
            await Task.WhenAll(tcp.Cast<Task>().Concat(dns).Concat(http).Concat(directHttp).Concat(localDns).Concat(gateways).Concat(new[] { proxy })).ConfigureAwait(false);
            snapshot.Tcp.AddRange(tcp.Select(delegate(Task<Probe> t) { return t.Result; }));
            snapshot.Dns.AddRange(dns.Select(delegate(Task<Probe> t) { return t.Result; }));
            snapshot.Http.AddRange(http.Select(delegate(Task<Probe> t) { return t.Result; }));
            snapshot.GatewayProbes.AddRange(gateways.Select(delegate(Task<Probe> t) { return t.Result; }));
            snapshot.DirectHttp.AddRange(directHttp.Select(delegate(Task<Probe> t) { return t.Result; }));
            snapshot.LocalDns.AddRange(localDns.Select(delegate(Task<Probe> t) { return t.Result; }));
            token.ThrowIfCancellationRequested();
            return snapshot;
        }

        private static List<AdapterSnapshot> ReadAdapters()
        {
            List<AdapterSnapshot> result = new List<AdapterSnapshot>();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                Guid id;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback || !Guid.TryParse(nic.Id, out id)) continue;
                AdapterSnapshot a = new AdapterSnapshot { Id = id.ToString("D"), Name = nic.Name, Description = nic.Description, Type = nic.NetworkInterfaceType, Up = nic.OperationalStatus == OperationalStatus.Up };
                try
                {
                    IPInterfaceProperties ip = nic.GetIPProperties();
                    IPv4InterfaceProperties v4 = ip.GetIPv4Properties();
                    if (v4 != null) { a.Dhcp = v4.IsDhcpEnabled; a.Index = v4.Index; }
                    try
                    {
                        IPv6InterfaceProperties v6 = nic.Supports(NetworkInterfaceComponent.IPv6) ? ip.GetIPv6Properties() : null;
                        if (v6 != null) a.IPv6Index = v6.Index;
                    }
                    catch (NetworkInformationException) { }
                    foreach (UnicastIPAddressInformation address in ip.UnicastAddresses)
                        if (address.Address.AddressFamily == AddressFamily.InterNetwork) a.Addresses.Add(address.Address.ToString());
                    foreach (GatewayIPAddressInformation gateway in ip.GatewayAddresses)
                        if (gateway.Address.AddressFamily == AddressFamily.InterNetwork && !gateway.Address.Equals(IPAddress.Any)) a.Gateways.Add(gateway.Address.ToString());
                    a.Dns.AddRange(ip.DnsAddresses.Where(delegate(IPAddress address) { return address.AddressFamily == AddressFamily.InterNetwork; }).Select(delegate(IPAddress address) { return address.ToString(); }));
                    a.AllDns.AddRange(ip.DnsAddresses.Select(delegate(IPAddress address) { return address.ToString(); }));
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{" + a.Id + "}"))
                    {
                        if (key != null)
                        {
                            string configured = Convert.ToString(key.GetValue("NameServer", ""), CultureInfo.InvariantCulture);
                            a.DnsAutomatic = String.IsNullOrWhiteSpace(configured);
                            a.DnsConfigurationReadable = true;
                            foreach (string entry in configured.Split(new[] { ',', ';', ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                IPAddress address;
                                if (!IPAddress.TryParse(entry, out address) || address.AddressFamily != AddressFamily.InterNetwork) { a.DnsConfigurationReadable = false; break; }
                                a.ConfiguredDns.Add(address.ToString());
                            }
                        }
                    }
                    a.InformationReadable = true;
                }
                catch (NetworkInformationException) { a.DnsConfigurationReadable = false; }
                result.Add(a);
            }
            return result;
        }

        private static ProxySnapshot ReadProxy()
        {
            ProxySnapshot proxy = new ProxySnapshot { AutoConfigKnown = false };
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetKey))
                {
                    if (key == null) return proxy;
                    object value = key.GetValue("ProxyEnable");
                    proxy.EnableExists = value != null;
                    proxy.EnableValue = value is int ? (int)value : 0;
                    proxy.Enabled = proxy.EnableValue != 0;
                    proxy.Server = Convert.ToString(key.GetValue("ProxyServer", ""), CultureInfo.InvariantCulture);
                    proxy.Pac = Convert.ToString(key.GetValue("AutoConfigURL", ""), CultureInfo.InvariantCulture);
                    proxy.Override = Convert.ToString(key.GetValue("ProxyOverride", ""), CultureInfo.InvariantCulture);
                    proxy.Readable = true;
                }
                ProxyInsight automatic = DiagnosticInsights.ReadProxy();
                proxy.AutoConfigKnown = automatic.UserRead;
                proxy.AutoDetect = automatic.AutoDetect;
                if (!String.IsNullOrEmpty(automatic.Pac)) proxy.Pac = automatic.Pac;
            }
            catch (System.Security.SecurityException) { }
            catch (UnauthorizedAccessException) { }
            return proxy;
        }

        private static void ProbeProxy(ProxySnapshot proxy)
        {
            if (!proxy.Enabled) return;
            if (proxy.Server == null || proxy.Server.Length > 8192)
            {
                proxy.Endpoints.Add(new Probe { Label = I18n.T("未识别的代理项"), Detail = I18n.T("代理配置过长或为空，未作端口判断"), Uncertain = true });
                return;
            }
            List<Uri> endpoints = ParseProxyEndpoints(proxy.Server);
            int totalEndpoints = proxy.Server.Split(';').SelectMany(delegate(string part) { return ParseProxyEndpoints(part); }).Select(delegate(Uri uri) { return uri.DnsSafeHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture); }).Distinct().Count();
            if (endpoints.Count == 0 || totalEndpoints > 8 || proxy.Server.Split(';').Any(delegate(string part) { return !String.IsNullOrWhiteSpace(part) && ParseProxyEndpoints(part).Count == 0; }))
                proxy.Endpoints.Add(new Probe { Label = I18n.T("未识别的代理项"), Detail = I18n.T("未能可靠解析全部代理端点，保持原配置"), Uncertain = true });
            Task<Probe>[] probes = endpoints.Select(delegate(Uri uri) { return Task.Run(delegate { return TcpProbe(uri.DnsSafeHost, uri.Port); }); }).ToArray();
            Task.WaitAll(probes);
            proxy.Endpoints.AddRange(probes.Select(delegate(Task<Probe> t) { return t.Result; }));
        }

        internal static List<Uri> ParseProxyEndpoints(string value)
        {
            List<Uri> result = new List<Uri>();
            if (String.IsNullOrWhiteSpace(value) || value.Length > 8192) return result;
            foreach (string piece in value.Split(';'))
            {
                string part = piece.Trim();
                int equals = part.IndexOf('=');
                if (equals >= 0) part = part.Substring(equals + 1).Trim();
                Uri uri;
                if (Uri.TryCreate(part.IndexOf("://", StringComparison.Ordinal) >= 0 ? part : "http://" + part, UriKind.Absolute, out uri)
                    && (uri.Scheme == "http" || uri.Scheme == "https" || uri.Scheme == "socks") && !String.IsNullOrEmpty(uri.Host) && uri.Port > 0
                    && !result.Any(delegate(Uri existing) { return existing.Host == uri.Host && existing.Port == uri.Port; })) result.Add(uri);
                if (result.Count == 8) break;
            }
            return result;
        }

        internal static AddressFamily ProbeAddressFamily(string host)
        {
            IPAddress address;
            if (IPAddress.TryParse(host.Trim('[', ']'), out address)) return address.AddressFamily;
            return Socket.OSSupportsIPv6 ? AddressFamily.InterNetworkV6 : AddressFamily.InterNetwork;
        }

        internal static Probe TcpProbe(string host, int port)
        {
            host = host.Trim('[', ']');
            Probe result = new Probe { Label = (host.IndexOf(':') >= 0 ? "[" + host + "]" : host) + ":" + port.ToString(CultureInfo.InvariantCulture) };
            result.Endpoint = result.Label;
            result.Method = "TCP";
            try
            {
                AddressFamily family = ProbeAddressFamily(host);
                using (TcpClient client = new TcpClient(family))
                {
                    if (family == AddressFamily.InterNetworkV6) client.Client.DualMode = true;
                    IAsyncResult connect = client.BeginConnect(host, port, null, null);
                    using (WaitHandle handle = connect.AsyncWaitHandle)
                    {
                        if (!handle.WaitOne(2500)) { result.Detail = I18n.T("TCP 连接超时"); return result; }
                        client.EndConnect(connect);
                        result.Success = true;
                        result.Detail = I18n.T("TCP 连接成功");
                    }
                }
            }
            catch (SocketException ex)
            {
                result.Uncertain = ex.SocketErrorCode != SocketError.ConnectionRefused && ex.SocketErrorCode != SocketError.HostUnreachable && ex.SocketErrorCode != SocketError.NetworkUnreachable && ex.SocketErrorCode != SocketError.HostNotFound && ex.SocketErrorCode != SocketError.TimedOut;
                result.Detail = I18n.T("TCP 未连接（") + ex.SocketErrorCode.ToString() + I18n.T("）");
            }
            catch (Exception ex) { result.Uncertain = true; result.Detail = I18n.T("TCP 探测未确定（") + ex.GetType().Name + I18n.T("）"); }
            return result;
        }

        private static Probe DnsProbe(string host)
        {
            Probe result = new Probe { Label = host, Endpoint = host, Method = "DNS" };
            try
            {
                IAsyncResult lookup = Dns.BeginGetHostAddresses(host, null, null);
                using (WaitHandle handle = lookup.AsyncWaitHandle)
                {
                    if (!handle.WaitOne(4000)) { result.Detail = I18n.T("系统 DNS 解析超时"); return result; }
                    IPAddress[] addresses = Dns.EndGetHostAddresses(lookup);
                    result.ResolvedAddressCount = addresses.Length;
                    result.Success = addresses.Length > 0;
                    result.Detail = result.Success ? I18n.T("系统 DNS 返回 ") + addresses.Length.ToString(CultureInfo.InvariantCulture) + I18n.T(" 个地址") : I18n.T("系统 DNS 未返回地址");
                }
            }
            catch (Exception ex) { result.Detail = I18n.T("系统 DNS 未完成（") + ex.GetType().Name + I18n.T("）"); }
            return result;
        }

        private static Probe PingProbe(string gateway)
        {
            Probe result = new Probe { Label = gateway + " ICMP", Endpoint = gateway, Method = "ICMP" };
            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = ping.Send(gateway, 1200);
                    result.Success = reply.Status == IPStatus.Success;
                    result.Detail = result.Success ? I18n.T("网关应答，往返 ") + reply.RoundtripTime.ToString(CultureInfo.InvariantCulture) + " ms" : I18n.T("无成功应答（") + reply.Status.ToString() + I18n.T("）");
                }
            }
            catch (Exception ex) { result.Detail = I18n.T("ICMP 未完成（") + ex.GetType().Name + I18n.T("）"); }
            return result;
        }

        internal static IWebProxy ManualProxyFor(Uri target, ProxySnapshot configuration)
        {
            if (!configuration.Enabled) return null;
            if (configuration.Server.Length > 8192 || configuration.Override.Length > 16384) throw new InvalidDataException("Proxy size limit");
            string[] rules = configuration.Override.Split(';');
            if (rules.Length > 128) throw new InvalidDataException("Proxy bypass limit");
            foreach (string raw in rules)
            {
                string rule = raw.Trim();
                if (rule.Length == 0) continue;
                if (String.Equals(rule, "<local>", StringComparison.OrdinalIgnoreCase)) { if (target.IsLoopback || target.Host.IndexOf('.') < 0) return null; continue; }
                if (rule.IndexOfAny(new[] { '<', '>', '\\', '\r', '\n' }) >= 0 || rule.IndexOf('/') >= 0 && rule.IndexOf("://", StringComparison.Ordinal) < 0) throw new InvalidDataException("Unrecognized proxy bypass rule");
                bool scheme = rule.IndexOf("://", StringComparison.Ordinal) >= 0;
                string authority = scheme ? rule.Substring(rule.IndexOf("://", StringComparison.Ordinal) + 3) : rule;
                string compare = (scheme ? target.Scheme + "://" : "") + target.IdnHost + (authority.IndexOf(':') >= 0 ? ":" + target.Port.ToString(CultureInfo.InvariantCulture) : "");
                string expression = "\\A" + Regex.Escape(rule).Replace("\\*", ".*").Replace("\\?", ".") + "\\z";
                if (Regex.IsMatch(compare, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50))) return null;
            }
            string server = null;
            foreach (string raw in configuration.Server.Split(';'))
            {
                int equals = raw.IndexOf('=');
                if (equals < 0) { if (server == null) server = raw.Trim(); }
                else if (String.Equals(raw.Substring(0, equals).Trim(), target.Scheme, StringComparison.OrdinalIgnoreCase)) { server = raw.Substring(equals + 1).Trim(); break; }
            }
            if (String.IsNullOrEmpty(server)) return null;
            Uri proxy;
            if (!Uri.TryCreate(server.Contains("://") ? server : "http://" + server, UriKind.Absolute, out proxy) || (proxy.Scheme != "http" && proxy.Scheme != "https") || proxy.Port <= 0) throw new InvalidDataException("Unsupported manual proxy format");
            UriBuilder clean = new UriBuilder(proxy) { UserName = "", Password = "", Path = "", Query = "", Fragment = "" };
            return new WebProxy(clean.Uri) { Credentials = null };
        }

        private static Probe HttpProbe(string url, string expectedBody, bool marker = true, bool useSystemProxy = true, ProxySnapshot configuration = null)
        {
            Probe result = new Probe { Label = (useSystemProxy ? I18n.T("系统代理 ") : I18n.T("绕过代理 ")) + url, Endpoint = url, Marker = marker, Method = "HTTP" };
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            Timer deadline = null;
            try
            {
                if (useSystemProxy)
                {
                    configuration = configuration ?? ReadProxy();
                    if (!configuration.AutoConfigKnown || configuration.AutoDetect || !String.IsNullOrWhiteSpace(configuration.Pac))
                    {
                        result.Uncertain = true; result.AutomaticProxySkipped = true;
                        result.Detail = I18n.T("系统自动代理路径未验证：为避免执行 PAC/WPAD，跳过此 HTTP 抽样；直连对照不代表 PAC 故障。");
                        return result;
                    }
                    request.Proxy = ManualProxyFor(new Uri(url), configuration);
                }
                else request.Proxy = null;
                if (request.Proxy != null) request.Proxy.Credentials = null;
                request.Credentials = null;
                request.UseDefaultCredentials = false;
                request.PreAuthenticate = false;
                request.Timeout = 6500;
                request.ReadWriteTimeout = 2500;
                request.AllowAutoRedirect = false;
                request.UserAgent = "NetworkRescue/1.0";
                // A separate abort bounds PAC/WPAD and name-resolution paths that may ignore Timeout.
                deadline = new Timer(delegate(object state) { request.Abort(); }, null, 8000, Timeout.Infinite);
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    result.Responded = true;
                    int code = (int)response.StatusCode;
                    result.HttpStatus = code;
                    result.Redirected = code >= 300 && code < 400;
                    if (!marker) result.Success = response.ResponseUri.Scheme == "https" && code >= 200 && code < 400;
                    else if (expectedBody == null) result.Success = code == 204;
                    else if (code == 200)
                    {
                        using (StreamReader reader = new StreamReader(response.GetResponseStream()))
                        {
                            char[] body = new char[1024];
                            int read = reader.ReadBlock(body, 0, body.Length);
                            result.Success = new string(body, 0, read).Trim() == expectedBody;
                            result.ContentMismatch = !result.Success;
                        }
                    }
                    result.Detail = "HTTP " + code.ToString(CultureInfo.InvariantCulture) + (result.Success ? (marker ? I18n.T("，联网标记匹配") : I18n.T("，TLS 连接及 HTTP 响应成功；未验证固定联网标记")) : I18n.T("，未获得预期响应，可能为登录页、过滤或端点差异"));
                }
            }
            catch (WebException ex)
            {
                using (HttpWebResponse response = ex.Response as HttpWebResponse)
                {
                    result.Responded = response != null;
                    if (response != null) result.HttpStatus = (int)response.StatusCode;
                    result.Detail = response != null ? "HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + I18n.T("，未确认联网") : I18n.T("请求未完成（") + ex.Status.ToString() + I18n.T("）");
                }
            }
            catch (Exception ex) { result.Uncertain = true; result.Detail = I18n.T("请求未完成（") + ex.GetType().Name + I18n.T("）"); }
            finally { if (deadline != null) deadline.Dispose(); }
            return result;
        }

        private static DiagnosticItem Item(string id, string title, CheckState state, string summary, string detail)
        {
            return new DiagnosticItem { Id = id, Title = title, State = state, Summary = summary, Detail = detail };
        }

        private static string ProbeDetails(IEnumerable<Probe> probes)
        {
            return String.Join(Environment.NewLine, probes.Select(delegate(Probe p) { return p.Label + I18n.T("：") + p.Detail; }));
        }

        internal static bool IsLoopbackHost(string host)
        {
            if (String.Equals((host ?? "").TrimEnd('.'), "localhost", StringComparison.OrdinalIgnoreCase)) return true;
            IPAddress address;
            if (!IPAddress.TryParse((host ?? "").Trim('[', ']'), out address)) return false;
            if (address.AddressFamily == AddressFamily.InterNetworkV6 && address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
            return IPAddress.IsLoopback(address);
        }

        internal static bool HasResidualLocalProxy(Snapshot snapshot)
        {
            if (!snapshot.Proxy.DefinitelyUnreachable || snapshot.Online || snapshot.Http.Count < 2 || snapshot.Http.Any(delegate(Probe p) { return p.Uncertain; }) || !snapshot.DirectHttpWorks) return false;
            List<Uri> endpoints = new List<Uri>();
            foreach (string part in snapshot.Proxy.Server.Split(';').Where(delegate(string value) { return !String.IsNullOrWhiteSpace(value); }))
            {
                List<Uri> parsed = ParseProxyEndpoints(part);
                if (parsed.Count != 1 || !IsLoopbackHost(parsed[0].DnsSafeHost)) return false;
                if (!endpoints.Any(delegate(Uri uri) { return uri.Host == parsed[0].Host && uri.Port == parsed[0].Port; })) endpoints.Add(parsed[0]);
                if (endpoints.Count > 8) return false;
            }
            return endpoints.Count > 0 && snapshot.Proxy.Endpoints.Count >= endpoints.Count;
        }

        private static List<string> ActiveDns(Snapshot snapshot)
        {
            return snapshot.Adapters.Where(delegate(AdapterSnapshot a) { return a.Up && a.InformationReadable; }).SelectMany(delegate(AdapterSnapshot a) { return a.Dns.Concat(a.AllDns); }).Distinct().ToList();
        }

        private static bool HasOnlyLoopbackDns(Snapshot snapshot)
        {
            List<string> addresses = ActiveDns(snapshot);
            return !snapshot.Adapters.Any(delegate(AdapterSnapshot a) { return a.Up && !a.InformationReadable; }) && snapshot.Adapters.Any(delegate(AdapterSnapshot a) { return a.Up && a.InformationReadable && a.Dhcp; }) && addresses.Count > 0 && addresses.All(IsLoopbackHost);
        }

        internal static bool HasResidualLoopbackDnsRisk(Snapshot snapshot)
        {
            return snapshot.DnsFailed && HasOnlyLoopbackDns(snapshot) && snapshot.LocalDns.Count >= ActiveDns(snapshot).Count * 2 && snapshot.LocalDns.All(delegate(Probe p) { return !p.Success && !p.Uncertain; });
        }

        private static Probe LocalDnsProbe(string address)
        {
            Probe result = new Probe { Label = address + ":53 UDP DNS" };
            try
            {
                IPAddress ip = IPAddress.Parse(address);
                if (!IsLoopbackHost(address)) { result.Uncertain = true; result.Detail = I18n.T("目标不是本机，未探测"); return result; }
                byte[] query = new byte[] { 0, 0, 1, 0, 0, 1, 0, 0, 0, 0, 0, 0, 3, 119, 119, 119, 5, 98, 97, 105, 100, 117, 3, 99, 111, 109, 0, 0, 1, 0, 1 };
                byte[] id = Guid.NewGuid().ToByteArray();
                query[0] = id[0]; query[1] = id[1];
                using (UdpClient client = new UdpClient(ip.AddressFamily))
                {
                    client.Connect(ip, 53);
                    client.Send(query, query.Length);
                    IAsyncResult receive = client.BeginReceive(null, null);
                    using (WaitHandle handle = receive.AsyncWaitHandle)
                    {
                        if (!handle.WaitOne(1500)) { result.Detail = I18n.T("本机 UDP DNS 在 1.5 秒内未应答"); return result; }
                        IPEndPoint remote = new IPEndPoint(ip, 53);
                        byte[] response = client.EndReceive(receive, ref remote);
                        result.Success = response.Length >= 12 && response[0] == query[0] && response[1] == query[1] && (response[2] & 0x80) != 0;
                        result.Uncertain = !result.Success;
                        result.Detail = result.Success ? I18n.T("本机 DNS 服务已返回匹配的 DNS 响应（不据此保证解析正确）") : I18n.T("收到非预期 UDP 数据，服务状态未确定");
                    }
                }
            }
            catch (SocketException ex)
            {
                result.Uncertain = ex.SocketErrorCode != SocketError.ConnectionRefused && ex.SocketErrorCode != SocketError.ConnectionReset && ex.SocketErrorCode != SocketError.TimedOut;
                result.Detail = I18n.T("本机 UDP DNS 未应答（") + ex.SocketErrorCode.ToString() + I18n.T("）");
            }
            catch (Exception ex) { result.Uncertain = true; result.Detail = I18n.T("本机 DNS 探测未确定（") + ex.GetType().Name + I18n.T("）"); }
            return result;
        }

        internal static DiagnosticReport BuildReport(Snapshot snapshot)
        {
            DiagnosticReport report = new DiagnosticReport { Timestamp = DateTime.Now, InternetAvailable = snapshot.Online, EvidenceSnapshot = snapshot };
            bool residualProxy = HasResidualLocalProxy(snapshot), residualDns = HasResidualLoopbackDnsRisk(snapshot);
            List<AdapterSnapshot> active = snapshot.Adapters.Where(delegate(AdapterSnapshot a) { return a.Up; }).ToList();
            bool anyV4 = active.Any(delegate(AdapterSnapshot a) { return a.UsableV4; });
            bool gateway = active.Any(delegate(AdapterSnapshot a) { return a.Gateways.Count > 0 && a.UsableV4; });
            StringBuilder details = new StringBuilder();
            foreach (AdapterSnapshot a in snapshot.Adapters)
                details.AppendLine(a.Name + " | " + (a.Up ? I18n.T("已连接") : I18n.T("未连接")) + " | IPv4: " + (a.Addresses.Count == 0 ? I18n.T("无") : String.Join(", ", a.Addresses)) + " | " + (a.Dhcp ? "DHCP" : I18n.T("静态/未知")) + I18n.T(" | 网关: ") + String.Join(", ", a.Gateways) + " | DNS: " + String.Join(", ", a.AllDns.Count > 0 ? a.AllDns : a.Dns) + (a.InformationReadable ? "" : I18n.T(" | 信息读取不完整，不对此网卡提供修复")));
            if (!String.IsNullOrEmpty(snapshot.AdapterError)) details.AppendLine(I18n.T("网卡信息读取未完成：") + snapshot.AdapterError);
            DiagnosticItem adapter = Item("Adapters", I18n.T("网卡与 IPv4"), anyV4 ? (active.Any(delegate(AdapterSnapshot a) { return a.Apipa; }) ? CheckState.Warning : CheckState.Passed) : (snapshot.Online ? CheckState.Warning : (snapshot.AdapterError != null ? CheckState.Unknown : CheckState.Failed)), anyV4 ? I18n.T("发现已连接的非 APIPA IPv4 网卡") : (snapshot.Online ? I18n.T("未发现有效 IPv4，但 HTTP 已可用，可能使用 IPv6/隧道") : I18n.T("未发现已连接且有有效 IPv4 的网卡")), details.ToString() + I18n.T("APIPA（169.254.x.x）通常表示未取得 DHCP 地址；VPN、IPv6 网络和备用网卡需要结合实际用途判断。"));
            if (!snapshot.Online)
                foreach (AdapterSnapshot a in active.Where(delegate(AdapterSnapshot a) { return a.InformationReadable && a.Dhcp && (!a.UsableV4 || a.Apipa); }))
                    adapter.Actions.Add(new RepairAction { Id = "RenewDhcp", AdapterId = a.Id, Title = I18n.T("重新获取地址 · ") + a.Name, Description = I18n.T("该网卡当前使用 DHCP，尚未取得正常 IPv4。先检查网线/Wi-Fi；再向 DHCP 服务器重新申请租约。"), Impact = I18n.T("仅操作此网卡，连接可能短暂中断；DHCP 租约无法精确回滚。"), Suggested = !anyV4 });
            report.Items.Add(adapter);
            bool gatewayResponds = snapshot.GatewayProbes.Any(delegate(Probe p) { return p.Success; });
            report.Items.Add(Item("Gateway", I18n.T("默认网关"), gatewayResponds ? CheckState.Passed : CheckState.Unknown, gatewayResponds ? I18n.T("至少一个配置网关的 ICMP 或 TCP 端口有应答") : (gateway ? I18n.T("已配置 IPv4 网关，但探测未确认应答") : I18n.T("未发现可用 IPv4 网关")), ProbeDetails(snapshot.GatewayProbes) + Environment.NewLine + I18n.T("交叉探测最多四个网关的 ICMP 与 TCP 80/443。无应答可能是网关禁 Ping 或未开放管理端口，不能据此判定断网。VPN、点对点连接和 IPv6 可能使用独立路由；本工具不会改动路由或静态 IP。")));
            report.Items.Add(Item("DirectTcp", I18n.T("直连外网 TCP"), snapshot.Direct ? CheckState.Passed : CheckState.Unknown, snapshot.Direct ? I18n.T("至少一个外网 IP 的 TCP 443 可连接") : I18n.T("测试直连端点均未连接，不能据此单独判定断网"), ProbeDetails(snapshot.Tcp) + Environment.NewLine + I18n.T("不以 ICMP/Ping 失败判断断网；代理、VPN、公司策略或端点过滤会影响直连。")));
            DiagnosticItem dns = Item("Dns", I18n.T("系统 DNS"), snapshot.DnsWorks ? CheckState.Passed : (snapshot.Direct && snapshot.DnsFailed ? CheckState.Warning : CheckState.Unknown), snapshot.DnsWorks ? I18n.T("系统解析至少一个测试域名成功") : I18n.T("测试域名未解析成功"), ProbeDetails(snapshot.Dns) + Environment.NewLine + I18n.T("这是系统解析器的结果，可能含缓存；未直接测试每台 DNS 服务器。代理可在远端解析域名，系统 DNS 异常不等于代理网页不可用。"));
            if (HasOnlyLoopbackDns(snapshot)) dns.Detail += Environment.NewLine + I18n.T("活动网卡已配置的 DNS 全部指向本机。仅为这些 loopback 地址交叉探测 TCP 53 与实际 UDP DNS 响应：") + Environment.NewLine + ProbeDetails(snapshot.LocalDns);
            if (residualDns)
            {
                dns.State = CheckState.Warning;
                dns.Summary = I18n.T("系统 DNS 全指向本机，本地 DNS 服务未应答，可能为 VPN/DNS 客户端未启动");
                dns.Detail += Environment.NewLine + I18n.T("建议先启动原 VPN/DNS 客户端并正常断开、退出，再复检。无响应也可能是本地策略过滤，不能确定残留原因；不默认改 DNS，不自动清理 TUN、虚拟网卡或路由。");
            }
            bool automaticProxy = !snapshot.Proxy.AutoConfigKnown || snapshot.Proxy.AutoDetect || !String.IsNullOrWhiteSpace(snapshot.Proxy.Pac);
            if (snapshot.Direct && snapshot.DnsFailed && !automaticProxy)
            {
                if (!residualDns) dns.Actions.Add(new RepairAction { Id = "FlushDns", Title = I18n.T("清除 DNS 缓存"), Description = I18n.T("IP 直连成功，但多个系统域名解析失败。可清除本机缓存后复检；不保证解决 DNS 服务器或策略问题。"), Impact = I18n.T("不改变 DNS 服务器；现有缓存无法回滚。"), Suggested = !snapshot.Online });
                foreach (AdapterSnapshot a in active.Where(delegate(AdapterSnapshot a) { return a.InformationReadable && a.Dhcp && a.UsableV4 && a.DnsConfigurationReadable && !a.DnsAutomatic && a.ConfiguredDns.Count > 0; }))
                    dns.Actions.Add(new RepairAction { Id = "AutoDns", AdapterId = a.Id, Title = I18n.T("DNS 恢复自动 · ") + a.Name, Description = I18n.T("此 DHCP 网卡设置了自定义 DNS。解析异常尚不能证明此网卡的 DNS 就是根因；仅在确认不需要自定义 DNS 时选择。"), Impact = I18n.T("会替换此网卡自定义 IPv4 DNS，可能影响公司内网、过滤和分流；默认不选。备份后可恢复。"), Suggested = false });
            }
            report.Items.Add(dns);
            ProxySnapshot proxy = snapshot.Proxy;
            string proxySummary = !proxy.Readable ? I18n.T("无法读取当前用户代理") : proxy.Enabled ? I18n.T("手动代理已启用") : I18n.T("手动代理未启用");
            string proxyDetail = I18n.T("HTTP 诊断只读取自动代理配置，不执行 PAC/WPAD；无自动代理时按显式手动代理及绕过规则抽样，不发送默认用户凭据。");
            if (!String.IsNullOrWhiteSpace(proxy.Pac))
            {
                Uri pac;
                proxyDetail += Environment.NewLine + I18n.T("已配置 PAC：") + (Uri.TryCreate(proxy.Pac, UriKind.Absolute, out pac) ? pac.Scheme + "://" + pac.Host + I18n.T("/…（路径已隐藏）") : I18n.T("地址已隐藏，格式未识别")) + I18n.T("。PAC 脚本内容与自动发现结果未单独分析。");
            }
            if (proxy.Enabled) proxyDetail += Environment.NewLine + (proxy.Endpoints.Count > 0 ? ProbeDetails(proxy.Endpoints) : I18n.T("未识别可探测的代理端点，无法确认是否可达。")) + Environment.NewLine + I18n.T("本地代理（如 v2rayN）可能是有意配置；端口可达只说明进程监听，不保证代理上游可用。");
            if (snapshot.DirectHttp.Count > 0) proxyDetail += Environment.NewLine + I18n.T("绕过系统代理的 HTTP 对照（仅本次请求 Proxy=null，未修改系统代理配置）：") + Environment.NewLine + ProbeDetails(snapshot.DirectHttp);
            if (residualProxy)
            {
                proxySummary = I18n.T("本地代理端口均不可达，系统代理 HTTP 失败，而绕过代理的网页可达");
                proxyDetail += Environment.NewLine + I18n.T("此证据符合 VPN/代理打开时直接关机、重启后代理客户端未启动留下的手动代理配置。也可先启动 v2rayN 等原客户端。TUN、虚拟网卡、路由与 VPN 断网保护不在此判断范围内，本工具不会自动清理这些设置。");
            }
            CheckState proxyState = residualProxy ? CheckState.Failed : (!proxy.Readable ? CheckState.Unknown : (proxy.DefinitelyUnreachable && snapshot.Direct && !snapshot.Online ? CheckState.Warning : (snapshot.Online || !proxy.Enabled ? CheckState.Passed : CheckState.Unknown)));
            DiagnosticItem proxyItem = Item("Proxy", I18n.T("当前用户代理与 PAC"), proxyState, proxySummary, proxyDetail);
            if (residualProxy || (proxy.DefinitelyUnreachable && snapshot.Direct && !snapshot.Online))
                proxyItem.Actions.Add(new RepairAction { Id = "DisableProxy", Title = residualProxy ? I18n.T("关闭残留本地代理，恢复直连") : I18n.T("暂时关闭手动系统代理"), Description = residualProxy ? I18n.T("本机代理所有端口无响应，无 PAC；跟随系统代理 HTTP 失败，但绕过代理 HTTP 已成功。建议关闭未运行的本地客户端留下的手动代理；需要 VPN 时可重新启动原客户端。") : I18n.T("已启用的手动代理端口均不可达，而外网 IP 可直连。建议先启动或重启 v2rayN 等代理客户端；仍需直连时再选此项。"), Impact = I18n.T("关闭当前用户的手动系统代理，影响依赖代理的应用；保留代理地址，不修改 PAC。") + (residualProxy ? I18n.T("已取得本地残留代理强证据，执行前还会复查，备份后可恢复。") : I18n.T("默认不选，可从备份恢复。")), Suggested = residualProxy });
            report.Items.Add(proxyItem);
            bool markedOnline = snapshot.Http.Any(delegate(Probe p) { return p.Success && p.Marker; });
            report.Items.Add(Item("Http", I18n.T("HTTP 联网验证"), snapshot.Online ? CheckState.Passed : snapshot.Http.Any(delegate(Probe p) { return p.AutomaticProxySkipped; }) ? CheckState.Unknown : (snapshot.Http.Any(delegate(Probe p) { return p.Responded; }) ? CheckState.Warning : (!anyV4 && !snapshot.Direct ? CheckState.Failed : CheckState.Unknown)), snapshot.Online ? (markedOnline ? I18n.T("至少一个系统代理 HTTP 探测获得预期联网标记") : I18n.T("国内站点 TLS 及 HTTP 响应成功；未验证固定联网标记")) : I18n.T("尚未确认 HTTP 联网"), ProbeDetails(snapshot.Http) + Environment.NewLine + I18n.T("只代表这些测试端点；浏览器自有代理、DoH、网站限制与登录认证可能产生不同结果。国内 HTTPS 兜底仅验证 TLS 和状态码，不检查页面内容。")));
            report.Summary = snapshot.Online ? (markedOnline ? I18n.T("HTTP 联网已验证。可结合各项证据检查局部问题。") : I18n.T("已收到 HTTPS 响应；固定联网标记尚未验证。可结合各项证据检查局部问题。")) : (!anyV4 && !snapshot.Direct ? I18n.T("未取得可用 IPv4，且测试未确认联网。先检查网线、Wi-Fi 或代理/VPN。") : I18n.T("尚未确认联网；请依据具体证据排查，暂不做全面重置。"));
            if (residualProxy) report.Summary = I18n.T("发现未运行的本地代理残留：系统代理网页失败，但绕过代理的网页可达。符合 VPN 打开时直接关机后的常见情况，可备份后关闭残留代理并复检。");
            else if (residualDns && !snapshot.Online) report.Summary = I18n.T("系统 DNS 全指向本机且本地 DNS 服务未应答，可能是 VPN/DNS 客户端未启动。建议先启动原客户端并正常退出，再复检；不自动清理 DNS、TUN 或路由。");
            if (snapshot.Http.Any(delegate(Probe p) { return p.AutomaticProxySkipped; })) report.Summary = I18n.T("系统自动代理路径未验证，不能据此判断断网或 PAC 故障。") + (snapshot.DirectHttpWorks ? I18n.T("本次绕过代理的 HTTP 对照可用。") : I18n.T("本次直连对照也未确认，仍需结合自动代理客户端检查。"));
            AddEvidence(report, snapshot);
            return report;
        }

        private static void AddEvidence(DiagnosticReport report, Snapshot snapshot)
        {
            DiagnosticItem adapters = report.Items.First(delegate(DiagnosticItem item) { return item.Id == "Adapters"; });
            foreach (AdapterSnapshot adapter in snapshot.Adapters)
            {
                CheckState state = !adapter.InformationReadable ? CheckState.Unknown : !adapter.Up ? CheckState.Unknown : adapter.Apipa ? CheckState.Warning : adapter.UsableV4 ? CheckState.Passed : CheckState.Unknown;
                adapters.EvidenceGroups.Add(DiagnosticEvidence.Group(null, adapter.Name, state,
                    DiagnosticEvidence.Field("statusColumn", "状态", adapter.Up ? I18n.T("已连接") : I18n.T("未连接")),
                    DiagnosticEvidence.Field("adapterType", "接口类型", adapter.Type),
                    DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", adapter.InformationReadable ? I18n.T("通过") : I18n.T("未确定")),
                    DiagnosticEvidence.Field("adapterIpv4", "IPv4 地址", JoinEvidence(adapter.Addresses)),
                    DiagnosticEvidence.Field("adapterDhcp", "IP 获取方式", !adapter.InformationReadable ? I18n.T("未知") : adapter.Dhcp ? "DHCP" : I18n.T("静态/未知")),
                    DiagnosticEvidence.Field("adapterGateways", "默认网关", JoinEvidence(adapter.Gateways)),
                    DiagnosticEvidence.Field("adapterDns", "DNS 服务器", JoinEvidence(adapter.AllDns.Count > 0 ? adapter.AllDns : adapter.Dns))));
            }
            if (adapters.EvidenceGroups.Count == 0 || !String.IsNullOrEmpty(snapshot.AdapterError))
                adapters.EvidenceGroups.Add(DiagnosticEvidence.Group("connection", "网络连接配置", CheckState.Unknown, DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", snapshot.AdapterError ?? I18n.T("未读取"))));
            DiagnosticEvidence.AddProbes(report.Items.First(delegate(DiagnosticItem item) { return item.Id == "Gateway"; }), "adapterGateways", "默认网关", snapshot.GatewayProbes, false);
            DiagnosticEvidence.AddProbes(report.Items.First(delegate(DiagnosticItem item) { return item.Id == "DirectTcp"; }), "tcpTest", "TCP 端口测试", snapshot.Tcp, false);
            DiagnosticItem dns = report.Items.First(delegate(DiagnosticItem item) { return item.Id == "Dns"; });
            foreach (AdapterSnapshot adapter in snapshot.Adapters)
                dns.EvidenceGroups.Add(DiagnosticEvidence.Group(null, adapter.Name, null,
                    DiagnosticEvidence.Field("statusColumn", "状态", adapter.Up ? I18n.T("已连接") : I18n.T("未连接")),
                    DiagnosticEvidence.Field("adapterDns", "DNS 服务器", JoinEvidence(adapter.AllDns.Count > 0 ? adapter.AllDns : adapter.Dns)),
                    DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", adapter.InformationReadable ? I18n.T("通过") : I18n.T("未确定"))));
            DiagnosticEvidence.AddProbes(dns, "dns", "DNS 服务", snapshot.Dns, false);
            if (snapshot.LocalDns.Count > 0) DiagnosticEvidence.AddProbes(dns, "dns", "DNS 服务", snapshot.LocalDns, false);
            DiagnosticItem proxy = report.Items.First(delegate(DiagnosticItem item) { return item.Id == "Proxy"; });
            ProxySnapshot configuration = snapshot.Proxy;
            string pac = I18n.T("无");
            if (!String.IsNullOrWhiteSpace(configuration.Pac))
            {
                Uri uri;
                pac = Uri.TryCreate(configuration.Pac, UriKind.Absolute, out uri) ? uri.Scheme + "://" + uri.Host + I18n.T("/…（路径已隐藏）") : I18n.T("地址已隐藏，格式未识别");
            }
            proxy.EvidenceGroups.Add(DiagnosticEvidence.Group("browser", "浏览器配置", proxy.State,
                DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", configuration.Readable ? I18n.T("通过") : I18n.T("未确定")),
                DiagnosticEvidence.Field("evidenceManualProxy", "手动代理", DiagnosticEvidence.Boolean(configuration.Readable ? (bool?)configuration.Enabled : null)),
                DiagnosticEvidence.Field("evidenceAutoProxy", "自动代理", DiagnosticEvidence.Boolean(configuration.AutoConfigKnown ? (bool?)configuration.AutoDetect : null)),
                DiagnosticEvidence.Field(null, "PAC", configuration.AutoConfigKnown ? pac : I18n.T("未知"))));
            if (configuration.Enabled) DiagnosticEvidence.AddProbes(proxy, "browser", "浏览器配置", configuration.Endpoints, false);
            if (snapshot.DirectHttp.Count > 0) DiagnosticEvidence.AddProbes(proxy, "direct", "直连", snapshot.DirectHttp, true);
            DiagnosticEvidence.AddProbes(report.Items.First(delegate(DiagnosticItem item) { return item.Id == "Http"; }), "internet", "电脑能否上网", snapshot.Http, true);
        }

        private static string JoinEvidence(List<string> values)
        {
            return values.Count == 0 ? I18n.T("无") : String.Join(", ", values);
        }

        internal static bool IsAllowedAction(RepairAction action)
        {
            if (action == null) return false;
            if (action.Id == "FlushDns" || action.Id == "DisableProxy") return String.IsNullOrEmpty(action.AdapterId);
            Guid id;
            return (action.Id == "RenewDhcp" || action.Id == "AutoDns") && Guid.TryParseExact(action.AdapterId, "D", out id) && id != Guid.Empty;
        }

        internal static void RecordBeforeSample(RepairResult result, Snapshot snapshot, DateTime sampledUtc)
        {
            result.BeforeInternetAvailable = null;
            result.BeforeSampledUtc = null;
            if (snapshot == null || snapshot.Http.Count == 0 || snapshot.Http.Any(delegate(Probe p) { return p.AutomaticProxySkipped; }) || snapshot.Proxy == null || !snapshot.Proxy.AutoConfigKnown || snapshot.Proxy.AutoDetect || !String.IsNullOrWhiteSpace(snapshot.Proxy.Pac)) return;
            result.BeforeSampledUtc = sampledUtc;
            if (snapshot.Online) result.BeforeInternetAvailable = true;
            else if (snapshot.Http.All(delegate(Probe p) { return !p.Uncertain; })) result.BeforeInternetAvailable = false;
        }

        private static RepairItemResult NewRepairItem(string actionId, string adapterId)
        {
            return new RepairItemResult { ActionId = actionId, AdapterId = adapterId, State = RepairItemState.Prepared, Code = "Prepared" };
        }

        private static void SetPendingItems(RepairResult result, RepairItemState state, string code, string message)
        {
            foreach (RepairItemResult item in result.Items.Where(delegate(RepairItemResult i) { return i.State == RepairItemState.Prepared; }))
            { item.State = state; item.Code = code; item.Message = message; }
        }

        private static void AttachOperation(RepairResult result, RescueBackup backup, string kind)
        {
            result.OperationId = Guid.NewGuid().ToString("D");
            backup.Operation = new RepairOperationRecord { OperationId = result.OperationId, Kind = kind, StartedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Items = result.Items };
        }

        internal static void SummarizeItems(RepairResult result)
        {
            List<RepairItemResult> items = result.Items.Where(delegate(RepairItemResult i) { return i.State != RepairItemState.NotRestorable; }).ToList();
            result.RestartRequired = items.Any(delegate(RepairItemResult i) { return i.RestartRequired || i.State == RepairItemState.RestartRequired; });
            result.Success = items.Count > 0 && items.All(delegate(RepairItemResult i) { return i.State == RepairItemState.Completed; });
            result.PartialSuccess = items.Any(delegate(RepairItemResult i) { return i.State == RepairItemState.Completed; }) && !result.Success;
            if (items.Count == 0) { result.Outcome = RepairOutcome.NoChange; return; }
            if (result.Success) result.Outcome = RepairOutcome.Completed;
            else if (items.All(delegate(RepairItemResult i) { return i.State == RepairItemState.Completed || i.State == RepairItemState.RestartRequired; })) result.Outcome = RepairOutcome.RestartRequired;
            else if (items.Any(delegate(RepairItemResult i) { return i.State == RepairItemState.Completed || i.State == RepairItemState.RestartRequired; })) result.Outcome = RepairOutcome.Partial;
            else if (items.Any(delegate(RepairItemResult i) { return i.State == RepairItemState.Writing || i.State == RepairItemState.Prepared || i.State == RepairItemState.Unconfirmed; })) result.Outcome = RepairOutcome.NeedsReview;
            else if (items.All(delegate(RepairItemResult i) { return i.State == RepairItemState.Rejected; })) result.Outcome = RepairOutcome.Rejected;
            else if (items.All(delegate(RepairItemResult i) { return i.State == RepairItemState.Cancelled; })) result.Outcome = RepairOutcome.Cancelled;
            else result.Outcome = RepairOutcome.Failed;
        }

        private static bool PersistOperation(RepairResult result, Action persist)
        {
            try { persist(); result.JournalSaved = true; return true; }
            catch (Exception)
            {
                result.JournalSaved = false;
                string warning = I18n.T("操作记录未能保存；已开始的设置可能已改变，请重新诊断并保留备份。");
                if (!result.Details.Contains(warning)) result.Details.Add(warning);
                return false;
            }
        }

        internal static void BeginItemWrite(RepairItemResult item, Action persist, Action resetMarker)
        {
            item.State = RepairItemState.Writing;
            item.Code = "Writing";
            item.WriteStarted = item.MayHaveChanged = true;
            try { persist(); }
            catch (Exception ex)
            {
                item.State = RepairItemState.Prepared;
                item.WriteStarted = item.MayHaveChanged = false;
                resetMarker();
                throw new BackupCheckpointException(ex);
            }
        }

        // The delegate is also the isolated fault-injection seam; production delegates keep their existing preconditions.
        internal static bool RunTrackedItem(RepairResult result, RepairItemResult item, Action execute, Action persist, CancellationToken token)
        {
            bool canContinue = true;
            try
            {
                token.ThrowIfCancellationRequested();
                execute();
                item.State = RepairItemState.Completed; item.Code = "Completed";
                item.Message = I18n.T("操作已完成，仍需重新诊断验证。");
            }
            catch (RepairRefusedException ex)
            { item.State = RepairItemState.Rejected; item.Code = ex.Code; item.Message = ex.Message; item.MayHaveChanged = false; }
            catch (RestartNeededException ex)
            { item.State = RepairItemState.RestartRequired; item.Code = "RestartRequired"; item.Message = ex.Message; item.RestartRequired = true; item.MayHaveChanged = true; item.NativeCode = 1; }
            catch (BackupCheckpointException ex)
            { item.State = RepairItemState.Failed; item.Code = "BackupWriteFailed"; item.Message = SafeError(ex.InnerException); canContinue = false; }
            catch (OperationCanceledException)
            { item.State = item.WriteStarted ? RepairItemState.Unconfirmed : RepairItemState.Cancelled; item.Code = item.WriteStarted ? "CancelledAfterWrite" : "Cancelled"; item.Message = I18n.T("操作已取消；已开始的步骤不会自动回滚。"); }
            catch (RepairExecutionException ex)
            { item.MayHaveChanged = ex.MayHaveChanged; item.State = ex.MayHaveChanged ? RepairItemState.Unconfirmed : RepairItemState.Failed; item.Code = ex.Code; item.Message = ex.Message; item.NativeCode = ex.NativeCode; }
            catch (Exception ex)
            { item.State = item.WriteStarted ? RepairItemState.Unconfirmed : RepairItemState.Failed; item.Code = item.WriteStarted ? "WriteUnconfirmed" : "ExecutionFailed"; item.Message = SafeError(ex); }
            if (item.State != RepairItemState.Completed) result.Details.Add(item.ActionId + I18n.T(" 未完成：") + item.Message);
            return PersistOperation(result, persist) && canContinue;
        }

        private static void FinishOperation(RepairResult result, RescueBackup backup, Action persist)
        {
            SummarizeItems(result);
            if (backup == null || backup.Operation == null || String.IsNullOrEmpty(result.BackupPath)) return;
            backup.Operation.Finished = true;
            backup.Operation.Outcome = result.Outcome;
            backup.Operation.UpdatedUtc = DateTime.UtcNow;
            if (!PersistOperation(result, persist)) { result.Success = false; result.Outcome = RepairOutcome.NeedsReview; }
        }

        internal static void AbortOperation(RepairResult result, RescueBackup backup, Action persist, Exception error)
        {
            bool cancelled = error is OperationCanceledException;
            RepairRefusedException refusal = error as RepairRefusedException;
            result.Code = cancelled ? "Cancelled" : refusal != null ? refusal.Code : "PreparationFailed";
            SetPendingItems(result, cancelled ? RepairItemState.Cancelled : refusal != null ? RepairItemState.Rejected : RepairItemState.Failed, cancelled ? "Cancelled" : refusal != null ? refusal.Code : "PreparationFailed", cancelled ? I18n.T("操作已取消；已开始的步骤不会自动回滚。") : SafeError(error));
            FinishOperation(result, backup, persist);
            if (result.Items.Count == 0) result.Outcome = cancelled ? RepairOutcome.Cancelled : refusal != null || error is InvalidDataException ? RepairOutcome.Rejected : RepairOutcome.Failed;
            result.Success = false;
        }

        public async Task<RepairResult> RepairAsync(IList<RepairAction> actions, IProgress<string> progress, CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            RepairResult result = new RepairResult();
            RescueBackup backup = null;
            FileStream writeLock = null;
            Action persist = delegate { SaveBackup(backup, result.BackupPath); };
            try
            {
                if (actions == null || actions.Count == 0) { result.Outcome = RepairOutcome.NoChange; result.Summary = I18n.T("未选择修复操作。"); return result; }
                if (actions.Count > 64 || actions.Any(delegate(RepairAction a) { return !IsAllowedAction(a); }))
                {
                    result.Outcome = RepairOutcome.Rejected; result.Summary = I18n.T("操作不在安全白名单内，已拒绝执行。");
                    result.Items.Add(new RepairItemResult { ActionId = "InvalidAction", State = RepairItemState.Rejected, Code = "InvalidAction", Message = result.Summary });
                    return result;
                }
                List<RepairAction> selected = actions.GroupBy(delegate(RepairAction a) { return a.Id + ":" + a.AdapterId; }, StringComparer.OrdinalIgnoreCase).Select(delegate(IGrouping<string, RepairAction> group) { return new RepairAction { Id = group.First().Id, AdapterId = group.First().AdapterId, Suggested = group.Any(delegate(RepairAction a) { return a.Suggested; }) }; }).ToList();
                result.Items.AddRange(selected.Select(delegate(RepairAction a) { return NewRepairItem(a.Id, a.AdapterId); }));
                writeLock = AcquireOperationLock();
                Snapshot fresh = await CaptureAsync(progress, token).ConfigureAwait(false);
                RecordBeforeSample(result, fresh, DateTime.UtcNow);
                List<RepairAction> allowed = BuildReport(fresh).Items.SelectMany(delegate(DiagnosticItem item) { return item.Actions; }).ToList();
                if (selected.Any(delegate(RepairAction a) { return !allowed.Any(delegate(RepairAction b) { return a.Id == b.Id && a.AdapterId == b.AdapterId; }); }))
                { result.Summary = I18n.T("网络状态已变化，所选操作不再满足诊断前提。请重新诊断。"); SetPendingItems(result, RepairItemState.Rejected, "PreconditionChanged", result.Summary); SummarizeItems(result); return result; }
                if (selected.Any(delegate(RepairAction a) { return a.Id == "DisableProxy" && a.Suggested; }) && !HasResidualLocalProxy(fresh))
                { result.Summary = I18n.T("本地代理残留的强证据已变化，已跳过新手代理修复。请重新诊断。"); SetPendingItems(result, RepairItemState.Rejected, "PreconditionChanged", result.Summary); SummarizeItems(result); return result; }
                if (HasResidualLocalProxy(fresh))
                    foreach (RepairAction action in selected.Where(delegate(RepairAction a) { return a.Id == "DisableProxy"; })) action.Suggested = true;
                backup = CreateBackup(fresh);
                AttachOperation(result, backup, "Repair");
                result.BackupPath = SaveBackup(backup, null);
                result.JournalSaved = true;
                Notify(progress, I18n.T("备份已保存。正在按所选项目执行……"));
                for (int i = 0; i < selected.Count; i++)
                {
                    RepairAction action = selected[i]; RepairItemResult item = result.Items[i];
                    bool keepGoing = await Task.Run(delegate { return RunTrackedItem(result, item, delegate { ApplyAction(action, backup, result.BackupPath, result.Details, item, persist); }, persist, token); }).ConfigureAwait(false);
                    if (!keepGoing)
                    {
                        SetPendingItems(result, RepairItemState.Rejected, "JournalUnavailable", I18n.T("备份记录无法可靠保存，未开始其余操作。"));
                        break;
                    }
                }
                FinishOperation(result, backup, persist);
                result.Summary = result.Success ? I18n.T("所选操作已执行。请重新诊断验证联网结果。") : result.Outcome == RepairOutcome.RestartRequired ? I18n.T("Windows 返回需重启，设置是否生效仍需复检；程序不会自动重启。") : I18n.T("部分操作未完成或已跳过。请查看明细并重新诊断。");
                result.Details.Add(I18n.T("备份不回滚 DNS 缓存与 DHCP 租约；不会自动重启电脑。修改网络设置可能需要管理员权限。"));
                return result;
            }
            catch (OperationCanceledException)
            { AbortOperation(result, backup, persist, new OperationCanceledException()); result.Summary = I18n.T("操作已取消；已开始的步骤不会自动回滚。"); return result; }
            catch (Exception ex)
            { AbortOperation(result, backup, persist, ex); result.Summary = I18n.T("未完成修复：") + SafeError(ex); return result; }
            finally { try { if (writeLock != null) writeLock.Dispose(); } finally { operation.Release(); } }
        }

        private static RescueBackup CreateBackup(Snapshot snapshot)
        {
            if (!snapshot.Proxy.Readable || snapshot.AdapterError != null) throw new InvalidOperationException(I18n.T("配置读取不完整，无法建立可靠备份。"));
            RescueBackup backup = new RescueBackup { ProxyEnableExists = snapshot.Proxy.EnableExists, ProxyEnableValue = snapshot.Proxy.EnableValue, ProxyServer = snapshot.Proxy.Server, ProxyPac = snapshot.Proxy.Pac, ProxyOverride = snapshot.Proxy.Override };
            foreach (AdapterSnapshot a in snapshot.Adapters)
                backup.Adapters.Add(new AdapterBackup { Id = a.Id, Name = a.Name, Dhcp = a.Dhcp, DnsAutomatic = a.DnsAutomatic, Addresses = a.Addresses.ToArray(), Gateways = a.Gateways.ToArray(), Dns = (a.DnsConfigurationReadable && !a.DnsAutomatic ? a.ConfiguredDns : a.Dns).ToArray() });
            return backup;
        }

        private void ApplyAction(RepairAction action, RescueBackup backup, string backupPath, List<string> details, RepairItemResult item, Action persist)
        {
            if (!IsAllowedAction(action)) throw new RepairRefusedException("InvalidAction", I18n.T("非法操作。"));
            if (action.Id == "FlushDns")
            {
                if (!TcpProbe("1.1.1.1", 443).Success && !TcpProbe("8.8.8.8", 443).Success && !TcpProbe("223.5.5.5", 443).Success) throw new RepairRefusedException("PreconditionChanged", I18n.T("直连前提已变化，跳过清除缓存。"));
                if (DnsProbe("www.microsoft.com").Success || DnsProbe("www.cloudflare.com").Success || DnsProbe("www.baidu.com").Success) throw new RepairRefusedException("PreconditionChanged", I18n.T("系统 DNS 已恢复，跳过清除缓存。"));
                backup.FlushStarted = true;
                BeginItemWrite(item, persist, delegate { backup.FlushStarted = false; });
                RunIpconfigFlush();
                details.Add(I18n.T("已清除 DNS 缓存；缓存本身无法回滚。"));
                return;
            }
            if (action.Id == "DisableProxy")
            {
                Snapshot verified = CaptureAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                ProxySnapshot current = verified.Proxy;
                bool stillAllowed = BuildReport(verified).Items.SelectMany(delegate(DiagnosticItem check) { return check.Actions; }).Any(delegate(RepairAction available) { return available.Id == "DisableProxy"; });
                if (!stillAllowed || (action.Suggested && !HasResidualLocalProxy(verified))) throw new RepairRefusedException("PreconditionChanged", I18n.T("代理或直连网页状态已变化，跳过关闭代理。"));
                current = ReadProxy();
                ProbeProxy(current);
                if (!current.DefinitelyUnreachable || current.Server != verified.Proxy.Server || current.Pac != verified.Proxy.Pac || current.Override != verified.Proxy.Override || current.EnableValue != verified.Proxy.EnableValue) throw new RepairRefusedException("PreconditionChanged", I18n.T("代理已恢复监听或配置刚刚变化，已跳过关闭代理。"));
                if (current.Server != backup.ProxyServer || current.Pac != backup.ProxyPac || current.Override != backup.ProxyOverride || current.EnableValue != backup.ProxyEnableValue) throw new RepairRefusedException("PreconditionChanged", I18n.T("代理配置在备份后已变化，请重新诊断。"));
                backup.ProxyStarted = true;
                BeginItemWrite(item, persist, delegate { backup.ProxyStarted = false; });
                SetProxyEnable(false, 0);
                details.Add(I18n.T("已关闭当前用户手动系统代理；地址和 PAC 保留。请重新诊断。"));
                return;
            }
            AdapterSnapshot adapter = ReadAdapters().FirstOrDefault(delegate(AdapterSnapshot a) { return a.Id == action.AdapterId; });
            AdapterBackup saved = backup.Adapters.FirstOrDefault(delegate(AdapterBackup a) { return a.Id == action.AdapterId; });
            if (adapter == null || saved == null || !adapter.InformationReadable || !adapter.Up || !adapter.Dhcp) throw new RepairRefusedException("PreconditionChanged", I18n.T("目标网卡无法完整读取、已离线、消失或不再使用 DHCP，已跳过。"));
            if (action.Id == "RenewDhcp")
            {
                if (adapter.UsableV4 && !adapter.Apipa) throw new RepairRefusedException("PreconditionChanged", I18n.T("此网卡已取得正常 IPv4，已跳过续租。"));
                saved.RenewStarted = true;
                BeginItemWrite(item, persist, delegate { saved.RenewStarted = false; });
                InvokeAdapter(adapter.Id, "RenewDHCPLease", null);
                details.Add(I18n.T("已请求 DHCP 续租：") + adapter.Name + I18n.T("。租约与地址由 DHCP 服务器决定，无法精确回滚。"));
            }
            else
            {
                if (adapter.DnsAutomatic) throw new RepairRefusedException("PreconditionChanged", I18n.T("DNS 已为自动获取，已跳过。"));
                if (!adapter.DnsConfigurationReadable || adapter.ConfiguredDns.Count == 0) throw new RepairRefusedException("PreconditionChanged", I18n.T("原始自定义 DNS 无法完整读取，不能安全备份，已跳过。"));
                if (DnsProbe("www.microsoft.com").Success || DnsProbe("www.cloudflare.com").Success || DnsProbe("www.baidu.com").Success) throw new RepairRefusedException("PreconditionChanged", I18n.T("系统 DNS 已恢复，已跳过更改 DNS。"));
                if (!TcpProbe("1.1.1.1", 443).Success && !TcpProbe("8.8.8.8", 443).Success && !TcpProbe("223.5.5.5", 443).Success) throw new RepairRefusedException("PreconditionChanged", I18n.T("直连前提已变化，已跳过更改 DNS。"));
                if (saved.DnsAutomatic != adapter.DnsAutomatic || !saved.Dns.SequenceEqual(adapter.ConfiguredDns)) throw new RepairRefusedException("PreconditionChanged", I18n.T("DNS 在备份后已变化，请重新诊断。"));
                saved.DnsStarted = true;
                BeginItemWrite(item, persist, delegate { saved.DnsStarted = false; });
                InvokeAdapter(adapter.Id, "SetDNSServerSearchOrder", null);
                details.Add(I18n.T("已将此 DHCP 网卡的 IPv4 DNS 恢复自动：") + adapter.Name + I18n.T("。请复检内网和外网。"));
            }
        }

        private static void RunIpconfigFlush()
        {
            using (Process process = new Process())
            {
                process.StartInfo = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "ipconfig.exe"), "/flushdns") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                try
                {
                    if (!process.Start()) throw new RepairExecutionException("ProcessStartFailed", false, I18n.T("无法启动 ipconfig。"));
                }
                catch (RepairExecutionException) { throw; }
                catch (Exception ex) { throw new RepairExecutionException("ProcessStartFailed", false, SafeError(ex)); }
                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) throw new RepairExecutionException("ProcessTimeout", true, I18n.T("DNS 缓存操作等待超时，进程未被强制终止，状态需复检。"));
                Task.WaitAll(new Task[] { stdout, stderr }, 2000);
                if (process.ExitCode != 0) throw new RepairExecutionException("ProcessExitCode", true, I18n.T("ipconfig 返回 ") + process.ExitCode.ToString(CultureInfo.InvariantCulture) + I18n.T("；可能需要管理员权限。"), process.ExitCode);
            }
        }

        private static void InvokeAdapter(string adapterId, string method, string[] dns)
        {
            Guid id;
            if (!Guid.TryParseExact(adapterId, "D", out id) || id == Guid.Empty || (method != "SetDNSServerSearchOrder" && method != "RenewDHCPLease")) throw new RepairRefusedException("InvalidTarget", I18n.T("非法网卡或方法。"));
            string query = "SELECT * FROM Win32_NetworkAdapterConfiguration WHERE SettingID='{" + id.ToString("D") + "}'";
            using (ManagementObjectSearcher searcher = new ManagementObjectSearcher(query))
            {
                searcher.Options.Timeout = TimeSpan.FromSeconds(15);
                using (ManagementObjectCollection objects = searcher.Get())
                {
                    foreach (ManagementObject adapter in objects)
                    {
                        using (adapter)
                        {
                            if (!(adapter["DHCPEnabled"] is bool) || !(bool)adapter["DHCPEnabled"]) throw new RepairRefusedException("PreconditionChanged", I18n.T("网卡当前不是 DHCP，已拒绝修改。"));
                            using (ManagementBaseObject input = adapter.GetMethodParameters(method))
                            {
                                if (method == "SetDNSServerSearchOrder") input["DNSServerSearchOrder"] = dns;
                                using (ManagementBaseObject output = adapter.InvokeMethod(method, input, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(20) }))
                                {
                                    uint code = Convert.ToUInt32(output["ReturnValue"], CultureInfo.InvariantCulture);
                                    if (code == 1) throw new RestartNeededException();
                                    if (code != 0) throw new RepairExecutionException("WmiReturnCode", true, I18n.T("Windows 网络方法返回代码 ") + code.ToString(CultureInfo.InvariantCulture) + I18n.T("；未确认成功，可能需要管理员权限或 DHCP 服务。"), code);
                                }
                            }
                            return;
                        }
                    }
                }
            }
            throw new RepairRefusedException("AdapterMissing", I18n.T("未找到目标网卡的 WMI 配置。"));
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

        private static void SetProxyEnable(bool remove, int value)
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(InternetKey, true))
            {
                if (key == null) throw new RepairExecutionException("ProxyKeyUnavailable", false, I18n.T("无法打开当前用户代理配置。"));
                if (remove) key.DeleteValue("ProxyEnable", false);
                else key.SetValue("ProxyEnable", value, RegistryValueKind.DWord);
                key.Flush();
            }
            bool changed = InternetSetOption(IntPtr.Zero, 39, IntPtr.Zero, 0);
            bool refreshed = InternetSetOption(IntPtr.Zero, 37, IntPtr.Zero, 0);
            if (!changed || !refreshed) throw new RepairExecutionException("ProxyRefreshUnconfirmed", true, I18n.T("代理注册表已写入，但系统刷新未确认；请重开相关应用后复检。"));
        }

        internal static bool IsAllowedBackupPath(string path, string root)
        {
            try
            {
                string full = Path.GetFullPath(path);
                string directory = Path.GetDirectoryName(full);
                string expected = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return String.Equals(directory, expected, StringComparison.OrdinalIgnoreCase) && String.Equals(Path.GetExtension(full), ".nrb", StringComparison.OrdinalIgnoreCase) && !String.IsNullOrEmpty(Path.GetFileNameWithoutExtension(full)) && full.IndexOf(':', 3) < 0;
            }
            catch (Exception) { return false; }
        }

        private void CheckBackupDirectory(bool create = true)
        {
            if (create) Directory.CreateDirectory(BackupRoot);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(BackupRoot); }
            catch (FileNotFoundException) { throw new DirectoryNotFoundException(); }
            if ((attributes & FileAttributes.Directory) == 0) throw new IOException(I18n.T("备份路径不安全。"));
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException(I18n.T("备份目录本身不能是重解析点或链接。"));
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct LockFileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateLockFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out LockFileInformation information);

        internal FileStream AcquireOperationLock()
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle = null;
            try
            {
                CheckBackupDirectory();
                string root = Path.GetFullPath(BackupRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string path = Path.Combine(root, "network-operation.lock");
                if (!String.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase) || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new IOException(I18n.T("备份路径不安全。"));
                // OPEN_ALWAYS + share-none keeps a handle, not a permanent busy marker. OPEN_REPARSE_POINT never opens a final symlink target.
                handle = CreateLockFile(path, 0xC0000000, 0, IntPtr.Zero, 4, 0x00200080, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 32 || error == 33) throw new RepairRefusedException("OperationBusy", I18n.T("另一个实例正在修复或还原，请等待完成后重试。"));
                    throw new IOException(I18n.T("备份路径不安全。"));
                }
                LockFileInformation information;
                if (!GetFileInformationByHandle(handle, out information) || (information.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0 || information.Links != 1) throw new IOException(I18n.T("备份路径不安全。"));
                CheckBackupDirectory(false);
                FileStream stream = new FileStream(handle, FileAccess.ReadWrite, 1, false);
                handle = null; // Ownership moves to the stream and remains valid across await/continuation threads.
                return stream;
            }
            catch (RepairRefusedException) { throw; }
            catch (Exception) { throw new RepairRefusedException("OperationLockUnavailable", I18n.T("备份路径不安全。")); }
            finally { if (handle != null) handle.Dispose(); }
        }

        private string SaveBackup(RescueBackup backup, string existing)
        {
            if (backup != null && backup.Operation != null)
            {
                backup.Operation.UpdatedUtc = DateTime.UtcNow;
                if (!backup.Operation.Finished) backup.Operation.Outcome = backup.Operation.Items.Any(delegate(RepairItemResult i) { return i.State == RepairItemState.Writing; }) ? RepairOutcome.NeedsReview : RepairOutcome.NotStarted;
            }
            ValidateBackup(backup);
            CheckBackupDirectory();
            string path = existing ?? Path.Combine(BackupRoot, "network-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".nrb");
            if (!IsAllowedBackupPath(path, BackupRoot) || (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)) throw new InvalidOperationException(I18n.T("备份路径不安全。"));
            byte[] serialized;
            using (MemoryStream buffer = new MemoryStream())
            {
                new XmlSerializer(typeof(RescueBackup)).Serialize(buffer, backup);
                serialized = buffer.ToArray();
            }
            byte[] protectedData = null;
            string temp = null;
            try
            {
                protectedData = ProtectedData.Protect(serialized, Entropy, DataProtectionScope.CurrentUser);
                if (protectedData.Length > 1024 * 1024) throw new InvalidDataException(I18n.T("备份大小不合法。"));
                temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(protectedData, 0, protectedData.Length); stream.Flush(true); }
                if (File.Exists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
                if (!File.Exists(path) || new FileInfo(path).Length == 0) throw new IOException(I18n.T("备份未保存。"));
            }
            finally { if (temp != null && File.Exists(temp)) File.Delete(temp); Array.Clear(serialized, 0, serialized.Length); if (protectedData != null) Array.Clear(protectedData, 0, protectedData.Length); }
            return path;
        }

        internal static void ValidateBackup(RescueBackup backup)
        {
            if (backup == null || backup.Schema != 1 || backup.Adapters == null || backup.Adapters.Count > 128 || backup.ProxyEnableValue < 0 || backup.ProxyEnableValue > 1) throw new InvalidDataException(I18n.T("不支持或损坏的备份结构。"));
            if (backup.ProxyServer == null || backup.ProxyPac == null || backup.ProxyOverride == null || backup.ProxyServer.Length > 8192 || backup.ProxyPac.Length > 8192 || backup.ProxyOverride.Length > 16384) throw new InvalidDataException(I18n.T("代理备份字段不合法。"));
            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AdapterBackup a in backup.Adapters)
            {
                Guid id;
                if (a == null || !Guid.TryParseExact(a.Id, "D", out id) || id == Guid.Empty || !ids.Add(a.Id) || a.Name == null || a.Name.Length > 1024 || a.Dns == null || a.Dns.Length > 32 || a.Addresses == null || a.Addresses.Length > 128 || a.Gateways == null || a.Gateways.Length > 32 || (a.DnsStarted && !a.Dhcp)) throw new InvalidDataException(I18n.T("网卡备份字段不合法。"));
                foreach (string address in a.Dns.Concat(a.Addresses).Concat(a.Gateways))
                {
                    IPAddress ip;
                    if (address == null || address.Length > 45 || !IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork) throw new InvalidDataException(I18n.T("备份包含不合法的 IPv4 地址。"));
                }
                if (a.DnsStarted && !a.DnsAutomatic && a.Dns.Length == 0) throw new InvalidDataException(I18n.T("自定义 DNS 备份为空，不能恢复。"));
            }
            ValidateOperation(backup);
        }

        private static void ValidateOperation(RescueBackup backup)
        {
            RepairOperationRecord op = backup.Operation;
            if (op == null) return; // Existing schema-1 backups have no operation journal.
            Guid operationId;
            bool invalid = op.Schema != 1 || !Guid.TryParseExact(op.OperationId, "D", out operationId) || operationId == Guid.Empty || (op.Kind != "Repair" && op.Kind != "Restore") || op.StartedUtc == DateTime.MinValue || op.UpdatedUtc == DateTime.MinValue || !Enum.IsDefined(typeof(RepairOutcome), op.Outcome) || op.Items == null || op.Items.Count > (op.Kind == "Repair" ? 64 : 258);
            if (invalid) throw new InvalidDataException(I18n.T("不支持或损坏的备份结构。"));
            HashSet<string> keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RepairItemResult item in op.Items)
            {
                if (item == null) throw new InvalidDataException(I18n.T("不支持或损坏的备份结构。"));
                bool allowed = op.Kind == "Repair" ? IsAllowedAction(new RepairAction { Id = item.ActionId, AdapterId = item.AdapterId }) : (item.ActionId == "RestoreProxy" || item.ActionId == "FlushDns") ? String.IsNullOrEmpty(item.AdapterId) : (item.ActionId == "RestoreDns" || item.ActionId == "RenewDhcp") && IsAllowedAction(new RepairAction { Id = "AutoDns", AdapterId = item.AdapterId });
                bool adapterFound = op.Kind == "Restore" || String.IsNullOrEmpty(item.AdapterId) || backup.Adapters.Any(delegate(AdapterBackup a) { return String.Equals(a.Id, item.AdapterId, StringComparison.OrdinalIgnoreCase); });
                if (!allowed || !adapterFound || !keys.Add(item.ActionId + ":" + item.AdapterId) || !Enum.IsDefined(typeof(RepairItemState), item.State) || !IsOperationCode(item.Code) || item.Message != null && item.Message.Length > 4096 || item.NativeCode.HasValue && (item.NativeCode.Value < Int32.MinValue || item.NativeCode.Value > UInt32.MaxValue) || item.MayHaveChanged && !item.WriteStarted || item.RestartRequired != (item.State == RepairItemState.RestartRequired) || (item.State == RepairItemState.Writing || item.State == RepairItemState.Completed || item.State == RepairItemState.RestartRequired) && !item.WriteStarted || op.Finished && (item.State == RepairItemState.Prepared || item.State == RepairItemState.Writing)) throw new InvalidDataException(I18n.T("不支持或损坏的备份结构。"));
            }
            if (op.Finished && op.Outcome == RepairOutcome.NotStarted) throw new InvalidDataException(I18n.T("不支持或损坏的备份结构。"));
        }

        private static bool IsOperationCode(string code)
        {
            switch (code)
            {
                case "Prepared": case "Writing": case "Completed": case "PreconditionChanged": case "InvalidAction": case "InvalidTarget": case "AdapterMissing": case "RestartRequired": case "BackupWriteFailed": case "CancelledAfterWrite": case "Cancelled": case "WmiReturnCode": case "ProcessStartFailed": case "ProcessTimeout": case "ProcessExitCode": case "ProxyKeyUnavailable": case "ProxyRefreshUnconfirmed": case "WriteUnconfirmed": case "ExecutionFailed": case "JournalUnavailable": case "PreparationFailed": case "NotRestorable": case "OperationBusy": case "OperationLockUnavailable": return true;
                default: return false;
            }
        }

        private RescueBackup LoadBackup(string path)
        {
            CheckBackupDirectory(false);
            if (!IsAllowedBackupPath(path, BackupRoot) || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(I18n.T("只允许恢复本程序备份目录内的 .nrb 文件。"));
            FileInfo file = new FileInfo(path);
            if (file.Length == 0 || file.Length > 1024 * 1024) throw new InvalidDataException(I18n.T("备份大小不合法。"));
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            try
            {
                using (MemoryStream buffer = new MemoryStream(plain))
                using (XmlReader reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 }))
                {
                    RescueBackup backup = (RescueBackup)new XmlSerializer(typeof(RescueBackup)).Deserialize(reader);
                    ValidateBackup(backup);
                    return backup;
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        public bool BackupNeedsAdministrator(string backupFile)
        {
            return LoadBackup(backupFile).Adapters.Any(delegate(AdapterBackup a) { return a.DnsStarted; });
        }

        public BackupOperationInfo ReadBackupOperation(string backupFile)
        {
            RepairOperationRecord op = LoadBackup(backupFile).Operation;
            if (op == null) return null;
            BackupOperationInfo info = new BackupOperationInfo { BackupPath = Path.GetFullPath(backupFile), OperationId = op.OperationId, Kind = op.Kind, StartedUtc = op.StartedUtc, Outcome = op.Outcome, Finished = op.Finished };
            foreach (RepairItemResult item in op.Items)
                info.Items.Add(new RepairItemResult { ActionId = item.ActionId, AdapterId = item.AdapterId, State = item.State, Code = item.Code, WriteStarted = item.WriteStarted, MayHaveChanged = item.MayHaveChanged, RestartRequired = item.RestartRequired, NativeCode = item.NativeCode });
            // Stored/free-form messages and all original network settings deliberately stay inside the protected backup.
            return info;
        }

        public BackupOperationInfo FindIncompleteOperation()
        {
            try
            {
                CheckBackupDirectory(false);
                List<FileInfo> candidates = new List<FileInfo>();
                foreach (FileInfo file in new DirectoryInfo(BackupRoot).EnumerateFiles("*.nrb", SearchOption.TopDirectoryOnly))
                {
                    try { if ((file.Attributes & FileAttributes.ReparsePoint) == 0) { DateTime written = file.LastWriteTimeUtc; candidates.Add(file); } }
                    catch (Exception) { /* Skip a single inaccessible file before sorting. */ }
                }
                candidates = candidates.OrderByDescending(delegate(FileInfo f) { return f.LastWriteTimeUtc; }).ThenByDescending(delegate(FileInfo f) { return f.Name; }, StringComparer.OrdinalIgnoreCase).Take(100).ToList();
                foreach (FileInfo file in candidates)
                {
                    try
                    {
                        BackupOperationInfo info = ReadBackupOperation(file.FullName);
                        if (info != null && !info.Finished && info.Items.Count > 0) return info;
                    }
                    catch (Exception) { /* One corrupt, inaccessible or concurrently replaced file must not hide the rest. */ }
                }
            }
            catch (DirectoryNotFoundException) { return null; }
            catch (Exception) { throw new IOException(I18n.T("备份路径不安全。")); }
            return null;
        }

        public async Task<RepairResult> RestoreAsync(string backupFile, IProgress<string> progress, CancellationToken token)
        {
            await operation.WaitAsync(token).ConfigureAwait(false);
            RepairResult result = new RepairResult();
            RescueBackup safety = null;
            FileStream writeLock = null;
            Action persist = delegate { SaveBackup(safety, result.BackupPath); };
            try
            {
                token.ThrowIfCancellationRequested();
                if (!IsAllowedBackupPath(backupFile, BackupRoot)) throw new InvalidDataException(I18n.T("只允许恢复本程序备份目录内的 .nrb 文件。"));
                CheckBackupDirectory(false);
                writeLock = AcquireOperationLock();
                RescueBackup original = LoadBackup(backupFile);
                if (original.ProxyStarted) result.Items.Add(NewRepairItem("RestoreProxy", null));
                foreach (AdapterBackup adapter in original.Adapters.Where(delegate(AdapterBackup a) { return a.DnsStarted; })) result.Items.Add(NewRepairItem("RestoreDns", adapter.Id));
                if (original.FlushStarted) result.Items.Add(new RepairItemResult { ActionId = "FlushDns", State = RepairItemState.NotRestorable, Code = "NotRestorable", Message = I18n.T("已清除 DNS 缓存；缓存本身无法回滚。") });
                foreach (AdapterBackup adapter in original.Adapters.Where(delegate(AdapterBackup a) { return a.RenewStarted; })) result.Items.Add(new RepairItemResult { ActionId = "RenewDhcp", AdapterId = adapter.Id, State = RepairItemState.NotRestorable, Code = "NotRestorable", Message = I18n.T("原操作包含清除 DNS 缓存或 DHCP 续租；这两种临时状态无法精确恢复。备份中的 IP/网关仅供核对，程序不会写回旧租约或静态 IP。") });
                Notify(progress, I18n.T("正在验证备份与当前配置，恢复前也会保存当前状态……"));
                Snapshot now = await Task.Run(delegate { return new Snapshot { Adapters = ReadAdapters(), Proxy = ReadProxy() }; }).ConfigureAwait(false);
                safety = CreateBackup(now);
                AttachOperation(result, safety, "Restore");
                // Restore reads configuration only; no fresh HTTP sample exists for the optional before-state fields.
                result.BackupPath = SaveBackup(safety, null);
                result.JournalSaved = true;
                foreach (RepairItemResult currentItem in result.Items.Where(delegate(RepairItemResult i) { return i.State == RepairItemState.Prepared; }).ToList())
                {
                    RepairItemResult item = currentItem;
                    bool keepGoing = await Task.Run(delegate { return RunTrackedItem(result, item, delegate
                    {
                        if (item.ActionId == "RestoreProxy")
                        {
                            ProxySnapshot current = ReadProxy();
                            if (!current.Readable || current.Server != original.ProxyServer || current.Pac != original.ProxyPac || current.Override != original.ProxyOverride) throw new RepairRefusedException("PreconditionChanged", I18n.T("代理地址/PAC 在备份后变化或无法读取，跳过恢复启用状态，避免启用不同的代理。"));
                            safety.ProxyEnableExists = current.EnableExists;
                            safety.ProxyEnableValue = current.EnableValue;
                            safety.ProxyServer = current.Server;
                            safety.ProxyPac = current.Pac;
                            safety.ProxyOverride = current.Override;
                            safety.ProxyStarted = true;
                            BeginItemWrite(item, persist, delegate { safety.ProxyStarted = false; });
                            SetProxyEnable(!original.ProxyEnableExists, original.ProxyEnableValue);
                            result.Details.Add(I18n.T("已恢复当前用户手动代理的启用状态。"));
                        }
                        else
                        {
                            AdapterBackup a = original.Adapters.First(delegate(AdapterBackup adapter) { return String.Equals(adapter.Id, item.AdapterId, StringComparison.OrdinalIgnoreCase); });
                            AdapterSnapshot current = ReadAdapters().FirstOrDefault(delegate(AdapterSnapshot adapter) { return adapter.Id == a.Id; });
                            if (current == null || !current.InformationReadable || !current.Dhcp) throw new RepairRefusedException("PreconditionChanged", I18n.T("原网卡无法完整读取、不存在或已改为静态 IP，跳过 DNS 恢复。"));
                            if (!current.DnsConfigurationReadable || (!current.DnsAutomatic && current.ConfiguredDns.Count == 0)) throw new RepairRefusedException("PreconditionChanged", I18n.T("当前原始 DNS 无法完整备份，跳过恢复。"));
                            AdapterBackup currentBackup = safety.Adapters.First(delegate(AdapterBackup adapter) { return adapter.Id == a.Id; });
                            currentBackup.Dhcp = current.Dhcp;
                            currentBackup.DnsAutomatic = current.DnsAutomatic;
                            currentBackup.Dns = (current.DnsAutomatic ? current.Dns : current.ConfiguredDns).ToArray();
                            currentBackup.Addresses = current.Addresses.ToArray();
                            currentBackup.Gateways = current.Gateways.ToArray();
                            currentBackup.DnsStarted = true;
                            BeginItemWrite(item, persist, delegate { currentBackup.DnsStarted = false; });
                            InvokeAdapter(a.Id, "SetDNSServerSearchOrder", a.DnsAutomatic ? null : a.Dns);
                            result.Details.Add(I18n.T("已恢复网卡 DNS：") + a.Name);
                        }
                    }, persist, token); }).ConfigureAwait(false);
                    if (!keepGoing)
                    {
                        SetPendingItems(result, RepairItemState.Rejected, "JournalUnavailable", I18n.T("备份记录无法可靠保存，未开始其余操作。"));
                        break;
                    }
                }
                if (original.FlushStarted || original.Adapters.Any(delegate(AdapterBackup a) { return a.RenewStarted; })) result.Details.Add(I18n.T("原操作包含清除 DNS 缓存或 DHCP 续租；这两种临时状态无法精确恢复。备份中的 IP/网关仅供核对，程序不会写回旧租约或静态 IP。"));
                FinishOperation(result, safety, persist);
                if (result.Outcome == RepairOutcome.NoChange && result.JournalSaved) result.Success = true; // Preserve the legacy no-reversible-settings result.
                bool reversible = original.ProxyStarted || original.Adapters.Any(delegate(AdapterBackup a) { return a.DnsStarted; });
                result.Summary = result.Success ? (reversible ? I18n.T("已恢复备份中可恢复的设置。请重新诊断。") : I18n.T("此备份不含可自动恢复的设置；DNS 缓存与 DHCP 租约无法精确回滚。")) : result.Outcome == RepairOutcome.RestartRequired ? I18n.T("Windows 返回需重启，设置是否生效仍需复检；程序不会自动重启。") : I18n.T("部分设置未恢复，请查看明细并重新诊断。");
                return result;
            }
            catch (OperationCanceledException)
            { AbortOperation(result, safety, persist, new OperationCanceledException()); result.Summary = I18n.T("操作已取消；已开始的步骤不会自动回滚。"); return result; }
            catch (Exception ex)
            { AbortOperation(result, safety, persist, ex); result.Summary = I18n.T("恢复未完成：") + SafeError(ex); return result; }
            finally { try { if (writeLock != null) writeLock.Dispose(); } finally { operation.Release(); } }
        }

        private static string SafeError(Exception error)
        {
            if (error is InvalidOperationException || error is InvalidDataException) return error.Message;
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return I18n.T("权限不足。此操作可能需要管理员运行。");
            if (error is CryptographicException) return I18n.T("备份无法解密；仅可由创建备份的 Windows 用户在原环境解密。");
            if (error is ManagementException) return I18n.T("Windows WMI 操作失败；请检查权限、WMI/DHCP 服务并复检当前配置。");
            return error.GetType().Name + I18n.T("；未确认成功，请复检当前状态。");
        }
    }

    internal class RepairRefusedException : InvalidOperationException
    {
        public string Code;
        public RepairRefusedException(string code, string message) : base(message) { Code = code; }
    }

    internal class RepairExecutionException : InvalidOperationException
    {
        public string Code;
        public bool MayHaveChanged;
        public long? NativeCode;
        public RepairExecutionException(string code, bool mayHaveChanged, string message, long? nativeCode = null) : base(message) { Code = code; MayHaveChanged = mayHaveChanged; NativeCode = nativeCode; }
    }

    internal class BackupCheckpointException : Exception
    {
        public BackupCheckpointException(Exception inner) : base("Backup checkpoint failed", inner) { }
    }

    internal class RestartNeededException : RepairExecutionException
    {
        public RestartNeededException() : base("RestartRequired", true, I18n.T("Windows 返回需重启（代码 1）；设置可能已生效，请复检。程序不会自动重启。"), 1) { }
    }
}
