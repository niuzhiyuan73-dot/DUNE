@echo off
REM Double-click entry point for the modded offline launcher.
REM Keep the window open if the script dies before its own pause.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch_mod.ps1"
if errorlevel 1 (
    echo.
    echo [launcher exited with an error - see messages above]
    pause
)
