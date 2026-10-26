# 断网急救箱 · Network Rescue

Windows 本地网络诊断与针对性修复工具。先扫描、查看证据，再选择有明确前提的修复。

**开发与维护：上官一羽** · **版本：1.0.0** · **GPL-3.0-only**

[English](README.en.md) · [下载 1.0.0](https://github.com/shangguanyiyu0011/NetworkRescue/releases/tag/v1.0.0) · [使用说明](docs/USAGE.zh-CN.txt) · [参与开发](CONTRIBUTING.md) · [验证记录](docs/RELEASE-READINESS.v1.md)

## 下载与运行

| 系统 | 便携包 |
| --- | --- |
| Windows 10/11 x64 | [NetworkRescue-v1.0.0-win-x64.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.0.0/NetworkRescue-v1.0.0-win-x64.zip) |
| Windows 10 x86 | [NetworkRescue-v1.0.0-win-x86.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.0.0/NetworkRescue-v1.0.0-win-x86.zip) |

支持基线为 Windows 10 22H2 / Windows 11、系统 .NET Framework 4.8 或更高版本。Windows 11 没有 x86 版；纯 32 位 Windows 尚未实测。

完整解压 ZIP 到可写本地目录，双击 `NetworkRescue.exe`，保留语言、图标和文档文件。无需另装 WebView2、.NET SDK、Node.js 或 Python。程序使用系统 WPF 原生界面，不嵌入浏览器、不安装后台服务；正式包未作应用数字签名。

## 主要功能

- **两种模式、八项检查**：新手模式先诊断并显示建议；高级模式提供分组证据、工具和手动选择。检查网络硬件、连接配置、DHCP 服务、DNS、HOSTS、系统代理/PAC、LSP/Winsock 和 HTTP 联网。
- **网络工具**：Ping、DNS 查询与多服务器对比、路由追踪、TCP 端口、网卡卡片及活动 TCP 快照。连接表关联 PID/可读取的程序名，可搜索地址、端口、PID、程序名并按状态筛选。
- **VPN/Wi-Fi 排查**：只读观察代理/PAC/WPAD、WinHTTP、DNS、路由、虚拟网卡和 Wi-Fi；结论附置信度、依据与下一步，不因安装 VPN 就判故障。
- **持续监测与回放**：并发监测 1–4 个域名/IP，分别选择 HTTPS/ICMP 及 HTTPS 代理路径。各目标独立统计并保留最近 300 点，配置时间线保留最近 500 事件和完整总数；事件可联动查看真实缓存样本，缺失明确显示。
- **健康快照与常用设置**：保存最多 20 个健康快照和 20 个完整监测方案。正常诊断卡可直接自动命名保存；设置内可手动命名。选择常用设置只填入参数，不自动监测。
- **报告与备份**：导出先预览脱敏结果，再选择本地保存，不自动上传；修复前备份，支持受校验的本地还原及前后对比。
- **宽带测速**：Cloudflare 节点单连接下载/上传、HTTP 往返与真实采样曲线；结果描述到该节点的路径。
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

设置、备份、历史及工作区保存在 `%LOCALAPPDATA%\NetworkRescue\`，与旧版本兼容。健康快照和方案使用当前用户 DPAPI 保护；加密不等于匿名化，分享报告仍须检查脱敏结果。监控样本不持久化，不后台驻留。TCP 进程关联不是原子实时快照，进程退出、权限和 PID 复用会限制结果；筛选只作用于最多 512 条已读取记录，不提权、不结束程序。

## 界面预览

以下为**真实 WPF 界面与合成演示数据**，用于展示布局和交互，不代表真实公网诊断或测速结论。

![新手诊断 · 暖昼](docs/screenshots/diagnosis-beginner-light.png)

<details>
<summary>高级诊断、持续监测、测速与帮助</summary>

![高级诊断 · 暖昼](docs/screenshots/diagnosis-advanced-light.png)

![持续监测 · 静夜](docs/screenshots/monitor-dark.png)

![宽带测速 · 暖昼](docs/screenshots/speed-light.png)

![使用帮助 · 暖昼](docs/screenshots/help-light.png)

</details>

## 从源码构建

使用同版本 [v1.0.0 源码](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0)，在已允许本地脚本的 PowerShell 中运行：

```powershell
.\build.ps1 -Architecture x64
.\publish.ps1 -Architecture x64 -SkipBuild

.\build.ps1 -Architecture x86
.\publish.ps1 -Architecture x86 -SkipBuild
```

构建使用系统 C# 编译器、C# 5 / .NET Framework 4.8 和 WPF 引用，不恢复 NuGet、不下载运行时。默认执行 16 套 C# 检查，输出 `build-native-x64` / `build-native-x86`；打包输出到 `dist`。直接运行 `publish.ps1 -Architecture x64` 或 `x86` 也会先完整构建检查，再打包。脚本不会上传或自动发布。

1.0.0 晋升仅变更版本/许可等发布元数据和语言说明，未改网络逻辑；正式双架构各 16 套通过，断言总数 x64 3277、x86 3275。RC5 的 1513 项独立集成保留其原 EXE 绑定，未声称针对正式 EXE 重跑；本次没有新增 VM 故障实修、纯 32 位 OS 或长稳验证。详情见[验证记录](docs/RELEASE-READINESS.v1.md)。

## 许可与反馈

自有代码采用 **GPL-3.0-only**，见 [LICENSE](LICENSE)；对应源码与构建脚本位于同版本 [v1.0.0 标签](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0)。第三方及系统服务说明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

官方提供免费使用，不售激活码、不收使用授权费。GPL 允许在履行许可、对应源码等义务的前提下收费分发，收费本身不等于侵权或冒充官方，参见 [GNU GPL 常见问题](https://www.gnu.org/licenses/gpl-faq.en.html#DoesTheGPLAllowMoney)。如遇冒充官方售卖或收费激活，可向相关平台或 `shangguanyiyu0011@gmail.com` 反馈。软件“设置→反馈”仅复制邮箱，不发送邮件或自动附加报告。

问题反馈请使用 [Issues](https://github.com/shangguanyiyu0011/NetworkRescue/issues)，先移除私人配置、凭据、原始 IP/SSID 与未脱敏报告。
