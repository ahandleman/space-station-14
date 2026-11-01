@echo off
cd /d "%~dp0"
dotnet run --project StationCreator.csproj -- --open
if errorlevel 1 pause
