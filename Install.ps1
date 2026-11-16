param([string]$GameDir = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (Get-Process -Name SlayTheSpire2 -ErrorAction SilentlyContinue) {
    throw 'Close Slay the Spire 2 before installing.'
}
# Rebuild and verify before copying, including the original DLL and unchanged PCK checks.
& (Join-Path $PSScriptRoot 'BuildAndVerify.ps1') -GameDir $GameDir
$modsDir = Join-Path $GameDir 'mods'
$backupDir = Join-Path $PSScriptRoot ('backups/' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $modsDir -Force | Out-Null
foreach ($modId in @('AncientAffection', 'AncientAffectionCompat')) {
    $sourceDir = Join-Path $PSScriptRoot "dist/candidate/$modId"
    $targetDir = Join-Path $modsDir $modId
    if (Test-Path -LiteralPath $targetDir) {
        New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
        Copy-Item -LiteralPath $targetDir -Destination $backupDir -Recurse
    }
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    foreach ($file in Get-ChildItem -LiteralPath $sourceDir -File) {
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $targetDir $file.Name) -Force
    }
}
Write-Host "Installed local copies in $modsDir. Workshop originals were not changed."
Write-Host "Existing local copies, if any, were backed up to $backupDir."
