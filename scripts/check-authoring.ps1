# Keeps the authoring build (planet tabs, D89) compiling and inside its boundary. The solution builds the shipping
# designer (Authoring off); run this after touching Designer.App, its Authoring folder, or what the planet tab uses.
#   powershell -File scripts\check-authoring.ps1 [-Configuration Debug|Release]
param([string]$Configuration = "Debug")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

dotnet build src\Designer.App -c $Configuration -p:Authoring=true -v q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Authoring build failed."; exit 1 }

# Both graphs: the authoring restore (fresh from the build above) and the shipping one (restored fresh here).
dotnet restore src\Designer.App -v q
if ($LASTEXITCODE -ne 0) { Write-Host "Shipping restore failed."; exit 1 }

dotnet test tests\Designer.Tests -c $Configuration --filter "FullyQualifiedName~DesignerBoundaryTests" -v q -nologo
if ($LASTEXITCODE -ne 0) { Write-Host "Boundary tests failed."; exit 1 }

Write-Host "Authoring build OK."
