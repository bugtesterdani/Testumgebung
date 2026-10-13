#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
if (-not $env:WIKI_TOKEN -and -not $env:WIKI_SSH_KEY) {
    Write-Output '::warning::Wiki-Zugang fehlt (WIKI_TOKEN oder WIKI_SSH_KEY). Die Wiki-Seiten und das PDF sind als Build-Artefakt verfügbar. Einrichtung: docs/github.md.'
    exit 0
}
$root=Split-Path $PSScriptRoot -Parent
$checkout=Join-Path $root 'artifacts/wiki-checkout'
if (Test-Path $checkout) { throw 'Wiki-Checkout existiert bereits; frischen Workflow verwenden.' }
$oldSshCommand=$env:GIT_SSH_COMMAND
$temporaryFiles=@()
$temporaryDirectory=$null
$wikiUrl="https://github.com/$($env:GITHUB_REPOSITORY).wiki.git"
$env:GIT_TERMINAL_PROMPT='0'
try {
    if ($env:WIKI_SSH_KEY) {
        $tempRoot=if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
        $temporaryDirectory=Join-Path $tempRoot ('testumgebung-wiki-'+[guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $temporaryDirectory | Out-Null
        $keyFile=Join-Path $temporaryDirectory 'wiki.key'
        $knownHosts=Join-Path $temporaryDirectory 'known_hosts'
        $temporaryFiles=@($keyFile,$knownHosts)
        [IO.File]::WriteAllText($keyFile,$env:WIKI_SSH_KEY.Replace("`r`n","`n").TrimEnd()+"`n",[Text.UTF8Encoding]::new($false))
        if ($IsWindows) {
            $identity=[Security.Principal.WindowsIdentity]::GetCurrent().Name
            & icacls $keyFile /inheritance:r /grant:r "${identity}:(F)" | Out-Null
            if ($LASTEXITCODE) { throw 'Temporärer Wiki-Schlüssel konnte nicht geschützt werden.' }
        } else {
            [IO.File]::SetUnixFileMode($keyFile,[IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite)
        }
        # Verify host keys using GitHub HTTPS metadata rather than ssh-keyscan.
        $meta=Invoke-RestMethod -Uri 'https://api.github.com/meta'
        [IO.File]::WriteAllLines($knownHosts,@($meta.ssh_keys | ForEach-Object { 'github.com '+$_ }),[Text.Encoding]::ASCII)
        $env:GIT_SSH_COMMAND='ssh -F none -i "'+$keyFile.Replace('\','/')+'" -o IdentitiesOnly=yes -o StrictHostKeyChecking=yes -o UserKnownHostsFile="'+$knownHosts.Replace('\','/')+'"'
        $wikiUrl="git@github.com:$($env:GITHUB_REPOSITORY).wiki.git"
    } else {
        # Header only in process environment, never in URLs or persistent config.
        $encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$($env:WIKI_TOKEN)"))
        Write-Output "::add-mask::$encoded"
        $env:GIT_CONFIG_COUNT='1'
        $env:GIT_CONFIG_KEY_0='http.https://github.com/.extraheader'
        $env:GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $encoded"
    }
    & git clone $wikiUrl $checkout
    if ($LASTEXITCODE) { throw 'Wiki nicht erreichbar. Privates Wiki benötigt Pro/Team, eine erste Home-Seite und einen passenden Wiki-Zugang.' }
    Get-ChildItem (Join-Path $root 'artifacts/wiki') -Filter '*.md' | Copy-Item -Destination $checkout -Force
    Push-Location $checkout
    try {
        & git config user.name 'github-actions[bot]'
        & git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
        & git add -- '*.md'
        & git diff --cached --quiet
        if ($LASTEXITCODE -eq 0) { Write-Output 'Wiki bereits aktuell.'; return }
        if ($LASTEXITCODE -ne 1) { throw 'Wiki-Differenz konnte nicht ermittelt werden.' }
        & git commit -m "Dokumentation aus $env:GITHUB_SHA aktualisieren"
        if ($LASTEXITCODE) { throw 'Wiki-Commit fehlgeschlagen.' }
        & git push origin HEAD
        if ($LASTEXITCODE) { throw 'Wiki-Upload fehlgeschlagen.' }
    } finally { Pop-Location }
} finally {
    Remove-Item Env:GIT_CONFIG_COUNT,Env:GIT_CONFIG_KEY_0,Env:GIT_CONFIG_VALUE_0 -ErrorAction SilentlyContinue
    $env:GIT_SSH_COMMAND=$oldSshCommand
    foreach ($file in $temporaryFiles) {
        if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    }
    if ($temporaryDirectory -and (Test-Path -LiteralPath $temporaryDirectory)) {
        Remove-Item -LiteralPath $temporaryDirectory -Force
    }
    $encoded=$null
}
