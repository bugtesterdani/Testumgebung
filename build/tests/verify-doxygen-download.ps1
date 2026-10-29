#requires -Version 7.0
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../verified-archive.ps1')
$folder = Join-Path ([IO.Path]::GetTempPath()) ('doxygen-download-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
$archive = Join-Path $folder 'package.zip'
$valid = [Text.Encoding]::UTF8.GetBytes('verified fixture')
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($valid))
$script:calls = 0
$script:mode = 'fallback'
function Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, $ErrorAction)
    $script:calls++
    if ($script:mode -eq 'network' -and $Uri -eq 'primary') { throw 'Network unavailable' }
    $bytes = if ($script:mode -ne 'reject' -and $Uri -eq 'secondary') { $valid } else { [byte[]](1,2,3) }
    [IO.File]::WriteAllBytes($OutFile, $bytes)
}
try {
    # A corrupt cache and corrupt first source must not bypass the pinned hash.
    [IO.File]::WriteAllBytes($archive, [byte[]](9,8,7))
    Get-VerifiedArchive -Path $archive -Sha256 $hash -Urls @('primary','secondary')
    if ($script:calls -ne 2 -or (Get-FileHash $archive).Hash -ne $hash) { throw 'Fallback verification failed.' }
    Get-VerifiedArchive -Path $archive -Sha256 $hash -Urls @('primary','secondary')
    if ($script:calls -ne 2) { throw 'Verified cache was downloaded again.' }
    Remove-Item -LiteralPath $archive
    $script:mode = 'network'
    Get-VerifiedArchive -Path $archive -Sha256 $hash -Urls @('primary','secondary')
    if ((Get-FileHash $archive).Hash -ne $hash) { throw 'Network fallback failed.' }
    Remove-Item -LiteralPath $archive
    $script:mode = 'reject'
    $rejected = $false
    try { Get-VerifiedArchive -Path $archive -Sha256 $hash -Urls @('primary','secondary') }
    catch { if ($_.Exception.Message -notlike 'Kein Archiv*') { throw }; $rejected = $true }
    if (-not $rejected -or (Test-Path $archive) -or (Get-ChildItem $folder).Count -ne 0) { throw 'Unverified download retained or accepted.' }
    Write-Host 'Doxygen download: verified cache, corrupt download, network fallback, rejection and cleanup passed.'
} finally {
    Remove-Item Function:Invoke-WebRequest
    Get-ChildItem -LiteralPath $folder -File | Remove-Item -Force
    Remove-Item -LiteralPath $folder -Force
}
