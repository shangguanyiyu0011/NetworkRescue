param([switch]$SkipBuild, [ValidateSet('x64','x86')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -Architecture $Architecture }
$taskBuildDir = Join-Path $PSScriptRoot ('build-native-' + $Architecture)
$taskName = 'NetworkRescue-v1.2.0-win-' + $Architecture
$taskDist = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'dist'))
$taskStage = Join-Path $taskDist ('.native-package-' + [Guid]::NewGuid().ToString('N'))
$taskFolder = Join-Path $taskDist $taskName
$taskZip = Join-Path $taskDist ($taskName + '.zip')
$taskPendingZip = $taskStage + '.zip'
$taskFiles = [ordered]@{'NetworkRescue.exe'=(Join-Path $taskBuildDir 'NetworkRescue.exe');'assets/network-rescue-logo-256.png'=(Join-Path $taskBuildDir 'assets\network-rescue-logo-256.png')}
foreach ($taskLanguage in @('zh-CN','zh-TW','en','ja','ko','de','fr','es','pt','ru')) {
    $taskRelative='ui/locales/'+$taskLanguage+'.json'
    $taskFiles[$taskRelative]=Join-Path $taskBuildDir $taskRelative
    if($taskLanguage -ne 'zh-CN') { $taskRelative='i18n/messages.'+$taskLanguage+'.json'; $taskFiles[$taskRelative]=Join-Path $taskBuildDir $taskRelative }
}
foreach($taskRelative in @('docs/USAGE.zh-CN.txt','docs/RELEASE-READINESS.v1.md','docs/RELEASE-READINESS.v1.1.md','docs/RELEASE-NOTES.v1.1.md','docs/RELEASE-READINESS.v1.2.md','docs/RELEASE-NOTES.v1.2.md','THIRD_PARTY_NOTICES.md','LICENSE')) { $taskFiles[$taskRelative]=Join-Path $PSScriptRoot $taskRelative }
foreach($taskSource in $taskFiles.Values) { if(-not(Test-Path -LiteralPath $taskSource -PathType Leaf)){throw ('Missing native package file: '+$taskSource)} }
$taskExe=Join-Path $taskBuildDir 'NetworkRescue.exe'
if([Reflection.AssemblyName]::GetAssemblyName($taskExe).Version.ToString() -ne '1.2.0.0'){throw 'Wrong native application assembly version'}
$taskReader=[IO.BinaryReader]::new([IO.File]::OpenRead($taskExe))
try {
    if($taskReader.ReadUInt16() -ne 0x5a4d){throw 'Missing MZ header'}
    $taskReader.BaseStream.Position=0x3c
    $taskPe=$taskReader.ReadInt32()
    if($taskPe -lt 64 -or $taskPe -gt $taskReader.BaseStream.Length-6){throw 'Invalid PE offset'}
    $taskReader.BaseStream.Position=$taskPe
    if($taskReader.ReadUInt32() -ne 0x4550){throw 'Missing PE signature'}
    $taskMachine=$taskReader.ReadUInt16()
} finally {$taskReader.Dispose()}
if(($Architecture -eq 'x64' -and $taskMachine -ne 0x8664) -or ($Architecture -eq 'x86' -and $taskMachine -ne 0x014c)){throw 'Wrong native executable architecture'}
New-Item -ItemType Directory -Path $taskDist -Force | Out-Null
foreach($taskPath in @($taskStage,$taskFolder,$taskZip,$taskPendingZip)) {
    if(-not[IO.Path]::GetFullPath($taskPath).StartsWith($taskDist+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package path escapes dist'}
    if((Test-Path -LiteralPath $taskPath) -and ((Get-Item -LiteralPath $taskPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Package output is a reparse point'}
}
if((Get-Item -LiteralPath $taskDist -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'dist is a reparse point'}
New-Item -ItemType Directory -Path $taskStage | Out-Null
$taskTotalBytes=0
foreach($taskRelative in $taskFiles.Keys) {
    if($taskRelative -match '(^/|\\|:|(^|/)\.\.?(/|$))|\.(dll|nrb|nrh|log)$|(^|/)(runtime|packages|Backups)/'){throw 'Forbidden native package entry'}
    $taskTarget=Join-Path $taskStage $taskRelative
    New-Item -ItemType Directory -Path (Split-Path -Parent $taskTarget) -Force | Out-Null
    Copy-Item -LiteralPath $taskFiles[$taskRelative] -Destination $taskTarget
    $taskTotalBytes+=(Get-Item -LiteralPath $taskTarget).Length
}
if($taskTotalBytes -gt 10MB){throw 'The native portable package unexpectedly exceeds 10 MiB; inspect dependencies before publishing.'}
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
# Framework and modern .NET disagree about directory separators in CreateFromDirectory.
# Create each allowlisted entry explicitly so every ZIP has portable forward-slash names.
$taskZipWriter=[IO.Compression.ZipFile]::Open($taskPendingZip,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($taskRelative in $taskFiles.Keys) {
        $taskZipEntry=$taskZipWriter.CreateEntry($taskRelative,[IO.Compression.CompressionLevel]::Optimal)
        $taskSourceStream=[IO.File]::OpenRead((Join-Path $taskStage $taskRelative))
        try {
            $taskEntryStream=$taskZipEntry.Open()
            try {$taskSourceStream.CopyTo($taskEntryStream)} finally {$taskEntryStream.Dispose()}
        } finally {$taskSourceStream.Dispose()}
    }
} finally {$taskZipWriter.Dispose()}
$taskArchive=[IO.Compression.ZipFile]::OpenRead($taskPendingZip)
try {
    $taskExpected=@($taskFiles.Keys|Sort-Object)
    $taskActual=@($taskArchive.Entries|ForEach-Object{$_.FullName}|Sort-Object)
    if($taskActual.Count -ne $taskExpected.Count -or @(Compare-Object $taskExpected $taskActual -CaseSensitive).Count){throw 'Native ZIP allowlist mismatch'}
    foreach($taskEntry in $taskArchive.Entries) {
        $taskStream=$taskEntry.Open(); $taskHasher=[Security.Cryptography.SHA256]::Create()
        try {$taskHash=[BitConverter]::ToString($taskHasher.ComputeHash($taskStream)).Replace('-','')} finally {$taskStream.Dispose();$taskHasher.Dispose()}
        if($taskHash -ne (Get-FileHash -LiteralPath (Join-Path $taskStage $taskEntry.FullName) -Algorithm SHA256).Hash){throw ('Native ZIP content mismatch: '+$taskEntry.FullName)}
    }
} finally {$taskArchive.Dispose()}
$taskPrevious='.previous-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N')
foreach($taskOld in @($taskFolder,$taskZip)) { if(Test-Path -LiteralPath $taskOld){Move-Item -LiteralPath $taskOld -Destination ($taskOld+$taskPrevious)} }
Move-Item -LiteralPath $taskStage -Destination $taskFolder
Move-Item -LiteralPath $taskPendingZip -Destination $taskZip
Write-Output ('Verified native files: '+$taskFiles.Count+'; unpacked bytes: '+$taskTotalBytes)
Write-Output ('Folder: '+$taskFolder)
Write-Output ('ZIP: '+$taskZip+'; bytes: '+(Get-Item -LiteralPath $taskZip).Length)
Write-Output ('SHA256: '+(Get-FileHash -LiteralPath $taskZip -Algorithm SHA256).Hash)
