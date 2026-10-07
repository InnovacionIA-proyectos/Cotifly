#!/bin/sh
# =============================================================================
# Verifica los manifiestos de Swarm del backend .NET (HU-006.6, #30, ADR-013):
#
#   sh backend-dotnet/scripts/verificar-manifiestos-dotnet.sh
#
# Comprueba, con `docker compose config` (no hace falta Swarm ni desplegar nada):
#   1. Que cada manifiesto (staging y producción) FALLA sin las variables obligatorias
#      (${VAR:?...}), listando cada una que falta.
#   2. Que cada manifiesto VALIDA con las variables obligatorias puestas (valores
#      falsos, solo para resolver la interpolación).
#   3. Que la configuración resuelta trae las diferencias documentadas entre
#      staging y producción: orden de actualización (stop-first / start-first),
#      puerto publicado en modo host solo en staging, y CosteoNexo__Habilitado
#      (false en staging mientras la base sea compartida; true en producción).
#
# Sale con 0 si todo coincide con lo documentado en los propios manifiestos y en
# la PR #30; con 1 en la primera comprobación que no coincida (mensaje en stderr).
# Solo usa sh, docker compose y grep.
# =============================================================================
set -eu

raiz=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
cd "$raiz"

fallo() {
  echo "FALLO: $1" >&2
  exit 1
}

verificar_falla_sin_variables() {
  manifiesto=$1
  if docker compose -f "$manifiesto" config >/tmp/qa-manifiesto-sin-vars.$$ 2>&1; then
    fallo "$manifiesto validó sin las variables obligatorias (debería fallar)."
  fi
  for var in COTIFLY_TAG ConnectionStrings__Cotifly ConnectionStrings__Identidad \
             Cors__OrigenesPermitidos__0 ForwardedHeaders__RedesConocidas__0 \
             Jwt__SigningKey PUBLIC_HOST; do
    grep -q "falta $var" /tmp/qa-manifiesto-sin-vars.$$ \
      || fallo "$manifiesto no reportó la variable obligatoria faltante: $var."
  done
  rm -f /tmp/qa-manifiesto-sin-vars.$$
  echo "OK: $manifiesto falla explícitamente sin las variables obligatorias."
}

resolver_con_variables() {
  manifiesto=$1
  destino=$2
  COTIFLY_TAG=qa-verificacion \
  ConnectionStrings__Cotifly="Server=x;Database=y" \
  ConnectionStrings__Identidad="Server=x;Database=z" \
  Cors__OrigenesPermitidos__0="https://qa.example.com" \
  ForwardedHeaders__RedesConocidas__0="10.0.0.0/24" \
  Jwt__SigningKey="qa-verificacion-0000000000000000000000000000" \
  PUBLIC_HOST="qa.example.com" \
  docker compose -f "$manifiesto" config >"$destino" \
    || fallo "$manifiesto no validó con todas las variables obligatorias puestas."
  echo "OK: $manifiesto valida con las variables obligatorias puestas."
}

verificar_valor() {
  archivo=$1
  patron=$2
  descripcion=$3
  grep -q "$patron" "$archivo" || fallo "$descripcion (no se encontró '$patron' en $archivo)."
  echo "OK: $descripcion."
}

verificar_falla_sin_variables infra/stack.dotnet.staging.yml
verificar_falla_sin_variables infra/stack.dotnet.dokploy.yml

resolver_con_variables infra/stack.dotnet.staging.yml /tmp/qa-staging-config.$$.yml
resolver_con_variables infra/stack.dotnet.dokploy.yml /tmp/qa-dokploy-config.$$.yml

verificar_valor /tmp/qa-staging-config.$$.yml "order: stop-first" "staging actualiza con stop-first"
verificar_valor /tmp/qa-staging-config.$$.yml 'published: "8092"' "staging publica el puerto 8092 en modo host"
verificar_valor /tmp/qa-staging-config.$$.yml 'CosteoNexo__Habilitado: "false"' "staging trae CosteoNexo__Habilitado=false (base compartida)"

verificar_valor /tmp/qa-dokploy-config.$$.yml "order: start-first" "producción actualiza con start-first"
verificar_valor /tmp/qa-dokploy-config.$$.yml 'CosteoNexo__Habilitado: "true"' "producción trae CosteoNexo__Habilitado=true"
if grep -q "published:" /tmp/qa-dokploy-config.$$.yml; then
  fallo "producción no debería publicar ningún puerto (sin Traefik/TLS resuelto, HU-006.2)."
fi
echo "OK: producción no publica ningún puerto."

for archivo in /tmp/qa-staging-config.$$.yml /tmp/qa-dokploy-config.$$.yml; do
  # HU-013.9 (#105): Google Chat se retiró; ningún manifiesto trae GChat__*, el secreto gchat_sa, Chat__CreacionHabilitada ni la firma del PDF (Pdf__ClaveFirmaUrl).
  if grep -qE "GChat__|gchat_sa|Chat__CreacionHabilitada|Pdf__ClaveFirmaUrl|Pdf__MinutosUrlFirmada" "$archivo"; then
    fallo "$archivo todavía trae configuración de Google Chat o del chat legado (retirados en HU-013.9)."
  fi
  echo "OK: $archivo sin configuración de Google Chat."
  verificar_valor "$archivo" "ASPNETCORE_ENVIRONMENT: Production" "$archivo fija ASPNETCORE_ENVIRONMENT=Production"
done

rm -f /tmp/qa-staging-config.$$.yml /tmp/qa-dokploy-config.$$.yml

echo "Todos los manifiestos .NET (staging y producción) verificados correctamente."
