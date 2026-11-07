# Builds a self-contained, single-file GamingMode.exe (tray app + service in one) and, with -Installer, the MSI.
#   powershell -ExecutionPolicy Bypass -File build\publish.ps1 [-Version 1.0.0] [-Installer]
# Output: artifacts\publish\GamingMode.exe; with -Installer also artifacts\GamingMode-<version>-x64.msi
param([string]$Version = "1.0.0", [switch]$Installer)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$out = Join-Path $root "artifacts\publish"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet test "$root\tests\GamingMode.Tests" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Unit tests failed; not publishing." }

# Compression stays off: compressed bundles are unpacked into private memory (~+90 MB working set).
dotnet publish "$root\src\GamingMode.App" -c Release -r win-x64 --self-contained true -o $out `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded `
    -p:Version=$Version -p:ContinuousIntegrationBuild=true --nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }

if ($Installer) {
    Push-Location $root
    try {
        dotnet tool restore | Out-Null
        $msi = Join-Path $root "artifacts\GamingMode-$Version-x64.msi"
        dotnet tool run wix build "installer\GamingMode.wxs" -arch x64 -o $msi `
            -d "Version=$Version" -d "PublishDir=$out" -d "IconPath=$root\assets\GamingMode.ico"
        if ($LASTEXITCODE -ne 0) { throw "Installer build failed." }
        Get-Item $msi | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
    }
    finally { Pop-Location }
}

Get-ChildItem $out | Select-Object Name, @{ n = "MB"; e = { [math]::Round($_.Length / 1MB, 1) } }
Get-FileHash "$out\*.exe" -Algorithm SHA256 | Select-Object @{ n = "File"; e = { Split-Path $_.Path -Leaf } }, Hash
