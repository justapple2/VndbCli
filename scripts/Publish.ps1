[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'linux-arm', 'linux-musl-x64', 'linux-musl-arm64')]
    [string[]] $Runtime = @('win-x64'),
    [string] $OutputRoot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $privateRoot = if ($env:VN_PRIVATE_ROOT) { $env:VN_PRIVATE_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'VndbCli.private' }
    $logRoot = Join-Path $privateRoot 'build-logs'
    if (-not $OutputRoot) { $OutputRoot = Join-Path $privateRoot ('releases/0.6.0/cli/' + (New-VnGeneration)) }
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null

    foreach ($runtimeId in $Runtime) {
        $outputPath = Join-Path $OutputRoot $runtimeId
        $stage = New-VnRelease $outputPath 'cli' $runtimeId
        $binlog = "$logRoot/publish-$runtimeId-{}.binlog"
        & dotnet publish src/Vndb.Cli/Vndb.Cli.csproj -c Release -r $runtimeId --self-contained true `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:EnableCompressionInSingleFile=true -m:1 -p:UseSharedCompilation=false --disable-build-servers -o $stage "-bl:$binlog" "-p:VnPublishFiles=$stage.msbuild-files" -p:VnPublishProject=Vndb.Cli
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $runtimeId (exit $LASTEXITCODE)." }
        # Single-file bundles embed this exact RID dependency manifest.
        & python -B scripts/check_architecture.py --deps "Cli=$privateRoot/build/Vndb.Cli/bin/Release/net10.0/$runtimeId/vndb.deps.json"
        if ($LASTEXITCODE -ne 0) { throw "CLI runtime dependency check failed for $runtimeId." }
        Complete-VnRelease $stage @("Cli=$privateRoot/build/Vndb.Cli/bin/Release/net10.0/$runtimeId/vndb.deps.json")
        Write-Output "Published CLI: $outputPath (Web queries, reviewed organization/recovery; database stays on NAS)"
    }
}
finally {
    Pop-Location
}
