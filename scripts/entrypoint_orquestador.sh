#!/bin/sh
# Entrypoint del Orquestador: aplica las migraciones y arranca el servidor.
# Las migraciones corren aquí (un único servicio) para no depender de una tarea
# manual en cada despliegue. `alembic upgrade head` es idempotente.
set -e

echo "[entrypoint] Aplicando migraciones (alembic upgrade head)…"
alembic upgrade head

echo "[entrypoint] Iniciando el Orquestador…"
exec uvicorn orquestador.app:app --host 0.0.0.0 --port 8000
