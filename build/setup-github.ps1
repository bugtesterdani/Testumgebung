#requires -Version 7.0
<# Creates the requested private repository, uploads the existing signing identity
   as encrypted Actions secrets, then commits and pushes the project. No key output. #>
param(
    [string]$Repository='bugtesterdani/Testumgebung',
    [string]$GhPath,
    [string]$CertificatePath,
    [string]$CertificatePassword=$env:TESTUMGEBUNG_SIGN_PASSWORD
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if (-not $GhPath) { $GhPath=Join-Path $root 'artifacts/tools/github-cli/bin/gh.exe' }
if (-not (Test-Path $GhPath)) { $GhPath=(Get-Command gh -ErrorAction Stop).Source }
$GhPath=(Resolve-Path $GhPath).Path
if (-not $CertificatePath) { $CertificatePath=Join-Path $root 'artifacts/signing/Testumgebung-Development.pfx' }
if ($Repository -notmatch '^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$') { throw 'Ungültiger Repositoryname.' }
& $GhPath auth status
if ($LASTEXITCODE) { throw 'Zuerst gh auth login --hostname github.com --git-protocol https --web ausführen.' }
Push-Location $root
try {
    $repoJson=& $GhPath repo view $Repository --json visibility,url 2>$null
    if ($LASTEXITCODE) {
        & $GhPath repo create $Repository --private --description 'Windows-Testumgebung mit Prüfprogramm-Editor, parallelen Prüfplätzen und gebündelten Laufzeiten'
        if ($LASTEXITCODE) { throw 'Privates Repository konnte nicht erstellt werden.' }
        $repoJson=& $GhPath repo view $Repository --json visibility,url
        if ($LASTEXITCODE) { throw 'Repository konnte nicht geprüft werden.' }
    }
    $repo=$repoJson | ConvertFrom-Json
    if ($repo.visibility -ne 'PRIVATE') { throw 'Repository ist nicht privat. Upload abgebrochen.' }

    # Use the same certificate/key; re-export with a random password in memory.
    # Secret payloads are passed over stdin, never as command arguments or logs.
    $cert=[Security.Cryptography.X509Certificates.X509Certificate2]::new(
        (Resolve-Path $CertificatePath).Path,$CertificatePassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet -bor
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
    try {
        if (-not $cert.HasPrivateKey -or $cert.NotAfter -le [DateTime]::Now) { throw 'Signierzertifikat ungültig.' }
        $secretPassword=[Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(48))
        $bytes=$cert.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx,$secretPassword)
        function Set-ActionSecret([string]$Name,[string]$Value) {
            $psi=[Diagnostics.ProcessStartInfo]::new($GhPath)
            foreach ($arg in @('secret','set',$Name,'--repo',$Repository)) { $psi.ArgumentList.Add($arg) }
            $psi.UseShellExecute=$false
            $psi.RedirectStandardInput=$true
            $process=[Diagnostics.Process]::Start($psi)
            $process.StandardInput.Write($Value)
            $process.StandardInput.Close()
            $process.WaitForExit()
            if ($process.ExitCode) { throw "Actions-Secret $Name konnte nicht gespeichert werden." }
        }
        Set-ActionSecret 'SIGNING_PFX_PASSWORD' $secretPassword
        Set-ActionSecret 'SIGNING_PFX_BASE64' ([Convert]::ToBase64String($bytes))
        Write-Output "Signierzertifikat als Actions-Secrets gespeichert; Thumbprint: $($cert.Thumbprint)"
    } finally {
        $cert.Dispose()
        if ($bytes) { [Array]::Clear($bytes) }
        $secretPassword=$null
    }
    if (-not (Test-Path '.git')) { & git init -b main; if ($LASTEXITCODE) { throw 'Git-Initialisierung fehlgeschlagen.' } }
    $remote=& git remote get-url origin 2>$null
    if ($LASTEXITCODE -eq 0 -and $remote.TrimEnd('/') -notin @("https://github.com/$Repository.git","https://github.com/$Repository")) { throw "Anderes origin vorhanden: $remote" }
    if ($LASTEXITCODE) { & git remote add origin "https://github.com/$Repository.git"; if ($LASTEXITCODE) { throw 'Git-Remote konnte nicht gesetzt werden.' } }
    & git branch -M main
    if ($LASTEXITCODE) { throw 'Hauptbranch konnte nicht gesetzt werden.' }
    # Repository-local credential helper; existing global Git settings remain intact.
    & git config credential.helper ''
    & git config --add credential.helper ('!"'+$GhPath.Replace('\','/')+'" auth git-credential')
    & git add -- .
    if ($LASTEXITCODE) { throw 'Dateien konnten nicht vorgemerkt werden.' }
    $staged=@(& git diff --cached --name-only)
    if ($staged | Where-Object { $_ -match '(^|/)(artifacts|toolchains|tmp|output|bin|obj)/|\.(pfx|p12|key|pem)$|(^|/)\.env($|\.)' }) { throw 'Private oder generierte Datei in Git-Auswahl. Upload abgebrochen.' }
    if ($staged.Count) {
        & git -c user.name='bugtesterdani' -c user.email='39159547+bugtesterdani@users.noreply.github.com' commit -m 'Testumgebung: Quellcode, Dokumentation und automatisierte MSIX-Releases'
        if ($LASTEXITCODE) { throw 'Git-Commit fehlgeschlagen.' }
    }
    & git push -u origin main
    if ($LASTEXITCODE) { throw 'Git-Upload fehlgeschlagen.' }
    Write-Output "Privates Repository: $($repo.url)"
    Write-Output 'Wiki-Einrichtung und Releases: docs/github.md'
} finally { Pop-Location }
