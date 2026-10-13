# 6. Ejecución local

Sin `docker-compose` ([ADR-007](../specs/decisiones/ADR-007-contenedores-sin-docker-compose.md)): todo se hace con `docker run` y, para el entorno completo, con los scripts `scripts/levantar-local.ps1` (PowerShell) y `scripts/levantar-local.sh` (Linux, macOS o Git Bash).

Contenido:

- [6.1 Requisitos](#61-requisitos)
- [6.2 Entorno local completo con SQL Server](#62-entorno-local-completo-con-sql-server)
- [6.3 Compilar y probar en el contenedor del SDK](#63-compilar-y-probar-en-el-contenedor-del-sdk)
- [6.4 Construir y correr solo la imagen](#64-construir-y-correr-solo-la-imagen)
- [6.5 Correr la API desde el código](#65-correr-la-api-desde-el-código)
- [6.6 Abrir la solución en un IDE](#66-abrir-la-solución-en-un-ide)

## 6.1 Requisitos

- Docker Desktop (Windows o macOS) o Docker Engine (Linux), con unos 4 GB de memoria libres para SQL Server.
- Imágenes que se descargan la primera vez: `mcr.microsoft.com/dotnet/sdk:10.0` y `mcr.microsoft.com/mssql/server:2019-latest` (esta también aporta `sqlcmd` 18).
- No hace falta el SDK .NET 10 en el equipo (la máquina de desarrollo del equipo solo tiene el SDK 8): build, pruebas e imagen corren en contenedores.
- En Windows, lanzar los comandos con rutas del repo desde PowerShell: las rutas con espacios se rompen en Git Bash al montar volúmenes. Los scripts de entorno local funcionan en ambos.

## 6.2 Entorno local completo con SQL Server

Levanta SQL Server 2019, aplica el esquema real y corre la API con un login de servicio de permisos mínimos. Solo desarrollo: los datos y los IDROLES son ficticios.

```powershell
# PowerShell, desde la raíz del repo
.\backend-dotnet\scripts\levantar-local.ps1              # levanta (construye cotifly-api:dev si no existe)
.\backend-dotnet\scripts\levantar-local.ps1 -Construir   # reconstruye la imagen antes de levantar
.\backend-dotnet\scripts\levantar-local.ps1 -Bajar       # borra contenedores y red (conserva los datos)
.\backend-dotnet\scripts\levantar-local.ps1 -Bajar -BorrarDatos   # además borra los volúmenes
```

```bash
# Linux, macOS o Git Bash
sh backend-dotnet/scripts/levantar-local.sh [--construir] [--env-file RUTA]
sh backend-dotnet/scripts/levantar-local.sh --bajar [--borrar-datos]
```

Qué hace, en orden (los dos scripts son equivalentes e idempotentes):

| Paso | Recurso | Detalle |
|---|---|---|
| 1 | Red `cotifly-local` | Red de Docker donde la API alcanza a SQL Server por nombre |
| 2 | Contenedor `cotifly-local-sql` | `mcr.microsoft.com/mssql/server:2019-latest` (edición Developer), volumen `cotifly-local-sqldata`, puerto `14333` publicado **solo en `127.0.0.1`** para SSMS o Azure Data Studio. Espera hasta que responda |
| 3 | Bases | `scripts/sql/dev-01-bases.sql`: crea `dbTarifario_Nexo_Pruebas` y `dbsolucionesti` si no existen |
| 4 | Esquema `dbo_cotifly` | Lleva la base al esquema vigente (`0001` a `0010`, ADR-019) y se puede repetir: si `dbo_cotifly` no tiene tablas, `db/scripts/01_crear_esquema.sql` (instalación desde cero); si tiene el esquema anterior a HU-021 (tabla `Cotizaciones`, un volumen viejo), `0002` a `0010` en orden; si ya está migrada, solo el `0010` (idempotente). Después, los seeders de `db/seeder/` (idempotentes). No aplica el `02` del cubicador (HU-023): esta versión no lo mapea. Con `sqlcmd -b -I -C` 18 (`/opt/mssql-tools18/bin/sqlcmd`, dentro de la misma imagen de SQL Server 2019) |
| 5 | `dbsolucionesti` falsa | `scripts/sql/dev-02-identidad.sql`: las tres tablas del login con los tipos de `dbSolucionesTI_Prueba` (la base de identidad de QA, compartida con Nexo, #172; en local conserva el nombre `dbsolucionesti`) y los usuarios de prueba (tabla abajo) |
| 6 | Roles y login | `scripts/sql/dev-03-roles-y-login.sql`: `Roles.idRolExterno` = 34 (comercial) y 35 (gerente comercial); login `cotifly_app` con `SELECT`, `INSERT`, `UPDATE`, `DELETE` y `EXECUTE` sobre el **esquema** `dbo_cotifly` (sin `db_datareader` ni `db_datawriter`; si una versión anterior los dejó, los retira) y `SELECT` por columna (sin `CLAVE`) en `dbsolucionesti`: la misma receta que se pide al DBA |
| 6b | Verificación de permisos | `scripts/sql/dev-04-verificar-permisos.sql`, **conectado como `cotifly_app`**: debe leer y escribir en `dbo_cotifly`, leer las vistas de métricas y tomar un `sp_getapplock`; debe recibir permiso denegado al leer o escribir `dbo.tblOtraAplicacionDev` (tabla ficticia de `dev-01` que representa a otra aplicación de la base compartida) y al leer `CLAVE`. Si algo no se cumple, el script se detiene |
| 7 | Imagen | `cotifly-api:dev`; se construye si no existe o con `-Construir` / `--construir` |
| 8 | Variables | `backend-dotnet/.env.local` (no se versiona): se genera la primera vez con las cadenas a `cotifly-local-sql` con `cotifly_app`, una clave JWT aleatoria, CORS para `http://localhost:4200`, `Pdf__BaseUrl` y el correo deshabilitado. El `.sh` lo deja con permisos `600` (en Windows hereda los permisos de la carpeta del usuario). No se sobrescribe: se le pueden agregar variables de `.env.example` (por ejemplo `IA__Proveedor`, `IA__Modelo`, `IA__ApiKey` para probar el agente). Con `-EnvFile` / `--env-file` se usa otro archivo |
| 9 | Contenedor `cotifly-local-api` | `docker run --env-file ... -v cotifly-local-pdfs:/data/pdfs -p 127.0.0.1:8080:8080` (solo loopback) y espera a que `GET /health/ready` responda 200 |

**Instalación desde cero con el ejecutador.** Para una base de SQL Server propia (no la de `levantar-local`), `db/ejecutar-bd.ps1` (Windows) y `db/ejecutar-bd.sh` (Linux o contenedores) crean la base si se pide (`-CrearBase` / `-c`), corren `db/scripts/01_crear_esquema.sql` (el esquema final, igual a `0001` a `0010`) y luego los seeders de `db/seeder/` (líneas de negocio, mapeo de SAP TM y roles), y se detienen en el primer error. Si `dbo_cotifly` ya tiene tablas, el paso del esquema se detiene sin tocar nada; con `-Auto` / `-a` lo salta y solo corre los seeders. Los roles nacen sin `idRolExterno`: nadie entra hasta fijarlo. Ejemplo y opciones en [`db/README.md`](../../db/README.md):

```bash
cd db
./ejecutar-bd.sh -s localhost,1433 -d dbCotifly -u sa -p '<clave>' -c
```

Usuarios de prueba de la `dbsolucionesti` falsa:

| `idUsuario` | Rol externo | Resultado del login |
|---|---|---|
| `APEREZ` (ID 101) | 34 COMERCIAL | `comercial` |
| `LGOMEZ` (ID 102) | 35 GERENTE COMERCIAL | `gerente_comercial` |
| `KCANO` (ID 103) | 34 COMERCIAL | `comercial` (usuario de QA para la [regresión manual](qa/regresion-manual.md)) |
| `JCONSULTA` (ID 104) | 40 CONSULTA (sin mapeo) | 401 |

Prueba rápida:

```powershell
$r = Invoke-RestMethod -Method Post -Uri http://localhost:8080/api/auth/login-clientepro -ContentType 'application/json' -Body '{"idUsuario":"APEREZ"}'
Invoke-RestMethod -Uri http://localhost:8080/web/mis-cotizaciones -Headers @{ Authorization = "Bearer $($r.access)" }
```

Variables opcionales de los scripts: `COTIFLY_SQL_SA_PASSWORD` y `COTIFLY_SQL_APP_PASSWORD` (contraseñas de desarrollo por defecto), `COTIFLY_PUERTO_API` (8080), `COTIFLY_PUERTO_SQL` (14333), `COTIFLY_API_IMAGEN` (`cotifly-api:dev`), `COTIFLY_SQL_IMAGEN`, `COTIFLY_SQLCMD_IMAGEN` (por defecto la misma de SQL Server) y `COTIFLY_SQLCMD_RUTA` (`/opt/mssql-tools18/bin/sqlcmd`). `COTIFLY_SQL_APP_PASSWORD` no admite comillas, punto y coma ni espacios (romperían el literal `N'...'` del `CREATE LOGIN` o la cadena de conexión): el script la rechaza antes de empezar. `mcr.microsoft.com/mssql-tools18` no existe como imagen en MCR y `mcr.microsoft.com/mssql-tools` (sqlcmd 13, 2017) está obsoleta, por eso se usa el `sqlcmd` 18 que ya trae la imagen de SQL Server. Si se cambia una contraseña después de generar `.env.local`, borrar ese archivo para regenerarlo.

Sin `IA__Proveedor`, `POST /web/chat` responde 200 `{texto, error: true}` (agente sin configurar). El correo queda deshabilitado (`CORREO_DESHABILITADO`); para probar el envío hace falta un relay SMTP alcanzable desde el contenedor.

Los scripts solo crean y borran recursos con el prefijo `cotifly-local-` (nunca `docker prune`).

Verificado el 2026-09-25 (#14, tras la revisión de seguridad) con ambos scripts, desde cero y repetidos, con la imagen del código integrado: esquema aplicado, verificación de permisos en verde (y en rojo al darle a `cotifly_app` `db_datareader` a propósito), puertos solo en `127.0.0.1`, `/health/ready` 200, login de `APEREZ`, `LGOMEZ` y `KCANO`, 401 de `JCONSULTA`, `mis-cotizaciones`, teléfono, administración de líneas (lectura y edición del gerente), métricas (vistas), logout, y un turno completo de `/web/chat` hasta `generar_pdf` con un proveedor de IA simulado (folios `PCN-AP-25092026-001` y `-002` con el candado por iniciales, PDF descargado), todo con `cotifly_app` de permisos mínimos. Una contraseña con comilla simple o punto y coma se rechaza (código 2).

## 6.3 Compilar y probar en el contenedor del SDK

La máquina de desarrollo solo tiene SDK .NET 8, así que todo se compila y prueba dentro de la imagen oficial del SDK 10 (ADR-007). En Windows, desde PowerShell (las rutas con espacios se rompen en Git Bash):

```powershell
docker run --rm `
  -v //var/run/docker.sock:/var/run/docker.sock `
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal `
  -e REQNROLL_TELEMETRY_ENABLED=0 `
  -v "<ruta>\Cotifly:/repo" `
  -v cotifly-nuget:/root/.nuget/packages `
  -w /repo/backend-dotnet mcr.microsoft.com/dotnet/sdk:10.0 `
  sh -c "dotnet build -c Release && dotnet test -c Release --no-build"
```

- Se monta el repo completo (no solo `backend-dotnet/`) porque las pruebas copian `../db/migrations_mssql/*.sql`, `../db/scripts/*.sql` y `../db/seeder/*.sql` para aplicarlos en Testcontainers.
- El socket de Docker y `TESTCONTAINERS_HOST_OVERRIDE` permiten que las pruebas de integración levanten SQL Server 2019 como contenedor hermano (detalle en [08-testing](08-testing.md)).
- El volumen `cotifly-nuget` conserva la caché de NuGet entre ejecuciones. Las carpetas `bin/` y `obj/` que crea el contenedor están en `.gitignore`.

Los comandos exactos del CI (unitarias, integración, TRX y cobertura) están en [08-testing](08-testing.md#84-comandos).

## 6.4 Construir y correr solo la imagen

Imagen (#27), desde la raíz del repo, PowerShell:

```powershell
docker build -t cotifly-api:dev -f backend-dotnet/Dockerfile backend-dotnet

# Development: arranca sin cadenas de conexión; /health 200 y /health/ready 503
docker run -d --name cotifly-api -p 8080:8080 -e ASPNETCORE_ENVIRONMENT=Development cotifly-api:dev
curl.exe http://localhost:8080/health          # {"status":"ok","servicio":"cotifly-api","entorno":"Development"}
curl.exe http://localhost:8080/health/ready    # 503 {"status":"unavailable","detalle":"La cadena de conexión ..."}
docker inspect --format '{{.State.Health.Status}}' cotifly-api   # healthy (primer chequeo a los pocos segundos)
docker rm -f cotifly-api

# Con variables reales (.env a partir de .env.example) y volumen para los PDFs
docker run --rm -p 8080:8080 --env-file backend-dotnet/.env -v cotifly_pdfs:/data/pdfs cotifly-api:dev
```

- Variables: las de `.env.example`. La imagen ya fija `ASPNETCORE_HTTP_PORTS=8080`; `ASPNETCORE_ENVIRONMENT` no tiene valor en la imagen, así que sin la variable el entorno es `Production`.
- Todos los fallos de arranque terminan el contenedor con **código 1** y registran el error una sola vez, sin secretos. Ejemplos: en `Production` sin secretos o con placeholders, log `Hosting failed to start` que nombra la clave (`ConnectionStrings:Cotifly`...) y nunca el valor; con `-e ASPNETCORE_HTTP_PORTS=99999`, el mismo log con `ArgumentOutOfRangeException (Parameter 'port')`. Si falla antes de que exista el logger (`builder.Build()`), una línea `crit:` en stderr. `Program.cs` lo hace solo cuando `Cotifly.Api` es el proceso: sin eso, la excepción no controlada en el PID 1 del contenedor termina con 139 y repite la traza; bajo `WebApplicationFactory` la excepción se sigue propagando a las pruebas.
- La sonda del `HEALTHCHECK` se puede correr a mano: `docker exec cotifly-api dotnet /app/Cotifly.Api.dll --healthcheck` (imprime `sonda /health: 200` y sale con 0).
- Con otro puerto (`-e ASPNETCORE_HTTP_PORTS=9090`) la sonda usa el primero de esa lista; con `ASPNETCORE_URLS` (p. ej. `http://+:9090`), la primera URL `http://`. Preferir `ASPNETCORE_HTTP_PORTS`.
- Versión y metadatos: `docker build --build-arg VERSION=... --build-arg VCS_REF=... --build-arg BUILD_DATE=...`; se leen con `docker image inspect --format '{{json .Config.Labels}}' cotifly-api:dev`.

Depurar la imagen sin shell: [10-operacion](10-operacion.md#105-depurar-sin-shell).

## 6.5 Correr la API desde el código

**Correr la API desde el código** (sin construir la imagen):

```powershell
docker run --rm -p 8080:8080 `
  -v "<ruta>\Cotifly\backend-dotnet:/src" -v cotifly-nuget:/root/.nuget/packages `
  -w /src -e ASPNETCORE_ENVIRONMENT=Development -e ASPNETCORE_HTTP_PORTS=8080 `
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --project src/Cotifly.Api
# en otra terminal
curl http://localhost:8080/health
```

Levantar la API contra un SQL Server local de pruebas: pasar `-e ConnectionStrings__Cotifly="Server=host.docker.internal,1433;Database=dbTarifario_Nexo_Pruebas;User Id=...;Password=...;TrustServerCertificate=True"` al `docker run` anterior y consultar `curl http://localhost:8080/health/ready`.

Para un SQL Server local con el esquema y los usuarios de prueba ya listos, usar el entorno de [6.2](#62-entorno-local-completo-con-sql-server) (puerto `14333` en `127.0.0.1`).

## 6.6 Abrir la solución en un IDE

**Abrir la solución en un IDE:**
- Requiere el SDK .NET 10 en el host. Se puede instalar por usuario, sin permisos de administrador, con el script oficial `dotnet-install` (`dotnet-install.ps1 -Channel 10.0 -InstallDir $env:LOCALAPPDATA\dotnet`).
- IDE: Visual Studio 2022 17.14 o posterior, Visual Studio 2026 o una versión reciente de Rider.
- Si alternas entre compilar en el host y en el contenedor, borra antes las carpetas `bin/` y `obj/`: las rutas del restore (`project.assets.json`) no son compatibles entre ambos.
