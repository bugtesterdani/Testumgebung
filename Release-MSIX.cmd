@echo off
where pwsh >nul 2>nul
if errorlevel 1 (
  echo PowerShell 7 wird benoetigt. Anleitung: docs\msix-release.md
  pause
  exit /b 1
)
set "releaseVersion=%~1"
if not defined releaseVersion set /p "releaseVersion=Release-Version (z.B. 1.0.1.0): "
if not defined releaseVersion exit /b 1
pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0build\msix.ps1" -Version "%releaseVersion%"
set "releaseResult=%errorlevel%"
pause
exit /b %releaseResult%
