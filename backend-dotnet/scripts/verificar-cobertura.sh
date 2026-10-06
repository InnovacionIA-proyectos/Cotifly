#!/bin/sh
# =============================================================================
# Verifica el umbral de cobertura de líneas de un ensamblado en un reporte Cobertura (#14, ADR-010).
#
#   sh scripts/verificar-cobertura.sh <reporte.cobertura.xml> <ensamblado> <umbral-%>
#   sh scripts/verificar-cobertura.sh TestResults/cobertura/Cobertura.xml Cotifly.Dominio 90
#
# Lee el atributo line-rate del <package name="..."> (formato de ReportGenerator y de
# Microsoft.Testing.Extensions.CodeCoverage, en cualquier orden de atributos).
# Sale con 0 si la cobertura es >= umbral, 1 si es menor y 2 si el ensamblado no está en el reporte.
# Solo usa sh, grep, sed y awk (corre en el runner de CI y en la imagen del SDK).
# =============================================================================
set -eu

if [ "$#" -ne 3 ]; then
  echo "uso: $0 <reporte.cobertura.xml> <ensamblado> <umbral-%>" >&2
  exit 2
fi

reporte=$1
ensamblado=$2
umbral=$3

linea=$(grep -o "<package [^>]*name=\"$ensamblado\"[^>]*>" "$reporte" | head -n 1 || true)
if [ -z "$linea" ]; then
  echo "El ensamblado $ensamblado no aparece en $reporte." >&2
  exit 2
fi

tasa=$(printf '%s' "$linea" | sed -E 's/.*line-rate="([0-9.eE+-]+)".*/\1/')

awk -v tasa="$tasa" -v umbral="$umbral" -v nombre="$ensamblado" 'BEGIN {
  porcentaje = tasa * 100
  printf "Cobertura de líneas de %s: %.2f %% (umbral %s %%)\n", nombre, porcentaje, umbral
  if (porcentaje + 0.0000001 < umbral) { print "Por debajo del umbral."; exit 1 }
  exit 0
}'
