<#
.SYNOPSIS
  Builds artifacts\ExtormSub-Setup-<version>.exe: tests, self-contained publish, Inno Setup.

  Also writes artifacts\latest.json (the update manifest). Upload it next to the installer, at the
  UpdateManifestUrl set in src\ExtormSub.App\ExtormSub.App.csproj.

  Code signing (so SmartScreen shows a publisher): pass a certificate from your certificate store
  (-CertThumbprint; also works with USB tokens and EV certificates) or a .pfx file (-CertFile, -CertPassword).
  The same values can come from the EXTORMSUB_CERT_THUMBPRINT / EXTORMSUB_CERT_FILE / EXTORMSUB_CERT_PASSWORD variables.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 0.2.0
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File build\build-installer.ps1 -Version 0.3.0 -CertThumbprint 0123ABCD...
#>
param(
    [string]$Version = "0.2.0",
    [switch]$SkipTests,
    [string]$CertThumbprint = $env:EXTORMSUB_CERT_THUMBPRINT,
    [string]$CertFile = $env:EXTORMSUB_CERT_FILE,
    [string]$CertPassword = $env:EXTORMSUB_CERT_PASSWORD,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)
$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot -Parent
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "publish"

# dotnet: PATH first, then the per-user SDK location.
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$userDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not $dotnet -or -not (& $dotnet --list-sdks)) { $dotnet = $userDotnet }
if (-not (Test-Path $dotnet)) { throw "The .NET 8 SDK was not found. Install it from https://dot.net." }

if (-not $SkipTests) {
    Write-Host "== Tests" -ForegroundColor Cyan
    & $dotnet test (Join-Path $root "tests\ExtormSub.Tests") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Tests failed." }
}

Write-Host "== Publish (self-contained win-x64)" -ForegroundColor Cyan
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
# --artifacts-path keeps publish intermediates away from bin\ and obj\ so a running dev build is never touched.
& $dotnet publish (Join-Path $root "src\ExtormSub.App") -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -p:DebugType=None --artifacts-path (Join-Path $artifacts "build") -o $publish
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

# Native runtimes for other platforms come along with the Whisper.net packages; x64 Windows needs none of them.
foreach ($dir in "runtimes\win-arm64", "runtimes\win-x86", "runtimes\vulkan\linux-x64", "runtimes\linux-x64", "runtimes\osx-x64", "runtimes\osx-arm64") {
    $p = Join-Path $publish $dir
    if (Test-Path $p) { Remove-Item $p -Recurse -Force }
}
Get-ChildItem $publish -Filter *.lib -Recurse | Remove-Item -Force
$size = (Get-ChildItem $publish -Recurse | Measure-Object Length -Sum).Sum / 1MB
Write-Host ("   {0:N0} MB published" -f $size)

# Code signing: signtool from the Windows SDK, else the pinned Microsoft.Windows.SDK.BuildTools NuGet package.
$certArgs = @()
if ($CertThumbprint) { $certArgs = @("/sha1", ($CertThumbprint -replace '\s', '')) }
elseif ($CertFile) {
    $certArgs = @("/f", (Resolve-Path $CertFile).Path)
    if ($CertPassword) { $certArgs += @("/p", $CertPassword) }
}
$signtool = $null
if ($certArgs) {
    $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
    if (-not $signtool) {
        $tools = Join-Path $artifacts "tools\sdk-buildtools"
        $signtool = Join-Path $tools "bin\10.0.28000.0\x64\signtool.exe"
        if (-not (Test-Path $signtool)) {
            Write-Host "== Downloading signtool (Microsoft.Windows.SDK.BuildTools 10.0.28000.2705)" -ForegroundColor Cyan
            $pkg = Join-Path $artifacts "tools\sdk-buildtools.10.0.28000.2705.zip"
            New-Item -ItemType Directory -Force (Split-Path $pkg) | Out-Null
            Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/microsoft.windows.sdk.buildtools/10.0.28000.2705/microsoft.windows.sdk.buildtools.10.0.28000.2705.nupkg" -OutFile $pkg -UseBasicParsing
            $hash = (Get-FileHash $pkg -Algorithm SHA256).Hash
            if ($hash -ne "8BFDFB6CA2633F531CF80B5FA22512BA61A394D7988F0970DB83BAADC67929ED") { throw "SDK build tools package checksum mismatch ($hash)." }
            Expand-Archive $pkg $tools -Force
        }
    }
    $signArgs = @("sign", "/fd", "sha256", "/tr", $TimestampUrl, "/td", "sha256") + $certArgs
    Write-Host "== Signing ExtormSub binaries" -ForegroundColor Cyan
    $own = "ExtormSub.exe", "ExtormSub.dll", "ExtormSub.Core.dll", "ExtormSub.Infrastructure.dll" | ForEach-Object { Join-Path $publish $_ }
    & $signtool @signArgs @own
    if ($LASTEXITCODE -ne 0) { throw "Signing failed." }
    # Inno Setup signs Setup and its uninstaller with the same command ($q = quote, $$ = $, $f = the file).
    $innoSign = ((@($signtool) + $signArgs | ForEach-Object {
        $a = $_ -replace '\$', '$$$$'
        if ($a -match '\s') { "`$q$a`$q" } else { $a }
    }) -join ' ') + ' $f'
}
else {
    Write-Warning "Not signing: no certificate given. Windows SmartScreen will warn about this installer (see -CertThumbprint / -CertFile)."
}

# Inno Setup compiler: installed copy, else the pinned Tools.InnoSetup NuGet package (no admin needed).
$iscc = @(
    (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) {
    $tools = Join-Path $artifacts "tools\innosetup"
    $iscc = Join-Path $tools "tools\ISCC.exe"
    if (-not (Test-Path $iscc)) {
        Write-Host "== Downloading Inno Setup compiler (Tools.InnoSetup 6.7.3)" -ForegroundColor Cyan
        $pkg = Join-Path $artifacts "tools\innosetup.6.7.3.zip"
        New-Item -ItemType Directory -Force (Split-Path $pkg) | Out-Null
        Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/tools.innosetup/6.7.3/tools.innosetup.6.7.3.nupkg" -OutFile $pkg -UseBasicParsing
        $hash = (Get-FileHash $pkg -Algorithm SHA256).Hash
        if ($hash -ne "F780898E402FF80612CC8D9FCB8C6E02932BD1CB4C900FFDAA31F9341CFB49F4") { throw "Inno Setup package checksum mismatch ($hash)." }
        Expand-Archive $pkg $tools -Force
    }
}

Write-Host "== Installer" -ForegroundColor Cyan
$isccArgs = @("/DAppVersion=$Version", "/DPublishDir=$publish")
if ($signtool) { $isccArgs += @("/DSign", "/Sextormsub=$innoSign") }
& $iscc @isccArgs (Join-Path $root "installer\ExtormSub.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }
$setup = Join-Path $artifacts "ExtormSub-Setup-$Version.exe"
if ($signtool) { Write-Host ("   Signature: {0}" -f (Get-AuthenticodeSignature $setup).Status) }

# Update manifest; the installer URL is relative, so latest.json and the installer go in the same place.
$manifest = [ordered]@{
    version = $Version
    url     = Split-Path $setup -Leaf
    sha256  = (Get-FileHash $setup -Algorithm SHA256).Hash
    size    = (Get-Item $setup).Length
} | ConvertTo-Json
[IO.File]::WriteAllText((Join-Path $artifacts "latest.json"), $manifest) # UTF-8 without BOM
Write-Host ("== Done: {0} ({1:N0} MB) + latest.json" -f $setup, ((Get-Item $setup).Length / 1MB)) -ForegroundColor Green
