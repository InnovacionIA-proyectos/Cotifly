#!/bin/sh
# Reconstruye las imagenes del Bot Cotizador EN EL WORKER (TBSNODO1-DKP) tras un
# cambio de CODIGO. El manager esta al limite de disco: por eso se construye aqui.
#
# Uso (en el worker):
#   sh rebuild-on-worker.sh
# Despues, en el panel de Dokploy: proyecto "bot-cotizador" -> Redeploy
# (Dokploy re-hace `docker stack deploy` y toma las imagenes :latest recien construidas).
#
# Para cambios solo de CONFIG (este compose/env): NO hace falta reconstruir; basta
# editar en git, `git push` y Redeploy en Dokploy.
set -e

SRC="${SRC:-/home/dguerra/bot-cotizador-git}"
REPO="${REPO:-http://tbsservicios/BSGit/bot-cotizador.git}"

if [ -d "$SRC/.git" ]; then
  echo "[rebuild] git pull en $SRC"
  cd "$SRC" && git pull
else
  echo "[rebuild] clonando $REPO -> $SRC (requiere credenciales de git configuradas)"
  git clone "$REPO" "$SRC"
  cd "$SRC"
fi

echo "[rebuild] build MCP (weasyprint)…"
docker build -t bot-cotizador-mcp:latest -f mcp_server/Dockerfile .
echo "[rebuild] build Orquestador…"
docker build -t bot-cotizador-orquestador:latest -f orquestador/Dockerfile .

echo "[rebuild] OK. Ahora: Dokploy -> proyecto bot-cotizador -> Redeploy."
