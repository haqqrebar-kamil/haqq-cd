@echo off
rem Starts HAQQ CAD. Optionally pass a drawing: run.cmd C:\path\drawing.hcad
dotnet run --project src\HAQQ.CAD.Desktop -c Release -- %*
