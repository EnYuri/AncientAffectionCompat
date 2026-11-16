param(
    [string]$GameDir = (Split-Path -Parent $PSScriptRoot),
    [string]$OriginalModDir = ''
)
$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path
if (-not $OriginalModDir) {
    $OriginalModDir = Join-Path $GameDir '../../workshop/content/2868840/3750930021'
}
$OriginalModDir = (Resolve-Path -LiteralPath $OriginalModDir).Path
$candidate = Join-Path $PSScriptRoot 'dist/candidate'
Push-Location $PSScriptRoot
try {
    dotnet build src/AncientAffectionCompat.csproj -c Release "-p:STS2GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw 'Companion build failed.' }
    dotnet build tools/PatchBuilder/PatchBuilder.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Patch builder build failed.' }
    dotnet tools/PatchBuilder/bin/Release/net9.0/PatchBuilder.dll $GameDir $OriginalModDir src/bin/Release/net9.0/AncientAffectionCompat.dll $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Patch generation failed.' }
    dotnet run --project tools/Verification/Verification.csproj -c Release "-p:STS2GameDir=$GameDir" -- $GameDir $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed. Do not install this candidate.' }
    $originalPck = (Get-FileHash -LiteralPath (Join-Path $OriginalModDir 'AncientAffection.pck') -Algorithm SHA256).Hash
    $patchedPck = (Get-FileHash -LiteralPath (Join-Path $candidate 'AncientAffection/AncientAffection.pck') -Algorithm SHA256).Hash
    if ($originalPck -ne $patchedPck) { throw 'Original asset/localization PCK was changed.' }
    if ((Get-FileHash -LiteralPath (Join-Path $OriginalModDir 'AncientAffection.dll') -Algorithm SHA256).Hash -ne '20FB2DCEEA8D83DB5596044EB09F9C3AA9B728B32A71C9FB112E4232561FC085') {
        throw 'Original DLL changed during verification.'
    }
    Write-Host "Verified candidate: $candidate"
    Write-Host 'All localization/assets are byte-identical to the original. Live mods were not changed.'
} finally {
    Pop-Location
}
