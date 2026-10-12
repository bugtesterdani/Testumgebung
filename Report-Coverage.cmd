@echo off
cd /d "%~dp0"
pwsh -NoProfile -ExecutionPolicy Bypass -File build\coverage.ps1
pause
