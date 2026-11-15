@echo off
rem Makes the files you can send to other people: an installer exe and a portable zip.
rem Output goes to the "Installer" folder next to this file. Needs the .NET 10 SDK and internet (first run).
title HAQQ CAD - make installer
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0HAQQ.CAD\tools\installer\make-installer.ps1"
set RC=%errorlevel%
echo.
if not "%RC%"=="0" (echo Something went wrong. See the messages above.) else (start "" "%~dp0Installer")
pause
