#requires -Version 7.0
<#
.SYNOPSIS
Erstellt ein signiertes, self-contained x64-MSIX-Bundle (Release).
.EXAMPLE
pwsh -File build/msix.ps1 -Version 1.0.1.0
#>
param(
    [string]$Version = '1.0.0.0',
    [switch]$SkipTests,
    [string]$CertificatePath,
    [string]$CertificatePassword = $env:TESTUMGEBUNG_SIGN_PASSWORD,
    [string]$TimestampUrl,
    [string]$SdkBin
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'acquire-python.ps1')
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw 'Version muss vier Zahlen enthalten, z.B. 1.0.1.0.' }
$v = [version]$Version
if (@($v.Major, $v.Minor, $v.Build, $v.Revision | Where-Object { $_ -gt 65535 }).Count) { throw 'Versionsbestandteile dürfen höchstens 65535 sein.' }
if (-not $SdkBin) {
    $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $SdkBin = Get-ChildItem $sdkRoot -Directory | Where-Object { $_.Name -match '^10\.' } |
        Sort-Object { [version]$_.Name } -Descending | ForEach-Object { Join-Path $_.FullName 'x64' } |
        Where-Object { (Test-Path (Join-Path $_ 'makeappx.exe')) -and (Test-Path (Join-Path $_ 'signtool.exe')) } | Select-Object -First 1
}
if (-not $SdkBin) { throw 'Windows SDK mit MakeAppx und SignTool installieren oder -SdkBin angeben.' }
$makeappx = Join-Path $SdkBin 'makeappx.exe'
$signtool = Join-Path $SdkBin 'signtool.exe'
foreach ($file in @($makeappx, $signtool)) { if (-not (Test-Path $file)) { throw "Werkzeug fehlt: $file" } }

$out = Join-Path $root "artifacts\msix\$Version"
New-Item -ItemType Directory -Force $out | Out-Null
$bundle = Join-Path $out "Testumgebung_${Version}_x64.msixbundle"
if (Test-Path $bundle) { throw "Release existiert bereits: $bundle. Für ein neues Release die Version erhöhen." }

# Eigenständiges Testzertifikat als Datei, ohne Änderung eines Zertifikatspeichers.
if (-not $CertificatePath) {
    $signing = Join-Path $root 'artifacts\signing'
    New-Item -ItemType Directory -Force $signing | Out-Null
    $CertificatePath = Join-Path $signing 'Testumgebung-Development.pfx'
    $CertificatePassword = ''
    if (-not (Test-Path $CertificatePath)) {
        $rsa = [System.Security.Cryptography.RSA]::Create(3072)
        try {
            $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=Testumgebung Development', $rsa,
                [System.Security.Cryptography.HashAlgorithmName]::SHA256, [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
            $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new([System.Security.Cryptography.X509Certificates.X509KeyUsageFlags]::DigitalSignature, $true))
            $oids = [System.Security.Cryptography.OidCollection]::new()
            $null = $oids.Add([System.Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3'))
            $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($oids, $true))
            $cert = $request.CreateSelfSigned([DateTimeOffset]::Now.AddMinutes(-5), [DateTimeOffset]::Now.AddYears(2))
            [IO.File]::WriteAllBytes($CertificatePath, $cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Pfx, ''))
            $cert.Dispose()
        } finally { $rsa.Dispose() }
    }
}
$cert = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new(
    (Resolve-Path $CertificatePath).Path, $CertificatePassword,
    [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet)
if (-not $cert.HasPrivateKey -or $cert.NotAfter -le [DateTime]::Now) { throw 'Signierzertifikat benötigt einen gültigen privaten Schlüssel.' }

if (-not $SkipTests) {
    & dotnet test (Join-Path $root 'Testumgebung.sln') -c Release -m:1 -nr:false --nologo --verbosity quiet `
        --logger 'trx;LogFilePrefix=release' --results-directory (Join-Path $out 'tests')
    if ($LASTEXITCODE) { throw 'Release-Tests fehlgeschlagen.' }
}
& (Join-Path $PSScriptRoot 'publish.ps1') -Version $Version -Configuration Release -SelfContained -SkipTests -SkipChecksums
$layout = Join-Path $root 'artifacts\publish\Testumgebung'
# Jeder .NET-Worker bekommt seine eigene Laufzeit, unabhängig von installierten Frameworks.
foreach ($worker in @(@('CSharp','csharp'), @('Native','native'))) {
    & dotnet publish (Join-Path $root "src\Testumgebung.Workers.$($worker[0])\Testumgebung.Workers.$($worker[0]).csproj") `
        -c Release -r win-x64 --self-contained true -o (Join-Path $layout "workers\$($worker[1])") `
        -p:Version=$Version -p:FileVersion=$Version -p:InformationalVersion=$Version -m:1 -nr:false --nologo
    if ($LASTEXITCODE) { throw "Worker-Publish fehlgeschlagen: $($worker[0])" }
}
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force $assets | Out-Null
Copy-Item (Join-Path $root 'src\Testumgebung.Desktop\Assets\Msix\*.png') $assets -Force
$manifest = Get-Content (Join-Path $root 'installer\msix\AppxManifest.xml') -Raw
$manifest.Replace('__VERSION__',$Version).Replace('__PUBLISHER__',[Security.SecurityElement]::Escape($cert.Subject)) |
    Set-Content (Join-Path $layout 'AppxManifest.xml') -Encoding utf8
$packages = Join-Path $out 'packages'
New-Item -ItemType Directory -Force $packages | Out-Null
Write-Host 'MSIX-Paket erstellen (Protokoll: makeappx-pack.log)'
& $makeappx pack /o /d $layout /p (Join-Path $packages "Testumgebung_${Version}_x64.msix") *> (Join-Path $out 'makeappx-pack.log')
if ($LASTEXITCODE) { Get-Content (Join-Path $out 'makeappx-pack.log') -Tail 20; throw 'MSIX-Paketierung fehlgeschlagen.' }
Write-Host 'MSIX-Bundle erstellen'
& $makeappx bundle /o /d $packages /p $bundle /bv $Version *> (Join-Path $out 'makeappx-bundle.log')
if ($LASTEXITCODE) { Get-Content (Join-Path $out 'makeappx-bundle.log') -Tail 20; throw 'Bundle-Erstellung fehlgeschlagen.' }
$signArgs = @('sign','/fd','SHA256','/f',(Resolve-Path $CertificatePath).Path)
if ($CertificatePassword) { $signArgs += @('/p',$CertificatePassword) }
if ($TimestampUrl) { $signArgs += @('/tr',$TimestampUrl,'/td','SHA256') }
& $signtool @signArgs $bundle
if ($LASTEXITCODE) { throw 'Signierung fehlgeschlagen.' }
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$archive = [IO.Compression.ZipFile]::OpenRead($bundle)
try {
    $signature = $archive.GetEntry('AppxSignature.p7x')
    if (-not $signature) { throw 'Bundle enthält keine Signatur.' }
    $buffer = [IO.MemoryStream]::new()
    $stream = $signature.Open()
    try { $stream.CopyTo($buffer) } finally { $stream.Dispose() }
    $bytes = $buffer.ToArray()
    $buffer.Dispose()
    $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
    $cms.Decode([byte[]]$bytes[4..($bytes.Length - 1)])
    $cms.CheckSignature($true) # Kryptografische Prüfung; Zertifikatsvertrauen erfolgt am Zielrechner.
    if ($cms.SignerInfos[0].Certificate.Thumbprint -ne $cert.Thumbprint) { throw 'Unerwartetes Signierzertifikat.' }
} finally { $archive.Dispose() }
[IO.File]::WriteAllBytes((Join-Path $out 'Testumgebung-Signer.cer'),$cert.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
$cert.Dispose()
Copy-Item (Join-Path $root 'docs\msix-release.md') (Join-Path $out 'INSTALLATION.md')
Get-ChildItem $out -File | Where-Object Extension -in '.msixbundle','.cer' | ForEach-Object {
    "$( (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)"
} | Set-Content (Join-Path $out 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Fertig: $bundle"
