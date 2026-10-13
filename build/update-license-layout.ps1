#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$layout = Join-Path $root 'artifacts/publish/Testumgebung'
if (-not (Test-Path -LiteralPath (Join-Path $layout 'Testumgebung.Desktop.dll'))) { throw 'Zuerst MSIX mit PrepareOnly vorbereiten.' }
Copy-Item (Join-Path $root 'docs/*.md') -Destination (Join-Path $layout 'docs') -Force
Copy-Item -LiteralPath (Join-Path $root 'docs/licensing') -Destination (Join-Path $layout 'docs') -Recurse -Force
Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $layout -Recurse -Force
foreach ($name in @('LICENSE','LICENSE-STATUS.md','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md')) { Copy-Item -LiteralPath (Join-Path $root $name) -Destination $layout -Force }
Copy-Item -LiteralPath (Join-Path $root 'THIRD-PARTY-TERMS.md') -Destination (Join-Path $layout 'workers/python/runtime') -Force
Copy-Item -LiteralPath (Join-Path $root 'output/pdf/Testumgebung-Gesamthandbuch.pdf') -Destination (Join-Path $layout 'docs') -Force
& (Join-Path $PSScriptRoot 'check-release-licenses.ps1') -Layout $layout
