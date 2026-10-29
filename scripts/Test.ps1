[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & python -B scripts/check_architecture.py
    if ($LASTEXITCODE -ne 0) { throw 'Architecture boundaries failed.' }
    & python -B -m unittest discover -s tests/architecture -p 'test_*.py'
    if ($LASTEXITCODE -ne 0) { throw 'Architecture guard tests failed.' }
    $privateRoot = if ($env:VN_PRIVATE_ROOT) { $env:VN_PRIVATE_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'VndbCli.private' }
    $logRoot = Join-Path $privateRoot 'build-logs'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    & dotnet build VndbCli.slnx -c Release -m:1 -p:UseSharedCompilation=false --disable-build-servers "-bl:$logRoot/build-{}.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & python -B scripts/check_architecture.py --build-root (Join-Path $privateRoot 'build')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime architecture boundaries failed.' }
    & dotnet test --project tests/Vndb.Tests -c Release --no-build "-bl:$logRoot/test-{}.binlog" `
        --results-directory (Join-Path $privateRoot 'test-results') --report-xunit-trx
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $uiDirectory = Join-Path $privateRoot ('test-results/desktop-ui-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    & dotnet (Join-Path $privateRoot 'build/Vndb.Desktop.Smoke/bin/Release/net10.0/Vndb.Desktop.Smoke.dll') `
        ([IO.Path]::GetFullPath($uiDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Desktop UI checks failed.' }
}
finally {
    Pop-Location
}
