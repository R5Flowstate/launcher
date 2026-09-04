# Run R5Flowstate.Shell (Release). LOCAL testing only.
# Usage:
#   .\scripts\dev_run.ps1
#   .\scripts\dev_run.ps1 -InstallRoot "<your-game-install>"
#   .\scripts\dev_run.ps1 --install-root "<your-game-install>"
param(
    [Parameter(Mandatory = $false)]
    [string]$InstallRoot = "",

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest = @()
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $RepoRoot

# Accept GNU-style --install-root <path> from remaining args.
for ($i = 0; $i -lt $Rest.Count; $i++) {
    $tok = $Rest[$i]
    if ($tok -eq "--install-root" -or $tok -eq "-install-root") {
        if ($i + 1 -ge $Rest.Count) {
            Write-Error "Missing value after $tok"
        }
        $InstallRoot = $Rest[$i + 1]
        $i++
        continue
    }
    if ($tok.StartsWith("--install-root=")) {
        $InstallRoot = $tok.Substring("--install-root=".Length)
        continue
    }
}

$Project = Join-Path $RepoRoot "src\R5Flowstate.Shell\R5Flowstate.Shell.csproj"
if (-not (Test-Path $Project)) {
    Write-Error "Shell project not found: $Project"
}

$forward = @()
if (-not [string]::IsNullOrWhiteSpace($InstallRoot)) {
    $forward += @("--install-root", $InstallRoot)
}

Write-Host "dotnet run --project $Project -c Release --no-build -- $($forward -join ' ')"
# Prefer already-built Release; fall back to build-on-run if missing.
# AssemblyName is R5Flowstate (not R5Flowstate.Shell).
$exe = Join-Path $RepoRoot "src\R5Flowstate.Shell\bin\Release\net8.0-windows\R5Flowstate.exe"
if (Test-Path $exe) {
    if ($forward.Count -gt 0) {
        & $exe @forward
    } else {
        & $exe
    }
    exit $LASTEXITCODE
}

if ($forward.Count -gt 0) {
    dotnet run --project $Project -c Release -- @forward
} else {
    dotnet run --project $Project -c Release
}
exit $LASTEXITCODE
