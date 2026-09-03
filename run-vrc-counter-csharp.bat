@echo off
setlocal
cd /d "%~dp0"
if exist "VrcCounter-CSharp\VrcCounter.exe" (
  wscript.exe //nologo "%~dp0run-vrc-counter-csharp-hidden.vbs"
  exit /b 0
) else (
  dotnet run --project "src\VrcCounter\VrcCounter.csproj" --configuration Release -- --data-dir "%~dp0."
)
if errorlevel 1 pause
