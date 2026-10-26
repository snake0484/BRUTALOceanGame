@echo off
rem Builds the game in Release and runs it. Any arguments are passed to the game, e.g.:
rem   run.bat --wind 14 --breakers
rem   run.bat --surf-test
rem   run.bat --bench 12
rem   run.bat --surf-test --no-lips
rem   run.bat --surf-cam side --fold-lip   (prototype: the lip as a fold of the ocean mesh instead of the ribbon)
rem   run.bat --surf-cam --wind 16       (camera follows the curl; add "side" after --surf-cam for the shoulder view, "back" for the broken side)
rem   run.bat --fly-test                 (flies the coast and logs the slowest frames)
rem   run.bat --surf-test --wind 4 --swell 1.8 15 0   (groundswell: height m, period s, direction travelling toward in degrees; light wind; --swell 0 0 0 for none)
rem   run.bat --weather-offset 120          (weather starts 120 min into its timeline; --wind pins the sea and turns weather off, --weather forces it on)
rem   run.bat --weather-offset 725 --rain 0.9 (rain at 0.9 whatever the weather; --roof-test floats a roof over the camera, --bench-rain benches rain on/off)
rem   run.bat --seaside-test --water-pipes   (voxel water as the 3D pipe model with momentum instead of the cell automaton)
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
