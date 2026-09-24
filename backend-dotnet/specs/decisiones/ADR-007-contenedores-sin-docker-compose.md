# ADR-007 — Contenedores: solo Dockerfile multi-stage, sin docker-compose

- **Estado:** Aceptada (desviación explícita del estándar)
- **Fecha:** 2026-09-24
- **Issue / HU:** #7 (HU-003.1). Se implementa en #14 (HU-003.8) y #27 (HU-006.3). El despliegue es HU-006.

## Contexto

El estándar `dotnet-enterprise-architect` exige `Dockerfile` y `docker-compose.yml`, y el Gherkin "Empaquetado y pruebas según el estándar" de HU-003 repite esa exigencia. Pero el backend .NET es un único proceso:

- La base de datos es la instancia corporativa SQL Server 2019 ya existente, no un contenedor.
- Los PDFs se guardan detrás de `IClsAlmacenPdf`. El destino final (volumen o S3/MinIO) se decide en HU-003.7.
- El despliegue real es Docker Swarm con Dokploy (HU-006), que define los servicios desde su propio panel y manifiestos.

Además, la máquina de desarrollo solo tiene SDK .NET 8, así que build y tests de .NET 10 corren dentro de `mcr.microsoft.com/dotnet/sdk:10.0`.

## Decisión

1. **Un solo `backend-dotnet/Dockerfile` multi-stage:**
   - `sdk:10.0` para restore, build, test y publish.
   - `aspnet:10.0` como runtime: usuario no root, `EXPOSE 8080`, y fuentes e imágenes del PDF embebidas como recurso.
   - `HEALTHCHECK` sobre `/health`. La imagen `aspnet` no trae `curl`, así que se usa una sonda propia (el mismo binario con un argumento `--healthcheck`, o un ejecutable mínimo). Instalar `curl` queda como alternativa.
2. **Sin `docker-compose.yml`.** Es una desviación explícita y consciente del estándar corporativo, registrada en esta ADR.
3. **Ejecución local:**
   ```bash
   docker build -t cotifly-backend:dev -f backend-dotnet/Dockerfile backend-dotnet
   docker run --rm -p 8080:8080 --env-file backend-dotnet/.env -v cotifly_pdfs:/data/pdfs cotifly-backend:dev
   ```
   Las variables se documentan en `backend-dotnet/.env.example`, sin secretos reales.
4. **Build y test sin SDK local:**
   ```bash
   docker run --rm -v "<ruta>/backend-dotnet:/src" -w /src mcr.microsoft.com/dotnet/sdk:10.0 dotnet test
   ```
   En Windows se lanza desde PowerShell, por las rutas con espacios.
5. **Tests de integración con Testcontainers** (`Testcontainers.MsSql`):
   - Imagen `mcr.microsoft.com/mssql/server:2019-latest`, la misma versión que producción.
   - Se crea la base `dbTarifario_Nexo_Pruebas` y se aplican los scripts `migrations_mssql/*.sql`, partidos por `GO`.
   - Así se cubre la "BD local" que normalmente justificaría un compose.
   - Si los tests corren dentro del contenedor del SDK, se monta `/var/run/docker.sock` y puede hacer falta `TESTCONTAINERS_HOST_OVERRIDE` para que el contenedor alcance a SQL Server.

## Alternativas consideradas

- **Compose con SQL Server local + API:** duplica a Testcontainers, induce a desarrollar contra una base distinta de la corporativa y no se usa en el despliegue.
- **Compose solo para levantar la API:** no aporta nada frente a `docker run --env-file`.

## Consecuencias

- El criterio de HU-003 sobre `docker-compose.yml` no se cumple de forma literal. Al revisar HU-003.8 debe citarse esta ADR.
- La integración contra dependencias reales (SQL Server corporativo, SAP TM, Nexo, SMTP) se prueba en el entorno de QA desplegado.
- CI necesita un runner con Docker. Donde no lo haya, se filtran los tests de la categoría `Integracion`.

## Pendientes

| Pendiente | Dueño |
|---|---|
| Avisar de esta desviación a quien mantiene `dotnet-enterprise-architect`, junto con la de prefijos de tabla del 2026-09-22 | Duban |
| Confirmar que el runner de CI corporativo tiene Docker | Duban |
