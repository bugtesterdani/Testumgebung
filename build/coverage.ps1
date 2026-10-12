param([string]$PythonForSsh = '', [switch]$EnforceTargets)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    $run = Join-Path $root ('artifacts/coverage/' + (Get-Date -Format 'yyyy-MM-dd_HHmmss'))
    $toolDir = Join-Path $root 'artifacts/tools/reportgenerator'
    $tool = Join-Path $toolDir 'reportgenerator.exe'
    if (-not (Test-Path $tool)) {
        & dotnet tool install dotnet-reportgenerator-globaltool --tool-path $toolDir --version 5.5.11
        if ($LASTEXITCODE -ne 0) { throw 'ReportGenerator konnte nicht installiert werden.' }
    }
    $oldPython = $env:TESTUMGEBUNG_SSH_TEST_PYTHON
    try {
        if ($PythonForSsh) { $env:TESTUMGEBUNG_SSH_TEST_PYTHON = $PythonForSsh }
        # Sequential collectors avoid instrumenting the same worker copy twice.
        $workerDirectories = "$root/tests/Engine.Tests/bin/Release/net10.0/workers/csharp/,$root/tests/Engine.Tests/bin/Release/net10.0/workers/native/"
        & dotnet test tests/Engine.Tests/Engine.Tests.csproj -c Release --collect 'XPlat Code Coverage' --settings build/coverage.runsettings --results-directory "$run/raw" --logger trx -- "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.IncludeDirectory=$workerDirectories"
        $engineExit = $LASTEXITCODE
        & dotnet test tests/Desktop.UiTests/Desktop.UiTests.csproj -c Release --collect 'XPlat Code Coverage' --settings build/coverage.runsettings --results-directory "$run/raw" --logger trx
        $testExit = if ($engineExit -ne 0) { $engineExit } else { $LASTEXITCODE }
    } finally { $env:TESTUMGEBUNG_SSH_TEST_PYTHON = $oldPython }
    & $tool "-reports:$run/raw/*/coverage.opencover.xml" "-targetdir:$run/report" '-reporttypes:Html;MarkdownAssembliesSummary;JsonSummary;CsvSummary;TextSummary' '-title:Testumgebung - Testabdeckung und Risikostellen'
    if ($LASTEXITCODE -ne 0) { throw 'ReportGenerator fehlgeschlagen.' }
    & "$PSScriptRoot/coverage-summary.ps1" -RunDirectory $run -EnforceTargets:$EnforceTargets
    Write-Host "Bericht: $run/report/index.html"
    if ($testExit -ne 0) { throw "Tests fehlgeschlagen ($testExit). Bericht wurde trotzdem erstellt." }
} finally { Pop-Location }
