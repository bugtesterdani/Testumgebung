param()
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$bin = Join-Path $root 'toolchains/w64devkit/bin'
$originalPath = $env:PATH
$env:PATH = $bin + [IO.Path]::PathSeparator + $originalPath
$temp = Join-Path ([IO.Path]::GetTempPath()) ('Testumgebung-toolchain-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    $source = Join-Path $temp 'probe.cpp'
    $exe = Join-Path $temp 'probe.exe'
    @'
__attribute__((noinline)) int probe(int value) { return value + 1; }
int main() { return probe(41) == 42 ? 0 : 1; }
'@ | Set-Content -LiteralPath $source -Encoding ascii
    & (Join-Path $bin 'g++.exe') -g -O0 -static-libgcc -static-libstdc++ $source -o $exe
    if ($LASTEXITCODE -ne 0) { throw 'C++-Kompilierung fehlgeschlagen.' }
    & $exe
    if ($LASTEXITCODE -ne 0) { throw 'Kompiliertes Programm fehlgeschlagen.' }
    $output = & (Join-Path $bin 'gdb.exe') --batch -ex 'break probe' -ex run -ex 'print value' -ex continue $exe 2>&1
    if ($LASTEXITCODE -ne 0 -or ($output -join "`n") -notmatch '\$1 = 41') {
        throw ('GDB-Breakpoint/Variablenprüfung fehlgeschlagen: ' + ($output -join "`n"))
    }
    Write-Host 'OK: mitgelieferter GCC kompiliert; GDB hält am Breakpoint und liest value = 41.'
} finally {
    $env:PATH = $originalPath
    $resolved = [IO.Path]::GetFullPath($temp)
    if ($resolved.StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
