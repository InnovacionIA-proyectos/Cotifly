# shellcheck shell=sh
# =============================================================================
# Cotifly — funciones comunes de exportar-dbo-cotifly.sh y restaurar-dbo-cotifly.sh (HU-006.7, #31).
# Se carga con ". _comun.sh"; no se ejecuta sola.
#
# Los dos scripts usan bcp y sqlcmd 18 (mssql-tools18). Si no están en el PATH del equipo, el script se vuelve a
# ejecutar dentro de un contenedor de la imagen de SQL Server 2019, que ya los trae en /opt/mssql-tools18/bin
# (la misma idea que scripts/levantar-local.sh), montando esta carpeta, la carpeta de datos y el archivo de la clave.
#
# Variables de conexión (las mismas para exportar y restaurar; cada uso tiene su propio archivo de entorno):
#   COTIFLY_RESPALDO_SERVIDOR              host[,puerto] (obligatoria)
#   COTIFLY_RESPALDO_BASE                  base (por defecto dbTarifario_Nexo_Pruebas)
#   COTIFLY_RESPALDO_ESQUEMA               esquema (por defecto dbo_cotifly)
#   COTIFLY_RESPALDO_USUARIO               SQL login (obligatoria)
#   COTIFLY_RESPALDO_CLAVE_ARCHIVO         archivo con la clave en la primera línea (preferido: permisos 600)
#   COTIFLY_RESPALDO_CLAVE                 la clave en una variable: SOLO pruebas locales (queda visible en docker inspect)
#   COTIFLY_RESPALDO_CONFIAR_CERTIFICADO   1 = aceptar un certificado TLS no confiable (sqlcmd -C, bcp -u). Por
#                                          defecto 0: el cifrado es obligatorio y el certificado se valida.
#   COTIFLY_RESPALDO_IMAGEN                imagen con mssql-tools18 (por defecto mcr.microsoft.com/mssql/server:2019-latest)
#   COTIFLY_RESPALDO_USAR_CONTENEDOR       1 = usar el contenedor aunque bcp/sqlcmd estén instalados
#   COTIFLY_RESPALDO_RED_DOCKER            red de Docker para el contenedor de herramientas (solo pruebas locales)
# =============================================================================

export MSYS_NO_PATHCONV=1

fallar() { echo "ERROR: $1" >&2; exit 1; }
paso() { printf '\n==> %s\n' "$1"; }
ruta_absoluta() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }
# Cita un argumento para eval (comillas simples).
citar() { printf "'%s'" "$(printf '%s' "$1" | sed "s/'/'\\\\''/g")"; }

RUTA_HERRAMIENTAS=/opt/mssql-tools18/bin
IMAGEN_HERRAMIENTAS=${COTIFLY_RESPALDO_IMAGEN:-mcr.microsoft.com/mssql/server:2019-latest}

# Nombre SQL seguro (esquema, base): solo letras, dígitos y "_" (se interpola en consultas).
validar_identificador() {
  echo "$1" | grep -Eq '^[A-Za-z_][A-Za-z0-9_]*$' || fallar "identificador SQL no válido: $1"
}

# Si bcp/sqlcmd no están disponibles, vuelve a ejecutar el script dentro del contenedor de herramientas (no retorna).
#   $1 = ruta del script
#   $2 = carpeta del equipo que se monta en /datos (vacío = ninguna)
#   $3 = carpeta del equipo que se monta en /migraciones, solo lectura (vacío = ninguna)
#   resto = argumentos del script ya traducidos a rutas del contenedor
# Las variables COTIFLY_RESPALDO_* pasan al contenedor; la clave va como archivo montado de solo lectura.
reejecutar_si_hace_falta() {
  script=$1; datos=$2; migraciones=$3; shift 3
  if [ "${COTIFLY_RESPALDO_EN_CONTENEDOR:-0}" = "1" ]; then
    PATH="$RUTA_HERRAMIENTAS:$PATH"; export PATH
    return 0
  fi
  if [ "${COTIFLY_RESPALDO_USAR_CONTENEDOR:-0}" != "1" ] && command -v bcp >/dev/null 2>&1 && command -v sqlcmd >/dev/null 2>&1; then
    return 0
  fi
  command -v docker >/dev/null 2>&1 || fallar "no hay bcp/sqlcmd ni docker en este equipo"

  cmd="docker run --rm -i -e HOME=/tmp -e COTIFLY_RESPALDO_EN_CONTENEDOR=1"
  cmd="$cmd --user $(citar "$(id -u):$(id -g)")"
  cmd="$cmd -v $(citar "$(ruta_absoluta "$(dirname "$script")"):/cotifly-respaldo:ro")"
  for v in SERVIDOR BASE ESQUEMA USUARIO CONFIAR_CERTIFICADO DIARIOS SEMANALES DIA_SEMANAL PROHIBIDOS; do
    eval "valor=\${COTIFLY_RESPALDO_$v:-}"
    # shellcheck disable=SC2154
    if [ -n "$valor" ]; then export "COTIFLY_RESPALDO_$v"; cmd="$cmd -e COTIFLY_RESPALDO_$v"; fi
  done
  if [ -n "${COTIFLY_RESPALDO_CLAVE_ARCHIVO:-}" ]; then
    [ -r "$COTIFLY_RESPALDO_CLAVE_ARCHIVO" ] || fallar "no se puede leer COTIFLY_RESPALDO_CLAVE_ARCHIVO"
    archivo_clave="$(ruta_absoluta "$(dirname "$COTIFLY_RESPALDO_CLAVE_ARCHIVO")")/$(basename "$COTIFLY_RESPALDO_CLAVE_ARCHIVO")"
    cmd="$cmd -e COTIFLY_RESPALDO_CLAVE_ARCHIVO=/run/secrets/cotifly_respaldo_clave"
    cmd="$cmd -v $(citar "$archivo_clave:/run/secrets/cotifly_respaldo_clave:ro")"
  elif [ -n "${COTIFLY_RESPALDO_CLAVE:-}" ]; then
    export COTIFLY_RESPALDO_CLAVE
    cmd="$cmd -e COTIFLY_RESPALDO_CLAVE"
  else
    fallar "falta la clave: COTIFLY_RESPALDO_CLAVE_ARCHIVO o COTIFLY_RESPALDO_CLAVE"
  fi
  [ -n "${COTIFLY_RESPALDO_RED_DOCKER:-}" ] && cmd="$cmd --network $(citar "$COTIFLY_RESPALDO_RED_DOCKER")"
  [ -n "$datos" ] && cmd="$cmd -v $(citar "$(ruta_absoluta "$datos"):/datos")"
  [ -n "$migraciones" ] && cmd="$cmd -v $(citar "$(ruta_absoluta "$migraciones"):/migraciones:ro")"
  cmd="$cmd --entrypoint /bin/sh $(citar "$IMAGEN_HERRAMIENTAS") $(citar "/cotifly-respaldo/$(basename "$script")")"
  for a in "$@"; do cmd="$cmd $(citar "$a")"; done
  eval "exec $cmd"
}

# Carga un archivo de entorno (KEY=valor por línea, como /etc/cotifly/respaldo.env) y exporta sus variables.
cargar_entorno() {
  [ -r "$1" ] || fallar "no se puede leer el archivo de entorno $1"
  set -a
  # shellcheck disable=SC1090
  . "$1"
  set +a
}

# Carga SERVIDOR, BASE, ESQUEMA, USUARIO y CLAVE, y las opciones de certificado.
cargar_conexion() {
  SERVIDOR=${COTIFLY_RESPALDO_SERVIDOR:?falta COTIFLY_RESPALDO_SERVIDOR}
  BASE=${COTIFLY_RESPALDO_BASE:-dbTarifario_Nexo_Pruebas}
  ESQUEMA=${COTIFLY_RESPALDO_ESQUEMA:-dbo_cotifly}
  USUARIO=${COTIFLY_RESPALDO_USUARIO:?falta COTIFLY_RESPALDO_USUARIO}
  validar_identificador "$ESQUEMA"
  validar_identificador "$BASE"
  if [ -n "${COTIFLY_RESPALDO_CLAVE_ARCHIVO:-}" ]; then
    CLAVE=$(head -n 1 "$COTIFLY_RESPALDO_CLAVE_ARCHIVO" | tr -d '\r\n')
  else
    CLAVE=${COTIFLY_RESPALDO_CLAVE:?falta COTIFLY_RESPALDO_CLAVE o COTIFLY_RESPALDO_CLAVE_ARCHIVO}
  fi
  [ -n "$CLAVE" ] || fallar "la clave está vacía"
  # sqlcmd lee la clave de SQLCMDPASSWORD (no queda en su línea de comandos); bcp solo la acepta con -P.
  SQLCMDPASSWORD=$CLAVE; export SQLCMDPASSWORD
  CERT_SQLCMD=""
  CERT_BCP=""
  if [ "${COTIFLY_RESPALDO_CONFIAR_CERTIFICADO:-0}" = "1" ]; then
    CERT_SQLCMD="-C"
    CERT_BCP="-u"
  fi
}

# sqlcmd con salida limpia (sin encabezados ni relleno, sin líneas vacías ni \r). -b termina con código distinto de 0
# ante un error y -r1 manda los mensajes de error a stderr (no se mezclan con el resultado); -I = QUOTED_IDENTIFIER ON
# (índices filtrados). stdin es /dev/null: sqlcmd no debe consumir la entrada de un "while read". Falla (retorna 1) si la consulta falla: usarla como "x=$(sql ...)" o como comando suelto, nunca
# en medio de una tubería (sh no tiene pipefail).
sql() {
  # shellcheck disable=SC2086
  salida_sql=$(sqlcmd -S "$SERVIDOR" -d "$BASE" -U "$USUARIO" $CERT_SQLCMD -b -r1 -I -h -1 -W -Q "SET NOCOUNT ON; $1" </dev/null) \
    || { echo "ERROR: falló la consulta en $SERVIDOR [$BASE]" >&2; return 1; }
  printf '%s\n' "$salida_sql" | tr -d '\r' | sed '/^$/d'
}

# bcp contra una tabla del esquema: bcp_tabla <tabla> in|out <archivo> [opciones extra]. -n formato nativo; -q activa
# QUOTED_IDENTIFIER (lo exigen los índices filtrados al cargar). Con -q el nombre va sin corchetes y completo
# (base.esquema.tabla): bcp lo cita solo, y los tres nombres ya se validaron como identificadores simples.
bcp_tabla() {
  tabla=$1; sentido=$2; archivo=$3; shift 3
  # shellcheck disable=SC2086
  bcp "$BASE.$ESQUEMA.$tabla" "$sentido" "$archivo" -n -q -S "$SERVIDOR" -U "$USUARIO" -P "$CLAVE" $CERT_BCP "$@" </dev/null
}

# Tablas del esquema, una por línea, ordenadas por nombre.
listar_tablas() {
  sql "SELECT t.name FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = N'$ESQUEMA' AND t.is_ms_shipped = 0 ORDER BY t.name;"
}

# Columnas de las tablas del esquema: tabla|columna|tipo|largo|precisión|escala|precisión de fecha|nulable, en orden.
# Compara origen y destino (un DECIMAL(18,2) frente a DECIMAL(18,4) o un DATETIMEOFFSET(7) frente a (3) cuenta como
# esquema distinto: bcp nativo no convierte entre ellos sin perder datos).
listar_columnas() {
  sql "SELECT c.TABLE_NAME + '|' + c.COLUMN_NAME + '|' + c.DATA_TYPE + '|' + ISNULL(CAST(c.CHARACTER_MAXIMUM_LENGTH AS varchar(12)), '') + '|' + ISNULL(CAST(c.NUMERIC_PRECISION AS varchar(4)), '') + '|' + ISNULL(CAST(c.NUMERIC_SCALE AS varchar(4)), '') + '|' + ISNULL(CAST(c.DATETIME_PRECISION AS varchar(4)), '') + '|' + c.IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS c JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME WHERE c.TABLE_SCHEMA = N'$ESQUEMA' AND t.TABLE_TYPE = 'BASE TABLE' ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION;"
}
