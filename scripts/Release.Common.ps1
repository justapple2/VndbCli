# Publish output stays in a new sibling staging directory until its exact file set is sealed.
function New-VnRelease([string]$Target, [string]$Kind, [string]$Rid) {
    $result = & python -B (Join-Path $PSScriptRoot 'release_guard.py') begin $Target --kind $Kind --rid $Rid
    if ($LASTEXITCODE -ne 0) { throw 'Release preparation failed; existing output preserved.' }
    return ($result | ConvertFrom-Json).stage
}
function Complete-VnRelease([string]$Stage, [string[]]$Dependencies = @()) {
    $arguments = @('-B', (Join-Path $PSScriptRoot 'release_guard.py'), 'capture', $Stage)
    foreach ($dependency in $Dependencies) { $arguments += @('--deps', $dependency) }
    & python @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Publish file/dependency capture failed; staging retained.' }
    & python -B (Join-Path $PSScriptRoot 'release_guard.py') finalize $Stage
    if ($LASTEXITCODE -ne 0) { throw 'Release sealing failed; staging retained.' }
}
function New-VnGeneration {
    return [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
}
