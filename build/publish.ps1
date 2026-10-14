<#
.SYNOPSIS
  Baut die Testumgebung für den Pilotbetrieb: Tests, Veröffentlichung (win-x64) und optional den Installer.

.DESCRIPTION
  1. dotnet test (abschaltbar mit -SkipTests)
  2. dotnet publish der Desktop-Anwendung nach artifacts\publish\Testumgebung (inkl. workers\csharp, workers\python, workers\native)
  3. Prüfsummenliste artifacts\publish\SHA256SUMS.txt
  4. Mit -Installer: Inno Setup (ISCC.exe) → artifacts\installer\Testumgebung-Setup-<Version>.exe

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\publish.ps1 -Version 1.0.0 -Installer
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [switch]$SelfContained,
    [switch]$SkipTests,
    [switch]$SkipChecksums,
    [switch]$Installer,
    [string]$Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\Testumgebung"

& (Join-Path $PSScriptRoot 'acquire-python.ps1')
if (-not (Test-Path (Join-Path $root 'toolchains/python/python.exe'))) { throw 'Portable Python-Laufzeit fehlt.' }

foreach ($tool in @('bin/g++.exe', 'bin/gdb.exe', 'source/source.tar', 'toolchain.json')) {
    if (-not (Test-Path (Join-Path $root ('toolchains/w64devkit/' + $tool)))) {
        throw "Portable Toolchain unvollständig ($tool). Zuerst build/acquire-toolchain.ps1 ausführen."
    }
}

Write-Host "== Testumgebung $Version ($Configuration, $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }))"

if (-not $SkipTests) {
    Write-Host "== Tests"
    dotnet test (Join-Path $root "Testumgebung.sln") -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Tests fehlgeschlagen – keine Veröffentlichung." }
}

Write-Host "== Veröffentlichen nach $publish"
$resolvedPublish = [IO.Path]::GetFullPath($publish)
$resolvedArtifacts = [IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar
if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) { throw 'Ungültiger Veröffentlichungsordner.' }
if (Test-Path $resolvedPublish) { Remove-Item -LiteralPath $resolvedPublish -Recurse -Force }
$sc = if ($SelfContained) { "true" } else { "false" }
dotnet publish (Join-Path $root "src\Testumgebung.Desktop\Testumgebung.Desktop.csproj") `
    -c $Configuration -r win-x64 --self-contained $sc -o $publish `
    -p:Version=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version -m:1 -nr:false --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen." }

foreach ($w in @("workers\csharp", "workers\python\worker.py", "workers\python\runtime\python.exe", "workers\python\runtime\python314.zip", "workers\python\runtime\LICENSE.txt", "workers\python\runtime\pip-bootstrap\pip.whl", "workers\native", "toolchains\w64devkit\bin\g++.exe", "toolchains\w64devkit\bin\gdb.exe", "toolchains\w64devkit\source\source.tar")) {
    if (-not (Test-Path (Join-Path $publish $w))) { throw "Worker fehlt im Veröffentlichungsordner: $w" }
}

# Dokumentation für Administratoren und Werker beilegen
$docs = Join-Path $publish "docs"
New-Item -ItemType Directory -Force $docs | Out-Null
Copy-Item (Join-Path $root "docs\*.md") $docs
Copy-Item (Join-Path $root "docs\ki") $docs -Recurse -Force
Copy-Item (Join-Path $root "docs\beispiele") $docs -Recurse -Force
Copy-Item (Join-Path $root "docs\handbuch") $docs -Recurse -Force
Copy-Item (Join-Path $root "docs\pilot") $docs -Recurse -Force
Copy-Item (Join-Path $root "docs\sql") $docs -Recurse -Force
if (Test-Path (Join-Path $root 'docs/licensing')) {
    Copy-Item (Join-Path $root 'docs/licensing') $docs -Recurse -Force
}
# Original third-party texts must accompany redistributed dependencies.
if (Test-Path (Join-Path $root 'licenses')) {
    Copy-Item (Join-Path $root 'licenses') $publish -Recurse -Force
}
foreach ($notice in @('THIRD-PARTY-NOTICES.md','LICENSE-STATUS.md','THIRD-PARTY-TERMS.md','LICENSE','LICENSE.md','LICENSE.txt')) {
    if (Test-Path (Join-Path $root $notice)) { Copy-Item (Join-Path $root $notice) $publish -Force }
}
if (Test-Path (Join-Path $root 'output/pdf/Testumgebung-Gesamthandbuch.pdf')) {
    Copy-Item (Join-Path $root 'output/pdf/Testumgebung-Gesamthandbuch.pdf') $docs -Force
}

Write-Host "== Prüfsummen"
if (-not $SkipChecksums) {
$sums = Get-ChildItem $publish -Recurse -File | ForEach-Object {
    $hash = (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($_.FullName.Substring($publish.Length + 1))"
}
$sums | Set-Content (Join-Path $artifacts "publish\SHA256SUMS.txt") -Encoding utf8
} elseif (Test-Path (Join-Path $artifacts 'publish\SHA256SUMS.txt')) {
    Remove-Item -LiteralPath (Join-Path $artifacts 'publish\SHA256SUMS.txt') -Force
}

if ($Installer) {
    if (-not (Test-Path $Iscc)) { throw "Inno Setup 6 nicht gefunden ($Iscc). Installieren oder -Iscc angeben." }
    Write-Host "== Installer"
    & $Iscc "/DAppVersion=$Version" "/DSourceDir=$publish" "/DOutputDir=$(Join-Path $artifacts 'installer')" `
        "/DSelfContained=$sc" (Join-Path $root "installer\Testumgebung.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup fehlgeschlagen." }
}

Write-Host "== Fertig: $publish"
