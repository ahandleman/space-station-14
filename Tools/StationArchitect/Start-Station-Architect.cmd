@echo off
cd /d "%~dp0"
dotnet run --project "%~dp0StationArchitect.csproj" -- --open
if errorlevel 1 pause
