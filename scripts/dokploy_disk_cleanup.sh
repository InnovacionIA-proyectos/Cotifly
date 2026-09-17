#!/bin/sh
# Libera disco en un nodo de Dokploy/Swarm ANTES de un build, de forma segura.
#
# Uso (en el nodo, o por SSH):
#   sh dokploy_disk_cleanup.sh            # limpieza SEGURA (recomendada)
#   sh dokploy_disk_cleanup.sh --report   # solo reporta, no borra nada
#   sh dokploy_disk_cleanup.sh --aggressive  # además borra TODA imagen sin usar
#
# Qué borra la limpieza SEGURA (no destruye datos ni imágenes en uso):
#   - Caché de build de BuildKit (suele ser el mayor consumidor).
#   - Contenedores detenidos.
#   - Imágenes "dangling" (capas huérfanas sin tag).
#   - Redes sin usar.
# NUNCA borra: volúmenes (datos de Postgres/MinIO), ni imágenes en uso por servicios.
#
# --aggressive añade `docker image prune -a` (borra toda imagen que ningún
# contenedor use AHORA). En un clúster compartido esto puede borrar imágenes de
# OTROS stacks; se re-descargarán/reconstruirán al desplegar. Úsalo solo si el
# reporte muestra muchas imágenes viejas y sabes que no rompes otros proyectos.
set -e

MODE="${1:-safe}"

echo "========================================================================"
echo " Uso de disco del daemon Docker (antes):"
echo "========================================================================"
docker system df
echo
echo " Uso del sistema de archivos:"
df -h / 2>/dev/null | tail -n +1 || true
echo

if [ "$MODE" = "--report" ] || [ "$MODE" = "report" ]; then
    echo "[report] Modo reporte: no se borra nada."
    echo "[report] Volúmenes (NO se tocan en la limpieza):"
    docker volume ls
    exit 0
fi

echo "========================================================================"
echo " Limpieza SEGURA (sin tocar volúmenes ni imágenes en uso)…"
echo "========================================================================"

echo "[1/4] Contenedores detenidos…"
docker container prune -f

echo "[2/4] Caché de build (BuildKit)…"
docker builder prune -f

echo "[3/4] Imágenes dangling (sin tag)…"
docker image prune -f

echo "[4/4] Redes sin usar…"
docker network prune -f

if [ "$MODE" = "--aggressive" ] || [ "$MODE" = "aggressive" ]; then
    echo
    echo "!!! --aggressive: borrando TODA imagen sin usar (docker image prune -a) …"
    echo "!!! (afecta a otros stacks del nodo; se re-descargarán al desplegar)"
    docker image prune -a -f
fi

echo
echo "========================================================================"
echo " Uso de disco del daemon Docker (después):"
echo "========================================================================"
docker system df
echo
df -h / 2>/dev/null | tail -n +1 || true
echo "[ok] Limpieza terminada. Los volúmenes de datos quedaron intactos."
