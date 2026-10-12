# Synthetic fixtures verify report thresholds; these files never enter production coverage.
$ErrorActionPreference = 'Stop'
$workspace = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$directory = Join-Path ([IO.Path]::GetTempPath()) ('coverage-goals-' + [guid]::NewGuid())
try {
    foreach ($scenario in @(
        @{ Name='valid'; Complexity=7; Points=100; Covered=1; CrapPassed=$true; ComplexityPassed=$true },
        @{ Name='crap-over'; Complexity=7; Points=100; Covered=0; CrapPassed=$false; ComplexityPassed=$true },
        @{ Name='rounded-crap'; Complexity=7; Points=147; Covered=1; CrapPassed=$false; ComplexityPassed=$true },
        @{ Name='complexity-over'; Complexity=51; Points=1; Covered=1; CrapPassed=$true; ComplexityPassed=$false }
    )) {
        $run = Join-Path $directory $scenario.Name
        New-Item -ItemType Directory -Path "$run/report", "$run/raw/fixture" -Force | Out-Null
        @{ summary=@{ generatedon='fixture'; assemblies=1; coveredlines=95; coverablelines=100; coveredbranches=90; totalbranches=100; linecoverage=95; branchcoverage=90 }; coverage=@{ assemblies=@(@{ name='Testumgebung.Contracts'; coverage=95; branchcoverage=90 }) } } |
            ConvertTo-Json -Depth 8 | Set-Content "$run/report/Summary.json"
        $points = for ($index=0; $index -lt $scenario.Points; $index++) {
            $visited = [int]($index -lt $scenario.Covered)
            "<SequencePoint ordinal='$index' sl='$($index+1)' sc='1' el='$($index+1)' ec='2' vc='$visited'/>"
        }
        "<CoverageSession><Modules><Module><ModuleName>Testumgebung.Contracts</ModuleName><Classes><Class><Methods><Method cyclomaticComplexity='$($scenario.Complexity)'><Name>FixtureFunction</Name><SequencePoints>$($points -join '')</SequencePoints></Method></Methods></Class></Classes></Module></Modules></CoverageSession>" |
            Set-Content "$run/raw/fixture/coverage.opencover.xml"
        $failure = $null
        try { & "$workspace/build/coverage-summary.ps1" -RunDirectory $run -EnforceTargets }
        catch { $failure = $_.Exception.Message }
        $result = Get-Content "$run/report/Qualitaetsziele.json" -Raw | ConvertFrom-Json
        if (-not $result.LinePassed -or -not $result.BranchPassed -or $result.CRAPPassed -ne $scenario.CrapPassed -or $result.ComplexityPassed -ne $scenario.ComplexityPassed) { throw "Wrong threshold result: $($scenario.Name)" }
        $shouldPass = $scenario.CrapPassed -and $scenario.ComplexityPassed
        if ($shouldPass -ne ($null -eq $failure)) { throw "Wrong enforcement result: $($scenario.Name)" }
        if ($scenario.Name -eq 'rounded-crap') {
            if ($result.CRAPActualMaximum -le 55 -or $result.CRAPActualMaximum -ge 55.05) { throw 'Fixture must round to 55.0 while exceeding the limit.' }
            $html = [Net.WebUtility]::HtmlDecode((Get-Content "$run/report/Funktionen.html" -Raw))
            if (-not $html.Contains('<td>über 55</td>')) { throw 'Function filter must use the exact CRAP status.' }
        }
    }
    Write-Host 'Coverage goal checks passed: valid goals, CRAP failure, rounding boundary, complexity failure.'
}
finally {
    if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }
}
