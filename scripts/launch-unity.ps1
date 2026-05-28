<#
.SYNOPSIS
    Launch the VTuberSystemBase Unity Editor from the command line (recovery helper).

.DESCRIPTION
    uloop drives an already-running Unity Editor. Use this script to recover when the
    Editor is closed or has crashed. It reads the required Editor version from
    ProjectSettings/ProjectVersion.txt, locates Unity.exe under the Unity Hub install
    root, and launches it with -projectPath for this project.

    After launch it can take tens of seconds before the uloop bridge connects.

.EXAMPLE
    pwsh scripts/launch-unity.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/launch-unity.ps1 -Wait
#>
[CmdletBinding()]
param(
    # Root folder where Unity Editors are installed (one subfolder per version).
    # This machine keeps them under D:\UnityEditors; override to match yours.
    [string]$HubEditorsRoot = 'D:\UnityEditors',
    # When set, wait until the Editor process exits.
    [switch]$Wait,
    # When set, skip the post-launch window activation (focus trigger).
    [switch]$NoActivate
)

$ErrorActionPreference = 'Stop'

# This script lives in <repo>/scripts. The Unity project is <repo>/VTuberSystemBase.
$projectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\VTuberSystemBase')).Path

$versionFile = Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt'
if (-not (Test-Path $versionFile)) {
    throw "ProjectVersion.txt not found: $versionFile"
}

$versionLine = Get-Content $versionFile |
    Where-Object { $_ -match '^m_EditorVersion:' } |
    Select-Object -First 1
if (-not $versionLine) {
    throw "Could not read m_EditorVersion from ProjectVersion.txt."
}
$version = ($versionLine -replace '^m_EditorVersion:\s*', '').Trim()

$editorExe = Join-Path $HubEditorsRoot (Join-Path $version 'Editor\Unity.exe')
if (-not (Test-Path $editorExe)) {
    throw "Unity Editor not found: $editorExe. Install $version via Unity Hub."
}

# Do not start a second instance if this project's Editor is already running.
$running = Get-Process -Name 'Unity' -ErrorAction SilentlyContinue | Where-Object {
    try { $_.MainWindowTitle -like '*VTuberSystemBase*' } catch { $false }
}
if ($running) {
    Write-Host "Unity is already running (PID $($running.Id -join ', ')). Skipping launch."
    return
}

Write-Host "Launching Unity $version"
Write-Host "  project: $projectPath"
Write-Host "  editor:  $editorExe"

$process = Start-Process -FilePath $editorExe -ArgumentList @('-projectPath', $projectPath) -PassThru

# Unity defers its initial asset refresh / script compilation (and therefore the
# uLoopMCP server that the uloop CLI connects to) until the Editor window first
# receives focus. When launched from a script and never brought to the foreground,
# it sits idle and uloop can never connect. Activate the window once by PID so the
# focus event fires and Unity proceeds -- no manual click required.
if (-not $NoActivate) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $activated = $false
        for ($i = 0; $i -lt 120; $i++) {
            Start-Sleep -Milliseconds 500
            $process.Refresh()
            if ($process.HasExited) { break }
            if ($process.MainWindowHandle -ne 0) {
                # AppActivate may lose a race with the splash window; retry a few times.
                for ($j = 0; $j -lt 5; $j++) {
                    if ($shell.AppActivate($process.Id)) { $activated = $true; break }
                    Start-Sleep -Milliseconds 400
                }
                break
            }
        }
        if ($activated) { Write-Host "Activated Unity window (focus delivered)." }
        else { Write-Host "Could not auto-activate the Unity window; focus it manually if uloop cannot connect." }
    }
    catch {
        Write-Host "Auto-activation failed: $($_.Exception.Message). Focus the window manually if needed."
    }
}

if ($Wait) {
    $process.WaitForExit()
    Write-Host "Unity exited (ExitCode $($process.ExitCode))."
}
else {
    Write-Host "Unity launched (PID $($process.Id)). Wait for the uloop bridge to connect."
}
