@echo off
rem Builds HAQQ CAD and runs the automated tests. Requires the .NET 10 SDK on Windows.
dotnet build HAQQ.CAD.sln -c Release || exit /b 1
dotnet test HAQQ.CAD.sln -c Release --no-build || exit /b 1
echo.
echo Build and tests succeeded. Start the application with run.cmd
