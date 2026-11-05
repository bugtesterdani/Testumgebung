#requires -Version 7.0
# Test-only client: never copied into workers, MSIX or release download artifacts.
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = '1.15.0'
$sha256 = '98bb798dd6c70cc875b9bdacd600594351cf88f0a39a831a5055df061c871bb5'
$cache = Join-Path $root "artifacts/tools/svn-tests/$version"
$archive = Join-Path $cache 'package.zip'
$client = Join-Path $cache 'client'
New-Item -ItemType Directory -Force $cache | Out-Null
. (Join-Path $PSScriptRoot 'verified-archive.ps1')
Get-VerifiedArchive -Path $archive -Sha256 $sha256 -Urls @("https://www.visualsvn.com/files/Apache-Subversion-$version-x64.zip")
# Re-extract every time: a verified ZIP does not prove the cached executable is still intact.
Expand-Archive -LiteralPath $archive -DestinationPath $client -Force
$svn = Join-Path $client 'bin/svn.exe'
$admin = Join-Path $client 'bin/svnadmin.exe'
foreach ($exe in @($svn, $admin)) {
    $actual = & $exe --version --quiet
    if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $version) { throw "SVN-Testclient-Version stimmt nicht: $exe" }
}
Write-Host "SVN-Integrationstests: verifizierter Client $version bereit."
return $svn
