#requires -Version 7.0
# Offizielles, fest versioniertes Python-Embed-Paket; keine globale Installation.
param()
$ErrorActionPreference = 'Stop'
$version = '3.14.8'
$expected = 'a93abe456ab01bd96d7a085b3cdb6566b3063f4241360d114142fbdb07f0a310'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../toolchains'))
$destination = Join-Path $toolRoot 'python'
# Not used by the supported embedded standard library (no Tcl/Tk distribution).
# Keep the original downloaded ZIP intact; omit the unverified optional DLL.
function Remove-UnverifiedPythonExtra([string]$runtime) {
    $extra = Join-Path $runtime 'libtommath.dll'
    if (Test-Path -LiteralPath $extra) { Remove-Item -LiteralPath $extra -Force }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../THIRD-PARTY-TERMS.md') -Destination (Join-Path $runtime 'THIRD-PARTY-TERMS.md') -Force
}
if (Test-Path (Join-Path $destination 'runtime.json')) {
    $metadata = Get-Content (Join-Path $destination 'runtime.json') -Raw | ConvertFrom-Json
    if ($metadata.version -ne $version -or $metadata.archiveSha256 -ne $expected) { throw 'Vorhandene Python-Version stimmt nicht mit der Build-Vorgabe überein.' }
    foreach ($file in @('python.exe','python314.dll','python314.zip','python314._pth','LICENSE.txt')) {
        if (-not (Test-Path (Join-Path $destination $file))) { throw "Python-Laufzeit unvollständig: $file" }
    }
    Remove-UnverifiedPythonExtra $destination
    Write-Host "Portable Python-Laufzeit $version bereits vorhanden."
    & (Join-Path $PSScriptRoot 'acquire-pip.ps1')
    return
}
if (Test-Path $destination) { throw 'Unvollständige Python-Laufzeit vorhanden; bitte prüfen.' }
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$stage = Join-Path $toolRoot ('.python-stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$url = "https://www.python.org/ftp/python/$version/python-$version-embed-amd64.zip"
$archive = Join-Path $stage 'python-embed-amd64.zip'
Invoke-WebRequest -Uri $url -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Python-Archiv-Prüfsumme falsch.' }
$extracted = Join-Path $stage 'runtime'
Expand-Archive -LiteralPath $archive -DestinationPath $extracted
Remove-UnverifiedPythonExtra $extracted
# Isoliert von globalem PYTHONPATH und Benutzerpaketen; optionale Pakete neben der Laufzeit.
@('python314.zip', '.', 'Lib/site-packages', '..') | Set-Content (Join-Path $extracted 'python314._pth') -Encoding ascii
New-Item -ItemType Directory -Path (Join-Path $extracted 'Lib/site-packages') -Force | Out-Null
& (Join-Path $extracted 'python.exe') -c 'import sys,json,ssl,ctypes,sqlite3,importlib.metadata; assert sys.version_info[:3] == (3,14,8); print(sys.version)'
if ($LASTEXITCODE) { throw 'Portable Python-Laufzeit konnte nicht geprüft werden.' }
@{ distribution='CPython embeddable'; version=$version; architecture='x64'; archiveSha256=$expected; archiveUrl=$url } |
    ConvertTo-Json | Set-Content (Join-Path $extracted 'runtime.json') -Encoding utf8
$resolvedDestination = [IO.Path]::GetFullPath($destination)
if (-not $resolvedDestination.StartsWith($toolRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Python-Ziel außerhalb des Projekts.' }
Move-Item -LiteralPath $extracted -Destination $resolvedDestination
Write-Host "Portable Python-Laufzeit bereit: $destination"
& (Join-Path $PSScriptRoot 'acquire-pip.ps1')
