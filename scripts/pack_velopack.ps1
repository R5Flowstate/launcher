# Local-only Velopack pack. Does not publish.
# Version defaults to the Shell csproj <Version> so the UI and nupkg match.
param(
    [string]$Version = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

function Find-Iscc([string]$root) {
    $candidates = @(
        (Join-Path $root "tools\innosetup\ISCC.exe"),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "${env:LOCALAPPDATA}\Programs\Inno Setup 6\ISCC.exe"
    )
    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) { return $c }
    }

    $dest = Join-Path $root "tools\innosetup"
    $boot = Join-Path $root "artifacts\innosetup-setup.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path $boot) | Out-Null
    Write-Host "downloading Inno Setup 6 compiler -> $dest"
    Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-6.7.3.exe" -OutFile $boot
    $p = Start-Process -FilePath $boot -ArgumentList "/VERYSILENT","/SUPPRESSMSGBOXES","/NORESTART","/DIR=`"$dest`"" -Wait -PassThru
    if ($p.ExitCode -ne 0) {
        Write-Error "Inno Setup installer failed (exit $($p.ExitCode))"
    }
    $iscc = Join-Path $dest "ISCC.exe"
    if (-not (Test-Path $iscc)) {
        Write-Error "ISCC.exe missing after Inno install: $iscc"
    }
    return $iscc
}

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "artifacts\velopack"
}

$publish = Join-Path $RepoRoot "artifacts\publish-win-x64"
$project = Join-Path $RepoRoot "src\R5Flowstate.Shell\R5Flowstate.Shell.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& dotnet msbuild $project -nologo -getProperty:Version).Trim()
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "Could not read Version from $project"
}

Write-Host "publish $project -> $publish  version $Version"
# R5FStageDir is machine-local overlay staging; pack must not overwrite it.
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -p:R5FStageDir= -p:Version=$Version -p:PublishReadyToRun=true
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$seven = Join-Path $RepoRoot "tools\7za\7za.exe"
if (Test-Path $seven) {
    Copy-Item -Force $seven (Join-Path $publish "7za.exe")
    Copy-Item -Force (Join-Path $RepoRoot "tools\7za\COPYING") (Join-Path $publish "COPYING")
    Copy-Item -Force (Join-Path $RepoRoot "tools\7za\7za-SOURCE.txt") (Join-Path $publish "7za-SOURCE.txt")
}

# Loadscreen rpaks are Oodle. The nupkg must carry the decoder next to the shell.
$oodleName = "oo2core_8_win64.dll"
$oodleDest = Join-Path $publish $oodleName
$oodleCandidates = @(
    (Join-Path $RepoRoot "tools\oo2core\$oodleName"),
    $env:OodleDll,
    (Join-Path $RepoRoot "src\R5Flowstate.Content\bin\Release\net8.0\$oodleName"),
    $oodleDest
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }
if ($oodleCandidates.Count -eq 0) {
    Write-Error "$oodleName missing -- drop a copy in tools\oo2core\ or set OodleDll. Pack will not ship without it."
}
Copy-Item -Force $oodleCandidates[0] $oodleDest
if (-not (Test-Path $oodleDest)) {
    Write-Error "failed to stage $oodleName into $publish"
}
Write-Host "staged $oodleName from $($oodleCandidates[0])"

# Python also ships a `vpk` (Valve Pak). Velopack's tool lives in the dotnet tools dir.
$vpkExe = Join-Path $env:USERPROFILE ".dotnet\tools\vpk.exe"
if (-not (Test-Path $vpkExe)) {
    Write-Host "installing Velopack vpk tool (user)"
    dotnet tool update -g vpk
}
if (-not (Test-Path $vpkExe)) {
    Write-Error "Velopack vpk not found at $vpkExe (PATH vpk may be the Python Valve-Pak CLI)"
}

if (Test-Path $OutputDir) {
    Remove-Item -Recurse -Force $OutputDir
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$mainExe = "R5FlowstateLauncher.exe"
if (-not (Test-Path (Join-Path $publish $mainExe))) {
    Write-Error "published exe missing: $mainExe"
}

$icon = Join-Path $RepoRoot "src\R5Flowstate.Shell\Assets\app.ico"

Write-Host "vpk pack --packId R5Flowstate --channel win --packVersion $Version"
& $vpkExe pack `
    --packId R5Flowstate `
    --packVersion $Version `
    --packDir $publish `
    --mainExe $mainExe `
    --channel win `
    --packTitle "R5Flowstate" `
    --packAuthors "CafeFPS" `
    --icon $icon `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$veloSetup = Join-Path $OutputDir "R5Flowstate-win-Setup.exe"
if (-not (Test-Path $veloSetup)) {
    Write-Error "Velopack Setup missing: $veloSetup"
}

$iscc = Find-Iscc $RepoRoot
$iss = Join-Path $RepoRoot "setup\R5Flowstate.iss"
$wizard = Join-Path $RepoRoot "artifacts\R5FlowstateSetup.exe"
Write-Host "iscc $iss  -> $wizard"
& $iscc /Q `
    "/DMyAppVersion=$Version" `
    "/DVeloSetup=$veloSetup" `
    $iss
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
if (-not (Test-Path $wizard)) {
    Write-Error "Inno wizard missing: $wizard"
}

Write-Host "packed -> $OutputDir"
Get-ChildItem $OutputDir | Format-Table Name, Length
Write-Host "player installer -> $wizard"
Get-Item $wizard | Format-Table Name, Length
