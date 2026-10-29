#requires -Version 7.0
<# Erstellt das weitergebbare KI-Paket aus exakt der Dokumentation des Publish-Layouts. #>
param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$Output, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'KI-Paket benötigt die vierstellige Buildversion.' }
$base = (Resolve-Path -LiteralPath $Layout).Path
$out = (Resolve-Path -LiteralPath $Output).Path
$required = @('LICENSE', 'docs/ki-testprogramme.md', 'docs/ki/bausteinkatalog.json', 'docs/ki/konvertierungsbeispiel.tproj.json')
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $base $name) -PathType Leaf)) { throw "KI-Paket: Datei fehlt im Publish-Layout: $name" }
}
$files = @('LICENSE', 'docs/ki-testprogramme.md', 'docs/kommandozeile.md', 'docs/handbuch-csharp-dll.md', 'docs/portable-python.md',
    'docs/dynamische-verbindungen.md', 'docs/parallele-gruppen.md', 'docs/externe-prozesse.md', 'docs/zwei-pruefplaetze.md', 'docs/eigene-geraete.md') |
    ForEach-Object { Get-Item -LiteralPath (Join-Path $base $_) }
foreach ($directory in @('docs/ki', 'docs/handbuch', 'docs/beispiele')) {
    $files += Get-ChildItem -LiteralPath (Join-Path $base $directory) -Recurse -File
}
$zipPath = Join-Path $out "Testumgebung_${Version}_KI-Paket.zip"
# Fail on accidental reuse, just as the MSIX builder does for an existing bundle.
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in $files | Sort-Object FullName -Unique) {
        $relative = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $entry = $zip.CreateEntry('PACKAGE-INFO.json')
    $writer = [IO.StreamWriter]::new($entry.Open())
    try {
        $writer.Write((@{ version = $Version; guide = 'docs/ki-testprogramme.md'; format = 'testumgebung.projekt'; formatVersion = 1 } | ConvertTo-Json))
    } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
Write-Host "KI-Anleitung und Referenzen: $zipPath"
