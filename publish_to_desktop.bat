@echo off
setlocal

cd /d "%~dp0"

set DEST=%USERPROFILE%\Desktop
set STAGE=%TEMP%\owsath_publish

echo Building One Who Stands Against The Horde...
dotnet publish -c Release -r win-x64 --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -o "%STAGE%" 2>&1

if errorlevel 1 (
    echo.
    echo Build FAILED. Make sure .NET 8 SDK is installed:
    echo   https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

if not exist "%STAGE%\OWSATH.exe" (
    echo.
    echo ERROR: OWSATH.exe not found after publish.
    pause
    exit /b 1
)

copy /Y "%STAGE%\OWSATH.exe" "%DEST%\OWSATH.exe" >nul
rmdir /S /Q "%STAGE%"

if exist assets (
    xcopy /E /I /Y assets "%DEST%\assets" >nul
    echo Copied assets folder.
)

echo.
echo Done! Game installed to:
echo   %DEST%\OWSATH.exe
echo.
pause
