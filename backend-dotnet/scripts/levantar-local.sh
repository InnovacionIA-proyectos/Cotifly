#!/bin/sh
# =============================================================================
# Cotifly — entorno local de desarrollo sin docker-compose (ADR-007, #14). SOLO DESARROLLO.
#
# Levanta, con "docker run" y nombres propios (prefijo cotifly-local-):
#   1. Red          cotifly-local
#   2. SQL Server   cotifly-local-sql   (mcr.microsoft.com/mssql/server:2019-latest, volumen cotifly-local-sqldata,
#                                        puerto del host 14333 para SSMS/Azure Data Studio)
#   3. Esquema      con sqlcmd de la imagen mcr.microsoft.com/mssql-tools:
#                     - bases dbTarifario_Nexo_Pruebas y dbsolucionesti            (scripts/sql/dev-01-bases.sql)
#                     - migrations_mssql/0001 (solo si dbo_cotifly no existe), 0002 y 0003 (idempotentes)
#                     - dbsolucionesti FALSA con usuarios y roles de prueba          (scripts/sql/dev-02-identidad.sql)
#                     - IDROLES de desarrollo en Roles y login cotifly_app con permisos mínimos
#                                                                                    (scripts/sql/dev-03-roles-y-login.sql)
#   4. API          cotifly-local-api   (imagen cotifly-api:dev, --env-file, volumen cotifly-local-pdfs, puerto 8080)
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
# COTIFLY_PUERTO_SQL (14333), COTIFLY_API_IMAGEN (cotifly-api:dev), COTIFLY_SQL_IMAGEN, COTIFLY_SQLCMD_IMAGEN.
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
DIR_MIGRACIONES=$(ruta_absoluta "$DIR_BACKEND/../migrations_mssql")

RED=cotifly-local
SQL_CONTENEDOR=cotifly-local-sql
API_CONTENEDOR=cotifly-local-api
VOLUMEN_SQL=cotifly-local-sqldata
VOLUMEN_PDF=cotifly-local-pdfs
IMAGEN_SQL=${COTIFLY_SQL_IMAGEN:-mcr.microsoft.com/mssql/server:2019-latest}
IMAGEN_SQLCMD=${COTIFLY_SQLCMD_IMAGEN:-mcr.microsoft.com/mssql-tools:latest}
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
    -h|--help) sed -n '2,36p' "$0"; exit 0 ;;
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
sqlcmd_archivo() {
  base=$1
  archivo=$2
  shift 2
  docker run --rm -i --network "$RED" "$@" "$IMAGEN_SQLCMD" /opt/mssql-tools/bin/sqlcmd \
    -S "$SQL_CONTENEDOR" -U sa -P "$CLAVE_SA" -d "$base" -b -I -r0 -i /dev/stdin < "$archivo" >/dev/null
}

# Valor escalar de una consulta (sin encabezados ni espacios). Falla si sqlcmd falla.
sqlcmd_escalar() {
  salida=$(docker run --rm --network "$RED" "$IMAGEN_SQLCMD" /opt/mssql-tools/bin/sqlcmd \
    -S "$SQL_CONTENEDOR" -U sa -P "$CLAVE_SA" -d "$1" -b -h -1 -W -Q "SET NOCOUNT ON; $2") || return 1
  printf '%s' "$salida" | tr -d '\r '
}

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
    -p "$PUERTO_SQL:1433" -v "$VOLUMEN_SQL:/var/opt/mssql" "$IMAGEN_SQL" >/dev/null
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
if [ "$(sqlcmd_escalar dbTarifario_Nexo_Pruebas "SELECT COUNT(*) FROM sys.schemas WHERE name = 'dbo_cotifly'")" = "0" ]; then
  echo "aplicando 0001_script_bd_cotifly_sqlserver.sql"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_MIGRACIONES/0001_script_bd_cotifly_sqlserver.sql"
else
  # Después del 0002 el 0001 no se re-ejecuta (ver encabezado del 0002 y ADR-008).
  echo "dbo_cotifly ya existe: se omite el 0001"
fi
for script in 0002_identidad_estado_agente.sql 0003_metricas_reenviadas.sql; do
  echo "aplicando $script"
  sqlcmd_archivo dbTarifario_Nexo_Pruebas "$DIR_MIGRACIONES/$script"
done
echo "aplicando dbsolucionesti de desarrollo"
sqlcmd_archivo dbsolucionesti "$DIR_SCRIPTS/sql/dev-02-identidad.sql"
echo "aplicando IDROLES de desarrollo y login cotifly_app"
sqlcmd_archivo master "$DIR_SCRIPTS/sql/dev-03-roles-y-login.sql" -e "ClaveApp=$CLAVE_APP"

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
  fi
fi
[ -f "$archivo_env" ] || { echo "No existe el archivo de variables $archivo_env" >&2; exit 1; }

# ----------------------------------------------------------------------------- API
paso "API ($API_CONTENEDOR, http://localhost:$PUERTO_API)"
if existe_contenedor "$API_CONTENEDOR"; then docker rm -f "$API_CONTENEDOR" >/dev/null; fi
docker run -d --name "$API_CONTENEDOR" --network "$RED" -p "$PUERTO_API:8080" \
  --env-file "$archivo_env" -v "$VOLUMEN_PDF:/data/pdfs" "$IMAGEN_API" >/dev/null

printf 'esperando a /health/ready'
intentos=0
until [ "$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$PUERTO_API/health/ready" || true)" = "200" ]; do
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
  SQL Server   localhost,$PUERTO_SQL   usuario sa (COTIFLY_SQL_SA_PASSWORD) o cotifly_app
  Variables    $archivo_env
  Logs         docker logs -f $API_CONTENEDOR

Login de prueba (usuarios en scripts/sql/dev-02-identidad.sql):
  curl -s -X POST http://localhost:$PUERTO_API/api/auth/login-clientepro -H "Content-Type: application/json" -d '{"idUsuario":"APEREZ"}'

Bajar:  sh backend-dotnet/scripts/levantar-local.sh --bajar  [--borrar-datos]
EOF
