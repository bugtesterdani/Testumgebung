#requires -Version 7.0
<#
.SYNOPSIS
  Veröffentlicht die Kommandozeile (testumgebung-cli) als eigenständiges ZIP für Rechner ohne .NET – z. B. eine Offline-VM.

.DESCRIPTION
  1. Portable Python-Laufzeit sicherstellen (build/acquire-python.ps1)
  2. dotnet publish src/Testumgebung.Cli (win-x64, self-contained) nach artifacts\publish\Testumgebung-Cli
  3. C#- und C++-Worker ebenfalls self-contained (wie im MSIX), Python-Worker mit Laufzeit
  4. ZIP artifacts\Testumgebung-Cli-<Version>-win-x64.zip und dessen SHA-256

  Anleitung: docs/kommandozeile.md

.EXAMPLE
  pwsh -File build\publish-cli.ps1 -Version 1.0.0
#>
param(
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')][string]$Version = "1.0.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish\Testumgebung-Cli"

& (Join-Path $PSScriptRoot 'acquire-python.ps1')
if (-not (Test-Path (Join-Path $root 'toolchains/python/python.exe'))) { throw 'Portable Python-Laufzeit fehlt.' }

foreach ($tool in @('bin/g++.exe', 'bin/gdb.exe', 'source/source.tar', 'Dockerfile', 'toolchain.json')) {
    if (-not (Test-Path (Join-Path $root ('toolchains/w64devkit/' + $tool)))) {
        throw "Portable Toolchain unvollständig ($tool). Zuerst build/acquire-toolchain.ps1 ausführen."
    }
}

$resolved = [IO.Path]::GetFullPath($publish)
if (-not $resolved.StartsWith([IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Ungültiger Veröffentlichungsordner.'
}
if (Test-Path $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }

Write-Host "== testumgebung-cli $Version ($Configuration, win-x64, self-contained)"
dotnet publish (Join-Path $root "src\Testumgebung.Cli\Testumgebung.Cli.csproj") `
    -c $Configuration -r win-x64 --self-contained true -o $publish `
    -p:Version=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version -m:1 -nr:false --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish fehlgeschlagen." }

# Jeder .NET-Worker bekommt seine eigene Laufzeit, unabhängig von installierten Frameworks.
foreach ($worker in @(@('CSharp', 'csharp'), @('Native', 'native'))) {
    dotnet publish (Join-Path $root "src\Testumgebung.Workers.$($worker[0])\Testumgebung.Workers.$($worker[0]).csproj") `
        -c $Configuration -r win-x64 --self-contained true -o (Join-Path $publish "workers\$($worker[1])") `
        -p:Version=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version -m:1 -nr:false --nologo
    if ($LASTEXITCODE -ne 0) { throw "Worker-Publish fehlgeschlagen: $($worker[0])" }
}

foreach ($f in @("Testumgebung.Cli.exe", "ki\ki-testprogramme.md", "ki\bausteinkatalog.json", "ki\kommandozeile.md",
                 "workers\csharp", "workers\python\worker.py", "workers\python\runtime\python.exe", "workers\native")) {
    if (-not (Test-Path (Join-Path $publish $f))) { throw "Fehlt im Veröffentlichungsordner: $f" }
}
Copy-Item (Join-Path $root 'licenses') $publish -Recurse -Force
$docs = Join-Path $publish 'docs'
New-Item -ItemType Directory -Path $docs -Force | Out-Null
Copy-Item (Join-Path $root 'docs/*.md') $docs -Force
Copy-Item (Join-Path $root 'docs/licensing') $docs -Recurse -Force
Copy-Item (Join-Path $root 'docs/beispiele') $docs -Recurse -Force
Copy-Item (Join-Path $root 'docs/ki') $docs -Recurse -Force
foreach ($notice in @('LICENSE', 'LICENSE-STATUS.md', 'THIRD-PARTY-NOTICES.md', 'THIRD-PARTY-TERMS.md')) {
    Copy-Item (Join-Path $root $notice) $publish -Force
}
& (Join-Path $PSScriptRoot 'check-release-licenses.ps1') -Layout $publish

# Stable friendly command name while the managed assembly follows the solution's naming convention.
Set-Content -LiteralPath (Join-Path $publish 'testumgebung-cli.cmd') -Encoding ascii -Value '@echo off', '"%~dp0Testumgebung.Cli.exe" %*', 'exit /b %ERRORLEVEL%'
& (Join-Path $publish "Testumgebung.Cli.exe") test (Join-Path $root "docs\beispiele\kommandozeile")
if ($LASTEXITCODE -ne 0) { throw "Selbsttest der veröffentlichten Kommandozeile fehlgeschlagen." }

$zip = Join-Path $artifacts "Testumgebung-Cli-$Version-win-x64.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii
Write-Host "Fertig: $zip"
Write-Host "SHA-256: $hash"
