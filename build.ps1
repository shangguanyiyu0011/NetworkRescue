param([switch]$SkipTests, [ValidateSet('x64','x86')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
if ($Architecture -eq 'x64' -and -not [Environment]::Is64BitOperatingSystem) { throw 'Use -Architecture x86 on 32-bit Windows.' }
$taskFramework = if ([Environment]::Is64BitOperatingSystem) { 'Framework64' } else { 'Framework' }
$taskFrameworkRoot = Join-Path $env:WINDIR ('Microsoft.NET\' + $taskFramework + '\v4.0.30319')
$taskCompiler = Join-Path $taskFrameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $taskCompiler)) { throw 'The Windows .NET Framework 4.x compiler is required.' }
$taskBuildDir = Join-Path $PSScriptRoot ('build-native-' + $Architecture)
New-Item -ItemType Directory -Path $taskBuildDir -Force | Out-Null
if ((Get-Item -LiteralPath $taskBuildDir -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The build directory must not be a reparse point.' }
$taskReferences = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Net.Http.dll','/r:System.Management.dll','/r:System.Security.dll','/r:System.Xml.dll','/r:System.Runtime.Serialization.dll','/r:System.ServiceProcess.dll','/r:System.Web.Extensions.dll')
foreach ($taskReference in @('WPF\PresentationFramework.dll','WPF\PresentationCore.dll','WPF\WindowsBase.dll','System.Xaml.dll')) {
    $taskReferencePath = Join-Path $taskFrameworkRoot $taskReference
    if (-not (Test-Path -LiteralPath $taskReferencePath)) { throw ('Windows .NET WPF component missing: ' + $taskReference) }
    $taskReferences += '/r:' + $taskReferencePath
}
$taskCoreNames = @('Localization.cs','Engine.cs','SystemChecks.cs','SpeedTest.cs','AdvancedTools.cs','DiagnosticInsights.cs','NetworkMonitor.cs','NetworkChangeTimeline.cs','NetworkWorkspace.cs','ConnectionProcesses.cs','RepairHistory.cs','SiteDiagnostics.cs','UpdateService.cs','MonitoringHistory.cs','PortalLogin.cs','WifiStability.cs','DnsQuality.cs')
$taskCoreSources = $taskCoreNames | ForEach-Object { Join-Path $PSScriptRoot $_ }
$taskAppSources = @('Program.cs','NativeController.cs','NativeWindow.cs','NativeUiText.cs','SoftDialog.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
$taskAppSources += $taskCoreSources
$taskIconArguments = @()
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'assets\network-rescue.ico')) { $taskIconArguments += '/win32icon:' + (Join-Path $PSScriptRoot 'assets\network-rescue.ico') }
& $taskCompiler /nologo /codepage:65001 /langversion:5 /target:winexe "/platform:$Architecture" /optimize+ "/win32manifest:$PSScriptRoot\app.manifest" "/out:$taskBuildDir\NetworkRescue.exe" $taskReferences $taskIconArguments $taskAppSources
if ($LASTEXITCODE -ne 0) { throw 'Native application compilation failed.' }
# Only locale data and the small logo are copied; no browser runtime, SDK DLL or system WPF DLL is bundled.
if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'assets\network-rescue-logo-256.png')) {
    New-Item -ItemType Directory -Path (Join-Path $taskBuildDir 'assets') -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'assets\network-rescue-logo-256.png') -Destination (Join-Path $taskBuildDir 'assets') -Force
}
foreach ($taskLanguage in @('zh-CN','zh-TW','en','ja','ko','de','fr','es','pt','ru')) {
    $taskLocaleDir = Join-Path $taskBuildDir 'ui\locales'
    New-Item -ItemType Directory -Path $taskLocaleDir -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('ui\locales\' + $taskLanguage + '.json')) -Destination $taskLocaleDir -Force
    if ($taskLanguage -ne 'zh-CN') {
        $taskBackendDir = Join-Path $taskBuildDir 'i18n'
        New-Item -ItemType Directory -Path $taskBackendDir -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('i18n\messages.' + $taskLanguage + '.json')) -Destination $taskBackendDir -Force
    }
}
if (-not $SkipTests) {
    foreach ($taskSuite in @('Engine','SystemChecks','SpeedTest','AdvancedTools','Localization','DiagnosticInsights','NetworkMonitor','NetworkChangeTimeline','NetworkWorkspace','ConnectionProcesses','RepairHistory','SiteDiagnostics','UpdateService','MonitoringHistory','PortalLogin','WifiStability','DnsQuality')) {
        $taskTestSource = Join-Path $PSScriptRoot ($taskSuite + 'Tests.cs')
        $taskTestExe = Join-Path $taskBuildDir ($taskSuite + 'Checks.exe')
        & $taskCompiler /nologo /codepage:65001 /langversion:5 /target:exe "/platform:$Architecture" "/out:$taskTestExe" $taskReferences $taskCoreSources $taskTestSource
        if ($LASTEXITCODE -ne 0) { throw "$taskSuite test compilation failed." }
        & $taskTestExe
        if ($LASTEXITCODE -ne 0) { throw "$taskSuite tests failed." }
    }
    $taskControllerTests = Join-Path $taskBuildDir 'NativeControllerChecks.exe'
    & $taskCompiler /nologo /codepage:65001 /langversion:5 /target:exe "/platform:$Architecture" /main:NetworkRescue.NativeControllerTests "/out:$taskControllerTests" $taskReferences $taskAppSources "$PSScriptRoot\NativeControllerTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Native controller test compilation failed.' }
    & $taskControllerTests
    if ($LASTEXITCODE -ne 0) { throw 'Native controller tests failed.' }
    $taskViewTests = Join-Path $taskBuildDir 'NativeWindowChecks.exe'
    & $taskCompiler /nologo /codepage:65001 /langversion:5 /target:exe "/platform:$Architecture" /main:NativeWindowTests "/out:$taskViewTests" $taskReferences $taskAppSources "$PSScriptRoot\NativeWindowTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Native window test compilation failed.' }
    & $taskViewTests
    if ($LASTEXITCODE -ne 0) { throw 'Native window tests failed.' }
    $taskDialogTests = Join-Path $taskBuildDir 'SoftDialogChecks.exe'
    & $taskCompiler /nologo /codepage:65001 /langversion:5 /target:exe /define:SOFT_DIALOG_TESTS "/platform:$Architecture" "/out:$taskDialogTests" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "$PSScriptRoot\SoftDialog.cs" "$PSScriptRoot\SoftDialogTests.cs"
    if ($LASTEXITCODE -ne 0) { throw 'Dialog test compilation failed.' }
    & $taskDialogTests
    if ($LASTEXITCODE -ne 0) { throw 'Dialog tests failed.' }
}
Write-Output "Built native application: $taskBuildDir\NetworkRescue.exe"
