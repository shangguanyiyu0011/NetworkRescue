# 断网急救箱 · Network Rescue

Windows 本地网络诊断与针对性修复工具。先扫描、查看证据，再选择有明确前提的修复。

**开发与维护：上官一羽** · **版本：1.3.0** · **GPL-3.0-only**

[English](README.en.md) · [下载 1.3.0](https://github.com/shangguanyiyu0011/NetworkRescue/releases/tag/v1.3.0) · [使用说明](docs/USAGE.zh-CN.txt) · [参与开发](CONTRIBUTING.md) · [验证记录](docs/RELEASE-READINESS.v1.3.md)

## 下载与运行

| 系统 | 便携包 |
| --- | --- |
| Windows 10/11 x64 | [NetworkRescue-v1.3.0-win-x64.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.3.0/NetworkRescue-v1.3.0-win-x64.zip) |
| Windows 10 x86 | [NetworkRescue-v1.3.0-win-x86.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.3.0/NetworkRescue-v1.3.0-win-x86.zip) |

支持基线为 Windows 10 22H2 / Windows 11、系统 .NET Framework 4.8 或更高版本。Windows 11 没有 x86 版；纯 32 位 Windows 尚未实测。

完整解压 ZIP 到可写本地目录，双击 `NetworkRescue.exe`，保留语言、图标和文档文件。无需另装 WebView2、.NET SDK、Node.js 或 Python。程序使用系统 WPF 原生界面，不嵌入浏览器、不安装后台服务；正式包未作应用数字签名。

## 主要功能

- **两种模式、八项检查**：新手模式先诊断并显示建议；高级模式提供分组证据、工具和手动选择。检查网络硬件、连接配置、DHCP 服务、DNS、HOSTS、系统代理/PAC、LSP/Winsock 和 HTTP 联网。
- **网络工具**：Ping、DNS 查询与多服务器对比、路由追踪、TCP 端口、网卡卡片及活动 TCP 快照。连接表关联 PID/可读取的程序名，可搜索地址、端口、PID、程序名并按状态筛选。
- **VPN/Wi-Fi 排查**：只读观察代理/PAC/WPAD、WinHTTP、DNS、路由、虚拟网卡和 Wi-Fi；结论附置信度、依据与下一步，不因安装 VPN 就判故障。
- **持续监测与回放**：并发监测 1–4 个域名/IP，分别选择 HTTPS/ICMP 及 HTTPS 代理路径。手动开始后自动本地加密保存，每 15 秒及监测结束/应用退出时保存；最多 20 次、总计 64 MiB，每目标最多 3600 个真实样本，图表显示最近 300 点。时间线最多 500 事件并显示完整总数及截断；跨启动载入仅回放，不发起网络检测。
- **IPv4/IPv6 网页验证**：网站专项分别强制使用 IPv4、IPv6 已解析地址检查 HTTP(S)，保留原域名和默认 TLS 验证；每族最多尝试 2 个地址、总计 3 秒。没有 AAAA 时 IPv6 不适用；局部未确认不会自动禁用协议。
- **疑似网络门户**：有相关证据时，用户确认后在系统浏览器手动打开 Windows 认证触发页；它不是已确认的实际门户，不自动登录。完成认证后可重新诊断。
- **健康快照与常用设置**：保存最多 20 个健康快照和 20 个完整监测方案。正常诊断卡可直接自动命名保存；设置内可手动命名。选择常用设置只填入参数，不自动监测。
- **报告与备份**：导出先预览脱敏结果，再选择本地保存，不自动上传；修复前备份，支持受校验的本地还原及前后对比。
- **宽带测速**：Cloudflare 节点单连接下载/上传、HTTP 往返与真实采样曲线；结果描述到该节点的路径。
- **负载期间延迟**：测速分别记录空闲、下载和上传三个阶段的真实延迟样本，显示中位数、p95 与相对空闲阶段的增量。样本不足明确标注，不补成 0；仅反映到测试节点的路径，不能据此确认路由器导致延迟。
- **Wi-Fi 稳定性时间线**：仅在手动监测期间，约每 5 秒只读采集 WLAN 当前连接的信号、链路速率及 UTC 时间，最多保留 720 个采集快照、每次最多 8 个接口，可与无应答事件联查。不存 SSID/BSSID/网卡名称，不更改 WLAN 权限、服务或信道。
- **DNS 稳定性**：显式指定 1 个域名与 1–4 个 DNS 服务器，运行 3–10 轮（默认 5 轮），总时限 60 秒。分别显示响应率、解析结果及超时率；每轮延迟为 A+AAAA 查询的总耗时，不是单次查询 RTT。结果受缓存影响且只针对该域名，不自动改 DNS，也不凭单次最快结果推荐服务器。
- **手动托盘监测**：默认关闭，手动启用并开始监测后可关闭窗口或隐藏到托盘继续当前会话；彻底退出会停止监测并保存。不会开机自启、安装服务或自行开始监测，现有采样次数、历史容量和保存周期不变。持续无响应与恢复通知采用去抖和冷却，可静音；通知只描述目标观测，不证明整机断网。
- **观测无响应时段摘要**：按目标列出次数、起止观测、最后失败、恢复、未闭合区间与采样缺口。无响应时长仅覆盖真实失败观测，不把 HTTP 403 当断网、不把缺口当故障；附近配置和 Wi-Fi 只作同时段证据，不推断因果。界面最多预览 200 个时段和 200 个缺口，显示总数与截断。
- **单文件离线 HTML 监测报告**：先将 typed 数据转换为匿名别名，再生成摘要、真实曲线与时间线；HTML 先在内存生成；预览仅为同一匿名数据的可读摘要，确认后选择保存位置，不是渲染整个页面。报告不含 JavaScript 或外部资源，不自动打开或上传；完整绘制已保存样本、不降采样，单文件上限 8 MiB，超限提示而不静默删点；分享前仍须检查。
- **更新检查**：默认使用官方仓库 `shangguanyiyu0011/NetworkRescue`；旧空配置自动补全，非空自定义仓库保留。仍须点击检查并手动下载，不自动联网检查或安装。
- **十语与两套外观**：简中、繁中、英语、日语、韩语、德语、法语、西语、葡语、俄语；暖昼/静夜即时切换。使用帮助与离线自救说明可在断网时阅读。

## 修复范围

每项修复都要求满足当次证据，用户确认后复查配置、保存备份并执行，完成后重新诊断。

| 操作 | 范围与恢复边界 |
| --- | --- |
| 清除 DNS 缓存 `FlushDns` | 不更改 DNS 服务器；旧缓存不能恢复 |
| 重新获取 DHCP 地址 `RenewDhcp` | 仅符合条件的 DHCP 网卡；租约不能精确回滚 |
| DNS 恢复自动 `AutoDns` | 替换指定网卡的自定义 IPv4 DNS，默认不选；配置可从备份恢复 |
| 关闭手动系统代理 `DisableProxy` | 仅当前用户，保留代理地址，不改 PAC；配置可从备份恢复 |

程序不自动清空 HOSTS、重置 Winsock/WinHTTP、删除路由、卸载网卡或重启 Windows。只有失效回环代理、无 PAC/WPAD、代理路径失败而直连实际成功等条件同时成立，才建议关闭残留手动代理。正常运行的本地代理、公司代理和自定义 DNS 需要保留其用途判断。

诊断和测速使用普通权限；除关闭当前用户手动代理外，修复按需请求同账户 UAC，并重新诊断、重新选择。使用另一个管理员账户的 SID 会被拒绝。操作完成、配置发生变化和网络恢复是不同证据。

## 监测与数据边界

ICMP 不跟随 HTTP 代理，网关不回 Ping 不能证明断网；忽略系统 HTTP 代理也不等于绕过 VPN 隧道。HTTPS 只读取响应头，保留 TLS 验证，3 秒取消时限、32 KiB 响应头上限，不跟随跳转、不发送默认或代理凭据。任何 HTTP 状态都表示收到应答，不等于网页可用；407 单独提示代理认证。

健康快照要求最近 5 分钟内成功联网诊断，保存前配置仍相同且读取完整。只比较去秘密后的代理 authority、DNS/IP、已配置网关与网卡状态，变化不等于故障，不自动还原。跨启动不保存或比较 PAC 私密路径/凭据。

设置、备份、历史及工作区保存在 `%LOCALAPPDATA%\NetworkRescue\`，与旧版本兼容。健康快照和方案使用当前用户 DPAPI 保护；加密不等于匿名化，分享报告仍须检查脱敏结果。监测记录使用当前用户 DPAPI 自动本地加密保存到 `Monitoring\`，不上传、不安装后台服务。每 15 秒及监测结束/应用退出时保存；意外中断的 `Interrupted` 只恢复最后检查点，最多 15 秒采样可能未保存。损坏记录不覆盖、不删除，“清空”只删除可读记录。监测历史继续兼容读取 schema 1/2，旧文件读取与回放不改写；旧记录缺少 Wi-Fi 观测不代表当时没有无线连接。加密记录可能含 IP/域名，不是匿名数据。托盘模式需手动启用，默认关闭，不会自行开始监测或开机自启；彻底退出会停止当前会话。TCP 进程关联不是原子实时快照，进程退出、权限和 PID 复用会限制结果；筛选只作用于最多 512 条已读取记录，不提权、不结束程序。

## 界面预览

以下五图为 **1.0.0 首次上传时的真实 WPF 界面与合成演示数据**，是历史截图，未重新截图，不作为 1.3.0 实窗验证或真实公网诊断、测速结论。

![新手诊断 · 暖昼](docs/screenshots/diagnosis-beginner-light.png)

<details>
<summary>高级诊断、持续监测、测速与帮助</summary>

![高级诊断 · 暖昼](docs/screenshots/diagnosis-advanced-light.png)

![持续监测 · 静夜](docs/screenshots/monitor-dark.png)

![宽带测速 · 暖昼](docs/screenshots/speed-light.png)

![使用帮助 · 暖昼](docs/screenshots/help-light.png)

</details>

## 从源码构建

使用同版本 [v1.3.0 源码](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.3.0)，在已允许本地脚本的 PowerShell 中运行：

```powershell
.\build.ps1 -Architecture x64
.\publish.ps1 -Architecture x64 -SkipBuild

.\build.ps1 -Architecture x86
.\publish.ps1 -Architecture x86 -SkipBuild
```

构建使用系统 C# 编译器、C# 5 / .NET Framework 4.8 和 WPF 引用，不恢复 NuGet、不下载运行时。默认执行 23 套 C# 检查，输出 `build-native-x64` / `build-native-x86`；打包输出到 `dist`。直接运行 `publish.ps1 -Architecture x64` 或 `x86` 也会先完整构建检查，再打包。脚本不会上传或自动发布。

1.3.0 产品版本为 1.3.0、程序集/文件版本为 1.3.0.0。新增手动托盘监测与通知、按目标观测时段摘要和单文件离线 HTML 报告；保留 1.2 全部功能与四种修复范围。本机最终双架构各 23 套检查全部通过，x64 3993 项、x86 3991 项；报告实窗、离屏 UI、实际 NotifyIcon 与隔离控制器集成的范围及限制统一见[1.3 验证记录](docs/RELEASE-READINESS.v1.3.md)。[1.2](docs/RELEASE-READINESS.v1.2.md)、[1.1](docs/RELEASE-READINESS.v1.1.md)及[1.0 历史验证](docs/RELEASE-READINESS.v1.md)保留各自产物绑定，不转作 1.3.0 通过证据。正式发行资产校验值以同一提交 CI 构建及发行资产中的 SHA256SUMS 为准。

## 许可与反馈

自有代码采用 **GPL-3.0-only**，见 [LICENSE](LICENSE)；对应源码与构建脚本位于同版本 [v1.3.0 标签](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.3.0)。第三方及系统服务说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

官方提供免费使用，不售激活码、不收使用授权费。GPL 允许在履行许可、对应源码等义务的前提下收费分发，收费本身不等于侵权或冒充官方，参见 [GNU GPL 常见问题](https://www.gnu.org/licenses/gpl-faq.en.html#DoesTheGPLAllowMoney)。如遇冒充官方售卖或收费激活，可向相关平台或 `shangguanyiyu0011@gmail.com` 反馈。软件“设置→反馈”仅复制邮箱，不发送邮件或自动附加报告。

问题反馈请使用 [Issues](https://github.com/shangguanyiyu0011/NetworkRescue/issues)，先移除私人配置、凭据、原始 IP/SSID 与未脱敏报告。
