#requires -Version 5.1
# Switch the installed mod variant inside this offline copy.
# Usage:  powershell -ExecutionPolicy Bypass -File switch_variant.ps1 -Name v2a
# Names: original | v1 | v2a | v2b | v2c
# ASCII-only by design.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet("original", "v1", "v2a", "v2b", "v2c")]
    [string]$Name
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$SS = Join-Path $Root "game\DuneImperium_Data\StreamingAssets"
$BundleName = "b56aa8ad35f9c2e1694994f384e51bbe.bundle"
$BundleTarget = Join-Path $SS "aa\StandaloneWindows64\$BundleName"
$LocTarget = Join-Path $SS "Localization\win\localization"
$Out = Join-Path $Root "mods\out"
$Bak = Join-Path $Root "backup\game"

if (Get-Process DuneImperium -ErrorAction SilentlyContinue) {
    Write-Host "[ERROR] game is running. Close it before switching variants."
    Read-Host "Press Enter to exit"
    exit 1
}
if (-not (Test-Path -LiteralPath $BundleTarget)) {
    Write-Host "[ERROR] target bundle not found: $BundleTarget"
    Read-Host "Press Enter to exit"
    exit 1
}

function Install-Bundle([string]$src, [string]$label) {
    if (-not (Test-Path -LiteralPath $src)) {
        Write-Host "[ERROR] source missing: $src"
        return $false
    }
    Copy-Item -LiteralPath $src -Destination $BundleTarget -Force
    Write-Host ("  scenario bundle <- {0} ({1} B)" -f $label, (Get-Item -LiteralPath $src).Length)
    return $true
}

function Restore-Localization {
    Copy-Item -LiteralPath (Join-Path $Bak "localization_win.orig") -Destination $LocTarget -Force
    Write-Host "  localization    <- original"
}

switch ($Name) {
    "original" {
        Write-Host "Installing: ORIGINAL (no mod)"
        Install-Bundle (Join-Path $Bak "scenario_bundle.orig") "original" | Out-Null
        Restore-Localization
    }
    "v1" {
        Write-Host "Installing: v1 (asymmetric, human only - superseded)"
        Install-Bundle (Join-Path $Out "v1_$BundleName") "v1" | Out-Null
        Restore-Localization
    }
    "v2a" {
        Write-Host "Installing: v2a (symmetric boost, original entry name)"
        Install-Bundle (Join-Path $Out "v2a_$BundleName") "v2a" | Out-Null
        Restore-Localization
    }
    "v2b" {
        Write-Host "Installing: v2b (global only, VP 15)"
        Install-Bundle (Join-Path $Out "v2b_$BundleName") "v2b" | Out-Null
        Restore-Localization
    }
    "v2c" {
        Write-Host "Installing: v2c (symmetric + custom entry name)"
        Install-Bundle (Join-Path $Out "v2c_$BundleName") "v2c" | Out-Null
        $loc = Join-Path $Out "v2c_localization_win"
        if (-not (Test-Path -LiteralPath $loc)) {
            Write-Host "[ERROR] v2c localization file missing: $loc"
        } else {
            Copy-Item -LiteralPath $loc -Destination $LocTarget -Force
            Write-Host ("  localization    <- v2c ({0} B)" -f (Get-Item -LiteralPath $loc).Length)
        }
    }
}

Write-Host ""
Write-Host "Done. Installed variant: $Name"
Write-Host "Launch with the mod launcher, then check the Challenge list."
Read-Host "Press Enter to close"
