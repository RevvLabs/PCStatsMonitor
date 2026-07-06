@echo off
REM PCStatsMonitor v2 — Build & Run (Windows)
REM Requires: .NET 10 SDK  →  https://aka.ms/dotnet/download

cd /d "%~dp0"

echo Checking .NET SDK...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET 10 SDK not found. Download from https://aka.ms/dotnet/download
    pause
    exit /b 1
)

echo Building solution...
dotnet build PCStatsMonitor.sln -c Release -p:Platform=x64
if errorlevel 1 (
    echo Build FAILED.
    pause
    exit /b 1
)

echo.
echo Build OK. Launching as Administrator (required for hardware sensors)...
powershell -Command "Start-Process 'src\PCStatsMonitor.App\bin\x64\Release\net10.0-windows\PCStatsMonitor.exe' -Verb RunAs"
