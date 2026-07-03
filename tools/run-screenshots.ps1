<#
.SYNOPSIS
    Run the LabPlot.Screenshots headless harness directly via its built exe.

.DESCRIPTION
    Mirrors tools/run-avalonia.ps1: build first (unless -SkipBuild), then launch
    the built exe directly instead of 'dotnet run', so there is no dotnet parent
    process wrapping the headless harness. Build uses -nodeReuse:false and
    UseSharedCompilation=false so MSBuild worker nodes and the Roslyn compiler
    server do not stay resident as leftover dotnet.exe processes after the
    build finishes (no extra 'dotnet build-server shutdown' step is needed
    because those flags already suppress node reuse).

.PARAMETER Only
    Prefix filter forwarded to the harness as --only (e.g. 'viewer/'). Only
    scenarios whose relative output path starts with this prefix run. When
    omitted, every scenario runs.

.PARAMETER SkipBuild
    Skip building; run the existing exe as-is.

.EXAMPLE
    .\tools\run-screenshots.ps1
    Build then run every scenario (portal + gpc + spectrum + dls + viewer).

.EXAMPLE
    .\tools\run-screenshots.ps1 -Only viewer/
    Build then run only the viewer/* scenarios.

.EXAMPLE
    .\tools\run-screenshots.ps1 -SkipBuild -Only gpc/
    Run gpc/* scenarios without rebuilding.
#>
[CmdletBinding()]
param(
    [string]$Only,
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'tools\LabPlot.Screenshots'
$exePath = Join-Path $repoRoot 'tools\LabPlot.Screenshots\bin\Debug\net10.0\LabPlot.Screenshots.exe'

# 1. Build (unless skipped).
#    -nodeReuse:false stops MSBuild worker nodes from staying resident.
#    UseSharedCompilation=false stops the Roslyn (VBCSCompiler) server
#    from staying resident. Together they prevent 'dotnet.exe' ghosts
#    after the build completes, same as tools/run-avalonia.ps1.
if (-not $SkipBuild) {
    Write-Host "Building LabPlot.Screenshots ..."
    & dotnet build $projectPath -nodeReuse:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
        return
    }
}

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found: $exePath"
    return
}

# 2. Launch the exe directly (not 'dotnet run') so there is exactly one
#    process and stdout/stderr stream straight to the current console.
$exeArgs = @()
if ($Only) {
    $exeArgs += '--only'
    $exeArgs += $Only
}

Write-Host "Launching $exePath $($exeArgs -join ' ')"
& $exePath @exeArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Harness exited with code $LASTEXITCODE"
}
