@echo off
REM Compila BackupChecker in un unico .exe (Windows x64, non richiede .NET installato sul PC che lo usa).
REM Requisito solo per compilare: .NET 8 SDK  ->  winget install Microsoft.DotNet.SDK.8
cd /d "%~dp0"
dotnet publish src\BackupChecker.csproj -c Release -o publish --disable-build-servers
if errorlevel 1 (
  echo.
  echo Compilazione fallita.
  pause
  exit /b 1
)
echo.
echo Fatto: publish\BackupChecker.exe
pause
