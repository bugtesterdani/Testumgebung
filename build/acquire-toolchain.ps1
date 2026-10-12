<# Lädt die fest versionierte portable GCC/GDB-Toolchain samt korrespondierendem Quellarchiv. #>
param()
$ErrorActionPreference = 'Stop'
$toolRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../toolchains'))
$destination = Join-Path $toolRoot 'w64devkit'
if (Test-Path (Join-Path $destination 'toolchain.json')) {
    Write-Host 'Toolchain bereits vorhanden.'
    exit 0
}
if (Test-Path $destination) { throw 'Unvollständige Toolchain vorhanden; bitte prüfen, nicht automatisch überschreiben.' }
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
$stage = Join-Path $toolRoot ('.stage-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage | Out-Null
$url = 'https://github.com/skeeto/w64devkit/releases/download/v2.8.0/'
$archive = Join-Path $stage 'w64devkit-x64-2.8.0.7z.exe'
Invoke-WebRequest -Uri ($url + 'w64devkit-x64-2.8.0.7z.exe') -OutFile $archive
$expected = '6252bf34fe2231a55ac7f03d482b36d2c7c58697990551bba508102cfb3f342e'
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Toolchain-Prüfsumme falsch.' }
$extract = Join-Path $stage 'extracted'
$process = Start-Process -FilePath $archive -ArgumentList @('-y', ('-o"' + $extract + '"')) -WindowStyle Hidden -Wait -PassThru
if ($process.ExitCode -ne 0) { throw "Entpacken fehlgeschlagen: $($process.ExitCode)" }
$sourceDir = [IO.Path]::GetFullPath((Join-Path $extract 'w64devkit'))
$destination = [IO.Path]::GetFullPath($destination)
if (-not $sourceDir.StartsWith($toolRoot + [IO.Path]::DirectorySeparatorChar) -or
    -not $destination.StartsWith($toolRoot + [IO.Path]::DirectorySeparatorChar)) { throw 'Toolchain-Pfad außerhalb des Projekts.' }
if (-not (Test-Path (Join-Path $sourceDir 'bin/g++.exe')) -or -not (Test-Path (Join-Path $sourceDir 'bin/gdb.exe'))) { throw 'Compiler/Debugger fehlen.' }
# Korrespondierende Quellen mitliefern; Toolchain-Lizenztexte im Original erhalten.
New-Item -ItemType Directory -Path (Join-Path $sourceDir 'source') | Out-Null
$sourceArchive = Join-Path $sourceDir 'source/source.tar'
Invoke-WebRequest -Uri ($url + 'source.tar') -OutFile $sourceArchive
$sourceHash = '5f76bc3b87ccfe4c3eb3d6045614cc983419a955f78f65f8921c10bb1f90e654'
if ((Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $sourceHash) { throw 'Quellarchiv-Prüfsumme falsch.' }
Move-Item -LiteralPath $sourceDir -Destination $destination
$compiler = (& (Join-Path $destination 'bin/g++.exe') --version | Select-Object -First 1)
$debugger = (& (Join-Path $destination 'bin/gdb.exe') --version | Select-Object -First 1)
@{ distribution='w64devkit'; version='2.8.0'; architecture='x64'; compiler=$compiler; debugger=$debugger;
   binarySha256=$expected; sourceSha256=$sourceHash; sourceUrl=($url+'source.tar') } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination 'toolchain.json') -Encoding utf8
Write-Host "$compiler / $debugger"
# Der heruntergeladene Extraktor bleibt als überprüfbarer Cache im Stagingordner; keine globale Installation/PATH-Änderung.
