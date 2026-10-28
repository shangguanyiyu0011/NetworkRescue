# 参与开发

断网急救箱 / Network Rescue 由上官一羽开发与维护，自有代码采用 **GPL-3.0-only**，见 [LICENSE](LICENSE)。仓库为 [shangguanyiyu0011/NetworkRescue](https://github.com/shangguanyiyu0011/NetworkRescue)；定位问题前请阅读 [README](README.md) 和 [1.3 验证记录](docs/RELEASE-READINESS.v1.3.md)。

## 构建与打包

支持基线：Windows 10 22H2 x86/x64、Windows 11 x64、系统 .NET Framework 4.8 或更高。使用已经允许本地脚本的 PowerShell，无需改变系统执行策略：

```powershell
.\build.ps1 -Architecture x64
.\publish.ps1 -Architecture x64 -SkipBuild

.\build.ps1 -Architecture x86
.\publish.ps1 -Architecture x86 -SkipBuild
```

`build.ps1` 默认 x64，使用系统 `csc.exe`、C# 5、明确的 `/platform` 和系统 WPF 引用；不恢复 NuGet、不下载运行时、不复制系统 DLL。输出 `build-native-x64` / `build-native-x86`，默认运行 23 套 C# 检查。`publish.ps1` 不加 `-SkipBuild` 时会先构建/检查，运行包和 ZIP 位于 `dist`。`-SkipTests` 只跳过检查，不构成验收证据；`-SkipBuild` 只应在对应构建已完成后使用。脚本不上传或发布 GitHub Release。

20 个核心套件为 Engine、SystemChecks、SpeedTest、AdvancedTools、Localization、DiagnosticInsights、NetworkMonitor、NetworkChangeTimeline、NetworkWorkspace、ConnectionProcesses、RepairHistory、SiteDiagnostics、UpdateService、MonitoringHistory、PortalLogin、WifiStability、DnsQuality、MonitoringSummary、TrayMonitor、MonitoringHtmlReport；另有 NativeController、NativeWindow、SoftDialog，共 23 套。1.3.0 本机最终 x64/x86 全部通过，分别为 3993/3991 项；逐套结果与 EXE 绑定见本版验证记录。1.1 的 18 套及更早计数保留历史绑定。Timer 采样断言数量可能随调度变化，不硬套历史次数。x86 进程在 x64 Windows 通过不等于纯 32 位 OS 已实测。

仓库仅提供 [CI 示例](docs/ci-build-example.yml)，当前未启用自动工作流。需要时由维护者手动复制到 `.github/workflows/build.yml` 启用 Windows x64/x86 build + publish 矩阵；示例仅请求内容读取权限，不部署或发布，文件本身不是远端通过证据。

## 代码与安全边界

生产入口为 `Program` → `NativeWindow` / `NativeController` / `NativeUiText` / `SoftDialog`，系统 WPF；无浏览器运行时、远程 UI 或本地 HTTP UI 服务。保持项目原结构、C# 5 与零新增运行依赖。

- 修复只接受当次报告中的白名单动作与有效网卡 GUID。保持确认、重新检测、DPAPI 当前用户备份、逐项结果、跨进程互锁及同 SID UAC；不把未知状态变成可重置故障，不增加任意命令或绕过前提。
- 只读诊断、监测和测速可取消，迟到结果不能覆盖新会话；开始写入后的异常必须保留备份/步骤。中断恢复由用户选择，不自动回滚或重启。
- 全部 1–4 监测目标先验证再发包；保留单目标入口。ICMP 不跟随代理，网关不回不证明断网。HTTP 保留 TLS、3 秒取消、32 KiB 头、不跳转、不发凭据；应答不等于网页可用，407 保留。
- 更新仓库默认官方 `shangguanyiyu0011/NetworkRescue`，旧空值迁移补全、非空自定义保留；配置读取/迁移不触发联网，检查/下载继续由用户操作。相应默认值、迁移与自定义保留需回归验证。
- 入口一次设置进程内 TLS 1.2 与错误正文缓冲上限 0；不要在并发请求中临时切换全局网络策略。Engine/Site/SpeedTest/UpdateService 错误处理不依赖错误正文。
- 工作区两文件各 DPAPI CurrentUser/schema 1/1 MiB/20 项，原子写、损坏不覆盖。健康保存前提含 5 分钟内成功联网、完整读取与私密内存 Fingerprint 相同；持久化只保留脱敏 authority。比较不自动恢复配置。
- 监测手动开始后自动 DPAPI CurrentUser 本地加密保存，每 15 秒及监测结束/应用退出时保存；最多 20 条、总计 64 MiB，每目标最多 3600 个真实样本、图表最近 300 点、事件最多 500 条并显示截断。跨启动载入只回放，不发起网络检测。Interrupted 仅恢复意外中断的最后检查点，最多 15 秒样本可能未保存。损坏不覆盖不删除，清空只删可读记录，保留既有设置/历史兼容。
- 网站专项分别强制 IPv4/IPv6 HTTP(S)，保留原域名与默认 TLS 验证；每族最多 2 地址且合计 3 秒，不切换全局协议设置。无 AAAA 为不适用；局部未确认不自动禁用协议。疑似门户确认后由系统浏览器手动打开 Windows 认证触发页，明确非已确认门户、不自动登录，并提供诊断复测。
- 回放使用已保留的真实样本，缺失明确展示，不补造观测。连接进程数据非原子，只对最多 512 已读取行搜索/筛选；不提权/结束程序，保留 PID 复用等限制。
- 测速负载延迟按 idle/download/upload 保留真实样本、median/p95/相对 idle 增量；样本不足保留空值，不能写成 0。仅测试节点路径，不作路由器因果结论。
- WifiStability 仅手动监测期间约 5 秒只读 current WLAN 连接，最多 720 快照、每次 8 接口；保留 UTC、信号及链路速率，不存 SSID/BSSID/adapterName，不改 WLAN 权限、服务、信道。监测历史 schema 2 兼容读取 1.1 schema 1，旧记录缺失不伪造。
- DnsQuality 只接受显式 1 域名及 1–4 服务器，3–10 轮默认 5，60 秒上限；响应率、解析结果、超时率分开，每轮时延为 A+AAAA 总耗时而非单查询 RTT。说明缓存和域名特定性，不自动改 DNS，不凭单次最快推荐。托盘需用户启用，默认关闭，不自启、不安装服务，仅继续手动开始的当前会话。
- 托盘关闭/退出路径必须分明：关闭窗口在显式启用后仅隐藏当前会话，彻底退出停止并保存。去抖通知与静音、冷却不能启动新监测或泄露目标，现有采样/历史上限不变。
- 时段摘要只依据真实 typed 样本：HTTP 403 视作应答，缺失不是故障，未闭合独立表示；首末失败观测与恢复时间分开。UI 各预览 200 时段/缺口并保留完整总数；附近配置/Wi-Fi 不作因果。
- HTML 先转换 typed 匿名视图再生成；不靠生成后正则脱敏。单文件无 JS/外部资源，先在内存构造完整 HTML，再显示同一匿名数据的摘要预览（不渲染整页）；确认后文件选择器本地保存，不自动打开或上传。完整绘制已存样本、不降采样，单文件上限 8 MiB；超限提示而不静默删点，仍提示分享前检查；schema 1/2 旧历史只读兼容，不因回放改写。
- 保持 typed EvidenceGroups 和真实样本，不解析翻译日志来判断故障。双主题、十语、窄窗换行、键盘操作、复制/tooltip 与状态文字需要一起验证，不能只靠颜色。
- 报告先脱敏预览再本地保存，不上传。修复测试优先隔离数据/替身；联网使用回环和小流量。真实修改网络配置或大流量测速须有授权和恢复方案。

## 语言、提交与问题报告

界面资源为 `ui/locales/*.json`，十语；后台资源为 `i18n/messages.*.json`，九语。修改文案同步键集、占位符及 UTF-8，运行本地化/控制器及相关 WPF 检查。系统网卡名、错误码可保留原文，判断不能依赖翻译结果。

一次改动解决一个明确问题，说明触发条件、最终行为、验证与缺口。涉及修复、存储、取消、权限或请求边界的变更提供针对性检查；截图使用合成演示数据并标注，不能当作真实公网结论。

提交前排除 `build*`、`dist`、本机设置、日志、`.nrb`/`.nrh`/`.nrw`、私人报告、健康快照与监测方案。使用 [故障模板](.github/ISSUE_TEMPLATE/bug_report.yml)，只提供版本、架构、复现和已检查的最小脱敏证据；不提交凭据、订阅、原始 IP/SSID 或完整敏感 URL。

1.3.0 产品/程序集版本为 1.3.0/1.3.0.0；正式发布需让二进制和对应源码指向同一版本 tag，并随分发保留 GPL 许可及必要声明。官方免费与 GPL 允许合规收费分发并不冲突；不要把收费本身写成侵权，不接受冒充官方或收费激活的表述。应用签名、真实 VM 修复和远端 CI 状态都按实际证据记录。正式发行资产校验以同一提交 CI 构建及发行资产 SHA256SUMS 为准，本机 EXE 哈希不替代发行包清单。
