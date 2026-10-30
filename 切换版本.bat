@echo off
setlocal
echo ============================================
echo  DUNE offline mod - switch variant
echo ============================================
echo  1 = original   (no mod)
echo  2 = v1         (asymmetric, superseded)
echo  3 = v2a        (symmetric boost)
echo  4 = v2b        (global only, VP 15)
echo  5 = v2c        (symmetric + custom name)
echo.
set /p CH=Choose 1-5:
if "%CH%"=="1" set N=original
if "%CH%"=="2" set N=v1
if "%CH%"=="3" set N=v2a
if "%CH%"=="4" set N=v2b
if "%CH%"=="5" set N=v2c
if "%N%"=="" (
  echo Invalid choice.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0switch_variant.ps1" -Name %N%
