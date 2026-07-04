@echo off
title One Who Stands Against The Horde (OWSATH)
cd /d "%~dp0"

where dotnet >nul 2>&1
if %errorlevel% neq 0 (
    echo .NET 8 SDK is required. Download it free from:
    echo   https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

dotnet run --project goblinminigame.csproj
pause
