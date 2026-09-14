# Registra (o actualiza) la tarea de Task Scheduler equivalente al LaunchAgent de la Mac.
# Corre actualizar-autoblog.ps1 a las 10:00, 13:00 y 16:00 (hora local Argentina).
# Ejecutar una vez: powershell -ExecutionPolicy Bypass -File .\windows\Register-Task.ps1

$ErrorActionPreference = "Stop"

$taskName = "PuntomuertoAutoblog"
$repoRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $repoRoot "actualizar-autoblog.ps1"
$logPath = Join-Path $env:TEMP "puntomuerto-autoblog.log"

if (-not (Test-Path $scriptPath)) {
  throw "No encuentro $scriptPath"
}

$action = New-ScheduledTaskAction `
  -Execute "powershell.exe" `
  -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`"" `
  -WorkingDirectory $repoRoot

# Tres disparadores diarios, como el plist de la Mac.
$triggers = @(
  (New-ScheduledTaskTrigger -Daily -At "10:00"),
  (New-ScheduledTaskTrigger -Daily -At "13:00"),
  (New-ScheduledTaskTrigger -Daily -At "16:00")
)

# Corre con el usuario logueado (PC prendida la mayor parte del dia).
$principal = New-ScheduledTaskPrincipal `
  -UserId $env:USERNAME `
  -LogonType Interactive `
  -RunLevel Limited

$settings = New-ScheduledTaskSettingsSet `
  -AllowStartIfOnBatteries `
  -DontStopIfGoingOnBatteries `
  -StartWhenAvailable `
  -MultipleInstances IgnoreNew `
  -ExecutionTimeLimit (New-TimeSpan -Hours 1)

Register-ScheduledTask `
  -TaskName $taskName `
  -Action $action `
  -Trigger $triggers `
  -Principal $principal `
  -Settings $settings `
  -Description "Piston libre: baja feeds (Autoblog incluido) y publica docs/ si hay novedades. Log: $logPath" `
  -Force | Out-Null

Write-Host "Tarea '$taskName' registrada."
Write-Host "Horarios: 10:00, 13:00, 16:00 (hora local)."
Write-Host "Script: $scriptPath"
Write-Host "Log:    $logPath"
Write-Host ""
Write-Host "Probar ahora:"
Write-Host "  Start-ScheduledTask -TaskName $taskName"
Write-Host "  Get-Content `$env:TEMP\puntomuerto-autoblog.log -Tail 40"
