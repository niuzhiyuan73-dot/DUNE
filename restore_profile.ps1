#requires -Version 5.1
# Manual recovery: restore the real LocalLow profile dirs if a session did not exit cleanly.
# ASCII-only by design.

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$StateFile = Join-Path $Root "profile\.swapped"
$LocalLow = Join-Path $env:USERPROFILE "AppData\LocalLow"
$Real1 = Join-Path $LocalLow "Dire Wolf Digital"
$Real2 = Join-Path $LocalLow "Unity\Dire Wolf Digital_Dune_ Imperium"

function Is-Junction([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return $false }
    $item = Get-Item -LiteralPath $p -Force
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Restore-Real([string]$real, [string]$bak) {
    if (Is-Junction $real) {
        cmd /c rmdir "$real" | Out-Null
        Write-Host "  removed junction: $real"
    }
    if (Test-Path -LiteralPath $bak) {
        if (Test-Path -LiteralPath $real) {
            Write-Host "  [WARN] target exists, backup kept: $bak"
        } else {
            Move-Item -LiteralPath $bak -Destination $real
            Write-Host "  restored: $real"
        }
    } else {
        Write-Host "  no backup found for $real (nothing to do)"
    }
}

Write-Host "=== Restoring real profile dirs ==="
Restore-Real $Real1 ($Real1 + ".real")
Restore-Real $Real2 ($Real2 + ".real")
if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
Write-Host "=== Done. Steam version should now see its own saves again. ==="
Read-Host "Press Enter to close"
