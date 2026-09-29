#!/bin/sh
# =============================================================================
# Cotifly — exportación lógica diaria del esquema dbo_cotifly (HU-006.7, #31). Política: DEPLOY.md §6.
#
# COMPLEMENTA, no reemplaza, los backups nativos del DBA de la base compartida dbTarifario_Nexo_Pruebas: exporta solo
# los datos de las tablas de dbo_cotifly (bcp en formato nativo, una tabla por archivo) y permite restaurar Cotifly sin
# restaurar la base completa de otras aplicaciones. La estructura NO se exporta: la definen los scripts
# db/migrations_mssql/000N (el restore los aplica en la instancia de pruebas y compara columnas).
#
# Permisos: basta SELECT sobre el esquema. Recomendado un login propio de solo lectura (pedir al DBA):
#   CREATE LOGIN cotifly_respaldo WITH PASSWORD = '...';  USE dbTarifario_Nexo_Pruebas;
#   CREATE USER cotifly_respaldo FOR LOGIN cotifly_respaldo;  GRANT SELECT ON SCHEMA::dbo_cotifly TO cotifly_respaldo;
# (cotifly_app también sirve, porque ya tiene SELECT sobre el esquema, pero además puede escribir.)
#
# Consistencia: bcp exporta tabla por tabla en sesiones separadas; no es una foto transaccional de todo el esquema.
# Programarlo en baja actividad (02:30). El restore valida las llaves foráneas y avisa si algo quedó inconsistente.
#
# Resultado: <DIR>/diario/dbo_cotifly-AAAAMMDD-HHMMSS.tar.gz con <tabla>.dat, columnas.txt, filas.txt, metadatos.txt y
# SHA256SUMS. El día COTIFLY_RESPALDO_DIA_SEMANAL (1-7, por defecto 7 = domingo) se copia además a <DIR>/semanal.
# Retención: COTIFLY_RESPALDO_DIARIOS (7) y COTIFLY_RESPALDO_SEMANALES (4) archivos más recientes. Permisos 600/700:
# el contenido tiene datos personales (correos, nombres, NIT).
#
# Uso:
#   sh exportar-dbo-cotifly.sh --entorno /etc/cotifly/respaldo.env
#   COTIFLY_RESPALDO_SERVIDOR=... COTIFLY_RESPALDO_USUARIO=... COTIFLY_RESPALDO_CLAVE_ARCHIVO=... \
#     COTIFLY_RESPALDO_DIR=/var/backups/cotifly sh exportar-dbo-cotifly.sh
# Cron (worker, root):  30 2 * * * /opt/cotifly/respaldo/exportar-dbo-cotifly.sh --entorno /etc/cotifly/respaldo.env >> /var/log/cotifly-respaldo.log 2>&1
#
# Variables: las de conexión de _comun.sh, más COTIFLY_RESPALDO_DIR (obligatoria), COTIFLY_RESPALDO_DIARIOS,
# COTIFLY_RESPALDO_SEMANALES y COTIFLY_RESPALDO_DIA_SEMANAL. Sale con 0 solo si la exportación quedó completa.
# =============================================================================
set -eu
umask 077

DIR_SCRIPT=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=_comun.sh disable=SC1091
. "$DIR_SCRIPT/_comun.sh"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --entorno) shift; cargar_entorno "${1:?falta el archivo después de --entorno}" ;;
    -h|--help) sed -n '2,34p' "$0"; exit 0 ;;
    *) fallar "opción desconocida: $1 (ver --help)" ;;
  esac
  shift
done

# Retención validada ANTES de exportar: un valor raro (vacío, 0, "siete") no debe descubrirse al podar.
DIARIOS=${COTIFLY_RESPALDO_DIARIOS:-7}
SEMANALES=${COTIFLY_RESPALDO_SEMANALES:-4}
DIA_SEMANAL=${COTIFLY_RESPALDO_DIA_SEMANAL:-7}
for par in "COTIFLY_RESPALDO_DIARIOS=$DIARIOS" "COTIFLY_RESPALDO_SEMANALES=$SEMANALES"; do
  valor=${par#*=}
  case "$valor" in ''|*[!0-9]*) fallar "${par%%=*} debe ser un entero >= 1 (recibido: '$valor')" ;; esac
  [ "$valor" -ge 1 ] || fallar "${par%%=*} debe ser un entero >= 1 (recibido: '$valor')"
done
case "$DIA_SEMANAL" in [1-7]) ;; *) fallar "COTIFLY_RESPALDO_DIA_SEMANAL debe ser 1-7 (1 = lunes, 7 = domingo; recibido: '$DIA_SEMANAL')" ;; esac

if [ "${COTIFLY_RESPALDO_EN_CONTENEDOR:-0}" = "1" ]; then
  DESTINO=/datos
else
  DESTINO=${COTIFLY_RESPALDO_DIR:?falta COTIFLY_RESPALDO_DIR (carpeta de los respaldos)}
  mkdir -p "$DESTINO"
fi
reejecutar_si_hace_falta "$0" "$DESTINO" ""
cargar_conexion

SELLO=$(date -u +%Y%m%d-%H%M%S)
NOMBRE="$ESQUEMA-$SELLO"
mkdir -p "$DESTINO/diario" "$DESTINO/semanal"
TMP="$DESTINO/.en-curso-$SELLO"
mkdir -p "$TMP"
trap 'rm -rf "$TMP" "$DESTINO/diario/$NOMBRE.tar.gz.parcial"' EXIT
trap 'exit 130' INT TERM

paso "Exportando [$BASE].[$ESQUEMA] desde $SERVIDOR como $USUARIO"
version=$(sql "SELECT CAST(SERVERPROPERTY('ProductVersion') AS varchar(40)) + ' ' + CAST(SERVERPROPERTY('Edition') AS varchar(80));")
# Identidad del servidor de origen: el restore se niega a restaurar encima de él.
origen_servername=$(sql "SELECT ISNULL(CAST(@@SERVERNAME AS nvarchar(256)), N'');")
origen_machinename=$(sql "SELECT ISNULL(CAST(SERVERPROPERTY('MachineName') AS nvarchar(256)), N'');")
origen_netbios=$(sql "SELECT ISNULL(CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS nvarchar(256)), N'');")
listar_tablas > "$TMP/tablas.txt"
[ -s "$TMP/tablas.txt" ] || fallar "el esquema $ESQUEMA no tiene tablas visibles para $USUARIO (¿falta GRANT SELECT ON SCHEMA::$ESQUEMA?)"
listar_columnas > "$TMP/columnas.txt"

: > "$TMP/filas.txt"
while IFS= read -r tabla; do
  validar_identificador "$tabla"
  salida=$(bcp_tabla "$tabla" out "$TMP/$tabla.dat" 2>&1) || { echo "$salida" >&2; fallar "bcp out falló en $tabla"; }
  filas=$(echo "$salida" | sed -n 's/^\([0-9][0-9]*\) rows copied.*/\1/p' | tail -n 1)
  [ -n "$filas" ] || { echo "$salida" >&2; fallar "no se pudo leer el número de filas de $tabla"; }
  echo "$tabla|$filas" >> "$TMP/filas.txt"
  echo "  $tabla: $filas filas"
done < "$TMP/tablas.txt"

{
  echo "formato=cotifly-bcp-nativo-1"
  echo "fecha_utc=$SELLO"
  echo "servidor=$SERVIDOR"
  echo "base=$BASE"
  echo "esquema=$ESQUEMA"
  echo "usuario=$USUARIO"
  echo "version_sql=$version"
  echo "origen_servername=$origen_servername"
  echo "origen_machinename=$origen_machinename"
  echo "origen_netbios=$origen_netbios"
  echo "tablas=$(wc -l < "$TMP/tablas.txt" | tr -d ' ')"
} > "$TMP/metadatos.txt"
rm -f "$TMP/tablas.txt"
(cd "$TMP" && sha256sum ./*.dat columnas.txt filas.txt metadatos.txt > SHA256SUMS)

tar -czf "$DESTINO/diario/$NOMBRE.tar.gz.parcial" -C "$TMP" .
mv "$DESTINO/diario/$NOMBRE.tar.gz.parcial" "$DESTINO/diario/$NOMBRE.tar.gz"
echo "Respaldo: diario/$NOMBRE.tar.gz ($(wc -c < "$DESTINO/diario/$NOMBRE.tar.gz" | tr -d ' ') bytes)"

# Copia semanal: una sola por semana ISO. Si el script corre dos veces el día configurado (reintento manual), la copia
# nueva reemplaza a la anterior de la misma semana, así la retención semanal cuenta semanas y no ejecuciones.
if [ "$(date -u +%u)" = "$DIA_SEMANAL" ]; then
  semana=$(date -u +%G%V)
  for previa in "$DESTINO/semanal/$ESQUEMA"-*.tar.gz; do
    [ -f "$previa" ] || continue
    fecha_previa=$(basename "$previa" | sed -n "s/^$ESQUEMA-\([0-9]\{8\}\)-[0-9]\{6\}\.tar\.gz$/\1/p")
    [ -n "$fecha_previa" ] || continue
    if [ "$(date -u -d "$fecha_previa" +%G%V 2>/dev/null || true)" = "$semana" ]; then
      rm -f "$previa" && echo "Copia semanal anterior de la misma semana reemplazada: $(basename "$previa")"
    fi
  done
  cp "$DESTINO/diario/$NOMBRE.tar.gz" "$DESTINO/semanal/$NOMBRE.tar.gz"
  echo "Copia semanal: semanal/$NOMBRE.tar.gz"
fi

# Retención: conserva los N más recientes de cada carpeta (el nombre lleva la fecha, ordena bien).
podar() {
  carpeta=$1; conservar=$2
  # shellcheck disable=SC2010
  ls -1 "$carpeta" 2>/dev/null | grep -E "^$ESQUEMA-[0-9]{8}-[0-9]{6}\.tar\.gz$" | sort -r | tail -n +"$((conservar + 1))" | while IFS= read -r viejo; do
    rm -f "$carpeta/$viejo" && echo "Retención: borrado $(basename "$carpeta")/$viejo"
  done
}
podar "$DESTINO/diario" "$DIARIOS"
podar "$DESTINO/semanal" "$SEMANALES"
echo "Exportación completa."
