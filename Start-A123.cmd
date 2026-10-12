@echo off
set "exampleExe=%~dp0src\Testumgebung.Desktop\bin\Debug\net10.0-windows\Testumgebung.Desktop.exe"
if not exist "%exampleExe%" (
  echo Die Anwendung zuerst bauen: dotnet build src\Testumgebung.Desktop -m:1
  pause
  exit /b 1
)
start "" "%exampleExe%" --development --example-order
