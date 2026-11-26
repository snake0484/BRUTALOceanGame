# Two-process local network test: one BRUTALServer and N BRUTALNetClient processes on localhost.
# Prints each process's SUMMARY lines (bytes/s per client, reconcile error). Build first (Release recommended).
param(
    [int]$Port = 7777,
    [int]$Clients = 2,
    [double]$Seconds = 20,
    [int]$Npcs = 48,
    [string]$Configuration = "Release",
    [double]$SimLoss = 0,
    [int]$SimPing = 0
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$server = Join-Path $root "src/Server/bin/$Configuration/net10.0/BRUTALServer.dll"
$client = Join-Path $root "src/Net.Client/bin/$Configuration/net10.0/BRUTALNetClient.dll"
$logs = Join-Path ([IO.Path]::GetTempPath()) "brutal-net-local-test"
New-Item -ItemType Directory -Force $logs | Out-Null

$serverLog = Join-Path $logs "server.log"
$serverProc = Start-Process dotnet -ArgumentList @($server, "--port", $Port, "--seed", 1, "--npcs", $Npcs, "--exit-when-empty") `
    -RedirectStandardOutput $serverLog -RedirectStandardError (Join-Path $logs "server.err") -PassThru -NoNewWindow
Start-Sleep -Milliseconds 800

$clientProcs = @()
for ($i = 0; $i -lt $Clients; $i++) {
    $clientArgs = @($client, "--connect", "127.0.0.1:$Port", "--seconds", $Seconds, "--pattern", $i)
    if ($SimLoss -gt 0 -or $SimPing -gt 0) { $clientArgs += @("--sim-loss", $SimLoss, "--sim-ping", $SimPing) }
    if ($i -eq 0) { $clientArgs += "--edit" }
    $clientProcs += Start-Process dotnet -ArgumentList $clientArgs -RedirectStandardOutput (Join-Path $logs "client$i.log") `
        -RedirectStandardError (Join-Path $logs "client$i.err") -PassThru -NoNewWindow
}

$clientProcs | Wait-Process -Timeout ([int]($Seconds + 60))
$serverProc | Wait-Process -Timeout 30

Select-String -Path (Join-Path $logs "*.log") -Pattern "SUMMARY" | ForEach-Object { $_.Line }
