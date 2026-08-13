@echo off
setlocal
cd /d "%~dp0"
if exist "VrcCounter-CSharp\VrcCounter.exe" (
  "VrcCounter-CSharp\VrcCounter.exe" --data-dir "%~dp0."
) else (
  dotnet run --project "src\VrcCounter\VrcCounter.csproj" --configuration Release -- --data-dir "%~dp0."
)
if errorlevel 1 pause
