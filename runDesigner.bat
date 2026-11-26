@echo off
rem Builds the ship designer in Release and runs it. Any arguments are passed to it, e.g.:
rem   runDesigner.bat --demo                  (opens with a hollow box hull, cylinder nose and roof wedge)
rem   runDesigner.bat --demo-mesh             (opens with a mesh-modelled, subdivided hull cut by a door)
rem   runDesigner.bat --load path\hull.bplan
rem   runDesigner.bat --bake-bench 100        (headless: bake timings for a 100-solid hull, then exits)
rem   runDesigner.bat --bake-bench 100 --bench-meshes        (every 5th solid a smoothed mesh solid)
rem   Mesh editing: select a solid, Properties > To mesh, then the Mesh panel (Edit mesh, 1/2/3 select modes).
rem Plans save to Documents\BRUTALGame\Plans by default.
setlocal
cd /d "%~dp0"

dotnet build src\Designer.App -c Release -v q -nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

pushd src\Designer.App\bin\Release\net10.0
BRUTALDesigner.exe %*
popd
