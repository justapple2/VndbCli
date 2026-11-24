[CmdletBinding()]
param([string] $OutputRoot)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $privateRoot = if ($env:VN_PRIVATE_ROOT) { $env:VN_PRIVATE_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'VndbCli.private' }
    $logRoot = Join-Path $privateRoot 'build-logs'
    if (-not $OutputRoot) { $OutputRoot = Join-Path $privateRoot ('releases/0.6.2/desktop/' + (New-VnGeneration) + '/win-x64') }
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $stage = New-VnRelease $OutputRoot 'desktop' 'win-x64'
    & dotnet publish src/Vndb.Desktop/Vndb.Desktop.csproj -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
        -m:1 -p:UseSharedCompilation=false --disable-build-servers -o $stage "-bl:$logRoot/desktop-publish-{}.binlog" "-p:VnPublishFiles=$stage.msbuild-files" -p:VnPublishProject=Vndb.Desktop
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }
    & python -B scripts/check_architecture.py --deps "Desktop=$privateRoot/build/Vndb.Desktop/bin/Release/net10.0/win-x64/Vndb.Desktop.deps.json"
    if ($LASTEXITCODE -ne 0) { throw 'Desktop runtime dependency check failed.' }
    Complete-VnRelease $stage @("Desktop=$privateRoot/build/Vndb.Desktop/bin/Release/net10.0/win-x64/Vndb.Desktop.deps.json")
    Write-Output "Published Windows x64 desktop: $OutputRoot"
}
finally { Pop-Location }
