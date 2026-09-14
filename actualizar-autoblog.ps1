# Reloj principal en Windows: todas las fuentes, Autoblog incluido.
# Log: $env:TEMP\puntomuerto-autoblog.log
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"

$log = Join-Path $env:TEMP "puntomuerto-autoblog.log"
function Write-Log([string]$msg) {
  $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg
  Add-Content -Path $log -Value $line
  Write-Host $line
}

try {
  Write-Log "=== inicio actualizar-autoblog ==="

  if (Test-Path .env) {
    Get-Content .env | ForEach-Object {
      if ($_ -match '^\s*#' -or $_ -match '^\s*$') { return }
      $parts = $_ -split '=', 2
      if ($parts.Count -eq 2) {
        [Environment]::SetEnvironmentVariable($parts[0].Trim(), $parts[1].Trim(), "Process")
      }
    }
  }

  $dotnetRoots = @(
    "$env:ProgramFiles\dotnet",
    "$env:LOCALAPPDATA\Microsoft\dotnet",
    "$env:USERPROFILE\.dotnet"
  )
  foreach ($root in $dotnetRoots) {
    if (Test-Path (Join-Path $root "dotnet.exe")) {
      $env:PATH = "$root;$env:PATH"
      break
    }
  }

  git pull --ff-only
  if ($LASTEXITCODE -ne 0) { throw "git pull fallo con codigo $LASTEXITCODE" }

  # Todas las fuentes, Autoblog incluido.
  & dotnet run
  if ($LASTEXITCODE -ne 0) { throw "dotnet run fallo con codigo $LASTEXITCODE" }

  git add docs/noticias.json docs/semana.json docs/guiones.json docs/guiones
  git diff --cached --quiet
  if ($LASTEXITCODE -eq 0) {
    Write-Log "Sin notas nuevas; no hay commit."
    exit 0
  }

  $fecha = Get-Date -Format "yyyy-MM-dd"
  git commit -m "chore: actualizar noticias $fecha"
  if ($LASTEXITCODE -ne 0) { throw "git commit fallo con codigo $LASTEXITCODE" }

  git push
  if ($LASTEXITCODE -ne 0) { throw "git push fallo con codigo $LASTEXITCODE" }

  Write-Log "Publicado OK."
}
catch {
  Write-Log "ERROR: $_"
  exit 1
}
