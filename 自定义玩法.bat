@echo off
REM Double-click: pick any content sets + a preset, build and install.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0mods\tools\custom_preset.ps1"
