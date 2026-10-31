@echo off
REM Double-click: graphical picker for content sets + mode + faction + overlays.
REM The console is only a launcher; the window is the UI. Kept minimized so it
REM stays out of the way but is still reachable from the taskbar if needed.
powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Minimized -File "%~dp0mods\tools\custom_preset.ps1"
