#!/bin/sh
# =============================================================================
# Cotifly — prueba local de punta a punta de exportar-dbo-cotifly.sh y restaurar-dbo-cotifly.sh (HU-006.7, #31).
# SOLO PRUEBAS LOCALES: no toca tbsdatacenter ni ningún servidor real.
#
# Con "docker run" y nombres propios (prefijo cotifly-prueba-respaldo-), todo se borra al terminar:
#   1. Origen: SQL Server 2019 con 0001-0003 y datos ficticios (probar-respaldo-datos.sql).
#   2. Login cotifly_respaldo con SOLO CONNECT + SELECT ON SCHEMA::dbo_cotifly (el permiso que se pide al DBA).
#   3. Exportación como cotifly_respaldo, con retención (9 diarios previos simulados; quedan 7).
#   4. Controles del restore: sin --destino-pruebas, contra "tbsdatacenter", contra un servidor que se identifica
#      (MachineName) con un nombre prohibido y con un archivo alterado, se niega.
#   5. Destino: SQL Server 2019 NUEVO y vacío; restore con --aplicar-migraciones.
#   6. Verificación: SHA-256 del contenido de cada tabla (SELECT * FOR JSON) igual en origen y destino, llaves foráneas
#      confiables e IDENTITY reajustado.
# Requisitos: docker. Uso: sh backend-dotnet/scripts/respaldo/probar-respaldo-local.sh
# Tarda ~3-5 minutos (arranque de dos SQL Server). Sale con 0 solo si todo pasa.
# =============================================================================
set -eu

DIR_SCRIPT=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=_comun.sh disable=SC1091
. "$DIR_SCRIPT/_comun.sh"
DIR_MIGRACIONES="$DIR_SCRIPT/../../../db/migrations_mssql"
DIR_SQL_DEV="$DIR_SCRIPT/../sql"

PREFIJO=cotifly-prueba-respaldo
RED=$PREFIJO-red
ORIGEN=$PREFIJO-origen
DESTINO=$PREFIJO-destino
IMG=${COTIFLY_RESPALDO_IMAGEN:-mcr.microsoft.com/mssql/server:2019-latest}
SQLCMD=/opt/mssql-tools18/bin/sqlcmd
SA="Prueba_Sa#$(date +%s)"
CLAVE_RESPALDO="Prueba_Resp#$(date +%s)"
TRABAJO=$(mktemp -d)

limpiar() {
  docker rm -f "$ORIGEN" "$DESTINO" >/dev/null 2>&1 || true
  docker network rm "$RED" >/dev/null 2>&1 || true
  rm -rf "$TRABAJO"
}
trap limpiar EXIT INT TERM

# sqlcmd como sa: q <servidor> <base> <consulta>; archivo: qf <servidor> <base> <archivo>
q() { docker run --rm --network "$RED" --entrypoint "$SQLCMD" "$IMG" -S "$1" -U sa -P "$SA" -C -d "$2" -b -I -h -1 -W -Q "SET NOCOUNT ON; $3" </dev/null | tr -d '\r'; }
qf() { docker run --rm -i --network "$RED" --entrypoint "$SQLCMD" "$IMG" -S "$1" -U sa -P "$SA" -C -d "$2" -b -I -r0 -i /dev/stdin < "$3" >/dev/null; }
esperar_sql() {
  i=0
  until q "$1" master "SELECT 1" >/dev/null 2>&1; do
    i=$((i + 1)); [ "$i" -ge 60 ] && fallar "SQL Server $1 no respondió en 2 minutos"; sleep 2
  done
}
levantar_sql() {
  docker run -d --name "$1" --network "$RED" -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e "MSSQL_SA_PASSWORD=$SA" "$IMG" >/dev/null
  esperar_sql "$1"
}

paso "1. Origen $ORIGEN con 0001-0003 y datos ficticios"
docker network create "$RED" >/dev/null
levantar_sql "$ORIGEN"
qf "$ORIGEN" master "$DIR_SQL_DEV/dev-01-bases.sql"
for s in "$DIR_MIGRACIONES"/[0-9][0-9][0-9][0-9]_*.sql; do qf "$ORIGEN" dbTarifario_Nexo_Pruebas "$s"; done
qf "$ORIGEN" dbTarifario_Nexo_Pruebas "$DIR_SCRIPT/probar-respaldo-datos.sql"

paso "2. Login cotifly_respaldo con solo SELECT sobre el esquema"
q "$ORIGEN" master "CREATE LOGIN cotifly_respaldo WITH PASSWORD = N'$CLAVE_RESPALDO', CHECK_POLICY = OFF;" >/dev/null
q "$ORIGEN" dbTarifario_Nexo_Pruebas "CREATE USER cotifly_respaldo FOR LOGIN cotifly_respaldo; GRANT SELECT ON SCHEMA::dbo_cotifly TO cotifly_respaldo;" >/dev/null
permisos=$(q "$ORIGEN" dbTarifario_Nexo_Pruebas "SELECT permission_name + ' ' + class_desc FROM sys.database_permissions WHERE grantee_principal_id = USER_ID(N'cotifly_respaldo') ORDER BY 1;" | tr '\n' ',')
echo "  permisos: $permisos"
[ "$permisos" = "CONNECT DATABASE,SELECT SCHEMA," ] || fallar "permisos inesperados para cotifly_respaldo"

paso "3. Exportación como cotifly_respaldo"
printf '%s\n' "$CLAVE_RESPALDO" > "$TRABAJO/clave"
mkdir -p "$TRABAJO/respaldos/diario"
for d in 01 02 03 04 05 06 07 08 09; do : > "$TRABAJO/respaldos/diario/dbo_cotifly-200001${d}-023000.tar.gz"; done
COTIFLY_RESPALDO_USAR_CONTENEDOR=1 COTIFLY_RESPALDO_SERVIDOR=$ORIGEN COTIFLY_RESPALDO_USUARIO=cotifly_respaldo \
COTIFLY_RESPALDO_CLAVE_ARCHIVO="$TRABAJO/clave" COTIFLY_RESPALDO_CONFIAR_CERTIFICADO=1 COTIFLY_RESPALDO_RED_DOCKER=$RED \
COTIFLY_RESPALDO_DIR="$TRABAJO/respaldos" COTIFLY_RESPALDO_DIA_SEMANAL="$(date -u +%u)" \
  sh "$DIR_SCRIPT/exportar-dbo-cotifly.sh"
archivo=$(find "$TRABAJO/respaldos/diario" -name 'dbo_cotifly-*.tar.gz' ! -name '*-023000.tar.gz' | head -n 1)
[ -s "$archivo" ] || fallar "no se generó el respaldo"
[ "$(find "$TRABAJO/respaldos/diario" -name '*.tar.gz' | wc -l | tr -d ' ')" = "7" ] || fallar "la retención diaria no dejó 7 archivos"
[ "$(find "$TRABAJO/respaldos/semanal" -name '*.tar.gz' | wc -l | tr -d ' ')" = "1" ] || fallar "no se creó la copia semanal"
echo "  retención: 7 diarios, 1 semanal"

paso "4. Controles de seguridad del restore"
if sh "$DIR_SCRIPT/restaurar-dbo-cotifly.sh" "$archivo" >/dev/null 2>&1; then fallar "restauró sin --destino-pruebas"; fi
echo "  sin --destino-pruebas: rechazado"
if COTIFLY_RESPALDO_SERVIDOR=TBSDATACENTER,1433 COTIFLY_RESPALDO_USUARIO=sa COTIFLY_RESPALDO_CLAVE=x \
   sh "$DIR_SCRIPT/restaurar-dbo-cotifly.sh" --destino-pruebas "$archivo" >/dev/null 2>&1; then fallar "no rechazó tbsdatacenter"; fi
echo "  servidor tbsdatacenter: rechazado"

levantar_sql "$DESTINO"
mkdir -p "$TRABAJO/alterado"
(cd "$TRABAJO/alterado" && gunzip -c "$archivo" | tar -xf - && printf 'x' >> Roles.dat && tar -cf - . | gzip > "$TRABAJO/respaldos/alterado.tar.gz")
if COTIFLY_RESPALDO_USAR_CONTENEDOR=1 COTIFLY_RESPALDO_SERVIDOR=$DESTINO COTIFLY_RESPALDO_USUARIO=sa COTIFLY_RESPALDO_CLAVE="$SA" \
   COTIFLY_RESPALDO_CONFIAR_CERTIFICADO=1 COTIFLY_RESPALDO_RED_DOCKER=$RED \
   sh "$DIR_SCRIPT/restaurar-dbo-cotifly.sh" --destino-pruebas "$TRABAJO/respaldos/alterado.tar.gz" >/dev/null 2>&1; then fallar "aceptó un archivo alterado"; fi
rm -f "$TRABAJO/respaldos/alterado.tar.gz"
echo "  archivo alterado: rechazado"
# Identidad del servidor: el nombre que se pasa (cotifly-prueba-respaldo-destino) no está prohibido, pero lo que el
# servidor dice de sí mismo (MachineName = hostname del contenedor) sí.
maquina=$(q "$DESTINO" master "SELECT CAST(SERVERPROPERTY('MachineName') AS nvarchar(256));")
if COTIFLY_RESPALDO_USAR_CONTENEDOR=1 COTIFLY_RESPALDO_SERVIDOR=$DESTINO COTIFLY_RESPALDO_USUARIO=sa COTIFLY_RESPALDO_CLAVE="$SA" \
   COTIFLY_RESPALDO_CONFIAR_CERTIFICADO=1 COTIFLY_RESPALDO_RED_DOCKER=$RED COTIFLY_RESPALDO_PROHIBIDOS="$maquina" \
   sh "$DIR_SCRIPT/restaurar-dbo-cotifly.sh" --destino-pruebas "$archivo" >/dev/null 2>&1; then fallar "no rechazó un servidor que se identifica con un nombre prohibido"; fi
echo "  servidor que se identifica como prohibido ($maquina): rechazado"

paso "5. Restore en la instancia nueva $DESTINO con --aplicar-migraciones"
COTIFLY_RESPALDO_USAR_CONTENEDOR=1 COTIFLY_RESPALDO_SERVIDOR=$DESTINO COTIFLY_RESPALDO_USUARIO=sa COTIFLY_RESPALDO_CLAVE="$SA" \
COTIFLY_RESPALDO_CONFIAR_CERTIFICADO=1 COTIFLY_RESPALDO_RED_DOCKER=$RED \
  sh "$DIR_SCRIPT/restaurar-dbo-cotifly.sh" --destino-pruebas --aplicar-migraciones "$archivo"

paso "6. Contenido idéntico, llaves confiables e IDENTITY"
tablas=$(q "$ORIGEN" dbTarifario_Nexo_Pruebas "SELECT t.name FROM sys.tables t WHERE t.schema_id = SCHEMA_ID(N'dbo_cotifly') ORDER BY t.name;")
distintas=0
for t in $tablas; do
  validar_identificador "$t"
  consulta="SELECT CONVERT(varchar(64), HASHBYTES('SHA2_256', CAST((SELECT * FROM dbo_cotifly.[$t] ORDER BY 1, 2 FOR JSON PATH, INCLUDE_NULL_VALUES) AS varbinary(max))), 2);"
  if [ "$(q "$ORIGEN" dbTarifario_Nexo_Pruebas "$consulta")" = "$(q "$DESTINO" dbTarifario_Nexo_Pruebas "$consulta")" ]; then
    echo "  $t: igual"
  else
    echo "  $t: DISTINTO" >&2; distintas=$((distintas + 1))
  fi
done
[ "$distintas" -eq 0 ] || fallar "$distintas tablas con contenido distinto"
no_confiables=$(q "$DESTINO" dbTarifario_Nexo_Pruebas "SELECT COUNT(*) FROM sys.foreign_keys WHERE schema_id = SCHEMA_ID(N'dbo_cotifly') AND (is_not_trusted = 1 OR is_disabled = 1);")
[ "$no_confiables" = "0" ] || fallar "$no_confiables llaves foráneas deshabilitadas o no confiables"
echo "  llaves foráneas: todas habilitadas y confiables"
identidades=$(q "$DESTINO" dbTarifario_Nexo_Pruebas "SELECT t.name + '.' + ic.name FROM sys.identity_columns ic JOIN sys.tables t ON t.object_id = ic.object_id WHERE t.schema_id = SCHEMA_ID(N'dbo_cotifly') ORDER BY 1;")
for tc in $identidades; do
  t=${tc%%.*}; c=${tc#*.}
  validar_identificador "$t"; validar_identificador "$c"
  estado=$(q "$DESTINO" dbTarifario_Nexo_Pruebas "SELECT CAST(IDENT_CURRENT(N'dbo_cotifly.$t') AS varchar(20)) + ' >= ' + CAST(ISNULL(MAX([$c]), 0) AS varchar(20)) + CASE WHEN IDENT_CURRENT(N'dbo_cotifly.$t') >= ISNULL(MAX([$c]), 0) THEN ' ok' ELSE ' MAL' END FROM dbo_cotifly.[$t];")
  echo "  IDENTITY $t: $estado"
  case "$estado" in *ok) ;; *) fallar "IDENTITY sin reajustar en $t" ;; esac
done
echo
echo "PRUEBA DE RESPALDO Y RESTORE: OK"
