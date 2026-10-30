@echo off
setlocal
echo ============================================
echo  DUNE offline mod - switch variant
echo ============================================
echo  1 = original   (no mod)
echo  2 = v1         (asymmetric, superseded)
echo  3 = v2a        (challenge: symmetric boost)
echo  4 = v2b        (challenge: VP 15 only)
echo  5 = v2c        (deprecated: custom name)
echo  6 = v3solo     (standard solo: explicit decks for all)
echo.
set /p CH=Choose 1-6:
if "%CH%"=="1" set N=original
if "%CH%"=="2" set N=v1
if "%CH%"=="3" set N=v2a
if "%CH%"=="4" set N=v2b
if "%CH%"=="5" set N=v2c
if "%CH%"=="6" set N=v3solo
if "%N%"=="" (
  echo Invalid choice.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0switch_variant.ps1" -Name %N%
