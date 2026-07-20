@echo off
REM Build the Linux x64 package from Windows (cross-compile; no Linux needed).
REM Produces OWSATH-linux-x64.tar.gz one folder up, ready to hand to any
REM 64-bit Linux machine: extract, sh install.sh, ./OWSATH.

set STAGE=%TEMP%\owsath_linux
set PKG=%TEMP%\OWSATH-linux

dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%STAGE%"
if errorlevel 1 exit /b 1

if exist "%PKG%" rmdir /s /q "%PKG%"
mkdir "%PKG%"
copy "%STAGE%\OWSATH" "%PKG%\" >nul
xcopy /e /i /q assets "%PKG%\assets" >nul
copy linux\install.sh "%PKG%\" >nul
copy linux\owsath.desktop "%PKG%\" >nul
copy linux\README-LINUX.txt "%PKG%\" >nul

tar -czf "..\..\OWSATH-linux-x64.tar.gz" -C "%TEMP%" OWSATH-linux
echo Done: OWSATH-linux-x64.tar.gz
