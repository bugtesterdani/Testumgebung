#requires -Version 7.0
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root 'artifacts'
$output = Join-Path $artifacts "msix/$Version"
$stage = Join-Path $artifacts ('entra-broker-stage-' + [guid]::NewGuid().ToString('N'))
$project = Join-Path $root 'src/Testumgebung.EntraBroker/Testumgebung.EntraBroker.csproj'
$zip = Join-Path $output "Testumgebung-EntraBroker-$Version-win-x64.zip"

New-Item -ItemType Directory -Path $stage, $output -Force | Out-Null
try {
    dotnet test (Join-Path $root 'Testumgebung.EntraBroker.slnx') -c Release --nologo
    if ($LASTEXITCODE) { throw 'Entra-Broker-Tests fehlgeschlagen.' }
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $stage -p:Version=$Version -p:FileVersion=$Version --nologo
    if ($LASTEXITCODE) { throw 'Entra-Broker-Publish fehlgeschlagen.' }
    & (Join-Path $stage 'Testumgebung.EntraBroker.exe') --help
    if ($LASTEXITCODE) { throw 'Veröffentlichte Entra-Anwendung konnte nicht gestartet werden.' }

    Copy-Item (Join-Path $root 'LICENSE') $stage
    Copy-Item (Join-Path $root 'docs/entra-dienst.md') $stage
    $origins = @{
        '0427b9abbead3fa64c6655eb4290c537397131fa' = 'https://github.com/dotnet/dotnet/blob/0427b9abbead3fa64c6655eb4290c537397131fa/LICENSE.TXT'
        '44525024595742ebe09023abe709df51de65009b' = 'https://github.com/dotnet/dotnet/blob/44525024595742ebe09023abe709df51de65009b/LICENSE.TXT'
        '25d90ed3f48854036d444541a049089ccd198707' = 'https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/blob/8.19.2/LICENSE.txt'
    }
    $cache = (& dotnet nuget locals global-packages --list) -replace '^global-packages: ', ''
    if ($LASTEXITCODE -or -not (Test-Path -LiteralPath $cache -PathType Container)) { throw 'NuGet-Paketverzeichnis fehlt.' }
    $deps = Get-Content -LiteralPath (Join-Path $stage 'Testumgebung.EntraBroker.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    $inventory = foreach ($library in $deps.libraries.GetEnumerator()) {
        if ($library.Value.type -ne 'package') { continue }
        $folder = Join-Path $cache $library.Value.path
        $manifest = Get-ChildItem -LiteralPath $folder -Filter '*.nuspec' -File | Select-Object -First 1
        $metadata = ([xml](Get-Content -LiteralPath $manifest.FullName -Raw)).package.metadata
        $commit = [string]$metadata.repository.commit
        if ($metadata.license.type -ne 'expression' -or $metadata.license.'#text' -ne 'MIT' -or -not $origins.ContainsKey($commit)) {
            throw "Lizenzvorprüfung für $($library.Key) fehlt."
        }
        $notices = Join-Path $stage ('licenses/' + $library.Key)
        New-Item -ItemType Directory -Path $notices -Force | Out-Null
        Copy-Item (Join-Path $root "licenses/third-party/entra-broker/$commit.txt") (Join-Path $notices 'LICENSE.txt')
        Get-ChildItem -LiteralPath $folder -File |
            Where-Object Name -Match 'licen[cs]e|notice|copying' |
            Where-Object Name -NE 'LICENSE.txt' |
            Copy-Item -Destination $notices
        [ordered]@{ package = $library.Key; license = 'MIT'; source = $origins[$commit] }
    }
    @($inventory) | ConvertTo-Json -AsArray | Set-Content -LiteralPath (Join-Path $stage 'DEPENDENCIES.json') -Encoding utf8
    [IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
    $hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)" -Encoding ascii
    Write-Host "Fertig: $zip"
}
finally {
    $resolved = (Resolve-Path -LiteralPath $stage).Path
    $prefix = [IO.Path]::GetFullPath($artifacts) + [IO.Path]::DirectorySeparatorChar + 'entra-broker-stage-'
    if ($resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
