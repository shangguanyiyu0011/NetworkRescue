# Network Rescue · 断网急救箱

A local Windows network diagnosis and targeted repair tool. Scan first, inspect the evidence, then choose a repair whose prerequisites are met.

**Developed and maintained by 上官一羽** · **Version 1.0.0** · **GPL-3.0-only**

[简体中文](README.md) · [Download 1.0.0](https://github.com/shangguanyiyu0011/NetworkRescue/releases/tag/v1.0.0) · [Chinese usage guide](docs/USAGE.zh-CN.txt) · [Contributing](CONTRIBUTING.md) · [Validation record](docs/RELEASE-READINESS.v1.md)

## Download and run

| System | Portable package |
| --- | --- |
| Windows 10/11 x64 | [NetworkRescue-v1.0.0-win-x64.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.0.0/NetworkRescue-v1.0.0-win-x64.zip) |
| Windows 10 x86 | [NetworkRescue-v1.0.0-win-x86.zip](https://github.com/shangguanyiyu0011/NetworkRescue/releases/download/v1.0.0/NetworkRescue-v1.0.0-win-x86.zip) |

The baseline is Windows 10 22H2 / Windows 11 with system .NET Framework 4.8 or later. Windows 11 has no x86 edition; a pure 32-bit Windows installation has not been tested.

Extract the entire ZIP to a writable local folder and open `NetworkRescue.exe`. Keep the language, logo and documentation files together. No separate WebView2, .NET SDK, Node.js or Python installation is required. The native system WPF interface embeds no browser and installs no background service. The application is not digitally signed.

## Features

- **Two modes, eight checks:** beginner mode provides diagnosis and eligible recommendations; advanced mode provides grouped evidence, tools and manual selection. Checks cover hardware, connection configuration, DHCP service, DNS, HOSTS, system proxy/PAC, LSP/Winsock and HTTP connectivity.
- **Network tools:** Ping, DNS lookup/comparison, traceroute, TCP ports, adapter cards and active TCP snapshots. Associate readable process names/PIDs, search addresses, ports, PIDs or names, and filter by state.
- **VPN/Wi-Fi investigation:** read-only proxy/PAC/WPAD, WinHTTP, DNS, route, virtual-adapter and Wi-Fi evidence, with confidence and next steps. An installed VPN is not itself a fault.
- **Monitoring and replay:** monitor 1–4 hostnames/IPs concurrently, with per-target HTTPS/ICMP and HTTPS proxy choices. Each target keeps independent statistics and its latest 300 points. A configuration timeline retains 500 events plus the full count; replay uses actual cached samples and explicitly marks missing data.
- **Healthy snapshots and saved settings:** up to 20 snapshots and 20 complete monitoring plans. A healthy diagnosis card can save an automatically named snapshot; Settings also offers manual naming. Selecting saved settings fills inputs without starting monitoring.
- **Reports and backups:** review a redacted preview before saving locally; nothing is automatically uploaded. Repairs save a backup and support validated local restoration and before/after comparisons.
- **Speed test:** single-connection download/upload, HTTP round trips and actual sample curves to Cloudflare's endpoint, describing that path rather than guaranteed ISP bandwidth.
- **Update checks:** the official `shangguanyiyu0011/NetworkRescue` repository is configured by default. Empty older settings are filled automatically; non-empty custom repositories are retained. Checks and downloads remain user-initiated, without automatic network checks or installation.
- **Ten languages, two themes:** Simplified/Traditional Chinese, English, Japanese, Korean, German, French, Spanish, Portuguese and Russian; Warm Day and Quiet Night themes. Usage and offline troubleshooting help remain readable without Internet access.

## Repair scope

Repairs require current evidence, confirmation, a fresh configuration check and backup, followed by diagnosis.

| Action | Scope and recovery limit |
| --- | --- |
| `FlushDns` | Clears cache without changing DNS servers; old cache entries cannot be restored |
| `RenewDhcp` | Eligible DHCP adapters only; the lease cannot be rolled back precisely |
| `AutoDns` | Replaces an adapter's custom IPv4 DNS, unselected by default; configuration is recoverable from backup |
| `DisableProxy` | Current-user manual proxy switch only; address and PAC retained; configuration is recoverable |

The app does not automatically clear HOSTS, reset Winsock/WinHTTP, delete routes, remove adapters or restart Windows. A stale local-proxy recommendation requires failed loopback ports, no PAC/WPAD, failure through the manual proxy and a successful direct HTTP request, among other prerequisites. Working proxies, corporate configuration and custom DNS require their intended use to be respected.

Diagnosis and speed tests run with ordinary privileges. Repairs other than disabling the current-user manual proxy request same-account UAC as needed, then require a new diagnosis and selection. A different administrator SID is rejected. An operation completing, configuration changing and connectivity returning are separate findings.

## Monitoring and local data

ICMP does not follow HTTP proxies; a silent gateway is not proof of an outage. Ignoring the system HTTP proxy does not bypass a VPN tunnel. HTTPS reads response headers only, preserves TLS validation, has a three-second cancellation deadline and 32 KiB header limit, and sends no default/proxy credentials or redirect requests. Every HTTP status is a response, not proof of page usability; 407 identifies proxy authentication.

Saving a healthy snapshot requires a successful network diagnosis within five minutes, an unchanged configuration and complete reads at save time. Compare sanitized proxy authorities, DNS/IP, configured gateways and adapter status only. A change is not a fault, and comparison does not restore configuration automatically. Private PAC paths/credentials are not persisted or compared across restarts.

Settings, backups, history and workspace data remain under `%LOCALAPPDATA%\NetworkRescue\`, compatible with earlier versions. Current-user DPAPI protects snapshots/plans; encryption is not anonymization, so review redacted reports before sharing. Monitoring samples are not persisted and no resident monitoring runs in the background. TCP/process association is a non-atomic snapshot affected by process exits, access limitations and PID reuse. Filters cover at most 512 captured rows, without elevation or terminating processes.

## Screenshots

These five images show the **1.0.0 WPF UI from the initial upload, with synthetic demonstration data**. The finalized release retains the main layout with revised update-setting notices; screenshots have not been retaken. They are not public-network diagnosis or speed-test conclusions.

![Beginner diagnosis, Warm Day](docs/screenshots/diagnosis-beginner-light.png)

<details>
<summary>Advanced diagnosis, monitoring, speed test and help</summary>

![Advanced diagnosis, Warm Day](docs/screenshots/diagnosis-advanced-light.png)

![Monitoring, Quiet Night](docs/screenshots/monitor-dark.png)

![Speed test, Warm Day](docs/screenshots/speed-light.png)

![Usage help, Warm Day](docs/screenshots/help-light.png)

</details>

## Build from source

Use the matching [v1.0.0 source tag](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0) in a PowerShell session that already permits local scripts:

```powershell
.\build.ps1 -Architecture x64
.\publish.ps1 -Architecture x64 -SkipBuild

.\build.ps1 -Architecture x86
.\publish.ps1 -Architecture x86 -SkipBuild
```

Builds use system C# 5 / .NET Framework 4.8 and WPF references, with no NuGet restore or runtime download. The default build runs 16 C# suites and writes `build-native-x64` / `build-native-x86`; packages go to `dist`. Calling `publish.ps1 -Architecture x64` or `x86` directly first runs the full build/checks. The scripts do not upload or publish releases.

The finalized 1.0.0 release configures the official update source and fills older empty settings. Product version remains 1.0.0 and assembly/file version 1.0.0.0. All 16 suites passed for each architecture: 3318 assertions on x64 and 3316 on x86, plus a live GitHub update query. Initial-upload and RC5 evidence retains its historical binary bindings. See the [validation record](docs/RELEASE-READINESS.v1.md).

## License and feedback

The project's own code is **GPL-3.0-only**; see [LICENSE](LICENSE). Corresponding source and build scripts are provided at the matching [v1.0.0 tag](https://github.com/shangguanyiyu0011/NetworkRescue/tree/v1.0.0). See [third-party notices](THIRD_PARTY_NOTICES.md) for system components and services.

Official use is free; the maintainer sells no activation codes or usage licenses. GPL-compliant paid redistribution is allowed when its license and corresponding-source obligations are met; charging alone is not official impersonation. See the [GNU GPL FAQ](https://www.gnu.org/licenses/gpl-faq.en.html#DoesTheGPLAllowMoney). Report impersonated official sales or paid activation to the relevant platform or `shangguanyiyu0011@gmail.com`. Settings → Feedback only copies the address; the app sends no email or automatic report attachment.

Use [Issues](https://github.com/shangguanyiyu0011/NetworkRescue/issues) for bug reports, removing private configuration, credentials, raw IPs/SSIDs and unreviewed reports first.
