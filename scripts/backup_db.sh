#!/bin/sh
# Backup de bot-db (SQL Server, HU-002.4) a un archivo .bak con timestamp.
# Uso (dentro de la red del stack, con las env del servicio db):
#   DB_HOST=bot-db BACKUP_DIR=/backups sh scripts/backup_db.sh
#
# Reemplaza el pg_dump anterior (Postgres) -- ver git history si hace falta el
# backup/restore de Postgres para un rollback previo a esta HU.
set -e

: "${MSSQL_SA_PASSWORD:?falta MSSQL_SA_PASSWORD}"
: "${MSSQL_DB:?falta MSSQL_DB}"
HOST="${DB_HOST:-bot-db}"
DIR="${BACKUP_DIR:-/backups}"
STAMP=$(date +%Y%m%d-%H%M%S)
OUT="${DIR}/${MSSQL_DB}-${STAMP}.bak"

mkdir -p "$DIR"
# -C: confía en el certificado del servidor (mismo motivo que el driver 17 en vez
# del 18 -- sin esto, sqlcmd puede rechazar la conexión por TLS sin certificado).
sqlcmd -S "$HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -Q \
    "BACKUP DATABASE [$MSSQL_DB] TO DISK = N'$OUT' WITH FORMAT, INIT;"
echo "Backup creado: $OUT"
