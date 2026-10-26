using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using TimeoutException = System.TimeoutException;

namespace NetworkRescue
{
    internal class HardwareEvidence
    {
        internal string Id, Name, Type;
        internal bool Up, UsableAddress, Readable = true;
        internal bool? Dhcp, Physical, Enabled;
        internal uint? DeviceError;
    }

    internal class HardwareSnapshot
    {
        internal List<HardwareEvidence> Adapters = new List<HardwareEvidence>();
        internal string WmiError;
    }

    internal class HostsEvidence
    {
        internal int CustomLines, CustomMappings, InvalidLines;
        internal HashSet<string> Covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    internal class CatalogEntry
    {
        internal uint Id;
        internal Guid Provider;
        internal int ChainLength;
        internal uint[] Chain;
    }

    public static class DiagnosticService
    {
        private static readonly string[] TestHosts = { "www.microsoft.com", "www.cloudflare.com", "www.baidu.com", "www.msftconnecttest.com", "www.gstatic.com" };

        public static async Task<DiagnosticReport> DiagnoseAsync(RescueEngine engine, IProgress<string> progress, CancellationToken token)
        {
            if (engine == null) throw new ArgumentNullException("engine");
            token.ThrowIfCancellationRequested();
            DiagnosticReport report;
            try { report = await Within(engine.DiagnoseAsync(progress, token), 25000, token).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                report = new DiagnosticReport { Timestamp = DateTime.Now, Summary = I18n.T("基础联网诊断超时，尚未确认联网。请依据已完成项目检查。") };
            }
            if (progress != null) progress.Report(I18n.T("正在只读检查网络硬件、DHCP、HOSTS 与 LSP/Winsock 目录……"));
            Task<InsightSnapshot> insights = DiagnosticInsights.ReadAsync(token);
            Task<HardwareSnapshot> hardware = Task.Run(delegate { return ReadHardware(token); }, token);
            Task<DiagnosticItem> hosts = BoundedItem("Hosts", I18n.T("HOSTS 文件"), delegate { return ReadHosts(token); }, token);
            Task<DiagnosticItem> winsock = BoundedItem("Winsock", I18n.T("LSP / Winsock 协议"), delegate { return ReadWinsock(token); }, token);
            HardwareSnapshot snapshot = null;
            DiagnosticItem hardwareItem;
            try
            {
                snapshot = await Within(hardware, 6000, token).ConfigureAwait(false);
                hardwareItem = BuildHardware(snapshot);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { hardwareItem = Unknown("Hardware", I18n.T("网络硬件配置"), ex); }
            DiagnosticItem dhcp = await BoundedItem("DhcpService", I18n.T("DHCP 服务"), delegate { return ReadDhcp(snapshot, token); }, token).ConfigureAwait(false);
            DiagnosticItem hostsItem = await hosts.ConfigureAwait(false);
            DiagnosticItem winsockItem = await winsock.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            report = Compose(report, hardwareItem, dhcp, hostsItem, winsockItem, snapshot);
            DiagnosticInsights.Apply(report, await insights.ConfigureAwait(false), snapshot);
            return report;
        }

        private static async Task<T> Within<T>(Task<T> work, int milliseconds, CancellationToken token)
        {
            Task delay = Task.Delay(milliseconds, token);
            Task completed = await Task.WhenAny(work, delay).ConfigureAwait(false);
            if (token.IsCancellationRequested)
            {
                ObserveFailure(work);
                token.ThrowIfCancellationRequested();
            }
            if (completed != work)
            {
                // Timed-out native reads cannot be aborted safely; observe any later exception without blocking the UI.
                ObserveFailure(work);
                throw new TimeoutException();
            }
            return await work.ConfigureAwait(false);
        }

        private static void ObserveFailure<T>(Task<T> work)
        {
            work.ContinueWith(delegate(Task<T> task) { var ignored = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        }

        private static async Task<DiagnosticItem> BoundedItem(string id, string title, Func<DiagnosticItem> read, CancellationToken token)
        {
            try { return await Within(Task.Run(read, token), 6000, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Unknown(id, title, ex); }
        }

        private static DiagnosticItem Unknown(string id, string title, Exception error)
        {
            return Item(id, title, CheckState.Unknown, error is TimeoutException ? I18n.T("只读检查超时，未确认结果") : I18n.T("无法完整读取，未确认结果"), I18n.T("原因类型：") + error.GetType().Name + I18n.T("。未修改任何设置；读取失败不等于配置异常。"));
        }

        private static DiagnosticItem Item(string id, string title, CheckState state, string summary, string detail)
        {
            return new DiagnosticItem { Id = id, Title = title, State = state, Summary = summary, Detail = detail, EvidenceGroups = DiagnosticEvidence.Summary(state, summary) };
        }

        internal static DiagnosticReport Compose(DiagnosticReport report, DiagnosticItem hardware, DiagnosticItem dhcp, DiagnosticItem hosts, DiagnosticItem winsock, HardwareSnapshot snapshot)
        {
            List<DiagnosticItem> connection = report.Items.Where(delegate(DiagnosticItem item) { return item.Id == "Adapters" || item.Id == "Gateway" || item.Id == "DirectTcp"; }).ToList();
            bool usable = snapshot != null && snapshot.Adapters.Any(delegate(HardwareEvidence a) { return a.Up && a.UsableAddress; });
            bool tcp = connection.Any(delegate(DiagnosticItem item) { return item.Id == "DirectTcp" && item.State == CheckState.Passed; });
            bool bad = connection.Any(delegate(DiagnosticItem item) { return item.Id == "Adapters" && (item.State == CheckState.Failed || item.State == CheckState.Warning); });
            CheckState state = report.InternetAvailable || usable || tcp ? (bad && !usable && !report.InternetAvailable ? CheckState.Warning : CheckState.Passed) : (bad ? CheckState.Warning : CheckState.Unknown);
            StringBuilder detail = new StringBuilder();
            foreach (DiagnosticItem item in connection) detail.AppendLine(item.Title + I18n.T("：") + item.Summary + Environment.NewLine + item.Detail);
            if (usable) detail.AppendLine(I18n.T("系统另检出已连接的可用 IPv4 或非链路本地 IPv6 地址；IPv4 检查不能单独代表 IPv6 连接。"));
            DiagnosticItem merged = Item("Connection", I18n.T("网络连接配置"), state, report.InternetAvailable ? I18n.T("当前 HTTP 联网已确认，连接配置证据见详情") : usable ? I18n.T("发现已连接的 IPv4 / IPv6 地址，结合网关与直连证据判断") : I18n.T("已汇总地址、网关与直连证据，尚未确认完整联网"), detail.ToString());
            merged.EvidenceGroups.Clear();
            foreach (DiagnosticItem item in connection)
            {
                merged.Actions.AddRange(item.Actions);
                if (item.EvidenceGroups != null) merged.EvidenceGroups.AddRange(item.EvidenceGroups);
            }
            if (merged.EvidenceGroups.Count == 0) merged.EvidenceGroups = DiagnosticEvidence.Summary(CheckState.Unknown, I18n.T("基础检查未完成"));
            DiagnosticItem dns = Find(report, "Dns", I18n.T("DNS 服务"));
            DiagnosticItem proxy = Find(report, "Proxy", I18n.T("浏览器配置"));
            proxy.Detail += Environment.NewLine + I18n.T("检测范围：当前用户 Windows 系统代理、PAC 及相应联网抽样；未检查所有浏览器的扩展、独立代理、DoH、策略、缓存或每项设置。");
            DiagnosticItem http = Find(report, "Http", I18n.T("电脑能否上网"));
            report.Items = new List<DiagnosticItem> { hardware, merged, dhcp, dns, hosts, proxy, winsock, http };
            return report;
        }

        private static DiagnosticItem Find(DiagnosticReport report, string id, string title)
        {
            DiagnosticItem item = report.Items.FirstOrDefault(delegate(DiagnosticItem value) { return value.Id == id; });
            if (item == null) return Item(id, title, CheckState.Unknown, I18n.T("基础检查未完成"), I18n.T("基础诊断超时，未取得此项证据。"));
            item.Title = title;
            return item;
        }

        internal static bool IsUsableAddress(IPAddress address)
        {
            if (IPAddress.IsLoopback(address)) return false;
            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                byte[] octets = address.GetAddressBytes();
                return octets[0] != 0 && octets[0] < 224 && !(octets[0] == 169 && octets[1] == 254);
            }
            return address.AddressFamily == AddressFamily.InterNetworkV6 && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.Equals(IPAddress.IPv6Any);
        }

        private static HardwareSnapshot ReadHardware(CancellationToken token)
        {
            HardwareSnapshot snapshot = new HardwareSnapshot();
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                token.ThrowIfCancellationRequested();
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                HardwareEvidence evidence = new HardwareEvidence { Id = nic.Id.Trim('{', '}'), Name = nic.Name, Type = nic.NetworkInterfaceType.ToString(), Up = nic.OperationalStatus == OperationalStatus.Up };
                try
                {
                    IPInterfaceProperties ip = nic.GetIPProperties();
                    evidence.UsableAddress = ip.UnicastAddresses.Any(delegate(UnicastIPAddressInformation address) { return IsUsableAddress(address.Address); });
                    IPv4InterfaceProperties v4 = ip.GetIPv4Properties();
                    if (v4 != null) evidence.Dhcp = v4.IsDhcpEnabled;
                }
                catch (NetworkInformationException) { evidence.Readable = false; }
                snapshot.Adapters.Add(evidence);
            }
            try
            {
                using (ManagementObjectSearcher searcher = new ManagementObjectSearcher("SELECT GUID,Name,NetEnabled,PhysicalAdapter,ConfigManagerErrorCode FROM Win32_NetworkAdapter"))
                {
                    searcher.Options.Timeout = TimeSpan.FromSeconds(3);
                    using (ManagementObjectCollection objects = searcher.Get())
                    foreach (ManagementObject adapter in objects)
                    using (adapter)
                    {
                        token.ThrowIfCancellationRequested();
                        string id = Convert.ToString(adapter["GUID"], CultureInfo.InvariantCulture).Trim('{', '}');
                        HardwareEvidence evidence = snapshot.Adapters.FirstOrDefault(delegate(HardwareEvidence a) { return String.Equals(a.Id, id, StringComparison.OrdinalIgnoreCase); });
                        if (evidence == null && adapter["PhysicalAdapter"] is bool && (bool)adapter["PhysicalAdapter"])
                        {
                            evidence = new HardwareEvidence { Id = id, Name = Convert.ToString(adapter["Name"], CultureInfo.InvariantCulture), Type = I18n.T("WMI 物理网卡"), Readable = false };
                            snapshot.Adapters.Add(evidence);
                        }
                        if (evidence == null) continue;
                        if (adapter["NetEnabled"] is bool) evidence.Enabled = (bool)adapter["NetEnabled"];
                        if (adapter["PhysicalAdapter"] is bool) evidence.Physical = (bool)adapter["PhysicalAdapter"];
                        if (adapter["ConfigManagerErrorCode"] != null) evidence.DeviceError = Convert.ToUInt32(adapter["ConfigManagerErrorCode"], CultureInfo.InvariantCulture);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { snapshot.WmiError = ex.GetType().Name; }
            return snapshot;
        }

        internal static DiagnosticItem BuildHardware(HardwareSnapshot snapshot)
        {
            bool linked = snapshot.Adapters.Any(delegate(HardwareEvidence a) { return a.Up && (!a.DeviceError.HasValue || a.DeviceError.Value == 0); });
            bool physicalError = snapshot.Adapters.Any(delegate(HardwareEvidence a) { return a.Physical == true && a.DeviceError.HasValue && a.DeviceError.Value != 0 && a.DeviceError.Value != 22; });
            CheckState state = linked ? CheckState.Passed : physicalError ? CheckState.Failed : snapshot.Adapters.Count == 0 ? CheckState.Unknown : CheckState.Warning;
            StringBuilder details = new StringBuilder();
            foreach (HardwareEvidence a in snapshot.Adapters.Take(64))
                details.AppendLine(SafeLabel(a.Name) + " | " + a.Type + I18n.T(" | 系统链路=") + (a.Up ? I18n.T("连接") : I18n.T("未连接/未知")) + I18n.T(" | 可用 IPv4/IPv6=") + a.UsableAddress + I18n.T(" | 物理设备=") + (a.Physical.HasValue ? a.Physical.ToString() : I18n.T("未知")) + I18n.T(" | 启用=") + (a.Enabled.HasValue ? a.Enabled.ToString() : I18n.T("未知")) + I18n.T(" | 设备错误码=") + (a.DeviceError.HasValue ? a.DeviceError.Value.ToString(CultureInfo.InvariantCulture) : I18n.T("未读取")));
            if (snapshot.Adapters.Count > 64) details.AppendLine(I18n.T("仅展示前 64 个设备的详情。"));
            if (snapshot.WmiError != null) details.AppendLine(I18n.T("WMI 设备状态读取不完整：") + snapshot.WmiError + I18n.T("；系统链路证据仍可参考。"));
            details.AppendLine(I18n.T("错误码 22 表示设备禁用；备用断开、虚拟/TAP 与主动禁用设备不单独作为整体故障。检测限于 Windows 可见设备与链路，不能验证网线、电源、路由器或无线射频的物理状态。"));
            DiagnosticItem item = Item("Hardware", I18n.T("网络硬件配置"), state, linked ? I18n.T("系统识别到已连接网卡；未发现阻断当前链路的设备错误") : physicalError ? I18n.T("物理网卡存在系统报告的设备错误，请查看设备管理器") : I18n.T("未确认活动网卡链路，请结合当前使用方式检查"), details.ToString());
            item.EvidenceGroups.Clear();
            foreach (HardwareEvidence adapter in snapshot.Adapters.Take(64))
            {
                CheckState deviceState = !adapter.Readable || !adapter.Up ? CheckState.Unknown : adapter.DeviceError.HasValue && adapter.DeviceError.Value != 0 ? CheckState.Warning : CheckState.Passed;
                item.EvidenceGroups.Add(DiagnosticEvidence.Group(null, SafeLabel(adapter.Name), deviceState,
                    DiagnosticEvidence.Field("adapterType", "接口类型", adapter.Type),
                    DiagnosticEvidence.Field("statusColumn", "状态", adapter.Up ? I18n.T("连接") : I18n.T("未连接/未知")),
                    DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", adapter.Readable ? I18n.T("通过") : I18n.T("未确定")),
                    DiagnosticEvidence.Field("evidenceUsableAddress", "可用 IPv4 / IPv6", adapter.Readable ? DiagnosticEvidence.Boolean(adapter.UsableAddress) : I18n.T("未知")),
                    DiagnosticEvidence.Field("evidenceDeviceType", "物理设备", DiagnosticEvidence.Boolean(adapter.Physical)),
                    DiagnosticEvidence.Field("evidenceEnabled", "启用状态", DiagnosticEvidence.Boolean(adapter.Enabled)),
                    DiagnosticEvidence.Field("evidenceDeviceError", "设备错误码", adapter.DeviceError.HasValue ? adapter.DeviceError.Value.ToString(CultureInfo.InvariantCulture) : I18n.T("未读取"))));
            }
            if (snapshot.Adapters.Count == 0 || snapshot.WmiError != null)
                item.EvidenceGroups.Add(DiagnosticEvidence.Group("hardware", "网络硬件配置", CheckState.Unknown, DiagnosticEvidence.Field("evidenceReadStatus", "读取状态", snapshot.WmiError ?? I18n.T("未读取"))));
            return item;
        }

        private static string SafeLabel(string value)
        {
            if (String.IsNullOrEmpty(value)) return I18n.T("未命名");
            return new string(value.Take(100).Where(delegate(char c) { return !Char.IsControl(c); }).ToArray());
        }

        private static DiagnosticItem ReadDhcp(HardwareSnapshot snapshot, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (ServiceController service = new ServiceController("Dhcp"))
            {
                service.Refresh();
                return BuildDhcp(snapshot, service.Status);
            }
        }

        internal static DiagnosticItem BuildDhcp(HardwareSnapshot snapshot, ServiceControllerStatus status)
        {
            if (snapshot == null)
            {
                DiagnosticItem unknown = Item("DhcpService", I18n.T("DHCP 服务"), CheckState.Unknown, I18n.T("已读取服务，但无法确定网卡是否依赖 DHCP"), I18n.T("Windows Dhcp 服务状态：") + status + I18n.T("。网卡读取不完整；未操作服务。"));
                AddDhcpEvidence(unknown, status, null, null);
                return unknown;
            }
            int dhcp = snapshot.Adapters.Count(delegate(HardwareEvidence a) { return a.Dhcp == true; });
            List<HardwareEvidence> active = snapshot.Adapters.Where(delegate(HardwareEvidence a) { return a.Up; }).ToList();
            bool activeDhcp = active.Any(delegate(HardwareEvidence a) { return a.Dhcp == true; });
            bool staticOnly = active.Count > 0 && active.All(delegate(HardwareEvidence a) { return a.Dhcp == false; });
            CheckState state = status == ServiceControllerStatus.Running || staticOnly ? CheckState.Passed : activeDhcp ? CheckState.Warning : CheckState.Unknown;
            string summary = staticOnly ? I18n.T("活动 IPv4 网卡使用静态配置；DHCP 服务未运行也不单独视为故障") : status == ServiceControllerStatus.Running ? I18n.T("Windows DHCP Client 服务正在运行") : activeDhcp ? I18n.T("活动网卡依赖 DHCP，但 DHCP Client 服务未运行") : I18n.T("未确认活动 DHCP 依赖，服务状态见详情");
            DiagnosticItem item = Item("DhcpService", I18n.T("DHCP 服务"), state, summary, I18n.T("Windows Dhcp 服务状态：") + status + I18n.T("；配置 DHCP 的网卡数：") + dhcp.ToString(CultureInfo.InvariantCulture) + I18n.T("；活动 DHCP 网卡：") + activeDhcp + I18n.T("。") + Environment.NewLine + I18n.T("这里只读检查客户端服务与 IPv4 DHCP 配置，不验证 DHCP 服务器、租约续期或 IPv6 SLAAC/DHCPv6。若活动 DHCP 网卡需重新获取地址而服务未运行，可在服务管理器核对启动配置；不会自动启停服务。"));
            AddDhcpEvidence(item, status, dhcp, activeDhcp);
            return item;
        }

        private static void AddDhcpEvidence(DiagnosticItem item, ServiceControllerStatus status, int? adapters, bool? activeDhcp)
        {
            item.EvidenceGroups = new List<DiagnosticEvidenceGroup> { DiagnosticEvidence.Group("dhcp", "DHCP 服务", item.State,
                DiagnosticEvidence.Field("evidenceService", "服务", "DHCP Client (Dhcp)"),
                DiagnosticEvidence.Field("statusColumn", "状态", status),
                DiagnosticEvidence.Field("evidenceDhcpAdapters", "配置 DHCP 的网卡数", adapters.HasValue ? adapters.Value.ToString(CultureInfo.InvariantCulture) : I18n.T("未知")),
                DiagnosticEvidence.Field("evidenceActiveDhcp", "活动网卡依赖 DHCP", DiagnosticEvidence.Boolean(activeDhcp))) };
        }

        private static DiagnosticItem ReadHosts(CancellationToken token)
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "etc");
            using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters"))
            {
                if (key != null) directory = Environment.ExpandEnvironmentVariables(Convert.ToString(key.GetValue("DataBasePath", directory), CultureInfo.InvariantCulture));
            }
            string path = Path.Combine(directory, "hosts");
            path = LocalHostsPath(path, delegate(string root) { return new DriveInfo(root).DriveType; }, File.GetAttributes);
            if (path == null) return Item("Hosts", I18n.T("HOSTS 文件"), CheckState.Unknown, I18n.T("HOSTS 路径无法确认为本地普通文件，未读取"), I18n.T("仅允许本地绝对路径；拒绝网络盘、UNC、设备路径、备用数据流及文件或父目录上的重解析点。未访问网络共享，未修改文件，配置路径不回显。"));
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (stream.Length > 1024 * 1024) return Item("Hosts", I18n.T("HOSTS 文件"), CheckState.Unknown, I18n.T("文件超过 1 MiB，未完整分析"), I18n.T("为限制读取量未加载文件内容。大文件本身不能证明异常；未修改文件。"));
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    {
                        List<string> lines = new List<string>();
                        int total = 0;
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            token.ThrowIfCancellationRequested();
                            total += line.Length + 1;
                            if (total > 1024 * 1024) return Item("Hosts", I18n.T("HOSTS 文件"), CheckState.Unknown, I18n.T("读取期间文件增长超限，未完整分析"), I18n.T("未修改文件。"));
                            lines.Add(line);
                        }
                        return BuildHosts(ParseHosts(lines));
                    }
                }
            }
            catch (FileNotFoundException) { return Item("Hosts", I18n.T("HOSTS 文件"), CheckState.Passed, I18n.T("HOSTS 文件不存在；Windows 可直接使用 DNS"), I18n.T("缺失 HOSTS 不单独视为故障；未创建文件。")); }
            catch (DirectoryNotFoundException) { return Item("Hosts", I18n.T("HOSTS 文件"), CheckState.Unknown, I18n.T("HOSTS 所在目录不存在，无法核对配置路径"), I18n.T("未创建目录或文件。")); }
        }

        internal static string LocalHostsPath(string path, Func<string, DriveType> driveType, Func<string, FileAttributes> attributes)
        {
            try
            {
                if (String.IsNullOrWhiteSpace(path) || path.Length <= 3 || path.Length >= 260 || !Char.IsLetter(path[0]) || path[1] != ':' || (path[2] != '\\' && path[2] != '/') || path.IndexOf(':', 2) >= 0) return null;
                string full = Path.GetFullPath(path);
                string root = Path.GetPathRoot(full);
                DriveType kind = driveType(root);
                if (kind != DriveType.Fixed && kind != DriveType.Removable && kind != DriveType.Ram) return null;
                string current = root;
                if ((attributes(current) & FileAttributes.ReparsePoint) != 0) return null;
                string[] parts = full.Substring(root.Length).Split(Path.DirectorySeparatorChar);
                for (int i = 0; i < parts.Length; i++)
                {
                    if (parts[i].Length == 0 || parts[i].EndsWith(".", StringComparison.Ordinal) || parts[i].EndsWith(" ", StringComparison.Ordinal)) return null;
                    current = Path.Combine(current, parts[i]);
                    FileAttributes flags;
                    try { flags = attributes(current); }
                    catch (FileNotFoundException) { return i == parts.Length - 1 ? full : null; }
                    if ((flags & FileAttributes.ReparsePoint) != 0 || (i < parts.Length - 1 && (flags & FileAttributes.Directory) == 0)) return null;
                }
                return full;
            }
            catch (Exception ex)
            {
                if (ex is ArgumentException || ex is NotSupportedException || ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException) return null;
                throw;
            }
        }

        internal static HostsEvidence ParseHosts(IEnumerable<string> lines)
        {
            HostsEvidence result = new HostsEvidence();
            foreach (string source in lines)
            {
                string line = source.Split('#')[0].Trim();
                if (line.Length == 0) continue;
                string[] fields = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                IPAddress address;
                if (fields.Length < 2 || !IPAddress.TryParse(fields[0], out address) || fields[0].IndexOf('%') >= 0 || (address.AddressFamily == AddressFamily.InterNetwork && fields[0].Split('.').Length != 4)) { result.InvalidLines++; continue; }
                bool custom = false, invalid = false;
                foreach (string field in fields.Skip(1))
                {
                    string host = field.TrimEnd('.');
                    if (!ValidHost(host)) { invalid = true; continue; }
                    if (IPAddress.IsLoopback(address) && (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host.Equals("localhost.localdomain", StringComparison.OrdinalIgnoreCase))) continue;
                    result.CustomMappings++;
                    custom = true;
                    if (TestHosts.Contains(host, StringComparer.OrdinalIgnoreCase)) result.Covered.Add(host.ToLowerInvariant());
                }
                if (custom) result.CustomLines++;
                if (invalid) result.InvalidLines++;
            }
            return result;
        }

        private static bool ValidHost(string host)
        {
            if (host.Length == 0 || host.Length > 253 || host.IndexOfAny(new[] { '/', '\\', ':', '@', '*', '?' }) >= 0) return false;
            try
            {
                string ascii = new IdnMapping().GetAscii(host);
                return ascii.Split('.').All(delegate(string label) { return label.Length > 0 && label.Length <= 63 && label[0] != '-' && label[label.Length - 1] != '-' && label.All(delegate(char c) { return (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_'; }); });
            }
            catch (ArgumentException) { return false; }
        }

        internal static DiagnosticItem BuildHosts(HostsEvidence hosts)
        {
            bool warning = hosts.InvalidLines > 0 || hosts.Covered.Count > 0;
            DiagnosticItem item = Item("Hosts", I18n.T("HOSTS 文件"), warning ? CheckState.Warning : CheckState.Passed, hosts.CustomMappings == 0 && !warning ? I18n.T("文件可读，未发现非标准 localhost 映射") : I18n.T("文件可读：自定义映射 ") + hosts.CustomMappings + I18n.T(" 条，无效行 ") + hosts.InvalidLines + I18n.T(" 行"), I18n.T("自定义有效行：") + hosts.CustomLines + I18n.T("；自定义域名映射：") + hosts.CustomMappings + I18n.T("；无效行：") + hosts.InvalidLines + I18n.T("；覆盖测试域名：") + (hosts.Covered.Count == 0 ? I18n.T("无") : String.Join(I18n.T("、"), hosts.Covered.OrderBy(delegate(string value) { return value; }))) + I18n.T("。") + Environment.NewLine + I18n.T("标准回环 localhost 项已忽略。自定义映射可能用于开发、内网或过滤，不能据此判定恶意；测试域名覆盖可能影响本工具结果。只检查文本格式与上述测试域名，不评判全部映射用途，未清空或修改文件，报告不展示其他域名及注释。"));
            item.EvidenceGroups = new List<DiagnosticEvidenceGroup> { DiagnosticEvidence.Group("hosts", "HOSTS 文件", item.State,
                DiagnosticEvidence.Field("evidenceCustomLines", "自定义有效行", hosts.CustomLines),
                DiagnosticEvidence.Field("evidenceCustomMappings", "自定义域名映射", hosts.CustomMappings),
                DiagnosticEvidence.Field("evidenceInvalidLines", "无效行", hosts.InvalidLines),
                DiagnosticEvidence.Field("evidenceCoveredDomains", "覆盖测试域名", hosts.Covered.Count == 0 ? I18n.T("无") : String.Join(", ", hosts.Covered.OrderBy(delegate(string value) { return value; })))) };
            return item;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct ProtocolInfo
        {
            internal uint ServiceFlags1, ServiceFlags2, ServiceFlags3, ServiceFlags4, ProviderFlags;
            internal Guid Provider;
            internal uint CatalogId;
            internal int ChainLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 7)] internal uint[] Chain;
            internal int Version, AddressFamily, MaxAddress, MinAddress, SocketType, Protocol, ProtocolMaxOffset, NetworkByteOrder, SecurityScheme;
            internal uint MessageSize, ProviderReserved;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        }

        [DllImport("ws2_32.dll", EntryPoint = "WSCEnumProtocols", ExactSpelling = true)]
        private static extern int EnumProtocols(IntPtr protocols, IntPtr buffer, ref uint length, out int error);
        [DllImport("ws2_32.dll", EntryPoint = "WSCEnumProtocols32", ExactSpelling = true)]
        private static extern int EnumProtocols32(IntPtr protocols, IntPtr buffer, ref uint length, out int error);
        [DllImport("ws2_32.dll", EntryPoint = "WSCGetProviderPath", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ProviderPath(ref Guid provider, StringBuilder path, ref int length, out int error);
        [DllImport("ws2_32.dll", EntryPoint = "WSCGetProviderPath32", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int ProviderPath32(ref Guid provider, StringBuilder path, ref int length, out int error);
        [DllImport("ws2_32.dll", ExactSpelling = true)]
        private static extern int WSAStartup(ushort version, IntPtr data);
        [DllImport("ws2_32.dll", ExactSpelling = true)]
        private static extern int WSACleanup();

        private static DiagnosticItem ReadWinsock(CancellationToken token)
        {
            IntPtr data = Marshal.AllocHGlobal(512);
            bool started = false;
            try
            {
                int code = WSAStartup(0x0202, data);
                if (code != 0) return Item("Winsock", I18n.T("LSP / Winsock 协议"), CheckState.Unknown, I18n.T("Winsock 初始化失败，未确认目录"), I18n.T("Windows API 错误码：") + code + I18n.T("；未修改配置。"));
                started = true;
                return ReadWinsockCatalogs(token);
            }
            finally
            {
                if (started) WSACleanup();
                Marshal.FreeHGlobal(data);
            }
        }

        private static DiagnosticItem ReadWinsockCatalogs(CancellationToken token)
        {
            List<DiagnosticItem> catalogs = new List<DiagnosticItem>();
            catalogs.Add(ReadCatalog(false, token));
            if (Environment.Is64BitProcess) catalogs.Add(ReadCatalog(true, token));
            else if (Environment.Is64BitOperatingSystem) catalogs.Add(Item("Catalog", I18n.T("64 位目录"), CheckState.Unknown, I18n.T("当前为 32 位进程，未覆盖 64 位目录"), I18n.T("请使用 64 位构建完成双目录检查。")));
            return BuildWinsock(catalogs);
        }

        internal static DiagnosticItem BuildWinsock(List<DiagnosticItem> catalogs)
        {
            CheckState state = catalogs.Any(delegate(DiagnosticItem item) { return item.State == CheckState.Warning; }) ? CheckState.Warning : catalogs.Any(delegate(DiagnosticItem item) { return item.State == CheckState.Unknown; }) ? CheckState.Unknown : CheckState.Passed;
            DiagnosticItem result = Item("Winsock", I18n.T("LSP / Winsock 协议"), state, state == CheckState.Passed ? I18n.T("已检查目录链引用与 Provider DLL 文件存在性") : I18n.T("部分目录检查存在问题或未确定，见详情"), String.Join(Environment.NewLine, catalogs.Select(delegate(DiagnosticItem item) { return item.Title + I18n.T("：") + item.Summary + Environment.NewLine + item.Detail; })) + Environment.NewLine + I18n.T("仅验证 Winsock 传输协议目录结构、链引用和 DLL 文件存在性；未加载 DLL、验证签名/内容或检查 WFP、名称空间 Provider。第三方 Provider 不等于劫持。不会重置 Winsock。"));
            result.EvidenceGroups = catalogs.SelectMany(delegate(DiagnosticItem item) { return item.EvidenceGroups; }).ToList();
            return result;
        }

        private static DiagnosticItem ReadCatalog(bool catalog32, CancellationToken token)
        {
            string title = catalog32 || !Environment.Is64BitProcess ? I18n.T("32 位 Winsock 目录") : I18n.T("64 位 Winsock 目录");
            try
            {
                token.ThrowIfCancellationRequested();
                uint length = 0;
                int error;
                int count = catalog32 ? EnumProtocols32(IntPtr.Zero, IntPtr.Zero, ref length, out error) : EnumProtocols(IntPtr.Zero, IntPtr.Zero, ref length, out error);
                if (count == 0 && length == 0) return Item("Catalog", title, CheckState.Warning, I18n.T("目录为空"), I18n.T("Windows API 未返回协议条目。"));
                if (count != -1 || error != 10055 || length == 0 || length > 4 * 1024 * 1024) return Item("Catalog", title, CheckState.Unknown, I18n.T("无法取得目录缓冲区"), I18n.T("Windows API 错误码：") + error + I18n.T("；未确认目录健康。"));
                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                List<CatalogEntry> entries = new List<CatalogEntry>();
                try
                {
                    uint capacity = length;
                    count = catalog32 ? EnumProtocols32(IntPtr.Zero, buffer, ref length, out error) : EnumProtocols(IntPtr.Zero, buffer, ref length, out error);
                    int size = Marshal.SizeOf(typeof(ProtocolInfo));
                    if (count < 0 || size != 628 || count > capacity / size) return Item("Catalog", title, CheckState.Unknown, I18n.T("目录读取不完整"), I18n.T("Windows API 错误码：") + error + I18n.T("；目录可能在读取期间变化。"));
                    for (int i = 0; i < count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        ProtocolInfo info = (ProtocolInfo)Marshal.PtrToStructure(IntPtr.Add(buffer, i * size), typeof(ProtocolInfo));
                        entries.Add(new CatalogEntry { Id = info.CatalogId, Provider = info.Provider, ChainLength = info.ChainLength, Chain = info.Chain });
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
                List<string> problems = ValidateChains(entries);
                int missing = 0, unreadable = 0, checkedPaths = 0;
                foreach (Guid id in entries.Select(delegate(CatalogEntry entry) { return entry.Provider; }).Distinct())
                {
                    token.ThrowIfCancellationRequested();
                    Guid provider = id;
                    int capacity = 32768;
                    StringBuilder path = new StringBuilder(capacity);
                    int code = catalog32 ? ProviderPath32(ref provider, path, ref capacity, out error) : ProviderPath(ref provider, path, ref capacity, out error);
                    if (code != 0) { unreadable++; continue; }
                    string expanded = ResolveProviderPath(path.ToString(), catalog32);
                    if (expanded == null) { unreadable++; continue; }
                    try
                    {
                        if ((File.GetAttributes(expanded) & FileAttributes.Directory) != 0) missing++;
                        else checkedPaths++;
                    }
                    catch (FileNotFoundException) { missing++; }
                    catch (DirectoryNotFoundException) { missing++; }
                    catch (UnauthorizedAccessException) { unreadable++; }
                    catch (System.Security.SecurityException) { unreadable++; }
                    catch (IOException) { unreadable++; }
                }
                return BuildCatalogItem(title, catalog32, entries.Count, problems, checkedPaths, missing, unreadable);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return Unknown("Catalog", title, ex); }
        }

        internal static DiagnosticItem BuildCatalogItem(string title, bool catalog32, int entries, List<string> problems, int checkedPaths, int missing, int unreadable)
        {
            CheckState state = problems.Count > 0 || missing > 0 || entries == 0 ? CheckState.Warning : unreadable > 0 ? CheckState.Unknown : CheckState.Passed;
            DiagnosticItem item = Item("Catalog", title, state, I18n.T("协议条目 ") + entries + I18n.T("；链问题 ") + problems.Count + I18n.T("；DLL 已确认存在 ") + checkedPaths + I18n.T("，缺失 ") + missing + I18n.T("，未确定 ") + unreadable, String.Join(Environment.NewLine, problems.Take(12)) + (problems.Count > 12 ? Environment.NewLine + I18n.T("其余链问题已省略。") : "") + Environment.NewLine + I18n.T("使用 WSCEnumProtocols") + (catalog32 ? "32" : "") + I18n.T(" 与 WSCGetProviderPath") + (catalog32 ? "32" : "") + I18n.T("；不将 Provider 名称、第三方来源或 LSP 数量作为异常依据。"));
            item.EvidenceGroups = new List<DiagnosticEvidenceGroup> { DiagnosticEvidence.Group(null, title, state,
                DiagnosticEvidence.Field("evidenceProtocolEntries", "协议条目", entries),
                DiagnosticEvidence.Field("evidenceChainProblems", "链问题", problems.Count),
                DiagnosticEvidence.Field("evidenceDllPresent", "DLL 已确认存在", checkedPaths),
                DiagnosticEvidence.Field("evidenceDllMissing", "DLL 缺失", missing),
                DiagnosticEvidence.Field("evidenceDllUnknown", "DLL 未确定", unreadable)) };
            return item;
        }

        internal static string ResolveProviderPath(string value, bool catalog32)
        {
            string path = Environment.ExpandEnvironmentVariables(value);
            if (String.IsNullOrWhiteSpace(path) || path.IndexOf('%') >= 0 || !Path.IsPathRooted(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            string system32 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32") + Path.DirectorySeparatorChar;
            if (catalog32 && Environment.Is64BitProcess && path.StartsWith(system32, StringComparison.OrdinalIgnoreCase))
                path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SysWOW64", path.Substring(system32.Length));
            return path;
        }

        internal static List<string> ValidateChains(IList<CatalogEntry> entries)
        {
            List<string> problems = new List<string>();
            Dictionary<uint, CatalogEntry> ids = new Dictionary<uint, CatalogEntry>();
            foreach (CatalogEntry entry in entries)
            {
                if (entry.Id == 0 || ids.ContainsKey(entry.Id)) problems.Add(I18n.T("目录含零值或重复 CatalogEntryId：") + entry.Id);
                else ids.Add(entry.Id, entry);
            }
            foreach (CatalogEntry entry in entries)
            {
                if (entry.ChainLength < 0 || entry.ChainLength > 7 || (entry.ChainLength > 1 && (entry.Chain == null || entry.Chain.Length < entry.ChainLength))) { problems.Add(I18n.T("目录 ") + entry.Id + I18n.T(" 的链长度无效。")); continue; }
                if (entry.ChainLength <= 1) continue;
                HashSet<uint> seen = new HashSet<uint>();
                for (int i = 0; i < entry.ChainLength; i++)
                {
                    CatalogEntry referenced;
                    uint reference = entry.Chain[i];
                    if (!seen.Add(reference)) problems.Add(I18n.T("目录 ") + entry.Id + I18n.T(" 重复引用层 ") + reference + I18n.T("。"));
                    if (!ids.TryGetValue(reference, out referenced)) problems.Add(I18n.T("目录 ") + entry.Id + I18n.T(" 引用不存在的条目 ") + reference + I18n.T("。"));
                    else if (referenced.ChainLength != (i == entry.ChainLength - 1 ? 1 : 0)) problems.Add(I18n.T("目录 ") + entry.Id + I18n.T(" 的分层/基础协议顺序不符。"));
                }
            }
            return problems;
        }
    }
}
