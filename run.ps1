#Requires -Version 5.1
<#
.SYNOPSIS
    Builds SemanticStart and starts it.

.DESCRIPTION
    Wraps the three steps that have to happen in order during development.

    The first one is the reason this script exists: SemanticStart runs from the tray, so a copy is
    almost always still running from the last build, and it holds SemanticStart.Core.dll open. The
    build then fails with MSB3027/MSB3021 pointing at a locked file, which reads like a build error
    and is not one. Stopping the running instance first makes that whole class of confusion go away.

    Afterwards it reports which hotkey actually registered. That is worth surfacing rather than
    assuming, because a hotkey belongs to whichever process registers it first: if something else
    already owns the configured chord, SemanticStart falls back down a candidate list and opens on
    a combination that is not the one documented.

.PARAMETER Configuration
    Debug or Release. Defaults to Release, which is what you want when using the app rather than
    debugging it.

.PARAMETER Test
    Run the test suite after building and before launching. A failing suite stops the launch.

.PARAMETER NoLaunch
    Build (and optionally test) without starting the app.

.EXAMPLE
    .\run.ps1

.EXAMPLE
    .\run.ps1 -Configuration Debug -Test
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Test,

    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

function Write-Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

# --- 1. Stop the running instance so the build can overwrite its assemblies -------------------
$running = @(Get-Process -Name 'SemanticStart.App' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    Write-Step "Stopping $($running.Count) running instance(s)"
    foreach ($process in $running) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    # Windows releases the file locks when the process actually exits, not when the kill is issued.
    $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
}

# --- 2. Build ----------------------------------------------------------------------------------
Write-Step "Building ($Configuration)"
dotnet build (Join-Path $root 'SemanticStart.slnx') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

if ($Test) {
    Write-Step "Running tests"
    dotnet test (Join-Path $root 'tests\SemanticStart.Tests') -c $Configuration --nologo --no-build
    if ($LASTEXITCODE -ne 0) {
        throw "Tests failed; not launching."
    }
}

if ($NoLaunch) {
    Write-Step "Done (not launching)"
    return
}

# --- 3. Launch -------------------------------------------------------------------------------
$exe = Get-ChildItem -Path (Join-Path $root "src\SemanticStart.App\bin\$Configuration") `
    -Filter 'SemanticStart.App.exe' -Recurse -File -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime |
    Select-Object -Last 1

if (-not $exe) {
    throw "Built successfully but could not find SemanticStart.App.exe under bin\$Configuration."
}

# The app logs each registration, so read only what is appended from here on; the file accumulates
# across runs and an older line would report a hotkey from a previous launch.
$log = Join-Path $env:LOCALAPPDATA 'SemanticStart\logs\app.log'
$logLinesBefore = if (Test-Path $log) { (Get-Content $log -ErrorAction SilentlyContinue).Count } else { 0 }

Write-Step "Starting $($exe.FullName)"
Start-Process $exe.FullName

$registration = $null
foreach ($attempt in 1..20) {
    Start-Sleep -Milliseconds 500
    if (-not (Test-Path $log)) { continue }

    $registration = Get-Content $log -ErrorAction SilentlyContinue |
        Select-Object -Skip $logLinesBefore |
        Select-String -Pattern 'Registered hotkey|No hotkey could be registered' |
        Select-Object -Last 1
    if ($registration) { break }
}

if ($registration) {
    Write-Host $registration.Line.Trim() -ForegroundColor Green
} else {
    # Not a failure: the app runs from the tray and may simply have been slower than we waited.
    Write-Host "Started. No hotkey registration logged yet - check $log." -ForegroundColor Yellow
}
