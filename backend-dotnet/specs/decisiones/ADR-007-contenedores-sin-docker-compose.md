# ADR-007 — Contenedores: solo Dockerfile multi-stage, sin docker-compose

- **Estado:** Aceptada (desviación explícita del estándar)
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #14 (HU-003.8) y #27 (HU-006.3). El despliegue es HU-006.
- **Actualización 2026-10-13 (HU-022.1, #146):** por decisión del usuario ("todo lo referente a python muere en este
  repo") el stack Python se retiró del repositorio, incluida la carpeta `deploy/` (con `deploy/docker-compose.swarm.yml`). Ya no hay rollback al Python desde
  aquí: el último estado del Python vive en el tag `python-final`. Las rutas del Python que cita esta ADR quedan como historia.

## Contexto

El estándar `dotnet-enterprise-architect` exige `Dockerfile` y `docker-compose.yml`, y el Gherkin "Empaquetado y pruebas según el estándar" de HU-003 repite esa exigencia. Pero el backend .NET es un único proceso:

- La base de datos es la instancia corporativa SQL Server 2019 ya existente, no un contenedor.
- Los PDFs se guardan detrás de `IClsAlmacenPdf`. HU-003.7 (#13) mantiene el volumen como único adaptador; S3/MinIO queda pendiente de las réplicas de HU-006.6 (ADR-012).
- El despliegue real es Docker Swarm con Dokploy (HU-006), que define los servicios desde su propio panel y manifiestos.

Además, la máquina de desarrollo solo tiene SDK .NET 8, así que build y tests de .NET 10 corren dentro de `mcr.microsoft.com/dotnet/sdk:10.0`.

## Decisión

1. **Un solo `backend-dotnet/Dockerfile` multi-stage:**
   - `sdk:10.0` para restore, build, test y publish.
   - `aspnet:10.0` como runtime: usuario no root, `EXPOSE 8080`, y fuentes e imágenes del PDF embebidas como recurso.
   - `HEALTHCHECK` sobre `/health`. La imagen `aspnet` no trae `curl`, así que se usa una sonda propia (el mismo binario con un argumento `--healthcheck`, o un ejecutable mínimo). Instalar `curl` queda como alternativa.
2. **Sin `docker-compose.yml` para ejecución local.** Es una desviación explícita y consciente del estándar corporativo y de HU-003.8 (#14), que pide `docker-compose.yml`; queda registrada en esta ADR. No aplica al despliegue: el manifiesto de Swarm (`infra/stack*.yml`, en formato compose para `docker stack deploy`/Dokploy) sí existe y lo define para el backend .NET HU-006.6 (#30).
3. **Ejecución local:**
   ```bash
   docker build -t cotifly-api:dev -f backend-dotnet/Dockerfile backend-dotnet
   docker run --rm -p 8080:8080 --env-file backend-dotnet/.env -v cotifly_pdfs:/data/pdfs cotifly-api:dev
   ```
   Las variables se documentan en `backend-dotnet/.env.example`, sin secretos reales.
4. **Build y test sin SDK local:**
   ```bash
   docker run --rm -v "<ruta>/backend-dotnet:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
   ```
   En Windows se lanza desde PowerShell, por las rutas con espacios.
5. **Tests de integración con Testcontainers** (`Testcontainers.MsSql`):
   - Imagen `mcr.microsoft.com/mssql/server:2019-latest`, la misma versión que producción.
   - Se crea la base `dbTarifario_Nexo_Pruebas` y se aplican los scripts `db/migrations_mssql/*.sql`, partidos por `GO`.
   - Así se cubre la "BD local" que normalmente justificaría un compose.
   - Si los tests corren dentro del contenedor del SDK, se monta `/var/run/docker.sock` y puede hacer falta `TESTCONTAINERS_HOST_OVERRIDE` para que el contenedor alcance a SQL Server.

## Implementación (#27, HU-006.3, 2026-09-24)

Concreta el punto 1. Detalle operativo en `backend-dotnet/docs/03-infraestructura.md` y `docs/06-ejecucion-local.md` (antes README §3 y §6).

- **Nombre de la imagen:** `cotifly-api`. En local `cotifly-api:dev`; en el registry `${REGISTRY}/cotifly-api:<versión>`, con la convención de tags de HU-006.4 (#28).
- **Tags fijos:** `mcr.microsoft.com/dotnet/sdk:10.0.401-noble` (build) y `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra` (runtime), como `ARG SDK_VERSION` / `ARG ASPNET_VERSION`. Un tag flotante (`10.0`) haría que dos builds del mismo commit den imágenes distintas.
  - **Cómo subir de parche:** tomar el último parche de las release notes de .NET 10 (o `docker pull ...:10.0` + `docker image inspect`, variables `DOTNET_SDK_VERSION` y `ASPNET_VERSION`), cambiar ambos `ARG` juntos (el runtime debe ser el que trae ese SDK), reconstruir, correr las pruebas y repetir la verificación de `docs/06-ejecucion-local.md` §6.4. `global.json` no cambia (`10.0.100` + `latestFeature`).
- **Metadatos:** `ARG VERSION=dev`, `ARG VCS_REF=unknown`, `ARG BUILD_DATE` → etiquetas `org.opencontainers.image.{title,description,version,revision,created,source,vendor}` en la etapa runtime, y `-p:InformationalVersion=$VERSION` en el publish (versión de producto del `.dll`).
- **Runtime `chiseled-extra` en vez de `aspnet:10.0`:** la app necesita tzdata (`Zona:ZonaHoraria=America/Bogota` se valida al arrancar) e ICU (formato es-CO de dinero y fechas; `InvariantGlobalization=false`). `chiseled` sin `-extra` no los trae; `aspnet:10.0` (Ubuntu completo) sí, pero suma shell y `apt` y pesa más. Imagen final medida: **87,6 MB comprimida / ~304 MB en disco** (base `aspnet:10.0` sola: 95,9 MB / 340 MB). Verificado dentro de la imagen: `America/Bogota` (−05:00) y fecha larga `jueves, 24 de septiembre de 2026`.
  - **Moneda:** ICU da `3294798m.ToString("C0", es-CO)` = `$ 3.294.798`. HU-003.5 (#11) usará un formateador propio (`NumberFormatInfo` clonado de es-CO con `CurrencyPositivePattern=0`, `CurrencyDecimalDigits=0`) para igualar el `$3.294.798` del Python.
  - **Fuentes del PDF:** embebidas y cargadas con `IFontResolver`; chiseled no trae fontconfig ni fuentes del sistema.
- **Sin shell en runtime:** la carpeta `/data/pdfs` se crea en la etapa build y se copia con dueño `app` (UID 1654). `/app` y los binarios se copian con dueño root, así el usuario `app` no puede borrarlos ni reemplazarlos. Sin `VOLUME` en el Dockerfile (evita volúmenes anónimos). `/data/pdfs` solo aplica en modo volumen (HU-003.7 decide volumen o S3/MinIO); con varias réplicas un volumen local no se comparte entre nodos, lo decide HU-006.6 con HU-003.7.
  - **Depuración:** `docker debug` si la licencia lo incluye; contenedor auxiliar con `--pid=container:<id> --network=container:<id>` sobre `sdk:10.0.401-noble` con `dotnet-counters`/`dotnet-dump`; o build de emergencia `--build-arg ASPNET_VERSION=10.0.12-noble` con tag `-debug`, nunca por el pipeline de producción (`docs/10-operacion.md` §10.5).
- **HEALTHCHECK: opción "mismo binario con `--healthcheck`"** (no un ejecutable aparte ni `curl`). `Program.cs` detecta el argumento antes de construir el host y delega en `Cotifly.Api.Sistema.clsSondaSalud`, que hace `GET /health` con límite de 3 s, sin proxy (`SocketsHttpHandler { UseProxy = false }`), y termina con 0 si responde 200, 1 si no. Destino, en el mismo orden que Kestrel: primera URL `http://` de `ASPNETCORE_URLS` (`+`, `*`, `0.0.0.0`, `[::]` → 127.0.0.1; `https://` se ignora), si no el primer puerto de `ASPNETCORE_HTTP_PORTS`, si no 8080. No usar `ASPNETCORE_URLS` salvo necesidad; si se usa, la sonda lo respeta. Parámetros iguales al `orquestador/Dockerfile` (30 s / 5 s / 3 reintentos) más `start-period=20s`.
  - Se descartó un proyecto `Cotifly.Sonda` aparte: sumaba un ensamblado, reglas de arquitectura y un publish extra para unas decenas de líneas; y `curl` obligaba a salir de chiseled.
  - La sonda consulta `/health` (vida) y no `/health/ready`, para que una base caída no provoque reinicios en Swarm. Cada sonda levanta un proceso `dotnet` (~40–70 MB transitorios).
- **Todos los fallos de arranque terminan con código 1:** si `Cotifly.Api` es el proceso (no bajo `WebApplicationFactory`, donde la excepción se propaga a la prueba), `Program.cs` captura cualquier excepción al construir el host (una línea `crit:` en stderr, sin valores de configuración) o al arrancarlo (el host ya registró `Hosting failed to start`, que en `ValidateOnStart` nombra la clave y nunca el valor; no se repite). Sin eso, la excepción no controlada en el PID 1 del contenedor termina con 139 y repite la traza. Verificado: `Production` sin variables → 1; `ASPNETCORE_HTTP_PORTS=99999` → 1.
- **Contenido de la imagen:** `dotnet publish -r linux-x64 --self-contained false`, `UseAppHost=false`, sin `.pdb` (`DebugType=none`; las trazas pierden el número de línea, a cambio de no publicar símbolos ni rutas del código; alternativa a evaluar en HU-006.4: `DebugType=embedded` + `PathMap`), solo recursos satélite `es`, sin `web.config`. `.dockerignore` es lista blanca (props, `global.json`, `.editorconfig`, `src/`): `tests/`, `specs/`, docs y `.env` no entran ni al contexto.
- **Las pruebas no corren en `docker build`**, aunque el punto 1 decía "restore, build, test y publish": el contexto no incluye `tests/` y las de integración necesitan el socket de Docker (Testcontainers), que no existe durante el build. Corren con el comando del punto 4 y en CI (#14).
- **Para HU-006.6 (#30):** `bot-orquestador` escucha hoy en 8000 (Traefik `loadbalancer.server.port=8000`; `deploy/docker-compose.swarm.yml` publica `8091 → 8000`). Con .NET: puerto 8080 en `target` y `traefik.http.services.<svc>.loadbalancer.server.port`; healthcheck del stack, si lo redefine, en forma exec `["CMD", "dotnet", "/app/Cotifly.Api.dll", "--healthcheck"]` (nunca `CMD-SHELL`); readiness con el healthcheck de Traefik a `/health/ready` (saca la tarea del balanceo sin reiniciarla); reservar memoria para la sonda; retirar `bot-mcp`; logs en UTC. HU-007 debe usar `ConnectionStrings__Cotifly` (no `SQLSERVER_CONNECTION_STRING`). #27 no toca los manifiestos del Python.

## Implementación (#14, HU-003.8, 2026-09-25)

Concreta los puntos 2 a 5 sin agregar `docker-compose`:

- **Entorno local con `docker run`:** `backend-dotnet/scripts/levantar-local.ps1` (PowerShell) y `levantar-local.sh` (Linux, macOS, Git Bash) crean la red `cotifly-local`, un SQL Server 2019 (`cotifly-local-sql`, volumen `cotifly-local-sqldata`), aplican con `sqlcmd` 18 (el que trae la propia imagen de SQL Server 2019) las bases, el esquema `dbo_cotifly` vigente (`0001` a `0010`: `db/scripts/01_crear_esquema.sql` en una base vacía o las migraciones que falten; actualizado en HU-021.14) con los seeders de `db/seeder/`, una `dbsolucionesti` falsa y el login `cotifly_app` con los permisos mínimos que se piden al DBA (por esquema `dbo_cotifly`), verifican esos permisos conectados como `cotifly_app`, y corren `cotifly-api:dev` con `--env-file` (`backend-dotnet/.env.local`, generado y no versionado). Publican SQL Server y la API solo en `127.0.0.1`, esperan a `/health/ready` 200 y solo borran recursos `cotifly-local-*`. Detalle en `docs/06-ejecucion-local.md` §6.2.
  - **Alcance:** solo desarrollo y QA **local**, con datos, usuarios e IDROLES ficticios. No es un entorno de QA compartido ni se usa en el despliegue (HU-006), y las pruebas automáticas siguen usando Testcontainers.
  - **Por qué un script y no un compose** (el compose del estándar tampoco cubriría esto sin un contenedor de inicialización con su propia lógica): los scripts SQL se aplican con `sqlcmd -I` (QUOTED_IDENTIFIER, exigido por los índices filtrados) y en orden condicional (el `0001` solo si `dbo_cotifly` no existe, porque después del `0002` no se re-ejecuta); se crea el login `cotifly_app` con permisos mínimos y se verifica que no alcance otros esquemas; se valida la contraseña; y el archivo de variables se genera una vez con una clave aleatoria. Un `docker-compose.yml` con `depends_on` no ordena ni condiciona scripts, y moverlo a un contenedor init sería el mismo script con más piezas.
- **CI:** `.github/workflows/backend-dotnet.yml` (GitHub Actions, `ubuntu-latest`, que trae Docker): build, pruebas unitarias, pruebas de integración con Testcontainers, cobertura, auditoría NuGet, `docker build` con prueba de humo del `HEALTHCHECK` y lint del contrato. Resuelve el pendiente "runner con Docker" para GitHub Actions.
- **Depuración sin shell ensayada:** el contenedor auxiliar con `--pid=container:<id>` necesita además `--cap-add=SYS_PTRACE` para leer el socket de diagnóstico del proceso `app` (`docs/10-operacion.md` §10.5).
- El `Dockerfile` no cambió en #14 salvo comentarios que apuntaban al README anterior; `docker build --no-cache` verificado.

## Alternativas consideradas

- **Compose con SQL Server local + API:** duplica a Testcontainers, induce a desarrollar contra una base distinta de la corporativa y no se usa en el despliegue.
- **Compose solo para levantar la API:** no aporta nada frente a `docker run --env-file`.

## Consecuencias

- El criterio de HU-003 sobre `docker-compose.yml` no se cumple de forma literal para ejecución local. Al revisar HU-003.8 (#14) debe citarse esta ADR.
- La integración contra dependencias reales (SQL Server corporativo, SAP TM, Nexo, SMTP) se prueba en el entorno de QA desplegado.
- CI necesita un runner con Docker. Donde no lo haya, se filtran los tests de la categoría `Integracion`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Avisar de esta desviación a quien mantiene el estándar `dotnet-enterprise-architect`, junto con la de prefijos de tabla del 2026-09-22, citando la implementación de #14 (scripts de entorno local en lugar del compose). **Sigue pendiente** al cierre de #14 | Duban |
| Confirmar que el runner de CI corporativo tiene Docker (resuelto para GitHub Actions `ubuntu-latest` en #14; solo queda si el CI corporativo es otro) | Duban |
