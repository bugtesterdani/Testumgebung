#requires -Version 7.0
param([string]$Report = 'artifacts/security/nuget-audit.json')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($Report, $root)
New-Item -ItemType Directory -Force ([IO.Path]::GetDirectoryName($output)) | Out-Null
Push-Location $root
try {
    # Includes transitive packages; do not equate a successful restore with a clean audit.
    $json = & dotnet list Testumgebung.sln package --vulnerable --include-transitive --format json
    if ($LASTEXITCODE) { throw 'NuGet-Sicherheitsprüfung fehlgeschlagen.' }
    Set-Content -LiteralPath $output -Value ($json -join "`n") -Encoding utf8
    $audit = ($json -join "`n") | ConvertFrom-Json
    if (-not $audit.projects -or @($audit.logs | Where-Object level -EQ 'error').Count) {
        throw 'NuGet-Audit liefert keine verlässliche Projektauswertung.'
    }
    $vulnerable = @($audit.projects.frameworks.topLevelPackages | Where-Object vulnerabilities)
    $vulnerable += @($audit.projects.frameworks.transitivePackages | Where-Object vulnerabilities)
    if ($vulnerable.Count) { throw "Bekannte verwundbare Paketauflösungen: $($vulnerable.Count). Bericht: $output" }
    # SDK-generated runtime downloads are not ordinary NuGet PackageReferences.
    $runtimeEntries = 0
    foreach ($folder in @('src', 'tests')) {
        foreach ($file in Get-ChildItem (Join-Path $root $folder) -Recurse -Filter project.assets.json) {
            $assets = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
            foreach ($framework in $assets.project.frameworks.Values) {
                foreach ($dependency in $framework.downloadDependencies) {
                    if ($dependency.name -match '^Microsoft\.(AspNetCore|NETCore|WindowsDesktop)\.App\.Runtime\.') {
                        $runtimeEntries++
                        if ($dependency.version -ne '[10.0.13, 10.0.13]') {
                            throw "Nicht freigegebener Runtime-Download: $($dependency.name) $($dependency.version)"
                        }
                    }
                }
            }
        }
    }
    if (-not $runtimeEntries) { throw 'Keine Runtime-Nachweise gefunden; zuerst die Solution wiederherstellen.' }
    Write-Host "Keine bekannten verwundbaren NuGet-Pakete; $runtimeEntries Runtime-Downloads auf 10.0.13 geprüft."
} finally { Pop-Location }
