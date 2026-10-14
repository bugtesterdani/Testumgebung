@echo off
cd /d "%~dp0"
pwsh -NoProfile -File build\doxygen.ps1
if errorlevel 1 exit /b 1
start "" "artifacts\doxygen\current\html\index.html"
