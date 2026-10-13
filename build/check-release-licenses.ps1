#requires -Version 7.0

param([Parameter(Mandatory)][string]$Layout)

$ErrorActionPreference = 'Stop'

$base = (Resolve-Path -LiteralPath $Layout).Path

foreach ($file in @('docs/oeffentliche-veroeffentlichung.md','LICENSE','LICENSE-STATUS.md','THIRD-PARTY-NOTICES.md','THIRD-PARTY-TERMS.md','licenses/third-party/python/LICENSE.txt','toolchains/w64devkit/source/source.tar','toolchains/w64devkit/Dockerfile')) {

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
            $reviewedWindowsRuntime = $key -in @('runtimepack.Microsoft.NETCore.App.Runtime.win-x64/10.0.12','runtimepack.Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.12') -and $package.license_effective -eq 'MIT plus Microsoft .NET Library / Windows SDK terms; see per-file attribution'
            if (-not $reviewedWindowsRuntime -and $package.license_effective -notin @('MIT','Apache-2.0','PostgreSQL')) { throw "Nicht freigegebene NuGet-Lizenz: $key / $($package.license_effective)" }
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



$licenseFolder = Join-Path $base 'licenses/third-party/microsoft'
$pinned = @{
    'dotnet-library.html' = '0f69be2c8ac44c3980047664c3347ff22cba5842dc9d8b0a88a4f0914645a780'
    'dotnet-library.txt' = '412203c598ba1df3b0a82bb49dacc0f7c989a5011b7ced146cbf03467f9a9222'
    'windows-sdk.html' = '2dc27de28d82f4d0d2f471899c818a5e9787de21061774390204170d4207d77d'
    'windows-sdk.txt' = '574a1547d1a3954a24a25abbc5a585fc8fc214b905a67bd4dd214c2e328c5152'
    'dotnet-windows-mapping.md' = '3bdf7142d570f11780d1a5fcb6cbe47f5cf4a15bc634acf8db93fe3be8837119'
}
foreach ($name in $pinned.Keys) {
    $file = Join-Path $licenseFolder $name
    if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pinned[$name]) { throw "Microsoft-Originalnachweis fehlt oder wurde verändert: $name" }
}
foreach ($binary in $inventory.binaries) {
    $name = [IO.Path]::GetFileName($binary.path)
    if ($name -in @('coreclr.dll','PresentationNative_cor3.dll','wpfgfx_cor3.dll','vcruntime140_cor3.dll') -and $binary.license -ne 'Microsoft .NET Library License') { throw "Microsoft-Dateilizenz falsch: $($binary.path)" }
    if ($name -eq 'D3DCompiler_47_cor3.dll' -and $binary.license -ne 'Microsoft Windows SDK License') { throw "SDK-Dateilizenz falsch: $($binary.path)" }
}

Write-Host 'Technische Release-Lizenzprüfung bestanden: Hinweise, Microsoft-Dateizuordnung, Originalverträge und Quellen. Keine rechtliche Vertriebsfreigabe.'
