@echo off
REM Restore real LocalLow profile dirs if the modded session did not exit cleanly.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0restore_profile.ps1"
