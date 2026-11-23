<#
  Construiește PLAYZEK-ANTICHEAT-Setup.msi.
    1. publică aplicația desktop și serviciul (self-contained, win-x64 — jucătorul nu trebuie să instaleze .NET);
    2. compilează MSI-ul cu WiX 6 (NuGet, fără instalare separată).
  Rezultat: artifacts\installer\PLAYZEK-ANTICHEAT-Setup.msi
  Semnare (producție): signtool sign /fd SHA256 /tr <timestamp-url> /td SHA256 /f cert.pfx <msi și exe-uri>
#>
param([string]$Version = "0.1.0", [string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $root "artifacts\publish"
Remove-Item -Recurse -Force $publish -ErrorAction SilentlyContinue

$common = @("-c", $Configuration, "-r", "win-x64", "--self-contained", "true", "-p:Version=$Version", "-nologo", "-v", "q")
dotnet publish (Join-Path $root "client\desktop-app\Playzek.Desktop") @common -o (Join-Path $publish "app")
if ($LASTEXITCODE -ne 0) { throw "Publicarea aplicației desktop a eșuat." }
dotnet publish (Join-Path $root "client\service\Playzek.ProtectionService") @common -o (Join-Path $publish "service")
if ($LASTEXITCODE -ne 0) { throw "Publicarea serviciului a eșuat." }

dotnet build (Join-Path $root "client\installer\Playzek.Installer.wixproj") -c $Configuration -p:ProductVersion=$Version `
  -o (Join-Path $root "artifacts\installer") -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Compilarea MSI a eșuat." }

$msi = Get-Item (Join-Path $root "artifacts\installer\PLAYZEK-ANTICHEAT-Setup.msi")
"MSI: $($msi.FullName) ($([math]::Round($msi.Length / 1MB, 1)) MB)"
(Get-FileHash $msi.FullName -Algorithm SHA256).Hash
