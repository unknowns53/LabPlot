<#
.SYNOPSIS
    Launch LabPlot.Avalonia directly via its built exe (avoids 'dotnet run'
    parent/child process leaks).

.DESCRIPTION
    'dotnet run' spawns LabPlot.Avalonia as a child of the host dotnet
    process. On Windows, Ctrl+C and terminal close do not always reach the
    child, so the child can stay alive as an orphan and accumulate.

    This script kills any existing LabPlot.Avalonia processes, builds the
    project (unless -NoBuild is given), then launches the exe directly so
    the process tree is exactly one process named "LabPlot.Avalonia".

      - Ctrl+C terminates the app reliably.
      - Stop-Process -Name 'LabPlot.Avalonia' can clean up leftovers.
      - Console.WriteLine output stays visible in the terminal.

    Build is invoked with -nodeReuse:false and UseSharedCompilation=false
    so that MSBuild worker nodes and the Roslyn (VBCSCompiler) server do
    not stay resident as 'dotnet.exe' ghosts after the build finishes.

    -KillOnly additionally runs 'dotnet build-server shutdown' to evict
    any already-resident MSBuild / Roslyn servers from previous builds.

.PARAMETER Configuration
    'Debug' (default) or 'Release'.

.PARAMETER KillOnly
    Stop existing LabPlot.Avalonia processes and exit without launching.

.PARAMETER NoBuild
    Skip building; launch the existing exe.

.EXAMPLE
    .\tools\run-avalonia.ps1
    Build Debug and launch.

.EXAMPLE
    .\tools\run-avalonia.ps1 -KillOnly
    Just clean up leftover processes.

.EXAMPLE
    .\tools\run-avalonia.ps1 -NoBuild
    Launch the existing exe without rebuilding.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [switch]$KillOnly,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\LabPlot.Shell.Avalonia\LabPlot.Shell.Avalonia.csproj'
$exePath = Join-Path $repoRoot "src\LabPlot.Shell.Avalonia\bin\$Configuration\net10.0\LabPlot.Avalonia.exe"

# 1. Kill any existing LabPlot.Avalonia processes (prevents pile-up).
$existing = @(Get-Process -Name 'LabPlot.Avalonia' -ErrorAction SilentlyContinue)
if ($existing.Count -gt 0) {
    Write-Host "Stopping $($existing.Count) existing LabPlot.Avalonia process(es)..."
    $existing | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

if ($KillOnly) {
    # Also evict resident MSBuild / Roslyn servers (they linger as
    # 'dotnet.exe' processes that Stop-Process -Name LabPlot.Avalonia
    # cannot reach).
    Write-Host "Shutting down dotnet build server (MSBuild / VBCSCompiler) ..."
    & dotnet build-server shutdown | Out-Null
    Write-Host "KillOnly specified; not launching."
    return
}

# 2. Build (unless skipped).
#    -nodeReuse:false stops MSBuild worker nodes from staying resident.
#    UseSharedCompilation=false stops the Roslyn (VBCSCompiler) server
#    from staying resident. Together they prevent 'dotnet.exe' ghosts
#    after the build completes.
if (-not $NoBuild) {
    Write-Host "Building $Configuration ..."
    & dotnet build $projectPath -c $Configuration -v minimal -nodeReuse:false /p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed with exit code $LASTEXITCODE"
        return
    }
}

if (-not (Test-Path $exePath)) {
    Write-Error "Executable not found: $exePath"
    return
}

# 3. Launch the exe directly. stdout/stderr stream to the current console,
#    and Ctrl+C reaches a single process so termination is reliable.
Write-Host "Launching $exePath"
& $exePath
