[CmdletBinding()]
param([string] $OutputRoot)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $projectRoot
try {
    & python -B scripts/check_repository.py --all-files
    if ($LASTEXITCODE -ne 0) { throw 'Repository privacy check failed.' }
    & python -B scripts/check_architecture.py
    if ($LASTEXITCODE -ne 0) { throw 'Architecture boundaries failed.' }
    & python -B -m unittest discover -s tests/architecture -p 'test_*.py'
    if ($LASTEXITCODE -ne 0) { throw 'Architecture guard tests failed.' }
    $privateRoot = if ($env:VN_PRIVATE_ROOT) { $env:VN_PRIVATE_ROOT } else { Join-Path (Split-Path -Parent $projectRoot) 'VndbCli.private' }
    if (-not $OutputRoot) { $OutputRoot = Join-Path $privateRoot ('test-results/run-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)) }
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
    $relative = [IO.Path]::GetRelativePath($projectRoot, $OutputRoot)
    if (-not [IO.Path]::IsPathRooted($relative) -and $relative -ne '..' -and -not $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar)) { throw 'Test output must stay outside the repository.' }
    $logRoot = Join-Path $OutputRoot 'build-logs'
    New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
    $priorArtifacts = $env:VN_TEST_ARTIFACTS
    $env:VN_TEST_ARTIFACTS = Join-Path $OutputRoot 'artifacts'
    $webTests = @(Get-ChildItem -LiteralPath tests/web -Filter '*.test.cjs' -File | Sort-Object Name | ForEach-Object { $_.FullName })
    & node --test @webTests
    if ($LASTEXITCODE -ne 0) { throw 'Web interaction tests failed.' }
    & dotnet build VndbCli.slnx -c Release -m:1 -p:UseSharedCompilation=false --disable-build-servers "-bl:$logRoot/build-{}.binlog"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & python -B scripts/check_architecture.py --build-root (Join-Path $privateRoot 'build')
    if ($LASTEXITCODE -ne 0) { throw 'Runtime architecture boundaries failed.' }
    & dotnet test --project tests/Vndb.Tests -c Release --no-build "-bl:$logRoot/test-{}.binlog" `
        --results-directory (Join-Path $OutputRoot 'dotnet') --report-xunit-trx
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
    $uiDirectory = Join-Path $OutputRoot 'desktop-ui'
    & dotnet (Join-Path $privateRoot 'build/Vndb.Desktop.Smoke/bin/Release/net10.0/Vndb.Desktop.Smoke.dll') `
        ([IO.Path]::GetFullPath($uiDirectory))
    if ($LASTEXITCODE -ne 0) { throw 'Desktop UI checks failed.' }
    & python -B scripts/check_repository.py --all-files
    if ($LASTEXITCODE -ne 0) { throw 'Final repository privacy check failed.' }
}
finally {
    if (Test-Path variable:priorArtifacts) { $env:VN_TEST_ARTIFACTS = $priorArtifacts }
    Pop-Location
}
