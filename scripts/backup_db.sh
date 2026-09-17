#!/bin/sh
# Backup de bot-db (Postgres) a un dump comprimido con timestamp.
# Uso (dentro de la red del stack, con las env del servicio db):
#   DB_HOST=bot-db BACKUP_DIR=/backups sh scripts/backup_db.sh
set -e

: "${POSTGRES_USER:?falta POSTGRES_USER}"
: "${POSTGRES_DB:?falta POSTGRES_DB}"
HOST="${DB_HOST:-bot-db}"
DIR="${BACKUP_DIR:-/backups}"
STAMP=$(date +%Y%m%d-%H%M%S)
OUT="${DIR}/botcotizador-${STAMP}.dump"

mkdir -p "$DIR"
# -Fc = formato custom (comprimido, restaurable con pg_restore selectivo).
PGPASSWORD="${POSTGRES_PASSWORD}" pg_dump -h "$HOST" -U "$POSTGRES_USER" -d "$POSTGRES_DB" -Fc -f "$OUT"
echo "Backup creado: $OUT"
