param([Parameter(Mandatory=$true)][string]$RunDirectory, [switch]$EnforceTargets)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$summary = Get-Content "$RunDirectory/report/Summary.json" -Raw | ConvertFrom-Json
$methods = @{}
foreach ($file in Get-ChildItem "$RunDirectory/raw/*/coverage.opencover.xml") {
    [xml]$doc = Get-Content $file.FullName -Raw
    foreach ($module in $doc.CoverageSession.Modules.Module) {
        foreach ($method in $module.SelectNodes('Classes/Class/Methods/Method[SequencePoints/SequencePoint]')) {
            $key = "$($module.ModuleName)|$($method.Name)"
            if (-not $methods.ContainsKey($key)) {
                $methods[$key] = @{ Module=[string]$module.ModuleName; Name=[string]$method.Name; Complexity=[int]$method.cyclomaticComplexity; Points=@{} }
            }
            foreach ($point in $method.SequencePoints.SequencePoint) {
                $pointKey = "$($point.ordinal):$($point.sl):$($point.sc):$($point.el):$($point.ec)"
                $methods[$key].Points[$pointKey] = ($methods[$key].Points[$pointKey] -or ([int]$point.vc -gt 0))
            }
        }
    }
}
$metrics = foreach ($method in $methods.Values) {
    $covered = @($method.Points.Values | Where-Object { $_ }).Count
    $fraction = $covered / $method.Points.Count
    $crap = $method.Complexity*$method.Complexity*[math]::Pow(1-$fraction,3)+$method.Complexity
    [pscustomobject]@{ Module=$method.Module; Method=$method.Name; Complexity=$method.Complexity; SequenceCoverage=[math]::Round(100*$fraction,1); CRAP=[math]::Round($crap,1); CRAPExact=$crap }
}
$metrics | Sort-Object CRAPExact -Descending | Select-Object Module, Method, Complexity, SequenceCoverage, CRAP, CRAPExact | Export-Csv "$RunDirectory/report/Methoden-Wartbarkeit.csv" -NoTypeInformation -Encoding UTF8
$rows = foreach ($project in Get-ChildItem "$root/src" -Directory) {
    $coverage = $summary.coverage.assemblies | Where-Object name -eq $project.Name
    $moduleMethods = @($metrics | Where-Object Module -eq $project.Name)
    $complex = @($moduleMethods | Where-Object Complexity -ge 15).Count
    $risky = @($moduleMethods | Where-Object { $_.Complexity -ge 15 -and $_.SequenceCoverage -lt 80 }).Count
    $max = ($moduleMethods | Measure-Object Complexity -Maximum).Maximum
    $assessment = if (-not $coverage) { 'Nicht gemessen: separater Workerprozess' } elseif ($risky -gt 0) { 'Komplexe, wenig getestete Methoden bearbeiten' } elseif ($coverage.coverage -lt 80) { 'Testlücken schließen' } elseif ($complex -gt 0) { 'Komplexe Methoden vereinfachen' } else { 'Nach diesen Kriterien unauffällig' }
    [pscustomobject]@{ Modul=$project.Name; ZeilenProzent=$coverage.coverage; ZweigeProzent=$coverage.branchcoverage; KomplexitaetMax=$max; MethodenAb15=$complex; DavonUnter80Prozent=$risky; Wartbarkeit=$assessment }
}
$rows | Export-Csv "$RunDirectory/report/Module-Wartbarkeit.csv" -NoTypeInformation -Encoding UTF8
$passed=0; $failed=0; $skipped=0
foreach ($trx in Get-ChildItem "$RunDirectory/raw/*.trx") {
    [xml]$test = Get-Content $trx.FullName -Raw
    $passed += [int]$test.TestRun.ResultSummary.Counters.passed
    $failed += [int]$test.TestRun.ResultSummary.Counters.failed
    $skipped += [int]$test.TestRun.ResultSummary.Counters.total - [int]$test.TestRun.ResultSummary.Counters.executed
}
$text = @(
    '# Testabdeckung und Wartbarkeit', '',
    "Stand: $($summary.summary.generatedon). Tests: $passed bestanden, $failed fehlgeschlagen, $skipped übersprungen.", '',
    "Gesamt ($($summary.summary.assemblies) gemessene Module): **$($summary.summary.linecoverage) % Zeilen**, **$($summary.summary.branchcoverage) % Zweige**; $($summary.summary.coveredlines) von $($summary.summary.coverablelines) ausführbaren Zeilen.", '',
    '[Interaktiven ReportGenerator-Bericht öffnen](index.html)', '',
    '| Modul | Zeilen % | Zweige % | Max. Komplexität | Methoden ≥15 | Davon <80 % getestet | Wartbarkeitsbewertung |',
    '|---|---:|---:|---:|---:|---:|---|'
)
foreach ($row in $rows) { $text += "| $($row.Modul) | $($row.ZeilenProzent) | $($row.ZweigeProzent) | $($row.KomplexitaetMax) | $($row.MethodenAb15) | $($row.DavonUnter80Prozent) | $($row.Wartbarkeit) |" }
$text += @('', '## Einordnung', '',
    'Zeilenabdeckung ist ausgeführter Code, keine Quote bestandener Tests. Zweigabdeckung erfasst Entscheidungswege. Komplexität zählt unabhängige Kontrollflusswege einer Methode. Hohe Werte erschweren Änderungen und vollständige Tests.', '',
    'Die Wartbarkeitsbewertung ist eine transparente Priorisierung: Komplexität ≥15 mit weniger als 80 % ausgeführten Sequenzpunkten, danach Modulabdeckung <80 %, danach verbleibende komplexe Methoden. Das sind projektinterne Richtwerte, kein Microsoft Maintainability Index und keine Wartbarkeits-Prozentzahl. Hohe Abdeckung beweist weder korrekte Assertions noch gute Architektur.', '',
    'Methoden-Wartbarkeit.csv enthält alle gemessenen Methoden mit Komplexität, Sequenzpunktabdeckung und CRAP = C² × (1−Abdeckung)³ + C. Sequenzpunkte können mehrfach auf derselben Zeile liegen; die Quote kann daher von der Zeilenabdeckung abweichen. Methoden werden über beide Testläufe zusammengeführt.', '',
    'Der Standardlauf über coverage.ps1 instrumentiert zusätzlich die separaten .NET-Worker CSharp und Native. Direkte dotnet-test-Aufrufe ohne IncludeDirectory erfassen nur die Module im Testprozess. Nicht erfasste Module stehen in der Modulübersicht und Qualitaetsziele.json. Python- und C++-Nutzercode sowie reale Hardware werden hier nicht als vollständig abgedeckt bewertet. Generierter Code unter obj und Testcode sind ausgeschlossen; Auto-Properties bleiben enthalten.', '',
    'Übersprungene Tests und Gründe stehen in den TRX-Dateien unter ../raw. Ohne konfigurierte Server entfallen SQL Server, PostgreSQL, MySQL und der Serverprüfungs-Export; ohne lokale Python/Paramiko-Gegenstelle zusätzlich SSH/SFTP. Lokale Protokolltests ersetzen keine Freigabe am realen Prüfplatz.', '',
    '## Größte Risikostellen', '',
    '| Modul | Methode | Komplexität | Sequenzpunkte % | CRAP |',
    '|---|---|---:|---:|---:|'
)
foreach ($method in ($metrics | Sort-Object CRAP -Descending | Select-Object -First 15)) {
    $name = $method.Method.Replace('|','\|')
    $text += "| $($method.Module) | $name | $($method.Complexity) | $($method.SequenceCoverage) | $($method.CRAP) |"
}
$text += @('', '## Wiederholen', '', 'Im Projekt Report-Coverage.cmd starten. Benötigt Windows, .NET 10 SDK und bei der ersten Ausführung NuGet-Zugriff. Das Skript erstellt einen neuen datierten Ordner und überschreibt keine bisherigen Berichte.', '',
    'Werkzeuge: [Coverlet 10.1.0](https://www.nuget.org/packages/coverlet.collector/10.1.0) und [ReportGenerator 5.5.11](https://www.nuget.org/packages/dotnet-reportgenerator-globaltool/5.5.11). Der Collector ist ausschließlich eine private Testabhängigkeit.')
$text | Set-Content "$RunDirectory/report/Wartbarkeit.md" -Encoding UTF8
$head = '<meta charset="utf-8"><style>body{font:16px Segoe UI,sans-serif;margin:32px;color:#183044}table{border-collapse:collapse}th,td{padding:9px;border:1px solid #ccd5de;text-align:left}th{background:#e6eef5}tr:nth-child(even){background:#f5f8fa}p{max-width:1100px}a{color:#075bb0}</style>'
$intro = "<h1>Testabdeckung und Wartbarkeit</h1><p>$passed Tests bestanden, $failed fehlgeschlagen, $skipped übersprungen. Zeilenabdeckung: <b>$($summary.summary.linecoverage) %</b>, Zweigabdeckung: <b>$($summary.summary.branchcoverage) %</b>.</p><p><a href='index.html'>ReportGenerator mit Quellcode und Risikostellen</a> · <a href='Wartbarkeit.md'>Ausführliche Erklärung und Grenzen</a> · <a href='Module-Wartbarkeit.csv'>CSV der Module</a></p><p>Priorisierung: komplexe Methoden (≥15) mit weniger als 80 % Sequenzpunktabdeckung, danach Modulabdeckung unter 80 %, danach weitere komplexe Methoden. Kein Microsoft Maintainability Index. Messumfang: $($summary.summary.assemblies) Module; fehlende Messungen sind in der Tabelle ausgewiesen.</p>"
$rows | ConvertTo-Html -Title 'Testumgebung: Module und Wartbarkeit' -Head $head -PreContent $intro | Set-Content "$RunDirectory/report/Wartbarkeit.html" -Encoding UTF8
$functionRows = $metrics | Sort-Object Complexity -Descending | Select-Object Module, Method, Complexity, SequenceCoverage, CRAP, @{ Name='CRAPStatus'; Expression={ if ($_.CRAPExact -ge 30) { 'ab 30' } else { 'erfüllt' } } }
$search = '<h1>Komplexität und CRAP aller Funktionen</h1><p><a href="Wartbarkeit.html">Module</a> · <a href="index.html">Quellcode und Abdeckung</a> · <a href="Methoden-Wartbarkeit.csv">CSV</a></p><p>Modul-Maximum = höchste gemessene Funktionskomplexität. Ziele: jede Funktion mit Komplexität ≤50 und CRAP <30. CRAP = C² × (1−Sequenzpunktabdeckung)³ + C. Die Qualitätsprüfung verwendet ungerundete Werte; die Tabelle zeigt eine Nachkommastelle. Async-Zustandsautomaten und lokale Funktionen sind mit ihrem tatsächlichen Messnamen aufgeführt.</p><input id="search" placeholder="Modul oder Funktion suchen" style="width:600px;padding:10px"><label><input type="checkbox" id="over">Komplexität über 50</label><label><input type="checkbox" id="crap">CRAP ab 30</label>'
$filterScript = '<script>function filter(){const q=document.getElementById("search").value.toLowerCase(),over=document.getElementById("over").checked,crap=document.getElementById("crap").checked;document.querySelectorAll("table tr").forEach((r,i)=>{if(!i)return;const c=Number(r.cells[2].textContent.replace(",",".")),risk=Number(r.cells[4].textContent.replace(",","."));r.style.display=(!q||r.textContent.toLowerCase().includes(q))&&(!over||c>50)&&(!crap||r.cells[5].textContent==="ab 30")?"":"none";});}document.getElementById("search").addEventListener("input",filter);document.getElementById("over").addEventListener("change",filter);document.getElementById("crap").addEventListener("change",filter);</script>'
$functionRows | ConvertTo-Html -Title 'Testumgebung: Funktionskomplexität' -Head $head -PreContent $search -PostContent $filterScript | Set-Content "$RunDirectory/report/Funktionen.html" -Encoding UTF8
$lineFraction = $summary.summary.coveredlines / $summary.summary.coverablelines
$branchFraction = $summary.summary.coveredbranches / $summary.summary.totalbranches
$aboveLimit = @($metrics | Where-Object Complexity -gt 50)
$aboveCrapLimit = @($metrics | Where-Object CRAPExact -ge 30)
$targets = [pscustomobject]@{
    LineMinimumPercent = 90; LineActualPercent = 100*$lineFraction; LinePassed = $lineFraction -ge 0.90
    BranchMinimumPercent = 85; BranchActualPercent = 100*$branchFraction; BranchPassed = $branchFraction -ge 0.85
    ComplexityMaximum = 50; FunctionsAboveMaximum = $aboveLimit.Count; ComplexityPassed = $aboveLimit.Count -eq 0
    CRAPMaximum = 30; CRAPMaximumExclusive = $true; CRAPActualMaximum = ($metrics | Measure-Object CRAPExact -Maximum).Maximum; FunctionsAtOrAboveCRAPMaximum = $aboveCrapLimit.Count; CRAPPassed = $aboveCrapLimit.Count -eq 0
    MeasuredModules = $summary.summary.assemblies; UnmeasuredModules = @($rows | Where-Object { $null -eq $_.ZeilenProzent } | Select-Object -ExpandProperty Modul)
}
$targets | ConvertTo-Json -Depth 4 | Set-Content "$RunDirectory/report/Qualitaetsziele.json" -Encoding UTF8
$goals = '<h1>Qualitätsziele</h1><p>Zeilen ≥90 %, Zweige ≥85 %, jede gemessene Funktion mit Komplexität ≤50 und CRAP <30. Abdeckung und CRAP werden aus den Rohzählwerten bewertet, ohne Rundung.</p><p><a href="Funktionen.html">Alle Funktionen durchsuchen</a> · <a href="Wartbarkeit.html">Module ansehen</a></p>'
$targets | ConvertTo-Html -Title 'Testumgebung: Qualitätsziele' -Head $head -PreContent $goals | Set-Content "$RunDirectory/report/Qualitaetsziele.html" -Encoding UTF8
$moduleHtml = Get-Content "$RunDirectory/report/Wartbarkeit.html" -Raw
$moduleHtml.Replace('<h1>Testabdeckung und Wartbarkeit</h1>', '<h1>Testabdeckung und Wartbarkeit</h1><p><a href="Funktionen.html">Komplexität und CRAP aller Funktionen</a> · <a href="Qualitaetsziele.html">Qualitätsziele 90 / 85 / 50 / <30</a></p>') | Set-Content "$RunDirectory/report/Wartbarkeit.html" -Encoding UTF8
if ($EnforceTargets -and (-not $targets.LinePassed -or -not $targets.BranchPassed -or -not $targets.ComplexityPassed -or -not $targets.CRAPPassed)) {
    throw "Qualitätsziele nicht erfüllt: Zeilen $($targets.LineActualPercent) %, Zweige $($targets.BranchActualPercent) %, $($aboveLimit.Count) Funktionen über Komplexität 50, $($aboveCrapLimit.Count) Funktionen ab CRAP 30."
}

