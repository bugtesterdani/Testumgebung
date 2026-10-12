#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot '../toolchains/python/pip-bootstrap'
$wheel = Join-Path $directory 'pip.whl'
$expected = '71138adf1f4ca900cdb7d289c21b7494329f2332b6d85f0e1c42108c0384ed3e'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
if (-not (Test-Path $wheel)) {
    Invoke-WebRequest 'https://files.pythonhosted.org/packages/f3/6e/1736e5b4ae2b778ef2f81c47d797de9f891d4d8acb047a24ca37a60294dd/pip-26.2.1-py3-none-any.whl' -OutFile $wheel
}
if ((Get-FileHash -LiteralPath $wheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'pip-Prüfsumme falsch.' }
Write-Host 'pip 26.2.1 für externe Python-Kopien bereit.'
