#requires -Version 7.0
# Download into a temporary file and publish to the cache only after verification.
function Get-VerifiedArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Sha256,
        [Parameter(Mandatory)][string[]]$Urls
    )
    if ((Test-Path -LiteralPath $Path) -and
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256) { return }

    $temporary = "$Path.$([guid]::NewGuid().ToString('N')).download"
    try {
        foreach ($url in $Urls) {
            try {
                Invoke-WebRequest -Uri $url -OutFile $temporary -TimeoutSec 120 -ErrorAction Stop
                $actual = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash
                if ($actual -ne $Sha256) { throw "SHA-256 erwartet $Sha256, erhalten $actual" }
                Move-Item -LiteralPath $temporary -Destination $Path -Force
                return
            } catch {
                Write-Warning "Archiv von $url nicht verwendbar: $($_.Exception.Message)"
            } finally {
                if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
            }
        }
        throw 'Kein Archiv mit der festgelegten SHA-256-Prüfsumme verfügbar. Es wird nichts entpackt oder ausgeführt.'
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force }
    }
}
