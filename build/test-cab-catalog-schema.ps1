$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$catalogPath = Join-Path $root 'docs/cab/jscript-befehlskatalog.json'
$schemaPath = Join-Path $root 'docs/cab/jscript-befehlskatalog.schema.json'
$json = Get-Content -LiteralPath $catalogPath -Raw
if (-not (Test-Json -Json $json -SchemaFile $schemaPath)) {
    throw 'Der cab-JScript-Katalog entspricht nicht dem Schema.'
}

# Negative cases ensure that the schema rejects malformed catalog metadata.
foreach ($mutation in 'missing-id', 'invalid-generation', 'invalid-source', 'invalid-page') {
    $catalog = $json | ConvertFrom-Json -AsHashtable
    switch ($mutation) {
        'missing-id' { $catalog.eintraege[0].Remove('id') }
        'invalid-generation' { $catalog.eintraege[0].generationen = @{ Ax='yes'; X2='y'; X3='y'; X4='y' } }
        'invalid-source' {
            $entry = $catalog.eintraege | Where-Object { $_.parameter.Count -gt 0 } | Select-Object -First 1
            $entry.parameter[0].quelle = 'unknown'
        }
        'invalid-page' { $catalog.eintraege[0].seiten[0] = 383 }
    }
    $invalid = $catalog | ConvertTo-Json -Depth 100 -Compress
    $accepted = Test-Json -Json $invalid -SchemaFile $schemaPath -ErrorAction SilentlyContinue
    if ($accepted) { throw "Das Schema akzeptiert den Fehlerfall $mutation." }
}
