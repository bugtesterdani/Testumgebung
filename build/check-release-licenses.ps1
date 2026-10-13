#requires -Version 7.0

param([Parameter(Mandatory)][string]$Layout)

$ErrorActionPreference = 'Stop'

$base = (Resolve-Path -LiteralPath $Layout).Path

foreach ($file in @('LICENSE','LICENSE-STATUS.md','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md','licenses/third-party/python/LICENSE.txt','toolchains/w64devkit/source/source.tar','toolchains/w64devkit/Dockerfile')) {

    if (-not (Test-Path -LiteralPath (Join-Path $base $file))) { throw "Lizenz-Nachweis fehlt: $file" }

}

$forbidden = Get-ChildItem -LiteralPath $base -File -Recurse | Where-Object { $_.Name -match '^(Microsoft\.Data\.SqlClient\.SNI.*\.dll|libtommath\.dll)$' }

if ($forbidden) { throw "Nicht freigegebene native Datei im Release: $($forbidden.FullName -join ', ')" }

$inventory = Get-Content -LiteralPath (Join-Path $base 'docs/licensing/inventory.json') -Raw | ConvertFrom-Json -AsHashtable
foreach ($manifest in Get-ChildItem -LiteralPath $base -Recurse -Filter '*.deps.json') {

    $deps = Get-Content -LiteralPath $manifest.FullName -Raw | ConvertFrom-Json -AsHashtable

    foreach ($key in $deps.libraries.Keys) {
        if ($deps.libraries[$key].type -in @('package','runtimepack')) {
            if (-not $inventory.packages.ContainsKey($key)) { throw "Abhängigkeit fehlt im Lizenzinventar: $key" }
            $package = $inventory.packages[$key]
            if ($package.license_effective -notin @('MIT','Apache-2.0','PostgreSQL')) { throw "Nicht freigegebene NuGet-Lizenz: $key / $($package.license_effective)" }
            foreach ($record in $package.metadata.license_files) {
                $notice = Join-Path $base $record.path
                if (-not (Test-Path -LiteralPath $notice) -or (Get-FileHash -LiteralPath $notice -Algorithm SHA256).Hash.ToLowerInvariant() -ne $record.sha256) { throw "Lizenztext fehlt oder wurde verändert: $($record.path)" }
            }
        }

        if ($key -like 'OPCFoundation.NetStandard.Opc.Ua.*/*' -and $key -notlike '*/1.5.378.182') { throw "Nicht geprüfter OPC-Stand: $key" }

        if ($key -match '^SQLitePCLRaw\.lib\.e_sqlite3/2\.1\.(10|11)$') { throw "Veraltete SQLite-Binärversion: $key" }

    }

}

$source = Join-Path $base 'toolchains/w64devkit/source/source.tar'

if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant() -ne '5f76bc3b87ccfe4c3eb3d6045614cc983419a955f78f65f8921c10bb1f90e654') { throw 'Toolchain-Quellen passen nicht zum geprüften Kit.' }

Write-Host 'Release-Lizenzprüfung bestanden: Hinweise, Quellarchiv und bereinigte Komponenten.'
