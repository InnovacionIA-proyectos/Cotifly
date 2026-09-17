# Verificación de la Fase 1 contra Postgres en Docker.
# Requisito: Docker Desktop instalado y CORRIENDO (motor "Running") tras reiniciar.
# Uso:  pwsh -File scripts\verificar_bd.ps1
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)

Write-Host "== 1) Verificando que Docker responde ==" -ForegroundColor Cyan
docker info | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Docker no responde. Abre Docker Desktop y espera a 'Running'." }

Write-Host "== 2) Levantando Postgres ==" -ForegroundColor Cyan
docker compose up -d postgres

Write-Host "== 3) Esperando a que Postgres esté sano ==" -ForegroundColor Cyan
for ($i = 0; $i -lt 30; $i++) {
    $estado = docker inspect --format '{{.State.Health.Status}}' (docker compose ps -q postgres) 2>$null
    if ($estado -eq "healthy") { break }
    Start-Sleep -Seconds 2
}
Write-Host "   Estado Postgres: $estado"

Write-Host "== 4) Aplicando migraciones (alembic upgrade head) ==" -ForegroundColor Cyan
& ".venv\Scripts\alembic.exe" upgrade head

Write-Host "== 5) Ejecutando la suite completa ==" -ForegroundColor Cyan
& ".venv\Scripts\python.exe" -m pytest -q
