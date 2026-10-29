#requires -Version 7.0
# Prüft die realen Paketdateien und einen fehlenden Pflichtbestandteil.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$fixtures = Join-Path $root ('artifacts/tests/ai-package-' + [guid]::NewGuid().ToString('N'))
$layout = Join-Path $fixtures 'layout'
$out = Join-Path $fixtures 'output'
New-Item -ItemType Directory -Path $layout, $out -Force | Out-Null
try {
    Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $layout -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $layout
    & (Join-Path $root 'build/create-ai-package.ps1') -Layout $layout -Output $out -Version '1.0.16.23'
    $zip = [IO.Compression.ZipFile]::OpenRead((Join-Path $out 'Testumgebung_1.0.16.23_KI-Paket.zip'))
    try {
        foreach ($name in @('LICENSE','docs/ki-testprogramme.md','docs/parallele-gruppen.md','docs/live-log-und-fortschritt.md','docs/beispiele/parallele-gruppen/parallel.tproj.json','docs/kommandozeile.md','docs/beispiele/kommandozeile/normal.tsim.json','docs/ki/bausteinkatalog.json','docs/ki/konvertierungsbeispiel.tproj.json')) {
            $entry = $zip.GetEntry($name)
            if (-not $entry) { throw "Pflichtdatei fehlt im ZIP: $name" }
            $reader = [IO.StreamReader]::new($entry.Open())
            try { $actual = $reader.ReadToEnd() } finally { $reader.Dispose() }
            if ($actual -cne [IO.File]::ReadAllText((Join-Path $layout $name))) { throw "ZIP-Inhalt weicht vom Publish-Stand ab: $name" }
        }
        $reader = [IO.StreamReader]::new($zip.GetEntry('PACKAGE-INFO.json').Open())
        try { $info = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
        if ($info.version -ne '1.0.16.23' -or $info.formatVersion -ne 1) { throw 'Paketversion oder Formatversion stimmt nicht.' }
    } finally { $zip.Dispose() }
    Remove-Item -LiteralPath (Join-Path $layout 'docs/ki/bausteinkatalog.json')
    $rejected = $false
    try { & (Join-Path $root 'build/create-ai-package.ps1') -Layout $layout -Output $out -Version '1.0.16.24' }
    catch { if ($_.Exception.Message -notlike '*Datei fehlt*') { throw }; $rejected = $true }
    if (-not $rejected -or (Test-Path -LiteralPath (Join-Path $out 'Testumgebung_1.0.16.24_KI-Paket.zip'))) { throw 'Unvollständiges KI-Paket wurde nicht sicher abgewiesen.' }
    Write-Host 'KI-Paket: Inhalte, Version und fehlender Pflichtbestandteil geprüft.'
} finally {
    $allowed = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($fixtures).StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unzulässiger Testpfad.' }
    Remove-Item -LiteralPath $fixtures -Recurse -Force
}
