# Publishes a GitHub Release from this PC (same result as the Release workflow, for when Actions isn't available).
#   powershell -ExecutionPolicy Bypass -File build\release.ps1 -Version 1.2.0
# Needs: a clean, pushed main branch and the GitHub CLI signed in (gh auth login).
param([Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version)

$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "$env:ProgramFiles\GitHub CLI\gh.exe" }
$tag = "v$Version"

Push-Location $root
try {
    if (git status --porcelain) { throw "Commit your changes first - a release must match what's on GitHub." }
    git fetch origin --tags --quiet
    if ((git rev-parse HEAD) -ne (git rev-parse origin/main)) { throw "Push main first (git push)." }
    $existing = git tag --list $tag
    if ($existing -and (git rev-parse "$tag^{commit}") -ne (git rev-parse HEAD)) { throw "$tag already exists on a different commit." }

    & "$PSScriptRoot\publish.ps1" -Version $Version -Installer
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    $msi = Join-Path $root "artifacts\GamingMode-$Version-x64.msi"
    $hash = (Get-FileHash $msi -Algorithm SHA256).Hash.ToLower()
    "$hash  GamingMode-$Version-x64.msi" | Set-Content -NoNewline -Encoding ascii "$msi.sha256"

    if (-not $existing) { git tag $tag; git push origin $tag }
    & $gh release create $tag $msi "$msi.sha256" --title "Gaming Mode $tag" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "Creating the GitHub release failed." }
    Write-Host "Released $tag - installed copies will pick it up within 12 hours (or via Check for updates)." -ForegroundColor Green
}
finally { Pop-Location }
