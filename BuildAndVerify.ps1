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
$candidate = Join-Path $PSScriptRoot 'dist/workshop-content'
$before = @{}
foreach ($name in @('AncientAffection.dll','AncientAffection.json','AncientAffection.pck')) {
    $before[$name] = (Get-FileHash -LiteralPath (Join-Path $OriginalModDir $name) -Algorithm SHA256).Hash
}
Push-Location $PSScriptRoot
try {
    dotnet build src/AncientAffectionCompat.csproj -c Release "-p:STS2GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw 'Companion build failed.' }
    dotnet build tools/PatchBuilder/PatchBuilder.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Patch builder build failed.' }
    dotnet tools/PatchBuilder/bin/Release/net9.0/PatchBuilder.dll $GameDir $OriginalModDir src/bin/Release/net9.0/AncientAffectionCompat.dll $candidate
    if ($LASTEXITCODE -ne 0) { throw 'Patch generation failed.' }
    dotnet build tools/LoaderVerification/LoaderVerification.csproj -c Release "-p:STS2GameDir=$GameDir"
    if ($LASTEXITCODE -ne 0) { throw 'Loader verification build failed.' }
    foreach ($scenario in @('ordering','normal','missing','disabled','duplicate','modified','wrong-game')) {
        dotnet tools/LoaderVerification/bin/Release/net9.0/LoaderVerification.dll $GameDir $candidate $OriginalModDir $scenario
        if ($LASTEXITCODE -ne 0) { throw "Loader verification failed: $scenario" }
    }
    dotnet run --project tools/Verification/Verification.csproj -c Release "-p:STS2GameDir=$GameDir" -- $GameDir $candidate $OriginalModDir
    if ($LASTEXITCODE -ne 0) { throw 'Verification failed. Do not publish this candidate.' }
    foreach ($name in $before.Keys) {
        if ((Get-FileHash -LiteralPath (Join-Path $OriginalModDir $name) -Algorithm SHA256).Hash -ne $before[$name]) {
            throw "Original file changed during verification: $name"
        }
    }
    Write-Host "Verified Workshop add-on: $candidate"
    Write-Host 'Original DLL, manifest and PCK unchanged. No original content, installers or backups are included.'
} finally {
    Pop-Location
}
