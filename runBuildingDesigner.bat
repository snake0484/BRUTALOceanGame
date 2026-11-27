@echo off
rem Builds the building designer in Release and runs it. Any arguments are passed to it, e.g.:
rem   runBuildingDesigner.bat --demo                  (opens with a hut: slab, walls, roof, a door and a window cut)
rem   runBuildingDesigner.bat --load path\hut.bbld
rem   runBuildingDesigner.bat --input-script path\steps.txt
rem Buildings save to Documents\BRUTALGame\Buildings by default.
setlocal
cd /d "%~dp0"

dotnet build src\BuildingDesigner.App -c Release -v q -nologo
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

pushd src\BuildingDesigner.App\bin\Release\net10.0
BRUTALBuildingDesigner.exe %*
popd
