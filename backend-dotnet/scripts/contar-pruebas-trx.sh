#!/bin/sh
# =============================================================================
# Suma las pruebas ejecutadas en los TRX de una carpeta y falla si son menos que un mínimo (#14).
#
#   sh scripts/contar-pruebas-trx.sh <carpeta-con-trx> <minimo>
#   sh scripts/contar-pruebas-trx.sh TestResults/unitarias 1300
#
# Motivo: el CI corre "dotnet test --ignore-exit-code 8" porque un filtro por trait deja proyectos con cero
# pruebas (código 8 de MTP). Ese código también taparía un filtro mal escrito que no descubra NINGUNA prueba:
# este control lo detecta. Lee el atributo executed de <Counters> de cada .trx (formato de
# Microsoft.Testing.Extensions.TrxReport). Sale con 1 si el total es menor que el mínimo y con 2 si no hay TRX.
# =============================================================================
set -eu

if [ "$#" -ne 2 ]; then
  echo "uso: $0 <carpeta-con-trx> <minimo>" >&2
  exit 2
fi

carpeta=$1
minimo=$2

total=0
archivos=0
for trx in "$carpeta"/*.trx; do
  [ -f "$trx" ] || continue
  archivos=$((archivos + 1))
  n=$(grep -o '<Counters [^>]*' "$trx" | head -n 1 | sed -E 's/.* executed="([0-9]+)".*/\1/')
  case "$n" in ''|*[!0-9]*) n=0 ;; esac
  echo "$(basename "$trx"): $n"
  total=$((total + n))
done

if [ "$archivos" -eq 0 ]; then
  echo "No hay archivos .trx en $carpeta." >&2
  exit 2
fi

echo "Pruebas ejecutadas: $total (mínimo $minimo)"
if [ "$total" -lt "$minimo" ]; then
  echo "Se ejecutaron menos pruebas que el mínimo: revisar los filtros de dotnet test." >&2
  exit 1
fi
