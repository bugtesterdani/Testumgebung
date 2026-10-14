# Exportiert den echten Katalog oder prüft ein Projekt ohne dessen Code auszuführen.
param([string]$CheckProject)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'artifacts/tools/project-format-reference'
New-Item -ItemType Directory -Path $tool -Force | Out-Null
$reference = [Security.SecurityElement]::Escape((Join-Path $root 'src/Testumgebung.Persistence/Testumgebung.Persistence.csproj'))
@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="$reference" /></ItemGroup>
</Project>
"@ | Set-Content (Join-Path $tool 'ProjectFormatReference.csproj') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'project-format-reference.cs') -Destination (Join-Path $tool 'Program.cs') -Force
if ($CheckProject) {
    $mode = 'check'
    $target = [IO.Path]::GetFullPath($CheckProject)
} else {
    $mode = 'export'
    $target = Join-Path $root 'docs/ki/bausteinkatalog.json'
}
& dotnet run --project (Join-Path $tool 'ProjectFormatReference.csproj') -c Release -- $mode $target
if ($LASTEXITCODE) { throw 'Formatreferenz konnte nicht exportiert beziehungsweise Projekt konnte nicht validiert werden.' }
