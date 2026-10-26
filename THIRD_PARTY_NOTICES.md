# 许可、系统组件与网络服务

## 项目许可

断网急救箱 / Network Rescue 由上官一羽开发与维护。项目自有代码采用 **GPL-3.0-only**，完整文本见 [LICENSE](LICENSE)。1.0.0 对应源码及构建脚本位于 [v1.0.0 标签](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0)，分发时应保留许可与必要声明，并履行对应源码义务。

官方免费使用，不售激活码、不收使用授权费；GPL 合规收费分发允许，收费本身不等于违法或冒充官方。冒充官方售卖或收费激活可向相关平台或 `shangguanyiyu0011@gmail.com` 反馈，参见 [GNU GPL 常见问题](https://www.gnu.org/licenses/gpl-faq.en.html#DoesTheGPLAllowMoney)。软件仅复制邮箱，不发送邮件或自动附件。

## 当前运行包与系统环境

1.0.0 使用 Windows / .NET Framework 的 WPF 原生控件、Windows Forms 和系统网络 API，包括 `System.Net.Http`、DPAPI、WLAN、TCP/进程读取等能力。基线为 Windows 10 22H2 x86/x64、Windows 11 x64，系统 .NET Framework 4.8 或更高；纯 32 位 OS 尚未实测。

这些系统组件仅引用/调用，不随应用复制分发，继续适用系统原有条款。包内不包含 WebView2 Runtime/SDK/Loader、系统 WPF/.NET DLL、Node.js 或 Python，不恢复 NuGet 或下载运行时。系统组件的签名不等于本应用签名；正式 EXE 未作应用数字签名。

当前源码与运行包不包含历史 WebView2 实现及其旧 vendor/runtime 材料；历史验收记录中的组件名称不是当前依赖声明。新增工作区、变化时间线和进程关联均为本地代码，无第三方运行包。

## 按需访问的服务

- 诊断按需访问 Microsoft、Google、百度等测试或网页端点；网站专项/持续监测请求用户指定目标，DNS 对比向用户指定 DNS 地址发出原始查询。
- 宽带测速使用 `https://speed.cloudflare.com/`，上传合成数据而不读取用户文件。端点和网络路径会影响结果。
- 默认官方 GitHub `shangguanyiyu0011/NetworkRescue`，旧空配置补全、非空自定义保留；用户手动检查稳定发布时才访问 GitHub Releases API；只打开经校验的同仓库 HTTPS 发布页，不自动下载或安装。URL 校验不是下载签名验证。

这些服务是测量/信息对象，不是打包依赖，不代表合作或背书。HTTP 保留证书校验、取消/头部边界，不发默认或代理凭据；忽略 HTTP 代理不等于绕过 VPN。

界面本地 WPF、不嵌入浏览器、不加载远程 UI、不安装后台服务。报告先脱敏预览后本地保存，不上传；工作区/备份/历史使用当前用户 DPAPI 的部分仍可能含敏感信息，不等于匿名化。监控样本只在内存，不持久化；本地记录不进入分发包。

## 功能方向参考

- [NETworkManager](https://github.com/BornToBeRoot/NETworkManager)
- [Complete Internet Repair](https://rizonesoft.com/downloads/complete-internet-repair/)

本项目为独立实现，参考其网络排查/修复方向，不包含它们的程序、品牌素材或源码，也不沿用它们的许可证。项目 GPL-3.0-only 许可不重新授权 Windows 系统组件或外部服务。
