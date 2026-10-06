# syntax=docker/dockerfile:1
# =============================================================================
# Cotifly — backend .NET 10 (Cotifly.Api). Único servicio expuesto (ADR-002, ADR-007, #27).
#
#   docker build -t cotifly-api:dev -f backend-dotnet/Dockerfile backend-dotnet
#   docker run --rm -p 8080:8080 --env-file backend-dotnet/.env -v cotifly_pdfs:/data/pdfs cotifly-api:dev
#
# Etapas:
#   build   -> SDK: restore (capa cacheada) + publish Release de src/Cotifly.Api.
#   runtime -> ASP.NET Core chiseled-extra: sin SDK, sin shell, sin tests, usuario no root.
#
# Las pruebas NO corren aquí: la imagen se construye desde src/ únicamente (tests/ está en
# .dockerignore) y las pruebas de integración necesitan el socket de Docker (Testcontainers),
# que no existe dentro de "docker build". Corren aparte, en CI o con los comandos de docs/06-ejecucion-local.md y docs/08-testing.md.
#
# Tags fijos (reproducibles). Para actualizar el parche de .NET:
#   1. Ver el último parche en https://github.com/dotnet/core/blob/main/release-notes/10.0/releases.json
#      (o `docker pull mcr.microsoft.com/dotnet/sdk:10.0` y leer DOTNET_SDK_VERSION / ASPNET_VERSION
#      con `docker image inspect`).
#   2. Cambiar SDK_VERSION y ASPNET_VERSION juntos (el SDK 10.0.4xx trae el runtime 10.0.N; usar ese N).
#   3. Reconstruir, correr las pruebas y verificar HEALTHCHECK (docs/06-ejecucion-local.md, §6.4).
# =============================================================================
ARG SDK_VERSION=10.0.401-noble
ARG ASPNET_VERSION=10.0.12-noble-chiseled-extra
# Metadatos de la imagen (los pasa el pipeline, HU-006.4 #28):
#   --build-arg VERSION=<versión> --build-arg VCS_REF=$(git rev-parse --short HEAD) --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
ARG VERSION=dev
ARG VCS_REF=unknown
ARG BUILD_DATE=

# -----------------------------------------------------------------------------
# build
# -----------------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:${SDK_VERSION} AS build
# RID del servidor (Swarm/Dokploy es linux-x64). Publicar para un RID concreto deja fuera los
# binarios nativos de Windows/macOS que traen los paquetes (SqlClient, MSAL): ~25 MB menos.
ARG RID=linux-x64
WORKDIR /src

# 1) Solo lo que define el restore: si no cambian los .csproj ni los props, esta capa se reutiliza.
#    Se restaura el proyecto Api (no la .sln): la .sln referencia tests/, que no entra al contexto.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Cotifly.Dominio/Cotifly.Dominio.csproj               src/Cotifly.Dominio/
COPY src/Cotifly.Aplicacion/Cotifly.Aplicacion.csproj         src/Cotifly.Aplicacion/
COPY src/Cotifly.Infraestructura/Cotifly.Infraestructura.csproj src/Cotifly.Infraestructura/
COPY src/Cotifly.Api/Cotifly.Api.csproj                       src/Cotifly.Api/
RUN --mount=type=cache,id=cotifly-nuget,target=/root/.nuget/packages \
    dotnet restore src/Cotifly.Api/Cotifly.Api.csproj -r $RID

# 2) Código y publish (framework-dependent: el runtime lo pone la imagen aspnet).
#    - UseAppHost=false: se arranca con "dotnet Cotifly.Api.dll".
#    - DebugType=none: sin .pdb (imagen más chica y sin rutas del código fuente); las trazas de
#      excepción conservan tipo y método, no número de línea.
#    - SatelliteResourceLanguages=es: solo los recursos localizados en español (mensajes de SqlClient).
#    - IsTransformWebConfigDisabled: sin web.config (es de IIS).
#    - VERSION: todo ARG de la etapa llega a los RUN como variable de entorno, y MSBuild lee las variables de entorno
#      como propiedades sin distinguir mayúsculas: VERSION=dev se volvía $(Version)=dev y el restore fallaba con
#      NU1105 ("dev" no es una versión válida; #11, antes quedaba oculto porque la capa del restore venía de caché).
#      Por eso el ARG se declara después del restore y el publish corre con "env -u VERSION": el valor solo entra
#      como InformationalVersion (el shell expande $VERSION antes de quitar la variable del entorno).
COPY src/ src/
ARG VERSION
RUN --mount=type=cache,id=cotifly-nuget,target=/root/.nuget/packages \
    env -u VERSION dotnet publish src/Cotifly.Api/Cotifly.Api.csproj \
      -c Release --no-restore -r $RID --self-contained false -o /app/publish \
      -p:UseAppHost=false -p:DebugType=none -p:DebugSymbols=false \
      -p:SatelliteResourceLanguages=es -p:IsTransformWebConfigDisabled=true \
      -p:InformationalVersion=$VERSION

# Carpeta de PDFs: la imagen runtime no tiene shell (no hay "RUN mkdir"), así que se crea
# aquí y se copia con dueño app (UID 1654, $APP_UID de las imágenes .NET).
RUN mkdir -p /data/pdfs

# -----------------------------------------------------------------------------
# runtime
# -----------------------------------------------------------------------------
# chiseled-extra = Ubuntu 24.04 mínimo + ICU + tzdata, sin shell ni gestor de paquetes.
#   - tzdata: Zona:ZonaHoraria=America/Bogota se valida al arrancar.
#   - ICU:    formato es-CO (separadores de miles y decimales, fecha larga) en PDF y correo. InvariantGlobalization=false.
# La variante "chiseled" (sin -extra) no trae ICU ni tzdata; "aspnet:10.0" (Ubuntu completo) sí, pero pesa más
# y trae shell y apt.
FROM mcr.microsoft.com/dotnet/aspnet:${ASPNET_VERSION} AS runtime
ARG VERSION
ARG VCS_REF
ARG BUILD_DATE

# Etiquetas OCI: identifican la imagen en el registry y en "docker inspect".
LABEL org.opencontainers.image.title="cotifly-api" \
      org.opencontainers.image.description="Backend .NET 10 de Cotifly (bot cotizador de Botero Soto): API web, agente de IA, PDF y correo." \
      org.opencontainers.image.version="$VERSION" \
      org.opencontainers.image.revision="$VCS_REF" \
      org.opencontainers.image.created="$BUILD_DATE" \
      org.opencontainers.image.source="https://github.com/InnovacionIA-proyectos/Cotifly" \
      org.opencontainers.image.vendor="Botero Soto Soluciones Logísticas"

# Sin TZ: el contenedor queda en UTC y la zona de negocio sale de Zona:ZonaHoraria (IClsReloj),
# así un DateTime.Now accidental no queda oculto por la zona del sistema.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

# Binarios con dueño root (0:0) y /app creado por COPY (no por WORKDIR, que con la imagen chiseled
# lo crearía con dueño app): el usuario app puede leerlos pero no borrarlos ni reemplazarlos.
COPY --from=build --chown=0:0 /app/publish /app/
# Única carpeta escribible: PDFs (montar un volumen aquí; ver docs/06-ejecucion-local.md).
COPY --from=build --chown=1654:1654 /data/pdfs /data/pdfs
WORKDIR /app

# Usuario sin privilegios que ya trae la imagen ("app", UID 1654 = $APP_UID).
USER $APP_UID

EXPOSE 8080

# Equivale al HEALTHCHECK de orquestador/Dockerfile (GET /health, 30s/5s/3). Sin curl: el mismo binario
# hace de sonda (clsSondaSalud) y termina con 0/1 sin levantar el host web. start-period cubre el arranque.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["dotnet", "/app/Cotifly.Api.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/Cotifly.Api.dll"]
