# Build R5Flowstate.Launcher.sln (Release). LOCAL testing only.
$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

$Sln = Join-Path $RepoRoot "R5Flowstate.Launcher.sln"
if (-not (Test-Path $Sln)) {
    Write-Error "Solution not found: $Sln"
}

Write-Host "dotnet restore $Sln"
dotnet restore $Sln
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "dotnet build $Sln -c Release --no-restore"
dotnet build $Sln -c Release --no-restore
exit $LASTEXITCODE
