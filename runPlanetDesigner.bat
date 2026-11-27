@echo off
rem Builds the planet designer in Release and runs it. Any arguments are passed to it, e.g.:
rem   runPlanetDesigner.bat                       (the home planet, from orbit over day-side land)
rem   runPlanetDesigner.bat --body home-moon
rem   runPlanetDesigner.bat --distance 30000 --pitch 35   (30 km from the target, 35 degrees above its horizon)
rem   runPlanetDesigner.bat --embedded            (show the layers built into the game, not the ones on disk)
rem Edits save to data\planets\(body)\ in this repository; the game picks them up when it is next built.
rem 1 2 3 pick look, paint, sites. Paint: left drag. Right drag orbits, middle drag pans, wheel zooms. Sites: Shift + click places, drag moves. Ctrl+Z/Y/S. --input-script file drives the mouse.
setlocal
cd /d "%~dp0"

dotnet build src\PlanetDesigner.App -c Release -v q -nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

pushd src\PlanetDesigner.App\bin\Release\net10.0
BRUTALPlanetDesigner.exe %*
popd
