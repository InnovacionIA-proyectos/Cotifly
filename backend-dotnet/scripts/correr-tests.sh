#!/bin/sh
# =============================================================================
# Corre la suite de pruebas de Cotifly por área funcional, o completa, con los mismos
# comandos documentados en docs/08-testing.md §8.4 (MTP: --filter-class, --filter-trait,
# --ignore-exit-code 8). Pensado para que cualquiera del equipo (no solo QA) pueda
# corroborar en su máquina lo que ya corrió el CI o una sesión de QA, sin memorizar
# los filtros exactos de cada PR.
#
# Uso (desde backend-dotnet/):
#   sh scripts/correr-tests.sh listar
#   sh scripts/correr-tests.sh area <nombre> [--integracion] [--no-build]
#   sh scripts/correr-tests.sh suite [--integracion] [--no-build]
#   sh scripts/correr-tests.sh todo [--no-build]
#
# Ejemplos:
#   sh scripts/correr-tests.sh area conexos
#   sh scripts/correr-tests.sh area licitaciones --integracion
#   sh scripts/correr-tests.sh suite --integracion
#   sh scripts/correr-tests.sh todo
#
# --integracion necesita Docker (Testcontainers levanta SQL Server). Sin ella, corre
# solo las pruebas sin la categoría Integracion ni sap (equivalente al job "unitarias"
# del CI). --no-build omite "dotnet build -c Release" (asume que ya está compilado).
#
# Resultados: un .trx por proyecto en TestResults/<area-o-suite>-<unitarias|integracion>/,
# igual que el resto de comandos de este documento (TestResults/ está en .gitignore).
#
# Agregar un área nueva: sumar un "case" en la función patrones_de() de abajo con los
# patrones de --filter-class de esa área (uno o más; el script corre uno por patrón).
# =============================================================================
set -eu

cd "$(dirname "$0")/.."

patrones_de() {
  case "$1" in
    conexos)              echo '*ServiciosConexos*' ;;
    usd-trm)               printf '%s\n' '*EnvioCosteoService*' '*PdfCosteoMoneda*' '*FijacionMoneda*' ;;
    redactor-ia)            printf '%s\n' '*RedactorCotizacionLlm*' '*TextosCosteoService*' ;;
    asistente-folio)        echo '*AsistenteFolio*' ;;
    retiro-google-chat)     echo '*RetiroGoogleChat*' ;;
    pdf-concurrencia)       printf '%s\n' '*GeneradorPdfMigraDoc*' '*MaquetacionPdf*' ;;
    licitaciones)           printf '%s\n' '*ValidadorFilasLicitacion*' '*LicitacionesCaptura*' '*CapturaFilasLicitacion*' ;;
    *) return 1 ;;
  esac
}

listar_areas() {
  echo "Áreas disponibles (definidas en patrones_de() de este script):"
  echo "  conexos              — HU-013.3, servicios conexos desde el catálogo de Nexo (#113)"
  echo "  usd-trm              — HU-013.7, USD en el formulario de costeo con TRM fijada (#114)"
  echo "  redactor-ia          — HU-013.5, redactor IA del asunto e introducción del PDF (#115)"
  echo "  asistente-folio      — HU-013.6, asistente IA por folio (#116)"
  echo "  retiro-google-chat   — HU-013.9, retiro de Google Chat y del agente legado (#120)"
  echo "  pdf-concurrencia     — render de PDF serializado, bug de concurrencia (#92, #95, #121)"
  echo "  licitaciones         — HU-013.11, validación de filas de licitación (#122)"
}

uso() {
  echo "uso: $0 listar" >&2
  echo "     $0 area <nombre> [--integracion] [--no-build]" >&2
  echo "     $0 suite [--integracion] [--no-build]" >&2
  echo "     $0 todo [--no-build]" >&2
  exit 2
}

sin_build=0
con_integracion=0
comando=${1:-}
[ "$#" -gt 0 ] && shift || true

case "$comando" in
  listar) listar_areas; exit 0 ;;
  area) nombre=${1:-}; [ -n "$nombre" ] || uso; shift || true ;;
  suite) nombre='' ;;
  todo) nombre='' ;;
  *) uso ;;
esac

for arg in "$@"; do
  case "$arg" in
    --integracion) con_integracion=1 ;;
    --no-build) sin_build=1 ;;
    *) echo "opción desconocida: $arg" >&2; uso ;;
  esac
done

if [ "$sin_build" -eq 0 ]; then
  echo "== dotnet build -c Release =="
  dotnet build -c Release
fi

correr() {
  # correr <etiqueta-resultados> <--filter-trait-args...>
  etiqueta=$1
  shift
  carpeta="TestResults/$etiqueta"
  echo "== dotnet test ($etiqueta) =="
  dotnet test --solution Cotifly.sln -c Release --no-build "$@" --ignore-exit-code 8 \
    --results-directory "$carpeta" --report-trx
}

correr_area() {
  nombre=$1
  sufijo=$2 # unitarias | integracion
  shift 2
  patrones=$(patrones_de "$nombre") || { echo "área desconocida: $nombre" >&2; listar_areas >&2; exit 2; }
  echo "$patrones" | while IFS= read -r patron; do
    [ -n "$patron" ] || continue
    correr "area-$nombre-$sufijo" "$@" --filter-class "$patron"
  done
}

if [ -n "$comando" ] && [ "$comando" = "area" ]; then
  if [ "$con_integracion" -eq 1 ]; then
    correr_area "$nombre" integracion --filter-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
  else
    correr_area "$nombre" unitarias --filter-not-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
  fi
elif [ "$comando" = "suite" ]; then
  if [ "$con_integracion" -eq 1 ]; then
    correr "suite-integracion" --filter-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
  else
    correr "suite-unitarias" --filter-not-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
  fi
elif [ "$comando" = "todo" ]; then
  correr "suite-unitarias" --filter-not-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
  correr "suite-integracion" --filter-trait "Categoria=Integracion" --filter-not-trait "Categoria=sap"
fi

echo "Listo. Resultados (.trx) en TestResults/ (no versionado)."
