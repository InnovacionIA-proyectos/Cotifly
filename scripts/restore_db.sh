#!/bin/sh
# Restore de bot-db (Postgres) desde un dump creado por backup_db.sh.
# Uso: DB_HOST=bot-db sh scripts/restore_db.sh /backups/botcotizador-YYYYmmdd-HHMMSS.dump
set -e

: "${POSTGRES_USER:?falta POSTGRES_USER}"
: "${POSTGRES_DB:?falta POSTGRES_DB}"
: "${1:?uso: restore_db.sh <archivo.dump>}"
HOST="${DB_HOST:-bot-db}"

# --clean --if-exists: recrea objetos sin fallar si no existían (restore idempotente).
PGPASSWORD="${POSTGRES_PASSWORD}" pg_restore -h "$HOST" -U "$POSTGRES_USER" -d "$POSTGRES_DB" \
    --clean --if-exists --no-owner "$1"
echo "Restore aplicado desde: $1"
