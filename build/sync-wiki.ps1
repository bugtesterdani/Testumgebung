#requires -Version 7.0
param()
$ErrorActionPreference='Stop'
if (-not $env:WIKI_TOKEN) {
    Write-Output '::warning::WIKI_TOKEN fehlt. Die Wiki-Seiten und das PDF sind als Build-Artefakt verfügbar. Einrichtung: docs/github.md.'
    exit 0
}
$root=Split-Path $PSScriptRoot -Parent
$checkout=Join-Path $root 'artifacts/wiki-checkout'
if (Test-Path $checkout) { throw 'Wiki-Checkout existiert bereits; frischen Workflow verwenden.' }
# Header only in process environment, never in URLs, command arguments or git config.
$encoded=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("x-access-token:$($env:WIKI_TOKEN)"))
Write-Output "::add-mask::$encoded"
$env:GIT_CONFIG_COUNT='1'
$env:GIT_CONFIG_KEY_0='http.https://github.com/.extraheader'
$env:GIT_CONFIG_VALUE_0="AUTHORIZATION: basic $encoded"
$env:GIT_TERMINAL_PROMPT='0'
try {
    & git clone "https://github.com/$($env:GITHUB_REPOSITORY).wiki.git" $checkout
    if ($LASTEXITCODE) { throw 'Wiki nicht erreichbar. Privates Wiki benötigt Pro/Team, eine erste Home-Seite und WIKI_TOKEN mit Repository-Schreibrechten.' }
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
    $encoded=$null
}
