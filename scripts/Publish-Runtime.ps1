[CmdletBinding()]
param(
    [ValidateSet('linux-x64', 'linux-arm64')]
    [string] $Runtime = 'linux-x64',
    [string] $OutputRoot
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Release.Common.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    $privateRoot = if ($env:VN_PRIVATE_ROOT) { $env:VN_PRIVATE_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'VndbCli.private' }
    $logRoot = Join-Path $privateRoot 'build-logs'
    if (-not $OutputRoot) { $OutputRoot = Join-Path $privateRoot ('releases/0.6.1/runtime/' + (New-VnGeneration)) }
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $releaseRoot = Join-Path $OutputRoot $Runtime
    $stage = New-VnRelease $releaseRoot 'runtime' $Runtime
    foreach ($component in @(@{Name='web';Project='Vndb.Web'}, @{Name='cli';Project='Vndb.Cli'}, @{Name='admin';Project='Vndb.Admin'})) {
        $destination = Join-Path $stage $component.Name
        & dotnet publish "src/$($component.Project)/$($component.Project).csproj" -c Release -r $Runtime `
            --self-contained false -p:UseAppHost=false -m:1 -p:UseSharedCompilation=false --disable-build-servers -o $destination "-bl:$logRoot/runtime-$($component.Name)-{}.binlog" "-p:VnPublishFiles=$stage.$($component.Name).msbuild-files" "-p:VnPublishProject=$($component.Project)"
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $($component.Name)" }
        $assemblyName = @{web='Vndb.Web';cli='vndb';admin='vndb-admin'}[$component.Name]
        $moduleName = $component.Project.Substring('Vndb.'.Length)
        & python -B scripts/check_architecture.py --deps "$moduleName=$destination/$assemblyName.deps.json"
        if ($LASTEXITCODE -ne 0) { throw "Runtime dependency check failed: $($component.Name)" }
    }
    Copy-Item -LiteralPath deploy/Dockerfile.runtime -Destination (Join-Path $stage 'Dockerfile')
    Complete-VnRelease $stage
    Write-Output "NAS runtime build context: $releaseRoot"
}
finally { Pop-Location }
