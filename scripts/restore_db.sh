#!/bin/sh
# Restore de bot-db (SQL Server, HU-002.4) desde un .bak creado por backup_db.sh.
# Uso: DB_HOST=bot-db sh scripts/restore_db.sh /backups/botcotizador-YYYYmmdd-HHMMSS.bak
#
# LIMITACIÓN CONOCIDA (a diferencia del pg_restore anterior): RESTORE DATABASE
# falla si hay conexiones activas contra la base destino -- a mano, primero:
#   ALTER DATABASE [$MSSQL_DB] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
# y después de este script, volver a modo normal:
#   ALTER DATABASE [$MSSQL_DB] SET MULTI_USER;
# No se automatizó acá porque tumbar conexiones activas sin avisar es una acción
# destructiva que no debería dispararse sola dentro de un script -- decisión de
# quien lo ejecute, según el mismo criterio de este proyecto de separar
# evaluar/proponer de ejecutar en cambios destructivos de BD.
set -e

: "${MSSQL_SA_PASSWORD:?falta MSSQL_SA_PASSWORD}"
: "${MSSQL_DB:?falta MSSQL_DB}"
: "${1:?uso: restore_db.sh <archivo.bak>}"
HOST="${DB_HOST:-bot-db}"

# REPLACE: sobreescribe la BD existente con el contenido del backup (restore
# idempotente, mismo criterio que --clean --if-exists del pg_restore anterior).
sqlcmd -S "$HOST" -U sa -P "$MSSQL_SA_PASSWORD" -C -Q \
    "RESTORE DATABASE [$MSSQL_DB] FROM DISK = N'$1' WITH REPLACE;"
echo "Restore aplicado desde: $1"
