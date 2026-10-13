#requires -Version 7.0
param([Parameter(Mandatory)][string]$Layout, [Parameter(Mandatory)][string]$Output, [Parameter(Mandatory)][string]$Version)
$ErrorActionPreference = 'Stop'
$base = (Resolve-Path -LiteralPath $Layout).Path
$out = (Resolve-Path -LiteralPath $Output).Path
foreach ($name in @('LICENSE','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md')) {
    Copy-Item -LiteralPath (Join-Path $base $name) -Destination $out -Force
}
Copy-Item -LiteralPath (Join-Path $base 'docs/oeffentliche-veroeffentlichung.md') -Destination (Join-Path $out 'PUBLICATION.md') -Force
$zipPath = Join-Path $out "Testumgebung_${Version}_Licenses-and-Sources.zip"
Add-Type -AssemblyName System.IO.Compression
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    $files = @('LICENSE','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md','docs/oeffentliche-veroeffentlichung.md') |
        ForEach-Object { Get-Item -LiteralPath (Join-Path $base $_) }
    foreach ($dir in @('licenses','docs/licensing','toolchains/w64devkit/source','toolchains/w64devkit/src')) {
        $files += Get-ChildItem -LiteralPath (Join-Path $base $dir) -Recurse -File
    }
    $files += Get-Item -LiteralPath (Join-Path $base 'toolchains/w64devkit/Dockerfile')
    foreach ($file in $files) {
        $relative = [IO.Path]::GetRelativePath($base, $file.FullName).Replace('\','/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $zip.Dispose() }
Write-Host "Lizenztexte und passende Toolchain-Quellen: $zipPath"
