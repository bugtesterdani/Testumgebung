#requires -Version 7.0
param()
$ErrorActionPreference = 'Stop'
$directory = Join-Path $PSScriptRoot '../toolchains/python/pip-bootstrap'
$wheel = Join-Path $directory 'pip.whl'
$expected = 'f3b60b20ffaadd4415ed7957212a38ac63fa4c5fa375bd0f4a797777940fad13'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
if (-not (Test-Path $wheel)) {
    Invoke-WebRequest 'https://files.pythonhosted.org/packages/07/25/ee8c38ae72d66b844ce8eb9711ba905dba21ca1a8a0c960057c41cd23337/pip-26.3-py3-none-any.whl' -OutFile $wheel
}
if ((Get-FileHash -LiteralPath $wheel -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'pip-Prüfsumme falsch.' }
Write-Host 'pip 26.3 für externe Python-Kopien bereit.'
