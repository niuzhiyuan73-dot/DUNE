#requires -Version 5.1
# Dune Imperium - modded offline launcher with isolated profile.
# Swaps LocalLow profile dirs to junctions pointing at this folder's profile\,
# launches the copied game, then restores the real dirs on exit.
# ASCII-only by design (avoids PS 5.1 encoding issues).

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Exe = Join-Path $Root "game\DuneImperium.exe"
$StateFile = Join-Path $Root "profile\.swapped"

$LocalLow = Join-Path $env:USERPROFILE "AppData\LocalLow"
$Real1 = Join-Path $LocalLow "Dire Wolf Digital"
$Real2 = Join-Path $LocalLow "Unity\Dire Wolf Digital_Dune_ Imperium"
$Prof1 = Join-Path $Root "profile\DireWolf"
$Prof2 = Join-Path $Root "profile\UnityCache"

function Is-Junction([string]$p) {
    if (-not (Test-Path -LiteralPath $p)) { return $false }
    $item = Get-Item -LiteralPath $p -Force
    return [bool]($item.Attributes -band [IO.FileAttributes]::ReparsePoint)
}

function Remove-Junction([string]$p) {
    if (Test-Path -LiteralPath $p) {
        cmd /c rmdir "$p" | Out-Null
    }
}

function Restore-Real([string]$real, [string]$bak) {
    if (Is-Junction $real) {
        Remove-Junction $real
        Write-Host "  [swap] removed junction: $real"
    }
    if (Test-Path -LiteralPath $bak) {
        if (Test-Path -LiteralPath $real) {
            Write-Host "  [WARN] both exist, leaving $bak in place for manual check"
        } else {
            Move-Item -LiteralPath $bak -Destination $real
            Write-Host "  [swap] restored real dir: $real"
        }
    }
}

function Recover-IfNeeded {
    $need = $false
    if (Test-Path -LiteralPath $StateFile) { $need = $true }
    if (Test-Path -LiteralPath ($Real1 + ".real")) { $need = $true }
    if (Test-Path -LiteralPath ($Real2 + ".real")) { $need = $true }
    if (-not $need) { return }
    Write-Host "[recover] previous session did not exit cleanly, restoring..."
    Restore-Real $Real1 ($Real1 + ".real")
    Restore-Real $Real2 ($Real2 + ".real")
    if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
    Write-Host "[recover] done"
}

function Swap-To-Profile {
    if (-not (Test-Path -LiteralPath $Prof1)) { New-Item -ItemType Directory -Path $Prof1 -Force | Out-Null }
    if (-not (Test-Path -LiteralPath $Prof2)) { New-Item -ItemType Directory -Path $Prof2 -Force | Out-Null }

    if (Test-Path -LiteralPath $Real1) {
        Move-Item -LiteralPath $Real1 -Destination ($Real1 + ".real")
    }
    cmd /c mklink /J "$Real1" "$Prof1" | Out-Null

    if (Test-Path -LiteralPath $Real2) {
        Move-Item -LiteralPath $Real2 -Destination ($Real2 + ".real")
    }
    $real2parent = Split-Path -Parent $Real2
    if (-not (Test-Path -LiteralPath $real2parent)) { New-Item -ItemType Directory -Path $real2parent -Force | Out-Null }
    cmd /c mklink /J "$Real2" "$Prof2" | Out-Null

    Set-Content -LiteralPath $StateFile -Value "swapped" -Encoding ASCII
    Write-Host "[swap] profile isolated to $Root\profile"
}

function Unswap {
    Restore-Real $Real1 ($Real1 + ".real")
    Restore-Real $Real2 ($Real2 + ".real")
    if (Test-Path -LiteralPath $StateFile) { Remove-Item -LiteralPath $StateFile -Force }
}

# Reshuffle seats on every launch: the seat numbers live inside the installed
# scenario bundle, so this has to run right before the game starts.
# The helper is called with --ascii because this console uses the GBK code page
# and UTF-8 Chinese output would come out garbled.
function Invoke-SeatShuffle {
    $seater = Join-Path $Root "mods\tools\randomize_seats.py"
    if (-not (Test-Path -LiteralPath $seater)) { return }

    $cands = @(
        (Join-Path $env:USERPROFILE ".workbuddy\binaries\python\versions\3.13.12\python.exe"),
        "python"
    )

    $savedEap = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    $lastOut = $null
    try {
        foreach ($c in $cands) {
            if ($c -eq "python") {
                if (-not (Get-Command python -ErrorAction SilentlyContinue)) { continue }
            } elseif (-not (Test-Path -LiteralPath $c)) {
                continue
            }
            $lastOut = & $c $seater --ascii 2>&1
            if ($LASTEXITCODE -eq 0) {
                $lastOut | ForEach-Object { Write-Host ("$_") }
                return
            }
        }
        Write-Host "[seats] shuffle unavailable (no python with UnityPy); keeping scenario seats"
        if ($lastOut) {
            $lastOut | Select-Object -First 4 | ForEach-Object { Write-Host ("[seats]   " + $_) }
        }
    }
    catch {
        Write-Host ("[seats] skipped: " + $_.Exception.Message)
    }
    finally {
        $ErrorActionPreference = $savedEap
    }
}

Write-Host "=== Dune Imperium modded launcher ==="

if (-not (Test-Path -LiteralPath $Exe)) {
    Write-Host "[ERROR] game copy not found: $Exe"
    Read-Host "Press Enter to exit"
    exit 1
}

$steam = Get-Process steam -ErrorAction SilentlyContinue
if (-not $steam) {
    Write-Host "[WARN] Steam client is not running."
    Write-Host "       This game needs Steamworks. Start Steam and switch it to"
    Write-Host "       OFFLINE MODE (menu: Steam -> Go Offline) before launching."
    Read-Host "Press Enter to continue anyway"
}

Recover-IfNeeded

$appidFile = Join-Path $Root "game\steam_appid.txt"
if (-not (Test-Path -LiteralPath $appidFile)) {
    Set-Content -LiteralPath $appidFile -Value "1689500" -Encoding ASCII
    Write-Host "[init] created steam_appid.txt"
}

$fatal = $null
try {
    Swap-To-Profile
    Invoke-SeatShuffle

    Write-Host "[run] launching game..."
    $p = Start-Process -FilePath $Exe -WorkingDirectory (Split-Path -Parent $Exe) -PassThru
    $p.WaitForExit()
    Write-Host "[run] game exited (code $($p.ExitCode))"
}
catch {
    $fatal = $_
    Write-Host ("[ERROR] " + $_.Exception.Message)
}
finally {
    Unswap
    Write-Host "[swap] real profile restored"
}

if ($fatal) {
    Read-Host "Press Enter to exit"
    exit 1
}

Read-Host "Press Enter to close"
