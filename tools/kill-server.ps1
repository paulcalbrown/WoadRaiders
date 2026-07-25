<#
.SYNOPSIS
Stops the local WoadRaiders dev server that is holding a UDP port, so the port
frees up before the next `dotnet run --project WoadRaiders.Server`.

.DESCRIPTION
  .\tools\kill-server.ps1              stop whatever process owns UDP 9050
  .\tools\kill-server.ps1 -Port 9977   target a different port

Replaces the ad-hoc `(Get-NetUDPEndpoint -LocalPort 9050).OwningProcess | Stop-Process`
one-liners with one stable command. It reports the process it kills, is a no-op
(exit 0) when nothing is listening, and never throws on an empty port.

By default it only stops processes whose name looks like the dev server
(dotnet / WoadRaiders.Server); pass -Force to stop whatever holds the port.

Windows PowerShell 5.1 compatible.
#>
param(
    [int]$Port = 9050,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

try {
    $endpoints = Get-NetUDPEndpoint -LocalPort $Port -ErrorAction Stop
} catch {
    Write-Output "No server listening on UDP $Port."
    return
}

$pids = $endpoints | Select-Object -ExpandProperty OwningProcess -Unique
foreach ($procId in $pids) {
    if (-not $procId -or $procId -eq 0) { continue }
    try {
        $proc = Get-Process -Id $procId -ErrorAction Stop
    } catch {
        Write-Output "UDP $Port is held by PID $procId, but that process is already gone."
        continue
    }

    $looksLikeServer = $proc.ProcessName -match 'dotnet|WoadRaiders'
    if (-not $looksLikeServer -and -not $Force) {
        Write-Output "UDP $Port is held by $($proc.ProcessName) (PID $procId) -- not obviously the dev server. Re-run with -Force to stop it anyway."
        continue
    }

    Stop-Process -Id $procId -Force -Confirm:$false
    Write-Output "Stopped $($proc.ProcessName) (PID $procId) on UDP $Port."
}
