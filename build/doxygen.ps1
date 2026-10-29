#requires -Version 7.0
param([string]$OutputDirectory = 'artifacts/doxygen/current', [string]$Python = 'python')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '1.18.0'
$sha256 = 'e84f54cfd49ef06b0b16536056dbec0c496323de28abcce53a4269463de35eaf'
$cache = Join-Path $root "artifacts/tools/doxygen/$version"
$archive = Join-Path $cache 'package.zip'
$exe = Join-Path $cache 'bin/doxygen.exe'
$output = [IO.Path]::GetFullPath($OutputDirectory, $root)
New-Item -ItemType Directory -Force $cache, $output | Out-Null
. (Join-Path $PSScriptRoot 'verified-archive.ps1')
Get-VerifiedArchive -Path $archive -Sha256 $sha256 -Urls @(
    "https://github.com/doxygen/doxygen/releases/download/Release_1_18_0/doxygen-$version.windows.x64.bin.zip",
    "https://www.doxygen.nl/files/doxygen-$version.windows.x64.bin.zip"
)
# Always extract from the verified archive; do not trust a modified cached executable.
Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $cache 'bin') -Force
$actualVersion = & $exe --version
if ($LASTEXITCODE -ne 0 -or ($actualVersion.Trim() -split ' ')[0] -ne $version) { throw 'Doxygen-Version stimmt nicht.' }
$config = Join-Path $output 'Doxyfile.generated'
$base = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Doxyfile') -Raw
$portableOutput = $output.Replace('\', '/')
$base += "`nOUTPUT_DIRECTORY = `"$portableOutput`"`nWARN_LOGFILE = `"$portableOutput/warnings.log`"`n"
Set-Content -LiteralPath $config -Value $base -Encoding utf8
Push-Location $root
try {
    & $exe $config
    if ($LASTEXITCODE -ne 0) { throw 'Doxygen fehlgeschlagen.' }
    if (-not (Test-Path (Join-Path $output 'html/index.html'))) { throw 'Doxygen-HTML-Bericht fehlt.' }
    & $Python (Join-Path $PSScriptRoot 'documentation-summary.py') $output
    if ($LASTEXITCODE -ne 0) { throw 'Dokumentationsauswertung fehlgeschlagen.' }
    Copy-Item -LiteralPath (Join-Path $root "licenses/third-party/doxygen/$version/LICENSE") -Destination $output -Force
    Copy-Item -LiteralPath (Join-Path $root "licenses/third-party/doxygen/$version/NOTICE.md") -Destination $output -Force
    @{ version=$version; archiveSha256=$sha256; commit=(& git rev-parse HEAD);
        workingTreeDirty=[bool](& git status --porcelain); scope='src; own source only' } |
        ConvertTo-Json | Set-Content (Join-Path $output 'provenance.json') -Encoding utf8
    Write-Host "API-Bericht: $output/html/index.html"
    Write-Host "Dokumentationslücken: $output/Dokumentationsstatus.html"
} finally { Pop-Location }
