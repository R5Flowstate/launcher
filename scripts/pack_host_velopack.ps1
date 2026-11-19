# Local-only Velopack pack of the server tool (R5FlowstateServer.exe + r5f-host.exe). Does not publish.
# Version defaults to the Host.Gui csproj <Version>.
param(
    [string]$Version = "",
    [string]$OutputDir = ""
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

if ([string]::IsNullOrWhiteSpace($OutputDir)) {
    $OutputDir = Join-Path $RepoRoot "artifacts\host-velopack"
}

$publish = Join-Path $RepoRoot "artifacts\publish-host-win-x64"
$gui = Join-Path $RepoRoot "src\R5Flowstate.Host.Gui\R5Flowstate.Host.Gui.csproj"
$cli = Join-Path $RepoRoot "src\R5Flowstate.Host.Cli\R5Flowstate.Host.Cli.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& dotnet msbuild $gui -nologo -getProperty:Version).Trim()
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Error "Could not read Version from $gui"
}

if (Test-Path $publish) {
    Remove-Item -Recurse -Force $publish
}
foreach ($proj in @($gui, $cli)) {
    Write-Host "publish $proj -> $publish  version $Version"
    dotnet publish $proj -c Release -r win-x64 --self-contained true -o $publish -p:Version=$Version
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$mainExe = "R5FlowstateServer.exe"
foreach ($exe in @($mainExe, "r5f-host.exe")) {
    if (-not (Test-Path (Join-Path $publish $exe))) {
        Write-Error "published exe missing: $exe"
    }
}
Copy-Item -Force (Join-Path $RepoRoot "docs\HOST_TOOL.md") (Join-Path $publish "HOST_TOOL.md")

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

& $vpkExe pack `
    --packId R5FlowstateServer `
    --packVersion $Version `
    --packDir $publish `
    --mainExe $mainExe `
    --channel win `
    --packTitle "R5Flowstate Server" `
    --packAuthors "CafeFPS" `
    --icon (Join-Path $RepoRoot "src\R5Flowstate.Shell\Assets\app.ico") `
    --outputDir $OutputDir
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$setup = Join-Path $OutputDir "R5FlowstateServer-win-Setup.exe"
if (-not (Test-Path $setup)) {
    Write-Error "Velopack Setup missing: $setup"
}
Write-Host "packed -> $OutputDir"
Get-ChildItem $OutputDir | Format-Table Name, Length
