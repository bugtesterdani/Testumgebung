$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$previous = $env:TESTUMGEBUNG_CAB_WRITE_REPORT
Push-Location $root
try {
    $env:TESTUMGEBUNG_CAB_WRITE_REPORT = '1'
    & dotnet test tests/Engine.Tests/Engine.Tests.csproj --filter FullyQualifiedName~Coverage_report_is_current_and_deferrals_are_exact
    if ($LASTEXITCODE -ne 0) { throw 'cab-Abdeckungsnachweise fehlgeschlagen; Bericht nicht freigeben.' }
} finally {
    $env:TESTUMGEBUNG_CAB_WRITE_REPORT = $previous
    Pop-Location
}
