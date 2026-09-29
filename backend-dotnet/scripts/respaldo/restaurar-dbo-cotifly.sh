#!/bin/sh
# =============================================================================
# Cotifly — restaurar una exportación de dbo_cotifly (exportar-dbo-cotifly.sh) en una instancia de PRUEBAS
# (HU-006.7, #31). Política y prueba de restore: DEPLOY.md §6.
#
# Pasos:
#   1. Verifica el archivo (SHA256SUMS y formato).
#   2. Con --aplicar-migraciones: crea la base si no existe y aplica db/migrations_mssql (0001 solo si el esquema no
#      existe; los demás son idempotentes), igual que scripts/levantar-local.sh.
#   3. Compara las columnas de destino con las del respaldo (columnas.txt): si difieren, no toca nada.
#   4. Deshabilita las llaves foráneas del esquema, BORRA las filas de todas sus tablas (también las semillas de las
#      migraciones), carga cada tabla con bcp (-E conserva los IDENTITY, -k los NULL), reajusta los IDENTITY y vuelve a
#      habilitar las llaves con WITH CHECK (valida los datos cargados).
#   5. Compara las filas de cada tabla con filas.txt.
# No es transaccional: si falla a mitad, la instancia de pruebas queda a medio cargar (volver a correrlo).
#
# SEGURIDAD: está pensado solo para una instancia de pruebas. Antes de tocar nada:
#   - exige --destino-pruebas;
#   - se niega si el servidor contiene un nombre prohibido: "tbsdatacenter" SIEMPRE, más los que agregue
#     COTIFLY_RESPALDO_PROHIBIDOS (lista adicional separada por comas; no reemplaza a tbsdatacenter), tanto en el
#     nombre que se le pasa (COTIFLY_RESPALDO_SERVIDOR) como en lo que el servidor dice de sí mismo (@@SERVERNAME,
#     SERVERPROPERTY('MachineName'), 'ServerName' y 'ComputerNamePhysicalNetBIOS');
#   - se niega si el destino tiene la misma identidad que el servidor de origen registrado en metadatos.txt.
# Límite: un CNAME o un listener de Availability Group muestran el nombre del nodo real; si ese nodo no está prohibido
# ni es el origen registrado, no se detecta. Restaurar en producción es una decisión del DBA con sus backups nativos.
#
# Permisos en la instancia de pruebas: con --aplicar-migraciones, crear base y esquema (p. ej. sa o dbcreator +
# db_owner); sin él, db_owner de la base (ALTER de las tablas, DELETE, INSERT, DBCC CHECKIDENT).
#
# Uso:
#   sh restaurar-dbo-cotifly.sh --destino-pruebas [--aplicar-migraciones] [--entorno ARCHIVO] <respaldo.tar.gz>
# Variables: las de conexión de _comun.sh (apuntando a la instancia de PRUEBAS), COTIFLY_RESPALDO_PROHIBIDOS (adicionales) y
# COTIFLY_RESPALDO_MIGRACIONES (por defecto ../../../db/migrations_mssql desde esta carpeta).
# =============================================================================
set -eu
umask 077

DIR_SCRIPT=$(cd "$(dirname "$0")" && pwd)
# shellcheck source=_comun.sh disable=SC1091
. "$DIR_SCRIPT/_comun.sh"

destino_pruebas=0
aplicar_migraciones=0
archivo=""
while [ "$#" -gt 0 ]; do
  case "$1" in
    --destino-pruebas) destino_pruebas=1 ;;
    --aplicar-migraciones) aplicar_migraciones=1 ;;
    --entorno) shift; cargar_entorno "${1:?falta el archivo después de --entorno}" ;;
    -h|--help) sed -n '2,33p' "$0"; exit 0 ;;
    -*) fallar "opción desconocida: $1 (ver --help)" ;;
    *) archivo=$1 ;;
  esac
  shift
done

[ "$destino_pruebas" -eq 1 ] || fallar "falta --destino-pruebas: este script solo restaura en una instancia de pruebas"
[ -n "$archivo" ] || fallar "falta el archivo del respaldo (.tar.gz)"
servidor_min=$(printf '%s' "${COTIFLY_RESPALDO_SERVIDOR:-}" | tr '[:upper:]' '[:lower:]')
# tbsdatacenter SIEMPRE está prohibido; COTIFLY_RESPALDO_PROHIBIDOS AGREGA nombres (no reemplaza la lista).
prohibidos=$(printf 'tbsdatacenter,%s' "${COTIFLY_RESPALDO_PROHIBIDOS:-}" | tr '[:upper:]' '[:lower:]' | tr ',' ' ')
case " $prohibidos " in *" tbsdatacenter "*) ;; *) fallar "la lista de servidores prohibidos no incluye tbsdatacenter" ;; esac
[ -n "$(printf '%s' "$prohibidos" | tr -d ' ')" ] || fallar "la lista de servidores prohibidos quedó vacía"
for p in $prohibidos; do
  case "$servidor_min" in *"$p"*) fallar "el servidor '$COTIFLY_RESPALDO_SERVIDOR' contiene el nombre prohibido '$p': no se restaura ahí" ;; esac
done

if [ "${COTIFLY_RESPALDO_EN_CONTENEDOR:-0}" != "1" ]; then
  [ -r "$archivo" ] || fallar "no se puede leer $archivo"
  carpeta=$(dirname "$archivo")
  migraciones=""
  opciones=--destino-pruebas
  if [ "$aplicar_migraciones" -eq 1 ]; then
    migraciones=${COTIFLY_RESPALDO_MIGRACIONES:-$DIR_SCRIPT/../../../db/migrations_mssql}
    [ -d "$migraciones" ] || fallar "no existe la carpeta de migraciones $migraciones"
    opciones="$opciones --aplicar-migraciones"
  fi
  # shellcheck disable=SC2086
  reejecutar_si_hace_falta "$0" "$carpeta" "$migraciones" $opciones "/datos/$(basename "$archivo")"
  # Sin contenedor (bcp y sqlcmd instalados): rutas del equipo.
  DIR_MIGRACIONES=${migraciones:-}
else
  # Dentro del contenedor de herramientas: solo agrega mssql-tools18 al PATH.
  reejecutar_si_hace_falta "$0" "" ""
  DIR_MIGRACIONES=/migraciones
fi
cargar_conexion

TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
trap 'exit 130' INT TERM

paso "Verificando $archivo"
tar -xzf "$archivo" -C "$TMP"
(cd "$TMP" && sha256sum -c --quiet SHA256SUMS) || fallar "el respaldo está dañado (SHA256SUMS no coincide)"
grep -qx "formato=cotifly-bcp-nativo-1" "$TMP/metadatos.txt" || fallar "formato de respaldo desconocido"
esquema_respaldo=$(sed -n 's/^esquema=//p' "$TMP/metadatos.txt")
[ "$esquema_respaldo" = "$ESQUEMA" ] || fallar "el respaldo es del esquema $esquema_respaldo y el destino es $ESQUEMA"
sed 's/^/  /' "$TMP/metadatos.txt"

# Segunda y tercera barrera, además del nombre en COTIFLY_RESPALDO_SERVIDOR (un alias DNS, una IP o un puerto
# reenviado pueden apuntar a tbsdatacenter sin nombrarlo). Se le pregunta al propio servidor quién es y se rechaza si:
#   - alguno de sus nombres contiene un nombre prohibido (sin distinguir mayúsculas), o
#   - coincide con la identidad del servidor ORIGEN del respaldo (metadatos.txt): nunca se restaura encima del origen.
# Límite conocido: con un CNAME o un listener de Availability Group que apunte a otro nodo, el servidor responde con el
# nombre del nodo real; si ese nodo no está en la lista ni es el origen registrado, no se detecta.
paso "Comprobando la identidad del servidor de destino"
base_objetivo=$BASE
BASE=master
identidad=$(sql "SELECT ISNULL(CAST(@@SERVERNAME AS nvarchar(256)), N'') + N'|' + ISNULL(CAST(SERVERPROPERTY('MachineName') AS nvarchar(256)), N'') + N'|' + ISNULL(CAST(SERVERPROPERTY('ServerName') AS nvarchar(256)), N'') + N'|' + ISNULL(CAST(SERVERPROPERTY('ComputerNamePhysicalNetBIOS') AS nvarchar(256)), N'');")
BASE=$base_objetivo
identidad_min=$(printf '%s' "$identidad" | tr '[:upper:]' '[:lower:]')
echo "  @@SERVERNAME|MachineName|ServerName|ComputerNamePhysicalNetBIOS = $identidad"
for p in $prohibidos; do
  case "$identidad_min" in *"$p"*) fallar "el servidor de destino se identifica como '$identidad' y contiene el nombre prohibido '$p': no se restaura ahí" ;; esac
done
origen_ids=$(sed -n 's/^origen_\(servername\|machinename\|netbios\)=//p' "$TMP/metadatos.txt" | tr '[:upper:]' '[:lower:]')
if [ -z "$origen_ids" ]; then
  echo "  AVISO: el respaldo no registra la identidad del servidor de origen (formato anterior); se omite esa comparación"
fi
destino_ids=$(printf '%s' "$identidad_min" | tr '|' '\n')
for o in $origen_ids; do
  for d in $destino_ids; do
    [ -n "$o" ] && [ "$o" = "$d" ] && fallar "el servidor de destino es el mismo que el de origen del respaldo ('$o'): no se restaura encima del origen"
  done
done

if [ "$aplicar_migraciones" -eq 1 ]; then
  paso "Migraciones en [$BASE] de $SERVIDOR"
  base_objetivo=$BASE
  BASE=master
  sql "IF DB_ID(N'$base_objetivo') IS NULL EXEC(N'CREATE DATABASE [$base_objetivo]');" >/dev/null
  BASE=$base_objetivo
  existe=$(sql "SELECT COUNT(*) FROM sys.schemas WHERE name = N'$ESQUEMA';")
  for script in "$DIR_MIGRACIONES"/[0-9][0-9][0-9][0-9]_*.sql; do
    [ -f "$script" ] || fallar "no hay scripts en $DIR_MIGRACIONES"
    nombre=$(basename "$script")
    case "$nombre" in
      0001_*) if [ "$existe" != "0" ]; then echo "  $nombre: el esquema ya existe, se omite (ver ADR-008)"; continue; fi ;;
    esac
    echo "  aplicando $nombre"
    # shellcheck disable=SC2086
    sqlcmd -S "$SERVIDOR" -d "$BASE" -U "$USUARIO" $CERT_SQLCMD -b -I -r0 -i "$script" >/dev/null
  done
fi

paso "Comparando columnas del destino con el respaldo"
listar_columnas > "$TMP/columnas-destino.txt"
if ! diff "$TMP/columnas.txt" "$TMP/columnas-destino.txt" > "$TMP/columnas.diff"; then
  cat "$TMP/columnas.diff" >&2
  fallar "el esquema de destino no coincide con el del respaldo (aplicar las mismas migraciones o usar --aplicar-migraciones)"
fi
echo "  iguales ($(wc -l < "$TMP/columnas.txt" | tr -d ' ') columnas)"

tablas=$(cut -d'|' -f1 "$TMP/filas.txt")
for tabla in $tablas; do validar_identificador "$tabla"; done

paso "Cargando datos en [$BASE].[$ESQUEMA] de $SERVIDOR"
for tabla in $tablas; do sql "ALTER TABLE [$ESQUEMA].[$tabla] NOCHECK CONSTRAINT ALL;" >/dev/null; done
for tabla in $tablas; do sql "DELETE FROM [$ESQUEMA].[$tabla];" >/dev/null; done
for tabla in $tablas; do
  salida=$(bcp_tabla "$tabla" in "$TMP/$tabla.dat" -E -k -h "TABLOCK" 2>&1) || { echo "$salida" >&2; fallar "bcp in falló en $tabla"; }
  echo "  $tabla: $(echo "$salida" | sed -n 's/^\([0-9][0-9]*\) rows copied.*/\1/p' | tail -n 1) filas"
done
# IDENTITY: que el siguiente valor quede después del máximo cargado.
identidades=$(sql "SELECT t.name FROM sys.identity_columns ic JOIN sys.tables t ON t.object_id = ic.object_id JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'$ESQUEMA' ORDER BY t.name;")
for tabla in $identidades; do
  validar_identificador "$tabla"
  sql "DBCC CHECKIDENT (N'[$ESQUEMA].[$tabla]', RESEED) WITH NO_INFOMSGS;" >/dev/null
done
fallos_fk=""
for tabla in $tablas; do
  sql "ALTER TABLE [$ESQUEMA].[$tabla] WITH CHECK CHECK CONSTRAINT ALL;" >/dev/null 2>"$TMP/fk.err" || fallos_fk="$fallos_fk $tabla"
done
[ -z "$fallos_fk" ] || fallar "llaves foráneas inválidas tras la carga en:$fallos_fk (respaldo inconsistente; ver DEPLOY.md §6)"

paso "Comparando filas"
errores=0
while IFS='|' read -r tabla esperadas; do
  reales=$(sql "SELECT COUNT_BIG(*) FROM [$ESQUEMA].[$tabla];")
  if [ "$reales" = "$esperadas" ]; then
    echo "  $tabla: $reales = $esperadas"
  else
    echo "  $tabla: $reales != $esperadas" >&2
    errores=$((errores + 1))
  fi
done < "$TMP/filas.txt"
[ "$errores" -eq 0 ] || fallar "$errores tablas con un número de filas distinto al del respaldo"
echo "Restore completo y verificado en $SERVIDOR [$BASE].[$ESQUEMA]."
