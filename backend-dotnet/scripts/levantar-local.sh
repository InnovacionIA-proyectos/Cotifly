#!/bin/sh
# =============================================================================
# Cotifly — entorno local de desarrollo sin docker-compose (ADR-007, #14). SOLO DESARROLLO.
#
# Levanta, con "docker run" y nombres propios (prefijo cotifly-local-):
#   1. Red          cotifly-local
#   2. SQL Server   cotifly-local-sql   (mcr.microsoft.com/mssql/server:2019-latest, volumen cotifly-local-sqldata,
#                                        puerto 14333 solo en 127.0.0.1 para SSMS/Azure Data Studio)
#   3. Esquema      con sqlcmd 18 (/opt/mssql-tools18, incluido en la misma imagen de SQL Server 2019):
#                     - bases dbTarifario_Nexo_Pruebas y dbsolucionesti            (scripts/sql/dev-01-bases.sql)
#                     - dbo_cotifly al esquema vigente (0001 a 0010, y luego 02, 03, pendientes/04, 05, 06, 07, 08, 09, 10, 11, 12, 13 y 14): base vacía -> db/scripts/01_crear_esquema.sql;
#                       esquema anterior a HU-021 (Cotizaciones) -> 0002 a 0010; ya migrada -> solo el 0010 (idempotente)
#                       y después los ajustes db/scripts/02_*.sql en adelante (el 02 del cubicador; se pueden repetir)
#                     - seeders de db/seeder/ (idempotentes: líneas de negocio, mapeo SAP, roles)
#                     - dbsolucionesti FALSA con usuarios y roles de prueba          (scripts/sql/dev-02-identidad.sql)
#                     - IDROLES de desarrollo en Roles y login cotifly_app con permisos mínimos
#                                                                                    (scripts/sql/dev-03-roles-y-login.sql)
#                     - verificación de esos permisos conectado como cotifly_app     (scripts/sql/dev-04-verificar-permisos.sql)
#   4. API          cotifly-local-api   (imagen cotifly-api:dev, --env-file, volumen cotifly-local-pdfs, 127.0.0.1:8080)
#                   y espera a que GET /health/ready responda 200.
#
# Uso (desde cualquier carpeta; Linux, macOS o Git Bash en Windows; en PowerShell usar levantar-local.ps1):
#   sh backend-dotnet/scripts/levantar-local.sh                 levanta (construye la imagen si no existe)
#   sh backend-dotnet/scripts/levantar-local.sh --construir     reconstruye cotifly-api:dev antes de levantar
#   sh backend-dotnet/scripts/levantar-local.sh --env-file RUTA usa ese archivo en lugar de backend-dotnet/.env.local
#   sh backend-dotnet/scripts/levantar-local.sh --bajar         borra los contenedores y la red (conserva los datos)
#   sh backend-dotnet/scripts/levantar-local.sh --bajar --borrar-datos   además borra los volúmenes cotifly-local-*
#
# Variables opcionales: COTIFLY_SQL_SA_PASSWORD, COTIFLY_SQL_APP_PASSWORD, COTIFLY_PUERTO_API (8080),
# COTIFLY_PUERTO_SQL (14333), COTIFLY_API_IMAGEN (cotifly-api:dev), COTIFLY_SQL_IMAGEN, COTIFLY_SQLCMD_IMAGEN y
# COTIFLY_SQLCMD_RUTA (por defecto la misma imagen de SQL Server con /opt/mssql-tools18/bin/sqlcmd).
# COTIFLY_SQL_APP_PASSWORD no admite comilla simple, comilla doble, punto y coma ni espacios (romperían el literal
# SQL del CREATE LOGIN o la cadena de conexión): el script lo valida antes de empezar.
#
# Los puertos se publican solo en 127.0.0.1 (no quedan expuestos a la red) y .env.local queda con permisos 600.
#
# El archivo de variables por defecto es backend-dotnet/.env.local (no se versiona: .gitignore ignora .env.*).
# Se genera la primera vez con las cadenas al SQL Server local y una clave JWT aleatoria; después no se
# sobrescribe, así se le pueden agregar variables (por ejemplo IA__Proveedor/IA__Modelo/IA__ApiKey). Para
# regenerarlo, borrarlo. Solo borra contenedores, red y volúmenes con prefijo cotifly-local-.
# Documentación: backend-dotnet/docs/06-ejecucion-local.md.
# =============================================================================
set -eu

# Git Bash (MSYS) convierte los argumentos que parecen rutas POSIX (/opt/..., /dev/stdin) en rutas de Windows
# antes de pasarlos a docker.exe: se desactiva, y las rutas del repo se toman en formato Windows (pwd -W) cuando
# existe. En Linux y macOS ambas cosas no tienen efecto.
export MSYS_NO_PATHCONV=1
ruta_absoluta() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }

DIR_SCRIPTS=$(ruta_absoluta "$(dirname "$0")")
DIR_BACKEND=$(ruta_absoluta "$DIR_SCRIPTS/..")
DIR_MIGRACIONES=$(ruta_absoluta "$DIR_BACKEND/../db/migrations_mssql")
DIR_DB=$(ruta_absoluta "$DIR_BACKEND/../db")

RED=cotifly-local
SQL_CONTENEDOR=cotifly-local-sql
API_CONTENEDOR=cotifly-local-api
VOLUMEN_SQL=cotifly-local-sqldata
VOLUMEN_PDF=cotifly-local-pdfs
IMAGEN_SQL=${COTIFLY_SQL_IMAGEN:-mcr.microsoft.com/mssql/server:2019-latest}
# sqlcmd 18: mcr.microsoft.com/mssql-tools18 no existe en MCR y mcr.microsoft.com/mssql-tools (v13, 2017) está
# obsoleta; la imagen de SQL Server 2019 ya trae /opt/mssql-tools18, así no se descarga nada más.
IMAGEN_SQLCMD=${COTIFLY_SQLCMD_IMAGEN:-$IMAGEN_SQL}
RUTA_SQLCMD=${COTIFLY_SQLCMD_RUTA:-/opt/mssql-tools18/bin/sqlcmd}
IMAGEN_API=${COTIFLY_API_IMAGEN:-cotifly-api:dev}
CLAVE_SA=${COTIFLY_SQL_SA_PASSWORD:-CotiflyLocal#2026}
CLAVE_APP=${COTIFLY_SQL_APP_PASSWORD:-CotiflyApp#2026}
PUERTO_API=${COTIFLY_PUERTO_API:-8080}
PUERTO_SQL=${COTIFLY_PUERTO_SQL:-14333}

construir=0
bajar=0
borrar_datos=0
archivo_env=""

while [ "$#" -gt 0 ]; do
  case "$1" in
    --construir) construir=1 ;;
    --bajar) bajar=1 ;;
    --borrar-datos) borrar_datos=1 ;;
    --env-file) shift; archivo_env=${1:?falta la ruta después de --env-file} ;;
    -h|--help) sed -n '2,44p' "$0"; exit 0 ;;
    *) echo "Opción desconocida: $1 (ver --help)" >&2; exit 2 ;;
  esac
  shift
done

paso() { printf '\n==> %s\n' "$1"; }

existe_contenedor() { docker container inspect "$1" >/dev/null 2>&1; }

# ----------------------------------------------------------------------------- bajar
if [ "$bajar" -eq 1 ]; then
  paso "Bajando el entorno local (solo recursos cotifly-local-*)"
  for c in "$API_CONTENEDOR" "$SQL_CONTENEDOR"; do
    if existe_contenedor "$c"; then docker rm -f "$c" >/dev/null && echo "contenedor $c borrado"; fi
  done
  if docker network inspect "$RED" >/dev/null 2>&1; then docker network rm "$RED" >/dev/null && echo "red $RED borrada"; fi
  if [ "$borrar_datos" -eq 1 ]; then
    for v in "$VOLUMEN_SQL" "$VOLUMEN_PDF"; do
      if docker volume inspect "$v" >/dev/null 2>&1; then docker volume rm "$v" >/dev/null && echo "volumen $v borrado"; fi
    done
  fi
  exit 0
fi

# sqlcmd desde la imagen de herramientas, en la red local. El script entra por stdin (sin montar carpetas:
# funciona igual en Git Bash, Linux y dentro de otro contenedor). -b corta ante error, -r0 manda los errores a stderr
# (la salida normal se descarta), -I activa
# QUOTED_IDENTIFIER (índices filtrados). Los scripts están en UTF-8, la codificación por defecto de sqlcmd en Linux.
# Los argumentos extra son opciones de "docker run": las variables de sqlcmd van como variables de entorno
# (-e Nombre=valor), que sqlcmd lee como $(Nombre); así una contraseña con símbolos no rompe su línea de comandos.
# -C confía en el certificado autofirmado del SQL Server local (sqlcmd 18 cifra por defecto).
# USUARIO_SQL y CLAVE_SQL: sa, salvo en la verificación de permisos, que corre como cotifly_app.
USUARIO_SQL=sa
CLAVE_SQL=$CLAVE_SA
sqlcmd_archivo() {
  base=$1
  archivo=$2
  shift 2
  docker run --rm -i --network "$RED" "$@" --entrypoint "$RUTA_SQLCMD" "$IMAGEN_SQLCMD" \
    -S "$SQL_CONTENEDOR" -U "$USUARIO_SQL" -P "$CLAVE_SQL" -C -d "$base" -b -I -r0 -i /dev/stdin < "$archivo" >/dev/null
}

# Valor escalar de una consulta (sin encabezados ni espacios). Falla si sqlcmd falla.
sqlcmd_escalar() {
  salida=$(docker run --rm --network "$RED" --entrypoint "$RUTA_SQLCMD" "$IMAGEN_SQLCMD" \
    -S "$SQL_CONTENEDOR" -U sa -P "$CLAVE_SA" -C -d "$1" -b -h -1 -W -Q "SET NOCOUNT ON; $2") || return 1
  printf '%s' "$salida" | tr -d '\r '
}

# La contraseña de cotifly_app va dentro de un literal N'...' (CREATE LOGIN) y de una cadena de conexión.
case "$CLAVE_APP" in
  *\'*|*\"*|*\;*|*' '*|'')
    echo "COTIFLY_SQL_APP_PASSWORD no puede estar vacía ni contener comillas, punto y coma o espacios." >&2
    exit 2 ;;
esac

# ----------------------------------------------------------------------------- red y SQL Server
paso "Red $RED"
docker network inspect "$RED" >/dev/null 2>&1 || docker network create "$RED" >/dev/null
echo "lista"

paso "SQL Server 2019 ($SQL_CONTENEDOR, puerto del host $PUERTO_SQL)"
if existe_contenedor "$SQL_CONTENEDOR"; then
  docker start "$SQL_CONTENEDOR" >/dev/null
  echo "ya existía: iniciado"
else
  docker run -d --name "$SQL_CONTENEDOR" --network "$RED" \
    -e ACCEPT_EULA=Y -e MSSQL_PID=Developer -e "MSSQL_SA_PASSWORD=$CLAVE_SA" \
    -p "127.0.0.1:$PUERTO_SQL:1433" -v "$VOLUMEN_SQL:/var/opt/mssql" "$IMAGEN_SQL" >/dev/null
  echo "creado"
fi

printf 'esperando a SQL Server'
intentos=0
until sqlcmd_escalar master "SELECT 1" >/dev/null 2>&1; do
  intentos=$((intentos + 1))
  if [ "$intentos" -ge 60 ]; then
    echo; echo "SQL Server no respondió en 2 minutos. Logs:" >&2
    docker logs --tail 30 "$SQL_CONTENEDOR" >&2
    exit 1
  fi
  printf '.'
  sleep 2
done
echo " listo"

# ----------------------------------------------------------------------------- esquema y datos
paso "Bases, esquema dbo_cotifly y dbsolucionesti de desarrollo"
sqlcmd_archivo master "$DIR_SCRIPTS/sql/dev-01-bases.sql"
# Esquema vigente (HU-021, ADR-019): 0001 a 0010. Se puede correr las veces que haga falta:
#   - dbo_cotifly sin tablas -> instalación desde cero (db/scripts/01_crear_esquema.sql, equivale a 0001-0010);
#   - con el esquema anterior (tabla Cotizaciones, p. ej. un volumen de antes de HU-021) -> migraciones 0002 a 0010
#     (el 0003 y el 0004 exigen Cotizaciones, por eso no se repiten sobre una base ya migrada);
#   - ya migrada (CotizacionesSolicitud) -> solo el 0010, idempotente.
# Después van los ajustes db/scripts/02_*.sql en adelante (hoy el 02 del cubicador, HU-023.3; el 03 del orden del camión y el id del
# catálogo del conexo y el 05 de la categoría del conexo, HU-014 #188: el backend mapea sus columnas, sin ellos /health/ready da 503
# "falta 03" o "falta 05"; el 07 de BUSQUEDA_AGOTADA, HU-023 #176; el 08 que retira las vistas del borrador de métricas, HU-014.3 #152;
# el 09 de scorePrioridad, HU-018.1 #155, el 10 del rechazo de la línea, HU-028.1 #183, y el 11 del peso con tara, HU-030.1 #184: el
# backend mapea sus columnas, sin ellos /health/ready da 503 "falta 09", "falta 10" o "falta 11"), en orden de nombre,
# igual que db/ejecutar-bd.*: cada uno revisa si su cambio ya está, así que se pueden repetir.
# De db/scripts/pendientes/ solo entra el 04_cubicacion_segundo_plano.sql (HU-023.5, el backend mapea sus columnas: sin él
# /health/ready da 503 "falta 04"), intercalado por nombre: queda 02, 03, pendientes/04, 05, 06, 07, 08, 09, 10, 11, 12, 13. El resto de pendientes/ no se corre.
tablas=$(sqlcmd_escalar dbTarifario_Nexo_Pruebas "SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly'")
viejo=$(sqlcmd_escalar dbTarifario_Nexo_Pruebas "SELECT CASE WHEN OBJECT_ID(N'dbo_cotifly.Cotizaciones', N'U') IS NULL THEN 0 ELSE 1 END")
if [ "$tablas" = "0" ]; then
  echo "aplicando db/scripts/01_crear_esquema.sql (instalación desde cero, 0001 a 0010)"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_DB/scripts/01_crear_esquema.sql"
elif [ "$viejo" = "1" ]; then
  echo "dbo_cotifly tiene el esquema anterior a HU-021: se aplican 0002 a 0010"
  for script in 0002_identidad_estado_agente.sql 0003_metricas_reenviadas.sql 0004_costeo_nexo.sql                 0005_lineas_solicitud.sql 0006_textos_y_concurrencia.sql 0007_captura_nexo_campos.sql                 0008_horas_por_linea.sql 0009_reestructuracion_cotizaciones.sql 0010_nombres_municipio.sql; do
    echo "aplicando $script"
    sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_MIGRACIONES/$script"
  done
else
  echo "dbo_cotifly ya está migrado: se aplica solo el 0010 (idempotente)"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_MIGRACIONES/0010_nombres_municipio.sql"
fi
pendiente04="$DIR_DB/scripts/pendientes/04_cubicacion_segundo_plano.sql"
aplicar_pendiente04() {
  [ -n "$pendiente04" ] || return 0
  echo "aplicando db/scripts/pendientes/04_cubicacion_segundo_plano.sql (ajuste, se puede repetir)"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$pendiente04"
  pendiente04=""
}
for ajuste in "$DIR_DB"/scripts/[0-9][0-9]_*.sql; do
  [ -f "$ajuste" ] || continue
  nombre=$(basename "$ajuste")
  case "$nombre" in 01_*) continue ;; esac
  # el pendientes/04 va por orden de nombre: antes del primer ajuste posterior a "04_"
  if [ "$nombre" \> "04_cubicacion_segundo_plano.sql" ]; then aplicar_pendiente04; fi
  echo "aplicando db/scripts/$nombre (ajuste, se puede repetir)"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$ajuste"
done
aplicar_pendiente04
for seeder in 01_lineas_negocio.sql 02_sap_mapeo_trqtype.sql 03_roles.sql; do
  echo "sembrando $seeder"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_DB/seeder/$seeder"
done
echo "aplicando dbsolucionesti de desarrollo"
sqlcmd_archivo dbsolucionesti "$DIR_SCRIPTS/sql/dev-02-identidad.sql"
echo "aplicando IDROLES de desarrollo y login cotifly_app"
sqlcmd_archivo master "$DIR_SCRIPTS/sql/dev-03-roles-y-login.sql" -e "ClaveApp=$CLAVE_APP"
echo "verificando los permisos de cotifly_app (debe poder usar dbo_cotifly y nada más)"
USUARIO_SQL=cotifly_app
CLAVE_SQL=$CLAVE_APP
sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_SCRIPTS/sql/dev-04-verificar-permisos.sql"
USUARIO_SQL=sa
CLAVE_SQL=$CLAVE_SA
echo "permisos verificados"

# ----------------------------------------------------------------------------- imagen y variables
paso "Imagen $IMAGEN_API"
if [ "$construir" -eq 1 ] || ! docker image inspect "$IMAGEN_API" >/dev/null 2>&1; then
  docker build -t "$IMAGEN_API" -f "$DIR_BACKEND/Dockerfile" "$DIR_BACKEND"
else
  echo "se usa la existente (--construir para reconstruirla)"
fi

if [ -z "$archivo_env" ]; then
  archivo_env="$DIR_BACKEND/.env.local"
  if [ ! -f "$archivo_env" ]; then
    paso "Generando $archivo_env"
    clave_jwt=$(head -c 48 /dev/urandom | base64 | tr -d '\n')
    cat > "$archivo_env" <<EOF
# Generado por scripts/levantar-local.sh (SOLO DESARROLLO; no se versiona). Borrarlo para regenerarlo.
# Se le pueden agregar variables de .env.example (por ejemplo IA__Proveedor, IA__Modelo, IA__ApiKey).
ASPNETCORE_ENVIRONMENT=Development
Zona__ZonaHoraria=America/Bogota
ConnectionStrings__Cotifly=Server=$SQL_CONTENEDOR,1433;Database=dbTarifario_Nexo_Pruebas;User Id=cotifly_app;Password=$CLAVE_APP;Encrypt=True;TrustServerCertificate=True
ConnectionStrings__Identidad=Server=$SQL_CONTENEDOR,1433;Database=dbsolucionesti;User Id=cotifly_app;Password=$CLAVE_APP;Encrypt=True;TrustServerCertificate=True
Jwt__SigningKey=$clave_jwt
Cors__OrigenesPermitidos__0=http://localhost:4200
Pdf__BaseUrl=http://localhost:$PUERTO_API
Correo__Habilitado=false
EOF
    # Tiene la contraseña de cotifly_app y la clave JWT: solo para el usuario actual.
    chmod 600 "$archivo_env"
  fi
fi
[ -f "$archivo_env" ] || { echo "No existe el archivo de variables $archivo_env" >&2; exit 1; }

# ----------------------------------------------------------------------------- API
paso "API ($API_CONTENEDOR, http://localhost:$PUERTO_API)"
if existe_contenedor "$API_CONTENEDOR"; then docker rm -f "$API_CONTENEDOR" >/dev/null; fi
docker run -d --name "$API_CONTENEDOR" --network "$RED" -p "127.0.0.1:$PUERTO_API:8080" \
  --env-file "$archivo_env" -v "$VOLUMEN_PDF:/data/pdfs" "$IMAGEN_API" >/dev/null

printf 'esperando a /health/ready'
intentos=0
until [ "$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$PUERTO_API/health/ready" || true)" = "200" ]; do
  intentos=$((intentos + 1))
  if [ "$intentos" -ge 45 ]; then
    echo; echo "La API no quedó lista en 90 s. Logs:" >&2
    docker logs --tail 40 "$API_CONTENEDOR" >&2
    exit 1
  fi
  printf '.'
  sleep 2
done
echo " lista"

cat <<EOF

Entorno local listo.
  API          http://localhost:$PUERTO_API   (GET /health, GET /health/ready)
  SQL Server   127.0.0.1,$PUERTO_SQL   usuario sa (COTIFLY_SQL_SA_PASSWORD) o cotifly_app
  Variables    $archivo_env
  Logs         docker logs -f $API_CONTENEDOR

Login de prueba (usuarios en scripts/sql/dev-02-identidad.sql):
  curl -s -X POST http://localhost:$PUERTO_API/api/auth/login-clientepro -H "Content-Type: application/json" -d '{"idUsuario":"APEREZ"}'

Bajar:  sh backend-dotnet/scripts/levantar-local.sh --bajar  [--borrar-datos]
EOF
