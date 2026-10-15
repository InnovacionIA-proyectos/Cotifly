#!/bin/sh
# =============================================================================
# Cotifly — construir, probar y publicar la imagen del backend .NET en el registry (HU-006.4, #28).
#
# Reemplaza al rebuild-on-worker.sh del Python (borrado en HU-022.1, tag python-final): la imagen ya no se construye en el worker con :latest,
# se construye en cualquier equipo con Docker, se prueba y se sube a GHCR con tags inmutables. Dokploy despliega
# después un tag concreto (DEPLOY.md §4). Mismo resultado que el job publicar-imagen de
# .github/workflows/backend-dotnet.yml, que hoy no corre porque GitHub Actions está bloqueado por facturación.
#
# Tags (nunca "latest"; sha-<corto> y X.Y.Z son inmutables: nunca se reemplazan con otros bytes):
#   sha-<commit corto>    siempre; si ya existe en el registry se conserva
#   X.Y.Z                 si HEAD tiene un tag git vX.Y.Z (o con --version X.Y.Z, que exige ese tag en HEAD); se crea
#                         sobre el digest de sha-<corto> con "docker buildx imagetools create --prefer-index=false"
#                         (sin reconstruir; mismo digest).
#                         Final solo desde un commit de origin/main; X.Y.Z-rc.N puede salir de otra rama (QA).
#   qa | main             con --canal qa | --canal main, desde esa rama y con HEAD subido a origin
#
# Uso (desde cualquier carpeta; Linux, macOS o Git Bash en Windows; en PowerShell usar publicar-imagen.ps1):
#   sh backend-dotnet/scripts/publicar-imagen.sh --canal qa           build + humo + escaneo + push de sha-xxxxxxx y qa
#   sh backend-dotnet/scripts/publicar-imagen.sh                      en un commit con tag v1.2.0: 1.2.0 y sha-xxxxxxx
#   sh backend-dotnet/scripts/publicar-imagen.sh --sin-push           build, humo y escaneo, sin subir (no pide login)
#
# Opciones:
#   --canal qa|main     agrega el tag del canal
#   --version X.Y.Z     versión explícita; HEAD debe tener el tag vX.Y.Z (trazabilidad imagen <-> commit)
#   --sin-push          no sube nada (sirve para validar el Dockerfile y la sonda)
#   --sin-prueba        omite la prueba de humo (no recomendado)
#   --sin-escaneo       omite el escaneo de vulnerabilidades con Trivy (no recomendado)
#   --permitir-sucio    permite cambios sin commit (solo con --sin-push: una imagen publicada debe salir de un commit)
#
# Variables:
#   COTIFLY_IMAGEN   repositorio de la imagen (por defecto ghcr.io/innovacionia-proyectos/cotifly-backend)
#   COTIFLY_TRIVY_IMAGEN    imagen de Trivy para el escaneo (por defecto aquasec/trivy:0.74.0 fijada por digest, la del CI)
#   GHCR_USUARIO, GHCR_TOKEN   si están, se hace "docker login ghcr.io" con ellas (token: PAT clásico con write:packages).
#                    Si no, se usa la sesión que ya tenga "docker login ghcr.io".
#
# Requisitos: docker, git, curl. La prueba de humo publica el puerto 8080 del contenedor en un puerto libre de
# 127.0.0.1 y borra su contenedor al terminar (nombre cotifly-publicar-<pid>).
# =============================================================================
set -eu

export MSYS_NO_PATHCONV=1
ruta_absoluta() { (cd "$1" && (pwd -W 2>/dev/null || pwd)); }

DIR_SCRIPTS=$(ruta_absoluta "$(dirname "$0")")
DIR_BACKEND=$(ruta_absoluta "$DIR_SCRIPTS/..")
IMAGEN=${COTIFLY_IMAGEN:-ghcr.io/innovacionia-proyectos/cotifly-backend}
IMAGEN_LOCAL=cotifly-api:publicar
CONTENEDOR="cotifly-publicar-$$"
# Trivy fijado por digest (misma imagen que el CI). COTIFLY_TRIVY_IMAGEN permite cambiarlo.
TRIVY_IMAGEN=${COTIFLY_TRIVY_IMAGEN:-aquasec/trivy:0.74.0@sha256:62b1e65e8869bc4b4c6aa4fa2b21595256c7c2f6018a9d9ad61caf87187c1969}

canal=""
version=""
push=1
prueba=1
escaneo=1
permitir_sucio=0

while [ "$#" -gt 0 ]; do
  case "$1" in
    --canal) shift; canal=${1:?falta qa o main después de --canal} ;;
    --version) shift; version=${1:?falta la versión después de --version} ;;
    --sin-push) push=0 ;;
    --sin-prueba) prueba=0 ;;
    --sin-escaneo) escaneo=0 ;;
    --permitir-sucio) permitir_sucio=1 ;;
    -h|--help) sed -n '2,39p' "$0"; exit 0 ;;
    *) echo "Opción desconocida: $1 (ver --help)" >&2; exit 2 ;;
  esac
  shift
done

paso() { printf '\n==> %s\n' "$1"; }
fallar() { echo "ERROR: $1" >&2; exit 1; }

case "$canal" in
  ''|qa|main) ;;
  *) fallar "--canal solo admite qa o main (recibido: $canal)" ;;
esac

# ----------------------------------------------------------------------------- commit y versión
cd "$DIR_BACKEND"
commit=$(git rev-parse --short=7 HEAD)
if [ -n "$(git status --porcelain -- .)" ]; then
  if [ "$permitir_sucio" -eq 1 ] && [ "$push" -eq 0 ]; then
    echo "AVISO: hay cambios sin commit; la imagen no corresponde exactamente a $commit."
    commit="$commit-sucio"
  else
    fallar "hay cambios sin commit. Una imagen publicada debe salir de un commit (usar --sin-push --permitir-sucio para probar)."
  fi
fi

SEMVER='^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$'
tag_git=$(git tag --points-at HEAD | grep -E '^v[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.]+)?$' | head -n 1 || true)
if [ -n "$version" ]; then
  echo "$version" | grep -Eq "$SEMVER" || fallar "--version debe ser semver (X.Y.Z o X.Y.Z-rc.N)"
  git tag --points-at HEAD | grep -qx "v$version" || fallar "HEAD no tiene el tag v$version (crearlo con: git tag -a v$version -m ... && git push origin v$version)"
elif [ -n "$tag_git" ]; then
  version=${tag_git#v}
fi

# Trazabilidad antes de publicar (no aplica con --sin-push):
#   siempre           hace falta --canal o una versión, y HEAD debe estar en alguna rama de origin: un build de un
#                     commit local no puede ocupar el sha-<corto> inmutable.
#   --canal qa|main   se corre desde esa rama (QA o main) y con HEAD ya subido a origin.
#   X.Y.Z (final)     el commit está en origin/main y el tag vX.Y.Z de origin apunta a HEAD.
#   X.Y.Z-rc.N        puede salir de otra rama (p. ej. QA, para probar en staging la candidata exacta), con el tag de
#                     origin apuntando a HEAD.
if [ "$push" -eq 1 ]; then
  [ -n "$canal" ] || [ -n "$version" ] || fallar "para publicar hace falta --canal qa|main o una versión (tag vX.Y.Z en HEAD o --version)"
  git fetch -q origin || fallar "git fetch origin falló"
  [ -n "$(git branch -r --contains HEAD | grep -v -- '->' | grep '^ *origin/' || true)" ] \
    || fallar "HEAD ($commit) no está en ninguna rama de origin: hacer push antes de publicar"
  if [ -n "$canal" ]; then
    rama_canal=QA
    [ "$canal" = main ] && rama_canal=main
    rama_actual=$(git rev-parse --abbrev-ref HEAD)
    [ "$rama_actual" = "$rama_canal" ] || fallar "--canal $canal se publica desde la rama $rama_canal (rama actual: $rama_actual)"
    git merge-base --is-ancestor HEAD "origin/$rama_canal" || fallar "HEAD no está en origin/$rama_canal: hacer push antes de publicar"
  fi
  if [ -n "$version" ]; then
    # Tag anotado: "^{}" da el commit al que apunta; tag liviano: la propia referencia.
    remoto=$(git ls-remote origin "refs/tags/v$version^{}" | cut -f1)
    [ -n "$remoto" ] || remoto=$(git ls-remote origin "refs/tags/v$version" | cut -f1)
    [ -n "$remoto" ] || fallar "el tag v$version no está en origin (git push origin v$version)"
    [ "$remoto" = "$(git rev-parse HEAD)" ] || fallar "el tag v$version de origin apunta a $remoto, no a HEAD"
    case "$version" in
      *-*) ;;
      *) git merge-base --is-ancestor HEAD origin/main || fallar "una versión final (v$version) sale de un commit de origin/main; para probar fuera de main usar vX.Y.Z-rc.N" ;;
    esac
  fi
fi

# sha-<corto> va primero: los demás tags se crean sobre su digest (mismos bytes).
etiquetas="sha-$commit"
[ -n "$version" ] && etiquetas="$etiquetas $version"
[ -n "$canal" ] && etiquetas="$etiquetas $canal"
if [ -n "$version" ]; then
  version_build=$version
elif [ -n "$canal" ]; then
  version_build="$canal-$commit"
else
  version_build="manual-$commit"
fi

echo "Imagen:     $IMAGEN"
echo "Versión:    $version_build"
echo "Etiquetas:  $etiquetas"

# ----------------------------------------------------------------------------- build
paso "docker build ($IMAGEN_LOCAL)"
docker build \
  --build-arg VERSION="$version_build" \
  --build-arg VCS_REF="$commit" \
  --build-arg BUILD_DATE="$(date -u +%Y-%m-%dT%H:%M:%SZ)" \
  -t "$IMAGEN_LOCAL" -f "$DIR_BACKEND/Dockerfile" "$DIR_BACKEND"

# ----------------------------------------------------------------------------- prueba de humo
limpiar() { docker rm -f "$CONTENEDOR" >/dev/null 2>&1 || true; }
if [ "$prueba" -eq 1 ]; then
  paso "Prueba de humo (Development, sin base: /health 200, /health/ready 503, HEALTHCHECK healthy)"
  trap limpiar EXIT INT TERM
  docker run -d --name "$CONTENEDOR" -p 127.0.0.1::8080 -e ASPNETCORE_ENVIRONMENT=Development "$IMAGEN_LOCAL" >/dev/null
  puerto=$(docker port "$CONTENEDOR" 8080/tcp | head -n 1 | sed 's/.*://')
  estado=""
  intentos=0
  while [ "$intentos" -lt 45 ]; do
    estado=$(docker inspect --format '{{.State.Health.Status}}' "$CONTENEDOR" 2>/dev/null || echo "sin-contenedor")
    [ "$estado" = "healthy" ] && break
    [ "$estado" = "unhealthy" ] && break
    intentos=$((intentos + 1))
    sleep 2
  done
  if [ "$estado" != "healthy" ]; then
    docker logs --tail 40 "$CONTENEDOR" >&2 || true
    fallar "HEALTHCHECK en estado '$estado'"
  fi
  salud=$(curl -fsS "http://127.0.0.1:$puerto/health") || fallar "GET /health no respondió 200"
  echo "GET /health -> $salud"
  listo=$(curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:$puerto/health/ready" || true)
  [ "$listo" = "503" ] || fallar "GET /health/ready respondió $listo (se esperaba 503 sin base)"
  echo "GET /health/ready -> 503 (esperado sin base)"
  limpiar
  trap - EXIT INT TERM
fi

# ----------------------------------------------------------------------------- escaneo de vulnerabilidades
if [ "$escaneo" -eq 1 ]; then
  paso "Escaneo de vulnerabilidades ($TRIVY_IMAGEN: HIGH/CRITICAL con parche disponible fallan)"
  # Excepciones temporales por CVE con vencimiento (las mismas del CI, #86): ver el encabezado de .trivyignore.yaml.
  [ -f "$DIR_BACKEND/.trivyignore.yaml" ] || fallar "no existe $DIR_BACKEND/.trivyignore.yaml (lo usa también el CI)"
  docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
    -v "$DIR_BACKEND/.trivyignore.yaml:/trivyignore.yaml:ro" "$TRIVY_IMAGEN" \
    image --quiet --scanners vuln --severity HIGH,CRITICAL --ignore-unfixed \
    --ignorefile /trivyignore.yaml --exit-code 1 "$IMAGEN_LOCAL" \
    || fallar "la imagen tiene vulnerabilidades HIGH/CRITICAL con parche (actualizar ASPNET_VERSION/SDK_VERSION o el paquete)"
fi

# ----------------------------------------------------------------------------- push
if [ "$push" -eq 0 ]; then
  paso "Sin push (--sin-push). Imagen local: $IMAGEN_LOCAL"
  exit 0
fi

paso "Login en $(echo "$IMAGEN" | cut -d/ -f1)"
if [ -n "${GHCR_USUARIO:-}" ] && [ -n "${GHCR_TOKEN:-}" ]; then
  printf '%s' "$GHCR_TOKEN" | docker login "$(echo "$IMAGEN" | cut -d/ -f1)" -u "$GHCR_USUARIO" --password-stdin
else
  echo "Sin GHCR_USUARIO/GHCR_TOKEN: se usa la sesión existente de docker login."
fi

# Tags inmutables (igual que el job publicar-imagen del CI):
#   sha-<corto>  se sube solo si no existe; si ya existe se conserva (no se reemplaza con un build nuevo).
#   X.Y.Z        error si ya existe; si no, se crea sobre el digest de sha-<corto> (imagetools create --prefer-index=false,
#                que conserva el mismo manifiesto y por lo tanto el mismo digest).
#   qa | main    canal móvil: se mueve al digest de sha-<corto>.
paso "Etiquetar y subir"
# "not found" o MANIFEST_UNKNOWN = no existe; cualquier otro error (red, credenciales) detiene el script.
existe() {
  if salida=$(docker buildx imagetools inspect "$IMAGEN:$1" 2>&1); then return 0; fi
  if printf "%s" "$salida" | grep -Eqi "not found|manifest_unknown|manifest unknown"; then return 1; fi
  fallar "no se pudo consultar $IMAGEN:$1: $salida"
}
for etiqueta in $etiquetas; do
  if [ "$etiqueta" = "sha-$commit" ]; then
    if existe "$etiqueta"; then
      echo "$IMAGEN:$etiqueta ya existe: se conserva (los demás tags apuntan a ese digest, no a este build)"
    else
      docker tag "$IMAGEN_LOCAL" "$IMAGEN:$etiqueta"
      docker push "$IMAGEN:$etiqueta"
    fi
    continue
  fi
  case "$etiqueta" in
    qa|main) ;;
    *) existe "$etiqueta" && fallar "$IMAGEN:$etiqueta ya existe; una versión publicada no se sobrescribe" ;;
  esac
  docker buildx imagetools create --prefer-index=false -t "$IMAGEN:$etiqueta" "$IMAGEN:sha-$commit"
done
digest=$(docker buildx imagetools inspect "$IMAGEN:sha-$commit" | sed -n 's/^Digest: *//p' | head -n 1)

cat <<EOF

Imagen publicada.
  Etiquetas  $(for e in $etiquetas; do printf '%s:%s  ' "$IMAGEN" "$e"; done)
  Digest     $digest

Siguiente paso (DEPLOY.md §4): en Dokploy fijar COTIFLY_TAG=${version:-sha-$commit} en el stack y desplegar.
EOF
