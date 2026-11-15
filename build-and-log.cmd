@echo off
rem Builds and tests HAQQ CAD and writes everything to build.log in this folder.
cd /d "%~dp0"
echo === SDKs > build.log
dotnet --list-sdks >> build.log 2>&1
echo === BUILD >> build.log
dotnet build HAQQ.CAD.sln -c Release >> build.log 2>&1
echo BUILD_EXIT=%errorlevel% >> build.log
echo === TEST >> build.log
dotnet test HAQQ.CAD.sln -c Release --no-build >> build.log 2>&1
echo TEST_EXIT=%errorlevel% >> build.log
echo === DONE >> build.log
