#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$report = Join-Path ([IO.Path]::GetTempPath()) ('tu-audit-test-' + [guid]::NewGuid().ToString('N') + '.json')
function dotnet {
    $global:LASTEXITCODE = 1
    '{"logs":[{"level":"error","message":"Fixture: audit feed unavailable"}]}'
}
try {
    $failure = $null
    try { & (Join-Path $root 'build/check-security.ps1') -Report $report }
    catch { $failure = $_.Exception.Message }
    if ($failure -notlike '*Exitcode 1*') { throw 'Ein fehlgeschlagener NuGet-Aufruf muss den Check verweigern und seinen Exitcode nennen.' }
    if (-not (Test-Path -LiteralPath $report)) { throw 'Der Fehlerbericht muss auch bei fehlgeschlagenem Audit gespeichert werden.' }
    $saved = Get-Content -Raw -LiteralPath $report | ConvertFrom-Json
    if ($saved.logs[0].message -ne 'Fixture: audit feed unavailable') { throw 'Die ursprüngliche NuGet-Diagnose muss erhalten bleiben.' }
    Write-Host 'Sicherheitscheck verweigert unvollständige Audits und erhält den Fehlerbericht.'
} finally {
    if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
}