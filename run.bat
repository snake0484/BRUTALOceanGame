@echo off
rem Builds the game in Release and runs it. Any arguments are passed to the game, e.g.:
rem   run.bat --wind 14
rem   run.bat --bench 12
rem   run.bat --fly-test                 (flies the coast and logs the slowest frames)
rem   run.bat --wind 4 --swell 1.8 15 0   (groundswell: height m, period s, direction travelling toward in degrees; light wind; --swell 0 0 0 for none)
rem   run.bat --weather-offset 120          (weather starts 120 min into its timeline; --wind pins the sea and turns weather off, --weather forces it on)
setlocal
cd /d "%~dp0"

dotnet build src\Game -c Release -v q -nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

pushd src\Game\bin\Release\net10.0
BRUTALGame.exe %*
popd
