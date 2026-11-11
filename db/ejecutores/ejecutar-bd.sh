#!/usr/bin/env bash
# Instala o pone al día la base de datos de Cotifly: esquema dbo_cotifly, ajustes y configuración inicial.
# Es la versión para Linux/contenedores de ejecutar-bd.ps1 (misma lógica, mismos pasos).
#
# Uso:
#   ./ejecutar-bd.sh -s <servidor> -d <base> [-u <usuario>] [-p <clave>] [-c] [-e] [-r] [-a] [-y]
#     -s  servidor (ej. localhost,1433)              -d  base de datos (ej. dbCotifly)
#     -u  usuario de SQL Server (sin -u: autenticación de Windows, solo sirve fuera de Linux/Docker)
#     -p  contraseña (o variable SQLCMDPASSWORD)     -c  crea la base si no existe
#     -e  solo el esquema y sus ajustes (sin seeders) -r  solo los seeders (el esquema ya existe)
#     -a  se acepta por compatibilidad (docker-compose): el ejecutador ya decide solo si instala el esquema
#     -y  no pedir confirmación
#
# Pasos, deteniéndose en el primer error (rutas relativas a db/, la carpeta padre de este archivo):
#   (1) crear la base (-c);
#   (2) esquema/cotifly_esquema.sql, SOLO si dbo_cotifly no tiene tablas (base nueva); si ya tiene, se salta;
#   (3) scripts/*.sql (02 en adelante), ajustes de las bases existentes en orden de nombre: idempotentes, sobre
#       una base recién instalada no cambian nada;
#   (4) seeder/*.sql en orden de nombre (idempotentes).
# No corre scripts/operacion/. No hay servidor ni base por defecto, a propósito.
set -euo pipefail

DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
RAIZ_DB="$(cd "$DIR/.." && pwd)"
SERVIDOR="" BASE="" USUARIO="" CREAR=0 SOLO_ESQUEMA=0 SOLO_SEEDER=0 SI=0

while getopts "s:d:u:p:ceray" opt; do
  case $opt in
    s) SERVIDOR=$OPTARG ;;  d) BASE=$OPTARG ;;  u) USUARIO=$OPTARG ;;  p) export SQLCMDPASSWORD=$OPTARG ;;
    c) CREAR=1 ;;  e) SOLO_ESQUEMA=1 ;;  r) SOLO_SEEDER=1 ;;  a) ;;  y) SI=1 ;;
    *) echo "Opción no válida. Vea el encabezado de este archivo." >&2; exit 2 ;;
  esac
done

[[ -n $SERVIDOR && -n $BASE ]] || { echo "Faltan -s <servidor> y -d <base>." >&2; exit 2; }
[[ $BASE =~ ^[A-Za-z0-9_]+$ ]] || { echo "Nombre de base no válido: '$BASE'." >&2; exit 2; }
(( SOLO_ESQUEMA && SOLO_SEEDER )) && { echo "Use -e o -r, no los dos a la vez." >&2; exit 2; }
[[ -f $RAIZ_DB/esquema/cotifly_esquema.sql ]] || { echo "No existe $RAIZ_DB/esquema/cotifly_esquema.sql." >&2; exit 1; }

SQLCMD=$(command -v sqlcmd || true)
[[ -z $SQLCMD && -x /opt/mssql-tools18/bin/sqlcmd ]] && SQLCMD=/opt/mssql-tools18/bin/sqlcmd
[[ -z $SQLCMD && -x /opt/mssql-tools/bin/sqlcmd ]] && SQLCMD=/opt/mssql-tools/bin/sqlcmd
[[ -n $SQLCMD ]] || { echo "No se encontró sqlcmd." >&2; exit 1; }

if [[ -n $USUARIO ]]; then
  [[ -n ${SQLCMDPASSWORD:-} ]] || { read -r -s -p "Contraseña de $USUARIO: " SQLCMDPASSWORD; echo; export SQLCMDPASSWORD; }
  AUTH=(-U "$USUARIO")
else
  AUTH=(-E)
fi

if (( SOLO_SEEDER )); then PASOS="seeders"
elif (( SOLO_ESQUEMA )); then PASOS="esquema (si la base está vacía) + ajustes"
else PASOS="esquema (si la base está vacía) + ajustes + seeders"; fi

echo
echo "Base de datos de Cotifly"
echo "  Servidor : $SERVIDOR"
echo "  Base     : $BASE$( ((CREAR)) && echo '  (se crea si no existe)')"
echo "  Acceso   : ${USUARIO:-autenticación de Windows}"
echo "  Pasos    : $PASOS"
echo
if (( ! SI )); then
  read -r -p "Escriba SI para continuar: " respuesta
  [[ $respuesta == "SI" ]] || { echo "Cancelado, no se tocó nada."; exit 0; }
fi

# -C confía en el certificado, -I = QUOTED_IDENTIFIER ON, -b detiene el script en el primer error,
# -f 65001 lee los archivos como UTF-8 (los .sql llevan acentos).
COMUNES=(-S "$SERVIDOR" -C -I -b -f 65001 "${AUTH[@]}")
paso() { echo "-> $1"; }

if (( CREAR )); then
  paso "Crear la base $BASE si no existe"
  "$SQLCMD" "${COMUNES[@]}" -d master -Q "IF DB_ID(N'$BASE') IS NULL CREATE DATABASE [$BASE] COLLATE Latin1_General_100_CI_AS_SC_UTF8;"
fi

if (( ! SOLO_SEEDER )); then
  # Base nueva (dbo_cotifly sin tablas) -> esquema completo; base existente -> solo los ajustes.
  tablas=$("$SQLCMD" "${COMUNES[@]}" -d "$BASE" -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly';" | tr -d '[:space:]')
  [[ $tablas =~ ^[0-9]+$ ]] || { echo "No se pudo contar las tablas de dbo_cotifly en $BASE ($tablas)." >&2; exit 1; }
  if (( tablas == 0 )); then
    paso "Instalar el esquema dbo_cotifly (esquema/cotifly_esquema.sql)"
    "$SQLCMD" "${COMUNES[@]}" -d "$BASE" -i "$RAIZ_DB/esquema/cotifly_esquema.sql"
  else
    echo "dbo_cotifly ya tiene $tablas tablas: no se instala el esquema; se corren los ajustes."
  fi

  # Ajustes (02 en adelante): cada uno revisa si su cambio ya está, así que se pueden repetir.
  for archivo in "$RAIZ_DB"/scripts/*.sql; do
    [[ -f $archivo ]] || continue
    paso "Ajustar el esquema: scripts/$(basename "$archivo")"
    "$SQLCMD" "${COMUNES[@]}" -d "$BASE" -i "$archivo"
  done
fi

if (( ! SOLO_ESQUEMA )); then
  for archivo in "$RAIZ_DB"/seeder/*.sql; do
    paso "Sembrar: seeder/$(basename "$archivo")"
    "$SQLCMD" "${COMUNES[@]}" -d "$BASE" -i "$archivo"
  done
fi

echo
echo "Resumen"
"$SQLCMD" "${COMUNES[@]}" -d "$BASE" -Q "SET NOCOUNT ON; SELECT 'Tablas' AS Objeto, COUNT(*) AS Total FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id WHERE s.name = 'dbo_cotifly' UNION ALL SELECT 'Lineas de negocio', COUNT(*) FROM dbo_cotifly.MargenesPolitica UNION ALL SELECT 'Mapeos SAP TM', COUNT(*) FROM dbo_cotifly.SapMapeoTrqType UNION ALL SELECT 'Roles', COUNT(*) FROM dbo_cotifly.Roles UNION ALL SELECT 'Tasas del seguro', COUNT(*) FROM dbo_cotifly.TarifasSeguro UNION ALL SELECT 'Tramos de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramos UNION ALL SELECT 'Medidas de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidas UNION ALL SELECT 'Reglas de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroTramoMedidas UNION ALL SELECT 'Conexos de la politica', COUNT(*) FROM dbo_cotifly.PoliticaSeguroMedidaConexos;"
echo
echo "Listo."
