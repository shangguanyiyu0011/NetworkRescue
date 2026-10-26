# 断网急救箱 · Network Rescue 1.0.0 正式发布记录

开发与维护：上官一羽。正式产品版本 `1.0.0`，程序集/文件版本 `1.0.0.0`。自有代码采用 **GPL-3.0-only**，见 [LICENSE](../LICENSE)；仓库为 [shangguanyiyu0011/NetworkRescue](https://github.com/shangguanyiyu0011/NetworkRescue)，对应源码与构建脚本使用同版本 [v1.0.0 tag](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0)。

## 正式产物与验证边界

正式晋升仅调整版本、许可等发布元数据和语言说明，未更改 RC5 网络逻辑。正式双架构各 16 套构建/检查已通过；下表绑定正式 EXE。历史 RC5 的 1513 项独立集成绑定下方 RC5 哈希，未声称针对正式 EXE 重跑，不能把旧测试数量搬成正式 EXE 的新增集成结果。

| 项目 | x64 | x86 |
| --- | --- | --- |
| 产品版本 / PE 架构 | 1.0.0 / 0x8664 | 1.0.0 / 0x014C |
| C# 套件 | 16 通过 | 16 通过 |
| 本次断言总数 | 3277 | 3275 |
| EXE 字节 | 611840 | 612352 |
| EXE SHA256 | `24DA836E4BF2845C4C700D844FD28B4CEACC012EC2D0C27E8A356773933356D9` | `88EF8D8A2C2889AB533D6AFEEEE0AF7565AB3931FC5932794C8BB13D9B86167C` |

- 基线仍为 Windows 10 22H2 x86/x64、Windows 11 x64、系统 .NET Framework 4.8。x86 检查在 x64 Windows 上执行，不代表纯 32 位 OS 实测。
- 没有新增 VM 故障注入/产品实修、管理员实修、纯 32 位 OS 或长期运行验收。历史 VM 修复只能证明其原版本、原路径。
- 正式 EXE 未作应用数字签名。系统 DLL 的签名不等于应用签名，源码/哈希校验也不等于签名。
- README 的五张图为真实 WPF UI 加合成演示数据，展示界面，不作为真实公网诊断、健康配置或宽带测速证据。
- 运行包为 `NetworkRescue-v1.0.0-win-x64.zip` / `NetworkRescue-v1.0.0-win-x86.zip`，正式运行包各 25 文件（含 LICENSE）；ZIP 最终哈希与发布附件由维护者冻结。CI 仅提供未启用示例，不宣称远端 Actions 已通过。
- 内部 `NetworkRescue` 路径兼容，健康快照/方案/备份/历史及私人数据不混入源码或分发包。默认无新运行依赖，无浏览器 UI 或后台驻留监测。

## 源码与许可

自有代码 GPL-3.0-only；正式分发保留 LICENSE、声明及同版本对应源码。官方免费、不售激活码与 GPL 允许合规收费分发可以并存；反馈针对冒充官方或收费激活，不概括判定第三方收费本身违法。

---

# 以下为 RC5 及更早历史记录（原文保留）

时间界限：下文的候选版、许可未定、未公开及“本轮/当前/最终”均描述当时冻结时点，不是正式 1.0.0 的推广或许可状态。旧哈希、测试、截图与 VM 证据保持原绑定。

# 断网急救箱 1.0.0-rc.5 发布记录 · 2026-10-26

候选版；程序集 1.0.0.0。当前中文名、内部 NetworkRescue 路径和旧配置兼容保持。没有 GitHub 发布、应用签名或新增运行依赖，自有许可证仍未选定。
## RC5 本轮交互调整

- 正常诊断卡的“保存为健康快照”直接保存，以本地日期时间自动命名；同秒重名自动加序号，成功后提示，不跳到设置。手动命名仍在设置中保留；所有健康前提和存储上限保持。
- “常用监测设置（可选）”默认收起，放在主要监测输入和开始按钮之后；空列表不画空下拉框。说明先填写目标、保存上方参数、下次选择后自行开始，保存后给出确认。
- 健康快照及常用设置的下拉框与名称输入框左对齐、等宽。六项专项的修复操作统一靠右，原始技术记录在同一行右侧，收起时一并收起左侧检查项。长文案在窄窗换行，保留禁止不适用修复的规则。
- VPN/Wi-Fi 专项使用浅蓝灰/深蓝灰区分背景；故障定位使用独立结论、置信度、五个类型化检测指标和下一步建议，0 次测试显示未单独测试，不按翻译文字推导故障。
- 测速曲线支持靠近折线/节点查看真实采样点的阶段时间与阶段平均速度；不插值制造节点，不改变测速计算。
- 使用帮助按八个主题覆盖诊断、修复、快照、持续监测、常用设置、回放、测速与报告，展开内容按步骤排版；设置→反馈显示“开发与维护：上官一羽”。

## 本版冻结验证

最终 x64 EXE SHA256：57C121A0B06E2D98648A8DF7AD6464A8B6C5F57BEF8EA1C0E99A468085068624。
最终 x86 EXE SHA256：C4AC370A0FC3A975E92FB4591A680B664A98CBE52A3F2CBA2292FE2093849FDA。

最终双架构16套测试通过；独立 x64 集成 1513 项通过，测试绑定对应 EXE，前后哈希一致。十语界面各623键，九语后台各456键。包内24文件/源码70文件，ZIP和源码的最终哈希、体积由包外同版本交付清单记录，避免自引用。

| Suite | x64 | x86 |
| --- | --- | --- |
| Engine | 163 passed | 163 passed |
| SystemChecks | 108 passed | 106 passed |
| SpeedTest | 203 passed | 203 passed |
| AdvancedTools | 84 passed | 84 passed |
| Localization | 69 passed | 69 passed |
| DiagnosticInsights | 80 passed | 80 passed |
| NetworkMonitor | 180 passed | 180 passed |
| NetworkChangeTimeline | 58 passed | 58 passed |
| NetworkWorkspace | 79 passed | 79 passed |
| ConnectionProcesses | 121 passed | 121 passed |
| RepairHistory | 177 passed | 177 passed |
| SiteDiagnostics | 73 passed | 73 passed |
| UpdateService | 174 passed | 174 passed |
| NativeController | 344 passed | 344 passed |
| NativeWindow | 1205 passed | 1205 passed |
| SoftDialog | 159 passed | 159 passed |

首次独立矩阵发现德语窄窗按钮越出容器26px，已通过限宽换行及以外层容器约束头部宽度修复。之后用户追加右侧收起联动与帮助步骤排版，两项也纳入最终构建与回归。旧预览保留，不因打包中断用户操作。

真实操作边界：预览只读诊断确认网络健康；自动健康保存、测速数据和鼠标处理器的独立 UI 集成有明确注入夹具标签，不将其称为真实宽带测速/真实健康诊断。没有修改宿主代理、DNS、DHCP、路由、网卡或防火墙。本轮没有重跑VM真实故障注入、管理员实修、纯32位OS或长期运行验收。x86测试是在当前64位Windows上执行。

历史RC4及更早记录保留在下方，仅证明对应旧版本，不能转嫁RC5。

---


追加曲线图例/坐标含义及紧凑反馈布局已经纳入最终双架构1205 UI回归和最终独立集成。本版最终整编两架构16套均通过；之前中间轮x64更新fixture出现一次HttpListener拒绝访问，单项立即复跑与本轮最终整编均通过，未改权限或网络设置。
# 以下为 RC4 及更早历史冻结记录（原文保留）
# 断网急救箱 · Network Rescue 1.0.0-rc.4 发布就绪清单

当前版本：`1.0.0-rc.4`；程序集：`1.0.0.0`。RC4 为候选版，未批准正式稳定发布。中文名改为“断网急救箱”，英文名与 NetworkRescue 内部标识、EXE 和 LocalAppData 路径不变，保持设置/修复备份兼容。

## RC4 当前交付范围

- 系统 WPF 原生便携包、C# 5 / .NET Framework 4.8，未新增运行依赖。新增 `NetworkWorkspace.cs`、`ConnectionProcesses.cs` 及对应测试，13 核心 + Controller + Window + SoftDialog，共 16 套；最终源码白名单、资源键集和运行包由主代理冻结。
- 健康快照最多 20 个，名称最长 64 字符；需最近 5 分钟内成功联网诊断，保存前配置仍与诊断一致。DPAPI CurrentUser 保护并可跨启动读取；比较去秘密后的代理 authority、DNS、IP、已配置网关与网卡状态，变化不等于故障，仅对比、不自动还原。
- 异常回放从时间线事件联动每目标最近 300 个真实缓存样本及同时间其他目标；缓存外明确缺失，不补造数据、不持久化监控采样，不将相邻事件当因果。
- 活动 TCP 快照关联 PID/可读取程序名，支持搜索与状态筛选；只读、不提权、不结束程序。进程退出、权限限制和 PID 复用使关联存在采集时点边界。
- 最多 20 个完整监测方案，保存 1–4 hosts、各目标 method/proxy、统一 interval 1000..10000 ms、count 1..3600；选择只填配置，不自动监测，开始前继续全体验证。
- 新增本地数据独立于旧设置/修复备份，DPAPI CurrentUser、schema 1、文件最多 1 MiB、原子写；损坏不静默覆盖。健康快照/方案可能含敏感 IP/域名，报告与分享仍须脱敏，不混入发布包。
- 沿用 RC3 的 1–4 目标并发、每目标 300 点、只读 Capture 时间线最近 500 事件及完整总数、取消保留、停止后脱敏预览/本地保存；无上传或后台驻留。TLS/ResponseHeadersRead/进程内错误正文缓冲上限等边界不扩大。
- 输出目标为 `NetworkRescue-v1.0.0-rc.4-win-x64` / `NetworkRescue-v1.0.0-rc.4-win-x86` 及 ZIP，不覆盖历史候选。官方免费声明仍在设置→反馈，自有许可证与公开发布尚未因此完成。

## RC4 最终冻结记录 · 2026-10-26

以下 16 套已在最终 RC4 源码上双架构整编通过。独立集成绑定最终 x64 EXE，142/142 通过；RC2/RC3 的 VM、代码与集成证据不转嫁 RC4。

| 测试套件 | x64 最终断言/结果 | x86 最终断言/结果 |
| --- | --- | --- |
| Engine | 163 通过 | 163 通过 |
| SystemChecks | 108 通过 | 106 通过 |
| SpeedTest | 203 通过 | 203 通过 |
| AdvancedTools | 84 通过 | 84 通过 |
| Localization | 69 通过 | 69 通过 |
| DiagnosticInsights | 71 通过 | 71 通过 |
| NetworkMonitor | 180 通过 | 180 通过 |
| NetworkChangeTimeline | 58 通过 | 58 通过 |
| NetworkWorkspace | 79 通过 | 79 通过 |
| ConnectionProcesses | 121 通过 | 121 通过 |
| RepairHistory | 177 通过 | 177 通过 |
| SiteDiagnostics | 73 通过 | 73 通过 |
| UpdateService | 174 通过 | 174 通过 |
| NativeController | 319 通过 | 319 通过 |
| NativeWindow | 1137 通过 | 1137 通过 |
| SoftDialog | 159 通过 | 159 通过 |

| 冻结项目 | RC4 状态 |
| --- | --- |
| 最终 x64 / x86 EXE SHA256 | x64 `E5F1EF802FBF83F7748A5C781EE3FC86AFE85E25A63F804DF28E73B25DB8BBCE`；x86 `39495431EB481E0338C09062447D85F0CA81CEE8856BF6FBC20B59242095545C` |
| ZIP 哈希、体积、PE 架构、源码白名单与语言键集 | 运行包白名单各 24 文件；源码 70 文件；PE x64/x86 分别核验。十语 UI 各 598 键，九语后台各 456 键；完整 ZIP 哈希/体积见包外同版本 SHA256 与更新验收说明，避免自引用哈希。 |
| 独立集成、最终实窗与来宾复验 | x64 独立集成 142/142；实际窗口只读诊断八项通过，真实健康快照保存并与当前配置比较无差异。此轮没有重跑 VM 故障注入或管理员实修；旧证据保持历史边界。 |
| 健康快照前提、跨启动读取、损坏保留与原子写 | Workspace 双架构各 79；Controller 各 319。独立合法 1,034,230 字节 DPAPI 快照跨进程重载通过；集成跨 Controller 实例验证明确为同进程重建。 |
| 回放缓存缺失、跨目标联动、方案选择不启动、TCP PID 有限性 | UI 双架构各 1137；连接核心各 121，真实 IPv4/IPv6 回环 PID 命中。142 项集成覆盖双目标实际 ICMP、停止 51 ms 与 1.2 s 无迟到、编辑与旧会话分离、方案恢复不启动。 |
| 纯 32 位 OS、长稳、更多机器/DPI/GPU | 未因此完成，不扩大既有承诺 |
| 自有许可证、公开仓库/发布、应用签名 | 未因此完成 |

---

### 本版复核修正与边界

- 合法重定向 Temp 的祖先 junction 不再被误拒绝；workspace 根目录与文件自身 reparse 仍拒绝，旧文件保持。
- 健康快照列表与对比只发送必要元数据；对比预览最多 256 条且变化 JSON 预算 2 MiB，显示实际预览数/总数/截断状态，不声称无差异或可导出全部。
- 独立合法大快照产生 1016 项变化，最终对比包 819,497 字符（256 项），4 MiB 序列化/反解析通过。
- runtime 目标与编辑参数独立；停止后改主机/方式/代理或删目标不会改变旧图表和回放，下一次确认开始才替换。
- 健康与方案持久化属于当前 Windows 用户的本地加密资料；不打入源码或运行包。快照比较仅包含脱敏可见配置，不保存或比较 PAC 私密路径/凭据。
# 以下为 RC3 及更早历史冻结记录（原文保留）

时间界限：下文“当前/本版/本轮”和验证结果只对应各自 RC3、RC2 或 RC1 冻结时点，中文旧名与内部标识也保留历史语境；不能用其通过结论证明 RC4 已通过。

# Network Rescue 1.0.0-rc.3 发布就绪清单

当前版本：`1.0.0-rc.3`；程序集：`1.0.0.0`。RC3 为候选版，未批准正式稳定发布。以下当前区块与后面的 RC2/RC1 历史冻结记录分开；旧计数、哈希、截图及来宾结论均不转嫁 RC3。

## RC3 当前实现与分发边界

- 默认系统 WPF 原生便携包、C# 5 / .NET Framework 4.8，不恢复 NuGet、不新增包、不复制系统 DLL；系统 `System.Net.Http` 仅为引用。基线仍为 Windows 10 22H2 x86/x64、Windows 11 x64，纯 32 位 OS 尚未实测。
- UI 十语各 535 键；后台九语各 456 键。新增 `NetworkChangeTimeline.cs` 与 `NetworkChangeTimelineTests.cs`，共 11 核心 + Controller + Window + SoftDialog，即 14 套测试；66 文件源码白名单及最终运行包由主代理冻结检查。
- 持续监测为 1–4 个域名/IP 并发目标，先全体验证再发包；原 ICMP/HTTPS 单目标入口兼容，不接受带路径的 URL。各目标独立统计、各保留最近 300 点，1–10 秒目标间隔、每目标最多 3,600 次。网关快捷添加仅取已配置网关，不证明系统有效路由；网关不回 ICMP 不代表断网。
- 运行中约每 2 秒只读 Capture 观察手动代理/PAC、DNS、IP、已配置网关与 adapter status，并记录目标无回应/恢复事件。代理/PAC 先去凭据、路径、query/fragment；失败或不完整采集不当删除，变化事件不证明因果。
- 只在内存保留最近 500 事件，同时保留完整总数/截断状态。取消保留观测；停止后复用脱敏预览再保存，不上传、不后台驻留。主题/多语沿用，时间线长值三行，完整复制和 tooltip。
- `Program.ConfigureNetworkPolicy` 在入口一次设置 TLS 1.2 与 `HttpWebRequest.DefaultMaximumErrorResponseLength = 0`，阻止 .NET Framework 4.8 内部 CONNECT 407 错误正文预缓冲；只在当前进程内，不改 OS/代理，不在并发请求间临时切换。Engine/Site/SpeedTest/UpdateService 错误路径只依赖状态/headers，成功正文读取不变。
- HTTP 使用系统 `System.Net.Http` / `ResponseHeadersRead` 修复 GET 错误正文缓冲；保留 TLS、不跟随重定向、不发送默认和代理凭据、32 KiB 响应头上限与 3 秒取消。任何 HTTP 状态收到 response 均不等同网页可用，HTTPS 407 继续提取保留。
- 输出目标为 `NetworkRescue-v1.0.0-rc.3-win-x64` / `NetworkRescue-v1.0.0-rc.3-win-x86` 及 ZIP，不覆盖历史候选。官方免费说明仍在设置→反馈，仓库未公开、自有许可证未选，不宣称已开源或已签名/发布。

## RC3 最终冻结记录

两架构14套在926D/B6F6候选均通过（rc3-final-build-x64/x86.log）。最后仅把监测报告的#编号改为普通数字，避免被片段脱敏遮盖；对该改动单独重跑两架构Controller各296项后重编，产物为下表最终哈希（rc3-controller-final-x64/x86.log、rc3-frozen-build-x64/x86.log），其余源码未变，不把SkipTests当整套验收。最终x64独立集成54/54通过且前后哈希一致。SpeedTest Timer断言随调度变化。

| 测试套件 | x64 最终断言/结果 | x86 最终断言/结果 |
| --- | --- | --- |
| Engine | 163 | 163 |
| SystemChecks | 108 | 106 |
| SpeedTest | 203 | 203 |
| AdvancedTools | 84 | 84 |
| Localization | 69 | 69 |
| DiagnosticInsights | 71 | 71 |
| NetworkMonitor | 180 | 180 |
| NetworkChangeTimeline | 58 | 58 |
| RepairHistory | 177 | 177 |
| SiteDiagnostics | 73 | 73 |
| UpdateService | 174 | 174 |
| NativeController | 296 | 296 |
| NativeWindow | 1038 | 1038 |
| SoftDialog | 159 | 159 |

| 冻结项目 | RC3 状态 |
| --- | --- |
| 最终 x64 EXE SHA256 | `FF3DEF56BC9B79E81550E2A9E8D65A2904079F7BD3A7E18BFF91F0303CA318D2` |
| 最终 x86 EXE SHA256 | `82AD39815115BE80589572B8ADA4D9F38865F4A8793461E495131ED380F9739A` |
| ZIP 哈希、大小、PE 架构与 66 文件源码白名单 | 最终结果见包外更新与验收说明及SHA256清单 |
| 独立集成、最终实窗及来宾复验 | 最终x64集成54通过；最终双架构CONNECT407纯回环复测通过；实窗/来宾结果见包外更新与验收说明 |
| 纯 32 位 OS、长稳、更多机器/DPI/GPU | 未实测或未完成，不能据当前测试扩大承诺 |
| 自有许可证、公开仓库/发布、应用签名 | 未因此完成 |

## 本轮已取得的 RC2 来宾证据（历史版本）

绑定 RC2 最终 x64 EXE SHA256：`4066FB5AC7A959FAB02B155811D1BE00C22BB712062714E1551D648099659221`。本轮已完成真实代理故障注入、取消、产品 repair + 新备份 A、还原 A 时保存 B、还原 B 往返，且有独立 HTTP 恢复证据；不能转嫁 RC3。

网络侧 DHCP option 6 错误和 DHCP 后端停用也已真实注入。产品灰色修复入口、针对 Renew 的建议必须按实际证据记录，不把只读定位、网络侧恢复写成产品 AutoDns/RenewDhcp 已成功。错误网关的网络侧注入也已完成，但未可靠区分本地/上游，Unknown 是定位边界，不能记为根因通过。管理员 repair 路径未验：既有普通用户token、无GuestControl或已授权高权限通道；未新建提权任务或更改安全设置，不写成全通过。下文“RC2 未重新执行 VM”的表述是先前冻结时点的历史状态，由本段补充其后取得的证据；旧日志/截图/哈希保持原版本。

---

# 以下为 RC2/RC1 历史冻结记录（原文保留）

# Network Rescue 1.0.0-rc.2 发布就绪清单

当前版本：`1.0.0-rc.2`；程序集：`1.0.0.0`。本批为**RC2候选交付，未批准正式稳定发布**。RC1 A/B/C/D历史哈希与证据保留在下文，不改名成RC2，也不能转嫁本版。

## RC2默认分发与架构

- 基线：Windows 10 22H2 x86/x64、Windows 11 x64，使用系统.NET Framework 4.8或更高版本。纯32位OS仍未实测。
- 生产入口：Program → NativeWindow/NativeController/NativeUiText/SoftDialog，复用十个核心模块（含UpdateService）。Program仍采用进程限定 `RenderOptions.ProcessRenderMode=SoftwareOnly`，不修改OS或显卡设置，不保证所有GPU环境无问题。
- 输出目录：`build-native-x64` / `build-native-x86`。当前候选目标名为 `NetworkRescue-v1.0.0-rc.2-win-x64` / `NetworkRescue-v1.0.0-rc.2-win-x86` 及ZIP，不覆盖RC1冻结输出。
- 轻量原生包仅24个文件：EXE、10个UI JSON、9个后台JSON、小型logo及必要说明；不含WebView2/SDK/runtime或复制的系统DLL，不恢复NuGet、不额外安装Node/Python。最终目录/ZIP大小与ZIP哈希见包外更新说明及SHA256清单，不沿用RC1阶段体积。
- UI每语498键、后台九语每语456键；本版键集、占位符和UTF-8编码已核验。

## RC2实际输出与交互口径

1. 固定八项 `DiagnosticItem.EvidenceGroups` 从typed真实快照生成分组字段，rawDetail默认折叠，不从翻译日志推断配置或故障。
2. 高级Ping/DNS/Trace/TCP使用typed样本表格与汇总，原日志折叠。活动TCP五列为本地地址/本地端口/远端地址/远端端口/状态，IPv6地址与端口独立。
3. DNS对比结果两列、窄窗一列，原1–4台显式服务器和UDP/TCP有界查询规则不变。
4. 测速底卡为状态加节点/路径/耗时/用量四字段，计算原理默认折叠。曲线使用完整成功样本累计有效字节÷阶段实际耗时的累计有效均速，与final阶段指标同口径；rawBytes只作流量。下载、上传依次测量，横轴明确各自阶段时钟。停止未完成final仍空，不用进度值冒称最终值。
5. 专项每行修复按钮：Passed/Unknown/unsupported为灰色，只有Warning/Failed的VpnDns且主报告已提供FlushDns/AutoDns时可复用。没有新增修复能力，仍受确认、即时前提、DPAPI备份与跨进程互斥约束；其他专项不直接重置配置。
6. 品牌区logo左、品牌/slogan右。网站IPv6跳过不写0ms；3xx明确说明收到跳转、未跟随，不等于页面加载；技术细节默认折叠。
7. 设置→外观提供暖昼light/静夜dark，即时切换保存并保留page、输入、report和曲线；重选当前主题不发送命令、不覆盖草稿。SoftDialog确认/结果同步主题，UAC/系统文件选择器随系统。
8. 官方免费声明位于设置→反馈：不售激活码/不收使用授权费；冒充官方售卖或收费激活可向 `shangguanyiyu0011@gmail.com` 或平台举报，程序仅复制邮箱、不发送。开源仓库/license准备中未公开，不宣称已开源、不概括所有第三方收费违法；帮助不重复声明。
9. 继续保留底部设置/帮助图标、设置内历史、使用帮助、网卡卡片、邮箱仅复制、owner/repo手动查询可信发布页且不自动安装。当前项修复与底部批量选择独立。

诊断/测速普通权限；当前界面除DisableProxy修复外，其余修复按需同SID管理员提权，提权后重新诊断与选择，不自动继续写入。恢复仍按备份涉及的DNS/代理设置决定是否提权。

折叠不等于脱敏。报告仍先预览脱敏结果再本地保存，不自动上传；原始详情、日志或表格复制分享前仍须检查。

## RC2测试与当前冻结记录

以下13套均通过。10个核心模块、Controller和SoftDialog的验证来源为 `rc2-build-x64.log` / `rc2-build-x86.log`；最终UI的一行同主题重选guard后，只重跑受影响的NativeWindow两架构，最终均971。随后两架构重新编译，不重复执行未改的核心测试。源码与最终二进制清单随本批验收证据保存。

| 测试套件 | x64断言 | x86断言 |
| --- | --- | --- |
| Engine | 163 | 163 |
| SystemChecks | 108 | 106 |
| SpeedTest | 199 | 203 |
| AdvancedTools | 84 | 84 |
| Localization | 69 | 69 |
| DiagnosticInsights | 71 | 71 |
| NetworkMonitor | 112 | 112 |
| RepairHistory | 172 | 172 |
| SiteDiagnostics | 73 | 73 |
| UpdateService | 174 | 174 |
| NativeController | 270 | 270 |
| NativeWindow | 971 | 971 |
| SoftDialog | 159 | 159 |

SpeedTest包含逐样本Timer断言，数量会随调度略变；本次两架构均使用loopback，正文流量各1,266,698字节。SystemChecks两项架构差异来自当前64位系统上可检查的目录。

最终x64 EXE SHA256：`4066FB5AC7A959FAB02B155811D1BE00C22BB712062714E1551D648099659221`。

最终x86 EXE SHA256：`F8D82883F0FE9A03E6CB75A9B583C95294DF19E90CCF6E6E5140294D1F2393DE`。

独立最终x64集成381/381通过，日志 `integration.RC2.4066FB5A.log`，测试前后二进制哈希一致。覆盖真实controller、Ping回环4/4与停止、HTTPS回环取消、连接表、十语言双主题、SoftDialog配色、主题保存/切换/重选及隔离目录重载。离屏检查不等于实际桌面；重开验证在构造器读取当前设置后注入隔离目录重读，不宣称未注入的子进程首开验证。

另外纯内存边界检查确认4 MiB消息上限、超限拒绝，119/128网卡夹具结构化证据完整；3000行typed输出只保留512行，完整计数与截断标记保留。边界检查绑定同一核心源码，后续只改主题按钮guard。

实际桌面、最终公网测速及包大小的结果见本批包外 `NetworkRescue-v1.0.0-rc.2-更新与验收.md`。RC2未重新执行VM故障注入、真实修复/还原、纯32位OS或长时间运行验收；以下RC1记录仅为历史。

## 历史RC1核心隔离来宾矩阵：已返回的事实

负责人已返回 `CORE-ACCEPTANCE-RESULT.zh-CN.md`、对应JSON、源文件哈希清单及12份snapshot。以下为**1.0核心网络侧诊断/恢复**记录，网络恢复由VBox API完成，未调用产品本地repair/restore，也未验收最终WPF UI。

| 场景 | 实际观察 | 判断与恢复边界 |
| --- | --- | --- |
| 正常只读基线 | 固定八项通过，DNS/HTTP正常 | 只证明该网络模式下核心基线 |
| DNS网络侧故障 | DHCP option 6指向127.0.0.1；DNS 0/3，但缓存路径HTTP仍200 | `LocalConfiguration / Likely`；移除错误option 6后恢复八项通过，不能声称整机完全断网或产品AutoDns已通过 |
| 虚拟DHCP服务器停用 | 60秒租约到期后APIPA、无网关/DNS、HTTP失败；来宾DHCP Client服务仍通过 | `LocalNetwork / Likely`；启动虚拟服务器并续租后八项通过，不等于产品RenewDhcp已通过 |
| 错误默认网关 | 错误网关路由真实生效，公网探测全部失败 | 结果为 `Unknown / Uncertain`，不能写成根因定位通过；恢复正确网关及原NAT后最终八项通过、DNS/HTTP正常 |

负责人记录宿主快照字节前后一致，原NAT恢复运行，未触发宿主UAC或重启。含私有信息的原始宿主快照不复制进源码或运行包。

## 历史RC1 Build A：已完成的原生VM代理闭环

证据目录为 `work/isolated-lab/v1/native-final/review-evidence`，实际报告 `NATIVE-ACCEPTANCE-RESULT.zh-CN.md` 与JSON、源哈希和来宾截图由主代理归档。本次测试绑定的旧x64 EXE SHA256为 `7251DFC8EC1DA0DF768A19EC6EE85D8F6AD348A8AC67F07DA935692F8AB487EE`；它不是后续RC1 Build D或当前RC2的哈希。

| 阶段 | 实际产品/独立操作 | 独立联网证据 |
| --- | --- | --- |
| F0 | 受保护独立helper注入普通用户失效回环代理，无PAC/自动探测 | 系统代理HTTP 0/3，直连同URL 3/3，故障真实存在 |
| F1 | 产品新手界面选择关闭残留代理，确认并执行，保存新备份A | 产品Completed 1/1；系统HTTP恢复3/3，普通用户路径实际修复成功 |
| F2 | 产品高级界面明确选择A并还原，自动保存安全备份B | 失效代理重新启用，系统HTTP回到0/3；这是预期复现故障，不是还原失败 |
| F3 | 产品高级界面选择B并还原，自动保存安全备份C | 系统HTTP恢复3/3；代理值和连接选项与原始一致，产品往返完成 |

helper只执行故障注入与独立探测，未进行清理或代替产品修复/还原。A/B/C均为本轮真实新备份，未借用旧0.1备份。旧候选也实际启动了系统WPF、执行正常八项诊断、停止取消和高级报告导出。

该旧候选同时有两个已知UI缺陷：初始1280×860窗口在1024×768来宾中越界；新手导出按钮可见且启用，但controller拒绝为仅高级可用。这两项旧缺陷已有源码与NativeWindow测试修正，后续RC1 Build D沿用当时导航、更新、卡片与图表；该RC1 Build D另有13组代码测试及92项独立集成证据；旧包证据不能替代新包的VM修复验收。

本轮旧候选测试仅覆盖普通用户HKCU代理路径，未测试管理员修复或产品本地DNS/DHCP/路由修复；纯32位系统、长稳和中断恢复最终界面流程仍待测。已终止的0.1追加验收没有因此恢复或补做。证据不含宿主私有原始网络快照，归档时也不混入凭据、`.nrb`或私人数据。

## 历史RC1最终 Build D 的测试记录（不可转嫁RC2）

历史RC1 Build D x64 EXE SHA256：`3E0279208BE7EE9C246136869E5A329891DD7B4D282D75759934321A42527E6C`；x86 EXE SHA256：`29E912DECCF17A0386E7AC7B91F9AFFBFFBA9F090DB765155ACB3F8C795652BC`。Build A `7251DFC8…`是上述F0–F3实际VM报告绑定的旧包，不能与Build D混用。前一Build B的D624二进制仅作为下述渲染对照记录，不是当前包。

最后实窗检查出现硬件渲染白/黑屏，而AX状态正常。负责人用独立wrapper加载原D624二进制做唯一变量对照：进程设置为SoftwareOnly后，真实窗口恢复正常。Build D在Program内设置进程限定的 `RenderOptions.ProcessRenderMode=SoftwareOnly`，不修改OS、显卡驱动或系统级设置；这是针对本次HWND硬件渲染失败的兼容措施，不保证所有GPU/机器均无渲染问题，AX通过也不能代替实际画面核验。

主代理已确认Build D两架构13套C#全部通过，下表为Build D实际计数。独立Build D集成92项也已通过，日志为 `integration.Build-D.3E027920.log`，范围限定为离屏WPF与loopback，不等同VM修复/还原验收。正式实窗的软件渲染和最大化布局已观察正常。前一Build C实际完整公网测速17.8秒、21.7 MiB，得到下载1.2 Mbps、上传20.0 Mbps及HTTP往返205.4 ms；同时发现毫秒级强制进度造成曲线尖峰。Build D仅把图表采样窗改为至少200毫秒，保留窗口内累计字节和最终指标，并有新增7项断言覆盖；最终D的公网复测结果以本次输出验收记录为准。Build C新手模式八项通过，已实际打开脱敏预览和保存窗口，本次取消保存，未据此声称已写出报告文件。

| 测试套件 | x64断言 | x86断言 |
| --- | --- | --- |
| Engine | 163 | 163 |
| SystemChecks | 55 | 53 |
| SpeedTest | 36 | 36 |
| AdvancedTools | 49 | 49 |
| Localization | 69 | 69 |
| DiagnosticInsights | 59 | 59 |
| NetworkMonitor | 112 | 112 |
| RepairHistory | 172 | 172 |
| SiteDiagnostics | 73 | 73 |
| UpdateService | 174 | 174 |
| NativeController | 246 | 246 |
| NativeWindow | 428 | 428 |
| SoftDialog | 154 | 154 |

## RC2稳定发布前待补证据

| 项目 | 当前状态 | 所需证据 |
| --- | --- | --- |
| 当前代码/集成检查 | 两架构13套通过，最终UI各971，独立最终x64集成381通过 | 冻结日志见上；新增改动仍须重做相应检查 |
| RC2输出与实窗 | typed分组、表格、折叠、专项按钮和阶段曲线已验证；当前主机实窗记录见包外说明 | 更多机器、DPI和GPU环境仍待扩大覆盖 |
| 测速口径 | 累计有效均速与final同口径；loopback异常/取消与最终x64公网均已检查 | 不替代不同网络/节点环境验证，不沿用RC1瞬时曲线结论 |
| 当前运行包 | RC2双架构轻量ZIP，UI498/后台456键 | 最终白名单/哈希/架构/大小见包外清单，排除旧runtime和私人数据 |
| 产品修复/还原 | RC1 Build A普通用户代理F0–F3已测，RC2未因此通过 | 本版实际前提、确认、备份、结果与复检；不扩大为DNS/DHCP/路由或管理员路径 |
| 核心网络侧故障 | RC1三场景与网络侧恢复有历史记录 | 保留错误网关Unknown/Uncertain，不提升为RC2定位通过 |
| 中断恢复/权限 | 已有历史fixture与同用户代理往返记录 | 本版UI、跨进程锁、同账户UAC/跨SID边界及失败保留步骤的对应证据 |
| 纯32位系统/长稳 | 仍待测 | 真实x86 OS与明确时长/资源/取消/异常记录 |
| 自有许可证 | 未选定，公开发布门槛未完成 | 维护者选定正式文本，不自造LICENSE或沿用vendor许可 |
| 应用签名/公开发布/远端CI | 未因此新增或完成 | 如作为发布条件需真实记录，不把本地ZIP或系统签名写成应用发布/签名 |

每次验收记录完整候选版本、程序集、EXE/ZIP哈希、系统/程序架构、网络模式、结果与缺口。二进制或资源变化后重做相应冻结检查。源码导出由主代理统一处理；不得混入私人快照、报告、凭据、订阅或`.nrb`。未完成门槛前不宣称正式稳定版。
