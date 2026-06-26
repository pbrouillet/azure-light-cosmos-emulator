<#
.SYNOPSIS
    Publishes the Azure Cosmos Light Emulator as a self-contained single-file executable.

.PARAMETER Runtime
    Target runtime identifier (e.g. win-x64, linux-x64, osx-x64, osx-arm64).
    Defaults to the current platform's RID.

.PARAMETER Configuration
    Build configuration. Default: Release.

.PARAMETER Output
    Output directory. Default: publish/<Runtime>.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -Runtime linux-x64
    .\publish.ps1 -Runtime osx-arm64
#>
[CmdletBinding()]
param(
    [string]$Runtime,
    [string]$Configuration = "Release",
    [string]$Output
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Auto-detect RID when not specified
if (-not $Runtime) {
    $Runtime = dotnet --info |
        Select-String '^\s*RID:\s+(.+)$' |
        ForEach-Object { $_.Matches[0].Groups[1].Value.Trim() }
    if (-not $Runtime) {
        Write-Error "Could not auto-detect runtime identifier. Specify -Runtime explicitly."
        exit 1
    }
    Write-Host "Detected runtime: $Runtime" -ForegroundColor Cyan
}

if (-not $Output) {
    $Output = Join-Path "publish" $Runtime
}

$project = Join-Path $PSScriptRoot "src" "Cli" "Azure.Cosmos.LightEmulator.Cli.csproj"

$publishArgs = @(
    "publish"
    $project
    "-c", $Configuration
    "-r", $Runtime
    "--self-contained"
    "-o", $Output
    # Produce a clean single-file output: no side-car .pdb next to the exe.
    "-p:DebugType=none"
    "-p:DebugSymbols=false"
)

Write-Host ""
Write-Host "Publishing single-file executable..." -ForegroundColor Cyan
Write-Host "  Project:       $project"
Write-Host "  Runtime:       $Runtime"
Write-Host "  Configuration: $Configuration"
Write-Host "  Output:        $Output"
Write-Host ""

dotnet @publishArgs

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

# Find the produced executable
$exeName = if ($Runtime -like "win-*") { "Azure.Cosmos.LightEmulator.Cli.exe" } else { "Azure.Cosmos.LightEmulator.Cli" }
$exePath = Join-Path $Output $exeName

if (Test-Path $exePath) {
    # Strip any side-car debug symbols (incl. native .pdb from packages like SurrealDB)
    # so the output directory is a clean single executable.
    Get-ChildItem -Path $Output -Filter *.pdb -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue

    $size = (Get-Item $exePath).Length
    $sizeMB = [math]::Round($size / 1MB, 1)
    Write-Host ""
    Write-Host "Published successfully:" -ForegroundColor Green
    Write-Host "  $exePath ($sizeMB MB)"

    # Report whether the Windows icon was embedded (win-* runtimes only).
    if ($Runtime -like "win-*") {
        $iconSource = Join-Path $PSScriptRoot "assets" "icon.ico"
        if (Test-Path $iconSource) {
            try {
                Add-Type -AssemblyName System.Drawing
                $ico = [System.Drawing.Icon]::ExtractAssociatedIcon($exePath)
                if ($ico) {
                    Write-Host "  Icon embedded: yes (from assets\icon.ico)" -ForegroundColor Green
                    $ico.Dispose()
                }
            } catch {
                Write-Host "  Icon embedded: could not verify ($($_.Exception.Message))" -ForegroundColor Yellow
            }
        } else {
            Write-Host "  Icon source not found: $iconSource" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host ""
    Write-Host "Published to: $Output" -ForegroundColor Green
}
